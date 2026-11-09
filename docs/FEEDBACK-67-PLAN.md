# LogForesight 第 67 輪規劃：記憶體用量體檢

> 狀態：規劃中（使用者 2026-11-09 確認方向；同日完成四視角複檢改版，尚未開分支）
> 基準：dev@03a2a992（Windows 8,634 綠／11 略過；macOS 8,512 綠／14 略過；全量約 8 分鐘）
> 來源：使用者回報「排程連續執行三天，整台 24 GB 記憶體被 `LogForesight.Web` 吃光」（後端 Sqlite）；本輪為全專案記憶體體檢
> 實作方式：主代理寫階段規格，實作派內建 Agent（Sonnet 5.5，effort 由主代理依段評估；純測試小段可用 Haiku 5.5）；同一 checkout 一次只派一個改檔代理；每段主代理獨立重跑驗收，不採信代理摘要；收尾換模型體檢
> 掃描方式：四個 Sonnet 代理分區深讀（Core 分析主流程／PRTG／Persistence 與 Web 服務／Web 背景服務）＋一個 Haiku 機械普查，重點條目主代理親讀核對；NLog 6.1.4 與 EF Core 8.0.10 的行為由代理以獨立小專案實測。規劃初稿另經一個未看過結論的 Sonnet 代理做四視角（使用者／管理者／程式面／整體影響）獨立審查，38 條意見逐條核對後併入（見〈複檢紀錄〉）

## 背景（核對結論摘要）

**沒有找到「靜態集合只增不減、三天累到 24 GB」的單一洩漏。** 長命集合幾乎都有上限、TTL 或逐趟重建；`DbContext`、`PrtgClient`、`SentinelClient`、`JsonDocument`、linked CTS 的釋放都對。記憶體問題分三層：

1. **單趟峰值高＋GC 設定沒有生效也沒有硬上限**：第 35 輪批次 E 加了 `runtimeconfig.template.json`，但其中 `System.GC.Conserve` 不是 .NET 認得的鍵名（正確名稱是 `System.GC.ConserveMemory`），所以「保守回收」從來沒有啟用；也沒有 heap 硬上限。「實測數字待使用者」從未回填；站台沒有任何記憶體自我監測。
2. **確認存在的真缺陷**（量不大，但跨趟累積或每趟白做工）：NLog rule 殘留、PRTG 快照目標每 tick 整表重載、`AIService` 的 `HttpClient` 不釋放、兩個大結果快取不清舊版本且每次命中整份複製。
3. **堆外原生記憶體**：SQLite 連線層每條連線 page cache 上限 64 MB、1 GB mmap、`temp_store=MEMORY`，連線池關閉；GC 計數器看不到，硬上限也管不到。

### 核對表

| # | 項目 | 判定 | 證據 | 補充 |
|---|---|---|---|---|
| P0 | GC 設定鍵名寫錯 | ✅ 真缺陷 | `runtimeconfig.template.json:4` 寫 `System.GC.Conserve`；.NET 文件的 runtimeconfig 鍵是 `System.GC.ConserveMemory`（環境變數 `DOTNET_GCConserveMemory`）。全專案沒有讀回 GC 設定的地方可以發現這件事 | 第 35 輪 E4 的實測從未發生，錯鍵一直沉默 |
| P1 | NLog target 卸除後 rule 殘留 | ✅ 真缺陷 | `BatchRunRecorder.cs:539-553`：`Attach` 做 `AddTarget`＋`AddRule`，`Detach` 只 `RemoveTarget`。實測 NLog 6.1.4：附掛卸除 20 次後 `LoggingRules` 多 20 條 `Targets.Count==0` 的空殼 rule，target 本身可被 GC | 每個 recorder 一條：取數每趟 1 條（`AnalysisOrchestrator.cs:318`）、AI 排程每次 `TriggerRunAsync` 1 條（`AiAnalysisHostedService.cs:262`）。待補卡住時 AI 每 60 秒一趟（P12），三天可達數千條；每次 `ReconfigExistingLoggers` 成本隨 rule 數線性變重。**注意** `nlog.config:56-57` 有兩條合法的 `final` 規則也是 `Targets.Count==0`，不能用「無 target」當清理判準 |
| P2 | PRTG 快照目標「每小時才重算」失效 | ✅ 真缺陷 | `PrtgSnapshotHostedService.cs:94,1389`：`_targetRefreshHour` 只寫不讀；`ResolveTargets`（:625）每 tick 無條件 `RefreshTargets`，整表 `GetLatestHostMapWithDate`＋`GetSensorStatuses` 重載；守門啟用時再多 1～2 次整表（:630） | 每 5～15 分鐘一次 LOH 搖擺；非累積。`GetSensorStatuses` 共六個呼叫端、欄位用法各異（`CalibrationService.cs:1942` 用 DeviceObjid／Status／Category，`PrtgDailyPipeline.cs:390` 用 SensorName），`PrtgMonitoringController.cs:379` 每個請求整表只為取 Objid→SensorType |
| P3 | `BackfillScopeSensorsAsync` 單趟 3 次 `GetAllSensors` | ✅ 效能 | `PrtgSnapshotHostedService.cs:860-864`；`PrtgScopeDevices.Compute` 內 2 次；`PrtgMirrorGuardSource.GetSensors()` 無快取（`PrtgLiveGuardSource` 有 `_sensors ??=`） | 每次成功快照後跑 |
| P4 | `AIService` 每分片 new `HttpClient`＋`SocketsHttpHandler`，從未 Dispose | ✅ 真缺陷 | `AIService.cs:95,140,227`：非 `IDisposable`；`AiAnalysisHostedService.cs:459,720` 每個主機分片建一個；`AnalysisOrchestrator.cs:346` 每趟一個；`WebAiService.cs:127,157` 經 `SettingsBoundClient`（換設定時直接覆蓋 `_client`，`SettingsBoundClient.cs:45`，無釋放鉤子）；`HostDayWorkflowRecoveryHostedService.cs:307` | `PooledConnectionLifetime=0` 讓連線不久留，靠 GC 回收；量低但形狀不對 |
| P5 | 兩個大結果快取不清舊版本、每次命中整份複製 | ✅ 設計缺口 | `ActionableSnapshotCache.cs:60-62,71,80-93` 與 `IssueRankingCache.cs:66,81`：條目 tuple 沒有 Version 欄位、逐出只看 TTL 與 LRU；`TryGet` 命中回 `new List<>(entry.Items)` 整份複製、`Set` 再複製一次。另三個快取（`SummaryCache.cs:96`、`NextUnhandledSequenceCache.cs:92`、`IssueOwnedHostIdsCache.cs:62-65`）已會清非當前 Version | 鍵含 `DataVersionStamp`，每次寫入換鍵，舊版持續占位到第 256 筆；`ResolvedOccurrence` 單份可達數萬筆，每次命中就配置數百 KB 進 LOH。`IssueTodoQuery.cs:63-64` 命中後直接回傳 |
| P6 | 本機路徑 14 天區塊全載 | ⚠️ 峰值 | `AnalysisOrchestrator.cs:990,1028-1031`、`EventLogService.cs:335-340`：區塊內全部頻道事件連 Message 一次 `ToList`，無筆數上限；註解寫「預設 7 天」實際常數 14 | 平日只有 1 天；補跑多天、重跑、首次 120 天回補才吃滿。本輪不改 |
| P7 | 暴力破解日補掃整天 Security 4624 | ⚠️ 峰值 | `LogAnalysisService.cs:884-889`：只為比對帳號＋IP（`ExtractAccountsAndIpsWithTimes`，:966，從 `Message` 解析），卻把整天 SuccessAudit 含 Message 全載；載入的是「watchlist ∪ 4624」 | DC 一天數十萬到百萬筆；暴力破解持續時每趟觸發。判定只需要帳號／IP 落在**失敗集合**內的成功登入（:869-871 先算好 failed 集合） |
| P8 | NetIQ 整 job 事件桶 | ⚠️ 已知遺留 | `NetiqPipelineService.cs:598-617`，單 job 上限 10 萬筆、不截斷訊息；第 35 輪遞延進 BACKLOG | 預設平行 2×1 約 0.4～1 GB；調到上限 3×4 可達數 GB。本輪不改 |
| P9 | PRTG 狀態變更兩天整載 | ⚠️ 峰值 | `PrtgDailyPipeline.cs:460`→`EfPrtgStore.GetStateChanges`，含 Message、無筆數上限，再 `Where().ToList()` 複製 | flapping 站台單日數十萬列。本輪不改 |
| P10 | SQLite 連線層 PRAGMA（堆外） | ⚠️ 可設定化 | `SqlitePragmaInterceptor.cs:28`：`cache_size=-65536`、`mmap_size=1073741824`、`temp_store=MEMORY`；`StorageBackend.cs:103,357-363` 關閉連線池 | `cache_size` 是**上限、按需成長**（攔截器註解 :15 寫明），實際常駐取決於掃過的頁；並行 N 條連線最壞 N×64 MB。mmap 是檔案映射頁，不計入私有工作集。沒有 A 的數據不下修預設值 |
| P11 | 讀整段 log 行再記憶體篩選 | ⚠️ 請求級峰值 | `EfRecordHandlingStore.cs:164-179`（`GetLogs`／`GetReassignments` 以 `ReadLines(from, null)` 讀到今天）、`BatchRunStore.cs:402,433-441,533-541`（`GetRecentRuns`／`GetRun`／`GetRunsByIds` 載 2～120 天 run 行；`GetRun` 找不到時以窗口最小 RunId 決定是否全表）、`AuditLogStore.cs:108,127` | 已有 `EfJsonLogStore.EnumerateLines`（每頁 2,000 行、逐頁釋放 ctx）可用卻沒用；`BatchRunStore.ForEachLog`（:511）已是串流樣板。稽核查詢結果要依 `OccurredAt` 排序，串流只省原始字串清單，效益有限 |
| P12 | AI 排程待補無法處理時每 60 秒重觸發 | ⚠️ 頻率待實測（C-4 的修法不依賴此數字） | `AiAnalysisHostedService.cs:149-177,605-612`：待補 >0 且在窗口內或跟隨取數就觸發；無進度結束後沒有跨輪退避；`DateTime.Now` 直用五處（:134,165,167,168,177），沒有時鐘注入 | 帶動 P1 累積、每趟 run 行、每分鐘 `CountPendingAi`（註解說實機 7～15 秒）。是否真的每分鐘發生要看 `batch_runs` 的 `JobType='ai'` 筆數 |
| P13 | PRTG 取數每顆 sensor 一個 async 狀態機＋保存整個 `Exception` | ⚠️ 低 | `PrtgFetchService.cs:1301-1317`：`Enumerable.Range(0,totalSensors)` 全展開再用 `SemaphoreSlim` 限流；`:1262,1415,1494` 失敗存整個 `Exception` 到輪末 | 排隊中的 async 狀態機約數十 MB；全失敗時 `Exception` 數百 MB。本輪只改失敗形狀 |
| P14 | PRTG `Checkpoint` 每請求 4 次重讀 policy blob | ⚠️ 低 | `PrtgOperationScope.cs:49-58`、`PrtgScopeRevisionReader.ReadRawSemantic` 每次反序列化整份 policy＋SHA256 | 搖擺非累積。本輪不改 |
| P15 | `StorageBackend` 每趟重建會不會洩漏 EF service provider | ❌ 排除 | 實驗 EF Core 8.0.10：30 次帶新 interceptor 的 options 只增 1 個 provider；EF 的快取鍵不含 interceptor 實例 | 批次 A 的啟動 log 順手記 `ManyServiceProvidersCreatedWarning` 有無出現 |
| P16 | 其他靜態／單例集合 | ❌ 排除 | DNS 快取 10,000 上限、`SentinelEventFetchService` 512 筆、匯入與掃描待辦 30 分鐘 TTL、回填預覽 128 筆、`PrtgOperationScope.Versions` 留 32 筆、節流 10,000 鍵、journal 20 萬列／64 MiB、`PrtgRequestBudget` 容量有界、`NetiqPipelineService` 與 `PrtgFindingsRegistry` 每趟 new、`BatchRunRecorder` 輸出 500 行環形緩衝、`SchedulerRunState` 訊息只留最新一則、`PrtgDeviceIndexCache` 64 筆且刻意不綁版本 | — |

**附帶發現（非記憶體）**：
- `PrtgSnapshotJournal.cs:274,608` 的區段上限把 Upserts＋Removals＋Rows 合計；整點結算一個 delta 是 N 個 Removals＋一個 N 列 batch（`PrtgSnapshotHostedService.cs:1308-1320`），快照目標超過約 5 萬顆會擲 `InvalidDataException` → `JournalFailed` → 停止採集。
- `AnalysisOrchestrator.cs` 區塊註解寫 7 天、`EventLogService.DefaultScanChunkDays` 常數是 14。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A | 記憶體量測：健康頁、執行紀錄、診斷匯出記 GC／SQLite／NLog／快取指標；OOM 失敗原因可辨識 | 中 | 無 | 1 |
| B | GC 設定：修正錯鍵名、加 `HeapHardLimitPercent`、啟動時讀回生效值 | 小 | 無（與 A 同版出貨） | 2 |
| C | 真缺陷修正：C-1 NLog rule、C-3 `AIService` 釋放、C-4 AI 排程退避、C-2 快照目標與 sensor 投影 | 中 | A（共用 `BatchRunRecorder`／`AiAnalysisHostedService`，同一位實作者依序做） | 3 |
| D | 兩個大結果快取：版本清除、元素總數上限、命中不複製、命中統計 | 小 | A（統計介面） | 4 |
| E | 低風險峰值路徑：E-1 4624 補掃改串流過濾、E-2 執行紀錄與處理歷程改串流、E-3 PRTG 取數失敗形狀 | 中 | 無 | 5 |
| F | SQLite 連線層可設定化：三個 PRAGMA 值進 `appsettings.json`，`soft_heap_limit` 啟動設一次；預設值不變 | 小 | 無 | 6 |
| G | 附帶 bug：journal 2N＋1、註解 7／14 天 | 小 | 無 | 7 |

A 與 B 同一版出貨：硬上限的暫定值先上線擋住「再炸一次」，A 的數據只用來下修門檻與決定 F 要不要改預設。C、D、E、G 不等數據。

**實作順序與互踩盤點**：A → B → C-1 → C-3 → C-4 → C-2 → D → E-1 → E-2 → E-3 → F → G。A、C-1 都動 `BatchRunRecorder`；A、C-3、C-4 都動 `AiAnalysisHostedService.TriggerRunAsync`／`TickAsync`；A、C-2 都動 `PrtgSnapshotHostedService`；A、D 共用快取統計介面。這些由同一位實作者依序做，不並行。E、F、G 彼此獨立。

**回滾**：B、F 改設定值即可回到舊行為（B 另有環境變數覆寫）；其餘退版本。沒有 schema 變更。

---

## 批次 A：記憶體量測

### 現況與核對結果
- `HealthService.GetDetail()`（`HealthService.cs:82`）回 `HealthDetailDto`，只有版本、儲存、慢操作、分析進度、回填進度；儲存探測失敗時走早退分支（:85-112）。既有 `degraded` 判定只看慢操作占比與排程過期（:136-146）。
- 診斷匯出 `RunExportService.cs:504` 的 meta 只有 `AppVersion`、期間、保留天數；`SchemaVersion`（:76）目前為 1。
- 執行紀錄：`BatchRunRecorder.Milestone(string)`（:264）寫一行 Info 診斷行；`BatchRun.Phases`（`BatchRunStore.cs:189`）是時間軸階段，`RunTimingPhases.TopLevelLabels` 與 `run-phases.js` 都對它有對應，**不能拿來放記憶體**。
- 全站沒有讀回 GC 設定的地方（所以 P0 的錯鍵沉默了 32 輪）；全站只有 `PrtgTransferCapacityProbe` 處理 `OutOfMemoryException`；AI 排程失敗原因是 `ex.Message`（`AiAnalysisHostedService.cs:~268`）。
- `Microsoft.Data.Sqlite` 經 EF Core Sqlite 已把 `SQLitePCLRaw.core` 帶進 Core（`LogForesight.Core.csproj:45`）；SQLitePCL provider 要等第一條 Sqlite 連線開啟才初始化，`StorageBackend` 建構時有 fail-fast 連線。
- 健康頁在 `Views/Pages/Settings.cshtml` 的「系統健康」頁籤（:12,532-535）、`settings.js:633-640` 共用一次 `/api/health/detail`。

### 定案
1. 站台自己量，不靠外部工具。三個出口（健康頁詳情、執行紀錄、診斷匯出）都帶同一組「記憶體快照」，由 Core 單一純函式產生；SQLite 堆由 `StorageBackend` 提供（Sqlite 後端且已開過連線才讀，SqlServer 回 null），Core 直接呼叫 `SQLitePCL.raw`，不另設委派抽象。
2. 快照欄位（實作端可補不可少；下列 API 名稱與行為已於 2026-11-09 以獨立 .NET 8 小程式實測）：`WorkingSet64`、`PeakWorkingSet64`、`PrivateMemorySize64`、`PeakPagedMemorySize64`（OS 層峰值零成本，補「趟中尖峰」）、`GC.GetTotalMemory(false)`（即時堆）、`GC.GetGCMemoryInfo()` 的 `TotalCommittedBytes`／`HeapSizeBytes`／`FragmentedBytes`／各代與 LOH 大小／`TotalAvailableMemoryBytes`（＝生效中的硬上限；**第一次 GC 前前四者皆為 0**，欄位可空、0 時顯示「尚未回收」）、`GC.CollectionCount(2)`、`GCSettings.IsServerGC`、`GC.GetConfigurationVariables()` 的 `ServerGC`／`GCConserveMem`／`GCHeapHardLimitPercent`／`GCHeapHardLimit`（實測鍵名如此，讀回**生效值**而非設定檔）、SQLite `sqlite3_memory_used()`（當下；連線關閉後歸零）與 `sqlite3_memory_highwater(0)`（行程啟動以來峰值，這才是要看的數字；provider 未初始化時呼叫會擲例外，接住回 null）、`LogManager.Configuration.LoggingRules.Count`、各快取統計（名稱、條目數、元素總數、命中、未命中、逐出、超限拒收）。
3. 執行紀錄：取數與 AI 兩種 recorder 在**開始與結束**各寫一次：既有 `Milestone` 一行白話（人看）＋ `BatchRun` 兩個可空的型別化欄位 `MemoryStart`／`MemoryEnd`（程式讀）。不新增 phase、不動時間軸。
4. OOM 可辨識：`AnalysisOrchestrator.RunAsync` 與 AI 排程的最外層 catch 遇到 `OutOfMemoryException` 時，失敗原因固定帶「記憶體上限」字樣（常數集中一處），健康頁「上次執行」與執行紀錄都看得到。硬上限只管 .NET 堆，README 寫明堆外（SQLite）不受約束。
5. 健康頁詳情（需 Maintain）顯示快照，含「硬上限使用率」（`GC.GetTotalMemory(false)` ÷ `TotalAvailableMemoryBytes`）；**既有 `degraded` 判定加一條**：使用率 ≥ 85%（常數，不開設定）→ `degraded`，原因字串寫明「記憶體接近硬上限」。不另設「實體記憶體」條件：.NET 沒有乾淨的實體記憶體 API，而 OOM 的邊界就是硬上限。儲存探測失敗的早退分支也帶記憶體區段（記憶體讀取不碰資料庫）。
6. 診斷匯出 meta 加 `Memory`（匯出當下快照）與 `GcSettings`；runs 區每趟帶 `MemoryStart`／`MemoryEnd`。`SchemaVersion` **定案不升版**（純加鍵，舊讀取端忽略未知鍵）。去識別化不處理（純數字）。
7. 啟動時 `web.log` 記一行 GC 生效值（Server GC 與否、ConserveMemory、HeapHardLimitPercent、TotalAvailableMemoryBytes）與 EF `ManyServiceProvidersCreatedWarning` 的出現與否；格式化抽成純函式。
8. 前端：健康頁籤加「記憶體」卡（數字單位 MB、null 顯示「—」、硬上限使用率一條進度文字），套 `ui-ux-pro-max`、DESIGN-SYSTEM v2；不做趨勢圖（趨勢靠執行紀錄）。
9. 量測劇本：A＋B 併入後跑一晚，隔天診斷匯出就有四筆里程碑與當下快照，使用者貼匯出檔即可；本文「量測結果」表由主代理從匯出檔填。

### 改動
1. Core：`MemorySnapshot` record＋靜態 `Capture(Func<long?> sqliteHeap, Func<IEnumerable<CacheStatistics>> caches)`；`StorageBackend` 加 `TryReadSqliteHeap()`。
2. Web：`ICacheStatistics`（名稱、條目數、元素總數、命中、未命中、逐出、超限拒收）由五個結果快取與 `PrtgDeviceIndexCache` 實作，DI 以 `IEnumerable<ICacheStatistics>` 注入 `HealthService`。
3. `HealthDetailDto` 加 `Memory` 區段；`HealthService.GetDetail()` 兩條分支都填；`degraded` 判定加記憶體條件；`HealthStatuses` 不變。
4. `BatchRun` 加 `MemoryStart`／`MemoryEnd`；`BatchRunRecorder` 加 `RecordMemory(start|end)`；`AnalysisOrchestrator.RunAsync` 與 `AiAnalysisHostedService.TriggerRunAsync` 呼叫；兩處最外層 catch 的 OOM 原因常數。
5. `RunExportService` meta 加 `Memory`／`GcSettings`，runs 區帶里程碑；`RunMonitorService` 的執行詳情 DTO 帶出兩筆供畫面顯示。
6. 前端：`settings.js` 健康頁籤記憶體卡；`runs.js` 執行詳情顯示「記憶體 起／訖」兩行（有值才顯示）。
7. `Program.cs` 啟動 log 一行（純函式格式化）。
8. 文件：WEB-SPEC §健康 API 欄位、§9.10 執行詳情欄位、§匯出 meta；HELP 健康章節一段白話；README 維運節補「零程式碼自證法」（臨時設 `DOTNET_gcServer=0` 對照、`dotnet-gcdump`）。

### 測試／驗收（主代理以 `dotnet test` 與 grep 判定）
- 快照：欄位非負；`IsServerGC` 與測試行程 `GCSettings.IsServerGC` 一致；`GC.GetConfigurationVariables()` 讀回的字典含 `GCConserveMem` 與 `GCHeapHardLimitPercent` 兩鍵；型別化欄位 JSON 往返序列化相等；SqlServer 後端（或 provider 未初始化）SQLite 欄位為 null 且不擲例外；Sqlite fixture 開過連線後 `sqlite3_memory_highwater(0) > 0`。
- 健康：注入假快照，使用率 85% → `degraded` 且原因含「記憶體」；84% → 不因記憶體 degraded；`TotalAvailableMemoryBytes` 為 0 時不除以零、不 degraded；儲存探測失敗分支回應含 `Memory` 區段。
- 快取統計：塞 3 筆讀到 3；命中一次 hits=1、未命中一次 misses=1。
- 執行紀錄：一趟 recorder 開始與結束各一筆 `Milestone` 且 `MemoryStart`／`MemoryEnd` 非 null；`Phases` 數量與既有測試基線相同（不新增 phase）。
- OOM：假服務擲 `OutOfMemoryException`，執行紀錄失敗原因含「記憶體上限」（取數與 AI 兩路各一案）。
- 匯出：`RunExportServiceTests` 補 meta 含 `Memory`／`GcSettings`、runs 含 `memoryStart`／`memoryEnd`；`SchemaVersion` 仍為 1。
- 啟動 log 格式化純函式：給定假值輸出含四個鍵名。
- 前端：新增 `settings.behavior.mjs` 案例「null 顯示 —、數值顯示 MB」並登錄 `InlineData`；`FrontendConsistencyUiTests` 綠（不得插值 `innerHTML`）。
- 文件：grep WEB-SPEC 含 `MemoryStart`、`GcSettings`；HELP 健康章節含「記憶體」。
- 篩選詞（已確認命中）：`HealthAndPerformanceTests|HealthServiceKeyLocationWarningTests|HealthTabUiTests|BatchRunRecorderScopeTests|BatchRunRecorderPhaseTests|RunExportServiceTests|RunExportControllerTests|RunMonitorServiceTests|ActionableSnapshotCacheTests|SummaryCacheTests|IssueOwnedHostIdsCacheTests|CacheEvictionTests|UiTests|JsModuleImport|BehaviorTests|ApiTimeout|HelpContentServiceTests`（全部已 grep 確認存在）。

### 量測結果（A＋B 併入後由主代理從診斷匯出填）
| 時點 | WorkingSet | PeakWorkingSet | GC Heap | Committed | LOH | SQLite 堆 | LoggingRules | 備註 |
|---|---|---|---|---|---|---|---|---|
| 取數開始 | | | | | | | | |
| 取數結束 | | | | | | | | |
| AI 開始 | | | | | | | | |
| AI 結束 | | | | | | | | |

---

## 批次 B：GC 設定

### 現況與核對結果
- `runtimeconfig.template.json`：`System.GC.Server=true`、`System.GC.Conserve=5`（**錯鍵，無效**）、`System.GC.RetainVM=false`；無硬上限。
- Server GC 每個邏輯核心一個 heap，gen2 回收不積極；在 24 GB 單租戶機器上工作集會長到接近機器壓力才收。
- README 升級注意（:740,775-790）寫 IIS 發行採「先清空再複製」，發行目錄內的 `runtimeconfig.json` 與 `web.config` 都會被洗掉。

### 定案（使用者 2026-11-09 確認方向；複檢後補強）
1. 鍵名改為 `System.GC.ConserveMemory`，值維持 5。
2. 維持 Server GC，加 `System.GC.HeapHardLimitPercent`，值**暫定 60**，與 A 同版出貨；A 跑一晚後依「GC Heap 峰值（`PeakWorkingSet` 與 `HeapSizeBytes` 的較大者）」調整：峰值遠低於 60% 則下修到峰值的 1.5 倍左右並向上取整到 10 的倍數；接近或超過 60% 代表峰值路徑（P6～P9）才是主因，門檻不壓低，回頭擴大批次 E。
3. 不改 Workstation GC。若 A 顯示 GC Heap 很小但 WorkingSet 很大（差距以 GB 計），再評估 `GCHeapCount` 收斂 heap 數，記進待決。
4. 硬上限只管 .NET 堆；達上限擲 `OutOfMemoryException`，由 A-4 轉成可辨識的失敗原因。README 寫明：堆外（SQLite）不受約束；OOM 落在請求執行緒時該請求失敗、站台不會因此停止（ASP.NET Core 請求例外由中介軟體收）；覆寫一律用**機器層環境變數或 IIS 應用程式集區環境變數** `DOTNET_GCHeapHardLimitPercent`，不要改發行目錄內的檔案。

### 改動
1. `runtimeconfig.template.json` 改鍵名、加硬上限。
2. README 升級注意：新設定、覆寫方式、達上限症狀與該看哪裡（健康頁記憶體卡、執行紀錄失敗原因、`web.log` 啟動行）。
3. 守門測試：讀 `runtimeconfig.template.json`，**鍵名白名單**（`System.GC.Server`、`System.GC.ConserveMemory`、`System.GC.RetainVM`、`System.GC.HeapHardLimitPercent`，出現白名單外的 `System.GC.*` 鍵即紅），值型別與區間（Percent 30～90、ConserveMemory 0～9）。

### 測試／驗收
- 守門測試綠；突變：把鍵名改回 `System.GC.Conserve`，測試必須紅。
- 建置輸出 `LogForesight.Web.runtimeconfig.json` 含四個鍵（測試讀 `AppContext.BaseDirectory` 鄰近的 Web 輸出不可靠，改由主代理以 `dotnet build` 後 grep 判定，寫進執行紀錄）。
- A-7 的啟動 log 在 `dotnet run` 下印出 `GCConserveMem=5`、`GCHeapHardLimitPercent=60`、`GCHeapHardLimit=<實體記憶體×0.6>`（主代理手動一次，寫進執行紀錄；2026-11-09 已在獨立小程式驗證同一組設定能讀回這三個值）。
- 篩選詞：守門測試類名定為 `RuntimeConfigGuardTests`（新檔）。

---

## 批次 C：真缺陷修正

### C-1 NLog rule 殘留（P1）
- 現況：`BatchRunNLogTarget.Attach` 加 target＋`config.AddRule(Warn, Fatal, this)`，`Detach` 只移 target；`nlog.config:56-57` 兩條 `Microsoft.*`／`System.Net.Http.*` `final` 規則本來就沒有 target。
- 定案：`Attach` 自己 `new LoggingRule(...)` 並**保存參考**，`Detach` 移除該物件（不掃描、不比對特徵）；升級前殘留的空殼以精確特徵清理一次（pattern `*`、Warn～Fatal、非 `final`、無 target、且 rule 名稱為空）——放在 `Attach` 內順手做，不另開啟動步驟。維持 `ConfigLock` 與 `ReconfigExistingLoggers`。
- 驗收：附掛卸除 N 次後 `LoggingRules.Count` 回到起點；`nlog.config` 的兩條 `final` 規則在附掛卸除後**仍在**（以測試用 NLog 設定塞兩條同形狀規則斷言）；兩個 recorder 交錯附掛卸除（既有 `BatchRunRecorderConcurrencyTests` 形狀）各自的 rule 都被移除且另一個仍收得到事件。突變：把 `Detach` 移除 rule 那行拿掉，計數測試必須紅。
- 篩選詞：`BatchRunRecorderConcurrencyTests|BatchRunRecorderScopeTests|BatchRunRecorderPhaseTests`。

### C-3 `AIService` 釋放（P4）
- 現況：`AIService` 非 `IDisposable`；`IAiService` 有 6 個實作（含測試替身 `AiFakes.cs:48` 等）。
- 定案：`AIService : IAiService, IDisposable`（釋放 `HttpClient`、`_requestQueue`）；**不改 `IAiService`**，呼叫端以 `(service as IDisposable)?.Dispose()` 釋放，替身零改動。本輪只做兩處：`AiAnalysisHostedService` 每個分片 `finally` 釋放、`AnalysisOrchestrator.RunAsync` 以 `using`。`WebAiService`（`SettingsBoundClient` 換設定時釋放舊實例會讓在飛的互動請求擲 `ObjectDisposedException`，且泛型類別被 `DynamicAuthenticationProvider` 共用）與 `HostDayWorkflowRecoveryHostedService`（換設定極少）列「不做」並記 BACKLOG。
- 驗收：`AIService` Dispose 後 `ChatAsync` 擲 `ObjectDisposedException`；`AiAnalysisSchedulerTests` 以實作 `IDisposable` 的可計數替身斷言每個分片結束都釋放（成功、失敗、取消三條路徑各一案）；`AiRequestGateTests` 的 `new AIService(` 掃描不受影響。
- 篩選詞：`AIServiceCancellationTests|AIServiceChatJsonTransportTests|AiAnalysisSchedulerTests|AiRequestGateTests`。

### C-4 AI 排程無進度退避（P12）
- 現況：`TickAsync` 每 60 秒只要待補 >0 且在窗口內（或跟隨取數）就 `TriggerRunAsync`；處理迴圈在「本批全滅且總數未動」或「剩餘待補都在取數範圍／本輪已失敗」時結束，沒有跨輪退避；`BlockingIdleReason`（:185）是閒置原因唯一判定點；`AiIdleReasons` 常數在 `AiAnalysisRunState.cs:23-35`；`runs.js:1429` 有 `AI_IDLE_REASON_TEXT` 對照；hosted service 五處 `DateTime.Now` 直用。
- 定案：
  1. 退避狀態放既有單例 `AiAnalysisRunState`（`NoProgressStreak`、`NextEligibleAt`）；`AiAnalysisHostedService` 加可選 `Func<DateTime> now` 建構子參數（預設 `DateTime.Now`），五處直用改走它。
  2. 一輪結束時「成功完成＋降級寫回」為 0 → 進入退避：下一次自動觸發至少間隔 5 分鐘，連續無進度每次加倍，上限 **15 分鐘**（複檢後由 30 下修）；任何一次有進度歸零。
  3. 提前解除：排程設定儲存（每 tick 比較 `ScheduleOptions.UpdatedAt`，`ScheduleOptionsStore.Touch` 已會更新它）、AI 設定儲存（比較 `SystemSettings.UpdatedAt`，`SystemSettings.cs:351` 已有）、取數寫入新資料（`OnFetchFollowUp` 觸發時重置；「待補數上升」改用這個事件代替——退避中不查待補是本項的省成本前提，比較待補數會把查詢加回來）、跟隨取數期間（上限壓到 5 分鐘不加倍）。不新增儲存鉤子。手動觸發（立即補跑／強制重新分析）不受退避影響。
  4. 退避判斷放在 `BlockingIdleReason` 內 **`CountPendingAi` 之前**，退避中不查待補（省每分鐘 7～15 秒的查詢）。
  5. 新原因碼 `AiIdleReasons.Backoff = "backoff"`；`GET ai-status` 的 `idleReason` 帶它，另帶 `nextRetryAt`；`runs.js` 文案「上一輪沒有進度，暫停重試中（約 N 分後再試；可按『立即補跑』）」；WEB-SPEC §9.10 閒置原因清單同步。
  6. 站台重啟退避歸零（記憶體狀態，刻意不持久化）。
- 驗收（假時鐘）：無進度 → 1 分鐘後 tick 不觸發且不呼叫 `CountPendingAi`、5 分鐘後觸發；連續兩次無進度 → 10 分鐘、三次 → 15 分鐘封頂；有進度後下一分鐘即觸發；待補數上升立即解除；設定儲存立即解除；跟隨取數期間固定 5 分鐘；手動觸發不受影響；`BlockingIdleReason` 回 `backoff` 且 `nextRetryAt` 正確。突變：把加倍改成固定，第二次間隔測試必須紅。前端：`runs.behavior.mjs` 新增「`backoff` 原因碼有文案」案例並登錄 `InlineData`。文件：grep WEB-SPEC §9.10 含 `backoff`。
- 篩選詞：`AiAnalysisSchedulerTests|RunsPageUiTests|UiTests|JsModuleImport|BehaviorTests|ApiTimeout`（已確認存在）。

### C-2 PRTG 快照目標每 tick 整表重載（P2、P3）
- 現況：見核對表 P2、P3；五個重設點（:204 `RequestScopeRefresh`、:400 範圍補抓逾時、:418 設定變更、:528 補抓後）都把 `_targetRefreshHour` 設 null；`WhitelistFingerprint`（:1392）只含型別白名單。
- 定案：
  1. `RefreshTargets` 只在「`_targetRefreshHour` 為 null（啟動、五個既有重設點）」或「不等於本小時」或「指紋改變」時執行；指紋**擴大**為：型別白名單＋守門相關設定（啟用、目標與門檻欄位）＋ Sentinel 清單 blob 版本＋主機主檔 blob 版本。版本直接讀 `_backend.Blob("hosts").ReadVersion()` 與 `_backend.Blob("sentinels").ReadVersion()`（`EfJsonBlobStore.ReadVersion` 已是公開方法，SQLite 下微秒級；快照服務本來就持有 `_backend`）。**不改 `IHostStore`／`ISentinelStore` 介面**：兩者在測試有 35／11 個替身實作，加屬性會全部編不過。改守門設定、停用或合併主機後，下一個 tick 必重算。
  2. 守門目標（`PrtgResourceGuardTargets.Resolve`）跟著同一週期快取，不每 tick 重算。
  3. `PrtgMirrorGuardSource` 加單趟快取（同 `PrtgLiveGuardSource` 的 `_sensors ??=` 形狀）；`BackfillScopeSensorsAsync` 把 `Compute` 用的來源與自己的 `GetAllSensors` 共用同一個實例。
  4. 新增專用投影 `GetSensorTypes(objid 範圍可選)` 回 `Objid→SensorType`，供快照服務與 `PrtgMonitoringController.cs:379` 共用；**不改** `GetSensorStatuses` 簽章與其餘四個呼叫端。
- 保護生效之前的路徑：第一個 tick（null）必刷新；小時切換但沒有 tick（服務暫停）時下一個 tick 必刷新。
- 驗收：以假 store 計數 `GetLatestHostMapWithDate`／`GetSensorTypes`／`GetAllSensors`：同一小時連續 3 個 tick 只讀 1 次；換小時讀第 2 次；白名單改變、守門設定改變、主機停用、Sentinel 清單改變各自立即讀；五個重設點之後立即讀。`BackfillScopeSensorsAsync` 單趟 `GetAllSensors` 只 1 次。`PrtgMonitoringController` 該端點單次請求 `GetSensorStatuses` 呼叫次數為 0、`GetSensorTypes` 為 1。突變：把「同小時跳過」判斷拿掉，計數測試必須紅。
- 篩選詞：`PrtgSnapshotHostedServiceTests|PrtgScopeDevicesTests|PrtgResourceGuardTargetsTests|PrtgResourceGuardTests|PrtgResourceGuardAutoDetectTests|PrtgMonitoringContractTests|PrtgMonitoringPagingTests|EfPrtgStoreTests`（已確認存在）。

---

## 批次 D：兩個大結果快取（P5）

### 現況與核對結果
- `ActionableSnapshotCache`、`IssueRankingCache`：條目 tuple 沒有 Version 欄位，逐出只看 TTL 與 LRU；`TryGet`／`Set` 各整份複製一次。`IssueTodoQuery.cs:63-64` 命中後直接回傳給呼叫端。
- 另三個快取（`SummaryCache`、`NextUnhandledSequenceCache`、`IssueOwnedHostIdsCache`）已會清非當前 Version，**不動**。`PrtgDeviceIndexCache` 刻意不綁版本、64 筆，不動（只接 A 的統計）。
- 既有測試 `CacheEvictionTests` 五案（滿了淘汰最久沒存取的一筆）、`ActionableSnapshotCacheTests`。

### 定案
1. 兩個快取的條目加 `Version`，`EvictForInsert` 照 `SummaryCache.cs:96` 的寫法先清 `Version != version`，再過期、再 LRU。**不抽共用基底**（五個 tuple 型別各異，抽象只為兩行 `Where` 不值）。
2. 加「元素總數上限」（Items 筆數加總），**建構子可注入**（預設暫定 200,000）；超過時逐出 LRU 直到低於上限；單筆本身超過上限的不進快取（`TryGet` 回 null、統計記一次超限拒收）。`MaxEntries` 維持 256（版本清除後實際條目數本來就小，256 只是保險）。
3. 命中不複製：`TryGet` 回傳型別改成 `IReadOnlyList<T>`，順著改 `IssueTodoQuery.ResolveActionable`（:49，呼叫端 `DashboardService.cs:197` 與 `IssueTodoQuery.cs:38`）與 `IssueRankingBuilder.Build`（:107,117，呼叫端 `DashboardService.cs:180`、`ReportService.cs:162`；`ExcludeConcluded` 本來就收 `IReadOnlyList`）的回傳型別。已 grep 這些檔案沒有 `RemoveAll`／`Sort`／`Insert`／`RemoveAt`，預期編譯即過；若有呼叫端編不過就是真的就地修改，該處自行複製。`Set` 仍複製一次（呼叫端之後可能改自己那份）。
4. 命中／未命中／逐出／超限拒收計數接 A 的 `ICacheStatistics`。

### 測試／驗收
- 版本推進後寫入新鍵，舊版條目全部消失（兩個類別各一案，加進 `CacheEvictionTests`）；元素總數以注入的小上限（如 10）驗證：超限逐出 LRU 到上限內、單筆超限不進快取且 `TryGet` 回 null、統計超限拒收＋1；命中兩次回同一個參考（`ReferenceEquals`）；既有 TTL 測試與五案 LRU 測試不動仍綠。突變：把「清非當前 Version」拿掉，版本測試必須紅。
- 篩選詞：`ActionableSnapshotCacheTests|CacheEvictionTests|IssueRankingBuilderTests|DashboardServiceTests|ReportPerformanceContractTests|RecordListQueryServiceTests|OccurrenceFullKeyRegressionTests`。

---

## 批次 E：低風險峰值路徑

### E-1 4624 補掃改串流過濾（P7）
- 現況：`LogAnalysisService.cs:884-889` 以 `ScanRange(...)` 整天 Security（watchlist ∪ 4624）載進 `List<EventLogEntryData>`；之後 `ExtractAccountsAndIpsWithTimes`（:966）從 `Message` 解析帳號／IP，`FilterByTiming` 取各帳號／IP **最晚**成功時間與失敗集合比對；失敗集合在 :869-871 已先算好。
- 定案：補掃改為專用的「成功登入證據掃描」：逐筆讀 `EventRecord`，格式化 Message 後**沿用同一條 Message 解析路徑**（`LogAggregator.ExtractAccountsAndIps`），只保留「帳號或 IP 落在失敗集合內」的 `(Time, Accounts, Ips)` 輕量記錄，不建 `EventLogEntryData`、不保留 Message；**不設筆數上限**（過濾後記憶體與失敗集合規模成正比，不是與整天事件數）。`ExtractAccountsAndIpsWithTimes` 加一個吃輕量記錄的多載，主掃描已有的 4624 與 RDP 成功事件轉成同一型別後走同一多載（DRY）。測試注入點 `_localSecuritySupplementScanner` 改成回傳輕量記錄清單（只有 1 處測試使用）。Windows 專屬 API 維持既有平台守門（`[WindowsFact]`，macOS CI 略過）。
- 行為契約：同一份樣本事件，暴力破解後成功登入的判定（帳號、IP、時間）與改前逐位一致；後段才出現的成功登入仍被抽到。
- 驗收：既有 `SuccessfulLogonEvidenceSourceTests` 綠；新增契約測試：同一批樣本（含失敗集合外的大量 4624）跑新舊兩路，判定結果相等；新增「失敗集合外的 4624 不產生記錄」（替身計數為 0）與「樣本最後一筆才成功的帳號仍被判定」；`Map`（建 `EventLogEntryData`）呼叫次數為 0。
- 篩選詞：`SuccessfulLogonEvidenceSourceTests|LogAnalysisServiceSplitTests|LogAnalysisServiceRetryAiTests|CorrelationAnalyzerTests|LogAggregatorExtractionTests`。
- 文件：DETECTION-SPEC 補掃語意一句（只抽失敗集合相關的成功登入）。

### E-2 執行紀錄與處理歷程改串流（P11）
- 現況：`EfRecordHandlingStore.GetLogs`／`GetReassignments`（讀到今天）、`BatchRunStore.GetRecentRuns`／`GetRun`／`GetRunsByIds`（2～120 天窗口全載再 `LatestPerRun`，`GetRun` 以窗口最小 RunId 決定是否全表）以 `ReadLines` 全撈後記憶體篩選；`EfJsonLogStore.EnumerateLines`（升冪、每頁 2,000）與 `BatchRunStore.ForEachLog`（:511）已是現成樣板。`AuditLogStore.Query` 結果依 `OccurredAt` 排序，串流只省原始字串，效益有限。
- 定案：
  1. `GetLogs`／`GetReassignments`：`EnumerateLines` 串流過濾，結果排序不變。
  2. `GetRecentRuns`／`GetRun`／`GetRunsByIds`：升冪串流，只保留目標 RunId 的最新列（`Dictionary<RunId, 最新列>`），並照原樣累計窗口最小 RunId 供 `GetRun` 的全表退路判斷；**不新增降冪列舉、不提早停止**；`LatestPerRun` 語意（同 run 多列取最新）不變。全表退路也改同樣串流。
  3. `AuditLogStore`：本輪**不做**，記 BACKLOG（排序鍵非 Seq，要 top-K 才有效益）。
- 行為契約：三個 store 的回傳內容與順序與改前逐位一致。
- 驗收：既有 `HandlingStoreContractTests|HandlingServiceTests|HandlingRetentionTests|BatchRunStoreSqlPushdownTests|BatchRunIdentityAllocationTests` 綠；新增「替身 log store 的 `ReadLines` 呼叫次數為 0、`EnumerateLines` 大於 0」（兩個 store 各一案）；`GetRun` 在窗口內找到時不讀全表、窗口外且 RunId 小於窗口最小值時讀全表（兩案）。
- 篩選詞：上列五類。

### E-3 PRTG 取數失敗形狀（P13）
- 現況：`PrtgFetchService.cs:1262,1415,1494` 把每顆失敗的整個 `Exception` 存到輪末，只用來判逾時與計數。
- 定案：`Failures` 改存輕量記錄（失敗種類、訊息前 500 字、是否逾時旗標，逾時判定在記錄當下算好）；**worker 池改寫本輪不做**（重寫並行骨架風險大於數十 MB 的收益），記 BACKLOG。
- 驗收：既有 `PrtgFetchServiceTests|PrtgBackfillRunnerTests` 綠；新增「全部失敗時 `Failures` 不持有 `Exception` 參考」（型別斷言）與「逾時計數與改前一致」。
- 篩選詞：`PrtgFetchServiceTests|PrtgBackfillRunnerTests`。

---

## 批次 F：SQLite 連線層可設定化（P10）

### 現況與核對結果
- `SqlitePragmaInterceptor.cs:28`：`cache_size=-65536`、`mmap_size=1073741824`、`temp_store=MEMORY`、`busy_timeout=5000`；第 36 輪批次 B 定的，`cache_size` 是上限按需成長。沒有 `soft_heap_limit`。攔截器目前沒有任何測試引用。
- `StorageSettings`（`AppSettings.cs:213-243`）已有 `SqliteWal` 這種「僅 Sqlite」鍵的先例。

### 定案（複檢後由「下修」改為「可設定、預設不變」）
1. 三個值全部可設定、預設維持現值：`Storage:SqliteCacheSizeKb`（預設 65536）、`Storage:SqliteMmapSizeBytes`（預設 1073741824）、`Storage:SqliteSoftHeapLimitBytes`（預設 0＝不設）。留空用預設；負值擲設定錯誤並點名鍵。要不要下修預設，等 A 的 SQLite 堆峰值與慢操作統計再定。
2. `soft_heap_limit` 是全行程單一值：`StorageBackend` 建構的 fail-fast 連線**開啟之後**設一次（`SQLitePCL.raw.sqlite3_soft_heap_limit64`，已實測可呼叫、回傳前值；provider 在第一條連線開啟前未初始化，提早呼叫會擲例外），不放進每連線 PRAGMA 字串。
3. SqlServer 後端：三個鍵無消費端，啟動時若有填就 `web.log` Warn 一行「僅 Sqlite 適用，已忽略」；`StorageSettings` 註解與 README 標明「僅 Sqlite」（同 `SqliteWal`）。
4. `temp_store=MEMORY`、`busy_timeout` 不動。

### 改動
- `SqlitePragmaInterceptor` 接受參數；`StorageBackend` 從 `StorageSettings` 讀值並設 soft heap limit；`appsettings.json` 註解；README 升級注意一句；DB-SPEC Sqlite 連線層段落。

### 測試／驗收
- 以真實 Sqlite 檔案連線讀回 `PRAGMA cache_size`／`PRAGMA mmap_size` 斷言等於設定值（已實測 `PRAGMA cache_size;` 讀回負值 KB 形式，如 `-16384`）；留空時等於預設；負值擲設定錯誤且訊息含鍵名；設了 soft heap limit 後 `sqlite3_soft_heap_limit64(-1)` 查詢回設定值；SqlServer 後端填值時 log 一行（以 NLog 記憶體 target 斷言）。既有 `StorageBackendSqlitePoolingTests` 綠。
- 篩選詞：`StorageBackendSqlitePoolingTests|StorageBackendMutexNameTests|SqlitePragma`（攔截器新測試類名含 `SqlitePragma`）。

---

## 批次 G：附帶 bug

### G-1 journal 區段上限 2N＋1
- 定案：分別檢查「單一 batch 列數」與「Removals 數」各不超過 `MaxRowsPerSegment`，合計檢查改成 2×`MaxRowsPerSegment`＋常數；journal 整體 `MaxRows=200,000` 不變。
- 驗收：「6 萬顆 sensor 整點結算不擲例外」「12 萬顆擲例外」「6 萬顆連續兩個整點結算仍不超過整體上限」三案。
- 篩選詞：`PrtgSnapshotJournalTests|PrtgSnapshotHostedServiceTests`。

### G-2 註解 7／14 天不一致
- 定案：`AnalysisOrchestrator.cs` 區塊註解改為引用常數名稱，不寫數字。
- 驗收：grep 該檔不含「預設 7 天」字樣（主代理執行，寫進執行紀錄）。

---

## 文件同步清單（收尾前逐條 grep）

| 文件 | 內容 |
|---|---|
| README 升級注意 | B：GC 鍵名修正、硬上限、覆寫方式（機器層／app pool 環境變數）、達上限症狀與該看哪裡、堆外不受約束；F：三個 Sqlite 鍵；維運節：零程式碼自證法 |
| WEB-SPEC | 健康 API `Memory` 區段與 degraded 條件；§9.10 閒置原因加 `backoff`、執行詳情記憶體欄位；匯出 meta `Memory`／`GcSettings`、runs 區欄位 |
| DB-SPEC | Sqlite 連線層 PRAGMA 可設定與預設值 |
| PRTG-SPEC | 快照目標刷新週期與指紋內容 |
| DETECTION-SPEC | 補掃只抽失敗集合相關的成功登入 |
| HELP | 健康章節「記憶體」一段 |
| BACKLOG | P6、P9、P14 遞延；`WebAiService`／`HostDayWorkflowRecovery` 的 `AIService` 釋放；`AuditLogStore` 串流；PRTG 取數 worker 池；P8 已有條目更新引用 |

## 明確不做（本輪定案）

- **P6 本機 14 天區塊天數與筆數上限**：改分析迴圈語意，等 A 的數據證明本機路徑是峰值主因再開專段。
- **P8 NetIQ 整 job 事件桶**：第 35 輪已遞延、BACKLOG 有記；重構風險高（同 IP 多主機共用桶子語意），同上等數據。
- **P9 PRTG 狀態變更分批**：要改 `PrtgRuleEvaluator` 的輸入形狀，同上等數據。
- **P14 PRTG Checkpoint 重讀 policy**：搖擺非累積，量低。
- **改 Workstation GC**：先加硬上限觀察。
- **`AIService` 分片共用 `HttpClient`**：與既有逐實例佇列定案衝突，只做釋放。
- **`WebAiService`／`HostDayWorkflowRecovery` 的 `AIService` 釋放**：換設定極少且有在飛請求的競態，記 BACKLOG。
- **連線池重新開啟**：第 36 輪關池理由（user function 競態）仍成立。
- **F 下修 PRAGMA 預設值**：`cache_size` 是上限非常駐，沒有 A 的數據不動。
- **`AuditLogStore` 串流**、**PRTG 取數 worker 池**：效益有限或風險大於收益，記 BACKLOG。
- **五個快取抽共用基底**：只有兩個需要改，兩行 `Where` 不值一層抽象。

## 複檢紀錄

### 第一次複檢（規劃完成後）
- 與既有設計衝突：F 原本要推翻第 36 輪的 64 MB 取值，複檢後改為可設定、預設不變，不再衝突。B 的硬上限是新增設定，與第 35 輪 E4 相容（並修正其錯鍵）。C-2 不改既有五個重設點的語意。C-4 的退避進 `BlockingIdleReason` 單一判定點。
- 批次之間：見〈批次總覽〉的互踩盤點與固定順序。
- 四個坑：D「單筆超限」寫了不進快取；E-1 不設上限（過濾後規模與失敗集合成正比），避免偵測路徑截斷；C-2 寫了啟動與跨小時兩條「保護之前」路徑；C-4 寫了重啟歸零與四條提前解除。移除類：C-3 不改 `IAiService`，替身零改動。
- 升級既有資料：無 schema 變更；B／F 只改設定檔；C-1 的空殼 rule 以精確特徵清理且不碰 `final` 規則。

### 第二次複檢（獨立四視角審查 38 條，逐條核對後處置）
採納並改寫定案：P0 錯鍵（審查 #8，主代理核對 .NET 文件確認）、硬上限與 A 同版出貨（#11）、OS 峰值欄位（#3）、健康早退分支（#4）、`GetTotalMemory` 即時堆（#5）、前端行為案例（#6）、OOM 可辨識（#7）、IIS 環境變數覆寫（#9）、`GC.GetConfigurationVariables` 讀生效值（#10）、C-1 保存 rule 參考且不刪 `final` 規則（#12，主代理核對 `nlog.config:56-57` 確認）、C-2 專用投影與 controller 呼叫端（#13）、指紋擴大（#14）、C-3 只做兩處（#15、#16）、C-4 時鐘注入、退避前置、提前解除、15 分鐘上限、前端與文件（#17～#19）、D 只改兩個快取且不抽基底（#20）、命中不複製（#21）、統計計數與可注入上限（#22）、`PrtgDeviceIndexCache` 接統計（#23）、測試落腳 `CacheEvictionTests`（#24）、E-1 以失敗集合過濾取代上限（#25、#26）、E-2 升冪串流不提早停止並納入 `GetRecentRuns`（#27、#28）、`AuditLogStore` 降為不做（#29）、E-3 只改失敗形狀（#30）、F 預設不變只可設定、三值一致、soft heap 設一次（#31、#32）、F 真連線驗收（#33）、G-1 兩個整點案例（#34）、G-2 grep 驗收（#35）、文件同步清單（#36）、回滾與順序（#37、#38）。
不採納：無。審查對 `LatestPerRun` 方向的描述有誤（升冪 `g.Last()` 即最新，倒序首遇也是最新），但其「不新增降冪、不提早停止」的建議因 `GetRun` 退路依賴窗口最小 RunId 而更簡單正確，採納。
複檢完成，無新增事項。

### 第三次複檢（開工前就緒檢查，2026-11-09）
目的：把所有「實作前再查」「假設 API 存在」的點在規劃階段查掉，實作端拿到的每條契約都已核實。

| 項目 | 核實方式 | 結果 |
|---|---|---|
| `System.GC.ConserveMemory` 鍵名、`HeapHardLimitPercent` 行為、`GC.GetConfigurationVariables()` 鍵名 | 獨立 .NET 8 小程式帶同一組 runtimeconfig 實跑 | 鍵有效；讀回 `GCConserveMem=5`、`GCHeapHardLimitPercent=60`、`GCHeapHardLimit=實體×0.6`；`AppContext.GetData` 也讀得到 |
| `GC.GetGCMemoryInfo()` 在第一次 GC 前的值 | 同上 | `HeapSizeBytes`／`TotalCommittedBytes` 皆 0 → 欄位可空，即時堆用 `GetTotalMemory(false)` |
| `SQLitePCL.raw` 三個 API（`sqlite3_memory_used`／`memory_highwater`／`soft_heap_limit64`） | 同上，`Microsoft.Data.Sqlite 8.0.10` | provider 未初始化時擲例外；連線中 `memory_used>0`、關閉後歸零；`highwater` 保留峰值；`soft_heap_limit64` 可設並回前值；`PRAGMA cache_size;` 讀回 `-16384` |
| `Process.PeakWorkingSet64`／`PeakPagedMemorySize64` | 同上 | 可讀 |
| blob 版本公開 API | grep | `EfJsonBlobStore.ReadVersion` 公開；`JsonBlobCollection`／`Singleton` 的版本屬性 protected；`IHostStore` 35 個、`ISentinelStore` 11 個替身 → C-2 改走 `_backend.Blob(...).ReadVersion()`，不動介面 |
| `ScheduleOptions.UpdatedAt`／`SystemSettings.UpdatedAt` | grep | 皆存在且儲存時更新 → C-4 提前解除不需新增儲存鉤子 |
| `EnumerateLines(DateTime? from, DateTime? to)` 簽章 | grep `EfJsonLogStore.cs:251` | 存在；E-2 直接可用 |
| D-3 回傳型別改 `IReadOnlyList` 的波及面 | grep 呼叫端與就地修改方法 | `ResolveActionable` 2 個呼叫端、`Build` 2 個呼叫端；相關檔案無 `RemoveAll`／`Sort`／`Insert`／`RemoveAt` |
| `GetSensorStatuses` 六個呼叫端欄位用法 | grep＋讀 | 各異 → C-2 新增專用投影，不改簽章 |
| `nlog.config` 的 `final` 空規則 | 讀 :56-57 | 存在 → C-1 不以「無 target」當清理判準 |
| `GetRun` 全表退路依賴窗口最小 RunId | 讀 `BatchRunStore.cs:433-441` | 成立 → E-2 不提早停止 |
| `IAiService` 替身數 | grep | 6 個 → C-3 不改介面 |
| 說明書健康章節位置 | grep HelpContent | `12-settings.md`（設定頁含系統健康頁籤） |
| 所有篩選詞對到既有測試類別 | 逐一 grep | 全部命中；新類別只有 `RuntimeConfigGuardTests`、`SqlitePragma*`、`settings.behavior.mjs`／`runs.behavior.mjs` 新案例 |
| `[WindowsFact]` 存在 | grep `LogForesight.Tests/WindowsFact.cs` | 存在 → E-1 平台守門可用 |

就緒結論：A～G 每個子項的現況、定案、改動、驗收都已核實，沒有留給實作端的待查事項；「暫定」只剩四個可調數值（硬上限 60、元素總數上限 200,000、退避 5～15 分鐘、SQLite 三個預設值），調整不改契約形狀。可開分支實作。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 快照讀取點＋快取統計＋健康頁 | Sonnet 5.5 | `7404313a` | 主代理重跑定向 186 通過（含 DI 組裝 `HostDayWorkflowRegistrationTests`、`HealthTabUiTests`）；突變拿掉記憶體 degraded 條件 → 1 案紅 | 無 |
| A-2 執行紀錄里程碑＋OOM＋匯出＋啟動 log | Sonnet 5.5＋主代理微修 | `6db60010` | 主代理重跑定向 427 通過／1 略過；突變拿掉結束標記寫入 → 2 案紅 | ①匯出沒有逐趟清單，記憶體改放新頂層區段 `runMemory`（最多 200 趟，超過寫進 `meta.omitted`）；②`GcSettings` 不另做 DTO，三欄併進 `meta.memory`；③代理版匯出拿不到 SQLite 值，主代理補 `RunExportService` 可選 `sqliteHeap` 委派並由 DI 傳 `StorageBackend.TryReadSqliteHeap`，加測試；④既有 `RunExportServiceTests.Strip` 改為略過 `meta.memory`（即時值每次不同）；⑤取數路徑 OOM 只有 `RunFailureReasons.Describe` 單元測試，`RunAsync` 無替身可擲例外 |
| A-3 前端記憶體卡＋執行詳情＋文件 | Sonnet 5.5 | `49e2f2ec` | 主代理重跑前端定向（UiTests／JsModuleImport／BehaviorTests／ApiTimeout／Help／CspPolicyGuard）692 通過；文件無敘事字眼；說明書按鈕名稱與 Runs.cshtml 一致；瀏覽器實測併入 C-4 後一次做 | 設計判準套 `ui-ux-pro-max` 檢索：表格溢出由既有 `lf-table-wrap` 處理、兩表帶隱藏 caption、警示 badge 帶文字 |
| B GC 設定 | 主代理 | `9eb41d66` | `RuntimeConfigGuardTests` 2 案綠；突變回 `System.GC.Conserve` → 2 案紅；`dotnet build` 後 `bin/Debug/net8.0/LogForesight.Web.runtimeconfig.json` 含四鍵（`net8.0-windows` 目錄是舊目標框架殘留，不更新屬正常）；啟動 log 實測併入 C-4 後的瀏覽器驗證 | README 覆寫說明補「環境變數值為十六進位」 |
| C-1／C-3／C-4 NLog rule、AIService 釋放、AI 退避 | Sonnet 5.5＋主代理微修 | `d6bcb5e8` | 主代理重跑定向 1120 通過；代理突變 C-1（拿掉 rule 移除 → 3 案紅）、C-4（倍增改固定 → 2 案紅）；主代理獨立突變 C-4（退避判定恆假 → 4 案紅） | ①C-1 為了用獨立設定測試，`BatchRunNLogTarget` 改 internal、`Attach`／`Detach` 加可選 `configOverride`；②C-3 只釋放本輪新建的實例（注入的測試替身不釋放）；③`RunsPageUiTests` 既有逐字比對條件陣列的案例改為含 `backoff`；④代理指出 WEB-SPEC AI 閘道段仍寫「整輪共用一個 AIService」（與 CLAUDE.md 定案不符的舊敘述），主代理改為每分片一個、分片結束釋放 |
| C-2 PRTG 快照目標快取 | Sonnet 5.5 | `be5889d2` | 主代理重跑 PRTG 定向（快照、範圍、守門、監控頁、store、每日管線、校準、數值評估）全綠；代理突變「恆刷新＋改回整表讀」→ 9 個新案全紅 | ①`ResolveTargets`、`BackfillScopeSensorsAsync`、`RecentStateChangeScopeContext` 改 internal 供測試；②守門實際只讀 `PrtgResourceGuardEnabled`／`PrtgResourceGuardSensorObjids`／`PrtgUrl` 三欄，指紋即此三欄＋白名單＋hosts／sentinels blob 版本；③補抓後的 `GetAllSensors` 因中間有寫入刻意保留；④主機對應變動仍最慢一小時生效（與原設計「每小時重算」一致） |
| F SQLite 連線層可設定 | 主代理 | `2f974646` | `SqlitePragmaInterceptorTests` 以真實檔案連線讀回 PRAGMA（預設值、設定值、第二條連線）、不合法值點名鍵、軟上限生效；`AppSettingsKeyGuardTests` 綠 | ①鍵守門要求強型別鍵都列在 appsettings.json 範例檔，三鍵改列實際 `null` 值（綁定為留空用預設），原本只寫成註解會紅；②驗證集中在 `SqlitePragmaInterceptor.ResolveMemorySettings` 單一入口 |
| G 附帶 bug | 主代理 | `2f974646` | 新增 `PrtgSnapshotJournalSegmentLimitTests` 4 案；換回修正前 journal → 3 案紅（第 4 案「單一組成超限仍擲」兩版皆綠）；G-2 grep 該檔已無「預設 7 天」 | ①實作時一度把載入時的 snapshot 檢查也放寬到整體上限，突變顯示壓縮會先以 `SnapshotChunks` 切成每段 ≤ 100,000，原檢查本來就正確，已還原，只保留 segment 各組成分別比對的修正 |
| 文件同步（PRTG-SPEC、BACKLOG） | 主代理 | `d10d90f3` | grep 無敘事字眼 | 無 |
| D 兩個大結果快取 | Sonnet 5.5＋主代理微修 | `acbaf5a2` | 主代理重跑定向 210 通過；代理突變拿掉「清非當前版本」→ 3 案紅 | ①命中不複製後兩個既有「回傳副本」測試改為斷言同一參考，主代理順手改名（名稱已與行為不符）；②代理版單筆超限時保留同鍵舊條目，主代理改為一併移除並加案例，避免之後命中與本次查詢不一致的舊結果；③呼叫端只有 `DashboardService.BuildGroupRisk`、`ReportService.BuildIssueOthers` 兩個參數型別改唯讀，沒有任何呼叫端需要 `.ToList()` |
| E 低風險峰值路徑 | Sonnet 5.5 | 見下一個提交 | 主代理重跑合併篩選全綠；主代理突變 E-1（keep 恆放行 → 2 案紅）、E-2（GetRun 永不讀全表 → 3 案紅，含既有保留窗口外查詢兩案） | ①`LatestPerRunStream` 另加 `onEach` 參數累計窗口最小 RunId（不提早停止）；②`LatestPerRun`／`ReadAllRunLines`／`ReadRunLinesFrom` 已無呼叫端，刪除；③E-1 的 `[WindowsFact]` 案在 macOS 略過 |


## 體檢交接

（實作輪結束時填：全量測試數、與基線差、實作方最沒把握的地方）
