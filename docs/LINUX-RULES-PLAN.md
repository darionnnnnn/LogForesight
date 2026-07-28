# Linux 規則與雙平台預警規劃（LINUX-RULES-PLAN）

> 規劃日期：2026-07-28。狀態：**設計定案（六項核心決策已與使用者確認），實作未開始**；
> 種子規則的 pattern 字串與簽章鍵的正規化路線仍以 `--netiq-probe` Linux 擴充段的真實輸出為準（§4）。
> 緣起：NetIQ Sentinel 上同時有 Windows 與 Linux 主機，預警規則目前只有 Windows 面
> （`KnownIssueRule` 以 Source＋Event ID 比對，Linux syslog 沒有 Event ID）。本規劃讓 Linux 主機
> 的 log 預警能以與 Windows 相同的方式維護與檢視：Web 規則維護頁分為
> **Windows規則／Linux規則／告警抑制** 三分頁，批次端依主機 OS 套用對應平台的規則面。
> 關聯文件：docs/RULES-PLAN.md（規則外部化基礎）、docs/NETIQ-API-PLAN.md（取數管線，
> Linux 取數是它的擴充）、docs/PLAN.md（多主機總路線）。

## 0. 已確認的決策（2026-07-28）

| # | 決策 | 結論 |
|---|---|---|
| D1 | 規則模型 | **同一 `KnownIssueRule` 模型＋`Platform` 欄位**，同一份 rules.json／lf_rules；不分兩套 store（抑制、匯入、稽核、Web CRUD 全部重用） |
| D2 | Linux 規則比對什麼 | **最大化支援**：同時支援「Sentinel 正規化事件名」與「program＋訊息子字串」兩條比對路（§1.2），不假設 collector 有無正規化；probe 實測後校正種子 pattern，不改模型 |
| D3 | 簽章鍵 | `LogIssueSignature` 加 nullable `EventKey`；Linux 取值順位＝正規化事件名 → 命中規則 Id → `{program}/{priority}` 退階（§2） |
| D4 | 主機 OS 標記 | `WebHost` 加 `Os` 欄位（`windows`/`linux`），既有主機與本機來源預設 windows；主機頁/匯入精靈/CSV 可維護（§3） |
| D5 | 關聯層 | **第一版 Linux 不做關聯層**，console 與報告誠實申報「Linux 主機不適用」；SSH 暴力破解鏈列為後續獨立階段（§4.5） |
| D6 | Web UI | 規則維護頁三分頁 **Windows規則／Linux規則／告警抑制**；抑制維持單一分頁＋平台欄。另：**詳情頁面除 hostname 外顯示 IP 與作業系統類型**，其餘欄位視空間與需要加入（§5.3） |
| D7 | probe | Linux 事件取樣**併入既有 `--netiq-probe`**，一趟閘門同時定案 Windows 與 Linux 的欄位對應 |
| D8 | 種子範圍 | §8 草案範圍確認；pattern 字串待 probe 校正 |

## 1. 規則模型（`KnownIssueRule` 擴充）

### 1.1 新欄位

| 欄位 | 型別 | 說明 |
|---|---|---|
| `Platform` | string | `"windows"`／`"linux"`。**載入期正規化**：舊 rules.json 缺此欄位 → 補 `"windows"`（與 `NormalizeLegacyCriticalSeverity` 同一手法，不回寫原檔），舊檔零遷移 |
| `ProgramPattern` | string | Linux 專用：syslog identifier／process 名稱比對（sshd、sudo、kernel…），比對語意與 `SourcePattern` 一致（不分大小寫 Contains）。**可下推到 Lucene 做 server 端過濾**（§4.2） |
| `EventNamePattern` | string | Linux 專用：Sentinel 正規化事件名（`evt`／xdas taxonomy 名稱）比對，collector 有正規化的環境用這條路最穩。可空 |
| `MessagePatterns` | string[] | Linux 專用：訊息子字串清單，**OR 語意、不分大小寫 Contains**（任一命中即算）。空陣列＝不看訊息、program 命中即算。只能本地比對（§4.2） |

Windows 規則的三個 Linux 欄位恆空；Linux 規則的 `SourcePattern`/`EventIds`/`MatchAllEventIds` 恆空/false。
`Category`／`Severity`／`CountThreshold`／`ElevatesDayRisk`／`Enabled`／`Origin`／`Scope`／
處置知識庫四欄位／修改追蹤——**全部照舊共用**，這是「相同方式維護檢視」的落點。

**刻意不用 regex**：`MessagePatterns` 採子字串清單而非正則。理由：(a) 規則由 Web 頁人工維護，
regex 打錯一個跳脫字元就靜默不命中，子字串所見即所得；(b) 多發行版訊息差異用「多條 OR 子字串」
涵蓋（如 `"Failed password"`＋`"authentication failure"`），實務上足夠；(c) `RuleValidator`
對子字串只需驗長度，不需要驗 regex 語法與災難性回溯。未來確有需求再以新欄位擴充，不改既有語意。

### 1.2 Linux 比對語意（`FindRule` 的平台分路）

呼叫端先依主機 OS 選平台，只在該平台的規則子集內依序比對（清單順序＝優先序，與現行相同）：

```
Linux 事件命中一條規則 ＝
     (EventNamePattern 非空 且 事件帶正規化事件名 且 名稱命中)
  或 (ProgramPattern 非空 且 program 命中 且 (MessagePatterns 為空 或 任一子字串命中訊息))
```

兩條路是 **OR**——同一條種子規則可同時填 `EventNamePattern` 與 `ProgramPattern`+`MessagePatterns`，
正規化環境走前者、raw syslog 環境走後者，一份種子兩種環境都能用（D2「最大化支援」的落點）。

### 1.3 驗證（`RuleValidator` 平台條件式）

- `Platform` 必須為 `windows`/`linux` 二值之一。
- windows 規則：現行規則不變（`EventIds` 非空或 `MatchAllEventIds`）＋三個 Linux 欄位必空。
- linux 規則：`ProgramPattern` 與 `EventNamePattern` **至少一個非空**；`EventIds` 必空、
  `MatchAllEventIds` 必 false；`MessagePatterns` 每條非空白、長度受限、最多 8 條
  （超過代表這條規則想做的事太多，該拆成多條）。
- **遮蔽偵測按平台分區**：Windows 規則永遠不會遮蔽 Linux 規則，反之亦然。Linux 分區的遮蔽
  充分條件（保守，只警告不跳過，與現行一致）：排在後面的規則，其 program 比對範圍被前面
  啟用規則的 `ProgramPattern` 涵蓋（前者是後者的子字串）**且前面那條 `MessagePatterns` 為空**
  （不看訊息＝全吃）時，判為永不命中。訊息子字串之間的涵蓋關係不做精確判定——比對成本高
  且誤報遮蔽警告比漏報更擾人。

### 1.4 `RuleSchemaLimits` 增補

```
ProgramPatternMaxLength    = 100   （對齊 SourcePattern）
EventNamePatternMaxLength  = 200
MessagePatternMaxLength    = 200
MessagePatternsMaxCount    = 8
```

### 1.5 Id 慣例與 seed

- Builtin Linux 規則 Id：`builtin-linux-{類別}-{代表}`（如 `builtin-linux-ssh-bruteforce`）。
  現行 `builtin-` Id 不動（等同隱含 windows），不做 `builtin-win-` 改名——Id 出貨永不改名。
- `KnownIssueSeed.Version` 3→4：新增 §8 的 Linux 種子。既有部署升級 SOP 與 seed v2 前例完全相同：
  啟動提示 → `--import-rules` 預覽 → `--apply` → `--selftest`。
- probe 之後若需修訂 pattern 字串：seed v4→v5 再走一次 `--import-rules`，機制既有、零新設計。

## 2. 簽章鍵與資料模型

### 2.1 `LogIssueSignature.EventKey`（nullable string）

Windows 路徑不填（null），行為零改變。Linux 事件依序取：

1. **正規化事件名**（probe 確認 collector 有正規化時）——與 Windows Event ID 同構、最穩定。
2. **命中規則的 Id**——規則命中的事件以規則為粒度聚合（`sshd` 的暴力破解與成功登入分開計）。
3. **`{program}/{priority}` 退階**——未命中任何規則的事件退到 program＋syslog 等級粒度。
   比 Windows 的 Other 類粗，但對「量的異變」偵測仍有效，且誠實：沒有更細的穩定鍵可用。

聚合分組鍵擴為 `(LogName, Source, EventId, EntryType, EventKey)`；Linux 事件固定
`LogName = "Linux"`、`EventId = 0`、`Source = program`。趨勢層/慢速趨勢層吃簽章鍵、
純數字比較，**邏輯零修改**；歷史紀錄的簽章以 JSON 序列化儲存，新欄位自動相容
（實作時核對兩個 SQL 後端的紀錄表是否有簽章展開欄位需要跟進——以 docs/DB-PLAN.md 現況為準）。

### 2.2 syslog priority → `EntryType` 映射（固定表）

| syslog | EntryType |
|---|---|
| emerg / alert / crit / err | Error |
| warning | Warning |
| notice / info / debug | Information |

Sentinel `sev`（0–5）與 syslog priority 的實際對應由 probe 核對後落入 `SentinelFieldMap`。

## 3. 主機 OS 標記

- `WebHost.Os`（`"windows"`/`"linux"`）＋ `lf_hosts.os nvarchar(10) NOT NULL DEFAULT 'windows'`
  ＋ `CHECK (os IN ('windows','linux'))`。既有列與本機來源一律 windows。
- **主機頁**：清單加 OS 徽章與篩選；編輯表單可改（改 OS 等於改這台套哪個規則面，寫操作稽核）。
- **NetIQ 匯入精靈**：加 OS 欄。probe 會驗證 Sentinel 事件是否帶可判別 OS 的欄位（product name
  等）——能自動判別就預填、人工可改；不能就人工必選，**不做猜測預設**。
- **CSV 匯入**：選填 `os` 欄，缺值＝windows（相容既有檔）。
- **`--host-list`**：輸出加 OS 欄。
- 批次端 routing：依 `Os` 決定該主機套哪個平台規則子集、產哪種 Lucene 查詢（§4.2）、
  申報哪一份「不適用」清單（§4.5）。

## 4. 取數與分析管線（NETIQ-API-PLAN 的擴充）

### 4.1 `--netiq-probe` Linux 擴充段（D7：與既有 probe 同一趟閘門）

新增 probe 項（指定一台已知 Linux 主機的 IP 執行）：

1. 近 24h 該主機事件 20 筆**全欄位** JSON 傾印 → 定案：program 落在哪個欄位、
   有無正規化事件名（`evt`／xdasid）、`sev` 與 syslog priority 的對應、msg 原始格式
   （rsyslog template 差異）、OS 可判別欄位。
2. auth 類事件（sshd 登入）是否有被收進 Sentinel → Linux 版覆蓋率申報的依據（§4.4）。
3. program 子句的 Lucene 過濾實測（欄位名＋analyzer 行為：`sshd` 能不能精確 term 命中）。

### 4.2 Linux Q1（主聚合查詢）

```
filter = (IP 批次) AND ( (program ∈ 啟用 Linux 規則的 ProgramPattern 聯集)
                          OR (sev ≥ err 對應值) )
fields = 主機, program, 正規化事件名(若有), sev, dt, msg
```

與 Windows Q1 的兩個刻意差異：

- **`msg` 必須投影**（正規化事件名不可用的環境，規則比對與簽章鍵都要看訊息）→ 每筆
  傳輸量比 Windows Q1 的四欄大。對策：program/sev 已在 server 端把範圍縮到規則相關＋錯誤級，
  正常日量級可控；實際量由 probe 實測，超乎預期時的降級選項是「拿掉 generic err 收集、
  只收規則相關 program」（犧牲 Other 類偵測面，誠實申報）。
- **generic `sev ≥ err` 收集**：對齊本機 Windows System/Application 的 ErrorWarningOnly
  偵測面——沒有這塊，Linux 主機的趨勢層就只看得到規則命中事件，「首次出現的未知錯誤」
  完全不可見。這是 Linux 面的「其他事件」來源。

watchlist→Lucene 產生器做成平台分路的兩個純函數（Windows：來源＋EventID；Linux：program
聯集＋sev 門檻），皆進單元測試與 `--selftest`。`ChannelWatchlists`（Windows Operational 頻道
機制）只對 `Platform=windows` 規則推導，Linux 規則不參與——Linux 沒有頻道 watchlist 概念。

### 4.3 Q2／Q3 與分類

- Q2 範例訊息、Q3 風險主機原始 log：機制平台無關，Linux 主機同路徑（filter 改用 program／
  事件名鎖定簽章）。
- `Classify`：加平台參數的多載（依主機 OS 選規則子集）。本機路徑恆 windows，**行為零改變**；
  `LogAggregator.Aggregate` 現行呼叫不動，Sentinel 路徑由 `SentinelStatsSource` 帶平台。

### 4.4 覆蓋率誠實申報（Linux 版）

- Q4 頻道覆蓋的 Linux 對應物：該主機近 24h **有無 auth 類事件**（sshd/su/sudo 任一）。
  沒有＝「入侵偵測未覆蓋」（auth log 沒進 Sentinel），與 Windows 的「未收 Security 頻道」
  同一個總覽區塊呈現。
- 整台主機近 24h 零事件＝「無資料來源」告警（可能 agent/轉送掛了），沿用既有無回報機制。

### 4.5 關聯層（D5：第一版不做）

- Linux 主機的分析結果固定申報：「關聯層（攻擊鏈/故障鏈比對）不適用於 Linux 主機——
  本版僅規則層＋趨勢層＋慢速趨勢層」；console、風險報告、Web 詳情頁同步顯示。
  與「沒告警 ≠ 沒問題，是沒看」同一原則：不適用要說出來，不能讓人以為有看。
- 後續獨立階段（不在本規劃範圍）：至少【SSH 暴力破解→得手】一條（同日 failed password
  達門檻＋同帳號/IP 成功登入——與 Windows【破解得手】同構，帳號/IP 抽取穩定後才做）。

### 4.6 AI 層

- 知識庫四欄位共用，規則命中照舊走靜態渲染不呼叫 AI。
- prompt 措辭依平台帶入（「Event Log」→「syslog」），事件行格式改用 `program(EventKey)`；
  前置掃描/深入分析同路徑。Gemma 對 syslog 語意判讀無障礙，無新增呼叫類型，context 預算不變。

## 5. Web UI

### 5.1 規則維護頁（Rules.cshtml＋rules.js）

- 頁內 tabs：`規則｜告警抑制` → **`Windows規則｜Linux規則｜告警抑制`**。
- 兩個規則分頁共用同一套清單/篩選/排序/計數元件，只差 `Platform` 過濾；搜尋 placeholder
  依平台調整（Windows「搜尋來源、Event ID、說明」／Linux「搜尋 program、訊息關鍵字、說明」）。
- 編輯彈窗：比對區塊依平台切換——Windows 顯示現行「來源比對＋Event ID＋全部事件」；
  Linux 顯示「Program 比對＋正規化事件名（選填）＋訊息子字串（一行一項，OR，最多 8 條）」。
  類別/嚴重度/門檻/重大/知識庫/啟用區塊完全共用。新增規則時平台由所在分頁決定（不可改）。
- 告警抑制分頁：列表加「平台」欄與篩選；「抑制此規則」彈窗的主機下拉**依規則平台過濾**
  （Linux 規則只列 Linux 主機）。

### 5.2 API／DTO

- `RuleDto`/`SaveRuleRequest` 加 `Platform`＋三個 Linux 欄位；`validate`/`save`/`restore`
  流程不變。`GET /api/rules` 維持單一端點回全量、前端分平台呈現（規則量級小，不需分頁端點）。
- `RuleSuppressionDto` 加所屬規則平台（由 RuleId 反查帶出，非新儲存欄位）。

### 5.3 詳情頁面顯示（D6 使用者新增需求）

- **風險日詳情（RecordDetail）**：`detail-header` 除現有 hostname 外，加 **IP 與 OS 徽章**；
  有 `DisplayName` 時一併顯示（NetIQ 主機以 IP 登錄、光看 hostname 認不出機器的既有問題）。
  DTO（detail header 所用）補 `IpAddress`/`Os`/`DisplayName`。
- **主機詳情（HostDetail）**：同樣補齊 hostname＋DisplayName＋IP＋OS。
- **清單面（視空間加入）**：問題查詢（Records）主機欄加 OS 小徽章；儀表板「無回報主機」
  清單、報表下鑽的主機列、各處主機下拉選單——顯示 OS 標示，欄位擁擠處以 icon/縮寫呈現。
- Linux 主機的詳情頁：重點問題的簽章行顯示 `program（EventKey）` 取代 `來源（Event ID）`；
  關聯層區塊顯示 §4.5 的不適用申報。

## 6. `--selftest` 增補

- 合成 Linux 事件驗證段：每條 Linux 種子規則「應命中/實際命中」、兩條比對路（事件名路／
  program+訊息路）各自驗證、簽章鍵三順位取值驗證、EntryType 映射表驗證、趨勢分支對
  EventKey 簽章的運作驗證。
- 規則表驗證擴充：平台分區遮蔽偵測、Linux 欄位合格性、「Linux 規則零條但存在 Linux 主機」
  警告（規則面空白＝這些主機只剩 generic err 收集，要說出來）。
- Lucene 產生器（兩平台）推導檢查：規則改了、產生的子句跟著對。

## 7. DB 映射增補（RULES-PLAN 草案的延伸）

```
lf_rules 加欄：
  platform            nvarchar(10)   NOT NULL DEFAULT 'windows'
                      CHECK (platform IN ('windows','linux'))
  program_pattern     nvarchar(100)  NULL    -- windows 列恆 NULL
  event_name_pattern  nvarchar(200)  NULL

lf_rule_message_patterns（新子表，對齊 causes/steps 的正規化風格，seq 保序）
  rule_id       nvarchar(100)  FK → lf_rules
  seq           int
  pattern_text  nvarchar(200)
  PK (rule_id, seq)

lf_hosts 加欄：
  os  nvarchar(10)  NOT NULL DEFAULT 'windows'  CHECK (os IN ('windows','linux'))
```

Builtin 覆寫時 `lf_rule_message_patterns` 比照 causes/steps 全刪全插。欄位長度對齊
`RuleSchemaLimits`（§1.4），JSON 與 DB 同一組數字。

## 8. Linux 種子規則草案（seed v4；pattern 字串以 probe 實測訊息校正）

「嚴重度」欄的「高（重大）」＝ High＋`ElevatesDayRisk`。訊息子字串刻意收多發行版變體。

| Id（builtin-linux-…） | program | 訊息子字串（OR，示意） | 類別 | 嚴重度 | 門檻 |
|---|---|---|---|---|---|
| ssh-bruteforce | sshd | `Failed password`、`authentication failure`、`Invalid user` | Security | High | 10 |
| ssh-accept | sshd | `Accepted password`、`Accepted publickey` | Security | **Low（收集用，非告警）** | 1 |
| su-sudo-failure | su／sudo | `authentication failure`、`incorrect password` | Security | Medium | 5 |
| account-change | useradd／usermod／userdel | （不看訊息，program 命中即算） | Security | High | 1 |
| priv-group-change | gpasswd／groupadd | `to group sudo`、`to group wheel`、（群組異動全收） | Security | High | 1 |
| audit-tamper | auditd | `audit daemon is exiting`、`The audit daemon is stopping` | Security | 高（重大） | 1 |
| storage-io | kernel | `I/O error`、`Buffer I/O error`、`EXT4-fs error`、`XFS`＋`error` | Storage | 高（重大） | 1 |
| smart-prefail | smartd | `Prefailure`、`FAILED SMART self-check` | Storage | 高（重大） | 1 |
| hw-error | kernel | `Hardware Error`、`Machine Check`、`MCE` | Hardware | 高（重大） | 1 |
| oom-kill | kernel | `Out of memory`、`oom-kill` | Resource | High | 1 |
| service-fail-loop | systemd | `entered failed state`、`Failed to start` | Service | Medium | 3 |
| segfault-loop | kernel | `segfault` | Service | Medium | 3 |
| time-sync | chronyd／ntpd | `Can't synchronise`、`no reachable sources`、`time reset` | Config | Medium | 3 |
| cron-failure | cron／CRON | `FAILED`、`error` | Service | Medium | 3 |

比照 Windows 面（README「正常 RDP 使用不會誤報」）的防誤報設計：

- `ssh-accept` 一律 Low、不參與風險判定——日常維運 SSH 登入絕不告警；收集目的是趨勢基準
  與未來 SSH 關聯鏈的成功面。
- 帳號/群組異動類收「事件發生」不判合理性——管理員日常建帳號會出現在清單但屬 High 單項，
  不觸發任何組合推論（關聯層本版不做，天然沒有無錨點誤報的空間）。
- 每條種子附完整知識庫四欄位（白話說明/影響/常見原因/處置步驟，繁中），實作時撰寫。

## 9. 測試計畫

- **單元**：Linux 比對純函數（兩條路、優先序、大小寫）；簽章鍵三順位；`RuleValidator`
  平台分區與 Linux 欄位驗證；遮蔽偵測平台分區；EntryType 映射；兩平台 Lucene 產生器；
  載入期 `Platform` 正規化（舊檔相容）。
- **Web**：規則 CRUD 的平台驗證（Windows 規則帶 Linux 欄位被擋、反之亦然）；抑制主機
  下拉的平台過濾；詳情 DTO 帶 IP/OS。
- **合約**：`SentinelStatsSource` Linux 分支 → 簽章統計與本機路徑同構（餵 fixture 事件）。
- **`--selftest`**：§6 全項。
- **閘門**：probe Linux 段輸出貼回定案 → 種子 pattern 校正 → 試點一台 Linux 主機端到端。

## 10. 階段與閘門

| 階段 | 內容 | 依賴 |
|---|---|---|
| **P1** | `--netiq-probe` 擴充 Linux 取樣段（§4.1） | 無；與 Windows probe 同一趟真實環境執行 |
| —閘門— | **probe 真實輸出貼回**：定案欄位對應、正規化有無、pattern 校正、OS 判別欄位 | 使用者於真實環境執行 |
| **P2** | 規則模型/驗證/seed v4/載入正規化＋`WebHost.Os`＋Web 三分頁與詳情頁顯示（§1/§3/§5） | 不依賴取數管線；pattern 可先以 §8 通用字串出貨，probe 後 v5 修訂 |
| **P3** | `SentinelStatsSource` 雙平台分支（Lucene 產生器、欄位對應、簽章聚合、覆蓋申報）＝ NETIQ-API-PLAN §8 步驟 3~4 的擴充版 | probe 閘門 |
| **P4** | `--selftest` 增補＋文件收尾（README 規則章節加 Linux 訊號清單、WEB-SPEC §9.7 與詳情頁、RULES-PLAN 註記本文件） | P2/P3 |
| **P5** | Linux 關聯鏈（SSH 暴力破解系列）——**獨立規劃，另開文件** | P3 上線後、帳號/IP 抽取穩定 |

P2 刻意設計成不被 probe 擋（模型的雙路比對語意已定，probe 只影響種子字串與簽章鍵的
正規化路線）；若 probe 時程未定，P2 可先行，維護面先可用。

## 11. 開放事項（實作前確認）

1. `LogName = "Linux"` 常數命名（vs `"Syslog"`）——實作時定，影響面僅顯示與簽章鍵前綴。
2. Linux Q1 的 generic `sev ≥ err` 收集是否保留（§4.2 建議保留；量級超乎預期時降級並申報）。
3. 兩個 SQL 後端的紀錄表若有簽章展開欄位，`EventKey` 需跟進加欄（實作時核對 DB-PLAN 現況）。
