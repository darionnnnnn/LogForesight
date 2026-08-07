# Linux 規則面（現況）

> 本文件是 Linux syslog 規則面的現況參考：規則模型、比對語意、主機 OS 標記與目前的種子
> 規則清單。規則外部化的共用機制（儲存、驗證、seed／匯入政策、抑制）見
> [docs/RULES-SPEC.md](RULES-SPEC.md)，本文件只談 Linux 專屬的部分。緣起與各輪決策過程見
> docs/archive/HISTORY.md／docs/archive/FEEDBACK-*-PLAN.md；取數管線尚未完成，見
> docs/BACKLOG.md。

## 現況總覽

Linux syslog 沒有 Event ID，規則面因此與 Windows 共用同一個 `KnownIssueRule` 模型
（同一份規則儲存、同一套抑制／匯入／驗證／Web CRUD 機制，不分兩套 store），
只是多了 `Platform` 欄位與三個 Linux 專用比對欄位。Web 規則維護頁分
**Windows規則／Linux規則／告警抑制** 三分頁，主機依 `Os` 欄位套用對應平台的規則面。

**目前狀態（2026-08-07）**：規則模型、種子、驗證、Web 維護介面，以及**事件模型與簽章聚合**
（`EventKey` 分組鍵、規則層／趨勢層／慢速趨勢層、關聯層短路申報，見下方「簽章鍵與聚合」）
皆已完成並有專屬測試覆蓋；但 **Sentinel 的 Linux 取數分支尚未實作**（見 docs/BACKLOG.md）——
也就是說現在能維護 Linux 規則、把主機標成 Linux，聚合分類邏輯也已就緒，但 Sentinel 搜尋
還不會實際把 Linux 事件送進分析管線，卡在使用者執行 NetIQ 維護頁「診斷」分頁 probe 的
第二輪資料。

## 規則模型（`KnownIssueRule` 的 Linux 專用欄位）

| 欄位 | 型別 | 說明 |
|---|---|---|
| `Platform` | string | `"windows"`／`"linux"`。舊 rules.json／規則庫缺此欄位時預設 `"windows"` |
| `ProgramPattern` | string | syslog identifier／process 名稱比對（sshd、sudo、kernel…），語意與 Windows 的 `SourcePattern` 一致（不分大小寫 Contains） |
| `EventNamePattern` | string | Sentinel 正規化事件名（`evt`／xdas taxonomy 名稱）比對，collector 有正規化的環境用這條路最穩。可空 |
| `MessagePatterns` | string[] | 訊息子字串清單，**OR 語意、不分大小寫 Contains**（任一命中即算）。空陣列＝不看訊息、program 命中即算 |

Windows 規則的三個 Linux 欄位恆空；Linux 規則的 `SourcePattern`/`EventIds`/`MatchAllEventIds`
恆空/false。`Category`／`Severity`／`CountThreshold`／`ElevatesDayRisk`／`Enabled`／`Origin`／
`Scope`／處置知識庫四欄位／修改追蹤——全部與 Windows 規則共用同一套維護方式。

**刻意不用 regex**：`MessagePatterns` 採子字串清單而非正則——規則由 Web 頁人工維護，regex
打錯一個跳脫字元就靜默不命中；多發行版訊息差異用「多條 OR 子字串」涵蓋即足夠實務所需；
子字串驗證只需查長度，不需要驗 regex 語法與災難性回溯風險。

### 比對語意（兩條路 OR）

```
Linux 事件命中一條規則 ＝
     (EventNamePattern 非空 且 事件帶正規化事件名 且 名稱命中)
  或 (ProgramPattern 非空 且 program 命中 且 (MessagePatterns 為空 或 任一子字串命中訊息))
```

同一條種子規則可同時填 `EventNamePattern` 與 `ProgramPattern`+`MessagePatterns`，正規化環境
走前者、raw syslog 環境走後者，一份種子兩種環境都能用。

**比對順序有意義**：program 比對是子字串比對，`"sudo"` 包含 `"su"`，所以 `sudo` 規則必須排在
`su` 之前，否則 sudo 的事件會被 su 規則先攔走；同理未來新增 program 有包含關係的規則時，
具體的要排在泛用的前面。

### 驗證

- `Platform` 必須為 `windows`/`linux` 二值之一。
- Windows 規則：`EventIds` 非空或 `MatchAllEventIds`＋三個 Linux 欄位必空。
- Linux 規則：`ProgramPattern` 與 `EventNamePattern` 至少一個非空；`EventIds` 必空、
  `MatchAllEventIds` 必 false；`MessagePatterns` 每條非空白、長度受限、最多 8 條。
- **遮蔽偵測按平台分區**：Windows 規則永遠不會遮蔽 Linux 規則，反之亦然。Linux 分區內，
  排在後面的規則若其 program 比對範圍被前面啟用規則的 `ProgramPattern` 涵蓋（前者是後者的
  子字串）且前面那條 `MessagePatterns` 為空（不看訊息＝全吃），判為永不命中。

### 欄位長度上限（`RuleSchemaLimits`）

`ProgramPatternMaxLength=100`、`EventNamePatternMaxLength=200`、`MessagePatternMaxLength=200`、
`MessagePatternsMaxCount=8`。

### Id 慣例

Builtin Linux 規則 Id：`builtin-linux-{類別}-{代表}`（如 `builtin-linux-ssh-bruteforce`）。
現行 `builtin-` Id（等同隱含 windows）不因此改名——Id 一經出貨永不改名。

## 主機 OS 標記

`WebHost.Os`（`windows`／`linux`，預設 `windows`）決定這台主機套用哪個平台的規則面：

- **主機頁**：清單有 OS 徽章與篩選；編輯表單可改（改 OS 等於改這台套哪個規則面，寫操作稽核）。
- **NetIQ 匯入精靈**：OS 預填來源是 **Sentinel 層級**（`Sentinel.Os`）——本環境 Windows／Linux
  的 NetIQ 已完全拆分成不同 Sentinel，同一台不混平台，因此判別的正確層級是 Sentinel 而非
  逐事件猜測；精靈依所選 Sentinel 的 Os 預填整批（可改，當混合環境的逃生門）。
- **CSV 匯入**：選填 `os` 欄，缺值＝windows（相容既有檔）。
- 四條寫入路徑（主機頁編輯、NetIQ 單筆／批次登錄、CSV `os` 欄、掃描精靈）一律經
  `WebHost.NormalizeOs` 正規化（大小寫與空白不拘、不合法值擋下），儲存值恆為小寫。
- 掃描精靈與 CSV 的 OS 只套用在**本次新增**的主機——既有主機（含復活的孤兒）的 OS 一律不動。

## Web UI

- **規則維護頁**（`/admin/rules`）：頁內分 **Windows規則｜Linux規則｜告警抑制** 三分頁；
  兩個規則分頁共用同一套清單／篩選／排序／計數元件，只差 `Platform` 過濾，搜尋
  placeholder 依平台調整。編輯彈窗的比對欄位區塊依平台切換：Windows 顯示「來源比對＋
  Event ID＋全部事件」；Linux 顯示「Program 比對＋正規化事件名（選填）＋訊息子字串
  （一行一項，OR，最多 8 條）」。新增規則的平台由所在分頁決定，建立後不可變更
  （`Platform` 與 `Origin` 同屬身分欄位）。
- **告警抑制分頁**：加「平台」欄與篩選；「抑制此規則」彈窗的主機下拉依規則平台過濾
  （Linux 規則只列 Linux 主機）。
- **風險日詳情／主機詳情**：標題列除 hostname 外顯示 Sentinel 回報的顯示名、**作業系統徽章**
  與 IP——NetIQ 主機以 IP 登錄，只有一串 IP 認不出是哪台機器；OS 決定套用哪個平台的規則面，
  判讀問題時需要知道。

## 現行 Linux 種子規則（17 條，seed v4）

「嚴重度」欄的「高（重大）」＝ High 且帶 `ElevatesDayRisk` 旗標（命中即列為當日高風險）。

| program | 訊息關鍵字（任一命中） | 意義 | 嚴重度 |
|---|---|---|---|
| sshd | Failed password / authentication failure / Invalid user | SSH 登入失敗；單日 ≥10 次視為暴力破解 | High |
| sshd | Accepted password / Accepted publickey | SSH 登入成功 | **Low（收集用，非告警）** |
| sudo | authentication failure / incorrect password attempt | sudo 提權驗證失敗（≥5 次） | Medium |
| su | authentication failure / incorrect password / FAILED su | su 提權驗證失敗（≥5 次） | Medium |
| useradd/usermod/userdel（`user`） | （不看訊息） | 帳號建立/修改/刪除 — 入侵者建立立足點 | High |
| groupadd/groupmod/groupdel（`group`） | （不看訊息） | 群組異動 | High |
| gpasswd | （不看訊息） | 帳號被加入/移出群組 — 加入 sudo/wheel 即提權 | High |
| auditd | audit daemon is exiting / stopping | **稽核服務被停止 — 滅跡的典型行為** | 高（重大） |
| kernel | I/O error / Buffer I/O error / EXT4-fs error / XFS internal error | 磁碟或檔案系統錯誤 | 高（重大） |
| smartd | Prefailure / FAILED SMART self-check / predicted TO FAIL | S.M.A.R.T. 預警硬碟即將故障 | 高（重大） |
| kernel | Hardware Error / Machine Check / mce: | CPU/記憶體/PCIe 硬體錯誤 | 高（重大） |
| kernel | Out of memory / oom-kill / Killed process | 記憶體耗盡，核心強制終止程序 | High |
| systemd | entered failed state / Failed to start / Main process exited | 服務啟動失敗或異常終止（≥3 次） | Medium |
| kernel | segfault | 應用程式反覆區段錯誤（≥3 次） | Medium |
| chronyd | Can't synchronise / no reachable sources | 時間同步失敗（≥3 次） | Medium |
| ntpd | time reset / synchronisation lost / no servers reachable | 時間同步失敗（≥3 次） | Medium |
| CRON | FAILED / (CRON) ERROR | 排程任務執行失敗（≥3 次） | Medium |

比照 Windows 面「正常 RDP 使用不會誤報」的防誤報設計：

- `ssh-accept` 一律 Low、不參與風險判定——日常維運 SSH 登入絕不告警；收集目的是趨勢基準
  與未來 SSH 關聯鏈的成功面。
- 帳號/群組異動類收「事件發生」不判合理性——管理員日常建帳號會出現在清單但屬 High 單項，
  不觸發任何組合推論（關聯層目前不涵蓋 Linux，天然沒有無錨點誤報的空間）。
- 每條種子附完整知識庫四欄位（白話說明/影響/常見原因/處置步驟，繁中）。

## 簽章鍵與聚合（實作現況，2026-08-07，批 4A，docs/FEEDBACK-12-PLAN.md §4.2）

`LogIssueSignature.EventKey`（`string`，預設空字串，非 nullable）——比實作前的設計草案簡化：
命中規則時 `EventKey = 規則 Id`（`KnownIssueCatalog.FindLinuxRule` 在 `LogAggregator.Aggregate`
**聚合之前**逐事件呼叫，訊息全文比對必須在聚合前做，聚合後只剩截斷過的 `SampleMessages`）；
未命中規則時 `EventKey = ""`，與 Windows 事件的空字串行為一致，一律聚合成 `Other` 類——
**沒有實作原設計草案的「正規化事件名」與 `{program}/{priority}` 退階兩級**：前者要等 4B
的診斷輪 B 確認 Sentinel 是否真的有做事件正規化（`EventNamePattern` 目前所有種子皆空）；
後者則是評估後認為沒有實質必要——未命中規則的 Linux 事件退到 `Other` 類，語意與 Windows
未命中事件完全一致，不需要另外分裂出更細的退階粒度製造維護負擔。

聚合分組鍵擴為 `(LogName, Source, EventId, EntryType, EventKey)` 五元組
（`LogAggregator.GroupKeyFor`，`internal` 供 `RiskyEventSelector` 重用同一套鍵）；Linux 事件
固定 `LogName="Linux"`、`EventId=0`、`Source=program`。`IssueSignatureKey.For` 的問題穩定鍵
在 `EventKey` 非空時多附加一段（4 段或 5 段皆可解析，Windows 四段鍵字串逐字不變）。
`TrendAnalyzer`／`SlowTrendAnalyzer` 的 `SameIssue` 一併比對 `EventKey`，避免「同 program
命中不同規則」（如 sshd 底下的 `ssh-bruteforce` 與 `ssh-accept`）被誤判成同一個問題的趨勢延續。
`ChannelCoverage.WasRead` 對 `LogName="Linux"` 恆回傳已讀（NetIQ pipeline 的 Linux 取數是整批
查詢、不是逐頻道讀取，套用 Windows 三頻道 fallback 會讓趨勢暖身期永遠跑不完）。

顯示面：`LogIssueSignature.SourceEventLabel`——`EventId` 恆為 0 的 Linux 事件若直接顯示
「{Source} EventId 0」會誤導成「這是編號 0 的事件」，命中規則時改顯示「{Source}（規則Id）」，
未命中規則仍顯示「{Source} EventId 0」（沒有更好的識別依據，誠實顯示比假裝有意義更好）。

syslog priority → `EntryType` 的固定映射（下表）**屬 4B 範圍**（`SentinelEventMapper` 從
Sentinel 原始事件建構 `EventLogEntryData` 時套用），4A 尚未實作——目前的測試與聚合邏輯只
驗證「給定 EntryType 後聚合/分類/趨勢/顯示是否正確」，不涉及 syslog priority 本身怎麼轉換：

| syslog | EntryType |
|---|---|
| emerg / alert / crit / err | Error |
| warning | Warning |
| notice / info / debug | Information |

## 關聯層（v1 定案不涵蓋 Linux，非暫時性缺口）

Linux 主機的分析結果固定申報：「關聯層（攻擊鏈/故障鏈比對）不適用於 Linux 主機——本版僅
規則層＋趨勢層＋慢速趨勢層」——`LogAnalysisService.BuildStatisticalRecordAsync` 依
`hostOs` 參數短路跳過 `CorrelationAnalyzer.Detect`（該分析器目前只認 Windows Event ID
群組，Linux 事件餵進去會被錯誤群組、靜默誤判，「不執行」比「執行但結果不可信」誠實），
並在 `UncoveredChecks` 寫入上述文字；批次執行輸出、風險報告、Web 詳情頁同步顯示——與
「沒告警 ≠ 沒問題，是沒看」同一原則，不適用要說出來，不能讓人以為有看。Linux 關聯鏈（至少
一條【SSH 暴力破解→得手】，同日 failed password 達門檻＋同帳號/IP 成功登入，與 Windows
【破解得手】同構）是獨立規劃的 §4.5（批 4C），需要診斷輪 B 的 sshd 樣本正則定案才能實作，
見 [docs/FEEDBACK-12-PLAN.md](FEEDBACK-12-PLAN.md) §4.5。
