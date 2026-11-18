# A：夜間取數主流程盤點（排程觸發 → 每個主機日分析紀錄寫入）

唯讀盤點，分支 feature/feedback-78。所有事實皆附「檔案:行號」，無行號者標「推論」。
縮寫：SCH＝LogForesight.Web/Services/SchedulerHostedService.cs；ORC＝Core/Service/AnalysisOrchestrator.cs；NPS＝Core/Service/NetiqPipelineService.cs；
NSA＝Core/Service/NetiqSourceAttempt.cs；EAR＝Core/Persistence/Sql/EfAnalysisRecordStore.cs；HDP＝Core/Service/HostDayPostProcessor.cs；
LAS＝Core/Service/LogAnalysisService.cs；HWS＝Core/Service/HostDayWorkflowState.cs；EJB＝Core/Persistence/Sql/EfJsonBlobStore.cs；
ICC＝Core/Persistence/IssueCaseCoordinator.cs；ND＝Core/Persistence/Dispatch/NightlyDispatch.cs；DC＝Core/Persistence/Dispatch/DispatchContext.cs；
WOC＝Core/Persistence/WorkOrderCoordinator.cs；BRR＝Core/Service/BatchRunRecorder.cs。

---

## 0. 流程圖（呼叫鏈）

```
SchedulerHostedService.ExecuteAsync（每 60 秒一拍 PollInterval，SCH:24）
 ├ TickAsync → EvaluateScheduleAsync（SCH:~225-300）
 │    └ _batchRunStore.GetRecentRuns(2 天)（SCH:251；補跑判定時再讀 effectiveLimit+1 天，SCH:282）
 │    └ ShouldTriggerNow → TriggerRunAsync(request)（SCH:~490）
 ├ SendDigests（寄信，不在輪詢路徑等待）
 └ RunRetentionIfDueAsync（SCH:~650；每天最多確認一次）

TriggerRunAsync（SCH:~495-610）
 ├ _runState.TryBeginRun（單一執行 gate，SCH:512）
 └ Task.Run → NamedMutexGate.RunExclusiveAsync（跨行程 Global\LogForesight，SCH:530，MutexTimeout 5 秒只用於「確定開始」）
      └ AnalysisOrchestrator.RunAsync（ORC:268）【整趟持有執行鎖，下方全部在鎖內】
           1. new StorageBackend(…, AnalysisMaxPoolSize)（ORC:286；自成連線池，上限＝3×4+2＝14，ORC:259）
           2. RuleBootstrapper.Run、RuleSeedStore.Sync（ORC:289-297）
           3. BatchRunStore／BatchRunRecorder 建立（登記一列 run、掛 NLog target，ORC:~318-326）
           4. hostStore／sentinelStore；SentinelIdBackfiller、NetiqOrphanSweeper、hostStore.Touch(本機)（ORC:~380-430）
           5. suppressionStore.LoadAll（到期通知，ORC:438）
           6. 權限異動檢查 permissionMonitor.Check（本機檔案系統，ORC:~470-560）
           7. RetentionPruner.Run（ORC:589；**循序、同步、在分析開始前**；每段 try/catch）
           8. 讀 system_settings（ORC:614）→ CredentialPreflight → _candidateSource.Build（ORC:634）→ DispatchContext.Build（ORC:656）
              → new WorkOrderCoordinator／NightlyDispatch／HostDayWorkflowService（ORC:~680-700）
           9. 三路並行：
              ├ localTask  = RunLocalAnalysisAsync（本機 1 台，ORC:970；非規模瓶頸）
              ├ netiqTask  = RunNetiqAnalysisAsync（ORC:1265）← 本盤點主體
              └ prtgTask   = PrtgDailyPipeline.RunAsync(…, analysisTask, …)（ORC:752；等本機＋NetIQ 結束後才收尾 workflow 腿，PrtgDailyPipeline.cs:~930-965）
              Task.WhenAll(analysisTask, prtgTask)（ORC:764）
          10. finally → Dispatch.FlushRun（ORC:773）
          11. 體檢（本機，ORC:794）、接續 PRTG 數值回填（ORC:~830）、recorder.Finish（ORC:854）
 ├ finally：_runState.EndRun → **_dataVersion.Bump()**（SCH:580，一趟只此一次）
 ├ _mail.NotifyAfterRunAsync（移出鎖外，SCH:603）
 └ _mail.EnqueueNightlyDispatch、_adminReminders.RunAfterNightly

RunNetiqAnalysisAsync（ORC:1265）
 ├ HostListSelection.FromStore（hostStore.GetAll 複製整份 + sentinelStore.GetAll，ORC:1275；Core/Service/HostListProviders.cs:33）
 ├ NetiqOptionsStore.Get（blob 讀，ORC:~1300）→ 夾回望天數
 └ new NetiqPipelineService(...).RunAsync(hostList, TrendWindowDays=14, ct)（ORC:~1330；TrendWindowDays 見 ORC:234）

NetiqPipelineService.RunAsync（NPS:182）
 ├ ReadAttemptHistoryForResume（帳本，NPS:196→323；只有排程續跑 _resumeRetryDays>0 才讀）
 ├ suppressionSnapshot = _suppressionStore.LoadAll()（整趟一份，NPS:231）
 └ Parallel.ForEachAsync(hostList.ByServer, MaxDegreeOfParallelism=maxParallel)（NPS:239；預設 2、上限 3，NetiqOptions.cs:62,72）
     └ RunServerAsync（NPS:405）→ 依 OS 分 Windows／Linux 組 → RunServerOsGroupAsync（NPS:437）
         【計畫階段】foreach target（循序，NPS:~466）：
            MissingDateFinder.FindWithExistingDates → store.ReadRecent（整列含 ContentJson，NPS:469／HDP:23）
            _hosts.Get(hostId)（NPS:486）
         allDates = 所有計畫缺漏日去重排序（NPS:517）
         clientPool = maxQueries 個 SentinelClient（NPS:524；預設 1、上限 4，NetiqOptions.cs:98,108）
         foreach date（依序，NPS:~530）：
            hostsNeedingDate = plans.Where(p => p.MissingDates.Contains(date))（NPS:535）
            batches = 高流量主機單獨一批 + 其餘 Chunk(IpBatchSize=50)（NPS:32,~538-542）
            Parallel.ForEachAsync(batches, MaxDegreeOfParallelism=maxQueries)（NPS:546）
              └ _guard.WaitIfBusyAsync → RunBatchDayAsync（NPS:584）
                  ├ RecordBatch "running"（NPS:621）
                  ├ client.SearchAsync 串流分桶 eventsByIp（NPS:618-650）
                  ├ 截斷 → 二分遞迴重查（NPS:~680-695）／SetHighVolume（NPS:703,711）
                  ├ foreach plan in batch（**循序**，NPS:~732）：
                  │     SentinelEventMapper.MapAll → AnalyzeHostDayAsync（NPS:755／793）→ Task.Yield（NPS:767）
                  └ 終態 RecordBatch（NPS:781，依狀態分組）
            每台 Sentinel 跑完 FlushHostTouches（NPS:977；hosts blob 整份讀改寫）

AnalyzeHostDayAsync（NPS:793）= 每主機日
 ├ BuildStatisticalRecordAsync（NPS:853→LAS:241）：LogAggregator、ReadRecent(date,14)、規則／趨勢／關聯
 ├ (重跑) plan.Store.DeleteDays（NPS:863）
 ├ plan.Store.Append（NPS:867→EAR:96）：主列＋top_issues 交易、UpsertFirstSeen（EAR:201）
 ├ _workflow.ParentSucceeded（NPS:877）
 ├ HostDayPostProcessor.AttachPrtgFindings（NPS:896；無 finding 時短路）
 ├ _workflow.SetPrtg（NPS:907；僅 PRTG 已發佈該日）
 ├ HostDayPostProcessor.AttachCase（NPS:916→HDP:240）：workflow×3、AttachNewDay、DispatchDay
 ├ HostDayPostProcessor.ReplaceRiskyEvents（NPS:918→HDP:321）
 └ HostDayPostProcessor.RecordPermissionChanges（NPS:920→HDP:~345）
```

---

## 1. 呼叫順序與平行度

| 階段 | 平行性 | 決定者 |
|---|---|---|
| 排程輪詢 | 單執行緒，每 60 秒 | `PollInterval`（SCH:24）；同一趟只能有一個（`TryBeginRun` SCH:512＋跨行程 mutex SCH:530） |
| 前置步驟 1～8 | **全部循序**，且排在三路分析之前 | ORC:286-656（含 RetentionPruner.Run ORC:589，最多單趟刪 5 萬列 `MaxPruneRowsPerRun` EAR:899） |
| 本機／NetIQ／PRTG | 三路並行（`Task.WhenAll`） | ORC:729, 752, 764 |
| NetIQ：跨 Sentinel | 並行 | `MaxParallelServers` 預設 2，硬上限 3（NetiqOptions.cs:62,72；NPS:43 `ResolveParallelism`） |
| NetIQ：單一 Sentinel 內「日」 | **依序**（趨勢比對需前一日已寫入） | `foreach (var date in allDates)` NPS:~530 |
| NetIQ：同一天內的批次 | 並行 | `MaxParallelQueriesPerServer` 預設 1，硬上限 4（NetiqOptions.cs:98,108；NPS:51） |
| 一個批次內的主機 | **循序**，每台後 `Task.Yield` | NPS:~732, 767 |
| 計畫階段（逐主機讀歷史） | **循序**，Sentinel 之間並行 | NPS:~466 foreach |
| DB 連線池 | 分析專用池，上限 `Servers上限3 × Queries上限4 + 2 = 14` | ORC:259, StorageBackend.cs:425（SQL Server 才套用；SQLite 見 §4） |

最壞同時活躍的「批次-日」＝3×4＝12（上限）；預設設定 2×1＝2。每個活躍批次各自循序處理 ≤50 台主機日。

---

## 2. 資料庫往返（單位：每趟／每 Sentinel-OS 組／每主機／每批次-日／每主機日／每事件）

### 2.1 每趟一次
- `StorageBackend` 建構：schema 確認 `EnsureCreated`＋`SchemaUpgrader.Upgrade`＋三個 migrator 的 `Evaluate`（持跨行程 schema mutex）StorageBackend.cs:93-200；**SCH 每個要判定保留清除的拍也會 new 一個 StorageBackend**（SCH:~660 `RetentionPruner` 檢查 `LastRunDate(new StorageBackend(...))`，由 `_retentionConfirmedDate` 擋住每天最多一次，SCH:~650）。
- 規則庫 `RuleBootstrapper.Run`、`RuleSeedStore.Sync`（ORC:289-297）。
- blob 讀：`system_settings`（SCH 的 `ResolveRunSettings` 讀一次 SCH:~625，ORC:614 **又讀一次**，推論：同一份設定重複讀）、`netiq_options`（ORC:~1300）、`hosts`、`sentinels`、`suppressions`＋`issue_owners`（`MuteAwareSuppressionStore.LoadAll`，ORC:438 讀一次、NPS:231 再讀一次）、`rules`、`permission_snapshot`。
- `HostListSelection.FromStore` 被呼叫 **2 次**（憑證預檢 ORC:621、NetIQ ORC:1275），每次複製整份主機清單（JsonBlobCollection.Read() `new(ReadSnapshot())`，JsonBlobCollection.cs:~110）。
- `RetentionPruner.Run`（ORC:589）：RetentionPruner.cs 約 15 段（紀錄 Prune/PruneDetails、執行紀錄、稽核、NetIQ 帳本、權限異動、風險事件、處理狀態/案件/交辦單、報告、PRTG 鏡像…）；`EAR.Prune` 每批 SELECT 主鍵＋2 個 DELETE（EAR:907-955）。
- 帳本歷史 `NetiqSourceAttempt.ReadHistory`：**每個日期 1 次查詢**（含 GroupBy＋self-join，NSA:264-299），範圍 `max(lookback, 7)` 天；只有排程續跑（`_resumeRetryDays>0`）才讀（NPS:323-336）。
- `batchRunStore.GetRecentRuns(PrtgReevaluationPlanner.LookbackDays+1)`（ORC:~727）。
- `DispatchContext.Build`（ORC:656）：profiles／activeOrders／LoadBoard／noise marks／continuity 一次性載入（DC:85-135）。
- `_candidateSource.Build()`（ORC:634；Web 端算候選人快照）。
- `FlushRun`：每張有新增成員的單 `RecordNightlyAppended`（AppendEvent＋UpdateOrder）＋空單結案（ND:279-295）。

### 2.2 每台 Sentinel（每個 OS 組）
- `FlushHostTouches`：hosts blob **整份讀＋整份寫**，`Serializable`（HostStore.cs:234-250 `MutateWithResourceFence`）。呼叫點：每個 Sentinel 跑完（NPS:~432）＋`RunAsync` finally（NPS:~292）。
- `_sentinels.FindByName`（NPS:~250）。

### 2.3 每主機（計畫階段，**不是每主機日**）
- `MissingDateFinder.FindWithExistingDates` → `store.ReadRecent(today-1, lookback)`（HDP:23；EAR:1092-1106）：**1 次 SELECT，載入窗口內所有列的完整 `ContentJson` 並逐列 `JsonSerializer.Deserialize<DailyAnalysisRecord>`**（EAR:1992-2022 的 `Deserialize`）。結果只用來取 `Date`、`LogSource`、`LatestNetiqAttemptStatus`（HDP:25-38）。窗口＝`lookback`（預設 `BackfillDays=1`，NetiqOptions.cs:54），帳本有待重查的主機 `max(lookback, 7)`（NPS:~470）。
- `_hosts.Get(hostId)`（NPS:486）：HostStore 為 cached 集合（HostStore.cs:11），**每次呼叫＝1 次版本探測查詢（ReadVersion）＋對整份清單 O(N) 線性掃描**（HostStore.cs:61；JsonBlobCollection.ReadSnapshot 約 JsonBlobCollection.cs:119-145）。計畫階段 N 台各一次 ⇒ O(N²) 比較、N 次探測。
- （僅手動重跑）`RerunDateFinder.Find`（NPS:~478）。
- 計畫完成後 `HostPlan` 清單、每台的 `ExistingNetiqDates`／`MissingDates` 持有到該 Sentinel-OS 組結束（NPS:~495-515）。

### 2.4 每批次-日（≤50 台）
- 帳本 `RecordBatch`（NSA:63-146）**每次＝1 個 `Serializable` 交易＋全域靜態 `WriteLock`**（NSA:19,74,81）：每 500 台一 chunk：1 次 SELECT（GroupBy max seq＋join，NSA:86-91）、逐台 INSERT／UPDATE、`SaveChanges`；`status != "success"` 時再 `ReadParentMetadataPage`（≥1 次查詢，每 16 列一頁）並對**既有的 NetIQ 父列**逐一 `PatchParentAttempt`（`json_set`／`JSON_MODIFY` 改寫 `content_json` 並 `write_revision+1`，NSA:~150-215）。
  - 呼叫點：`"running"` NPS:621；失敗 NPS:607,648,657,667；終態（依狀態分組）NPS:781；取消 NPS:773,776。⇒ 一個批次-日至少 2 個 Serializable 交易。
  - 重跑日（`hasExistingNetiqRecord`）因為已有 NetIQ 父列，`"running"` 會**真的改寫既有父列 JSON**（推論：因 status≠success）。
- Sentinel 搜尋（外部 HTTP，不計 DB）。

### 2.5 每主機日（固定成本，詳見 §6 表）
見 §6。

### 2.6 每事件
- 無逐事件 DB 往返。事件只在記憶體：`eventsByIp`（逐頁分桶，NPS:618-650）→ `SentinelEventMapper.MapAll`（NPS:~736）→ `LogAggregator.Aggregate`（LAS:~245）。
- `RecordPermissionChanges`（HDP:~345-460）每個符合 EventId 的事件在記憶體建 `PermissionChangeRecord`（含 `RawText` 上限 8000 字，HDP:~470 `RawTextMaxLength`），入庫前先 1 次 `GetDedupeKeysForHost`（HDP:448）、1 次 `AppendChanges`。
- `ReplaceRiskyEvents` 每主機日至多 500 筆（LAS:565 註解；EfRiskyEventStore.cs:22-62）。

### 2.7 「同一份資料在同一趟內被重複讀取」清單
1. `ReadRecent`：計畫階段每台 1 次（窗口 lookback 天，NPS:469），主機日分析又讀 14 天（LAS:276）。兩者讀的是同一張表同一台主機的重疊列，各自完整反序列化。若 `AttachPrtgFindings` 的 `!attachedNow` 分支觸發還有第三次 `ReadRecent(date,1)`（HDP:198）。
2. `hosts` blob：每主機計畫階段 1 次版本探測（NPS:486）；每個主機日 `DispatchDay` 的 `FindByName` 再 1 次探測（ND:78；僅有 unassigned 問題時）；`CapturePrtgEvidence` 也呼叫 `FindByName`（ICC:50）。
3. `issue_owners` blob：`IssueCaseCoordinator.AttachNewDay` **每個有問題的主機日整份讀＋反序列化**（ICC:194 `_issueProfiles.GetAll()`）；`IssueOwnerStore` 是 `JsonBlobCollection<IssueProfile>(blob)` 未開 `cached`（IIssueOwnerStore.cs:~23-33）。另外 ORC:438（LoadAll 內）、NPS:231、DC:Build 各讀一次。
4. 同一主機日的 workflow blob：`ParentSucceeded`／`SetPrtg`／`CaptureDeliveryVersion`／`RecordCaseIntents`／`RecordCaseResults`／`RecordDispatchOutcome` 各自 read-modify-write 同一個 blob（見 §6），PRTG 管線 finally 再對**本趟所有 touched 主機日**做 `Get`＋`SetPrtg`（PrtgDailyPipeline.cs:946-962）。
5. `system_settings`、`suppressions`、`HostListSelection`：見 2.1。

---

## 3. 記憶體：整趟存活的集合與一次載入的整份資料

| 集合／物件 | 位置 | 大小隨什麼成長 | 存活 |
|---|---|---|---|
| `hostList.ByServer`（`NetiqTarget` 清單） | NPS:239 | 主機數 | 整個 `RunAsync` |
| `plans`（`HostPlan`：Store、`MissingDates`、`RerunDates`、`ExistingNetiqDates`、`GroupIds`） | NPS:~495 | 該 Sentinel-OS 組主機數 × 回望天數 | 該組處理完 |
| `_attemptHistory`（`Dictionary<(hostId,date), AttemptHistory>`） | NPS:74,196 | 主機數 × 回望天數（≥7），每個日期上限 10,000 台（NSA:15） | 整個 `RunAsync`（僅續跑） |
| `_hostsWithRetry`（HashSet） | NPS:80,197 | 帳本待重查的主機數 | 整趟 |
| `_startedHostDays`（ConcurrentDictionary） | NPS:82,589 | **本趟所有已送查詢的主機日**（單調成長、不清） | 整趟（`NetiqPipelineService` 每趟新建） |
| `_permissionHostDayClaims`（ConcurrentDictionary<string,byte>） | NPS:93,194 | 有權限異動事件的主機日 | 整趟 |
| `_hostTouches` | NPS:96 | 一台 Sentinel 的已回報主機，flush 後移除（NPS:977） | 短 |
| `suppressionSnapshot` | NPS:231 | 抑制清單 | 整趟 |
| `eventsByIp`（逐批次-日） | NPS:618 | **單 job 事件數上限 `MaxResultsPerJob=100,000`（NetiqOptions.cs:28）**，最多 12 個批次同時活躍；每台映射後從桶移除（NPS:~745）。另外 `mapped`（`List<EventLogEntryData>`）、`events`、`record` 在 `AnalyzeHostDayAsync` 期間存活 | 一個批次-日 |
| `ReadRecent` 回傳的歷史（逐主機日） | LAS:276 | 14 筆完整 `DailyAnalysisRecord` 物件圖（含 TopIssues、各 alert 字串） | 一個主機日分析 |
| `PrtgFindingsRegistry`（`_byDate`、`_suppressedPatternIdsByDate`、`_evaluatedDates`、`_attachGates`） | ORC:~598；PrtgFindingsRegistry.cs:31-35,135 | 天數 × 有 finding 的主機；`_attachGates` 為每個「有追加 finding 的主機日」一個 object，不清 | 整趟 |
| `HostDayWorkflowService._touched`（Dictionary<date, HashSet<hostId>>） | HWS:371,381 | **本趟所有成功寫入的主機日**；僅在 PRTG 管線 finally 對 `days` 釋放（PrtgDailyPipeline.cs:965），不在該 days 內的日期不釋放（推論；服務為每趟新建，趟後釋放） | 整趟 |
| `DispatchContext`：`_profiles`、`_activeOrders`、`_loads`、`_noiseByHost`、`_continuityByHost`、`_chosen`、`_registeredOrders`、`_skipCounts`、**`_casesByHost`（每台首次派工時載入該主機全部案件）**、**`_dismissedByHost`** | DC:16-27,206-215 | 進行中單／案件數；`_casesByHost`／`_dismissedByHost` 隨「有 unassigned 問題的主機數×其案件數」成長，只在 `InvalidateCasesFor`（成員寫入成功）移除該台（DC:195） | 整趟 |
| `NightlyDispatch`：`_addedByOrder`、`_recurrenceByOrder`、`_handlerByOrder`、`_labelByOrder`、`_createdOrders`、`_blockedByOrder`、`_continuedOrders`、`_midrunCounts`、`_unassignable`（問題→主機名集合） | ND:52-64 | 本趟派出的單數、派不出去的（問題×主機） | 整趟 |
| `BatchRunRecorder`：`_outputTail`（最多 500 行）、`_openPhases` | BRR:286,126 | 常數上限 | 整趟 |
| `OrchestratorResult.LocalResults`／`NetiqResult.Warnings` | ORC:96；NPS:1061,1117 | 警告數隨主機數成長（Warnings 無上限，推論） | 整趟 |

**一次載入整份**：
- hosts blob（3000 台約 4 MB，JsonBlobCollection.cs 註解；快取版本探測命中時共用同一份 `CacheSnapshot`，JsonBlobCollection.cs:~119-145）；`Touch`／`SetHighVolume`／`FlushHostTouches` 這類 Mutate 每次反序列化兩份（`before`、`after`）＋序列化一份＋寫回（JsonBlobCollection.cs:~160-170）。
- `issue_owners`（不快取，每個有問題主機日一次）。
- `ReadRecent` 的整個窗口（含 ContentJson）。
- `HostListSelection.FromStore` 兩次。

---

## 4. 鎖與交易

| 鎖／交易 | 位置 | 範圍 | 持有期間做的事 |
|---|---|---|---|
| 跨行程具名 Mutex `Global\LogForesight` | SCH:530；NamedMutexGate.cs:19-60 | **整趟分析** | 全部（分析、派工、體檢、PRTG 回填） |
| `HostDayLocks[64]`（`lock`，依 (hostId,date) 雜湊） | EAR:64,113,238,342,379,407,457,494,689 | 一個主機日寫入；**64 把條帶，不同主機日可能共用同一把** | `Append`：整個交易（SELECT prtgOnly、INSERT 主列、INSERT top_issues）EAR:113-173。`AttachPrtgFindings`：`Serializable` 交易＋反序列化整列＋序列化整列＋SaveChanges（EAR:689-832） |
| `Append` 的交易 | EAR:117（預設隔離） | 主列＋子列 | 3 次往返 |
| `AttachPrtgFindings`／`AttachDailyRiskReport`／`TryClear…`／`AttachAiResultLocked` | EAR:416,464,535,693 | **Serializable** | 讀整列 JSON、改、寫回 |
| `NetiqSourceAttempt.WriteLock`（static `lock`） | NSA:19,74 | **全站所有 Sentinel／批次的帳本寫入串成單線** | 1 個 `Serializable` 交易（SELECT＋INSERT/UPDATE＋父列 patch，NSA:81-146） |
| `NetiqSourceAttempt.Mark` | NSA:322-336 | 借用 `EAR.LockFor` | 讀整列、反序列化、改、序列化、寫（呼叫點待查，見 §8） |
| `HostDayWorkflowStore.Update` 的具名 Mutex `Global\LogForesight.Workflow.{hostId}_{yyyyMMdd}` | HWS:165-190,355 | **每次 Update 都 `new Mutex(false, name)`**（NamedMutexGate.RunExclusive NamedMutexGate.cs:~95-110），逐主機日一個 OS 核心物件 | 內含 `blob.MutateWithContext` |
| `EfJsonBlobStore.MutateWithContext` 的 `lock(_lock)` | EJB:203 | **每次 `backend.Blob(key)` 都 new 一個實例**（StorageBackend.cs:284），`_lock` 是逐實例的，對 workflow 這種「用完即丟」的用法**實際上沒有跨呼叫互斥作用**（推論，互斥靠 OS mutex 與 DB 交易）；對長壽命實例（如 `HostStore` 的 blob）才有行程內序列化 | 讀（`ReadBoundedInContext`：SQLite 3 個查詢 EJB:~118-162）＋`FirstOrDefault` 再讀＋UPDATE |
| `HostStore.MutateWithResourceFence` | HostStore.cs:238-250 | hosts blob 整份讀改寫，`Serializable`，且 `beforeRead` 先檢查 PRTG scope purge 閘 | `SetHighVolume`（NPS:703,711，只在旗標需要變時）、`FlushHostTouches`（NPS:~1010）、`Touch`（ORC:~420） |
| `NightlyDispatch` 的 `lock (_ctx.Gate)` | ND:86,281 | **本機／NetIQ／PRTG 三路共用同一個 Gate** | 整個 `DispatchBatch`：Decide（記憶體）、`EnsureNightlyOrder`、`WriteNightlyMembers`（多次 DB 寫）；FlushRun 全部 DB 寫 |
| `WorkOrderCoordinator.OrderMutationLock`（static object） | WOC:41,93,170,511-554,… | **全行程**交辦單變更（含 Web 端處理人操作） | `EnsureNightlyOrderLocked`、`WriteNightlyMembersLocked`（`GetOpenForHost`、`_orders.Get`×張數、`_cases.SaveMany`、`_issueHandlings.SaveMany`、`_handlingLog.AppendLogs`，WOC:~559-640） |
| `BatchRunStore._lock` ＋ `EfJsonLogStore._lock` ＋ log id allocator | BatchRunStore.cs:408-417；EfJsonLogStore.cs:18,48,131 | 執行紀錄**每一行**（Info 輸出行、Warn 以上 NLog） | `ReserveMonotonicId`＝1 個 blob `MutateWithContext`（SQL Server 另 `sp_getapplock`，EJB:285-302）＋INSERT；每行 2 個 DB 交易 |
| `BatchRunRecorder._rowLock`／`_countLock`／`_outputLock` | BRR:121,125,283 | 計數／輸出緩衝 | 微小；但 `Message()` 在 `_outputLock` 內呼叫 `Append`→DB 寫（BRR:298-306），前 1500 行是**在鎖內做 DB 寫** |
| `PrtgFindingsRegistry.AttachExclusive` | PrtgFindingsRegistry.cs:126 | 逐 (主機,日) | `store.AttachPrtgFindings`（Serializable 交易） |
| SQLite：連線池關閉 | StorageBackend.cs:404-414 | 全站 | 每個 `_dbFactory()` 開 context 即「開實體連線＋PRAGMA（SqlitePragmaInterceptor）」，用完關閉（StorageBackend.cs:~119-125；註解說明此為避免 user-function 競態）。**所以每次 `using var ctx = _contextFactory()` 在 SQLite 上都是一次連線建立**（推論：成本；註解只說明原因，未說明成本） |

---

## 5. 跨步驟耦合

### 5.1 資料版本戳（`DataVersionStamp`）
- 定義：`SummaryCache.cs:14-21`，`long _version`，`Bump()`＝`Interlocked.Increment`，`Current`＝讀值。
- **誰推進**：
  1. `Program.cs:227-232`、`Program.cs:288-300`：任何成功（<400）的非 GET/HEAD HTTP 請求，且端點未貼 `[SkipDataVersionBump]`（排除法）。
  2. 夜間取數**一趟只推一次**，在整趟結束的 finally：SCH:580。**分析執行中完全不推進**（IssueRankingCache 註解與 CLAUDE.md 一致：「背景寫入執行中版本不推進」）。
  3. AI 排程 `AiAnalysisHostedService.cs:361`（每輪結束）；`CaseDaySyncHostedService.cs:80`；`DailyRecordBackfillHostedService.cs:58`；`WorkOrderBackfillHostedService.cs:62`；`HandlingMigrationHostedService.cs:67`；`PermissionChangeMigrationHostedService.cs:104`；`PrtgStructureSyncService.cs:413`；`PrtgRiskReviewController.cs:41`；`TopIssueBackfillHostedService.cs`（引用）。
- **誰消費**（鍵含版本戳＋短 TTL 保底）：`SummaryCache`（SummaryCache.cs:45-60，上限 256 筆）、`IssueRankingCache`（IssueRankingCache.cs:39,54，`maxElements=200_000`）、`ActionableSnapshotCache`（:33,48）、`NextUnhandledSequenceCache`（:31,42）、`IssueExclusionProvider`（:26）、`CaseDaySyncSchedule`（CaseDaySyncSchedule.cs:24，用 `Current` 判斷是否要再跑）。
- **耦合含義**：夜間每個主機日的寫入**不會**使儀表板快取失效；快取要等到整趟結束（SCH:580）或 TTL 才反映。若最佳化改變「推進時機」（例如分段推進），所有上述消費端的新鮮度語意都會變。**反過來，任何最佳化若讓夜間期間 HTTP 非 GET 請求變多，也會使這些快取在夜間被反覆失效**（推論）。
- `PermissionVersionStamp`（Web/Auth/PermissionVersionStamp.cs:42）：夜間流程只在 `NetiqOrphanSweeper.Sweep`（ORC:405，傳入 `_permissionVersionBump`）使用；`FlushHostTouches`／`SetHighVolume` **不**推進它。

### 5.2 `Append` 寫入會連動的對象
- `lf_daily_records`（`ContentJson`＋抽出欄：`Headline／ErrorCount／AiPending／LogSource…`，EAR:132-160）→ AI 排程用 `ai_pending` 欄找待補（`AiAnalysisHostedService.cs:437` `QueryPendingAi`，60 秒輪詢 :20）；`HostDayWorkflowRecoveryHostedService`（15 分鐘一輪，`PollInterval` :18）以 `QueryWorkflowRecoveryPage` 掃近期紀錄與 workflow blob 對帳。
- `lf_top_issues`（EAR:164-172；`MapToTopIssueRow` EAR:863-887，含 `SourceKey`、`SourceObservationsJson`）→ `EfIssueAggregateQuery`（`IIssueAggregateQuery`）讀。CLAUDE.md 規定 `source_key` 回填就緒閘門（`TopIssueBackfiller`）；Append 路徑寫入時一併填 `SourceKey`，因此**不得**改成省略此欄。
- `lf_issue_first_seen`（EAR:268-318 `UpsertFirstSeen`；讀取：`EfIssueAggregateQuery`、`HealthService`、`IssueFirstSeenSeedHostedService`）。
- `lf_risky_events`（`ReplaceDay`，EfRiskyEventStore.cs:22-62；`IRiskyEventStore` 讀端與 `FinalizePendingRiskReportAsync` LAS:~535 取 `QueryDay`）。
- 處理狀態表（`IssueHandling`／`IssueCase`／`RecordHandlingLog`／`WorkOrder*`）經 `AttachCase`。
- workflow blob `workflow_{hostId}_{yyyyMMdd}`（lf_blobs）經 `ParentSucceeded`。
- 夜間結束後：`_mail.NotifyAfterRunAsync`（SCH:603）依本趟結果寄信；`EnqueueNightlyDispatch`（SCH:~612）。

### 5.3 工作流指紋
- `HostDayWorkflowFingerprint.ForParentRecord(record)`（HWS:1068）＝`RecordStorageShaper.Compact(record)` 的投影 ＋ `RecordId`、`LatestNetiqAttemptStatus`、`LatestNetiqAttemptAtUtc`、各計數與 alert 文字、非 PRTG 問題（HWS:1079-1103）。傳入 `ParentSucceeded(…, decisionFingerprint, …, parentRecordId)`（NPS:877）；指紋或 `ParentRecordId` 與既有 state 不同 ⇒ `DecisionVersion++`、`ResetDelivery`、PRTG／AI 腿重設為等待（HWS:374-425）。
- **耦合**：`LatestNetiqAttemptAtUtc` 在 NPS 寫入前由 `DateTime.UtcNow` 設定（NPS:~859），所以同一主機日**重跑**必然得到不同指紋（推論），符合「父紀錄重新分析就重設下游」語意；若最佳化改變 `Compact` 投影的欄位、或改變 attempt 狀態寫入時機，會讓 AI 排程／復原路徑（以資料庫讀回的紀錄再算指紋）與此處不一致。CLAUDE.md 另載明指紋必須與風險等級無關且升級時要認得上一版算法（`LegacyParentFingerprint` HWS:1075）。
- `NetiqSourceAttempt.PatchParentAttempt`（NSA:~200-215）直接以 SQL 改寫既有父列 `content_json` 內的 `LatestNetiqAttemptStatus／AtUtc`（`write_revision+1`）：這會改變復原路徑讀回紀錄所算出的指紋（推論）；也會使其他持有舊 `write_revision` 的讀改寫者（如 `AttachPrtgFindings` 的 Serializable 交易、`TryUpdateForRecovery`）失敗重試。

### 5.4 PRTG 並行耦合
- PRTG 管線等 `analysisTask`（ORC:752）；在它的 finally 對本趟 `ParentHostIdsForDay(day)`（＝NetIQ/本機所有 `ParentSucceeded` 登記的主機）逐一 `workflow.Get` ＋ `SetPrtg`（PrtgDailyPipeline.cs:946-962）⇒ 每個主機日再多 1 次讀＋1 次 blob 讀改寫。
- 兩邊對同一主機日追加 PRTG finding 以 `AttachExclusive`（行程內鎖）序列化（PrtgFindingsRegistry.cs:126）。
- `Append` 遇到同主機日已有「只因 PRTG 建立」的列（`LogSource==Prtg`）會原地更新並再呼叫 `AttachPrtgFindings`＋一次讀回（EAR:121-135,175-198），這是 PRTG 先於 NetIQ 寫入時的額外成本。

### 5.5 NLog → 執行紀錄
- `BatchRunNLogTarget.Write`（BRR:~650）把這趟 async scope 內所有 Warn 以上 log 即時寫進 `batch_run_logs`（每行 `BatchRunStore.AppendLog`，含 id 配發交易）；`console.WriteLine` 經 `RecordingRunConsole`（ORC:145-154）→ `recorder.Message` 前 1500 行即時寫入（BRR:277,298）。NetIQ 路徑每個主機日至少 1 行 console（NPS:~930）。

---

## 6. 每主機日的固定成本（NetIQ 路徑，穩態：新日、無重跑、`_workflow` 非 null、PRTG 啟用）

K＝該日不同 (Source,EventId) 的問題數；U＝掛接後仍未指派的問題數；P＝有 open case 的問題數。
「往返」＝一次資料庫命令；SQLite 上每個 `_contextFactory()` 另需開關實體連線（見 §4）。

| # | 步驟 | 位置 | 讀 | 寫 | 備註 |
|---|---|---|---|---|---|
| 0 | （批次層攤提）`RecordBatch "running"` | NPS:621 → NSA:63 | 1 SELECT（join）＋1 SELECT（父列 metadata，status≠success）| 每台 1 INSERT＋SaveChanges；既有父列再 1 UPDATE | 1 個 Serializable 交易 ÷ ≤50 台；在全域 `WriteLock` 內 |
| 1 | 事件映射 `MapAll`、`LogAggregator.Aggregate`、抑制／趨勢／關聯計算 | NPS:~736, LAS:241-440 | — | — | 純 CPU／記憶體 |
| 2 | `ReadRecent(date, 14)` | LAS:276→EAR:1092 | **1 SELECT，≤13 列完整 ContentJson 反序列化** | — | 無索引以外的限制：`OwnedRows` 條件為 `(HostId==id) OR UPPER(HostName)==name`（EAR:85-93）|
| 3 | `plan.Store.Append`：(a) probe ctx (b) SELECT prtgOnly (c) INSERT 主列 SaveChanges (d) INSERT K' 列 top_issues SaveChanges (e) commit | NPS:867→EAR:96-173 | 1 | 2 次 SaveChanges（主列、子列）| 持 `HostDayLocks` 條帶鎖＋交易；另 `JsonSerializer.Serialize(shaped)`、`RecordStorageShaper.ForStorage`（EAR:98） |
| 4 | `UpsertFirstSeen`：每個不同 (Source,EventId) 開新 ctx：`Any` ＋ （已存在→1 條件式 UPDATE；不存在→INSERT＋SaveChanges） | EAR:201,268-318 | **K 次** | **K 次** | 即 ≥2K 次往返，每次獨立 ctx／連線；在主交易**之外**、且已釋放主機日鎖 |
| 5 | `_workflow.ParentSucceeded` | NPS:877→HWS:374,165 | blob：probe ctx＋有界讀（SQLite 3 查詢／SQL Server 1 查詢）＋`FirstOrDefault` 再讀 = **2～4 查詢** | 1 UPDATE／INSERT blob | `new Mutex` 具名 OS 物件＋Mutate 交易；另計 `ForParentRecord`（`Compact`＋序列化＋SHA256，NPS:878） |
| 6 | `AttachPrtgFindings` | NPS:896→HDP:~120 | 無 finding 時 0（`registry.For` 記憶體，短路）| 0 | 有 finding：Serializable 交易讀整列、反序列化、序列化、寫整列＋top_issues（EAR:689-832） |
| 7 | `FinalizeNonAiReportAfterPrtgAttachmentAsync`（僅 `!needsAi` 且風險 actionable） | NPS:899→LAS:~455-500 | `LoadSuppressions`（snapshot，0）；報告產生；`AttachDailyRiskReport` Serializable 交易（EAR:416） | 報告全文 `lf_reports` 寫＋主列更新 | 低風險日 `GenerateReportIfActionable` 回 null → 不寫 |
| 8 | `_workflow.SetPrtg`（僅 PRTG 已發佈該日，通常 PRTG 管線較晚才發佈） | NPS:907→HWS:494 | 2～4 | 1 | 穩態多半跳過，由 §9 finally 補 |
| 9 | `AttachCase`：`CaptureDeliveryVersion`（`store.Get`） | HDP:~246→HWS:782 | blob 有界讀 1～3 查詢 | — | |
| 10 | `RecordCaseIntents` | HDP:256→HWS:575 | 2～4 | 1 | **即使 K＝0 也執行**（`expectedCaseKeys` 空陣列仍呼叫；HDP:256 條件只看 `hasWorkflow`） |
| 11 | `AttachNewDay`（`issues.Count==0` 時整段早退，ICC:170） | HDP:265→ICC:168 | `GetOpenForHost` 1（ICC:172）、`GetForDay` 1（ICC:183）、`issue_owners` 整份 blob 1（ICC:194，**不快取**）| 每個掛入問題：`AppendLog`（鎖＋讀 log 尾端＋INSERT，EfRecordHandlingStore.cs:121-135；ICC:220）；`SaveMany(toSave)` 1～2 次；`ReloadCasesForAttach`：每個 case `_cases.Get` 1＋`SaveMany` | P=0 時只有前三個讀 |
| 12 | `RecordCaseResults` | HDP:~270→HWS:612 | 2～4 | 1 | 同 #10，**K＝0 也執行** |
| 13 | `DispatchDay`（U＝0 早退，ND:76） | HDP:286→ND:73 | `_hosts.FindByName`（版本探測 1＋O(N) 掃，ND:78）；`CasesFor` 首次 1 次讀該主機全部案件（DC:206）；`ActiveOrdersFor`/`Decide` 記憶體 | 持 `_ctx.Gate`＋`OrderMutationLock`：`EnsureNightlyOrder`（建單）、`WriteNightlyMembersLocked`：`GetOpenForHost` 1、`_orders.Get`×張數、`SaveMany` 案件、`SaveMany` 逐日列、`AppendLogs`（WOC:559-640）；每個 outcome `RecordDispatchOutcome`＝1 次 workflow Update（HWS:633） | 只有 U>0 才發生 |
| 14 | `ReplaceRiskyEvents`（`WithinRetention` 才做） | NPS:918→HDP:321→EfRiskyEventStore.cs:22 | — | probe ctx＋交易：`ExecuteDelete`（1）＋INSERT ≤500 列 `SaveChanges`（1） | 每主機日 ≥2 往返，即使 0 筆事件也 `ExecuteDelete` |
| 15 | `RecordPermissionChanges`（Windows 且 events>0 且有 4728/4732/…/4907） | NPS:920→HDP:~345 | `GetDedupeKeysForHost` 1；（彙總門檻時 `UpsertByDedupeKey`／`DeleteByDedupeKeys`／`ExistsByDedupeKey`）| `AppendChanges` 1 | 無相關事件時 0 往返 |
| 16 | console 輸出／Warn | NPS:~925-935 | — | 每行（前 1500 行）：id 配發 blob 交易＋INSERT；NLog Warn 同 | 一個主機日至少 1 行 |
| 17 | `Task.Yield` | NPS:767 | — | — | 每台一次排程往返 |
| 18 | （批次層攤提）終態 `RecordBatch` | NPS:781 | 1～2 | 每台 1 | 同 #0 |
| 19 | （趟末）PRTG finally：`workflow.Get` ＋（`Parent==Succeeded` 時）`SetPrtg` | PrtgDailyPipeline.cs:946-962 | 1 Get ＋ 2～4 | 1 | 每個本趟 touched 主機日 |

**小結（穩態、K 個問題、U＝0、無 open case）**：每主機日 DB 命令約為
`2（歷史讀）＋3（Append）＋2K（首見日）＋~3×3（workflow：ParentSucceeded/CaseIntents/CaseResults；每次 2～4 查詢＋1 寫）＋3（CaptureDeliveryVersion）＋2（ICC 前三讀＋issue_owners）＋2～3（RiskyEvents）＋1～2（權限異動）＋趟末 PRTG 對帳 3～5＋帳本攤提 ≈ 2`，
即**每主機日約 25～40 個 DB 命令（K≈3 時）**；其中 workflow（#5、#9、#10、#12、#19）約佔 15～20 個、且每次伴隨一個 OS 具名 Mutex。K、U、P、ParentSucceeded 的 SQLite 3 查詢細節為上述程式碼的直接計數；總數為加總估算（推論）。
3000 台×1 日＝約 7.5～12 萬個 DB 命令／夜；若回補 D 天則乘 D，且 `ReadRecent` 的反序列化量乘 14。

---

## 7. 最佳化候選

每條格式：現況／成長／等價改法／影響面／風險。

### C1　計畫階段只為了「哪些日子有紀錄」而整列反序列化 `ContentJson`
- **現況**：`NPS:469`→`HDP:23` `store.ReadRecent(today-1, lookbackDays)`→`EAR:1092-1106` 載入含 `ContentJson` 的完整列並 `Deserialize`（EAR:1992-2022）。呼叫端（HDP:25-53）只用 `Date`、`LogSource`、`LatestNetiqAttemptStatus`、（`requireAi` 時）`AiPending／AiAnalyzed／RiskLevel`。
- **成本隨**：主機數 × 回望天數（`lookbackDays`：預設 1；帳本有待重查主機 7 天；使用者設到最多 365/保留天數時等於 3000×D 列完整反序列化）。
- **等價改法**：新增輕量讀取（只投影 `RecordId/HostId/RecordDate/LogSource/AiPending/AiAnalyzed/RiskLevel` 抽出欄＋`LatestNetiqAttemptStatus`）。注意 `LatestNetiqAttemptStatus` 目前只存在於 `ContentJson`（NSA:~190 `json_set` 直接改 JSON；`DailyRecordRow` 抽出欄清單見 EAR:132-160 無此欄）——需先確認是否有抽出欄或改用 SQL JSON 函式取值，這是此候選的主要不確定點。
- **影響面**：`MissingDateFinder.FindWithExistingDates` 呼叫端：`NPS:469`、`NPS:366`（`MissingHostDays`）；`MissingDateFinder.Find`：`ORC:995`（本機）。`IAnalysisRecordReader` 介面（`IAnalysisRecordStore.cs:4`）若新增方法，所有實作與測試替身都要補；測試專案裡 `FindWithExistingDates` 的斷言（未盤點）。`HDP:20` 的 `NeedsBackfill` 與「只補跑失敗或未執行」三處判定共用（HDP 註解 ~:100），語意不得分岔。
- **風險**：中。`ParentNeedsRetry(LatestNetiqAttemptStatus)` 是排程續跑的判定依據（NPS:30 起、HDP:32,~100），欄位來源改變若不一致會讓待補判定靜默漏判；`DetailPruned` 列的 `Deserialize` 回傳的是只含抽出欄的 stub（EAR:1996-2014），新投影需沿用此語意。

### C2　主機日分析的趨勢歷史 `ReadRecent(date, 14)` 每主機日重讀 ≤13 列完整 JSON
- **現況**：`LAS:276`（另 `LAS:591` AI 補跑路徑、`WeeklyCheckupService.cs:84,116`、`HDP:198`）；每主機日 1 次 SELECT＋≤13 次反序列化。
- **成本隨**：主機日數 × 14（回補 D 天 ⇒ 主機數×D×14）。NPS 同一台主機的下一天（回補多日時）窗口與前一天幾乎完全重疊。
- **等價改法**：回補多日時在同一台主機的 plan 內保留「已讀入窗口」滑動（僅補讀新寫入的那一天）；或趨勢分析只投影需要的欄位。需先確認 `TrendAnalyzer`／`SlowTrendAnalyzer`／`CorrelationAnalyzer`／`ResidualCredentialDetector` 各自讀哪些欄位（未盤點，見 §8）。
- **影響面**：`TrendAnalyzer.Apply`、`SlowTrendAnalyzer.Apply`、`CorrelationAnalyzer.Detect`（跨日比對）、`ResidualCredentialDetector.Mark`、`BuildUncoveredChecks` 的 `history.Count` 判定（LAS:~380 `fullHistoryDays`）全部吃同一份 `history`；本機路徑（`ORC` 的 `RunLocalAnalysisAsync`）與 AI 補跑（LAS:591）也用 `ReadRecent`。快取跨主機日會引入「回補中途寫入的新日需要立刻進窗口」的時序要求（同台主機日依序處理，NPS:~530）。
- **風險**：高（分析判定核心；快取失效時機錯誤會讓 New/Rising 判定失真）。

### C3　`UpsertFirstSeen` 每個不同問題一次獨立 context、≥2 往返
- **現況**：`EAR:268-318`，`foreach (source,eventId)`：`using var ctx` ＋ `Any` ＋ UPDATE 或 INSERT＋SaveChanges；位於 `Append` 主交易之外（EAR:201）。
- **成本隨**：主機日 × K（每日不同問題數）。
- **等價改法**：每主機日一次 `IN`/join 查已存在的 (source_key,event_id) 集合，再批次 UPDATE／INSERT；保留「INSERT 撞鍵改走較早日期更新」與「失敗不拖垮已寫入主機日」的語意（EAR:289-317）。
- **影響面**：消費端 `EfIssueAggregateQuery`、`HealthService`、`IssueFirstSeenSeedHostedService`、`TopIssueBackfiller`；CLAUDE.md 的 K 來源鍵就緒閘門（`lf_issue_first_seen` 舊鍵重整）、索引完整鍵序列；平行 Sentinel 同時第一次寫同一新問題的競態（EAR:262-266 註解）。
- **風險**：中。批次化後唯一鍵衝突的粒度改變，需保留逐筆降級。

### C4　`HostDayWorkflowStore.Update`／`Get` 每次一個具名 Mutex＋多次 blob 讀＋寫，每主機日 5～7 次
- **現況**：`HWS:165-190`（Update）、`HWS:115`（Get）；呼叫序列見 §6 #5、#9、#10、#12、#19；每次 `new NamedMutexGate("Global\\LogForesight.Workflow.{id}_{date}")`（HWS:355-356）＋`backend.Blob(...)` 新實例（StorageBackend.cs:284）＋`MutateWithContext`（EJB:192-282）。
- **成本隨**：主機日數 × 5～7（含 K＝0 的主機日：#10、#12 不受 K 影響，HDP:256）。
- **等價改法（候選，語意需確認）**：對同一主機日把 `RecordCaseIntents`＋`RecordCaseResults`（無 unassigned、無 open case 時結果確定）合併為一次 Update；`CaptureDeliveryVersion` 的 `Get` 與緊接的 Update 合併為單一 Mutate；趟末 PRTG 對帳（PrtgDailyPipeline.cs:946-962）對「狀態不變」的主機日略過寫入（`skipUnchangedContent` 參數 EJB:~197 已存在但 `Update` 未使用，HWS:165-190）。
- **影響面**：`HostDayWorkflowService` 全部呼叫端：`NPS`（877,907,~950）、`HDP`（256,270,286,AttachCase）、`PrtgDailyPipeline`（946-962 及其它）、`AnalysisOrchestrator`（`RunLocalAnalysisAsync` 本機路徑 `workflow`）、`AiAnalysisHostedService`、`HostDayWorkflowRecoveryHostedService`、`RecordDetailQueryService`、`MailNotificationService`、`RunMonitorService`、`HealthService`、`RiskReportService`、`LogAnalysisService`（上面 grep 列出的 14 個檔案）。`DecisionVersion`／`MatchesVersion`（HWS:808）是郵件、AI、案件三條腿共用的併發權杖；`CaptureDeliveryVersion` 的結果用於 `RecordCaseIntents/Results/DispatchOutcome` 的 `expectedVersion`（HDP:246-300）。
- **風險**：高。這是交付腿狀態機，`HostDayWorkflowRecoveryHostedService` 以資料庫為權威重建並與 workflow blob 對帳；合併更新後中途失敗的狀態形狀需與現行相容。CLAUDE.md「工作流父指紋」守則。

### C5　`IssueCaseCoordinator.AttachNewDay` 每個有問題的主機日整份讀 `issue_owners`
- **現況**：`ICC:194` `_issueProfiles.GetAll()`（`IssueOwnerStore : JsonBlobCollection<IssueProfile>`，`IIssueOwnerStore.cs:23-33`，未 `cached`）。
- **成本隨**：有問題的主機日數 × issue_owners blob 大小（問題檔案數，含 Mutes）。
- **等價改法**：與 `HostStore` 同模式開 `cached: true`（版本探測＋快照），或趟首載一次傳入（`DispatchContext._profiles` 已有同資料，DC:16,85-95，但鍵不同且只含部分欄位）。
- **影響面**：`IssueOwnerStore` 所有建構點（`new IssueOwnerStore(backend.Blob("issue_owners"))` 於 ORC:~340,~654；Web 端 DI），`MuteAwareSuppressionStore`（ORC:~327）、`IIssueOwnerStore.GetAll` 全部呼叫端；快取回傳共用物件參考，呼叫端不得修改（JsonBlobCollection.cs:~113 註解）——需逐一確認 `GetAll` 呼叫端不就地修改物件。
- **風險**：低～中。

### C6　`RecordBatch` 帳本：每批次-日 ≥2 個 Serializable 交易＋全域靜態鎖
- **現況**：`NSA:19,74,81`；呼叫點 NPS:607,621,648,657,667,773,776,781,811,950。
- **成本隨**：批次-日數（≈主機日 ÷ 50）；另 `AnalyzeHostDayAsync` 的早退分支與失敗分支逐台單獨呼叫（NPS:811,950），之後 `terminalAttempts` 再群組呼叫一次（NPS:781）。
- **等價改法**：`"running"` 回執與終態回執合併為一次寫入（需確認 `"running"` 是否有人在趟中途讀；`ReadHistory` 只在趟首讀，NPS:196）；移除全域 `WriteLock` 前須確認 Serializable 交易已足以處理並行（註解 NSA:123-126 說「process-only host lock can't cover other workers」）。
- **影響面**：`NetiqAttemptStatus`（`LedgerNeedsRetry`／`CountsAsAttempt`／`CountsAsTruncated`／`ParentNeedsRetry`／`AutoRetryExhausted`，CLAUDE.md 指定唯一判定點）、`RunResume`／`BatchRunRecorder.CurrentResume`／`RecordNetiqOutcome`、`HostDayPostProcessor.NeedsBackfill`（HDP:~100）、`ScheduleCalculator.EmptyRecheckGap` 的 `LastAttemptAtUtc` 語意、`PatchParentAttempt` 對父列的 disqualify 契約（NSA:~125-146，註解「success receipt never qualifies an old parent」）。
- **風險**：高（排程續跑與停止／失敗的重試語意，帳本單一主機日只留一列）。

### C7　`BatchRunStore.AppendLog` 每行一次 id 配發交易＋INSERT，且 Output 前 1500 行在 `_outputLock` 內同步寫庫
- **現況**：`BRR:298-306`、`BatchRunStore.cs:408-417`、`EfJsonLogStore.cs:48-57,131-139`、`EJB:285-302`。
- **成本隨**：console 行數（每主機日 ≥1 行，上限 1500 行即時寫＋之後 500 行尾端緩衝）；Warn 以上 log 條數（無上限，隨失敗主機日／警告數成長）。
- **等價改法**：以 `AppendLines`（EfJsonLogStore.cs:143 已存在）批次緩衝＋時間／筆數 flush；id 一次保留一段。
- **影響面**：`BatchRunStore.AppendLog` 呼叫端（`BatchRunRecorder.Append`、`BatchRunNLogTarget.Write`、`CloseUnfinishedRuns`）、執行監控頁／診斷匯出讀 `GetLogs`（依 `LogId` 排序，BatchRunStore.cs:496）、站台重啟時中斷執行的關閉（SCH:`CloseInterruptedRuns`）、NLog target 在 `ConfigLock` 下 Attach／Detach（BRR:565-632）與「執行紀錄靜默變成沒有警告」的紅線（CLAUDE.md）。
- **風險**：中。批次緩衝若行程崩潰會遺失最後幾行（目前每行即時落盤）。

### C8　計畫階段逐主機 `_hosts.Get` ＝版本探測＋O(N) 線性掃描
- **現況**：`NPS:486`、`HostStore.cs:61`；（同模式 `FindByName` HostStore.cs:63 在 `ND:78`、`ICC:50` 每主機日）。
- **成本隨**：N（主機數）×（探測 1 次 ＋ 掃 N）＝O(N²)。3000 台約 450 萬次比較／趟、3000 次探測；`ND:78` 每個有 unassigned 的主機日再 1 次探測＋掃描。
- **等價改法**：計畫階段呼叫前先把 `hostList` 已有的資料（`NetiqTarget` 不含 `GroupIds`／`IsHighVolume`，見 `HostListSelection.FromStore` HostListProviders.cs:52）擴充帶入，或 `FromStore` 時建 `Dictionary<hostId,WebHost>`；`FindByName` 加字典。
- **影響面**：`NetiqTarget` record 欄位被 `NetiqHostList`、`HostListSelection`、`PrtgDailyPipeline`、`AnalysisOrchestrator` 憑證預檢（ORC:621-629）使用；`IHostStore.Get/FindByName` 全部呼叫端；HostStore 註解（HostStore.cs:~58-60「呼叫點在逐主機迴圈裡…」）顯示此處已做過一次最佳化（A5）。
- **風險**：低。

### C9　`FlushHostTouches` 與 `SetHighVolume`：hosts blob 整份讀改寫（Serializable）
- **現況**：`NPS:977-1030`、`NPS:703,711`、`HostStore.cs:154-162,234-250`；每 Sentinel 跑完 1 次＋`RunAsync` finally 1 次；`SetHighVolume` 只在旗標需翻轉時（`!IsHighVolume` 才寫，NPS:~700；清除須量 <50%，NPS:~708）。
- **成本隨**：Sentinel 數（個位數）＋高流量旗標翻轉的主機數。每次＝反序列化 2 份＋序列化 1 份＋寫入 4 MB（3000 台）。
- **等價改法**：`SetHighVolume` 累積到 `_hostTouches` 同一次 flush；目前成本已被 NPS 註解（NPS:960-975）設計為個位數次，**優先度低**。
- **影響面**：`PrtgScopePurgeStore.EnsureScopeMutationSlotFree`（HostStore.cs:~245 `beforeRead`）、hosts blob 跨行程（Web 端 `HostAdminService` 寫同一份）。
- **風險**：低，但收益小。

### C10　`plans.Where(p => p.MissingDates.Contains(date))` 與 `allDates` 為 O(日數×主機數×每台缺漏天數)
- **現況**：`NPS:517,535`（`List<DateTime>.Contains`）。
- **成本隨**：回補 D 天、N 台：N×D×D 次比較（3000×120×120≈4300 萬／Sentinel-OS 組，CPU 小，僅回補大量日時有意義）。
- **等價改法**：預先建 `date → plans` 字典。
- **影響面**：僅 NPS 內部（`HostPlan` private record，NPS:1041）。
- **風險**：低，收益小。

### C11　`RetentionPruner.Run` 在分析前同步執行、單趟最多 5 萬列；`Prune`／`PruneDetails` 的 `OwnedRows` 條件含 `UPPER(HostName)`
- **現況**：`ORC:589`（循序、阻擋之後所有分析）；`EAR:85-93`（`OwnedRows` 對 `_ownerHost==null` 時直接 `AsQueryable`，無 OR 條件——`RetentionPruner` 使用 `allHostRecords`（RetentionPruner.cs:79）＝不帶 owner）。
- **等價改法**：與取數並行或延後；但其與 `DeleteDays`／`Append` 的寫入競爭（SQLite 單寫入者）需評估。
- **影響面**：保留期語意（`EfIssueCaseStore.Expired`、DismissedCases 保留，CLAUDE.md「不要另寫不再打擾判定」）、`RetentionPruner` 各段 try/catch 失敗語意、`SCH:RunRetentionIfDueAsync` 的「24 小時內有取數就不單獨清」邏輯。
- **風險**：中。**此項是否構成瓶頸沒有證據（見 §8）。**

---

## 8. 不確定或沒讀到

1. **`RecordStorageShaper.ForStorage`／`Compact`** 的實際成本與保留欄位沒讀（只知道低風險日寫入時精簡，HWS:1060-1068 註解、EAR:98）。
2. **`DailyRecordRow` 抽出欄是否含 `LatestNetiqAttemptStatus`**：EAR:132-160 的欄位清單中沒有；C1 的可行性取決於此。`LfDbContext` 的欄位與索引沒讀。
3. **索引**：`lf_daily_records` 在 `(host_id, record_date)`、`lf_top_issues`、`lf_risky_events`、`lf_issue_first_seen` 的實際索引沒讀（DB-SPEC／LfDbContext／SchemaUpgrader），因此 `OwnedRows` 的 `(id!=0 && HostId==id) || UPPER(HostName)==name`（EAR:85-93）是否讓 `ReadRecent`／`HasRecord`／`DeleteDays` 退化為掃描**未驗證**（推論：OR＋函式欄位通常使索引失效，但 NPS 路徑的 `RecordStore(hostKey)` 兩者皆有值）。
4. **`TrendAnalyzer`／`SlowTrendAnalyzer`／`CorrelationAnalyzer`／`ResidualCredentialDetector`／`LogAggregator`／`SentinelEventMapper.MapAll`** 的內部配置量與時間沒讀；`SentinelClient.SearchAsync` 的分頁與記憶體（NPS 註解說串流分組，`PageObserver`＋`StreamOnly`）也沒讀。
5. **`NetiqSourceAttempt.Mark`**（NSA:322）的呼叫端沒追（只看到定義）。
6. **`SqlPerformanceMonitor` 實際量測值**沒有（沒有執行資料，只有程式碼）；本報告所有「成本」皆為次數／形狀，未有時間。
7. **`PrtgDailyPipeline`**（本盤點只讀了 finally 與介面，約 960 行）與 `PrtgResourceGuard.WaitIfBusyAsync` 的阻塞行為沒讀完；PRTG 對夜間 NetIQ 的反壓（`WaitIfBusyAsync` NPS:~549）可能是並行度的實際瓶頸，未驗證。
8. **本機路徑 `RunLocalAnalysisAsync`**（ORC:970-1262）只讀了入口；本機 1 台，視為非規模瓶頸。
9. **`EfIssueHandlingStore.GetForDay/SaveMany`、`EfWorkOrderStore`、`EfPermissionChangeStore`（`PermissionChangeStore`）、`EfReportStore`** 的查詢形狀只憑呼叫端推斷，沒讀實作。
10. **`AiAnalysisHostedService`** 在夜間與取數並行時對同一資料表的競爭（它「與取數排程互不互斥」，AiAnalysisHostedService.cs:15）沒有量化；`QueryPendingAi` 的 SQL 形狀未讀。
11. **`HostDayWorkflowRecoveryHostedService`**（15 分鐘一輪）只讀了輪詢結構；其與夜間寫入的實際鎖競爭未驗證。
12. **`RuleBootstrapper.Run`／`RuleSeedStore.Sync`／`CredentialPreflight`** 的資料庫成本沒讀。
13. 「每主機日約 25～40 個 DB 命令」為加總估算；K、U、P 與 SQLite／SQL Server 的實際命令數差異（例：有界讀 `ReadBoundedInContext` 在 SQLite 為 3 個查詢、SQL Server 為 1 個，EJB:~118-180）會使實際數字浮動。
14. 未讀 `SchedulerRunState`／`WebRunProgress`（每次 `progress.Report` 的鎖與成本，NPS 每主機日呼叫 1～2 次）。
