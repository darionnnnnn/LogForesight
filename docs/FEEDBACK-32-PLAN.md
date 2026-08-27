# LogForesight 第三十二輪規劃

> 狀態：實作完成，待體檢
> 基準：dev@a57d770（2942 綠，略過 6）
> 分支：`feature/feedback-32`
> 來源：使用者回饋三項（報告年月分層與保留期／NetIQ 匯入下拉寬度／掃描併發）
> 實作方式：委派 `agy`（Antigravity CLI），整輪只用這一種；Claude 負責規格、逐段獨立驗收、終檢

## 待決定案（使用者採用建議選項）

| 項目 | 定案 | 不選的理由 |
|---|---|---|
| 年月資料夾層級 | `export\{主機}\{yyyy-MM}\` | `export\{yyyy-MM}\{主機}\` 會讓同一台主機的報告散在各月目錄；3000 台規模下「找某台主機」是主要動線 |
| 年月來源 | 由檔名 `yyyy-MM-dd` 前綴推導 | 另傳日期參數＝同一事實兩處表達，會漂移 |
| 舊報告 | **不搬移**（沿用 `Db\` 那輪慣例） | `DailyAnalysisRecord.ReportFile` 存的是寫入當下的絕對路徑，讀取端吃絕對路徑，舊檔原地照樣讀得到；自動搬移是不可逆操作，收益只有「目錄好看」 |
| 報告保留期 | 新增 `ReportRetentionDays`，範圍 90~3650，預設 **1095**（3 年） | 沿用 `RetentionDays` 正是「拉不長」的根因——報告是純文字小檔，跟 DB 要不要留這麼久是兩件事 |
| 套用範圍 | 三種報告（風險／週檢／權限異動）一律套用年月分層與新保留期 | sink 只有一個出口，分流要多寫判斷且無使用者可見的好處 |
| 併發度來源 | 掃描列的下拉（每次掃描自選），非系統設定 | 一次臨時加速不該變成長期設定 |
| 併發上限 | 3 | 使用者指定 |
| 總預算 | 900 秒不變 | 併發的收益是「同樣時間掃完更多段」，不是放寬逾時 |
| UI 設計方案 | 不套 `ui-ux-pro-max`，沿用該列既有控制項樣式 | 本輪只調寬度與加一個同型下拉，無新視覺決策 |

**已知副作用（刻意接受）**：DB 紀錄過了 `RetentionDays`（預設 180）就被清除，屆時 Web 沒有入口能點開對應報告，該報告只以磁碟檔案存在——年月資料夾正是為這個情境而分。

## 批次總覽

| 作業 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 報告檔年月分層＋空目錄清理 | 小（2 檔＋測試） | 無 |
| B | `ReportRetentionDays` 設定全鏈 | 中（Core 4 處＋Web 5 處） | 無（與 A 正交） |
| C | 掃描併發（後端平行化） | 中偏難（執行緒安全） | 無 |
| D | 併發參數全鏈＋掃描列 UI | 小 | C |
| E | 文件收斂 | 小 | A~D |

建議順序：A → B → C → D → E。A/B/C 彼此無相依，但整輪只有一個執行者，依序派。

## 作業總覽（委派）

- 本輪委派模型：`agy`（gemini-3.7-flash-high）。中途不換；若換，於「執行紀錄」註明起點且不換回。
- 每階段抄成獨立規格檔交付，不讓執行端看整份規劃。
- Claude 每段獨立重驗（`dotnet test` ＋ `git diff` 逐條對契約），不採信執行端摘要。

---

## 作業A：報告檔年月分層

### 現況與核對結果

- [FileReportSink.cs](../LogForesight.Core/Persistence/FileReportSink.cs)：只分 `export\{host}\`，`host` 為空字串時直接落在 `export\`。三個呼叫端：
  - [RiskReportService.cs:117](../LogForesight.Core/Service/RiskReportService.cs:117)（`DailyRisk`，檔名 `yyyy-MM-dd_{風險等級}風險_{類別}.txt`）
  - [WeeklyCheckupService.cs:150](../LogForesight.Core/Service/WeeklyCheckupService.cs:150)（`WeeklyCheckup`，檔名 `yyyy-MM-dd_週檢.txt`）
  - [AnalysisOrchestrator.cs:381](../LogForesight.Core/Service/AnalysisOrchestrator.cs:381)（`Permission`，`host: ""`，檔名 `yyyy-MM-dd_權限異動.txt`）
  三者檔名**都**以 `yyyy-MM-dd` 開頭。
- [ExportReportPruner.cs](../LogForesight.Core/Service/ExportReportPruner.cs)：已用 `SearchOption.AllDirectories` 遞迴掃描並依檔名前綴判斷日期，**多一層目錄不影響清理正確性**；但它只刪檔、從不刪空目錄。
- [FileReportReader](../LogForesight.Core/Persistence/IReportReader.cs)：吃 `ReportFile` 的絕對路徑（或相對資料根目錄），只驗證「解析後仍在資料根目錄內」——多一層子目錄仍在根目錄內，讀取端零改動。

### 定案

年月子目錄由 sink 從 `fileName` 前 10 碼推導，成功才建子目錄；**解析失敗時退回舊行為**（不建年月層），不猜、不擲例外——sink 不該因為呼叫端給了非慣例檔名就讓整次分析失敗。

日期解析與 `ExportReportPruner.TryParseReportDate` 是同一個判準，抽成 Core 內共用的單一方法，兩邊引用同一份（避免「一邊認得、一邊不認得」的漂移）。

### 改動

1. `FileReportSink.WriteAsync`：`dir` 的組成由 `{export}[/{safeHost}]` 改為 `{export}[/{safeHost}]/{yyyy-MM}`，`yyyy-MM` 自 `fileName` 前 10 碼解析；解析失敗則不加這一層。
2. 日期解析抽成共用方法（現有 `ExportReportPruner.TryParseReportDate` 為既有實作，改由兩處共用；`internal` 可見度足夠，兩者同組件）。
3. `ExportReportPruner.Prune`：刪檔之後，**由深至淺**移除 `exportDir` 底下已無任何檔案與子目錄的空目錄（`exportDir` 本身永不刪除）。空目錄判定必須同時看檔案與子目錄，不能只看檔案。

### 測試／驗收

- 寫入 `2026-08-27_高風險_安全.txt`、host=`SRV01` → 實際路徑為 `{export}\SRV01\2026-08\2026-08-27_高風險_安全.txt`。
- 同上但 host 為空字串 → `{export}\2026-08\2026-08-27_權限異動.txt`。
- 檔名不以 `yyyy-MM-dd` 開頭（例如 `report.txt`）→ 落在 `{export}[\{host}]\report.txt`，**不新增年月層**、不擲例外。
- host 含 `..\` 等路徑字元 → 既有淨化行為不變（既有測試須續綠）。
- 跨月：同一 host 的 `2026-07-31_*` 與 `2026-08-01_*` 落在兩個不同子目錄。
- Prune：`{export}\SRV01\2026-01\` 底下全部檔案過期被刪後，`2026-01` 與（若已無其他月份）`SRV01` 目錄一併消失，`export` 本身仍存在。
- Prune：目錄下尚有未過期檔案時，該目錄不得被刪。
- 既有 `ExportReportPrunerTests` 全數續綠。

---

## 作業B：`ReportRetentionDays` 設定全鏈

### 現況與核對結果

- 報告檔清理呼叫點只有一處：[AnalysisOrchestrator.cs:534](../LogForesight.Core/Service/AnalysisOrchestrator.cs:534)，現吃 `retention.RetentionDays`。
- 保留天數的傳遞鏈：`SystemSettings`（DB blob）→ [RuntimeSettingsResolver.ApplySystemSettingsOverrides](../LogForesight.Core/Service/RuntimeSettingsResolver.cs:26) → `RetentionOptions`（[AnalysisOrchestrator.cs:1104](../LogForesight.Core/Service/AnalysisOrchestrator.cs:1104)）→ Orchestrator。
- Web 端鏈：[SettingsDtos.cs](../LogForesight.Web/Models/Dto/SettingsDtos.cs)（讀取 DTO ＋ 更新 DTO 的 `[Range(MinRetentionDays, 3650)]`）→ [SystemSettingsService](../LogForesight.Web/Services/SystemSettingsService.cs)（`Update` 指派、`ToDto` 映射、設定異動稽核摘要）→ [Settings.cshtml:443-457](../LogForesight.Web/Views/Pages/Settings.cshtml:443) 的 `number` 欄位 → [settings.js:474](../LogForesight.Web/wwwroot/js/pages/settings.js:474) 的 `renderRetentionFields`。
- 舊 blob 沒有這個鍵 → `System.Text.Json` 給 C# 屬性初始值（＝新預設 1095），語意正確：升級後報告保留期變長，**不會刪掉本來留著的東西**，零遷移風險。

### 定案

- 新欄位名 `ReportRetentionDays`，出廠預設常數 `DefaultReportRetentionDays = 1095`。
- 範圍與其他保留天數一致：`[Range(SystemSettings.MinRetentionDays, 3650)]`。
- **與 `RetentionDays` 之間不設任何大小關係約束**——報告本來就要能活得比 DB 紀錄久，設「必須 ≤ RetentionDays」會直接否定本項需求。
- 設定頁文案要講清楚已知副作用（見上方「已知副作用」），不能讓使用者以為報告在 Web 上永遠點得開。

### 改動

1. `SystemSettings`：新增 `ReportRetentionDays` 屬性與 `DefaultReportRetentionDays` 常數。
2. `RetentionOptions`：新增同名 `init` 屬性，預設引用該常數。
3. `RuntimeSettingsResolver`：把 DB 值疊上去；越界（< `MinRetentionDays` 或 > 3650）時保留內建預設並記 `Log.Warn`，與同檔 `RawEventRetentionDays` 的既有處置一致。
4. `AnalysisOrchestrator` 報告清理呼叫點改吃 `retention.ReportRetentionDays`，console 訊息的天數一併改（訊息現在會印天數，不可留舊值）。
5. Web：讀取 DTO ＋ 更新 DTO 新增欄位（含 `[Range]` 與中文 `ErrorMessage`，文案與鄰近欄位同型）；`SystemSettingsService` 的 `Update` 指派、`ToDto` 映射、設定異動稽核摘要（若該摘要列出其他保留天數，這一項也要列）。
6. `Settings.cshtml`：在「稽核與追責紀錄保留天數」之後新增一組欄位，`id="report-retention-days"`，`min=90 max=3650`，樣式與鄰近欄位逐字同型；說明文字須包含：這一項只管 `export\` 底下的報告檔（風險／週檢／權限異動），與 DB 紀錄的保留期各自獨立；超過 DB 保留期後報告仍在磁碟上、但 Web 已無入口可點開。
7. `settings.js`：`renderRetentionFields` 讀入、送出時帶上。

### 測試／驗收

- `SystemSettings` 預設值為 1095。
- 舊 blob JSON（無 `ReportRetentionDays` 鍵）反序列化後為 1095。
- Resolver：DB 值 400 → `RetentionOptions.ReportRetentionDays == 400`；DB 值 10（越界）→ 維持 1095。
- Resolver：`ReportRetentionDays` 大於 `RetentionDays` 時**照樣採用**，不被夾住。
- 更新 DTO 的 `[Range]` 邊界：89 不合法、90 合法、3650 合法、3651 不合法。
- `SystemSettingsService.Update` 後 `ToDto` 回讀得到同一個值。
- 前端原始檔字串比對測試（比照 `LocalizationLintTests` 的既有形狀）：`Settings.cshtml` 含 `id="report-retention-days"`，`settings.js` 含 `report-retention-days`。
- **不得**出現「報告保留天數必須小於等於歷史資料保留天數」這類驗證。

---

## 作業C：掃描併發（後端平行化）

### 現況與核對結果

- [NetiqDirectoryClient.ListHostsAsync:334](../LogForesight.Web/Services/NetiqDirectoryClient.cs:334)：`for` 逐段跑，每段「主掃描 → 補充掃描」，全程共用**一個** `SentinelClient`。
- `SentinelClient` 類別文件明寫「單一 instance＝單一併發佇列」，要平行必須各建實例（各自 SAML token、各自 `DisposeAsync` 登出）。
- **既有前例可抄**：[NetiqPipelineService.cs:386](../LogForesight.Core/Service/NetiqPipelineService.cs:386) 的 `ConcurrentBag` client pool ＋ `Parallel.ForEachAsync(MaxDegreeOfParallelism)` ＋ 「租不到就是不變量被破壞」的 `InvalidOperationException`。
- [SentinelQueryBuilder.ExpandToSegments:471](../LogForesight.Core/Analysis/SentinelQueryBuilder.cs:471)：分段**位址互不重疊**（/24 前綴或 /26 的 64 個 IP）。
- `ScanState` 非執行緒安全（裸 `HashSet`／`Dictionary`／`List`），且殘差輪會 `new HashSet(scan.SeenIps)` 列舉共享集合——平行下會擲 `InvalidOperationException`。
- **既有寫法在平行下會出錯（本輪必修）**：預算用盡時以 `segments.Skip(completedSegments.Count)` 推算未掃描網段，只在依序時成立。

### 定案

- **每個分段各自一份 `ScanState`**，不共用、不加鎖。合法性來自「分段位址互不重疊」：跨段之間本來就不需要互看已見 IP（排除清單早已是段內範圍，見 `SegmentExclusionOrNull`）。加鎖共用一份是另一條路，但鎖的粒度會落在殘差輪的熱路徑上，且 `SeenIps` 的列舉快照仍需額外處理——隔離比加鎖簡單且更快。
- 每段的 `ScanState` 以 `alreadyKnown` 建構（沿用既有語意）。**在段的工作內部建立**，不預先建 N 份——同時存活的份數受並行度節制。
- 併發度 1（預設）時行為必須與改動前**逐位相同**：池內只有一個 client、`Parallel.ForEachAsync` 退化為依序、分段處理順序仍為原順序。
- 結果合併：`Hosts` 依**分段原順序**串接後再由呼叫端去重（`BuildScanResult` 既有 `GroupBy` 去重不動），確保同一輸入的輸出順序穩定、不隨排程抖動。
- `warnings` 為共享清單：以鎖保護寫入，最後**依分段原順序**輸出，不留下不確定順序。
- 總預算（`budgetCts`）語意不變：仍是整趟掃描的 wall-clock 上限。

### 改動

1. `ListHostsAsync` 新增併發度參數（1~3，實際值由呼叫端傳入；方法內以 `Math.Clamp(value, 1, 3)` 夾住，上限定義為具名常數 `MaxScanConcurrency = 3`，理由註解比照 `MaxParallelQueriesPerServerLimit`）。
2. 分段迴圈改為 client pool ＋ `Parallel.ForEachAsync`，寫法比照 `NetiqPipelineService` 既有 pool（含「租不到＝不變量被破壞」的例外）。池大小＝併發度；池中每個 client 各自建立、各自 `DisposeAsync`。
3. 每段結果收進「以分段索引為鍵」的容器（例如固定長度陣列），完成後依索引順序合併主機清單、`incompleteSegments`、`warnings`。
4. **未掃描網段改以集合差集計算**：`segments` 扣掉「已完成的段」，不再用 `Skip(count)`。
5. 進度回報：多段情境下改以**已完成段數**為分子（`Interlocked` 累加），文案維持「掃描中 {已完成}/{總數}（{最近完成的段}）」等價語意；單段情境（`isSingleSegment`）的既有三段式文案完全不動。
6. 單段情境維持既有的「例外直接往上丟」行為（併發對單段無效）。

### 測試／驗收

- 併發度 1：對既有假 client 的查詢**順序與次數**與改動前相同（既有 NetIQ 探索測試全數續綠，不得修改既有斷言來配合）。
- 併發度 3、8 個分段：8 段全部被查詢；主機清單為各段聯集且無重複；**同時在跑的查詢數不超過 3**（以假 client 記錄進入／離開的併發峰值斷言）。
- 併發度 3 兩次執行同一輸入 → `Hosts` 順序完全相同（決定性）。
- 併發度傳 0／5 → 實際使用 1／3。
- 預算用盡（假 client 對第 2 段之後全部延遲）：警告訊息列出的「未掃描網段」**確實是沒被查過的那些**，不得因為完成順序不同而指錯段。
- client pool：建立的 client 數＝併發度；全部被 `DisposeAsync`（假 client 記錄 dispose 次數）。
- 併發度 > 1 且分段數 == 1：行為與併發度 1 相同（不建多餘 client）。

---

## 作業D：併發參數全鏈與掃描列 UI

### 現況與核對結果

- 參數鏈（照 `granularity` 的既有路徑逐一對照）：[netiq-import-wizard.js:331](../LogForesight.Web/wwwroot/js/pages/netiq-import-wizard.js:331) `api.post('/api/admin/netiq/scan', {...})` → [NetiqDtos.cs:155](../LogForesight.Web/Models/Dto/NetiqDtos.cs:155) → [AdminController.cs:220](../LogForesight.Web/Controllers/Api/AdminController.cs:220) → [NetiqDiscoveryService.StartScan:60](../LogForesight.Web/Services/NetiqDiscoveryService.cs:60) → `ScanAsync` → `DiscoverAsync` → `INetiqDirectoryClient.ListHostsAsync`。**六個轉手點，一處沒接就靜默退回預設。**
- `INetiqDirectoryClient` 有兩個實作：`SentinelRestDirectoryClient` 與 `StubNetiqDirectoryClient`（離線示範資料），兩者簽章都要跟上。
- [netiq-import-wizard.js:79](../LogForesight.Web/wwwroot/js/pages/netiq-import-wizard.js:79)：粒度下拉 `maxWidth = '200px'`，選項文字「每段 254 台（預設）」被截斷。

### 定案

- 併發度以整數傳遞（1~3），DTO 上 `[Range(1, 3)]`；未帶值時預設 1（＝既有行為）。
- 粒度下拉寬度：由 `200px` 放寬到足以完整顯示最長選項；**同列其餘控制項的寬度不動**。
- 併發下拉緊接在粒度下拉之後、掃描按鈕之前，樣式（`class`、寬度寫法）與粒度下拉同型。
- 提示文字：在既有那句粒度提示之後補一句說明併發的代價與單段無效，不新增第二個提示區塊。

### 改動

1. 掃描請求 DTO 新增 `Concurrency`（`[Range(1, 3)]`，可為 null＝1）；Controller 傳入 `StartScan`。
2. `StartScan` / `ScanAsync` / `DiscoverAsync` / `ListHostsAsync`（含 `StubNetiqDirectoryClient`）逐層帶上，預設值 1。
3. 前端：粒度下拉寬度修正；新增 `id="scan-concurrency-select"` 下拉，選項為「單一查詢（預設）」=1、「2 個併發」=2、「3 個併發」=3；送出時帶進 `api.post` 的 body。
4. 提示文字補一句：併發會同時對這台 Sentinel 開多條查詢（各自獨立登入），節流間隔是每條各自計算；只掃單一網段時併發沒有作用。

### 測試／驗收

- Controller 測試：body 帶 `concurrency: 3` → 傳到探索服務的值為 3；不帶 → 1；帶 5 → 模型驗證失敗（400）。
- 全鏈測試：以假 `INetiqDirectoryClient` 斷言 `ListHostsAsync` 收到的併發度＝請求值（六個轉手點任一沒接都會紅）。
- 前端原始檔字串比對測試：`netiq-import-wizard.js` 含 `scan-concurrency-select`、含三個選項值 `'1'/'2'/'3'`，且 `api.post('/api/admin/netiq/scan'` 的 body 含 `concurrency`。
- 前端原始檔字串比對測試：粒度下拉的 `maxWidth` 已不是 `200px`。
- 既有掃描相關測試全綠。

---

## 作業E：文件收斂

1. [DB-SPEC.md:448](DB-SPEC.md:448) 保留策略表：把「export 報告檔」自 `RetentionDays` 那列移出，新增 `ReportRetentionDays` 一列（預設 1095），並註明它與 DB 紀錄保留期各自獨立。
2. [WEB-SPEC.md:2307](WEB-SPEC.md:2307)：`export\*.txt` 的路徑描述補上 `{主機}\{yyyy-MM}\` 分層。
3. [WEB-SPEC.md](WEB-SPEC.md) §9.9a「匯入」分頁：補併發選項的行為與代價（含單段無效）。
4. [DETECTION-SPEC.md:545](DETECTION-SPEC.md:545) 週檢報告路徑 `export\{日期}_週檢.txt` → 更新為含年月層的形式。
5. 設定頁欄位說明同步進 WEB-SPEC 的設定章節（若該章節逐項列出保留天數）。
6. `CLAUDE.md` 測試基線數字更新。

現行文件只陳述現況，不寫「原本是 X、第 32 輪改成 Y」。

## 明確不做（本輪定案）

- **不搬移既有報告檔**到年月資料夾（不可逆、收益僅目錄整齊；舊路徑照樣讀得到）。
- **不做 Web 上的報告檔瀏覽／下載頁**：本輪需求是磁碟端的封存結構，不是新畫面。
- **不把掃描併發做成系統設定**（`NetiqOptions`）：定案為每次掃描自選。
- **不放寬掃描總預算**（900 秒）。
- **不提高併發上限到 4**（雖然 `MaxParallelQueriesPerServerLimit` 是 4）：使用者指定 3，且互動掃描可能與夜間 pipeline 同時對同一台 Sentinel 施壓。
- **不對 `ReportRetentionDays` 與 `RetentionDays` 設大小關係約束**。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1 報告年月分層 | agy | 通過 | 相關測試 27 綠 | agy 另開新檔寫 `FileReportSinkTests`，但基準版 `ExportReportPrunerTests.cs` 裡**已有同名類別**，它改命名空間躲掉編譯衝突 → 兩個同名測試類別。Claude 合併成一份、刪除重複的逃逸字元案例，既有斷言未改 |
| A2 空目錄清理 | agy | 通過 | 相關測試 28 綠（含合併後） | 無 |
| B1 Core 設定鏈 | agy | 通過 | 相關測試 20 綠 | console 訊息原本寫「風險報告檔」，但清理對象是三種報告 → Claude 改為「報告檔（風險／週檢／權限異動）」 |
| B2 Web 設定鏈 | agy | 通過 | 相關測試 91 綠 | agy 主動接上設定頁既有的「縮短保留期先確認」（`reducedItems`）機制，與鄰近欄位一致，保留 |
| C1 分段平行化 | agy | 通過 | NetIQ 271 綠 | agy 為了不改測試替身，在 `INetiqDirectoryClient` 上加了一個舊簽章的相容多載（＝為測試遷就正式碼）→ Claude 移除多載、改為更新 `FakeClient` 簽章並補 `LastConcurrency` |
| D1 併發參數全鏈 | agy | 通過 | NetIQ 278 綠 | 無（五個轉手點全接上，含「不傳＝1」回歸） |
| D2 掃描列 UI | agy | 通過 | UI 字串比對 5 綠 | agy 把粒度寬度從 200px 改成 230px（仍是魔術數字）→ Claude 改為 `width:auto` 依內容撐開，與字型／縮放無關 |
| E 文件 | Claude | 通過 | — | DB-SPEC 保留策略表、WEB-SPEC（報告路徑／設定頁／§9.9a 併發）、DETECTION-SPEC 週檢路徑、CLAUDE.md 基線 |

## 併回前終檢

- **全套測試**：2985 通過／略過 6／總計 2991（基準 2942，淨增 49）。
- **跨段產出鏈回頭 grep**：
  - `NetiqScanRequest.Concurrency` → 前端 `api.post('/api/admin/netiq/scan', {… concurrency})` ✅
  - `SystemSettingsDto.ReportRetentionDays` → `settings.js` 讀入 `settings.reportRetentionDays` ✅、
    送出 payload 含 `reportRetentionDays` ✅
  - `UpdateSystemSettingsRequest` 的**唯一**前端組裝點是 `settings.js` 的 `api.put('/api/admin/settings')`
    （已 grep 確認沒有第二個 payload 組裝處）——漏帶新欄位會讓值變 0 而卡在 `[Range]`，這條有查 ✅
- **BOM**：每個被改的檔案與基準 `a57d770` 逐一比對，全數未變。Claude 自己一次 `utf-8-sig` 寫入
  曾對 `ExportReportPrunerTests.cs` 加上 BOM，當場還原。
- **讀取端相容性**：`FileReportReader` 依紀錄存下的完整路徑讀取、只驗「仍在資料根目錄內」，
  新舊路徑都通；全 repo grep `"export"` 的組路徑點只有 sink 建構、pruner 呼叫、sink 預設值三處，
  不存在「用日期＋主機自行拼路徑再讀」的第二個讀取端。
- **已接受的殘留**：
  - `FileReportSink`（Persistence）為複用日期解析而 `using LogForesight.Core.Service`，
    分層方向略為顛倒；替代方案是把判定複製一份，違反「同一判定只寫一次」，故接受。
  - `ListHostsAsync` 的 `catch (AggregateException)` 正規化區塊在 `Parallel.ForEachAsync` 的
    實際例外形狀下不會被觸發（await 會直接重擲第一個內層例外），屬防禦性死碼；
    保留不影響行為，未實測前不動它。

## 體檢交接

- **實作模型**：Claude Opus 5（本輪規格、逐段驗收、手改、文件皆由它產出；實作委派 `agy` gemini-3.7-flash-high）。
- **測試**：2985 通過／略過 6／總計 **2991**（基準 dev@a57d770 為 2942，淨增 49）。
- **範圍**：`dev..feature/feedback-32`，9 個 commit（規劃 1、批次 A1/A2/B1/B2/C1/D1/D2 各 1、文件 1）。

### 實作方自認最沒把握的地方（體檢請優先看，但**不要只看這幾處**）

1. **C1 的平行化正確性**——「每段各自一份 `ScanState` 所以不必加鎖」這個論證建立在
   「`ExpandToSegments` 產出的分段位址互不重疊」上。這一點我讀過 `SentinelQueryBuilder`
   確認過，但若某天粒度選項變動（例如真的加了 /25）而分段開始重疊，這個設計會**靜默**
   退化成漏抓（跨段之間不再互看已見 IP）。請確認這個前提有沒有在程式碼裡被釘住，
   或至少是否該加一條斷言／註解。
2. **`budgetExceeded` 是多執行緒寫入的裸 `bool`**。我的判斷是 `Parallel.ForEachAsync`
   完成即構成記憶體屏障、讀取都在其後，因此安全；但這是推論不是實測。
3. **`catch (AggregateException)` 是防禦死碼**（`await` 會直接重擲第一個內層例外，
   不會是 AggregateException）。我選擇保留未動，理由是沒實測前不想改例外面。
   體檢若同意它不可能觸發，可以砍掉。
4. **`FileReportSink`（Persistence 層）為複用日期解析而 `using LogForesight.Core.Service`**，
   分層方向略為顛倒。替代方案是複製一份判定，違反「同一判定只寫一次」，故接受。
5. **報告檔與 DB 紀錄保留期脫鉤後的使用者體感**——超過 `RetentionDays` 之後 Web 上沒有
   任何入口能點開那份報告。設定頁說明與文件都寫了，但這是產品面的取捨，值得再看一眼
   文案夠不夠清楚。
6. **年月分層對既有部署的影響**：舊報告不搬移，所以升級後 `export\` 下會同時存在
   「舊的扁平檔」與「新的年月子目錄」。清理端遞迴掃描兩者都吃得到（既有測試涵蓋），
   但沒有實機驗證過混合狀態。

### 本輪未做、留給體檢判斷的

- 沒有實機跑過一次完整的 NetIQ 掃描（需要可連的 Sentinel）。併發的實際加速幅度、
  對 Sentinel 的負載都只有單元測試層級的保證。
- 設定頁的新欄位沒有在真實瀏覽器操作過（掃描列下拉寬度有用實際字型量測驗證：
  「每段 254 台（預設）」文字寬 176px，舊的 200px 框只容得下 141.8px 確實截斷，
  `width:auto` 後為 177.8px 可完整顯示）。
