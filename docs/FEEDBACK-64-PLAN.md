# LogForesight 第 64 輪規劃

> 狀態：實作完成，待換模型體檢（第四版定案；實作時修正的部分見「執行紀錄」的落差欄）
> 基準：dev@6e056648（8,403 綠／11 略過；全量牆鐘 28 分 13 秒，每案加總 8,540 秒，8 核）
> 來源：使用者回饋三項——全量測試耗時、PRTG 取值重試不夠且不能打爆 PRTG、多排程窗口續跑未完成與失敗部分；追加三項——停用主機被列為失敗、執行時間顯示、站台診斷匯出（2026-11-05）檢視結果
> 實作方式：主代理寫階段規格，實作派內建 Agent（Sonnet 5.5，effort 由主代理依段評估；小型測試段可用 Haiku 5.5）；一個 checkout 一次只派一個改檔代理；每段主代理獨立重跑驗收

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| A | 全量測試提速（不刪、不略過任何測試） | 小 | 無 | 1 |
| E | 排程頁與紀錄修正（停用主機、今天列與四路耗時、AI 失敗計數、噪音警告、建置識別） | 中 | 無 | 2 |
| B | PRTG 取數重試：逐台階段共用暫停閘、停損、待補裝置清單 | 中 | 無 | 3 |
| C | 多窗口續跑：三路回報剩餘量、停止點、單窗口續跑、里程碑 | 大 | B-2 欄位 | 4 |
| D | 文件與說明同步 | 小 | 隨各批次收尾 | 隨批次 |

跨批次共同規則：
- 所有新增等待一律可注入時鐘／等待函式並接受 `CancellationToken`；測試不得真等。
- **停止鈕回應時限**：任何新增的等待在取消後 1 秒內返回，趟次標 `Stopped`；每批驗收都含「等待中按停止」的案例。
- 失敗後「下一次再試」的時機寫進執行輸出與狀態卡。
- 不新增沒有消費端的設定；新常數先寫在程式常數（暫定），不開設定頁。
- 新增 JSON 欄位一律加在 record 尾端並給預設值，舊 JSON 缺欄＝`null`（「舊紀錄，資訊未知」），消費端不得把 `null` 當空集合或 0；前端遇 `null` 顯示既有字樣「無階段資料（升級前紀錄）」。
- 每批次收尾同步更新對應現況文件並在執行紀錄寫「本批新增 N 案」；`CLAUDE.md` 測試基線數字只在最後一批收尾時改一次。
- 驗收一律指定測試類別（不存在的標「（新）」）；斷言比對結構化欄位，不比整段文字；里程碑與畫面文字只比關鍵片語。
- 每批次「改動」節附「畫面文字清單」，供 `RunsPageUiTests` 等比對關鍵片語。

---

## 批次 A：全量測試提速

### 現況與核對結果

| 項目 | 數字 | 證據 |
|---|---|---|
| 牆鐘 | 28 分 13 秒（測試本體 27 分 20 秒） | trx |
| 每案加總 | 8,540 秒；等效並行 5.2 倍 | trx |
| `new StorageBackend(` 的測試類 | 82 類、1,342 案、加總 6,445 秒＝**75%** | trx × 原始碼比對 |
| `StorageBackend` 建構成本（修正前） | 新 SQLite 檔 2,740 ms；既有且已升級的 DB 仍 1,100～1,340 ms；`EfSqliteFixture`（記憶體）34 ms | 臨時探針 |
| **根因（已確認）** | SQLite 連線池被刻意關閉（`StorageBackend.cs:103` `DisableSqlitePoolingIfUnset`），`SchemaUpgrader.Upgrade` 上百個「檢查缺什麼」的 `SqlQueryRaw`／`ExecuteSqlRaw` **每個都重開一條連線**，每次開啟又經 `SqlitePragmaInterceptor` 重跑 PRAGMA（`cache_size`、`mmap_size=1 GB`、`journal_mode`…）。`Upgrade` 本體沒有交易（`:817` 的 `BeginTransaction` 在背景方法 `RekeyIssueFirstSeenToSourceKeys`），所有 `SqlQueryRaw` 都 `.ToList()` 實體化。 | `StorageBackend.cs:103,351`；`SqlitePragmaInterceptor.cs:13,28,35`；`SchemaUpgrader.cs:797-817` |
| **修正後（探針實測）** | `Upgrade` 期間維持一條開啟的連線：既有 DB 建構 **1,100 ms → 55～110 ms**，新 DB **2,740 ms → 150～200 ms**；`NetiqPermissionChangePostProcessorTests`（80 案）單獨跑 **97 秒 → 17 秒**；相關定向 247 案全綠。修正只涵蓋 `Upgrade`，`EnsureCreated` 自己開關一次連線不在範圍內（數字已含它）。 | 探針程式已收回，diff 存 scratchpad `feedback-64-batchA-probe.patch` |
| 建構子互斥 | `StorageBackend.cs:144` 取 `Global\LogForesight-Schema`（SQLite 與 SQL Server 共用常數）；所有測試即使各用自己的暫存 DB 也排成單線；取不到鎖 20 秒後仍執行（`:152-157`） | `StorageBackend.cs:139-161,183` |
| 序列集合 | `KnownIssueCatalogState` 14 類（8 類呼叫 `Initialize`、6 類純讀）；**集合的存在理由就是保護讀者不被寫者干擾**，讀者移出會把干擾帶回來；7 個含 `Initialize(` 呼叫的檔目前都已掛集合 | `KnownIssueCatalogStateCollection.cs:5-11`；grep |
| 真時鐘配額 | 四案 `new PrtgRequestBudget()` 用 `RealPrtgClock`，各等 60～180 秒；改注入加速時鐘後皆 < 2 秒（探針實測） | `PrtgFetchServiceTests.cs:144`、`PrtgBackfillRunnerTests.cs:88`、`PrtgBackfillRangeRunnerTests.cs:51` |
| 其他 | 無 `xunit.runner.json`；node 子行程約 300 次、真實 Kestrel／HttpListener 約 80 案——都在分鐘級以下 | 結構掃描 |

### 定案

第二版的「schema 雜湊短路」整套移除（根因不是升級器做太多事，而是每個檢查重開連線）。

1. **A-1 互斥鎖只在 SQLite 分名**：SQLite 連線的鎖名＝原常數＋資料檔**絕對路徑大小寫折疊後**（`ToUpperInvariant`，Windows 路徑不分大小寫）的 SHA-256 前 16 碼；記憶體 DB（`:memory:` 或 `Mode=Memory`）以連線字串本身雜湊；解析或正規化擲任何例外→**退回原常數**（寧可多互斥，不可失去互斥）。**SQL Server 鎖名維持原常數不變**。雜湊只用於組名稱，不寫 log。
2. **A-2 `SchemaUpgrader.Upgrade` 期間維持單一連線**（SQLite 專屬）：進入時連線未開啟才 `OpenConnection`，`finally` 只關自己開的；呼叫端已開啟（含 `:memory:` 由呼叫端持有連線的情境）不另開也不關。PRAGMA 只在這一次開啟套用一次即可（`journal_mode` 持久於檔案，其餘為逐連線狀態，升級器沒有任何步驟依賴「每次新連線」）。升級語意、步驟順序、資料步驟完全不變；SQL Server 路徑不動。跨行程殘餘風險（取不到鎖 20 秒後仍執行、另一行程等 `busy_timeout` 5 秒）與修正前相同，由 A-2-4 補測。
3. **A-3 測試端**：四個真時鐘配額測試改注入共用 `TestDoubles/AcceleratedPrtgClock`（取代 `PrtgFetchServiceTests` 內的私有類別），它累加 `TotalRequestedDelay`；驗收改結構斷言不守牆鐘。接受「配額真時鐘失去直接覆蓋」：配額邏輯以 `IPrtgClock` 抽象，`PrtgRequestBudgetTests` 已用假時鐘覆蓋窗口規則。不新增 `xunit.runner.json`、不調執行緒數。
4. **A-4 序列集合：不拆，只加守門**：`KnownIssueCatalogState` 成員不動；守門測試掃原始碼含 `KnownIssueCatalog.Initialize(`／`SecurityAuditWatchlist.Initialize(` 的測試檔必須帶該 `[Collection]`（只抓直接呼叫，經 `RuleBootstrapper` 間接的不在內，註解承認）。守門新建即綠，突變才是驗證。
5. **A-5 計時工具**：trx 解析腳本進 `LogForesight.Tests/tools/test-timing.py`（只讀本機 trx）；牆鐘數字只做觀察值（同機、無負載、連跑 2 次取中位數），**不當驗收門檻**。
6. **明確不做**：不刪、不合併、不 `Skip` 測試；不拆 `DisableParallelization` 集合；schema 雜湊短路（任一 provider）；SQL Server 鎖名變更。

### 改動

1. `StorageBackend.cs`：`BuildSchemaMutexName(isSqlite, connectionString)`（internal static）、建構子兩個分支各算一次、gate 改用它。
2. `SchemaUpgrader.cs`：`Upgrade` 拆成「開連線／try／finally 關」外殼＋`UpgradeCore`（原內容原封搬入）。
3. 測試：`StorageBackendMutexNameTests`（新）、`SchemaUpgraderConnectionTests`（新）、`TestCollectionGuardTests`（新）、`TestDoubles/AcceleratedPrtgClock.cs`（新）、四處 `new PrtgRequestBudget()` 改注入。
4. `LogForesight.Tests/tools/test-timing.py`（新）。
5. 畫面文字：無。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| A-1-1 | 同一 SQLite 檔以相對／絕對／大小寫異寫三種路徑建構，鎖名相同、以原常數為前綴、不含路徑明文；兩個不同檔鎖名不同；`:memory:` 與 `Mode=Memory;Cache=Shared` 鎖名不同；`file:` URI 與無法解析的字串退回原常數；SQL Server 兩種寫法都等於原常數 | `StorageBackendMutexNameTests`（新） |
| A-2-1 | 既有且已升級的 SQLite DB 再 `Upgrade` 一次：`DbConnectionInterceptor` 計數恰 1，結束後連線 Closed；**突變**（拿掉 `OpenConnection`）→次數變上百 | `SchemaUpgraderConnectionTests`（新） |
| A-2-2 | 經 `new StorageBackend(settings, dir, commandInterceptor)` 真路徑：`PRAGMA cache_size` 命令次數＝`EnsureCreated` 1 次＋`Upgrade` 1 次（修正前為上百次） | `SchemaUpgraderConnectionTests` |
| A-2-3 | 呼叫端已開啟連線時 `Upgrade` 不另開、不關閉；`Upgrade` 中途擲例外時仍關閉自己開的連線 | `SchemaUpgraderConnectionTests` |
| A-2-4 | 兩個 `StorageBackend` 對同一檔並行建構（WAL 與 DELETE 各一案）皆成功、無 `SQLITE_BUSY` | `SchemaUpgraderConnectionTests` |
| A-2-5 | 既有 `SchemaUpgrader*Tests`、`SchemaUpgraderModelParityTests`、`EfPermissionChangeStoreTests` 冪等案全綠 | 既有（77＋） |
| A-3-1 | 四案：`AcceleratedPrtgClock.TotalRequestedDelay ≥ 60 秒`（配額邏輯真的走過），`PrtgFetchServiceTests` 不再有私有 `AcceleratedClock` | 各原類別 |
| A-4-1 | 守門測試；突變（拿掉一個檔的集合屬性）必紅 | `TestCollectionGuardTests`（新） |
| A-觀察 | 全量牆鐘與加總寫進執行紀錄；8,403 綠／11 略過＋新增案數 | 執行紀錄 |

---

## 批次 E：排程頁與紀錄修正

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 站台版本辨識 | 匯出 `appVersion` 1.0.53.2 與 dev 相同，但 log 行為是 63-2 併入前的；版本號寫死在 `Directory.Build.props:8`；repo **沒有任何發佈腳本**，只有 `README.md:780` 的 `dotnet publish -c Release`；`HealthService.cs:50-52` 註解寫「Version＋commit」但沒有 commit；`/api/health` liveness 是 `[AllowAnonymous]`（`HealthController.cs:71`） | 同左 |
| 停用主機列為失敗 | `AllHosts` 先濾 `Active` 再與近期趟 HostName 聯集（同一份名單用於所有日期）；本機格狀態＝該日最新一趟**整趟**狀態；本機分析一律跑 `Environment.MachineName`，`Touch` 不改 `Active`；`ScheduleController.cs:414-425` 的 scope 判斷只擋 `LocalAnalysisEnabled` 不看 `Active`；`HostAdminService.cs:291` 「本機」篩選只列 `Active` | `RunMonitorService.cs:53-69,172,301-369`；`AnalysisOrchestrator.cs:292,402-404,672-682`；`BatchRunStore.cs:586-600` |
| 執行時間 | 第二行是整趟起訖聯集（含 value-fill）；展開列只認四個階段鍵；`fetchSeconds`／`followUpAiSeconds` 消費端只有 `run-timeline.js:118-119`；總表錨點寫死昨天、`TotalDays`／`TotalPages` 以 `days` 計（`RunsController.cs:50-62`）；「不含今天」寫在 `WEB-SPEC.md:2863,3031`、`Runs.cshtml:221`、`RunMonitorService.cs:81`、說明書兩處、匯出 guide `RunExportService.cs:1361` | `RunDayTimingBuilder.cs:14-17,36-82`；`run-timeline.js:114-169`；`RunMonitorService.cs:80-96` |
| AI 失敗計數 | `AiFailures` 只在 finally 覆寫；`RecordAiCall` 無呼叫者；消費端 `RunMonitorService`、`RunExportService`、`RunExportDtos`、`RunDtos` | `AiAnalysisHostedService.cs:277-305`；`BatchRunRecorder.cs:337-344` |
| treesize 提醒 | 每台一行 console＋一筆 Warn；`PrtgPagerResult` 是 record，加欄位不影響呼叫端 | `PrtgTablePager.cs:40,283-299` |
| 主機格 DTO | `RunDayHostStatusDto` 只有 `Status` 等欄，沒有備註 | `RunDtos.cs:53-65` |

### 定案

1. **E-1 建置識別**：`Directory.Build.props` 加 MSBuild target：`Exec git rev-parse --short HEAD`（`ContinueOnError`），成功→`InformationalVersion = <Version>+<sha>.<yyyyMMddTHHmm>`，失敗→`<Version>+local`。不依賴發佈腳本；README 部署章節補一行說明。顯示：健康頁（已登入）的版本列、匯出 `meta.build`（`{ "sha": "abc1234"|null, "builtAt": ..., "unknown": bool }`）；**匿名 liveness 不帶 sha**。`meta.build` 加進 `RunExportDeidentifier` 的允許字面清單。
2. **E-2 停用主機**：
   - 總表主機清單＝`Active` 主機 ∪ **本頁日期窗口內有趟或有分析紀錄的主機**（停用前的歷史列保留，停用後沒有新趟就自然消失）。`TotalHosts`、失敗計數、失敗主機欄同口徑。
   - 本機格狀態看本機階段（`Phases.local` 結果與 `LocalDaysFailed`）；舊紀錄無 `Phases`→退回整趟狀態，`RunDayHostStatusDto` 加 `Legacy` 旗標，前端 tooltip 沿用「無階段資料（升級前紀錄）」。匯出 `dailyHealth` 同口徑。
   - 本機**日誌分析**尊重站台主機 `Active`：已登記且停用→跳過並寫里程碑「本機主機已停用，略過本機日誌分析」；未登記→照跑；`LocalAnalysisEnabled` 優先。**體檢、權限檢查、保留清除、派工照跑**（它們不是主機日誌分析）。`ScheduleController` 的 scope／run-preview 判斷對齊同一條規則（停用時 `IncludesLocal=false`，`scope=host` 指到停用的本機回既有「本機分析已停用」類錯誤碼）。升級後首趟若因此跳過，健康頁版本列旁顯示一次性提示「站台主機已停用，不再分析本機日誌」（以最近一趟里程碑判定，無需關閉狀態）。
3. **E-3 今天列與四路耗時**：
   - 今天列是**額外附加列**：只在今天已有取數趟、且 `page==1` 時出現在最上方，`isToday=true`，**不佔 `days`、`TotalDays` 不含它**；`GetRecentRuns` 窗口多取一天。今天列的「未執行」顯示灰字「尚未處理」且不計入失敗、`inProgress=true` 時列首「今日（執行中）」。匯出 `dailyHealth` 最後附今天列（`isToday`、`inProgress`），guide 改為「dailyHealth 可能含今天（isToday），其 notRun 代表尚未處理不是失敗」。
   - DTO **替換**：移除 `fetchSeconds`／`followUpAiSeconds`，新增 `localSeconds`／`netiqSeconds`／`prtgSeconds`／`aiSeconds`／`aiRunCount`／`runCount`（舊趟無 `Phases` 時四路為 `null`）；前端第二行「本機 X · NetIQ Y · PRTG Z · AI W（N 趟）」，任一為 `null` 顯示「無階段資料（升級前紀錄）」；PRTG 含 `prtg`、`prtg-sync`、`prtg-value-fill`、`prtg-triggered` 子階段聯集，展開列 PRTG 下列子階段；總計後加「共 N 趟」。
4. **E-4 AI 失敗計數**：每個主機日結束由 recorder 累加（`RecordAiCall` 有呼叫者），外層例外與中斷保留已累計值；週檢 AI 呼叫計入。舊紀錄不回溯；`aiFailureRate` 新舊混合測試。
5. **E-5 噪音警告**：`PrtgPagerResult` 加 `TreeSizeShort` 旗標（預設 false），messages 那段改回傳旗標、移除逐台 console 與 `Log.Warn`；`PrtgFetchService` messages 階段結束印一行彙總「N 台裝置的 messages 讀取少於 treesize（僅提醒，帶時間篩選時語意未證實）」，只進 console、不計 Warn。`SentinelClient.cs:188-189` 註解校正。**不依賴批次 B**。
6. **E-6 AI 關文法的額外欄位**：關閉結構化輸出的那次嘗試，不送 `_extraRequestFields` 中與結構化輸出同類的鍵（清單集中在 `AiJsonDiagnostics` 一處：`response_format`、`grammar`、`json_schema`、`guided_json`，暫定），診斷行記下移除的鍵。
7. **明確不做**：提示詞調整；多趟合併規則；liveness 帶 sha。

### 改動

1. `Directory.Build.props`、`README.md` 部署章節、`HealthService.cs`／健康頁 view、`RunExportService.cs`（`meta.build`）、`RunExportDeidentifier.cs`。
2. `RunMonitorService.cs`（名單、本機格、今天列）、`RunsController.cs`（`TotalDays` 不含今天）、`RunDayTimingBuilder.cs`、`RunDtos.cs`、`run-timeline.js`、`runs.js`、`Views/Pages/Runs.cshtml:221` popover、`RunExportService.cs`（`dailyHealth`、guide）。
3. `AnalysisOrchestrator.cs`（本機 Active 判定與里程碑）、`ScheduleController.cs:414-425`（scope 對齊）、`HealthService.cs`（一次性提示）。
4. `AiAnalysisHostedService.cs`、`BatchRunRecorder.cs`、`WeeklyCheckupService`、`RunMonitorService`／`RunExportService`／`RunExportDtos`／`RunDtos`（口徑）。
5. `PrtgTablePager.cs`、`PrtgFetchService.cs`（彙總行）、`SentinelClient.cs` 註解。
6. `AIService.cs`、`AiJsonDiagnostics.cs`。
7. 畫面文字：「本機主機已停用，略過本機日誌分析」、「站台主機已停用，不再分析本機日誌」、「今日」、「今日（執行中）」、「尚未處理」、「本機 X · NetIQ Y · PRTG Z · AI W（N 趟）」、「共 N 趟」、「無階段資料（升級前紀錄）」、「建置時未取得版本識別」。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| E-1-1 | `InformationalVersion` 解析：有 sha、`local`、格式異常三案；健康 DTO 與匯出 `meta.build` 一致；liveness 回應不含 sha；`meta.build` 不被去識別化改寫 | `HealthAndPerformanceTests`、`RunExportServiceTests`、`RunExportDeidentifierTests` |
| E-2-1 | 13 台（12 NetIQ 啟用＋1 本機停用、窗口內無趟）：唯一一趟 NetIQ 全 401、本機成功→成功 0／失敗 12、`TotalHosts` 12、失敗主機欄不含本機、總表無本機格 | `RunMonitorServiceTests` |
| E-2-2 | 停用主機在窗口內有歷史趟→仍有格、狀態依本機階段；本機主機啟用、同趟→本機格 Success；舊紀錄無 `Phases`→整趟狀態＋`Legacy=true` | `RunMonitorServiceTests`、`RunsTimelineBehaviorTests` |
| E-2-3 | 已登記且停用→本機日誌分析零掃描、里程碑含「已停用」、體檢與權限檢查仍執行；未登記→照跑；`scope=host` 指停用本機→既有錯誤碼 | `AnalysisOrchestrator` 本機測試（既有類＋新案）、`ScheduleController` 測試 |
| E-3-1 | `days=14`、今天有趟、`page=1`→`items=15`、首列 `isToday`、`TotalDays=14`；`page=2` 無今天列；今天無趟→14 列；今天列 `notRun` 不計 `FailedCount`；匯出 `dailyHealth` 同 | `RunMonitorServiceTests`、`RunsControllerTests`（既有或新）、`RunExportServiceTests` |
| E-3-2 | 含 1,748 秒 `prtg-value-fill` 的趟→`prtgSeconds ≥ 1748`、`localSeconds`／`netiqSeconds`／`aiSeconds` 各等於階段聯集、`runCount` 正確；舊趟無 `Phases`→四路 `null`、前端顯示「無階段資料」 | `RunDayTimingBuilderTests`、`RunsTimelineBehaviorTests` |
| E-4-1 | 3 主機日、第 2 個降級、第 3 個擲例外→`AiCalls=3`、`AiFailures=2`；中斷趟保留已累計值；新舊混合 `aiFailureRate` 分母分子同源 | `AiAnalysisSchedulerTests`、`BatchRunRecorder*Tests`、`RunExportServiceTests` |
| E-5-1 | 11 台 messages 皆少於 treesize→`TreeSizeShort` 11 次、console 恰 1 行、`WarnCount` 不變 | `PrtgTablePagerTests`、`PrtgFetchServiceTests` |
| E-6-1 | 關文法那次請求不含清單內鍵、其他額外欄位保留；診斷行列出移除鍵 | `AIServiceExtraRequestFieldsTests`、`AIServiceJsonDiagnosticsTests` |
| 突變 | 拿掉 Active 過濾→E-2-1 紅；子階段鍵退回只有 `prtg`→E-3-2 紅；E-4 改回 finally 覆寫→E-4-1 紅；今天列計入 `TotalDays`→E-3-1 紅 | — |

定向：`--filter "FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunsController|FullyQualifiedName~RunDayTimingBuilderTests|FullyQualifiedName~RunsTimeline|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~RunExport|FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~PrtgTablePagerTests|FullyQualifiedName~PrtgFetchServiceTests|FullyQualifiedName~AIService|FullyQualifiedName~HealthAndPerformance|FullyQualifiedName~ScheduleController|FullyQualifiedName~LocalAnalysis|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 B：PRTG 取數重試

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 分頁重試 | retry 只在 `FetchTablePagedAsync`（`:1743-1746`）由 `_retryTransientPaging` 決定；`RunPagedStageAsync`（`:1707`）是 static、只包未收斂例外，不知道 retry；正式只有夜間管線、回填、結構同步傳 true，快照與探測逐台路徑本來就無重試 | `PrtgFetchService.cs:1707,1743-1746`；`PrtgDailyPipeline.cs:166`；`PrtgBackfillService.cs:853`；`PrtgStructureSyncService.cs:397` |
| 逐台派發結構 | `semaphore.WaitAsync` 在派發迴圈（spawn 前）；sensors 非 staged 路徑在 `onBatch` 內逐頁 `UpsertSensors` 並 `targets.AddRange`（整台成功前就累計） | `PrtgFetchService.cs:616-628,640-655,825-828` |
| 數值取數 | 共用 `PauseGate`（15／60／180 秒＋jitter、`Retry-After`）、補抓輪、連續 10 顆停損、總暫停 15 分停損、冷卻 10 分；`RecordSuccess` 由 `PrtgFetchService.cs:1204` 在 `lock(run.Sync)` 內呼叫，政策看不到閘 | `PrtgValueFetchRetry.cs:16-32,90-160`；`PrtgFetchService.cs:1025-1303` |
| 逐台 sensors／messages 失敗 | 同第三版 | `PrtgFetchService.cs:318-339,459-462,959` |
| 進度訊號 | `IRunProgress.Report(phase, done, total)` 只有三個整數，訊號類 done／total 恆 0；`SchedulerRunState.ReportProgress` 靠名稱顯式分派（`GuardPaused` `:426`、`PrtgFindingsReady` `:434`），`prtg-` 前綴分支在後；暫停顯示是取數卡的 `PausedReason` 單一字串 | `RunPhases.cs:12`；`SchedulerRunState.cs:240,395-440`；`runs.js:1337-1346` |
| PRTG 卡閒置列 | 已有「待補評估 N 天」與「上一趟」兩列（`#prtg-pending-reeval-row`、`#prtg-last-issue-row`） | `Runs.cshtml:144-148` |

### 定案

1. **B-1 逐台階段共用暫停閘（唯一退避層）**：
   - `FetchTablePagedAsync` 加參數 `allowPagerRetry`（預設 true）；逐台 sensors（`:732`）與逐台 messages（`:836`）傳 false，全站 devices／全站 sensors 維持。`RunPagedStageAsync` 不動、不複製。
   - 每個逐台階段一個 `PauseGate`；**`await gate.WaitAsync(ct)` 放在派發迴圈內、`semaphore.WaitAsync` 之前**（不持有名額空等）。任一台可重試失敗（`PrtgTablePager.IsRetryable`）→拉閘；失敗台進「重排佇列」，派發迴圈跑完第一遍後再跑第二遍佇列（不在 worker 內遞迴）；第二遍仍失敗→待補。
   - **每台結果整台成功才合併**：sensors 逐台改為每台先收進區域結果，整台成功才 `UpsertSensors`＋`targets.AddRange`，重排不會重複累計。
   - 暫停序列、jitter、`Retry-After`、連續失敗與總暫停停損**複用** `PrtgValueFetchRetryPolicy`；「連續」以完成順序計（與數值路徑同一函式）。
   - **世代號只加在 `PauseGate`**（放行時遞增），呼叫點「送出前讀世代、成功後比對一致才 `RecordSuccess`」，政策不動。數值路徑 `:1204` 同樣加比對——**這是刻意的行為變更**（慢成功不再歸零），B-5 表同步標註。
2. **B-2 停損與待補清單**：同第三版（`PendingSensorDevices`／`PendingMessageDevices` 兩個 `IReadOnlyList<long>?`、待補非空⇒`SourceSynced=true`＋`NeedsReevaluation=true`＋`Outcome=partial`＋Note、Warn 不 Error、實例層冷卻讓同趟後續階段略過、清除只針對完成同步的裝置）。
3. **B-3 配額壓力冷卻：不做**（理由同第三版）。
4. **B-4 messages 單台失敗語意與 sensors 一致**（同第三版）。
5. **B-5 既有入口影響表**：

| 入口 | 影響 |
|---|---|
| 夜間管線 `FetchDayAsync` | 變（B-1／B-2／B-4） |
| 手動立即執行的 PRTG 路徑、手動「同步結構與對應」 | 同管線，變 |
| 歷史回填：狀態變更區間（逐台 messages） | 變（同 B-1） |
| 歷史回填與觸發式的數值路徑 | **只變一處**：慢成功不再把暫停序列歸零（世代比對） |
| 快照／守門／探測／估算／連線測試 | 不變（本來就無 pager 重試、不經閘） |
| run-preview 補跑預覽 | 不變 |

6. **B-6 可見性**：
   - `RunPhases` 加 `PrtgPaused`／`PrtgResumed` 常數並列入 `All`；`SchedulerRunState.ReportProgress` 加**顯式分支且排在 `prtg-` 前綴分支之前**；狀態新增 `PrtgPausedCount`（int）與 `PrtgResumeAt`（絕對時間，前端自行倒數，避免 3 秒輪詢凍結），放行時清空。DTO 加 `prtgPausedCount`、`prtgResumeAt`（`null`＝未暫停）。
   - 落點：PRTG 卡進度軌文字 `#schedule-prtg-progress-text`——暫停時「PRTG 回應異常，暫停中（第 N 次，約 X 秒後重試）」，放行後回到原進度文字；停損後本趟顯示「PRTG 已停止本趟取數，N 台待補」。**不新增列**。
   - 閒置時把待補裝置併進既有「待補評估」列文字：「N 天待補（含 M 台裝置）」；DTO `prtgPendingDays` 旁加 `prtgPendingDevices`（`null`＝未知不顯示）。「立即執行回望 N 天」提示沿用（手動趟走同一個 `ReusableSyncFrom`，會只補待補裝置）。
   - 每次拉閘在執行輸出寫一行 Info（原因、第 N 次、等幾秒）。
7. **升級說明**：同第三版。

### 改動

1. `PrtgFetchService.cs`：`FetchTablePagedAsync(allowPagerRetry)`、逐台兩階段的閘與重排佇列、整台成功才合併、停損、待補清單、清除範圍、世代比對（含 `:1204`）。
2. `PrtgValueFetchRetry.cs`：`PauseGate.Generation`；政策命名中性化（保留舊名別名）。
3. `BatchRunStore.cs`：`PrtgDayStat` 兩個待補欄位。
4. `PrtgDailyPipeline.cs`：統計寫入、`NeedsReevaluation`、Note、`PrtgPaused`／`PrtgResumed` 進度事件。
5. `RunPhases.cs`、`SchedulerRunState.cs`、`ScheduleDtos.cs`、`SchedulerHostedService.PrtgRunHealth`、`runs.js`、`Runs.cshtml`（待補評估列文字）。
6. `RunExportService.cs`：`unsuccessfulPrtgDays` 帶待補數。
7. 畫面文字：「PRTG 回應異常，暫停中（第 N 次，約 X 秒後重試）」、「PRTG 已停止本趟取數，N 台待補」、「N 天待補（含 M 台裝置）」、逐日 Note「N 台待補（下一個窗口續補）」。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| B-1-1 | 300 台、第 5 台起全回 503、併發 4、假時鐘：閘拉起後直到放行 HTTP 請求數不增加；第二遍重排失敗台；請求總數＝第一遍送出數＋重排數（精確值） | `PrtgFetchServiceTests` |
| B-1-2 | 世代比對：閘拉起前發出、拉起後才成功的請求不歸零；放行後發出的成功歸零（逐台與數值路徑各一案） | `PrtgValueFetchRetryPolicyTests`、`PrtgFetchServiceTests` |
| B-1-3 | 閘等待期間 semaphore 名額為空（替身斷言） | `PrtgFetchServiceTests` |
| B-1-4 | 閘等待中取消：1 秒內返回、趟次 `Stopped`、已完成裝置的鏡像保留 | `PrtgFetchServiceTests`、`PrtgDailyPipelineTests` |
| B-1-5 | 逐台階段單台 503 只送 1 次後交給閘（pager 內建重試關閉）；全站 devices 仍有 2 次 | `PrtgFetchServiceTests` |
| B-1-6 | 某台第 3 頁失敗、重排後成功→該台 `targets` 無重複、`written` 精確 | `PrtgFetchServiceTests` |
| B-2-1 | 連續 10 台失敗→停損：剩餘裝置零請求；`PendingSensorDevices`＝失敗＋未送出集合 | `PrtgFetchServiceTests` |
| B-2-2 | 停損後同趟 messages 階段與觸發式取數零請求、全部記待補 | `PrtgDailyPipelineTests` |
| B-2-3 | 待補非空→`SourceSynced=true`、`NeedsReevaluation=true`、`Outcome=partial`、趟次 Warning | `PrtgDailyPipelineTests`、`PrtgReevaluationPlannerTests` |
| B-2-4 | 清除只刪已完成裝置的過期感測器；中途取消時待補裝置一顆不刪 | `PrtgFetchServiceTests` |
| B-2-5 | 舊 JSON 缺欄→兩欄位 `null`；消費端把 `null` 當未知 | `PrtgDayStatSerializationTests`（新）、`PrtgReevaluationPlannerTests` |
| B-4-1 | messages 單台失敗→不標 `HistoryAffectingFailure`、記 `PendingMessageDevices` | `PrtgFetchServiceTests` |
| B-5-1 | 快照、守門、探測測試類別全綠 | `PrtgSnapshotHostedServiceTests`、`PrtgResourceGuard*`、`PrtgProbe*` |
| B-6-1 | `PrtgPaused` 事件進顯式分支不污染 PRTG 進度軌；DTO `prtgPausedCount`／`prtgResumeAt` 隨拉閘／放行變化；進度文字與待補評估列文字由 DTO 驅動 | `SchedulerRunSnapshotTests`／`SchedulerRunStateTests`、`RunsPageUiTests`、`RunsTimelineBehaviorTests` |
| 突變 | 拿掉閘 `WaitAsync`→B-1-1 紅；待補只記失敗不記未送出→B-2-1 紅；清除退回全有全無→B-2-4 紅；世代比對拿掉→B-1-2 紅 | — |

定向：`--filter "FullyQualifiedName~PrtgFetchServiceTests|FullyQualifiedName~PrtgValueFetchRetry|FullyQualifiedName~PrtgTablePagerTests|FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~PrtgDayStat|FullyQualifiedName~PrtgReevaluationPlannerTests|FullyQualifiedName~PrtgSnapshotHostedServiceTests|FullyQualifiedName~PrtgBackfillRunnerTests|FullyQualifiedName~PrtgStructureSync|FullyQualifiedName~PrtgResourceGuard|FullyQualifiedName~PrtgProbe|FullyQualifiedName~SchedulerRun|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 C：多窗口續跑——只補失敗與未處理

### 現況與核對結果

| 項目 | 結果 | 證據 |
|---|---|---|
| 觸發 | 每窗口實例一次、不看成敗；`EvaluateScheduleAsync` 在 `IsRunning` 時整個 return | `ScheduleCalculator.cs:176-196`；`SchedulerHostedService.cs:218-244` |
| 停止 | 「不在任何窗口內」才停；`CatchUpDeadline` 散在 `SchedulerRunState`、`SchedulerHostedService`、`RunRequest`（`AnalysisOrchestrator.cs:33`）、`ScheduleDtos.cs:92`、`runs.js:1349` 共 22 處；`IsWithinAnyWindow` 另被 AI 排程使用（`AiAnalysisHostedService.cs:158,200`）；`BatchRun` 只持久化 `CatchUp` 旗標，`CatchUpDeadline`／`CatchUpNote` 只在記憶體 | `ScheduleCalculator.cs:85,122-123`；`BatchRunStore.cs:103` |
| 補跑與中斷 | `AutoCatchUp` 只補「完全沒觸發」的窗口；`isCatchUp = CatchUpNote != null` 會擴大回望（`AnalysisOrchestrator.cs:316,907,1223`）並令 `BatchRun.CatchUp=true`（匯出 `CatchUpRuns`）；異常中斷的第一趟不計入已觸發、再補一次帶 `CatchUpNote` | `ScheduleCalculator.cs:253,327-347`；`RunExportService.cs:913,990` |
| NetIQ 續跑 | 排程走 `MissingDateFinder(lookbackDays)`：沒紀錄、或父紀錄狀態在重查集合才重查；`ReadRecent` 以同一個 lookback 為範圍；常態回望 1 天；批次查詢失敗不寫紀錄→隔天就落在回望外永遠不補；空資料未截斷寫 success；partial 無上限 | `HostDayPostProcessor.cs:21-39,147`；`NetiqOptions.cs:54`；`NetiqPipelineService.cs:497-541,697-740` |
| NetIQ 帳本 | 逐日 log blob（`netiq_hostday_attempt_<日期>`），寫入未登錄狀態擲例外、讀到未登錄當「無前次」；狀態字面值散在 `NetiqPipelineService`（10 處）、`HostDayWorkflowState`（3 處）、`HostDayPostProcessor`（2）、`ScheduleController:250`、`RunMonitorService:266-286`；父紀錄狀態下游還有 `RecordStorageShaper`、`RecordDetailQueryService`、`RecordDtos`、郵件 | `NetiqSourceAttempt.cs:19-20,30,66,105` |
| 三路目標判定 | NetIQ 每台主機各呼叫一次 `MissingDateFinder`（`NetiqPipelineService.cs:362-370`）、PRTG 在 `ResolvePrtgDays`（`AnalysisOrchestrator.cs:1423-1445`）、本機在 `:904-910`——趟內本來就各自算 | 同左 |
| 總表 NetIQ 格 | `NetiqStatus` 把 partial／retained-incomplete 一律回 Warning，`_ => Warning`；`NetiqCell` 只填 `Status`，無備註 | `RunMonitorService.cs:266-292` |

### 定案（第四版）

第三版的 `ResumeWorkPlanner` 組合器與「趟末 Tick 再算一次」**刪除**：三路在趟內本來就各自算目標，再算一次是重複，且在 Tick 執行緒對全部主機重讀。

1. **C-1 三路回報剩餘量，`ResumeSummary` 持久化**：各路結束時把「本路剩餘／失敗／未處理」交給 `BatchRunRecorder`（本機：失敗日數；NetIQ：失敗＋未查主機日數；PRTG：待補裝置數與待補日數），`BatchRun.ResumeSummary { LocalDays, NetiqHostDays, PrtgDays, PrtgDevices, HasWork }` 隨趟次持久化；趟首里程碑寫各路「本趟要補」（既有 by-product：缺漏日數、`ResolvePrtgDays` 結果、NetIQ 目標主機日數），趟末寫實際完成與剩餘。續跑決策只讀上一趟的 `ResumeSummary.HasWork`（DB），不重算。
   - NetIQ 重查範圍：`NeedsRetry` 判定的主機日回望 `min(7, GetEffectiveBackfillDaysLimit(retention))` 天，來源＝父紀錄狀態 ∪ **帳本回執**（7 天份逐日 blob 各讀一次，不逐主機）；`ReadRecent` 範圍取 `max(7, BackfillDays)` 只在有重查狀態時放大；「從未嘗試過的日子」維持 `BackfillDays`；主機 `EnabledAt` 之前不補。手動立即執行不走這條。
   - PRTG：`ReusableSyncFrom` 改為「全部 `SourceSynced` 且兩個待補清單皆為空或 `null`→整段沿用；有待補→沿用同步、只補待補裝置再重評」。本機不變。
2. **C-2 統一停止點 `StopAt`**：`CatchUpDeadline` 全部改名 `StopAt`（`SchedulerRunState`、`SchedulerHostedService`、`RunRequest`、`ScheduleDtos`、`runs.js`，22 處一次到位，DTO 不保留舊名）；排程趟填觸發時窗口實例的 End（跨午夜落隔天）、補跑趟填開跑＋窗口長度、手動 `null`；`ShouldStopScheduledRun(now, stopAt)` 收斂為 `now >= stopAt`；`IsWithinAnyWindow` 保留給 AI 排程；`ScheduleCatchUpTests.cs:47-50` 四案改寫。`BatchRun` 加 `WindowInstanceStart`、`StopAt`；`CatchUp` 語意不變（只有 AutoCatchUp 趟為 true）。狀態卡文字改「本趟到 HH:mm 停止」（補跑趟前綴「補跑：」）。
3. **C-3 同窗口續跑決策表**（計數＝既有排程觸發查詢點讀 `BatchRun.WindowInstanceStart` 的趟數，重啟不歸零）：

| 趟型別 | 結束原因 | 同窗口再觸發？ |
|---|---|---|
| 排程 | 完成或失敗（非被停）、上一趟 `ResumeSummary.HasWork`、距 `StopAt` ≥ 10 分、該實例已跑趟數 < 2、距上一趟**開跑** ≥ 10 分 | 是：`Trigger=schedule`、**`WindowNote`**「本趟為窗口內續跑（第 2 趟，上一趟 #runId：結束原因）」、**`CatchUpNote=null`、`CatchUp=false`**（不擴大回望） |
| 排程 | 上一趟有 PRTG 停損 | 另加「距上一趟結束 ≥ 10 分」（避開實例層冷卻剛失效就重打） |
| 排程 | 被 `StopAt` 停 | 否 |
| 排程 | 第 2 趟任何結束 | 否 |
| 異常中斷 | 既有規則補一次（帶 `CatchUpNote`） | 該補跑趟計入實例趟數；之後仍受 < 2 上限 |
| 補跑（AutoCatchUp） | 任何 | 否 |
| 手動 | 任何 | 否 |

   - 判定時機：Tick 在 `!IsRunning` 時走既有觸發查詢，多讀最近一趟的 `ResumeSummary` 與 `WindowInstanceStart`；沒有旗標、沒有快取。
   - 相鄰窗口：第一趟優雅停止期間 `IsRunning` 為真，第二窗口在它結束後的下一 Tick 觸發。
   - 狀態卡 `triggerText` 附「（窗口內續跑第 2 趟，上一趟：被窗口結束停止／失敗／PRTG 停損）」。
4. **C-4 Sentinel 回空（只動帳本）**：沒有舊紀錄、事件 0 筆、未截斷→**不寫父紀錄**，只寫帳本回執 `empty-once`；下一窗口因「沒紀錄＋帳本有重查狀態」再查一次；第二次仍空→寫 success 父紀錄；第二次有資料→正常寫入。`empty-once` 只進 `NetiqSourceAttempt.KnownStatuses`、`NeedsRetry`、run-preview 清單與 `RunMonitorService` 狀態對照——**不進 `LatestNetiqAttemptStatus`，不波及記錄頁與郵件**。狀態值與「要不要重查」收斂成 `NetiqAttemptStatus`（常數＋`NeedsRetry(status, attempts)`），守門只守判定處（`KnownStatuses` 與 `NeedsRetry` 唯一）。
5. **C-5 自動重查上限（所有重查狀態一致）**：`failed`／`stopped`／`partial`／`retained-incomplete`／`empty-once` 的主機日，**自動重查最多 2 次**（以帳本該主機日的重查類回執數計，不加欄位），達上限→不再進排程清單、狀態不變；run-preview 手動補跑不受限。
6. **C-6 畫面**：`RunDayHostStatusDto` 加 `StatusNote`（`null`＝無）：`empty-once`→「空資料，下一窗口複查一次」（狀態顯示「未執行」色）；達上限→「已放棄自動重查，請手動補跑」（維持 Warning 色）；`NetiqStatus` 補明確 case、不再靠 `_ => Warning`。執行紀錄列顯示 `ResumeSummary` 摘要（`textContent`）；NetIQ 結束里程碑單位統一為主機日。
7. **明確不做**：`AutoCatchUp` 語意不變；跨程序租約；本機資料不完整日不重跑（BACKLOG）；`empty-once` 進父紀錄。

### 改動

1. `BatchRunRecorder.cs`／`BatchRunStore.cs`（`ResumeSummary`、`WindowInstanceStart`、`StopAt`）、`AnalysisOrchestrator.cs`（三路回報、里程碑、`RunRequest.StopAt`）、`NetiqPipelineService.cs`（回報、回空路徑）、`HostDayPostProcessor.cs`（`NeedsRetry` 參數、帳本來源）、`NetiqAttemptStatus.cs`（新）、`NetiqSourceAttempt.cs`（白名單改讀常數、回執計數）。
2. `ScheduleCalculator.cs`、`SchedulerHostedService.cs`、`SchedulerRunState.cs`、`ScheduleDtos.cs`、`ScheduleController.cs`（run-preview 清單、`stopAt`）、`runs.js`。
3. `RunMonitorService.cs`（`NetiqStatus`、`StatusNote`）、`RunDtos.cs`、`runs.js:368` 徽章 tooltip、`core/format.js`。
4. `PrtgReevaluationPlanner.cs`、`PrtgDailyPipeline.cs`。
5. 畫面文字：「本趟到 HH:mm 停止」、「補跑：本趟到 HH:mm 停止」、「本趟為窗口內續跑（第 2 趟，上一趟 #N：…）」、「續跑：本機 N 日／NetIQ N 主機日／PRTG N 日、N 裝置」、「空資料，下一窗口複查一次」、「已放棄自動重查，請手動補跑」。

### 驗收

| 編號 | 驗收 | 測試類別 |
|---|---|---|
| C-1-1 | 預設模式下 failed／stopped／partial／retained-incomplete（父紀錄）與帳本 `empty-once`／`failed`（無父紀錄）在 7 天內進清單；success 不進；7 天外不進；`EnabledAt` 前不進；`BackfillDays=1` 時 7 天內沒嘗試過的日子不進 | `MissingDateFinderRequireAiTests`（新案） |
| C-1-2 | 三路回報：本機 2 日失敗、NetIQ 4 主機日剩餘、PRTG 30 台待補→`ResumeSummary` 各欄精確、`HasWork=true`；全部完成→`HasWork=false` | `BatchRunRecorderPhaseTests`、`NetiqPipelineBaselineTests`、`PrtgDailyPipelineTests` |
| C-1-3 | 待補裝置非空→沿用同步、只對待補裝置發請求（URL 集合精確比對）、重評；待補空→整段沿用 | `PrtgReevaluationPlannerTests`、`PrtgDailyPipelineTests` |
| C-2-1 | `ShouldStopScheduledRun(now, stopAt)`：到 End 停、相鄰窗口 03:00 停、跨午夜 End 落隔天、補跑照 deadline、手動不停；AI 排程 `IsWithinAnyWindow` 不受影響 | `ScheduleCalculatorTests`、`ScheduleCatchUpTests`、`AiAnalysisSchedulerTests` |
| C-2-2 | DTO 只有 `stopAt`（無 `catchUpDeadline`）；前端兩種文字 | `ScheduleGuardStatusCodeTests`、`RunsPageUiTests` |
| C-3-1 | 決策表每列一案（7 案），含「異常中斷補跑計入趟數」「PRTG 停損後距結束 < 10 分不觸發」 | `SchedulerTickOrderTests`（新案） |
| C-3-2 | 續跑趟 `CatchUp=false`、`CatchUpNote=null`、回望不擴大；站台重啟後計數沿用 DB | `SchedulerTriggerQueryTests`、`AnalysisOrchestrator` 測試 |
| C-4-1 | 回空第一次→無父紀錄、帳本 `empty-once`；第二次仍空→success 父紀錄；第二次有資料→正常；父紀錄 `LatestNetiqAttemptStatus` 永不為 `empty-once` | `NetiqPipelineBaselineTests`、`NetiqSourceAttemptPayloadTests` |
| C-4-2 | 守門：`"partial"` 等字面值的**重查判定**只在 `NetiqAttemptStatus`；`KnownStatuses` 讀常數 | `NetiqAttemptStatusGuardTests`（新） |
| C-4-3 | run-preview 把 `empty-once` 算入補跑；總表該格「未執行」色＋`StatusNote` | `ScheduleRunPreviewNetiqAttemptTests`、`RunMonitorServiceTests` |
| C-5-1 | 同一主機日重查類回執 2 次後不進清單；格子 `StatusNote`「已放棄自動重查」；run-preview 仍列出 | `MissingDateFinderRequireAiTests`、`RunMonitorServiceTests`、`ScheduleRunPreviewNetiqAttemptTests` |
| C-整合 | 窗口 01:00–03:00／04:00–06:00；第一趟 NetIQ 10 台完成 6 台被停、PRTG sensors 停損剩 30 台→上一趟 `ResumeSummary.HasWork`；第二趟 NetIQ 只查 4 台主機日、PRTG 只對 30 台發請求、已同步裝置零請求；里程碑含「續跑」 | `ResumeRunIntegrationTests`（新） |
| 停止 | 閘等待中與續跑趟中按停止 1 秒內返回 | `SchedulerTickOrderTests`、B-1-4 |
| 突變 | NetIQ 重查改回受 `BackfillDays` 限制→C-1-1 紅；`empty-once` 移出 `NeedsRetry`→C-4-1 紅；停止點改回「不在任何窗口內」→C-2-1 相鄰案紅；續跑帶 `CatchUpNote`→C-3-2 紅 | — |

定向：`--filter "FullyQualifiedName~ResumeRun|FullyQualifiedName~MissingDateFinder|FullyQualifiedName~NetiqPipelineBaselineTests|FullyQualifiedName~NetiqSourceAttempt|FullyQualifiedName~NetiqAttemptStatus|FullyQualifiedName~ScheduleCalculatorTests|FullyQualifiedName~ScheduleCatchUpTests|FullyQualifiedName~SchedulerTickOrderTests|FullyQualifiedName~SchedulerTriggerQueryTests|FullyQualifiedName~ScheduleRunPreview|FullyQualifiedName~ScheduleGuardStatusCodeTests|FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~PrtgReevaluationPlannerTests|FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunsPageUiTests|FullyQualifiedName~BehaviorTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~ApiTimeout"`。

---

## 批次 D：文件同步（隨各批次收尾）

- A：`docs/DB-SPEC.md`（升級器單一連線一行）、`docs/BACKLOG.md`（計時工具用法）。
- E：WEB-SPEC §9.10 執行總表（今天列、四路、本機格、停用主機名單規則）；`WEB-SPEC.md:2863,3031`、`Runs.cshtml:221`、`RunMonitorService.cs:81`、說明書兩處、匯出 guide 的「不含今天」改寫；`README.md` 部署章節（建置識別）；`CLAUDE.md`「不要做」補「部署版本看建置識別，`local` 代表未取得」。
- B：`docs/PRTG-SPEC.md` §3（逐台階段閘與停損、待補裝置、messages 語意、pager 重試只在非逐台路徑、數值路徑世代比對）、WEB-SPEC §9.10 PRTG 卡文字、`DB-SPEC` JSON 欄。
- C：WEB-SPEC §9.10「多窗口續跑」改寫（三路回報、`StopAt`、決策表、里程碑、`StatusNote`）、「NetIQ 取數嘗試與補跑」補 `empty-once` 與上限、`NETIQ-API-REFERENCE.md` 狀態表、說明書排程頁「多個時段怎麼接續」。
- 最後一批收尾：`CLAUDE.md` 測試基線與牆鐘。

## 升級／回退注意事項

- **升級**：批次 A 不改任何資料與 schema 語意；SQL Server 無變化。舊 `NeedsReevaluation=true` 的 PRTG 日第一趟仍整段重做。本機主機若已停用，升級後首趟起不再分析本機日誌（健康頁提示）。`AiFailures` 新舊口徑不同，趨勢在升級日有斷層（不回溯）。
- **回退**：新 JSON 欄位（`PendingSensorDevices`、`PendingMessageDevices`、`ResumeSummary`、`WindowInstanceStart`、`StopAt`）對舊版無害；`BatchRun` 不移除任何舊欄位、`CatchUp` 語意不變、續跑趟 `CatchUp=false`。**`empty-once`** 只在帳本，但舊版對未知狀態是擲例外（體檢實測，原「當無前次回執」的描述錯誤）：回退後含該狀態的主機日 `RecordBatch` 擲例外→該 Sentinel 整台失敗、轄下剩餘批次不查；`ReadLatest` 擲例外→執行總表該頁 NetIQ 格全 Warning、run-preview 回 409，直到那些日的帳本過保留期（帳本是 log blob，無法用 SQL 改）。**要回退請先清掉含 `empty-once` 的帳本行，或不要回退。**回退後停止點回到「不在任何窗口內」。

## 明確不做（本輪定案）

- 不刪、不合併、不略過任何測試；不調整 xUnit 執行緒數；不拆 `DisableParallelization` 集合。
- schema 雜湊短路（任一 provider）；SQL Server 鎖名變更。
- `PrtgRequestBudget` 壓力冷卻（B-3）。
- `ResumeWorkPlanner` 組合器與趟末重算。
- `AutoCatchUp` 擴大成補「被停一半的窗口」。
- PRTG 重試參數開放設定頁；跨程序排程租約；`empty-once` 進父紀錄；liveness 帶 sha。

## 複檢

### 第二版處置（摘要，保留供追溯）

A-1 只對 SQLite 分名；A 牆鐘改觀察值；B-3 刪除、閘為唯一退避；B-4 待補⇒`NeedsReevaluation`；欄位型別兩個 `IReadOnlyList<long>?`；停止鈕回應時限；C-2 `StopAt`；C-5 帳本列計數；E-3 DTO；E-4 不回溯；E-1 無 git 環境；E-5 pager 回傳。

### 第三版處置

A-2 雜湊短路整套移除（根因＝每個檢查重開連線）；A-4 不拆集合只守門。

### 第四版處置（第二輪雙視角審查 37 條）

| 審查發現 | 處置 |
|---|---|
| A-1 大小寫未折疊、`file:` URI、fallback 不該退成連線字串雜湊 | 折疊、例外退回原常數、三案補測 |
| A-2 `:memory:` 關連線、中途例外、跨行程鎖、真路徑計數、PRAGMA 理由 | 只關自己開的；A-2-2～A-2-4 |
| A-3-1 Stopwatch 偶發紅 | 改 `TotalRequestedDelay` 結構斷言 |
| B-1 重排重複累計、閘位置、`allowPagerRetry`、世代落點、數值路徑行為變更 | 整台成功才合併；派發迴圈內；單一參數；世代只在 `PauseGate`；B-5 標註 |
| B-6 進度訊號只有三個整數、分派順序、落點、秒數凍結、與待補評估列重複 | 新常數＋顯式分支＋`PrtgPausedCount`／`PrtgResumeAt`；進度軌文字；併進既有列 |
| C-1／C-3 組合器與趟末重算重複、Tick 全主機重讀 | 刪除，改三路回報＋持久化 `HasWork` |
| C-1 7 天 `ReadRecent` 放大、失敗無紀錄日落在回望外 | 範圍取 max 只在有重查狀態時；帳本回執納入來源 |
| C-4 字面值十幾處、`empty-once` 波及父紀錄下游 | 只進帳本；守門只守判定處 |
| C-5 `failed` 無上限 | 所有重查狀態一致 2 次 |
| C-3 續跑誤走 `CatchUpNote`、異常中斷疊加、PRTG 停損後立即重打 | `WindowNote`、決策表加兩列 |
| C-2 消費端漏列、`IsWithinAnyWindow` 不可刪 | 22 處一次到位；AI 排程保留 |
| C 續跑原因看不見、放棄重查靜默、`empty-once` tooltip 無落點 | `triggerText` 附原因；`StatusNote` |
| E-1 發佈腳本不存在、匿名 liveness、去識別化 | MSBuild target；liveness 不帶；允許清單 |
| E-2 歷史格消失、消費端表、體檢等是否跳過、scope 不一致 | 名單＝Active ∪ 窗口內有趟；只跳過日誌分析；`ScheduleController` 對齊 |
| E-3 分頁數學、今天列 notRun 誤讀、guide、四處「不含今天」、舊欄位可刪 | 附加列不佔 days；「尚未處理」；替換欄位 |
| E 缺改動清單；E-5 與 B-1 綁定矛盾 | 補；解綁 |
| 測試類別不存在（`BatchRunStoreTests`、`HealthServiceTests`） | 改為 `PrtgDayStatSerializationTests`（新）、`HealthAndPerformanceTests` |
| D 基線每批改同一行 | 只在最後一批改 |

複檢完成，無其他新增事項。

## 體檢交接

- **分支**：`feature/feedback-64`，已推送；尚未併 dev。
- **全量**：自我檢查修正後實跑 8,496 通過／0 失敗／11 略過，6 分 51 秒。
- **自我檢查**：換模型前已自查一輪，修正與確認不是問題的項目見執行紀錄「換模型前自我檢查」列，體檢不必重走。
- **實際沒驗到的**：沒有連真實 PRTG、Sentinel 或 SQL Server；畫面文字只有字串與行為測試，沒有開瀏覽器看過排程作業頁的今日列、四路耗時、PRTG 暫停文字與設定頁版本列。
- **體檢請優先看**：
  1. 訊號通道：`PrtgFetchService.Signal` 只有夜間管線接，回填與結構同步不顯示暫停（刻意）。
  2. `RunDevicesWithPauseAsync`：閘、重排、停損與 `FetchSensorsForDevicesAsync` 的 `requireCompleteDevice`（快照路徑）是否都維持原行為。
  3. `ApplyResumePolicy` 與 `empty-once`：`failed` 在回望窗內但達每日上限時不查，當天不會再試；隔天歸零。
  4. 本機格改看本機階段後，本機 Security log 讀取失敗的 Warn 不再讓格子變黃（只留在執行詳情），是否可接受。
  5. E-3 的 `InProgress` 用 `_isHeld` 判定，異常中斷的趟不算執行中。
- **升級提醒**：站台版本要看 `版本+sha`（設定 > 系統健康），舊版本號分不出來；11-03 起的憑證問題已於 11-05 重新輸入。

## 體檢輪修正（Fable 5.1，2026-11-05）

實作與自查：Opus 5.5。體檢：三個唯讀 Sonnet 代理依角色分區（整趟排程流程／資料生命週期／架構並行前端測試）各附探針，共回報 25 條；主代理逐條讀路徑後修 13 條、文件 4 處、BACKLOG 3 項，其餘為可接受或不成立。每條修正附迴歸測試，B-1、B-4、A-1、A-2、C-2 另做突變驗證。

| 編號 | 哪裡 | 症狀 | 修法 | 迴歸測試 |
|---|---|---|---|---|
| B-1（高） | `NetiqPipelineService.ReadAttemptHistoryForResume` | 回望天數設超過 7 天時，第 8 天以前的主機日回 0 筆寫 `empty-once` 後，下一趟讀不到帳本、再寫一次 `empty-once`，每晚重查、永不定案也永不寫父紀錄（代理探針連跑三趟重現） | 帳本讀取範圍改為 `max(常態回望, 7)` | `NetiqPipelineBaselineTests.排程續跑_回望超過7天_窗外的回空複查也會定案` |
| B-4／A-4（中） | `PrtgDailyPipeline` 取消時的補位統計 | 沿用同步＋續補進行中被窗口到點取消，補位統計寫 `SourceSynced=false`、待補 null，下一窗口整段重同步、待補清單也丟 | 補位統計沿用 `reuseSyncFrom` 與原待補清單（續補已回傳剩餘時用剩餘） | `PrtgDailyPipelineTests.沿用同步的續補被窗口結束取消_統計仍標已同步並保留待補清單` |
| A-1（中） | `PrtgFetchService.IsPermanentFailure` | 分頁確定性不收斂（回應形狀不對、頁數或鏡像保護上限）的裝置既不可重試也不算永久失敗，永遠留在待補：每個窗口多跑一趟、那幾天每晚重評 | 不收斂與鏡像保護上限也算永久失敗（仍計失敗、不進待補） | `PrtgFetchServiceTests.逐台_分頁確定性不收斂的裝置不進待補_仍計失敗` |
| A-2（中） | `ScheduleCalculator.RunToContinue` | 數值取數停損只留下失敗顆數、沒有待補裝置，續跑不套用「距結束 10 分」，剛停損的 PRTG 下個 Tick 就被再打 | 待補裝置或失敗數值任一非零都從結束起算；`RunResume.PrtgHalted` 別名移除（C-6） | `ScheduleCalculatorTests.續跑_間隔從上一趟開跑算_不是從結束算` |
| C-1（中） | `PrtgReevaluationPlanner.PendingDeviceCount` | 狀態卡只取第一個有欄位的那一天、執行紀錄「仍待補」取跨天聯集，兩個數字對不上 | 兩處共用 `PendingDeviceUnion` | `PrtgReevaluationPlannerTests.PendingDeviceCount_取最新一趟的聯集_舊紀錄為null` |
| C-2（中） | `ScheduleCalculatorTests` | 「距上一趟開跑滿 10 分」改成「距結束」沒有案例會紅 | 補能區分兩種算法的案例 | 同 A-2 |
| B-5（中） | `AnalysisOrchestrator` NetIQ 階段說明與里程碑 | 一趟只有回 0 筆待複查時寫「無待補」，里程碑與匯出看不到這些主機日 | 階段說明與里程碑補「回 0 筆待複查 N」 | 既有 `NetiqPipelineBaselineTests` 回空案（`EmptyDeferred`） |
| B-2（中） | PLAN 回退注意事項 | 寫「舊版讀到 `empty-once` 當無前次回執、管線不壞」，實測舊版是擲例外：該 Sentinel 整台失敗、總表該頁全 Warning、run-preview 409 | 改寫回退段，要回退先清帳本行 | — |
| B-3（低） | `NetiqSourceAttempt.Receipt.AfterPrevious` | 同一趟跨午夜時計數被歸零；同趟先失敗後二分重查成截斷時截斷漏計 | 計數日跟著該趟第一筆；同趟由非截斷轉截斷補計一次 | `NetiqPipelineBaselineTests.同一趟的回執_跨午夜不歸零_先失敗後截斷補計截斷` |
| B-6（低） | `NetiqPipelineService` 帳密未設定路徑 | 與「密碼解不開」同類卻沒補記回執 | 同路徑補 `RecordUnattempted` | `NetiqPipelineBaselineTests.排程續跑_Sentinel帳密未設定_主機日留失敗回執` |
| A-9（低） | `SchedulerHostedService.DescribeWindowRun` | 跨午夜窗口 00:30 觸發看不到前一晚那趟留下什麼 | 下界取今天 0 點與本窗口實例起點較早者 | `SchedulerTriggerQueryTests` 跨午夜案 |
| C-5（低） | `run-timeline.js dayTimingDetailText` | 全是舊紀錄且算不出總耗時時，展開列寫「無階段資料」、第二行卻空白 | 判定移出 `totalSeconds` 條件 | 既有行為測試 |
| C-3／C-7／B-7（低） | 守門註解、`SchemaUpgrader.cs` BOM | 守門只守 `is "x" or` 一種形狀但註解沒講；檔案被加上 BOM | 註解寫明範圍；還原無 BOM | — |

**可接受、不修**：A-5 續跑只看上一趟排程趟（規劃定案）；A-8 停止點在觸發當下凍結（文件已註明）；A-10 停損文字蓋住後續進度（數字正確）；B-7／C-8 守門範圍窄（註解已寫明）。**進 BACKLOG**：A-3 跨午夜不沿用、A-6／A-7 續補不更新同步狀態與範圍。**代理主張不成立**：區 B 問題 6（續補 messages 區間）前提不成立；A-6 的「SyncedAt 誤清」經代理自己查無此路徑。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A（2026-11-05） | 主代理 | 全部完成。全量牆鐘 28 分 13 秒 → **8 分 10 秒**，每案加總 8,540 秒 → 2,015 秒（Windows 8 核，同機） | A-1-1、A-2-1～A-2-5、A-3-1、A-4-1 皆有測試；`OpenConnection` 突變使 A-2-1、A-2-2 紅 | ①A-2-2 改數「升級檢查用過幾個 sqlite3 handle」（PRAGMA 由攔截器直接下，`DbCommandInterceptor` 看不到）。②A-3-1 斷言改 `TotalRequestedDelay > 0`（加速時鐘下實際要求的等待會被真實流逝抵銷，≥60 秒會偶發紅）。③計畫外：`PrtgRequestBudget.Shared` 加 internal setter，測試組件載入時換加速時鐘，並把測試內 `new PrtgRequestBudget()` 全換掉（否則全量並行下彼此排隊到逾時）。④計畫外（提前做 B-1 世代號）：全量抓到真缺陷，暫停結束後才回來的舊請求失敗被算成新一波，已修並有確定性測試與突變。⑤兩案測試本身的並行問題修正：共用記憶體連線、Kestrel 連線重設。 |
| E（2026-11-05） | 主代理 | 全部完成 | E-1-1、E-2-1～E-2-3、E-3-1、E-3-2、E-5-1、E-6-1 有測試；Active 過濾、子階段鍵突變皆紅 | ①E-1 簡化：SDK 本來就把 sha 寫進 InformationalVersion，只是匯出與執行紀錄讀不含 sha 的組件版本，改為 `BuildVersion` 單一解析點，不加 MSBuild target、不加 `meta.build`（`appVersion` 本身帶 sha）。②E-2 名單細化：不用「窗口內有趟」（每趟都以站台機器名登記，條件恆真），改「停用主機當天有本機階段才算入」。③E-3 不新增四路秒數欄位，第二行直接讀既有 `Branches`（DRY），只加 `SubPhases`。④E-4 無程式改動：finally 含例外路徑、降級計數 63-2 已修，站台的 0 是舊版；既有 `AiAnalysisSchedulerTests` 已涵蓋。⑤E-6 移除的鍵寫 Info log，不進診斷行。⑥一個提交（70940e13）未以測試為條件就推上去，下一個提交（c2d10fdc）修正，之後一律條件式提交。 |
| B（2026-11-05） | 主代理 | 全部完成 | B-1-1～B-1-5、B-2-1～B-2-5、B-4-1、B-6-1 有測試；拿掉閘（兩處）、未送出不記待補、清除不保留待補、拿掉訊號分支四個突變皆紅 | ①B-1-3 沒有獨立案例：`IsRaised` 重檢與「閘前不佔名額」已由拿掉閘的突變涵蓋。②暫停訊號不做 `prtg-resumed`：放行時刻已是絕對時間，過了就不再顯示，少一個訊號。③閘與停損判定抽成 `PauseSession` 兩路共用。④`B-1-1` 用 30 台而非 300 台，上界由「4 成功＋10 失敗＝14 個請求」精確斷言。 |
| C（2026-11-05） | 主代理 | 全部完成 | C-1-1、C-1-3、C-2-1、C-3-1、C-4-1、C-4-2、C-4-3、C-5-1、C 整合有測試；拿掉續跑判定、漏帶 WindowEnd 突變皆紅；empty-once 移出重查的突變原本存活，補預覽案例後涵蓋 | 全部是實作時發現規劃不成立而改的：①**C-2 不改名**：`OccupiesScheduleWindow` 靠 `CatchUpDeadline != null` 判斷補跑，統一成 `StopAt` 會把排程趟也判成補跑；改為新增 `WindowEnd`，停止點取兩者其一。②**C-1／C-6 不新增 `ResumeSummary`**：`RunResume.Of` 直接從該趟已持久化的結果算，少一個欄位也少一處要同步。③**C-5 計數**：帳本每個主機日只留一列（覆寫），「數帳本列」不成立；累計欄位改隨回執寫入，舊資料缺欄為 0。④**C-5 上限語意**：第四版的「每資料日總計 2 次」會讓 Sentinel 中斷一晚的主機日永遠補不回來（正是 11-03～11-05 的實況），改為失敗類每天 2 趟隔天歸零、截斷類累計 2 趟。⑤`EnabledAt` 不存在，改以「帳本有嘗試過才補舊日」達成同一目的。⑥手動趟不沿用同步，待補裝置續補只發生在排程趟。 |
| 收官全量（2026-11-05） | 主代理 | 8,478 通過／2 失敗／11 略過，牆鐘 8 分 14 秒 | 兩案修正後定向全綠 | ①**真缺陷**：暫停訊號走進度回呼，回填進度條會跳成「1/15」、結構同步會顯示裸訊號；定向只跑到管線才沒抓到。改為獨立 `Signal` 通道，只有夜間管線接，補案例守住。②今天列是規劃內變更，舊測試斷言同步更新。 |
| 規劃比對（2026-11-05） | 主代理 | 逐條比對定案與程式碼，補齊遺漏 | 見各案 | 補：趟首里程碑版本帶 sha；A-3-1 另三案結構斷言；匯出今天列測試與 `unsuccessfulPrtgDays` 待補數；續補仍有待補時 `Outcome=partial`（真缺陷）；狀態卡 `windowNote`、執行紀錄「仍待補」；預設模式狀態重查測試；`PRTG-SPEC` 重評條件、`BACKLOG` 本機項目。**規劃外**：永久失敗（4xx，401／403／429 除外）不進待補，否則每晚重評、待補評估永遠掛著。**有意偏離**：C-3-1 決策表測在 `ScheduleCalculator`，Tick 只驗 `ComposeContinueRequest`（Tick 測試架構的相依是 null，觸發整趟不成立）；不做 `meta.build`（`appVersion` 本身帶 sha）；E-4 無程式改動，`RecordAiCall` 無正式呼叫者但有測試，留給體檢決定。 |
| 規劃比對突變（2026-11-05） | 主代理 | 永久失敗不進待補、待補讓當日標 partial 兩個突變皆紅 | 定向通過 | 後者原本存活：測試環境是激進策略，連不到 PRTG 的失敗顆數也會讓結果變 partial，斷言驗不到待補本身。案例改保守策略後，拿掉待補判定就紅。 |
| 換模型前自我檢查（2026-11-05） | 主代理 | 各角度走路徑與全 diff 複查，修 8 條 | 定向通過；窗口到點不補記、已開始也覆寫、手動也寫帳本、空資料算保留四個突變皆紅 | ①**真缺陷**：窗口到點時沒開始的主機日、Sentinel 憑證解不開或整台失敗時，都沒有回執。隔天落在 `BackfillDays=1` 之外就永遠補不回來，正是 11-03～11-05 的情境。改為只在排程趟補記回執。②`empty-once` 算進「保留原結果」，安靜主機會讓里程碑每晚誤報。③PRTG 整段失敗沒有待補裝置，`HasWork` 為假，同窗口不續跑。④失敗類隔天會再試，卻顯示「已放棄」。⑤第二趟前導文字把手動停止也說成窗口結束。⑥狀態輪詢多查一次執行紀錄，併入 `PrtgRunHealth`。⑦只有昨天待補時閒置列不顯示。⑧每趟每台都多讀 7 天紀錄，規劃是只在有重查狀態時放大，改回規劃。**確認不是問題**：激進策略續補的數值由觸發式取數接手（夜間同步一律 `fetchValues:false`）。**留在 BACKLOG**：窗口切斷 PRTG 結構同步會整段重做；還沒輪到的 Sentinel 與異常中斷不留回執。 |
