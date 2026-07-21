# 規則外部化＋主機級抑制機制（設計定案，2026-07-21）

> 規劃日期：2026-07-21。緣起：`KnownIssueCatalog.Rules` 原本寫死在程式碼，規則調整（環境特有雜訊、
> 新增偵測項目）需要重新編譯部署，維護門檻過高。本文件是這次調整的完整設計定案，實作見
> `Analysis/KnownIssueCatalog.cs`（`KnownIssueRule`＋比對邏輯）、`Analysis/KnownIssueSeed.cs`（內建種子）、
> `Analysis/RuleValidator.cs`、`Analysis/SuppressionFilter.cs`、
> `Persistence/IKnownIssueRuleStore.cs`／`JsonKnownIssueRuleStore.cs`、`Service/RuleBootstrapper.cs`、
> `Service/RuleImporter.cs`、`Models/RuleSuppression.cs`、`Persistence/ISuppressionStore.cs`／
> `JsonSuppressionStore.cs`、`Service/SuppressionCli.cs`。

## 目標與整體流程

1. **規則從寫死改為外部維護**：初次部署時把內建種子（`KnownIssueSeed.CreateRules()`）寫入
   `rules.json`（未來 DB 後端則寫入資料表），之後在該檔案/資料表直接維護，不需要重新編譯部署。
2. **啟動流程**：`rules.json` 不存在 → 寫入種子（僅此一次）；存在 → 載入＋驗證（單條不合格
   跳過、遮蔽偵測只警告）→ 呼叫 `KnownIssueCatalog.Initialize` 生效。全程見
   `RuleBootstrapper.Run`。
3. **後續更新走手動匯入**：程式改版新增/修訂 builtin 規則後，**不會自動覆寫**使用者的
   `rules.json`（避免「排程執行悄悄改變偵測行為」），而是啟動時提示、由維護者主動執行
   `--import-rules` 決定是否套用（見「Seed／匯入政策」）。
4. **主機級告警抑制**：獨立於規則本身，讓維護者能對「已知雜訊」的規則在特定主機關閉通知，
   同時不犧牲偵測與歷史資料的完整性（見「抑制機制」）。

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
   常數（`CorrelationAnalyzer.AccountChangeIds` 等六組，故意標 `internal` 供 selftest 驗證）
   與規則表是兩份獨立維護的東西——`rules.json` 新增的 Security 事件規則**不會**自動延伸關聯層
   的偵測範圍；`--selftest` 有一項檢查會驗證這六組 ID 是否都存在於目前生效的規則表，抓漂移用，
   但不能反向保證「規則表的新事件都被關聯層涵蓋」。

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

### `MatchAllEventIds` 為什麼要顯式宣告

規則外部化前，`EventIds` 空陣列天然代表「這個來源全部事件都算」（`WHEA-Logger` 等 3 條規則
如此使用）。正規化儲存後，「子表沒有列」如果繼續沿用這個隱含語意，會出現「有人不小心刪光
某規則的 EventId 列，規則就靜默變成全比對」的地雷——偵測範圍暴增卻沒有任何警訊。改成顯式旗標
後，`RuleValidator` 會擋掉「`MatchAllEventIds=false` 但 `EventIds` 為空」的不合格規則，
資料遺失的後果是「規則被拒絕、跳過並警告」，不是「偵測範圍靜默改變」。

### Id 命名與永久性

- Builtin 規則 Id 慣例：`builtin-{類別}-{代表事件}`（如 `builtin-storage-disk-io`）。
- Custom 規則建議 `custom-` 開頭（`RuleImporter` 用 `Origin` 欄位而非 Id 前綴判斷歸屬，
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

沿用專案既有的 Strategy + Factory 模式（與 `IAnalysisRecordStore`/`JsonlAnalysisRecordStore`
同一套）：

```
IKnownIssueRuleStore          （介面：Location / Exists / Load / Save）
  └ JsonKnownIssueRuleStore    （前期實作：rules.json）
  └ (未來) DbKnownIssueRuleStore

ISuppressionStore              （介面：Location / LoadAll / SaveAll）
  └ JsonSuppressionStore        （前期實作：suppressions.json）
  └ (未來) DbSuppressionStore
```

`StorageFactory.CreateRuleStore`/`CreateSuppressionStore` 依 `Storage.Type` 選後端，與
`CreateRecordStore` 同一開關；未來新增 DB 後端只需新增實作類別＋一個 case，
`KnownIssueCatalog`/`RuleBootstrapper`/`LogAnalysisService` 等消費端不需修改。

`rules.json`／`suppressions.json` 的容錯設計：

- **整檔 JSON 語法錯誤 → Load 失敗，且不覆寫使用者的壞檔**，讓使用者能看著原檔修正；
  程式降級用內建種子（規則）或空清單（抑制）繼續執行，不因設定檔壞掉而整個中斷。
- **單一物件解析失敗只跳過該條**，其餘照常載入（逐元素 try/catch，而非整份反序列化）。
- **原子寫入**：先寫 `.tmp` 再 `File.Move(overwrite: true)`，避免寫入途中被中斷留下半個
  損毀的檔案。
- **UTF-8 with BOM**：規則檔內容是中文長文字，缺 BOM 時記事本等工具容易誤判編碼顯示亂碼。
- Enum（`Category`/`Severity`）以字串儲存（`JsonStringEnumConverter`），不是數字——
  人工編輯時看得懂，也對應未來 DB 的 `CHECK` 約束設計（見下）。

## Seed／匯入政策

**初次部署寫入、後續手動匯入**（已與使用者確認的決定）：

- `rules.json` 不存在時，`RuleBootstrapper` 寫入完整內建種子，僅此一次。
- 之後程式改版若調整了 `KnownIssueSeed.CreateRules()`（新增規則、修訂知識庫文字），
  **不會自動覆寫**使用者的 `rules.json`——啟動時只提示「內建規則有更新（vX→vY），
  可執行 `--import-rules` 檢視」，實際套用需要維護者主動執行指令並確認。
- `--import-rules`（`RuleImporter`）以 `Id` 為鍵做 diff：
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

`RuleSuppression`（`Models/RuleSuppression.cs`）：`RuleId`、`Host`、`Reason`、`SuppressedBy`、
`CreatedAt`、`ExpiresAt`（`null`＝永久）、`MatchFilter`（卡位，必須 `null`）。獨立於規則本身
儲存（`suppressions.json`，無 seed 概念，缺檔＝空清單），因為兩者生命週期不同：規則是全域設定，
抑制是各主機的營運狀態。

**語意**（詳見上方「三條語意邊界」第 2 點）：只影響通知與風險升級，不影響偵測與紀錄。

**主機與到期比對**（`Analysis/SuppressionFilter.cs`，純函數）：`Host` 不分大小寫比對
`Environment.MachineName`；`ExpiresAt` 已到期的項目不生效，但**不自動刪除**——不留痕跡地
讓抑制過期會讓人以為「已經處理好了」，實際上只是靜默恢復告警。到期後：

- 每次執行的啟動階段列出「已到期、恢復告警」的提示（`Program.cs`）。
- `--list-suppressions` 與 `SelfTestRunner` 都會列出到期狀態。
- 需要人工用 `--unsuppress` 或直接編輯 `suppressions.json` 清理，這是刻意的：到期後的
  清理需要人判斷「這個問題後來到底處理了沒有」，不該由程式自動猜測。

**體檢固定提醒**（`WeeklyCheckupService`）：只要體檢確實產生報告（窗口內有訊號、AI 敘事成功），
就固定列出本機生效中的抑制清單＋窗口期間各自的發生次數——防止「暫時關掉」變成永久盲區。
不會為了顯示這個清單而強制觸發原本因「三層皆無訊號」而省略的 AI 呼叫，維持既有的成本控制設計。

**CLI**（`Service/SuppressionCli.cs`，`Program.cs` 接線）：

```
LogForesight.exe --suppress <ruleId> --reason "<文字>" [--days N]
LogForesight.exe --unsuppress <ruleId>
LogForesight.exe --list-suppressions
```

提供 CLI 是因為手編 JSON 的中文 `reason` 容易打錯逗號/引號；`--suppress` 會驗證 `ruleId`
是否存在於目前生效的規則庫，避免抑制一個打錯字的 Id 而悄悄無效。

## `RuleId` 落紀錄

`LogIssueSignature` 新增 `RuleId`（命中規則的穩定 Id）與 `Suppressed`（本次是否被抑制），
由 `KnownIssueCatalog.Classify` 與 `LogAnalysisService` 填入，隨歷史紀錄一起寫入（含無風險日
的精簡路徑，`RecordStorageShaper` 明確保留這兩個欄位）。這是未來管理頁「頻率報表」與
「哪些規則被哪些主機關閉」查詢的資料基礎——用 `Id` 查詢不受規則內容演進影響，比事後用
`(Source, EventId)` 反推更穩定。

## 未來擴充卡位（此版本不實作，只預留欄位/語意）

- **`Scope`**：目前只接受 `"all"`（全域規則）。多主機/群組規模化時（見 `docs/PLAN.md` 的
  NetIQ Sentinel 規劃）預期會加入主機名或群組名，讓「環境特有雜訊規則」不用套用到所有主機。
  欄位已卡位，屆時只需要在 `RuleValidator` 放寬檢查、在 `FindRule`/`Classify` 加入呼叫端
  的主機身分比對，不需要動 schema。
- **`MatchFilter`**（規則與抑制皆有）：為「同一條規則、同一台主機下，只想關閉其中一部分
  比對範圍」卡位（例如「這台主機上 MyApp 的 7034 是雜訊，其他服務的 7034 要照常告警」）。
  此版本刻意不實作——這個粒度的比對語意會顯著複雜化，且需求尚未被證實，欄位先卡位、
  語意留待需求出現再定義。

## 未來 DB 映射（欄位級草案，遵守 `docs/DB-PLAN.md` 的雙 DB 可移植規則）

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
  rule_id       nvarchar(100)
  host          nvarchar(255)
  reason        nvarchar(500)  NOT NULL
  suppressed_by nvarchar(100)
  created_at    timestamp
  expires_at    timestamp      NULL
  match_filter  nvarchar(100)  NULL   -- 卡位，此版本恆 NULL
  PK (rule_id, host)
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
- `lf_rule_suppressions` 的 `(rule_id, host)` 複合主鍵天然去重，`--suppress` 對同一鍵覆寫即可。
- 欄位長度全部對齊 `RuleSchemaLimits`（`Analysis/RuleSchemaLimits.cs`）——JSON 階段與 DB 階段
  用同一組數字，換後端時不會出現「JSON 階段能存、DB 階段塞不進欄位」的落差。
