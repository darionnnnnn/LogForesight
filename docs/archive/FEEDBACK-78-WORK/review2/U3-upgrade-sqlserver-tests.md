# U3 體檢報告：部署升級／SQL Server／測試品質／維護性（唯讀，第 78 輪 1e304931..HEAD）

未重複 R1～R3 已處置項目。探針都已刪除，`git status` 乾淨。

## 發現

### F1 中｜升級後「只靠設定信箱的收件人」的已寄主機日，工作流郵件腿由完成退回未完成，且不會自癒
- 情境：升級前已寄出的主機日（回望 14 天內），工作流帳本內存的是「舊算法意圖」（內容指紋含 AiAnalyzed／AiPending／DetailPruned 當時值）。升級後意圖改用 `ForMailContent`（三旗標固定 false），與舊意圖不同。
  收件人是設定內直填信箱、或沒有逐主機明細的統計型收件人（`view.Detail == null`）時：
  - 已寄鍵由 `LegacyKeySent` 擋下，不會重寄，所以也永遠不會走 `TrackWorkflowMail` 把新意圖記成已收。
  - 收尾流程 `PlanWorkflowMail(closingPass)` 會把計畫換成新意圖，新意圖沒有任何已送達的部分。
  - 逐問題收據重投影（`TrackAcceptedContributionWorkflow`）對 `Detail == null` 直接 `continue`；`TrackDurableUrgentStatisticsReceipts` 以新鍵查舊格式的 outbox 鍵，查不到。
- 預期：升級不改變既有主機日的郵件完成狀態。
- 實際（MailNotificationService.cs:2465-2490 PlanWorkflowMail、2492-2508 TrackAcceptedContributionWorkflow、2543-2560 TrackDurableUrgentStatisticsReceipts；寄出判定 2132-2155 LegacyKeySent）：升級後第一趟 `MailIsComplete` 變 false。`RunMonitorService.cs:373-377` 顯示郵件狀態 pending／partial、`WorkflowComplete=false`、`WorkflowOutcome=pending`。我只在程式內找到顯示用途的消費端，沒有找到會依它重跑工作的路徑，所以影響限於「執行總表、日明細的工作流狀態」。
- 已用探針重現：是。把既有升級測試複製成探針，額外把工作流帳本（MailIntents／MailExpectedPartsByIntent／MailDeliveredPartsByIntent／MailDeliveredParts）改寫成舊意圖後再跑一趟：
  - summary＋只填信箱：升級前 complete=True → 升級後 complete=False，已寄 1 封，AcceptedMailContributionReceipts=0
  - urgent＋只填信箱：同上 complete=False
  - 有帳號（逐主機明細）的組合：complete=True（收據重投影有補回）
  - 重現方式：複製 `MailNotificationServiceTestsSupersede` 的 Harness，寄一次後用 `HostDayWorkflowStore.Update` 把新意圖與其 part（`SHA256(intent + "|recipient|" + SHA256(小寫信箱))`）換成 `ForRecordWithFlags(record,false,true,false)` 算出的舊意圖，再把已寄鍵換成舊鍵，翻轉旗標後 `NotifyAfterRunAsync`。
- 測試缺口：既有 `升級前舊算法鍵已寄_旗標翻轉後不重寄也不留待送項目`（MailNotificationServiceTestsSupersede.cs:124-170）只改寫 `SummarySentKeys／UrgentSentKeys／UrgentOutbox`，工作流帳本仍是新意圖，所以看不到這個退化。
- 嚴重度：中（顯示層、一次性、限回望 14 天內且為統計型收件人；沒有重寄或漏寄）。
- 建議：升級相容要連帶處理工作流帳本。選項一：`LegacyKeySent` 命中時，順手以新意圖對該紀錄的各收件人 `TrackWorkflowMail(accepted:true)`（僅在舊鍵命中且舊意圖部分確實已收時）。選項二：`PlanWorkflowMail` 在舊意圖仍有已收部分時沿用舊意圖到回望期結束。補一個「帳本也是舊意圖」的升級測試。若決定接受，請在 WEB-SPEC 郵件節註明「升級後 14 天內統計型收件人的主機日郵件腿顯示未完成」。

### F2 低｜`PrtgSnapshotHostedServiceTests.cs` 檔首雙 BOM（batch 4 新增，與 R2 已修的同一類瑕疵）
- 實際：`xxd` 前 6 位元組為 `efbbbf efbbbf`；基準 1e304931 只有單 BOM。引入點是 e045e1b5。自查修正只處理了 `PrtgSnapshotDiagnosticsStore.cs`，沒有掃全部本輪改動檔。
- 已重現：是（掃描本輪改動的 .cs/.js/.md/.props，只有這一個檔）。嚴重度低。建議：移除多出的一組，並考慮加一個掃描守門測試。

### F3 低｜以原始碼文字比對代替行為的測試
- `BackgroundRepeatedReadTests.cs:171-179`（A1）：讀 `AnalysisOrchestrator.cs`、`ServiceCollectionExtensions.cs` 全文，用 `IndexOf("new IssueCaseCoordinator(")` 再取後 600 字元找 `PerRunIssueOwnerStore`。排版變動（參數增加）就會假紅；有人在別處也包裝則看不出。
- `BackgroundRepeatedReadTests.cs:268-273`（A2）：斷言 `PrtgDailyPipeline.cs` 不含 `"() => new SystemSettingsStore("`，換個寫法（`new SystemSettingsStore(x)` 配 lambda 以外的形狀）就假通過。
- `Batch2cTests.cs:394-400` 附近 A5：`Regex.Matches(text, "new PrtgScopeRevisionReader\\(")` 要求恰好 1 個。
- `Batch2cTests.cs:135-145` A4：掃整個 repo 所有 .cs（排除 obj/bin/Tests）找字串 `"/ 5) * 60"` 只在一個檔。既依賴檔案系統、也只認得這一種寫法。
- 嚴重度低（專案已有 FrontendConsistency 等同類守門，可視為慣例，但這四條屬於「行為可測卻改用文字比對」）。建議：A1 改成建構後驗證 `GetAll` 只讀一次的行為（已有 `CountingProxy` 範式）；A2／A5 用計數替身驗證 reader 被共用；A4 若要保留，登記到守門測試類並加註白名單。

### F4 低｜日期／時間相關的測試脆弱點（均非今天炸彈，只在極端時刻可能偶發）
- `MailNotificationServiceTestsSupersede.cs:30-32`：`ManualClock(DateTime.UtcNow)` 與 `Yesterday = DateTime.Today.AddDays(-1)` 兩個真實時鐘來源並用；服務以 `_clock.LocalToday()` 判定窗口。跨本機午夜的那一瞬間兩者可能差一天（機率極低，且該 class 內多個案例共用）。建議整份改固定日期＋固定時鐘（自查修正已對批次 1 對帳測試這麼做）。
- `Batch2cScenarios.cs:25,45`、`RunPreviewBatch5bTests.cs:90-170`、`PrtgBatch2bScenarios.cs:144`（`AssessEnv.Completed`）、`PrtgBatch4Rig.cs:52,196`：以 `DateTime.Today` 為錨，golden 以 D-n 相對標記正規化，所以不會因日期過期；只在測試跑過本機午夜時有一次性風險。
- `FirstSeenBatchTests.cs:168`：`Today.AddDays(-round * 0)` 恆為 `Today`，式子無作用（程式碼雜訊，不影響斷言）。

### F5 低｜新增只為測試存在的正式碼觀察點（R2/R3 未列者）
- `MailNotificationService.IntentComputedProbe`（internal 屬性，正式環境恆為 null）與 `MailIntentMemo(Action<string,DailyAnalysisRecord,bool>? probe)` 建構子參數：只供「一次通知內意圖只算一次」測試計數。每次記憶查詢都做一次 `probe?.Invoke`。
- `MailNotificationService.MailContextFingerprint` 由 private 改 internal，只為測試組出舊鍵。
- `HostDayWorkflowStore.GetManyChunkSize`（internal const）、`PrtgSnapshotHostedService.BusinessScopeComputeCount`（R2 已提）。
- 建議：保持 internal 並在各處註明「僅測試」，已是現況；若要減少，可由測試以 `CountingProxy` 之類外部替身量測，而非在正式類別加鉤子。

### F6 低｜行為差異（文件已載明，但對「站台長時間跑夜間分析」值得再確認）
- `PerRunIssueOwnerStore`（PerRunIssueOwnerStore.cs:16-45）：夜間一趟（3000 台可達數小時）內，管理者在網頁改「問題負責人的自動套用結論」，後續主機日不會採用，要等下一趟。程式註解與規劃文件寫明可接受；依 CLAUDE.md「不改運作邏輯」原則，這是一項已知取捨，並非遺漏。包裝內快取的清單元素是共用的，我查了呼叫點（`IssueCaseCoordinator.cs:194`、`MuteAwareSuppressionStore.cs:49`）只讀取，未發現修改。

## 檢查過、確認沒問題的項目

### 升級當下
- 郵件已寄鍵：`LegacyKeySent` 窮舉 8 種旗標，且 `WorkflowRecordKey` 的舊鍵形狀與測試用 `LegacyKey` 一致（探針重現鍵部分正確：升級後 `Sent.Count == 1`、未重寄、無非終態 outbox）。
- 待送項目（pending outbox）升級：舊格式鍵不在新活鍵集合會被標 `superseded`，新鍵另建並寄出；已被 SMTP 接受的收件人由 `MigrateLegacyAcceptedNetIqContributions` 以 outbox 的 ProblemKeys 轉成逐問題收據，不會重寄給已收者（讀碼確認，PRTG 問題走 HeldLegacy 不算已收，屬既有設計）。
- 重試退避狀態只在記憶體，重啟後歸初值，無持久化格式變更。
- 首見日表、掛接歷程、工作流指紋 `ForParentRecord`／`LegacyParentFingerprint`：本輪未改算法；`UpdateIfChanged` 與 raw 比對遇到舊格式 blob 只會寫一次，不會反覆重寫。
- `PrtgSnapshotDiagnosticsStore.RecordBatchCounts`：文件結構與上限檢查沿用，無格式變更。
- `ScheduleOptionsStore` 改 `cached: true`：走 `JsonBlobSingleton` 的版本探測（每次 Get 先讀單列版本，版本變才重讀），多站台實例並存仍會看到對方寫入；仍每次反序列化出新物件，讀改寫語意不變。
- `PrtgScopePurgeStore.ReadCandidatePage` 候選為空直接回水位線：與 golden（PurgeEmpty／PurgeEmptyScope）一致。
- `PrtgRiskProjectionStore.RunBatchDetailed`：只改回傳，不加投影版本，升級不觸發全表重評。

### SQL Server（EF `ToQueryString`＋連線攔截器確認可翻譯，探針已刪）
- `HostDayWorkflowStore.GetMany`：`blob_key IN (OPENJSON … nvarchar(100))`＋`CASE WHEN CAST(LEN(content) AS int) <= 65536 THEN content ELSE NULL END`；單一 JSON 參數，每段 500 個鍵，遠低於 2100。字串長度以 `LEN`（忽略尾端空白）在查詢端判定，超限以記憶體再驗＋回退單筆 `Get`，行為一致。
- `CountLightweightAtLeast` 的 `host_id <> 0 AND host_id IN (OPENJSON bigint)`、`GetStateChangesForRuleEvaluation`（OPENJSON、`ORDER BY sensor, changed_at, id`）、`GetDeviceIdNames`、首見日「已存在鍵」查詢（`OPENJSON … nvarchar(255)`，鍵 500 一段）都可翻譯，且為單一 JSON 參數。
- `BuildDayRiskCountsQuery`：`COUNT(CASE WHEN …)`＋`GROUP BY record_date`；`HasDuplicateHostDay`（`GroupBy().Any(g => g.Count() > 1)`）與 `CountRiskDays`（`GroupBy(_ => 1)`）能翻譯。
- `FrozenSet<long>.Contains`、`IReadOnlySet<long>.Contains` 在 EF 8＋SqlServer 都可翻譯（FrozenSet 只出現在快取鍵與可見主機集合，查詢用的是 IReadOnly 宣告型別）。
- 首見日批次 INSERT：每段 100 列 × 4 參數＝400；條件式 UPDATE：每段 100 組 → 1 + 200 = 201 參數，均在 2100 內；批次 INSERT 在 SQL Server 由 `SqlException`（2627/2601）被 `catch` 攔下後逐筆降級；`EnableRetryOnFailure` 的暫時性錯誤（含 1205）由執行策略重試。未涉及使用者交易，無額外鎖範圍擴大。R2 已提的鎖順序疑慮不重複。
- `CountLightweightAtLeast` 對 `HostId == 0` 舊列不計入（下界），且與 `HostMatcher`「有 host_id 只認 host_id」一致，不會高估而誤擋。
- 定序：本輪新增查詢中，會因大小寫不分改變結果的只有 R3 F5 已列的出現點分組；首見日的 `source_key` 為寫入時大寫化、讀回後以序數字典比對，大小寫差異在 SQL Server 端會多一次逐筆降級但結果正確。

### 測試品質
- 對帳測試（IssueAggregateBatch5a/5b）的預言機是獨立的記憶體舊演算法重做（OracleRows 直接讀全表），不是呼叫被測路徑；Batch2b／Batch2c／Batch4 的 golden 是在改動前的乾淨副本錄製，且日期以 D-n 正規化，無「斷言初值等於期望」的假通過。
- 並行寫入：`FirstSeenBatchTests` 的競態案例用檔案型 SQLite（各自連線），未共用 `EfSqliteFixture` 的記憶體連線；`BackgroundRepeatedReadTests.CountingProxy.GetAllCalls` 是實例欄位不是 static；`PrtgOperationScope.ReadVersions()` 是全域登記表，但測試都以 `Guid` kind 過濾，不受並行干擾。
- 新測試檔沒有使用反射碰私有成員（`BindingFlags`／`GetField`／`GetMethod` 皆無）；`HostStoreLookupTests`、`PrtgBatch4RepeatedLoadTests` 的計數器是實例層級。
- 沒有新增牆鐘時間門檻斷言（無 Stopwatch／Elapsed 比較）。

### 維護性
- 新增的公開／內部方法都有正式消費端（GetMany→RunMonitorService:331；RecordBatchCounts→PrtgSnapshotHostedService；UpdateIfChanged／*IfChanged→郵件收尾與 PostProcessor；GetDeviceIdNames、GetStateChangesForRuleEvaluation→PrtgDailyPipeline；QueryDayStatusLightweight→ScheduleController:244；CountLightweightAtLeast→RecordListQueryService:154）。
- 查詢端與記憶體端兩條聚合路徑並存（`NoMergedHostsInScope`／`HasDuplicateHostDay` 決定走哪條）是刻意的退路，舊路徑沒有變成死碼；代價是以後改聚合語意要兩邊同步，已有對帳測試守住。
- 工作樹：探針檔（ZzProbeU3.cs、ZzProbeSql.cs）已刪除，`git status` 乾淨。

## 推論／未驗證
- F1 的「只有顯示用途」是搜尋 `IsComplete`／`WorkflowComplete`／`MailIsComplete` 的結果，沒有逐一跟蹤所有動態路徑；若有未列出的消費端（例如匯出）需另行確認。
- 沒有實連 SQL Server：所有 SQL Server 結論僅限 EF provider 翻譯與 SQL 形狀，不含實際執行計畫、鎖與逾時。
