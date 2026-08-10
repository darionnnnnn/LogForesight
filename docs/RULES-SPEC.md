# 規則外部化＋告警抑制機制（現況規格）

> 本文件是規則庫（含 Windows／Linux 雙平台）與告警抑制機制（主機／主機群組／全站三種範圍，
> 回饋十三輪 F 加入群組與全站）的現況設計規格：規則不寫死在
> 程式碼、可在 Web 規則維護頁調整，不需重新編譯部署。實作見 `Analysis/KnownIssueCatalog.cs`
> （`KnownIssueRule`＋比對邏輯）、`Analysis/KnownIssueSeed.cs`（內建種子）、`Analysis/RuleValidator.cs`、
> `Analysis/SuppressionFilter.cs`、`Persistence/IKnownIssueRuleStore.cs`／`KnownIssueRuleStore.cs`、
> `Analysis/RuleBootstrapper.cs`、`Analysis/RuleImportPlanner.cs`、`Models/RuleSuppression.cs`、
> `Persistence/ISuppressionStore.cs`／`SuppressionStore.cs`。起草緣起與各輪決策過程見
> docs/archive/HISTORY.md「規則外部化＋主機級抑制機制」段；Linux 雙平台規則面的完整種子清單見
> [docs/LINUX-RULES.md](LINUX-RULES.md)。

## 目標與整體流程

1. **規則從寫死改為外部維護**：初次部署時把內建種子（`KnownIssueSeed.CreateRules()`）寫入
   `rules.json`（未來 DB 後端則寫入資料表），之後在該檔案/資料表直接維護，不需要重新編譯部署。
2. **啟動流程**：`rules.json` 不存在 → 寫入種子（僅此一次）；存在 → 載入＋驗證（單條不合格
   跳過、遮蔽偵測只警告）→ 呼叫 `KnownIssueCatalog.Initialize` 生效。全程見
   `RuleBootstrapper.Run`。
3. **後續更新走手動匯入**：程式改版新增/修訂 builtin 規則後，**不會自動覆寫**使用者的
   `rules.json`（避免「排程執行悄悄改變偵測行為」），而是啟動時提示、由維護者主動執行
   `--import-rules` 決定是否套用（見「Seed／匯入政策」）。
4. **告警抑制**：獨立於規則本身，讓維護者能對「已知雜訊」的規則在特定主機／主機群組／全站
   關閉通知，同時不犧牲偵測與歷史資料的完整性（見「抑制機制」）。

## 三條語意邊界（必須記住的行為，容易被誤解）

這三條是最容易在使用時產生「怎麼跟我想的不一樣」的地方，維護者與未來接手的開發者都應該先讀過：

1. **`Enabled = false`（停用規則）只影響 `Classify`/`FindRule`**，也就是「規則命中分類」與
   「靜態知識庫渲染」。**不影響**：
   - `TrendAnalyzer`：停用規則對應的事件仍會被聚合、仍會做頻率比對——只是不會有 `KnownIssue`
     文字附註（因為未命中任何規則，直接歸類 `Other`/`Low`）。
   - `CorrelationAnalyzer`：關聯層的事件 ID 群組（見下方「關聯層不搬」）是程式碼裡另外維護的
     常數，完全不查規則表，停用規則不會讓對應事件從關聯比對中消失。
2. **抑制（`RuleSuppression`）關的是「要不要吵」，不是「要不要偵測」**：
   - 影響：console/報告的告警呈現（紅色橫幅、頻率異常清單）、風險等級判定
     （`LogAnalysisService.ComputeRuleBasedRisk` 排除被抑制的簽章）。
   - 不影響：事件照常聚合、規則照常命中並落 `RuleId`、`TrendAnalyzer` 照常計算趨勢欄位與
     嚴重度升級（只是不產生告警文字）、歷史紀錄照常寫入完整資訊、**`CorrelationAnalyzer`
     完全不受影響**（單一事件被抑制，不代表它跟其他事件組合出的攻擊鏈/故障鏈也該被消音）。
   - 這樣設計的原因：(a) 維護者的抑制判斷可能是錯的或過時的，需要保留紀錄才能回查；
     (b) 管理頁要做的「每個規則的發生頻率」報表，資料正是來自「照常紀錄」；
     (c) 符合本專案「沒告警 ≠ 沒問題，是沒看」的一貫哲學——抑制是「看了但決定不吵」，
     不是「沒看」。
3. **關聯層的組合模式不搬進規則庫**：`CorrelationAnalyzer` 比對的是「多個獨立事件的已知組合」
   （入侵鏈、故障連鎖等），這是程式邏輯（條件判斷、時序比對、跨日比對），不是可以用
   `(SourcePattern, EventIds) → 分類/嚴重度` 描述的資料，所以維持在程式碼裡。它引用的事件 ID
   常數（`CorrelationAnalyzer.AccountChangeIds` 等，2026-07 起共八組，含 Defender 的
   `DefenderMalwareIds`/`DefenderProtectionOffIds`，故意標 `internal` 供 selftest 驗證）
   與規則表是兩份獨立維護的東西——`rules.json` 新增的 Security 事件規則**不會**自動延伸關聯層
   的偵測範圍；`--selftest` 有一項檢查會驗證這幾組 ID 是否都存在於目前生效的規則表，抓漂移用，
   但不能反向保證「規則表的新事件都被關聯層涵蓋」。

4. **規則不加 `LogName` 欄位；頻道由 provider 名稱天然區分**（2026-07 EventLogReader 遷移）：
   新增 Defender/RDP Operational 頻道後，規則仍只靠 `SourcePattern` 區分頻道（provider 名稱唯一：
   `Microsoft-Windows-Windows Defender`、`Microsoft-Windows-TerminalServices-*`），schema 與未來
   DB 映射不變。各頻道的 watchlist（Operational 頻道的 Information 等級事件要收哪些）沿用原本
   Security watchlist 的推導機制擴為多頻道（`KnownIssueCatalog.ChannelWatchlists`）：凡 SourcePattern
   命中該頻道 provider 探測字串的啟用規則，其 EventIds 聯集即為該頻道 watchlist——新增一條 Defender/RDP
   規則，對應頻道 watchlist 自動涵蓋，不需另外同步。停用規則會讓其事件退出 watchlist（Information
   事件收不進來），這與「停用只影響 Classify」略有不同，是 Operational 頻道收取機制的必然。

## 規則模型

`KnownIssueRule`（`Analysis/KnownIssueCatalog.cs`）新增六個管理欄位：

| 欄位 | 用途 |
|---|---|
| `Id` | 穩定識別鍵，seed 同步／匯入都靠它指名道姓比對 |
| `Origin` | `builtin`（程式內建，seed/匯入會更新其內容）／`custom`（使用者自訂，程式永不覆寫） |
| `Enabled` | 停用開關，語意見上方「三條邊界」第 1 點 |
| `Scope` | 生效範圍，此版本只接受 `"all"`，為未來多主機/群組規則卡位（見下） |
| `MatchAllEventIds` | 顯式宣告「不看 EventIds，來源命中就算」，取代舊版「EventIds 空陣列＝全比對」的隱含語意 |
| `MatchFilter` | 為未來「同規則同主機下只關閉部分比對範圍」卡位，此版本必須為 `null` |
| `Platform` | `windows`（預設）／`linux`，決定用哪組比對欄位（2026-07-28 新增，見下） |

### 雙平台（2026-07-28，docs/LINUX-RULES.md）

Linux syslog 沒有 Event ID，所以規則模型多了一個 `Platform` 欄位與三個 Linux 專用比對欄位
（`ProgramPattern`／`EventNamePattern`／`MessagePatterns`），**共用同一份規則儲存與同一套抑制、
匯入、驗證、Web CRUD 機制**——不分兩套 store。兩個平台的比對欄位互斥（Windows 規則不可填 Linux
欄位，反之亦然），由 `RuleValidator` 平台條件式驗證把關；`FindRule`／`FindLinuxRule` 依平台分路，
遮蔽偵測也按平台分區（Windows 規則永遠不會遮蔽 Linux 規則）。完整語意見該文件 §1。

一個容易踩的點：`ProgramPattern` 沿用 `SourcePattern` 的子字串比對語意，所以 program 名稱有包含
關係時（`"sudo"` 包含 `"su"`）順序有意義，具體的必須排在泛用的前面。

另一個 Linux 專屬限制：**`ProgramPattern` 僅接受英數字與 `_`／`.`／`-`**（`RuleValidator`
把關，2026-08-07 全案體檢新增）——它會以裸 term 形式直接進 Sentinel 的 Lucene filter
（`SentinelQueryBuilder.LinuxRuleProgramClauses` 的 `sp:{pattern}*`，不像 `MessagePatterns`
有引號＋跳脫保護），空白或 `(`／`:`／`*` 等特殊字元會讓整份夜間取數查詢語法壞掉、
整批主機查詢失敗。字元集與 `SentinelEventMapper` 的 msg 前綴 program 正則一致
（syslog identifier 的實務形狀），17 條種子全數天然合格。

### `MatchAllEventIds` 為什麼要顯式宣告

規則外部化前，`EventIds` 空陣列天然代表「這個來源全部事件都算」（`WHEA-Logger` 等 3 條規則
如此使用）。正規化儲存後，「子表沒有列」如果繼續沿用這個隱含語意，會出現「有人不小心刪光
某規則的 EventId 列，規則就靜默變成全比對」的地雷——偵測範圍暴增卻沒有任何警訊。改成顯式旗標
後，`RuleValidator` 會擋掉「`MatchAllEventIds=false` 但 `EventIds` 為空」的不合格規則，
資料遺失的後果是「規則被拒絕、跳過並警告」，不是「偵測範圍靜默改變」。

### Id 命名與永久性

- Builtin 規則 Id 慣例：`builtin-{類別}-{代表事件}`（如 `builtin-storage-disk-io`）。
- Custom 規則建議 `custom-` 開頭（`RuleImportPlanner` 用 `Origin` 欄位而非 Id 前綴判斷歸屬，
  前綴只是慣例，不是程式邏輯依據）。
- **Id 一經出貨（隨版本釋出）永不改名**：Id 是 seed 同步與匯入去重的鍵，改名等於舊規則變孤兒、
  新規則被當成全新項目插入。規則語意大幅調整時，正確做法是「舊 Id 標記 `Enabled=false`
  （或保留供歷史回查）＋新 Id 新增」，而不是編輯既有規則的 Id。

## 驗證（`RuleValidator`）

純函數，載入後逐條檢查，單條不合格就跳過該條、其餘規則照常載入——手動編輯打錯一條不該讓
整份規則表失效：

- 必填欄位非空、長度不超過 `RuleSchemaLimits` 的上限（與未來 DB 的 `nvarchar` 上限同一組數字，
  單點定義；同時替 prompt 預算把關，避免自訂規則塞超長文字稀釋小模型注意力）
- `Scope` 必須為 `"all"`、`MatchFilter` 必須為 `null`（此版本尚未支援，卡位欄位）
- `EventIds` 非空或 `MatchAllEventIds=true` 二擇一成立
- Id 不可重複（後者跳過）

**遮蔽偵測**（充分條件，非精確語意）：`FindRule` 依清單順序取第一個命中者，若排在後面的規則
的比對範圍已被前面且啟用中的規則完全涵蓋，就永遠不會被命中。只警告不跳過，由人決定是否調整
順序或縮小範圍——這是判斷力的問題，程式不擅自代勞。

## 儲存後端與 Interface

沿用專案既有的 Strategy + Factory 模式（與 `IAnalysisRecordStore` 同一套）：

```
IKnownIssueRuleStore （介面：Location / Exists / Load / Save）
  └ KnownIssueRuleStore  （唯一實作，DB blob，key=rules）

ISuppressionStore     （介面：Location / LoadAll / SaveAll）
  └ SuppressionStore     （唯一實作，DB blob，key=suppressions）
```

Web DI 以 `StorageBackend.Blob("rules")`/`Blob("suppressions")` 組出兩個 store（單一路由點，
與分析紀錄同一開關）；`KnownIssueCatalog`/`RuleBootstrapper`/`LogAnalysisService` 等消費端只認介面。

> **2026-07-24 起規則存資料庫**：Jsonl 檔案後端已全面退役（見 docs/archive/HISTORY.md「2026-07-24」段
> 定案 10），`Storage.Type` 收斂為 Sqlite／SqlServer 二選一，兩者都是 DB。原本的
> `JsonKnownIssueRuleStore`／`JsonSuppressionStore` 已於 2026-07-28 的簡化重構改名為
> `KnownIssueRuleStore`／`SuppressionStore`（名稱裡的 Json 早已名不符實——底層一律走
> `EfJsonBlobStore` 存進 `lf_blobs`）。下方保留的容錯設計中，「檔案」請讀作「blob 內容」。

序列化與容錯設計：

- **整份 JSON 語法錯誤 → Load 失敗，且不覆寫使用者的壞內容**，讓使用者能看著原值修正；
  程式降級用內建種子（規則）或空清單（抑制）繼續執行，不因內容壞掉而整個中斷。
- **單一物件解析失敗只跳過該條**，其餘照常載入（逐元素 try/catch，而非整份反序列化）。
- **原子寫入**：由 `EfJsonBlobStore.Mutate` 的單一交易保證（含 `UpdatedAt` 樂觀鎖與重試），
  取代檔案時代的「寫 `.tmp` 再 `File.Move`」手法——批次與 Web 併發寫入不會有一方被靜默蓋掉。
- Enum（`Category`/`Severity`）以字串儲存（`JsonStringEnumConverter`），不是數字——
  值本身可讀，也對應下方 DB 正規化草案的 `CHECK` 約束設計。

## Seed／匯入政策

> **現況（console 退場後）**：下文的 `--import-rules`／`--apply`／`--overwrite-builtin` 這組
> CLI 旗標已隨批次 console 專案退場移除，現行入口是 **Web 規則維護頁的「內建規則升級」
> 橫幅**（預覽差異 modal＋「覆蓋已修改的內建規則」勾選＝同一套語意，分類/套用邏輯
> 在 Core 純函數 `RuleImportPlanner`）。本節描述的**合併語意逐條不變**，原文保留。

**初次部署寫入、後續手動匯入**（已與使用者確認的決定）：

- `rules.json` 不存在時，`RuleBootstrapper` 寫入完整內建種子，僅此一次。
- 之後程式改版若調整了 `KnownIssueSeed.CreateRules()`（新增規則、修訂知識庫文字），
  **不會自動覆寫**使用者的 `rules.json`——啟動時只提示「內建規則有更新（vX→vY），
  可執行 `--import-rules` 檢視」，實際套用需要維護者主動執行指令並確認。
- `--import-rules`（`RuleImportPlanner`）以 `Id` 為鍵做 diff：
  - 種子裡存在、`rules.json` 沒有的 → **新增**
  - 兩邊 Id 相同、內容相同（不比較 `Enabled`）→ **略過**
  - 兩邊 Id 相同、內容不同、`Origin` 為 `builtin` → 預設**略過並提示**，需要
    `--overwrite-builtin` 才會覆蓋；覆蓋時**保留使用者原本的 `Enabled` 設定**
    （使用者停用某條 builtin 是操作決定，不是「內容被改過」，匯入不該把它悄悄打開）
  - 兩邊 Id 相同但 `Origin` 不是 `builtin`（使用者把它改成 custom 或衝突）→ **衝突**，
    不處理，需要人工排解
  - 預設**只預覽**（列出將新增/更新/略過/衝突的 Id 與原因），加 `--apply` 才真正寫入；
    套用後把 `SeedVersion` 更新為 `KnownIssueSeed.Version`
- **Custom 規則一律不受 seed／匯入影響**——這是「builtin 歸程式管、custom 歸使用者管」模型
  的核心：使用者想調整某條 builtin 的內容（改門檻、改嚴重度、改處置文字），正確做法是
  把該條 `Enabled` 設 `false`，複製一條改成 `custom-` 開頭的 Id 再修改，程式永遠不會碰它。
  代價是「微調也要複製整條」，換來的是零隱藏合併邏輯：打開 `rules.json` 看到的就是實際生效的
  內容，不需要理解程式會怎麼「聰明地」合併。

## 抑制機制

`RuleSuppression`（`Models/RuleSuppression.cs`）：`RuleId`、`Scope`（`Host`／`Group`／`Site`，
回饋十三輪 F 加入，見下）、`Host`、`HostGroupId`、`Reason`、`SuppressedBy`、`CreatedAt`、
`ExpiresAt`（`null`＝永久）、`MatchFilter`（卡位，必須 `null`）。獨立於規則本身儲存
（`suppressions.json`，無 seed 概念，缺檔＝空清單），因為兩者生命週期不同：規則是全域設定，
抑制是各主機/群組/全站的營運狀態。

**語意**（詳見上方「三條語意邊界」第 2 點）：只影響通知與風險升級，不影響偵測與紀錄。

**生效範圍三選一**（`SuppressionScopes`，回饋十三輪 F）：2000 台規模下同一條規則要在每台
同類主機上各設一次抑制，維護成本最終只會讓人乾脆停用整條規則，反而失去分類與知識庫——
`Scope` 因此不只是單台主機：

- **`Host`**（預設，既有資料未帶此欄位時反序列化到此值，語意與改版前逐位相同，零遷移）：
  只對 `Host` 欄位指定的單台主機生效。
- **`Group`**：對 `HostGroupId` 指定的主機群組**目前所有成員**生效（不是「建立當下的成員快照」，
  之後加入該群組的主機自動適用）。
- **`Site`**：不限主機或群組，全站生效。

**主機與到期比對**（`Analysis/SuppressionFilter.cs`，純函數）：`Host` 範圍不分大小寫比對
`Environment.MachineName`；`Group` 範圍比對呼叫端注入的「該主機所屬群組 Id 集合」是否包含
`HostGroupId`（`SuppressionFilter` 本身是純函數，不依賴群組 store，群組成員展開由呼叫端——
`LogAnalysisService`／`NetiqPipelineService`／`AnalysisOrchestrator`／`WeeklyCheckupService`
——各自解析後傳入）；`Site` 範圍一律生效。`ExpiresAt` 已到期的項目不生效，但**不自動刪除**——
不留痕跡地讓抑制過期會讓人以為「已經處理好了」，實際上只是靜默恢復告警。到期後：

- 每次執行的啟動階段（排程／立即執行）列出「已到期、恢復告警」的提示。
- 需要人工到 Web `/admin/rules`「告警抑制」分頁清理，這是刻意的：到期後的清理需要人判斷
  「這個問題後來到底處理了沒有」，不該由程式自動猜測。

**體檢固定提醒**（`WeeklyCheckupService`）：只要體檢確實產生報告（窗口內有訊號、AI 敘事成功），
就固定列出本機生效中的抑制清單（含 Group／Site 範圍展開後對本機生效的項目）＋窗口期間各自
的發生次數——防止「暫時關掉」變成永久盲區。不會為了顯示這個清單而強制觸發原本因「三層皆無
訊號」而省略的 AI 呼叫，維持既有的成本控制設計。

**維護入口**：`/admin/rules`（系統管理 > 規則維護）的「告警抑制」分頁——新增（選規則＋範圍
＋範圍目標＋事由＋選填到期天數）、查詢（可依主機/規則/平台過濾）、解除，皆走既有儲存前
驗證與稽核管線（見 docs/WEB-SPEC.md §9.7）。

## `RuleId` 落紀錄

`LogIssueSignature` 新增 `RuleId`（命中規則的穩定 Id）與 `Suppressed`（本次是否被抑制），
由 `KnownIssueCatalog.Classify` 與 `LogAnalysisService` 填入，隨歷史紀錄一起寫入（含無風險日
的精簡路徑，`RecordStorageShaper` 明確保留這兩個欄位）。這是未來管理頁「頻率報表」與
「哪些規則被哪些主機關閉」查詢的資料基礎——用 `Id` 查詢不受規則內容演進影響，比事後用
`(Source, EventId)` 反推更穩定。

## 未來擴充卡位（此版本不實作，只預留欄位/語意）

- **`KnownIssueRule.Scope`**（規則本身的生效範圍，與下方「抑制機制」的 `RuleSuppression.Scope`
  是兩個不同欄位，不要混淆——後者已於回饋十三輪 F 實作 Host/Group/Site 三值）：目前只接受
  `"all"`（全域規則）。多主機/群組規模化時（見 `docs/archive/HISTORY.md` 的 NetIQ Sentinel
  規劃）預期會加入主機名或群組名，讓「環境特有雜訊規則」不用套用到所有主機。欄位已卡位，
  屆時只需要在 `RuleValidator` 放寬檢查、在 `FindRule`/`Classify` 加入呼叫端的主機身分比對，
  不需要動 schema。
- **`MatchFilter`**（規則與抑制皆有）：為「同一條規則、同一台主機下，只想關閉其中一部分
  比對範圍」卡位（例如「這台主機上 MyApp 的 7034 是雜訊，其他服務的 7034 要照常告警」）。
  此版本刻意不實作——這個粒度的比對語意會顯著複雜化，且需求尚未被證實，欄位先卡位、
  語意留待需求出現再定義。

## 未來 DB 映射（欄位級草案，遵守 `docs/DB-SPEC.md` 的雙 DB 可移植規則）

`rules.json` 是巢狀 JSON，但 DB 階段**不做「序列化成 JSON 字串塞進 nvarchar 欄位」**——那只是
把檔案格式的習慣搬進關聯式資料庫，改一條處置步驟要整包字串解析/編輯/跳脫，DB 的型別檢查與
約束完全幫不上忙，也違背了「規則搬進 DB 好維護」的初衷。改為正規化的 1 主表＋3 子表：

```
lf_rules（每條規則一列）
  rule_id             nvarchar(100)  PK
  origin              nvarchar(10)   NOT NULL   CHECK (origin IN ('builtin','custom'))
  enabled             bool           NOT NULL
  scope               nvarchar(20)   NOT NULL   CHECK (scope IN ('all'))            -- 未來擴充值
  sort_order          int            NOT NULL                                       -- 比對順序，程式編號寫入
  match_all_event_ids bool           NOT NULL
  match_filter        nvarchar(100)  NULL                                           -- 卡位，此版本恆 NULL
  source_pattern      nvarchar(100)  NOT NULL
  category            nvarchar(20)   NOT NULL   CHECK (category IN (...IssueCategory 各值))
  severity            nvarchar(20)   NOT NULL   CHECK (severity IN (...IssueSeverity 各值))
  count_threshold     int            NOT NULL
  description         nvarchar(500)  NOT NULL
  plain_explanation   nvarchar(1000) NOT NULL
  impact              nvarchar(1000) NOT NULL

lf_rule_event_ids（規則的事件 ID，一 ID 一列；match_all_event_ids=true 時此表無列）
  rule_id   nvarchar(100)  FK → lf_rules
  event_id  int
  PK (rule_id, event_id)

lf_rule_causes（常見原因，一原因一列，seq 保序）
  rule_id   nvarchar(100)  FK → lf_rules
  seq       int
  cause_text nvarchar(500)
  PK (rule_id, seq)

lf_rule_steps（處置步驟，一步驟一列，seq 保序）
  rule_id   nvarchar(100)  FK → lf_rules
  seq       int
  step_text nvarchar(500)
  PK (rule_id, seq)

lf_rules_meta（單列，對應 rules.json 頂層的兩個版本號）
  schema_version  int
  seed_version    int

lf_rule_suppressions
  suppression_id bigint         IDENTITY PK   -- 代理鍵：host/host_group_id 依 scope 可為 NULL，
                                               -- 不適合直接當 PK 組成欄位（PK 欄位不可為 NULL）
  rule_id       nvarchar(100)
  scope         nvarchar(10)   NOT NULL   CHECK (scope IN ('Host','Group','Site'))  -- 回饋十三輪 F
  host          nvarchar(255)              NULL   -- 只有 scope='Host' 時有值
  host_group_id bigint                     NULL   -- 只有 scope='Group' 時有值
  reason        nvarchar(500)  NOT NULL
  suppressed_by nvarchar(100)
  created_at    timestamp
  expires_at    timestamp      NULL
  match_filter  nvarchar(100)  NULL   -- 卡位，此版本恆 NULL
  -- 同 (rule_id, scope, host|host_group_id) 覆寫去重是應用層 upsert 邏輯保證（現行 blob store
  -- 的既有作法：寫入前先移除同鍵舊項），不是 DB 約束——Site 範圍沒有目標可比對，唯一性
  -- 天然落在 (rule_id, scope='Site')，用 filtered unique index 表達比硬湊 PK 更乾淨。
```

要點：

- `enum` 存名稱字串＋`CHECK` 約束，不存數字——資料列本身可讀，打錯字在寫入當下就被擋下，
  不用等程式啟動解析失敗才發現（`rules.json` 階段沒有 DB 約束，所以 `RuleValidator` 的
  啟動期驗證必須涵蓋這塊，兩個後端都靠它把關，不是 DB 階段才需要）。
- `sort_order` 對應 `rules.json` 的陣列順序語意（比對順序＝清單順序），由程式在匯入/寫入時
  自動編號，使用者不需要理解或填寫任何優先權數字。
- `match_all_event_ids=true` 時 `lf_rule_event_ids` 沒有列——**必須是顯式旗標決定，不能靠
  「這規則有沒有列」反推**，否則「使用者不小心刪光某規則的 event id 列」會被誤解成
  「這規則要比對全部事件」，偵測範圍靜默暴增且沒有任何警訊（這正是 `MatchAllEventIds`
  設計成顯式欄位而非隱含語意的原因，同一顧慮延伸到正規化表設計）。
- `lf_rule_causes`/`lf_rule_steps` 刻意分兩張表，不合併成一張加 `kind` 欄位——兩者語意不同、
  未來演進方向也可能不同（例如處置步驟表想加「預估耗時」欄位），混在一張表省不了多少，
  卻讓每次查詢都要多帶一個條件。
- Builtin 覆寫（`--overwrite-builtin` 對應的 DB 版本）：一個交易內 upsert `lf_rules` 主表列 →
  刪除 `lf_rule_event_ids`/`lf_rule_causes`/`lf_rule_steps` 的舊列 → 重插新列。子表全刪全插
  而非逐列 diff——builtin 內容以程式內建種子為準，diff 沒有意義，全換最不容易出錯。
- `lf_rule_suppressions` 的 `(rule_id, host)` 複合主鍵天然去重，新增抑制對同一鍵覆寫即可。
- 欄位長度全部對齊 `RuleSchemaLimits`（`Analysis/RuleSchemaLimits.cs`）——JSON 階段與 DB 階段
  用同一組數字，換後端時不會出現「JSON 階段能存、DB 階段塞不進欄位」的落差。
