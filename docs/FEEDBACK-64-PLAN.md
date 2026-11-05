# LogForesight 第 64 輪規劃

> 狀態：規劃中（第二版：經使用者／管理者視角與架構視角兩輪獨立審查後修訂，待使用者確認後開分支）
> 基準：dev@6e056648（8,403 綠／11 略過；全量牆鐘 28 分 13 秒，每案加總 8,540 秒，8 核）
> 來源：使用者回饋三項——全量測試耗時、PRTG 取值重試不夠且不能打爆 PRTG、多排程窗口續跑未完成與失敗部分；追加三項——停用主機被列為失敗、執行時間顯示、站台診斷匯出（2026-11-05）檢視結果
> 實作方式：主代理寫階段規格，實作派內建 Agent（Sonnet 5.5，effort 由主代理依段評估；小型測試段可用 Haiku 5.5）；一個 checkout 一次只派一個改檔代理；每段主代理獨立重跑驗收

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A | 全量測試提速（不刪、不略過任何測試） | 中 | 無 | 1 |
| E | 排程頁與紀錄修正（停用主機、今天列與四路耗時、AI 失敗計數、噪音警告、建置識別） | 中 | 無 | 2（E-5 與 B-1 同一實作段） |
| B | PRTG 取數重試：逐台階段共用暫停閘、停損、待補裝置清單 | 中 | 無 | 3 |
| C | 多窗口續跑：只補失敗與未處理、停止點、單窗口續跑、里程碑 | 大 | B-2 欄位 | 4 |
| D | 文件與說明同步 | 小 | 各批次收尾時逐批做，不累積 | 隨批次 |

跨批次共同規則：
- 所有新增等待一律可注入時鐘／等待函式並接受 `CancellationToken`；測試不得真等。
- **停止鈕回應時限**：任何新增的等待（暫停閘、冷卻、續跑等待）在取消後 1 秒內返回，趟次標 `Stopped`；每批驗收都含「等待中按停止」的案例。
- 失敗後「下一次再試」的時機寫進執行輸出與狀態卡。
- 不新增沒有消費端的設定；新常數先寫在程式常數（暫定），不開設定頁。
- 新增 JSON 欄位一律加在 record 尾端並給預設值，舊 JSON 缺欄＝`null`（代表「舊紀錄，資訊未知」），消費端不得把 `null` 當空集合或 0。
- 每批次收尾同步更新 `CLAUDE.md` 測試基線與對應現況文件（不留到 D）。
- 驗收一律指定測試類別；斷言比對結構化欄位（數字、集合），不比整段文字；里程碑類只比關鍵片語。

---

## 批次 A：全量測試提速

### 現況與核對結果

| 項目 | 數字 | 證據 |
|---|---|---|
| 牆鐘 | 28 分 13 秒（測試本體 27 分 20 秒） | trx |
| 每案加總 | 8,540 秒；等效並行 5.2 倍 | trx |
| `new StorageBackend(` 的測試類 | 82 類、1,342 案、加總 6,445 秒＝**75%** | trx × 原始碼比對 |
| `StorageBackend` 建構成本 | 新 SQLite 檔 2,740 ms；**既有且已升級的 DB 仍 1,100～1,340 ms**；`EfSqliteFixture`（記憶體）34 ms | 臨時探針，量完已刪 |
| 建構子互斥 | `StorageBackend.cs:144` 取 `Global\LogForesight-Schema`（SQLite 與 SQL Server 共用常數，`:144,168,183`）再 `EnsureCreated`＋`SchemaUpgrader.Upgrade`；**所有測試即使各用自己的暫存 DB 也排成單線**；同一類別單獨跑 97 秒、全量裡 682 秒 | `StorageBackend.cs:139-161`；`NamedMutexGate.cs:19` |
| 序列集合尾段 | `KnownIssueCatalogState` 14 類 536 案 281 秒、`PrtgSnapshotSharedBudget` 205 秒、`CryptoKeyState` 107 秒；最後約 10 分鐘幾乎只剩它們（`PrtgDailyPipelineTests` 72 案單獨佔約 4～5 分鐘） | trx 逐分鐘類別序列 |
| 真時鐘配額 | `PrtgBackfillRangeRunnerTests.局部回填目標全部失敗不記新鮮度_一顆成功才記` 180 秒、`PrtgBackfillRunnerTests` 兩案各 60 秒、`PrtgFetchServiceTests.FetchDayAsync_併發設定值真的被採用而非寫死` 61 秒——都 `new PrtgRequestBudget()` 用 `RealPrtgClock` | 各測試檔；`PrtgFetchServiceTests.cs:144` 已有 `AcceleratedClock` |
| `SchemaUpgrader` 結構 | 單一靜態方法 1,853 行，`isSqlite` 旗標與 DDL 交錯；含資料步驟：seed 列（`:45-58`、`:1701`）、`UPDATE lf_work_orders`（`:356`）、`lf_issue_first_seen` 初次回補（`:1016-1075`） | `SchemaUpgrader.cs` |
| 其他 | 無 `xunit.runner.json`；node 子行程約 300 次、真實 Kestrel／HttpListener 約 80 案——都在分鐘級以下 | 結構掃描 |

### 定案

評估過不選的方案：只在測試端複製模板 DB（建構子仍跑 1.1 秒的 `Upgrade`，互斥鎖還在）；以升級步驟清單指紋短路（對不到 DB 實際狀態）；SQL Server 一起短路（`INFORMATION_SCHEMA` 雜湊保真度未證實）。

1. **A-1 互斥鎖只在 SQLite 分名**：SQLite 連線的鎖名加上資料檔**絕對路徑**（相對路徑先轉絕對、`:memory:` 用連線字串本身）的雜湊；**SQL Server 鎖名維持原常數不變**（避免滾動升級期間新舊行程鎖名不同、以及連線字串寫法差異讓互斥失效）。雜湊只用於組名稱，不寫 log。
2. **A-2 SQLite schema 雜湊短路**：
   - 位置：`SchemaUpgrader.Upgrade` 開頭，`isSqlite == false` 時**整段程式路徑與現在逐行相同**（守門測試見驗收）。
   - 短路鍵：`sqlite_master` 全部列的 `type`＋`name`＋`sql`（依名稱排序）的 SHA-256，加上升級器版本常數 `SchemaUpgrader.Version`。與 `lf_blobs` 鍵 `schema_fingerprint` 的記錄一致→跳過 schema 段；不一致、缺、或環境變數 `LF_SCHEMA_FORCE_UPGRADE=1`→走完整路徑，**全部成功後**重算並寫回；寫回失敗只記 Warn、不影響啟動。
   - 拆段：實作前先交「步驟歸屬表」（每個 `AddColumnIfMissing`／`AddIndexIfMissing`／`CreateTableIfMissing`／seed／UPDATE 一列，標 schema 段或資料段，資料段內若依賴某欄位存在要註明）；資料段每次照跑，順序維持原本相對順序。
   - 版本常數守門：測試掃 `SchemaUpgrader.cs` 的 schema 段呼叫簽章（呼叫次數與表／欄／索引名稱清單）算雜湊，與測試內記錄的雜湊比對；改了步驟沒遞增 `Version` 就紅，錯誤訊息講明要遞增。
3. **A-3 測試端**：四個真時鐘配額測試改注入 `AcceleratedClock`（抽到共用 helper）。接受「配額真時鐘行為失去直接覆蓋」：配額邏輯以 `IPrtgClock` 抽象，`PrtgRequestBudgetTests` 已用假時鐘覆蓋窗口規則。不新增 `xunit.runner.json`、不調執行緒數。
4. **A-4 拆序列集合**：`KnownIssueCatalogState` 只保留呼叫 `KnownIssueCatalog.Initialize`／`SecurityAuditWatchlist` 覆寫的類別；其餘移出，清單寫進執行紀錄。加守門測試：原始碼含 `KnownIssueCatalog.Initialize(` 的測試類別必須帶該 `[Collection]`。`CryptoKeyState`、`PrtgSnapshotSharedBudget` 不動。
5. **A-5 計時工具**：trx 解析腳本進 `LogForesight.Tests/tools/test-timing.py`；牆鐘數字只做執行紀錄的觀察值（同機、無負載、連跑 2 次取中位數），**不當驗收門檻**。
6. **明確不做**：不刪、不合併、不 `Skip` 測試；不直接拿掉 `DisableParallelization`；SQL Server 短路（進 BACKLOG）。

### 改動

1. `StorageBackend.cs`：鎖名組法（SQLite 分名、SQL Server 原名）。
2. `SchemaUpgrader.cs`：拆段、雜湊、`schema_fingerprint` 讀寫、`Version` 常數、強制旗標。
3. 測試：見驗收。
4. `LogForesight.Tests/tools/test-timing.py`。

### 驗收（每條可由 `dotnet test` 判定）

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| A-1-1 | 同一 SQLite 檔以相對與絕對路徑建構，鎖名相同；兩個不同檔鎖名不同；SQL Server 連線字串鎖名等於原常數 | `StorageBackendMutexNameTests`（新） |
| A-1-2 | 兩個不同 SQLite 檔同時建構，不互相等待（以可注入的 mutex 觀察器斷言兩者重疊持有） | 同上 |
| A-2-1 | 全新 DB 建構兩次：第一次走完整路徑並寫入 `schema_fingerprint`；第二次 schema 段零呼叫（以呼叫計數替身斷言），資料段呼叫次數與第一次相同 | `SchemaUpgraderFingerprintTests`（新） |
| A-2-2 | 已短路的 DB 手動 `DROP INDEX` 一個索引後建構→走完整路徑並補回索引 | 同上 |
| A-2-3 | `LF_SCHEMA_FORCE_UPGRADE=1`→走完整路徑 | 同上 |
| A-2-4 | `IsSqlite()==false` 的替身 context：`schema_fingerprint` 零讀零寫，且 schema 段與資料段呼叫序列與基準版（測試內記錄）逐項相同 | 同上 |
| A-2-5 | 寫回 `schema_fingerprint` 擲例外→建構成功、Warn 一則 | 同上 |
| A-2-6 | 版本常數守門：步驟簽章雜湊與記錄一致；突變（多加一個 `AddColumnIfMissing` 不改 `Version`）必紅 | `SchemaUpgraderVersionGuardTests`（新） |
| A-2-7 | 既有 `SchemaUpgrader*Tests`、`SchemaUpgraderModelParityTests` 全綠（77 案） | 既有 |
| A-3-1 | 四個案例單案 < 2 秒（測試內以 Stopwatch 守 5 秒上限） | 各原類別 |
| A-4-1 | 守門測試：含 `KnownIssueCatalog.Initialize(` 的測試類別皆帶 `[Collection("KnownIssueCatalogState")]`；突變（移出一個有呼叫的類）必紅 | `TestCollectionGuardTests`（新） |
| A-4-2 | 被移出的類別在全量連跑 2 次皆綠 | 執行紀錄 |
| A-觀察 | 全量牆鐘與加總（A-1 後、A-2 後、A-4 後各一次）寫進執行紀錄；8,403 綠／11 略過不變 | 執行紀錄 |

---

## 批次 B：PRTG 取數重試

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 分頁重試 | 每次 `PrtgTablePager.FetchAsync` 各 2 次（15／60 秒）；逐台 sensors／messages 每台各自重試、台與台之間沒有共用閘 | `PrtgTablePager.cs:126,159-168`；`PrtgFetchService.cs:624-690,826-940` |
| 數值取數 | 已有共用 `PauseGate`（15／60／180 秒＋jitter、`Retry-After`）、補抓輪、連續 10 顆停損、總暫停 15 分停損、冷卻 10 分；`PauseGate.WaitAsync(ct)` 吃取消 | `PrtgValueFetchRetry.cs:16-32,123-135`；`PrtgFetchService.cs:1025-1303` |
| 配額 | 只限速；失敗照計、無退避 | `PrtgRequestBudget.cs:84,113-116` |
| 逐台 sensors 失敗 | 只計數、不標 `HistoryAffectingFailure`、`sensorsRefreshed=false` 不清過期；同日其餘窗口沿用同步不補 | `PrtgFetchService.cs:318-339`；`PrtgDailyPipeline.cs:306-307` |
| 逐台 messages 失敗 | `Converged=false`→`HistoryAffectingFailure`→下個窗口整段重做 | `PrtgFetchService.cs:459-462,959` |
| 快照 | 一輪須在一個採樣間隔內完成，整輪失敗才退避、連續 3 次加倍 | `PrtgSnapshotHostedService.cs:657-691,1481-1496` |
| `PrtgFetchService` 生命週期 | 每趟 `Create` 一個實例；冷卻旗標是實例層 | `PrtgDailyPipeline.cs:165` |

### 定案

1. **B-1 逐台階段共用暫停閘（唯一退避層）**：sensors 逐台與 messages 逐台階段各建一個 `PauseGate`，**該階段的 pager 內建重試關閉**（`retry:null`），避免兩層等待疊加。任一台可重試失敗（`PrtgTablePager.IsRetryable`）→拉閘；worker 在**取得 semaphore 名額之前**等閘（不持有名額空等）；閘放行後該失敗裝置重排一次，第二次仍失敗→記入待補。暫停序列、jitter、`Retry-After`、連續失敗與總暫停停損全部**複用** `PrtgValueFetchRetryPolicy` 與 `PauseGate`（不複製判定）；`PauseGate` 補一個世代號：歸零只認閘放行後才發出的請求的成功（數值取數同時受惠）。
2. **B-2 停損與待補清單**：階段總暫停達 15 分或連續 10 台失敗→停損：剩餘裝置不送請求、全部記入待補。`PrtgDayStat` 加 `IReadOnlyList<long>? PendingSensorDevices = null, IReadOnlyList<long>? PendingMessageDevices = null`（`null`＝舊紀錄）。待補非空時：`SourceSynced` 仍為真（已完成的裝置完整）、**`NeedsReevaluation = true`**（補齊後該日要重評）、當日 `Outcome = partial`、`Note`「N 台待補（下一個窗口續補）」。停損寫 Warn（趟次「有警告」，不是 Error、不是 Failed）。停損拉起**實例層**冷卻（與數值取數同一旗標，刻意）：同一趟後面的逐台階段與觸發式取數看到冷卻直接略過並全部記待補，不再打 PRTG；下一趟（含手動立即執行）是新實例，不受影響。**過期感測器清除只針對本趟完成同步的裝置**（中途取消時完成集合只含已結束的裝置）。
3. **B-3 配額壓力冷卻：不做**。理由：與 B-1 閘、快照自己的退避形成三層疊加；全 purpose 共等會讓快照一輪超過採樣間隔，再觸發快照自己的加倍退避，形成連鎖。快照、守門、探測維持既有行為。
4. **B-4 messages 單台失敗語意與 sensors 一致**：記入 `PendingMessageDevices`，不標 `HistoryAffectingFailure`；全站分頁未收斂、裝置階段失敗、範圍計算失敗仍算歷史受影響。
5. **B-5 既有入口影響表**：

| 入口 | 影響 |
|---|---|
| 夜間管線 `FetchDayAsync` | 變（B-1／B-2／B-4） |
| 手動立即執行的 PRTG 路徑 | 同管線，變 |
| 手動「同步結構與對應」 | 同 `FetchDayAsync`，變 |
| 歷史回填：數值路徑 | 不變；狀態變更區間走逐台 messages，變（同 B-1） |
| 觸發式數值取數 | 不變（只多「冷卻中略過」既有行為） |
| 快照／守門／探測／估算／連線測試 | 不變 |
| run-preview 補跑預覽 | 不變（PRTG 預覽讀 `NeedsReevaluation`，待補日已標真） |

6. **B-6 可見性**：拉閘／放行經 `IRunProgress` 送 `prtg-paused`／`prtg-resumed`（沿用 `guard-paused` 的形狀），PRTG 卡顯示「PRTG 回應異常，暫停中（第 N 次，約 X 秒）」；每次拉閘在執行輸出寫一行 Info（原因、第 N 次、等幾秒）；閒置時 PRTG 卡多一列「待補裝置 N 台（下一個窗口續補）」（讀最新逐日統計）。
7. **升級說明**：舊紀錄 `NeedsReevaluation=true` 的日子升級後第一趟仍整段重做（一次性）；升級前逐台 sensors 失敗的裝置沒有紀錄，不會被補（已知限制，隔天全量同步自然修正）。

### 改動

1. `PrtgFetchService.cs`：逐台兩階段接閘、重排、停損、待補清單、清除範圍；`PrtgTablePager` 回傳「少於 treesize 的裝置數」供 E-5 彙總（介面變更）。
2. `PrtgValueFetchRetry.cs`：`PauseGate` 世代號；政策命名中性化（保留舊名別名）。
3. `BatchRunStore.cs`：`PrtgDayStat` 兩個待補欄位。
4. `PrtgDailyPipeline.cs`：統計寫入、`NeedsReevaluation`、Note、進度事件。
5. `runs.js`／`SchedulerHostedService.PrtgRunHealth`／`ScheduleDtos`：PRTG 卡兩列（DTO 新增欄位，不改舊欄位）。
6. `RunExportService.cs`：`unsuccessfulPrtgDays` 帶待補數。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| B-1-1 | 300 台、第 5 台起全回 503、併發 4、假時鐘：閘拉起後直到放行，HTTP 請求數不增加；放行後失敗台重排；請求總數有上界（＝已送台數＋重排台數，測試算出精確值） | `PrtgFetchServiceTests`（新案） |
| B-1-2 | 慢成功晚於新失敗：閘拉起前發出的請求在閘拉起後才成功→序列不歸零；閘放行後發出的成功→歸零 | `PrtgValueFetchRetryPolicyTests`（新案，數值路徑同測） |
| B-1-3 | 閘等待期間 semaphore 名額為空（替身斷言） | `PrtgFetchServiceTests` |
| B-1-4 | 閘等待中取消：1 秒內返回、趟次 `Stopped`、已完成裝置的鏡像保留 | `PrtgFetchServiceTests`、`PrtgDailyPipelineTests` |
| B-1-5 | 逐台階段的 pager 不再有內建重試（替身計數：單台 503 只送 1 次後交給閘） | `PrtgFetchServiceTests` |
| B-2-1 | 連續 10 台失敗→停損：剩餘裝置零請求；`PendingSensorDevices`＝失敗＋未送出集合（精確比對） | `PrtgFetchServiceTests` |
| B-2-2 | 停損後同趟 messages 階段與觸發式取數零請求、全部記待補 | `PrtgDailyPipelineTests` |
| B-2-3 | 待補非空→`SourceSynced=true`、`NeedsReevaluation=true`、`Outcome=partial`、趟次狀態 Warning | `PrtgDailyPipelineTests`、`PrtgReevaluationPlannerTests` |
| B-2-4 | 清除只刪已完成裝置的過期感測器；中途取消時待補裝置一顆不刪 | `PrtgFetchServiceTests` |
| B-2-5 | 舊 JSON 缺欄反序列化→兩個欄位為 `null`；消費端把 `null` 當「未知」不當空 | `BatchRunStoreTests`／`PrtgReevaluationPlannerTests` |
| B-4-1 | messages 單台失敗→不標 `HistoryAffectingFailure`、記 `PendingMessageDevices` | `PrtgFetchServiceTests` |
| B-5-1 | 快照、守門、探測測試類別全綠且無行為改變 | `PrtgSnapshotHostedServiceTests`、`PrtgResourceGuard*`、`PrtgProbe*` |
| B-6-1 | 進度事件 `prtg-paused`／`prtg-resumed` 送達；PRTG 卡文字與待補列由 DTO 欄位驅動 | `PrtgDailyPipelineTests`、`RunsPageUiTests`、`RunsTimelineBehaviorTests` |
| 突變 | 拿掉閘 `WaitAsync`→B-1-1 紅；待補只記失敗不記未送出→B-2-1 紅；清除退回全有全無→B-2-4 紅 | — |

定向：`--filter "FullyQualifiedName~PrtgFetchServiceTests|FullyQualifiedName~PrtgValueFetchRetry|FullyQualifiedName~PrtgTablePagerTests|FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~PrtgReevaluationPlannerTests|FullyQualifiedName~PrtgSnapshotHostedServiceTests|FullyQualifiedName~PrtgBackfillRunnerTests|FullyQualifiedName~PrtgStructureSync|FullyQualifiedName~PrtgResourceGuard|FullyQualifiedName~PrtgProbe|FullyQualifiedName~BatchRunStore|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 C：多窗口續跑——只補失敗與未處理

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 觸發 | 每窗口實例一次、不看成敗；`EvaluateScheduleAsync` 在 `IsRunning` 時整個 return | `ScheduleCalculator.cs:176-196`；`SchedulerHostedService.cs:218-244` |
| 停止 | 「不在任何窗口內」才停；相鄰窗口時前一趟跑進下一窗口；被停的趟是 `Stopped`、算已觸發；補跑趟看 `CatchUpDeadline`（`SchedulerRunState.cs:80`，唯一呼叫端 `SchedulerHostedService.cs:229`） | `ScheduleCalculator.cs:85,122-123` |
| AutoCatchUp | 只補「完全沒觸發」的窗口 | `ScheduleCalculator.cs:327-347` |
| NetIQ 續跑 | 排程走 `MissingDateFinder`：沒紀錄、或父紀錄狀態為 failed／running／partial／retained-incomplete 才重查；常態回望 1 天；空資料未截斷寫 success 後不再查；partial 無上限重查 | `HostDayPostProcessor.cs:30-39,147`；`NetiqOptions.cs:54`；`NetiqPipelineService.cs:697-740` |
| NetIQ 帳本 | 逐日 log blob（`netiq_hostday_attempt_<日期>`，append-only 列），不是資料表；狀態字串白名單 `KnownStatuses`，寫入未登錄值擲例外，讀到未登錄值當「無前次」 | `NetiqSourceAttempt.cs:7-9,19-20,30,66` |
| 本機續跑 | 回望 14 天、只看有無紀錄 | `AnalysisOrchestrator.cs:904-910` |
| PRTG 續跑 | 昨天每窗口重做規則評估（讀本地鏡像）；同步 `SourceSynced` 沿用 | `PrtgReevaluationPlanner.cs:20-123` |
| 里程碑 | 「第 n 個執行窗口」只在正常觸發帶；NetIQ 的「跳過 X」是主機數 | `SchedulerHostedService.cs:255-257`；`AnalysisOrchestrator.cs:1253` |

### 定案

1. **C-1 `ResumeWorkPlanner` 是組合器，不是第三套判定**：它只呼叫既有 `MissingDateFinder`（本機、NetIQ）與 `PrtgReevaluationPlanner`，彙整成 `ResumePlan { LocalDays, NetiqHostDays, PrtgDays, PrtgPendingDevices, HasWork }`，判定邏輯留在原處。改動只有三處既有判定的參數：
   - NetIQ：重查狀態（`NeedsRetry` 集合）的回望改為 `min(7, GetEffectiveBackfillDaysLimit(retention))` 天，**不受 `BackfillDays` 限制**；「沒紀錄的日」維持 `BackfillDays`（新主機不會第一天補 7 天）；主機 `EnabledAt` 之前的日子不補。
   - PRTG：`ReusableSyncFrom` 改為「全部 `SourceSynced` 為真且兩個待補清單皆為空或 `null`→整段沿用；有待補→沿用同步、只補待補裝置的 sensors／messages，再重評（`NeedsReevaluation` 已為真）」。
   - 本機：不變。
   - 手動立即執行不走清單（維持模式選單）。
2. **C-2 統一停止點 `StopAt`**：`SchedulerRunState.CatchUpDeadline` 改名 `StopAt`，排程趟填**觸發時窗口實例的 End**（跨午夜：Start>End 時 End 落在隔天），補跑趟填開跑＋窗口長度，手動趟 `null`；`ShouldStopScheduledRun(now, stopAt)` 收斂為 `stopAt is {} t && now >= t`，拿掉 `IsWithinAnyWindow` 分支。`BatchRun` 加 `WindowInstanceStart`（識別窗口實例，供 C-3 計數）與 `StopAt`。DTO 保留 `catchUpDeadline`（只在補跑趟有值）並新增 `stopAt`。`OccupiesScheduleWindow` 規則不變（補跑、手動佔住；排程趟不佔）。
3. **C-3 同窗口續跑決策表**（計數從既有排程觸發查詢點讀 `BatchRun.WindowInstanceStart`，重啟不歸零；不新增第二個查詢點）：

| 趟型別 | 結束原因 | 同窗口再觸發？ |
|---|---|---|
| 排程 | 完成或失敗（非被停）、`HasWork`、距 `StopAt` ≥ 10 分、該實例已跑趟數 < 2、距該實例上一趟 **開跑** ≥ 10 分 | 是（`Trigger=schedule`，里程碑「本趟為窗口內續跑（第 2 趟）」） |
| 排程 | 被 `StopAt` 停 | 否（等下一窗口） |
| 排程 | 第 2 趟任何結束 | 否 |
| 補跑 | 任何 | 否（窗口實例為 `null`） |
| 手動 | 任何 | 否；手動趟結束不觸發續跑計算 |

   - 計算時機：只在排程趟結束的狀態轉換那一個 Tick 算 `ResumePlan`（`SchedulerRunState` 加 `PendingResumeCheck` 旗標），不快取、不每 Tick 算。
   - 相鄰窗口：第一趟在 03:00 優雅停止期間 `IsRunning` 為真，第二窗口在它真正結束後的下一個 Tick 觸發（`WindowAlreadyTriggered` 對新實例為假），里程碑照寫「第 2 個執行窗口」。
4. **C-4 Sentinel 回空**：沒有舊紀錄、事件 0 筆、未截斷→寫入並標 `empty-once`，進重查清單一次；第二次仍空→`success`。狀態值與「要不要重查」收斂成單一來源 `NetiqAttemptStatus`（常數＋`NeedsRetry(status, attempts)`），`KnownStatuses`、`HostDayPostProcessor` 兩處、run-preview、`RunMonitorService` 五個呼叫點改讀它，守門測試掃字面值。畫面：`empty-once` 在總表與狀態卡顯示為「成功」色（不製造新噪音），tooltip「空資料，下一窗口複查一次」；run-preview 算入補跑。
5. **C-5 截斷重查上限**：`NeedsRetry` 的 `attempts` 參數＝該主機日在帳本的重查類回執數（既有 log 列計數，**不加欄位**）；partial／retained-incomplete 累計 ≥ 2 次→不再進排程清單（狀態不變，run-preview 手動補跑仍可選）。上限是「每個資料日總計 2 次」，不按執行日重置。
6. **C-6 里程碑與狀態卡**：趟首寫 `ResumeSummary`（結構化欄位：本機 N 日／NetIQ N 主機日／PRTG N 日、N 裝置）；趟末追加實際完成數；NetIQ 結束里程碑單位統一為主機日；執行紀錄列顯示摘要（`textContent`）。
7. **明確不做**：`AutoCatchUp` 語意不變；跨程序租約；本機資料不完整日不重跑（BACKLOG）。

### 改動

1. `ResumeWorkPlanner.cs`（新，Core）、`AnalysisOrchestrator.cs` 接點、`HostDayPostProcessor.cs`／`NetiqPipelineService.cs`（參數）、`NetiqAttemptStatus.cs`（新）、`NetiqSourceAttempt.cs`（讀計數、白名單改讀常數）。
2. `ScheduleCalculator.cs`、`SchedulerHostedService.cs`、`SchedulerRunState.cs`、`BatchRunStore.cs`（`StopAt`、`WindowInstanceStart`、`ResumeSummary`）。
3. `ScheduleController.cs`（status DTO `stopAt`、run-preview 對照）、`RunMonitorService.cs`、`runs.js`、`core/format.js`。
4. `PrtgReevaluationPlanner.cs`（`ReusableSyncFrom`）、`PrtgDailyPipeline.cs`（只補待補裝置）。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| C-1-1 | 父紀錄 failed／partial／retained-incomplete／empty-once 在 **預設模式**（`requireAi=false`）進清單；success 不進；7 天外不進；`EnabledAt` 前不進；沒紀錄的日仍受 `BackfillDays` | `MissingDateFinderRequireAiTests`（新案）、`ResumeWorkPlannerTests`（新） |
| C-1-2 | `ResumeWorkPlanner` 不含判定：以替身注入三個既有判定器，斷言輸出只是彙整 | `ResumeWorkPlannerTests` |
| C-1-3 | 待補裝置非空時沿用同步＋只對待補裝置發 sensors／messages 請求（URL 集合精確比對）＋重評；待補為空整段沿用 | `PrtgReevaluationPlannerTests`、`PrtgDailyPipelineTests` |
| C-2-1 | `ShouldStopScheduledRun(now, stopAt)`：排程趟到 End 停、相鄰窗口 03:00 停、跨午夜 End 落隔天、補跑照 deadline、手動不停 | `ScheduleCalculatorTests`、`ScheduleCatchUpTests` |
| C-2-2 | status DTO 同時帶 `catchUpDeadline`（補跑才有）與 `stopAt`；前端顯示讀 `stopAt` | `ScheduleGuardStatusCodeTests`、`RunsPageUiTests` |
| C-3-1 | 決策表每列一案（5 案）：「第 2 趟後不再觸發」「被停不觸發」「補跑不觸發」「手動不觸發」「距上一趟開跑 < 10 分不觸發」 | `SchedulerTickOrderTests`（新案） |
| C-3-2 | 站台重啟後計數沿用 DB（第 1 趟已跑→重啟→仍只允許 1 次續跑） | `SchedulerTriggerQueryTests` |
| C-3-3 | `ResumePlan` 只在趟結束的 Tick 算一次（替身計數） | `SchedulerTickOrderTests` |
| C-4-1 | 回空第一次→`empty-once`、第二次仍空→`success`、第二次有資料→正常寫入；帳本可寫入 `empty-once` | `NetiqPipelineBaselineTests`、`NetiqSourceAttemptPayloadTests` |
| C-4-2 | 五個呼叫點都讀 `NetiqAttemptStatus`：守門測試掃 `"partial"` 等字面值只出現在該檔 | `NetiqAttemptStatusGuardTests`（新） |
| C-4-3 | run-preview 把 `empty-once` 算入補跑；`RunMonitorService` 顯示為成功色 | `ScheduleRunPreviewNetiqAttemptTests`、`RunMonitorServiceTests` |
| C-5-1 | 同一資料日 partial 回執 2 次後不進清單；run-preview 仍列出 | `MissingDateFinderRequireAiTests`、`ScheduleRunPreviewNetiqAttemptTests` |
| C-6-1 | `ResumeSummary` 數字欄位與劇本一致；趟末完成數追加 | `ResumeRunIntegrationTests`（新） |
| C-整合 | 窗口 01:00–03:00／04:00–06:00；第一趟 NetIQ 10 台完成 6 台被停、PRTG sensors 停損剩 30 台→第二趟 NetIQ 只查 4 台主機日、PRTG 只對 30 台發請求、已同步裝置零請求；`ResumeSummary.NetiqHostDays==4`、`PrtgPendingDevices==30` | `ResumeRunIntegrationTests` |
| 停止 | 續跑等待／閘等待中按停止 1 秒內返回 | `SchedulerTickOrderTests`、B-1-4 |
| 突變 | NetIQ 清單改回受 `BackfillDays` 限制→C-整合紅；`empty-once` 移出 `NeedsRetry`→C-4-1 紅；停止點改回「不在任何窗口內」→C-2-1 相鄰案紅 | — |

定向：`--filter "FullyQualifiedName~ResumeWorkPlanner|FullyQualifiedName~ResumeRun|FullyQualifiedName~MissingDateFinder|FullyQualifiedName~NetiqPipelineBaselineTests|FullyQualifiedName~NetiqSourceAttempt|FullyQualifiedName~NetiqAttemptStatus|FullyQualifiedName~ScheduleCalculatorTests|FullyQualifiedName~ScheduleCatchUpTests|FullyQualifiedName~SchedulerTickOrderTests|FullyQualifiedName~SchedulerTriggerQueryTests|FullyQualifiedName~ScheduleRunPreview|FullyQualifiedName~ScheduleGuardStatusCodeTests|FullyQualifiedName~PrtgReevaluationPlannerTests|FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 E：排程頁與紀錄修正

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 站台版本辨識 | 匯出 `appVersion` 1.0.53.2 與 dev 相同，但 log 行為是 63-2 併入前的；版本號寫死在 `Directory.Build.props` | `SentinelClient.cs:193`／`PrtgClient.cs:185` 現為 Info；`AIService.cs:455,631`；`AiJsonDiagnostics.cs:216` |
| 停用主機列為失敗 | `AllHosts` 先濾 `Active` 再與近期趟 HostName 聯集；本機格狀態＝該日最新一趟**整趟**狀態；本機分析不看 `Active` | `RunMonitorService.cs:53-69,172,301-369`；`AnalysisOrchestrator.cs:292,672-682`；`BatchRunStore.cs:586-600` |
| 執行時間 | 第二行是整趟起訖聯集（含 value-fill）；展開列只認四個階段鍵；總表錨點寫死昨天 | `RunDayTimingBuilder.cs:14-17,36-82`；`run-timeline.js:114-169`；`RunMonitorService.cs:80-96` |
| AI 失敗計數 | `AiFailures` 只在 finally 覆寫；`RecordAiCall` 無呼叫者 | `AiAnalysisHostedService.cs:277-305`；`BatchRunRecorder.cs:337-344` |
| treesize 提醒 | 每台一行 console＋一筆 Warn | `PrtgTablePager.cs:283-299` |

### 定案

1. **E-1 建置識別**：`InformationalVersion` 帶 git 短 sha 與建置時間；發佈腳本以 `-p:SourceRevisionId` 傳入，建置環境無 git 且未傳入時寫 `local`，健康頁顯示「建置時未取得版本識別」。健康頁、`/api/health`、匯出 `meta.build` 顯示。
2. **E-2 停用主機**：
   - 總表只含 `Active` 主機；「未登記」的趟主機名才聯集。`TotalHosts`、失敗計數、失敗主機欄同步。停用主機不出現在總表（沒有格）。
   - 本機格狀態看本機階段（`Phases.local` 結果與 `LocalDaysFailed`）；舊紀錄無 `Phases`→退回整趟狀態並在格內 tooltip 標「升級前紀錄」（沿用 `run-timeline.js:155` 既有字樣）。匯出 `dailyHealth` 同口徑。
   - 本機分析尊重站台主機 `Active`：已登記且停用→跳過並寫里程碑；未登記→照跑；`LocalAnalysisEnabled` 優先。升級後首趟若因此跳過，健康頁提示一次（讀最近一趟里程碑）。
3. **E-3 今天列與四路耗時**：
   - 總表加今天列：今天已有取數趟才出現，列首「今日」，主機狀態欄規則與其他列相同（執行日 D 的趟、狀態對應 D-1 資料）；進行中的趟顯示「執行中」。匯出 `dailyHealth` 含今天、guide 文字同步。
   - DTO **新增**欄位 `localSeconds`／`netiqSeconds`／`prtgSeconds`／`aiSeconds`／`aiRunCount`／`runCount`，舊欄位 `fetchSeconds`／`followUpAiSeconds` 保留不改；前端第二行改讀新欄位「本機 X · NetIQ Y · PRTG Z · AI W（N 趟）」；PRTG 含 `prtg`、`prtg-sync`、`prtg-value-fill`、`prtg-triggered` 子階段聯集，展開列 PRTG 下列子階段。總計後加「共 N 趟」。
4. **E-4 AI 失敗計數**：每個主機日結束由 recorder 累加（`RecordAiCall` 有呼叫者），外層例外與中斷保留已累計值；週檢 AI 呼叫計入。舊紀錄不回溯；`aiFailureRate` 新舊混合時分母分子同源（測試含混合）。
5. **E-5 噪音警告**：treesize 提醒改為階段結束一行彙總（只進 console、不計 Warn）；`SentinelClient.cs:188-189` 註解校正。
6. **E-6 AI 關文法的額外欄位**：關閉結構化輸出的那次嘗試，不送 `_extraRequestFields` 中與結構化輸出同類的鍵（清單集中在 `AiJsonDiagnostics` 一處：`response_format`、`grammar`、`json_schema`、`guided_json`，暫定），診斷行記下移除的鍵。
7. **明確不做**：提示詞調整；多趟合併規則。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| E-1-1 | 健康 DTO 與匯出 `meta.build` 帶 sha＋時間；`local` 時旗標 `buildUnknown=true` | `HealthServiceTests`、`RunExportServiceTests` |
| E-2-1 | 13 台（12 NetIQ 啟用＋1 本機停用），唯一一趟 NetIQ 全 401、本機成功→成功 0／失敗 12、`TotalHosts` 12、失敗主機欄不含本機主機、總表無本機格 | `RunMonitorServiceTests` |
| E-2-2 | 本機主機啟用、同趟→本機格 Success；舊紀錄無 `Phases`→整趟狀態＋`legacy` 旗標 | `RunMonitorServiceTests`、`RunsTimelineBehaviorTests` |
| E-2-3 | 已登記且停用→本機分析零掃描、里程碑含「已停用」；未登記→照跑 | `AnalysisOrchestrator` 相關測試（`LocalAnalysis*`／新案） |
| E-3-1 | 今天有趟→今天列存在且 `isToday=true`；沒趟→不存在；匯出 `dailyHealth` 同 | `RunMonitorServiceTests`、`RunExportServiceTests` |
| E-3-2 | 含 1,748 秒 `prtg-value-fill` 的趟→`prtgSeconds ≥ 1748`、展開列子階段含 `prtg-value-fill`；舊欄位值不變 | `RunDayTimingBuilderTests`、`RunsTimelineBehaviorTests` |
| E-4-1 | 3 主機日、第 2 個降級、第 3 個擲例外→`AiCalls=3`、`AiFailures=2`；中斷趟保留已累計值 | `AiAnalysisSchedulerTests`、`BatchRunRecorder*Tests` |
| E-5-1 | 11 台 messages 皆少於 treesize→console 恰 1 行、`WarnCount` 不變 | `PrtgTablePagerTests`、`PrtgFetchServiceTests` |
| E-6-1 | 第二次嘗試關文法時請求 JSON 不含清單內鍵，其他額外欄位保留；診斷行列出移除鍵 | `AIServiceExtraRequestFieldsTests`、`AIServiceJsonDiagnosticsTests` |
| 突變 | 拿掉 Active 過濾→E-2-1 紅；子階段鍵退回只有 `prtg`→E-3-2 紅；E-4 改回 finally 覆寫→E-4-1 紅 | — |

定向：`--filter "FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunDayTimingBuilderTests|FullyQualifiedName~RunsTimeline|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~RunExport|FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~PrtgTablePagerTests|FullyQualifiedName~AIService|FullyQualifiedName~HealthService|FullyQualifiedName~LocalAnalysis|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 D：文件同步（隨各批次收尾）

- A：`docs/DB-SPEC.md`（`schema_fingerprint`、`LF_SCHEMA_FORCE_UPGRADE` 排障說明）、`docs/BACKLOG.md`（SQL Server 短路、計時工具用法）、`CLAUDE.md` 基線。
- B：`docs/PRTG-SPEC.md` §3（逐台階段閘與停損、待補裝置、messages 語意、pager 重試只在非逐台路徑）、WEB-SPEC §9.10 PRTG 卡兩列、`DB-SPEC` JSON 欄。
- C：WEB-SPEC §9.10「多窗口續跑」改寫（清單、`StopAt`、決策表、里程碑）、「NetIQ 取數嘗試與補跑」補 `empty-once` 與上限、`NETIQ-API-REFERENCE.md` 狀態表、說明書排程頁「多個時段怎麼接續」。
- E：WEB-SPEC §9.10 執行總表（今天列、四路、本機格來源、停用主機）、說明書兩處「不含今天」、`CLAUDE.md`「不要做」補「部署版本看建置 sha」。
- 每批次收尾：`CLAUDE.md` 測試基線數字。

## 升級／回退注意事項

- **升級**：SQLite 第一次啟動走完整升級並寫 `schema_fingerprint`；SQL Server 無變化。舊 `NeedsReevaluation=true` 的 PRTG 日第一趟仍整段重做。本機主機若已停用，升級後首趟起不再分析本機（健康頁提示一次）。`AiFailures` 新舊口徑不同，趨勢在升級日有斷層（不回溯）。
- **回退**：新 JSON 欄位對舊版無害（忽略）。`schema_fingerprint` blob 鍵對舊版無害（無全表枚舉，已 grep）。**`empty-once`**：舊版讀到會當「無前次回執」（`NetiqSourceAttempt.cs:66`），管線不會壞，但 run-preview 對含該狀態的日子回 409 `netiq_attempt_ledger_invalid` 直到那些日的帳本過保留期；回退前無法用 SQL 改（帳本是 log blob），文件寫明此影響。`StopAt`／`WindowInstanceStart` 舊版忽略，回退後回到「不在任何窗口內才停」。

## 明確不做（本輪定案）

- 不刪、不合併、不略過任何測試；不調整 xUnit 執行緒數。
- SQL Server 的 schema 短路；SQL Server 鎖名變更。
- `PrtgRequestBudget` 壓力冷卻（B-3）。
- `AutoCatchUp` 擴大成補「被停一半的窗口」。
- PRTG 重試參數開放設定頁。
- 跨程序排程租約。

## 複檢（第二版）

第一版複檢九條與兩輪獨立審查（使用者／管理者視角 25 條、架構視角 25 條）的處置摘要：

| 審查發現 | 處置 |
|---|---|
| A-1 鎖名變更波及 SQL Server、滾動升級互斥失效 | 只對 SQLite 分名，SQL Server 原名 |
| A-2 短路層級、拆段正確性、版本常數無守門 | 定案寫明位置；步驟歸屬表為前置交付；A-2-4／A-2-6 守門 |
| A 牆鐘不可客觀判定 | 改觀察值；驗收改代理指標 |
| A-4 移出集合無守門 | A-4-1 |
| B-1／pager／B-3 三層疊加；B-3 拖垮快照 | 刪 B-3；逐台階段關 pager 內建重試；閘是唯一退避 |
| 閘的併發語意（世代、名額） | B-1 定案與 B-1-2／B-1-3 |
| 停損冷卻旗標語意 | 寫明實例層、同趟後續階段略過、下一趟不受影響 |
| B-4 不標重評會讓該日「已評估」卻缺資料 | 待補非空⇒`NeedsReevaluation=true`、`partial`＋Note |
| 清除輸入集合 | B-2 完成集合＋B-2-4 |
| 欄位型別、`PendingStage` 放不下兩階段 | 兩個 `IReadOnlyList<long>?` |
| 停止鈕回應時限 | 共同規則＋B-1-4／C 停止案 |
| 既有入口影響表、可見性 | B-5、B-6 |
| C-1 第三套判定 | 退成組合器；C-1-2 守 |
| C-2 新狀態來源 | `StopAt` 統一 |
| C-3 狀態機、重啟、計數鍵、5 情境 | 決策表＋DB 計數＋C-3-1～3 |
| C-1 7 天與保留期、新主機負載 | `min(7, limit)`；沒紀錄日仍受 `BackfillDays`；`EnabledAt` 前不補 |
| C-5 計數鍵與帳本欄位 | 帳本列計數、不加欄位、每資料日總計 2 次 |
| C-4 UI、回退 | 成功色＋tooltip；回退影響寫明 |
| E-3 DTO 相容、「D-1」引用 | 新增欄位不改舊；文字改為「執行日 D 對應 D-1 資料」 |
| E-4 口徑斷層 | 不回溯，混合測試 |
| E-1 無 git 環境 | `SourceRevisionId`＋`local` 提示 |
| E-5 需要 pager 回傳計數 | 列入 B 改動 1 |
| D 基線累積 | 隨批次更新 |

複檢完成，無其他新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| | | | | |
