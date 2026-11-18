# B-prtg：PRTG 整條路徑盤點（夜間每日路徑＋常駐背景服務）

範圍：唯讀盤點，分支 feature/feedback-78。所有事實附 `檔案:行號`（相對 repo 根目錄）；「推論」「未實測」會明講。
簡寫：Pipeline＝`LogForesight.Core/Service/PrtgDailyPipeline.cs`；Fetch＝`.../PrtgFetchService.cs`；Store＝`LogForesight.Core/Persistence/Sql/EfPrtgStore.cs`；Snap＝`LogForesight.Web/Services/PrtgSnapshotHostedService.cs`。
規模假設：3000 台主機、PRTG 上萬顆感測器、保留 180 天（`SystemSettings.DefaultPrtgRetentionDays=180`，`LogForesight.Core/Models/SystemSettings.cs:441`）。

---

## 0. 流程圖（文字縮排）

### 0.1 夜間每日路徑 `PrtgDailyPipeline.RunAsync`（與本機／NetIQ 並行，Pipeline:34）

```
RunAsync(days = 由近到遠；正常夜＝昨天 + 最多 7 天補評估；手動回望最多 retention 上限)   [AnalysisOrchestrator.cs:1519-1573, PrtgReevaluationPlanner.LookbackDays=7]
├─ 讀 system_settings（new SystemSettingsStore，Pipeline:114）
├─ 資源守門自動偵測 EnsureAsync（Pipeline:129；可能 GetAllSensors 整表，PrtgResourceGuardAutoDetect.cs:73）
├─ new PrtgOperationScope(...Read 完整柵欄) ＋ Checkpoint（Pipeline:153-157）
├─ new PrtgClient；client.OperationCheckpoint = Checkpoint（Pipeline:158-159）
├─ 讀監看政策 blob（Pipeline:161）、hostStore.CapturePrtgSnapshot() 並把全部主機轉 WebHost 字典（Pipeline:162-163）
├─ 階段 0：手動作業閘（同步／回填進行中就等，Pipeline:176-211）
├─ 階段 1-3：整趟「一次」fetchService.FetchDayAsync(newest, …, stateChangesFrom=oldest-1)（Pipeline:246-282）
│    ├─ 階段 1 裝置全量同步 FetchDevicesAsync（Fetch:275，page=5000，批次 500 upsert）
│    ├─ 清除過期裝置 DeleteDevicesNotSyncedSince（Fetch:310）
│    ├─ ResolveScope → scopeProvider：
│    │     PrtgHostMapper.MapForDate(newest)（Pipeline:259；整份替換當日對應）
│    │     PrtgScopeDevices.Compute（Pipeline:272）
│    ├─ 階段 2 感測器：範圍裝置 ≤500 逐台、>500 全站分頁（Fetch:362-393）
│    ├─ 清除過期感測器 DeleteSensorsNotSyncedSince（Fetch:465）、ApplyAutoCategories（Fetch:486）
│    └─ 階段 3 狀態變更：逐裝置 id=<objid> 分頁（Fetch:511 → 1023-1243）
├─ 寫夜間同步狀態 blob（Pipeline:334-354）
├─ 自動範圍外清除 PrtgScopePurge.RunAfterStructureSync（Pipeline:357-375；先再 Compute 一次，Pipeline:367）
├─ 載入整份 sensor 鏡像 GetSensorStatuses／全部裝置 GetAllDevices／全部抑制（Pipeline:395-420）
├─ 【第一段】for 每個回望日（Pipeline:432-516）
│    ├─ Checkpoint（434）
│    ├─ ResolveHostMapRows：讀該日整份對應（448）
│    ├─ GetStateChanges(day-1, day+1)（465）→ PrtgRuleEvaluator.Evaluate（468）→ 歸戶（488-503）
├─ 數值就緒度 PrtgValueReadiness.Compute（只算 newest；Pipeline:520-537）
├─ 磁碟趨勢（Pipeline:541-580）→ 記憶體／CPU／Ping 趨勢三條（Pipeline:583-642）：逐規則各跑一遍 100 顆一頁的 AssessPage
├─ 跨日歷史一次查詢 GetPrtgFindingHitDates（Pipeline:646-670）
├─ 【第二段】for 每個回望日（Pipeline:673-794）
│    ├─ Checkpoint（675）、跨日標註、BuildSteadyEvidence（711）、逐主機抑制（718-727）、Publish（729）
│    └─ for 每台有 finding 的主機：Checkpoint（743）→ AttachPrtgFindings／AppendPrtgOnlyIfAbsent（748/756）→ AttachCase（753/760）
├─ 觸發式數值取數（保守策略略過；Pipeline:800-888）：for 每個回望日 PrtgTriggeredValueFetcher.RunAsync（851）
└─ finally：補發佈空結果、workflow 對帳（逐日×逐 touched 主機 Get＋SetPrtg，Pipeline:943-964）、寫 PrtgDayStat（983-1013）
```

### 0.2 常駐背景服務

```
PrtgSnapshotHostedService（BackgroundService，輪詢 60 秒，Snap:70,291-360）
 └─ 每 tick：TickAsync（Snap:481）
      ├─ settings.Get、RestoreJournal、FlushAccumulator(整點前結算)、各種略過判斷
      └─ 距上次嘗試 ≥ 策略間隔（保守 15 分／激進 5 分，PrtgFetchStrategy.cs:12-13）才執行：
           new PrtgOperationScope(...ReadRawSemantic)（Snap:552, 457-479）
           ├─ ReconcileRecentStateChangesForCurrentScope（Snap:558, 995）
           ├─ ExecuteSnapshotAsync → ResolveTargets → FetchFilteredAsync（每 100 顆一批、最多 3 批並行，Snap:639-767）
           ├─ BackfillScopeSensorsAsync（PrtgScopeDevices.Compute＋最多 50 台補抓，Snap:579-583, 891-993）
           └─ FetchRecentStateChangesAsync → Drain（每 tick 最多領 50 個佇列項目，Snap:586, 1035-1132）
PrtgRiskReviewHostedService：啟動後 25 秒起，每批 100 筆主機日，直到無待處理（PrtgRiskReviewHostedService.cs:11-34）
主機異動重算：PrtgHostMapRefresher.TryRefreshToday（呼叫端 HostAdminService:415/693/719、NetiqHostService:257、PrtgHostMappingAdminController:265/382/411/478/515）
```

---

## 1. 呼叫順序：每天重做 vs 整趟一次

| 步驟 | 頻率 | 位置 |
|---|---|---|
| 結構同步（裝置、感測器）＋狀態變更抓取 | **整趟一次**（區間 oldest-1 ～ 今天） | Pipeline:246-282；`FetchDayAsync` 的 `stateChangesFrom` 在 Fetch:502 |
| 主機對應重算 `MapForDate` | **整趟一次，只對 newest** | Pipeline:259 |
| 範圍計算 `PrtgScopeDevices.Compute` | 整趟 2 次（provider＋清除前重算） | Pipeline:272、367 |
| 自動範圍外清除 | 整趟一次（條件：本趟自己同步、全成功、有對應） | Pipeline:357-375 |
| 整份 sensor 鏡像、裝置鏡像、抑制載入 | 整趟一次 | Pipeline:395、400、418 |
| 取每日對應 `ResolveHostMapRows` | **每回望日** | Pipeline:448 |
| `GetStateChanges` ＋ `Evaluate` | **每回望日** | Pipeline:465-470 |
| 就緒度、磁碟／記憶體／CPU／Ping 趨勢 | **整趟一次，只算 newest**（`newest < ctx.Today`） | Pipeline:520-642 |
| 跨日歷史查詢 | 整趟一次（所有日的 EventKey 合併，內部 500 分批） | Pipeline:646-670 |
| 跨日標註、持平證據、抑制、Publish | **每回望日** | Pipeline:673-731 |
| 補追加＋案件掛接 | **每回望日 × 每台有 finding 的主機** | Pipeline:741-763 |
| 觸發式取數 | **每回望日**（日間序列，`foreach (var day in days)`） | Pipeline:846-877 |
| workflow 對帳 | **每回望日 × 該日被 touched 的主機** | Pipeline:943-964 |

要點：
- 趨勢規則只算 newest 一天；但「狀態型規則」（down/flapping/warning）每回望日各讀一次狀態變更。
- 當日重跑時 `reuseSyncFrom`（同日第二個窗口）會跳過 FetchDayAsync，只補待補裝置（Pipeline:225-240），其餘規則評估／發佈／補追加照跑。

---

## 2. 資料庫往返（讀寫次數與重複讀取）

### 2.1 夜間路徑逐階段表

單位：趟／日／裝置／感測器／規則／主機。

| 階段 | 讀 | 寫 | 備註（檔案:行號） |
|---|---|---|---|
| 設定 | 每趟 1~2 | 0 | Pipeline:114、136 |
| 監看政策、主機快照 | 每趟 1 | 0 | Pipeline:161-163；`CapturePrtgSnapshot` 以版本共用，HostStore.cs:20-33 |
| 階段 1 裝置 | 每 500 列 1 讀 | 每 500 列 1 個 Serializable 交易 | `UpsertDevices` Store:171-216；`BatchWriteScope` Store:107-125；每批都 `EnsureScopeMutationSlotFree`＋`IncrementPrtgCatalogueDataRevision`（Store:212） |
| 階段 2 感測器 | 同上 | **逐台模式：每台裝置至少 1 個交易；全站模式：每 500 列 1 個交易**；每個交易遞增目錄版本（Store:278） | Fetch:362-393、713-802 |
| 清除過期裝置/感測器 | 少數次 | 1 交易（Serializable） | Store:1185-1220、1238-1276 |
| ApplyAutoCategories | 1 次載入全部「category 為 null 或 auto」的感測器 | 有變更才寫 | Store:675-720 |
| 階段 3 狀態變更 | **每裝置每頁 1 次 PRTG 請求**；寫入：每頁 × 每日分桶各 1 次（1 select 既有鍵＋1 SaveChanges） | `AppendStateChanges` Store:740-778；Fetch:1111-1116 |
| 範圍外自動清除 | 見 §6／最佳化候選 C-1 | 每 250 筆 id 一個 Serializable 交易 | PrtgScopePurgeStore.cs:123-147、260-340 |
| 每日 ResolveHostMapRows | 每日 2~3 次（Max 日期 1~2 次＋整份當日列） | 0 | Store:2332-2354；Pipeline:448 |
| 每日 GetStateChanges | 每日 1 次 | 0 | Pipeline:465 |
| 就緒度＋4 條趨勢 | 每頁（100 顆）：價值列 1、`GetOkHostMapDates` 1、`GetReadinessMaps` 1、`GetDeviceNamesByObjids` 1（趨勢）；另整份 sensor 鏡像每條規則 1 次 | 0 | PrtgValueAssessment.cs:264-308；Pipeline:557、618 |
| 補追加 | 每台有 finding 的主機：1 個 Serializable 交易（讀整列 ContentJson、反序列化、比對、有變才寫） | 變更才寫 | EfAnalysisRecordStore.cs:672-855 |
| 案件掛接 | 每台每日 1 次（內容屬於案件流程，本盤點不展開） | | Pipeline:753 |
| 觸發式取數 | 每日：整份當日對應 1 次＋`QueryLightweight`（整日 高/中 紀錄＋ top issues）≥2 次＋`GetValueFetchTargets`＋`GetSensorsWithFullDayValues`；逐 sensor `UpsertValues`（1 select＋1 SaveChanges） | | PrtgTriggeredValueFetcher.cs:69、118、164-165；Fetch:1457 |
| finally workflow 對帳 | **每日 × 每台 touched 主機：1 次讀 blob＋1 次寫 blob（Update 永遠寫，因 `PrtgAttemptAtUtc` 每次變）** | HostDayWorkflowState.cs:115、494-500；Pipeline:943-964 |

### 2.2 已確認屬實的「同一份資料重複讀取」

1. **相鄰回望日的狀態變更重疊**——屬實。`GetStateChanges(day.AddDays(-1), day.AddDays(1))`（Pipeline:465），區間 `[d-1, d+1)` 為兩天；相鄰兩個回望日（d 與 d-1）的區間為 `[d-1,d+1)` 與 `[d-2,d)`，重疊一天。除了最舊／最新日，每天的資料被讀兩次。`CalibrationService.cs:2157` 的逐日迴圈是同一形狀。
   - 另：`GetStateChanges` 以 `ChangedAt` 篩選，而索引只有 `(sensor_objid, changed_at)`、`(sensor_objid, id)`、`created_at`（LfDbContext.cs:591-607）——**沒有以 changed_at 起頭的索引**，推論是每次呼叫都是整張狀態變更表（保留期內全部）的索引或表掃描（未實測，需看執行計畫）。
   - 另：整列載入含 `Message`（可能很長）與 `PrevStatus/Quality/CreatedAt`，`PrtgRuleEvaluator` 只用 `SensorObjid/ChangedAt/Status`（PrtgRuleEvaluator.cs:159-304）。
2. **整份感測器鏡像在同一趟被讀多次**——屬實。`GetSensorStatuses()` 的讀取點：Pipeline:395（1 次）、`PrtgValueReadiness.Compute` PrtgValueReadiness.cs:31（1 次）、磁碟趨勢 `assessment.Candidates(...)`（Pipeline:549→PrtgValueAssessment.cs:183）、記憶體／CPU／Ping 趨勢各 1 次（Pipeline:611）、`BuildSteadyEvidence` 有候選時每日 1 次（Pipeline:1192）。合計每夜至少 6 次整份載入（每次是全部未暫停感測器的 6 欄投影，Store:2095-2106）。
3. **`GetOkHostMapDates` 每頁重算**——屬實，且與頁面無關。`AssessPage` 每次呼叫都 `_store.GetOkHostMapDates(mapsFrom, mapsEnd)`（PrtgValueAssessment.cs:286），範圍＝窗口起點往前 31 天到今天，條件是 `MapStatus == Ok`（map_status 無索引，PK 為 `(map_date, device_objid)`，LfDbContext.cs:678-690）。這個查詢結果對同一趟的所有頁都相同（只與日期範圍有關）。每夜呼叫次數＝（就緒度頁數）＋（4 條趨勢各自頁數）＋（持平證據頁數）。就緒度母體包含 availability 類，頁數約等於「已對應裝置的 cpu/memory/disk/availability sensor 數 ÷ 100」。
4. **同一天對應整份被讀多次**——屬實。同一個 newest 日：`ResolveHostMapRows`（Pipeline:448）、就緒度 `DeviceToHostOn`（Pipeline:527）＋`ResolveNewestHostMap`（PrtgValueReadiness.cs:41）、4 條趨勢各 1 次 `ResolveNewestHostMap`（Pipeline:553、614）、觸發式 `GetHostMapForDay`（PrtgTriggeredValueFetcher.cs:69）。每次都是「該日全部裝置列」（含 unmatched，列數＝PRTG 全站裝置數）。
5. **`Evaluate` 每日重建字典**——屬實。每次 `Evaluate` 對 `sensorStatuses` 做 GroupBy 建 `sensorCategories`、`sensorTypes`（PrtgRuleEvaluator.cs:83-86、138-140），輸入每日相同；另 `rules` 重複群組判斷（90-102）。O(感測器數 × 回望日)。
6. **`FoldByDevice` 二次方**——`findings.Where(f => f.DeviceObjid == deviceObjid …)` 對每個有 finding 的裝置掃整份 findings（PrtgRuleEvaluator.cs:366-390）。
7. **同主機清單多次 `GetAll()`**——`PrtgHostMapper.MapForDate` 內 `GetAll()` 一次（PrtgHostMapper.cs:54），`BuildActiveHostIpLookup` 又一次（PrtgHostMapper.cs:376）；`Compute` 內再 2 次（PrtgScopeDevices.cs:64→376 與 79）。`Read()` 回傳淺複製清單（JsonBlobCollection.cs:77），3000 項、單趟數次，成本低；但 `PrtgHostMapRefresher.TryRefreshToday` 每次 `new HostStore(...)`（PrtgHostMapRefresher.cs:58），快取是實例層級（HostStore.cs:11、JsonBlobCollection.cs:29），所以每次都冷快取、整份 hosts blob（註解稱 3000 台約 4 MB，JsonBlobCollection.cs:70）重新讀取與反序列化。

### 2.3 每一步寫入的特殊結構

- 對應重算 `ReplaceHostMapForDate`：一個 Serializable 交易內先讀舊列、計算差異、`ExecuteDelete` 整日、分 500 批 Add＋SaveChanges、遞增主機對應版本（Store:999-1062）。每夜一次（nightly）＋每次主機異動一次（見 §5）。
- 對應表每天一份完整複本（列數＝PRTG 裝置數），保留 180 天（`Prune` Store:1109-1145）。對應日期重複多日時，過去日沿用「最近一份 ok 對應」，不再寫（PrtgHostMapper.cs:22 RuleVersion 說明、Pipeline:413-416），所以每天實際寫入的只有 newest 一份，但保留期內天數都有一份，列數 ≈ 裝置數 × 保留天數（推論，視是否每天都有對應重算）。

---

## 3. 記憶體

### 3.1 整趟存活的集合（夜間）

| 集合 | 大小隨什麼成長 | 位置 |
|---|---|---|
| `evaluationHosts`：全部主機轉 `WebHost` 的字典 | 主機數（3000） | Pipeline:162-163 |
| `allSensors`＋`sensorNames`＋`sensorToDevice`＋`sensorStatuses` | 鏡像感測器數（上萬）×4 份投影 | Pipeline:395-409 |
| `deviceNames`（全部 PRTG 裝置的名稱字典；實際載入完整 `PrtgDeviceRow` 含 Tags、GroupPath） | PRTG 全站裝置數 | Pipeline:400-402；`GetAllDevices` Store:1331 |
| `allSuppressions`（全部抑制） | 抑制條目數 | Pipeline:418-420 |
| `planned[day].DeviceToHost`（每回望日一份 device→host 字典） | 回望日數 × 已對應裝置數；借用同一份對應的日子仍各複製一份 | Pipeline:455、1266 |
| `planned[day].FindingsByHost`、`dayStates`、`prtgFindings`（登錄簿，整趟存活，且 `_attachGates` 對每個 (host,day) 留一把鎖物件） | 回望日 × finding 數／主機日數 | Pipeline:498；PrtgFindingsRegistry.cs:20-30、125-135 |
| `PrtgDayStat.PendingSensorDevices/PendingMessageDevices`（每日各存一份同一份待補清單，之後序列化進 BatchRun） | 待補裝置數 × 回望日數 | Pipeline:1010-1011 |
| `PrtgValueReadiness.Compute` 的 `allRows`（每顆 sensor 一列，含 `Daily` 清單＝最多 28 日） | 就緒度母體 × 日數 | PrtgValueReadiness.cs:38-55（回傳 `sortedRows`，Pipeline 只取 `FormatLine`，Pipeline:526-529） |
| `fetchService` 內 `ValueFetchRun` 的 `States/Failures/Retryable/Supplemented` 陣列 | 取數 sensor 數（輕量） | Fetch:1308-1345 |

### 3.2 一次載入整份鏡像／整份狀態

- `GetStateChanges`（每日，整列含 Message，Store:2081-2091）；`GetAllDevices`、`GetAllSensors`、`GetSensorStatuses`（見 §2.2）。
- `PrtgMirrorGuardSource.GetSensors()` ＝ `GetAllSensors()`（所有欄位，含 Tags、ThresholdsJson，Store:1356-1361；PrtgResourceGuardSource.cs:36-40）。`PrtgDailyPipeline` 在 Pipeline:272 與 367 各 new 一個來源，因此每趟載入兩次整份感測器全欄位；快照服務每次補抓 tick 也載入一次（Snap:914-919）。
- `ComputeBusinessScopeDevices` 載入整份最近對應 `GetLatestHostMap()`＋`_hosts.GetAll()`＋`GetManualMaps()`（Snap:1156-1186）。
- `ReadRecentStateChangeQueueRows`：載入所有佇列 blob 鍵（上限 15,000）再批次讀內容並反序列化（Store:585-602、RecentStateChangeQueueCapacity=15000，Store:74）。
- `PrtgSnapshotHostedService._sensorTypes`＝全部未暫停感測器的 objid→type 字典，常駐（Snap:143、1580；`GetSensorTypes` Store:2112-2128）。
- `PrtgSnapshotJournal` 在記憶體另持有 `_currentAccumulator`（等同累積器全部列的第二份複本）與 `_currentPending`（PrtgSnapshotJournal.cs:348-356、528-529）；累積器本身是 `Dictionary<DateTime, Dictionary<long, SensorAggregate>>`（PrtgSnapshotAccumulator.cs:98-99）。資料庫不可用時上限 200,000 列（PrtgSnapshotJournal.cs:39、396）。

### 3.3 對全部項目一次建立 Task 再限流

- `RunDevicesWithPauseAsync`：`Task.WhenAll(ids.Select(async id => …))`，**一個裝置一個 Task**，Task 內才排隊取 `session.Gate.WaitAsync` 與 `semaphore.WaitAsync`（Fetch:833-888；`PrtgFetchConcurrency` 預設 2，SystemSettings.cs:429）。夜間感測器逐台模式（≤500 台）與狀態變更（所有範圍裝置，預設 3000 台）都走這條 → 3000 個 Task 常駐等待（每個持有一個 `semaphore.WaitAsync(ct)` 的取消註冊）。
- `FetchValuesFirstRoundAsync`：`Enumerable.Range(0, totalSensors).Select(async index => …)` ＋ `Task.WhenAll`，每顆 sensor 一個 Task（Fetch:1350-1375）。夜間（激進策略）或觸發式取數用；數量＝取數 sensor 數。

### 3.4 單次回應記憶體

- PRTG 回應整包字串，上限 256 MB（PrtgClient.cs:107）；`table.json` 單頁 5000 列（PrtgTablePager.cs:64）；`JsonDocument.Parse` 全文（PrtgTablePager.cs:174）。`seenKeys` 對全站模式的感測器分頁會為**PRTG 全站每一列**存一個字串（PrtgTablePager.cs:139、217），即使 mapper 因不在範圍而丟掉。

---

## 4. 鎖、配額與取消

### 4.1 `PrtgRequestBudget`（PrtgRequestBudget.cs）

- 結構：單一 `lock (_lock)` 保護 `List<Reservation> _reservations`、`List<Waiter> _waiters`（FIFO）、`Dictionary<Purpose,int>`（行 118-125）。
- 限制：historicdata 每 60 秒 5 次、table 每秒 2 次、在途 4（快照最多 3，行 115-116、299-304、316-317）。
- 排隊：`AcquireAsync` 在無排隊者且有名額時立即 `GrantUnderLock`，否則 `new Waiter` 加入 `_waiters` 並註冊取消回呼（行 181-217）。
- 取消時搜尋：取消回呼內 `_waiters.Remove(waiter)`（線性掃描，行 205-212），成功後再 `EvaluateWaitersUnderLock()`（掃全部等待者，行 279-297）；每次評估 `HasCapacityUnderLock` 用 `_reservations.Count(...)`（行 389-398）；`ScheduleNextWakeupUnderLock` 每次配置 `new[]{…}` 與 `GetEarliestCapacityTimeUnderLock` 的 `Where/OrderBy/ToList`（行 306-387）。
- **等待者數量上界**：每個等待者對應一個正在 `client.GetJsonAsync` 的呼叫端；夜間上界＝`PrtgFetchConcurrency`（預設 2，見 §3.3 的 semaphore），快照 ≤ 3 批並行（Snap:696）。因此推論：目前結構下等待者很少，線性搜尋不是熱點；若 `PrtgFetchConcurrency` 被調得很高才會放大（未見上限，只見預設值）。
- **實際吞吐上界**：table 2 req/s ⇒ 夜間狀態變更階段 3000 台至少 3000 次請求 ≥ 約 25 分鐘（推論自配額，未實測）。這也是 DB 呼叫密度的上界（見 4.2）。

### 4.2 `PrtgOperationScope.Checkpoint`（PrtgOperationScope.cs:52-65）

每次呼叫讀了什麼：
1. `_read()`——夜間路徑傳入的是 `() => new SystemSettingsStore(backend.Blob("system_settings")).Get()`（Pipeline:154）。`SystemSettingsStore` 的快取是實例層級（ISystemSettingsStore.cs:19、JsonBlobSingleton.cs:48-70），**每次 new 都是冷快取**：1 次版本探測＋1 次整份 blob 讀取（`ReadWithVersion`）＋反序列化 `SystemSettings`。
2. `ScopeChanged()` → `_readScope()` ＝ `PrtgScopeRevisionReader.Read`（PrtgScopeDevices 無關；PrtgScopeRevisionReader.cs:48-100）：1 次 5 個 blob 版本的單一查詢；若 hosts／sentinels 版本與實例快取不同，重算主機指紋（序列化 `GetAll()` 投影＋SHA256，行 80-95）；實例是 `new PrtgScopeRevisionReader` 一次傳進去（Pipeline:154），整趟共用，所以指紋只在 hosts 版本變動時重算。
3. `Publish(current, …)`（行 63）內 `_scopeCaptured ? _readScope?.Invoke()`（行 106-109）——**再讀一次**同樣的 `ReadCore`（再 1 次查詢）；並對 `Versions` 靜態字典做 `Where/OrderByDescending/Skip(32)/TryRemove`（行 115-116），且算兩個 SHA256（行 108）。
4. `SameSettings(_initial, current)` 逐欄比對（行 130-154）。
→ 夜間每次 Checkpoint ≈ **4 次 DB 往返**（設定 2＋範圍 2）＋1 次 `SystemSettings` 反序列化。

被誰以多高頻率呼叫（全部呼叫點）：
- **每個 PRTG HTTP 請求 4 次**：`PrtgClient.GetResponseCoreAsync` 進入時（PrtgClient.cs:467）、取得預算前（480）、`MarkRequestSent` 前（495）、回應回來後（542）；包在 `client.OperationCheckpoint`（PrtgClient.cs:98-104）。
- **有界讀取（快照與探測用的 `GetBoundedJsonAsync`）：每讀 4096 位元組再 1 次**（PrtgClient.cs:577-583，`chunk = Math.Min(4096, limit+1)`）。快照批次回應上限 512 KB（PrtgSnapshotResponseContract.cs:10），故一次回應最多 128 次、典型 100 顆的回應約數至十數次（推論自回應大小，未實測）。
- 夜間管線自己：Pipeline:157、330、434（每回望日）、556／617（每頁 100 顆）、675（每回望日）、743（**每台有 finding 的主機 × 每回望日**）、848（每回望日）。
- 快照服務：`WithOperationScopeAsync`（Snap:464）、`_operationCheckpoint` 在 Tick 與 Drain 多處（Snap:388、394、420、434、557、559、585、1096）。**快照用 `settingsStore`（單例、有快取）與 `ReadRawSemantic`**（Snap:459-460）：`ReadRawSemantic` 每次還額外 `new PrtgMonitoringPolicyStore(...).Get()` 讀整份政策 blob（`JsonBlobSingleton` 預設不快取，PrtgMonitoringPolicy.cs:20-23；JsonBlobSingleton.cs:34-49），再 `Order()`、序列化、SHA256（PrtgScopeRevisionReader.cs:35-45）；`Publish` 又再呼叫一次 → **每次 Checkpoint 讀兩份政策 blob 並各自序列化**。政策 `SensorIds` 上限 15,000（ScaleLimits.cs:18）。
- Drain 的 per-lease Checkpoint 更重：`_settingsStore.Get()` ＋ `scopeReader.Read()` ＋ `ReadCatalogueDataRevision()`（Snap:1078-1089）。
- 回填／探測／結構同步另有呼叫點（PrtgBackfillService.cs:627-630、833-836；PrtgProbeService.cs:313-348；PrtgStructureSyncService.cs:395-398），本報告不展開。
- 其他讀取者：`PrtgOperationScope.ReadVersions()`（健康／操作頁，PrtgOperationsController.cs:32）對每個 Active 範圍呼叫 `UpdateVersion` → 再讀設定與範圍（PrtgOperationScope.cs:29-33、91-105）。

估算（推論，用上面事實推）：夜間狀態變更階段至少 3000 次請求 × 4 Checkpoint ≈ 12,000 次 Checkpoint ≈ 約 48,000 次 DB 往返；快照 10,000 顆＝100 批，每批 4＋(數至十數) 次 Checkpoint，每次 ≥ 2 次政策 blob 讀取。

### 4.3 手動同步與夜間取數互斥

- 夜間 → 手動：夜間在階段 0 讀 `manualWorkGate.Running`；同步進行就等 `PrtgStructureSyncService.WaitUntilIdleAsync`（每 2 秒輪詢，上限 90 分鐘，PrtgStructureSyncService.cs:87、93、192-217）；回填進行最多等 30 分鐘（每 5 秒輪詢，IPrtgManualWorkGate.cs:40、PrtgManualWorkGate.cs:12、65-76）；Pipeline:176-211。**只在趟首檢查一次**。
- 手動 → 夜間：`PrtgStructureSyncService.TryStart` 若 `_schedulerState.IsRunning`（取數執行中）即回 409（PrtgStructureSyncService.cs:281-287）；回填中也拒絕（289-293）；範圍外清除有 active owner 也拒絕（340-346）。
- 互斥全是**行程內旗標**（`SchedulerRunState`、`_state.TryBegin`），跨行程靠資料庫的 `ScopeRevisionBlobKey` 閘門（`LockScopeMutationGate`、`EnsureScopeMutationSlotFree`，PrtgScopePurgeStore.cs:440-460），所有 Upsert／Replace／Delete 每個交易都進這個閘門（見 §5）。

### 4.4 其他鎖

- `PrtgFindingsRegistry._lock`＋`_attachGates`（per host-day 鎖，PrtgFindingsRegistry.cs:23-24、125-133）；`EfAnalysisRecordStore.LockFor(hostId,date)`（EfAnalysisRecordStore.cs:689）。
- 快照服務：`_pendingWrite` 鎖同時包住 JSON 解析與日誌 fsync（Snap:1476-1485），3 批並行的 HTTP 在處理階段被它序列化。
- `JsonBlobSingleton`／`JsonBlobCollection` 快取命中不進鎖（JsonBlobCollection.cs:84-107）。

---

## 5. 跨步驟耦合（目錄版本／快取失效）

### 5.1 三個版本戳

| 版本 | 遞增點 | 位置 |
|---|---|---|
| 目錄版本 `prtg_catalogue_data_revision` | `UpsertDevices` 每批、`UpsertSensors` 每批、`ApplyAutoCategories`（有變更才）、`DeleteDevicesNotSyncedSince`、`DeleteSensorsNotSyncedSince`（有刪除才） | Store:212、278、移除分類 Store:~715、1213、1270；`IncrementPrtgCatalogueDataRevision` Store:2476-2486（讀＋條件更新，失敗擲並行例外） |
| 主機對應版本 `prtg_host_map_data_revision` | `ReplaceHostMapForDate`（每次）、`DeleteHostMapsExceptDate`、`Prune` 的對應刪除 | Store:1058、985-1000、1138；`IncrementHostMapDataRevision` Store:2496-2516 |
| 範圍版本 `prtg_scope_revision` | 人工對應、IP 排除寫入（`WriteScopeChange`，Store:2519-2542）；寫入前由所有鏡像寫入的交易 `LockScopeMutationGate` 以 no-op update 取得寫鎖（PrtgScopePurgeStore.cs:~420-440） | |
| `DataVersionStamp`（站內快取版本戳） | `PrtgStructureSyncService.cs:413`（手動同步成功）、`PrtgRiskReviewHostedService.cs:23`（**每批 100 筆**）、`PrtgRiskReviewController.cs:41`，以及其他背景服務與所有成功的非 GET 請求（Program.cs:288-300） | |

說明：**夜間路徑與快照服務直接寫資料庫、不經 `DataVersionStamp`**（PrtgDeviceIndexCache.cs 註解，行 55-69），所以那些快取靠 TTL 而非版本戳新鮮。

### 5.2 被影響的讀取端

- 監看政策頁 `PrtgMonitoringController`：fence token 內含 `CatalogueRevision`、`MapRevision`、`ScopeRevision`、`HostVersion`、設定版本、`MapDate`（PrtgMonitoringController.cs:249-273）；任何一個版本戳在使用者翻頁途中改變即回 409（`CatalogueChanged`）。`Preview` 端點（325-430）還有：每次呼叫序列化全主機清單算 SHA256 指紋（372-374）、載入並驗證全部規則（359-370）、**每列 1 次 `RecordStore(...).ReadRecent(targetDay,1)`（405，N+1，每頁 100 列）**、結束前再讀 9 個版本比對（417-421）。夜間每個對應重算與每個感測器 upsert 批次都會使它的 fence 失效。
- 快照 Drain：每個 lease 的 Checkpoint 比對 `ReadCatalogueDataRevision()`（Snap:1076、1084）；補抓寫入感測器（`BackfillScopeSensorsAsync` → `UpsertSensors`）會遞增版本，使同一 tick 稍後領出的 lease 可能被取消；另 `PrtgScopeRevisionReader.Read` 的範圍字串含 `ScopeRevisionBlobKey`，快照用 `ReadRawSemantic` 不含政策整份版本。
- 校準：`CalibrationService` 比對 `ReadHostMapDataRevision()`（CalibrationService.cs:841、896、2094、2181）。
- `PrtgDeviceIndexCache`：**刻意不綁版本戳**，只有 TTL（PrtgDeviceIndexCache.cs:55-75、106-120）；供 `PrtgConflictQueryService`（衝突清單）使用。
- 主機異動 → 重算對應 → 快照：`HostAdminService.cs:415-424`，`TryRefreshToday` 成功且是新增含 IP／改 IP／轉啟用才 `RequestScopeRefresh`；`RequestScopeRefresh` 設旗標、`InvalidateTargets`（清 `_targetObjids`、`_sensorTypes`）並喚醒服務（Snap:253-265、1553-1559）。`TryApplyRuleUpgrade` 在啟動時重算今天對應並刪除舊日對應（PrtgHostMapRefresher.cs:71-121；Program.cs:250-256）。
- 資料版本戳影響的快取：`SummaryCache`、`IssueRankingCache`、`ActionableSnapshotCache`、`NextUnhandledSequenceCache`、`IssueOwnedHostIdsCache`、`IssueExclusionProvider`（grep `DataVersionStamp` 於 ServiceCollectionExtensions.cs:379-417、552，各服務 ctor）。

---

## 6. 快照服務的週期成本

### 6.1 每個 tick（60 秒，Snap:70）固定做的

- `_settingsStore.Get()`（版本探測＋反序列化，Snap:483）；`RestoreJournal`（檔案鎖與字串比對，無 DB，Snap:489、1265-1309）；`FlushAccumulator(all:false)`（`PreviewDrainBefore` ＋ `KeysForDrainBefore` ＋ `CaptureDelta`，Snap:490、1464-1474）；`_structureSync.IsRunning`、`_backfill.GetStatus()`（組 DTO 含 `Output`，PrtgBackfillService.cs:41-70；推論小）、`IsFetchRunningPrtgPhase`。
- 其餘「未達策略間隔」就 return（Snap:539-545），**沒有** DB 工作或 PRTG 請求。

### 6.2 每個「實際執行」的 tick（保守 15 分／激進 5 分）

(a) 建 `PrtgOperationScope`（Snap:552-460）：`new PrtgScopeRevisionReader`（**每次 new → 主機指紋第一次呼叫必重算：序列化 `hosts.GetAll()` 投影＋SHA256，PrtgScopeRevisionReader.cs:80-95**，成本與主機數成正比）。
(b) `ReconcileRecentStateChangesForCurrentScope`（Snap:558、995-1010）：
 - `ComputeBusinessScopeDevices`：整份最近對應＋`hosts.GetAll()`＋手動對應（Snap:1156-1186）
 - 再 `new PrtgScopeRevisionReader(...).Read()`（Snap:1017，**又一個冷實例 → 主機指紋再算一次**）
 - `ReconcileRecentStateChanges`：`mirroredBusinessDevices` 查詢（Distinct device objid，Store:375-383）＋整個佇列讀取與反序列化（Store:389、585-602）
 - `ReadRecentStateChangeQueueStopSummary`（Snap:1006）
(c) `ExecuteSnapshotAsync`：
 - `ResolveTargets`：`ComputeTargetFingerprint`（2 次 blob 版本探測，Snap:1597-1609）；整點才 `RefreshTargets`（`GetLatestHostMapWithDate`＋`hosts.GetAll()`＋`GetValueFetchTargets`＋`GetSensorTypes`＋守門解析，Snap:1561-1591）；**每次**另 `new PrtgMonitoringPolicyStore(...).Get()` ＋ `SelectEffectiveTargets`（`Where(policy.SensorIds.Contains)` 對 `List<long>`，O(目標 × 政策 sensor 數)，PrtgSnapshotTargetResolver.cs:46-52、PrtgMonitoringPolicy.cs:17-18）。
 - `FetchFilteredAsync`：每 100 顆 1 次 `table.json` 請求（至多 3 批並行，Snap:714-742）。10,000 顆＝100 請求／tick。每批：`RecordDiagnostic("attempt")`＋`RecordDiagnostic("success")`（Snap:723、728）＝**2 次對診斷 blob 的整份讀改寫**（PrtgSnapshotDiagnosticsStore.cs:24-70：讀整份 blob 反序列化、`Validate`（含 `Distinct`/`Any`）、重新序列化、寫入，最多 360 小時列、4 MB 上限，被 `_gate` 序列化）；`ParseAndCheckpoint`＝在 `_pendingWrite` 鎖內解析 JSON＋`SaveJournal`（Snap:1476-1485）。
 - `SaveJournal` → `AppendDelta`：每批 1 個 journal segment＝讀 manifest 檔＋SHA256（`EnsureIncrementalReady`，PrtgSnapshotJournal.cs:295-303）＋序列化＋`WriteDurable`（fsync）＋`File.Move`＋`WriteManifest`（再一次 durable 寫入＋`File.Replace`）（305-360）；每 127 個 segment 壓縮一次，壓縮＝序列化整個累積器＋待寫批次並重寫整代（`CompactAfterSegments=127`，行 98、314-320、446-454、483-538）。10,000 顆／tick ≈ 100 個 segment ⇒ 約每 1.3 個 tick 壓縮一次（推論）。
 - 另 `PrtgSnapshotCapacityResponseValidator.Validate(json, batch)` 再解析一次回應（Snap:727；另 `ParseSnapshotResponse` Snap:775 再解析一次）＝每批 JSON 解析 2 次。
 - `FlushAccumulator(now,false)`（Snap:669）。
(d) `BackfillScopeSensorsAsync`（Snap:579-583、891-993）：`GetLatestStructureSyncedAt`（Max）＋**`PrtgScopeDevices.Compute`**（讀整份最近對應、`hostStore.GetAll()` ×2、手動對應、`GetDeviceNamesByObjids`、守門 `GetDevices/GetAllSensors`（整表全欄位）、序列化對應＋手動＋主機做 SHA256 指紋、`CaptureScopeFenceJson` 兩次，PrtgScopeDevices.cs:46-154）；若有待補裝置（上限 50/tick，`MaxBackfillDevicesPerTick`，Snap:147）才發 PRTG 請求與寫入（逐台 Serializable 交易＋目錄版本遞增）。**即使沒有待補裝置，這個 Compute 也每個執行 tick 跑一次**（Snap:915，早退在 935，之後）。
(e) `FetchRecentStateChangesAsync` → `DrainRecentStateChangesAsync`（Snap:586、1035-1132）：再一次 `ComputeBusinessScopeDevices`、`new PrtgScopeRevisionReader(...).Read()`（再一個冷實例）、`ReconcileRecentStateChanges`（再讀整個佇列）、`ClaimRecentStateChanges`（Serializable 交易內再讀整個佇列，Store:315-347）；之後每個 lease 1 次 messages 請求（至多 50 個/tick，被 30 秒 `ScopeWorkBudget` 限制，Snap:121、444-455）。
(f) 結算：整點後 `MergeSampledValues`（`BatchWriteAtomic`：一個大交易、每 500 列 1 select＋1 SaveChanges，Store:844-966）。
(g) 成功後寫新鮮度 blob、診斷等（Snap:667、1662-1686）。

與範圍大小成正比的項目：目標數 ÷100 ＝ HTTP 批數＝journal segment 數＝診斷 blob 讀改寫次數×2＝Checkpoint 數（見 §4.2）；主機數＝每次指紋計算與 `GetAll()`；範圍裝置數＝佇列大小（上限 15,000，Store:74）＝每個 tick 3 次整佇列讀取；感測器數＝`GetAllSensors`（Compute 的守門來源）、`GetSensorTypes`。

### 6.3 `PrtgRiskReviewHostedService`

- 啟動延遲 25 秒後迴圈：`RunBatch()` 每批取 100 筆 `RiskProjectionVersion < 版本`（有索引 `IX_lf_daily_risk_projection`，LfDbContext.cs:177），逐筆：主機日鎖＋1 個交易＋載入整列（含 `ContentJson`）＋反序列化，有變才序列化寫回；批與批間 `Task.Delay(100ms)`；`changed > 0` 就 `dataVersion.Bump()`（PrtgRiskReviewHostedService.cs:20-26；PrtgRiskProjectionStore.cs:19-60）。
- `count++` 不論該列是否有改寫（PrtgRiskProjectionStore.cs:58），所以**只要處理過候選就 Bump，即使內容沒變**；每批一次全站快取失效（§5.2 清單）。直到一批為 0 才結束（行 25）。

---

## 7. 最佳化候選

格式：現況／成本隨什麼成長／可能的等價改法／影響到哪些其他步驟或呼叫端／風險。優先級以「成本×可行性」排序，標示 [高][中][低]。

### C-1 [高] 自動範圍外清除對整張數值／狀態變更表全掃
- 現況：每個「本趟自己同步且全成功」的夜間都建立自動清除 job（Pipeline:357-375 → `PrtgScopePurge.RunAfterStructureSync` PrtgScopeDevices.cs:233-309）。`NewJob`（PrtgScopePurgeStore.cs:123-147）：`Max(Id)`×2；`CaptureCandidateSensorIds`（514-545）對 values 與 state_changes 以 DISTINCT sensor_objid 每 500 個一頁走完整個資料表；`CountCandidates`（498-512）。然後 `RunAfterStructureSync` 迴圈呼叫 `RunBatch`（PrtgScopeDevices.cs:269-279），每批 `BatchSize=250`（行 23）：Serializable 交易＋`LockScopeMutationGate`＋`LockScopeFence`（6 個 blob 版本 no-op update）＋`PauseExpiredOwner`＋兩張表 `ReadCandidatePage`（260-340、485-496）。`ReadCandidatePage` **逐 id 區間每 250 列前進一次，與是否有候選感測器無關**——候選集合為空時仍整張表走完（`rows.Where(candidateSensors.Contains)` 為空，游標仍前進；完成條件是游標 ≥ 水位，PrtgScopePurgeStore.cs:322-323、495）。
- 成本隨：`lf_prtg_values` 列數（感測器數 × 24 × 保留天數，量級推論可達數千萬）／250 ＝ 批數；狀態變更列數／250；每批約 10+ 語句且是 Serializable。**每夜一次**。
- 等價改法（要先確認語意）：候選感測器集合為空（`CaptureCandidateSensorIds` 結果空）時，job 可直接標 complete 不跑批次（刪除量為 0，結果相同）；候選非空時以 `WHERE sensor_objid IN (候選) AND id <= watermark` 直接分批刪除（索引 `IX_lf_prtg_val_purge_sensor (sensor_objid,id)` 已存在，LfDbContext.cs:609-620）而非全表順掃；`CaptureCandidateSensorIds` 改用感測器表與裝置表差集再以索引驗證是否有資料。
- 影響：`PrtgScopePurgeStore` 同一組方法也服務手動預覽／確認（`CreatePreview`、`Accept`、`RunBatch` 含 `authorizationFence`，PrtgScopePurgeStore.cs:~54-110、202-260）；`PrtgStructureSyncRunner`（PrtgStructureSyncRunner.cs:190）共用 `RunAfterStructureSync`；`PrtgScopePurgeJobsController`（PrtgScopePurgeJobsController.cs:156）；每批 `EnsureScopeMutationSlotFree` 與全部鏡像寫入互斥（§5.1），清除期間會擋住快照補抓與手動同步（`ScopePurgeConflict`，PrtgBackfillService.cs:275-286）。進度欄位（`RemainingValues`、`CountsReconciled`、`OtherwiseRemoved*`）有前端消費端，不可靜默改義。
- 風險：中。涉及「不可信範圍不清除」紅線與 durable job 語意；縮短只能是「早退／走索引」不得放寬任何保護（CheckScope／CheckShrink／fence）。屬資料保留／刪除語意，改前要重跑 `PrtgScopePurge*` 相關測試。

### C-2 [高] `Checkpoint` 的資料庫成本（4 次往返／次）與呼叫頻率
- 現況：見 §4.2。夜間 `() => new SystemSettingsStore(...).Get()`（Pipeline:154）每次冷快取＝2 次查詢＋反序列化；`Read()`（含政策與規則版本）＋`Publish` 內再讀一次＝2 次查詢；每 HTTP 請求 4 次（PrtgClient.cs:467/480/495/542）；有界讀每 4096 位元組 1 次（PrtgClient.cs:580）；快照路徑每次還讀兩份政策 blob（PrtgScopeRevisionReader.cs:37、Publish 內再呼叫）。
- 成本隨：HTTP 請求數（夜間≈裝置數＋頁數；快照≈目標數/100×(4＋回應位元組/4096)）、Pipeline 內每主機每日 Checkpoint（Pipeline:743）。
- 等價改法：
  1. 設定讀取改用單例快取版的 `ISystemSettingsStore`（快照已這樣，Snap:459）或在 scope 內做「版本探測相同就不反序列化」（只比版本整數＋`Revision`）。`SameSettings` 已要求逐欄比對，改成比對 `Revision` 字串（`Touch` 每次寫都換 GUID，ISystemSettingsStore.cs:21-25）等價於「是否被寫過」，但目前註解明說「不以 UpdatedAt 相同跳過比較：同時儲存／匯入也可能保留相同時間」（PrtgOperationScope.cs:56），所以改動前要確認 `Revision` 是否保證每次寫入都變（`Touch` 對 `Update` 路徑是；匯入路徑需查）。
  2. `Checkpoint` 與 `Publish` 共用同一次 scope 讀取（Publish 不再自己讀第二次）。
  3. 有界讀取的 chunk 級 Checkpoint 改成「每個請求至多 1~2 次」或以時間節流（例如 ≥N 毫秒才重查），取消仍由 `ct` 負責。
  4. 在 `ReadRawSemantic` 內以政策 blob 版本做快取（政策 `Get()` 改成帶版本探測的快取，如 `SystemSettingsStore`）。
- 影響：`PrtgOperationScope` 全部建構點（Pipeline:153、PrtgBackfillService.cs:625/830、PrtgProbeService.cs:310、PrtgStructureSyncService.cs:392、Snap:459）；測試以 Checkpoint 取消語意為契約（「設定或範圍變更就取消」是 CLAUDE.md 紅線所述「外部輪詢要能被取消」同族）；`ReadVersions`（PrtgOperationsController.cs:32）讀 `PrtgOperationVersion` 欄位（`ExpectedSettingsRevision` 等）。
- 風險：中。取消的時效性是產品行為（「每個 HTTP 邊界重讀持久設定」，PrtgOperationScope.cs:9）；節流會改變偵測延遲，要明確取捨。

### C-3 [高] 每日 `GetStateChanges` 重疊讀＋整表掃描＋整列載入
- 現況：Pipeline:465 每回望日 `GetStateChanges(day-1, day+1)`；Store:2081-2091；索引 LfDbContext.cs:591-607。
- 成本隨：回望日數 N × 表大小（無 changed_at 前導索引的推論下）；每夜 1 日時為 1 次整表掃描，補評估 7 日時 8 次。
- 等價改法：一次性讀取 `[oldest-1, newest+1)` 並依日分段切片（記憶體切片＝`ChangedAt` 範圍篩選）；或新增 `(changed_at)` 前導／覆蓋索引；並只投影 `SensorObjid, ChangedAt, Status`（Evaluate 只用這三欄）。`policySensorIds` 過濾可在 SQL 做。
- 影響：呼叫端只有兩個——Pipeline:465 與 `CalibrationService.cs:2157`（同樣逐日 2 日窗）；`PrtgRuleEvaluator.Evaluate` 的其他呼叫端（RuleAdminService 的規則試算？未逐一追，grep `PrtgRuleEvaluator.Evaluate(` 可確認）。新增索引屬於 schema 升級（`docs/DB-SPEC.md` §，SchemaUpgrader）。
- 風險：低（行為等價）；新增索引需考慮寫入成本與 SQL Server／SQLite 雙後端。Evaluate 對跨午夜持續 Down 只看 day-1 的前導（PrtgRuleEvaluator.cs:174-189），切片時必須保持「前一天＋當天」的同樣範圍，不能擴大。

### C-4 [高] `AssessPage` 每頁重算 `GetOkHostMapDates`（與頁無關），以及就緒度與趨勢各自重讀
- 現況：PrtgValueAssessment.cs:285-291；呼叫端 PrtgValueReadiness.cs:41-47、Pipeline:553-559、614-620、BuildSteadyEvidence Pipeline:1197-1203，RuleAdminService（RuleAdminService.cs:402 附近，未展開）。
- 成本隨：頁數（母體/100）× 對應表日期範圍內的 `map_status=ok` 列掃描（無 map_status 索引，PK 前導為 map_date，推論為範圍內全部列掃描）。
- 等價改法：比照 `newestHostMap` 的作法，由迴圈外算一次 `okDates` 傳入（參數可選，預設維持舊行為，與既有 `newestHostMap` 參數同形）；`GetReadinessMaps` 可改成一次讀本趟所有候選裝置的歷史對應再分頁切片。就緒度與 4 條趨勢可共用同一次「整份感測器鏡像讀取＋同一日期窗的價值列」（記憶體換往返；價值列 28 天 × 24 × 感測器數，量大，需斟酌是分類別共用還是只共用 `okDates` 與鏡像）。
- 影響：`PrtgValueAssessment.AssessPage` 的呼叫端（PrtgValueReadiness、Pipeline 四處、PrtgDailyPipeline.BuildSteadyEvidence、RuleAdminService 規則試算/就緒度、可能 CalibrationService）——介面是 `public`，要 grep 全部呼叫端並保持簽章向下相容；`PrtgValueAssessment` 類別註解稱「本類別是長壽服務，結果不記在實例欄位」（PrtgValueAssessment.cs:232-235），所以快取只能在呼叫端迴圈外持有，不能放進實例。
- 風險：低（純等價）；注意 `okDates` 的日期範圍與 `mapsEnd = max(today+1, windowEnd)` 取決於 `today`，跨午夜執行時要在同一趟內一致。

### C-5 [高] 快照每批 2 次診斷 blob 整份讀改寫
- 現況：Snap:723、728 → `PrtgSnapshotDiagnosticsStore.Record`（PrtgSnapshotDiagnosticsStore.cs:24-70）。
- 成本隨：目標數/100 × 2 × 診斷 blob 大小（≤360 小時列，≤4 MB 字元）；10,000 顆 ＝ 200 次整份讀＋反序列化＋Validate＋序列化＋寫，被 `_gate` 序列化。
- 等價改法：一輪快照在記憶體累計 attempts/successes 次數，輪末（或每批聚合）只寫一次，並讓 `Attempts` 累加 n；語意（`row.Attempts`=批數）不變。
- 影響：`PrtgSnapshotDiagnosticsService`（讀端，PrtgSnapshotDiagnosticsService.cs，依 `Attempts/Successes` 判讀狀態）；`PrtgSnapshotDiagnosticsStore` 測試；失敗路徑 `RecordDiagnostic("skip", …)` 的去重（`_recordedSkips`）不變。
- 風險：低。診斷在「輪中崩潰」時會少計幾個 attempt（best-effort 診斷，檔頭註解即聲明「不記錄單次或 sensor 級事件」）。

### C-6 [高] 快照 journal 每批 2 次 fsync＋manifest 讀取，並每 127 段整代重寫
- 現況：PrtgSnapshotJournal.cs:276-360（每批 `AppendDelta`＝讀 manifest＋SHA256＋寫 segment（fsync）＋`File.Move`＋寫 manifest（fsync＋`File.Replace`）），壓縮 446-454、483-538。
- 成本隨：批數＝目標數/100（每個執行 tick）；壓縮成本隨累積器列數（≤ 目標數 × 2 小時）。
- 等價改法：一輪結束後單一 `AppendDelta`（持久性邊界移到「一輪」而非「一批」）會改變崩潰復原粒度；較保守的等價做法是 (a) 不在每次 `EnsureIncrementalReady` 都重讀並雜湊 manifest（記憶體已有 `_manifestDigest`，需評估多行程寫入偵測是否依賴它：PrtgSnapshotJournal.cs:295-303 是「拒絕過期寫入」的跨行程保護）；(b) 把數個批次的 delta 合併為一個 segment（例如每 N 批或 M 毫秒）；(c) 壓縮門檻改依位元組。
- 影響：`PrtgSnapshotJournal` 的容量守門與 replay 測試、`GetJournalPreview/ArchiveJournalManually`、`PrtgSnapshotAlert`、`ExecuteSnapshotAsync` 的 `MaxRows/MaxBytes` 預留（Snap:644-652）。
- 風險：高。這是「耐久性契約」（崩潰後樣本不重不漏、批次 ack）；僅可在不改變「已 durable 才 Commit」順序的前提下合併。建議放在最後、或只做 (a) 的低風險子項並先測。

### C-7 [中] 快照 tick 重複計算範圍與整佇列
- 現況：每個執行 tick：`ComputeBusinessScopeDevices` 2 次（Snap:1015、1038）＋`PrtgScopeDevices.Compute` 1 次（Snap:915）＋`new PrtgScopeRevisionReader` 3 次以上（Snap:459-460、1017、1040、423），每個冷實例都重算主機指紋（PrtgScopeRevisionReader.cs:80-95）；`ReconcileRecentStateChanges` 2 次（Snap:1003、1045）＋`ClaimRecentStateChanges` 1 次，三次各讀整個佇列（Store:315-347、372-477、585-602）。`Compute` 在沒有待補裝置時仍整表載入感測器（`GetAllSensors`，Snap:919）。
- 成本隨：主機數、對應列數（PRTG 全站裝置數）、業務裝置數（佇列大小）、感測器數。
- 等價改法：同一個 tick 共用同一個 `PrtgScopeRevisionReader` 實例（或讓指紋以 hosts 版本做靜態／單例快取）；把 `ComputeBusinessScopeDevices` 結果記在 tick 內（以 `hosts` 版本＋對應版本＋手動對應版本為鍵）；`Drain` 內的第二次 Reconcile 在同 tick 已 Reconcile 且版本未變時可省（`_recentStateQueueHttpDeferred` 機制已有「同 tick 剛 Reconcile 就延後 HTTP」，Snap:1052-1056，表示兩次 Reconcile 在目前流程是刻意兩個階段，改動需對齊該狀態機）；`BackfillScopeSensorsAsync` 可先以 `ReadCatalogueDataRevision`＋對應版本＋範圍版本判斷「自上次補抓判定後沒變」就略過 Compute。
- 影響：`PrtgScopeRevisionReader` 另被 `MailNotificationService.cs:2022`、回填、探測、結構同步使用（冪等鍵格式不得更動，PrtgScopeRevisionReader.cs:23-25）；`PrtgScopeDevices.Compute` 被 8 處呼叫（見 §2 grep）；Drain/Reconcile 的持久化佇列語意（lease、`BusinessScopeVersion`）。
- 風險：中。快取鍵必須含授權／範圍維度（CLAUDE.md 紅線「新增快取鍵需含可見主機集合與資料版本戳」）；這裡的內容與使用者無關（純鏡像／主機清單結構），要在註解明講理由。

### C-8 [中] 夜間感測器鏡像多次整份載入與全欄位載入
- 現況：§2.2-2；`PrtgMirrorGuardSource` 兩次、全欄位（Pipeline:272、367；Store:1356）；`GetAllDevices` 全欄位只為取名稱（Pipeline:400）。
- 等價改法：趨勢／就緒度共用 Pipeline:395 已載入的 `allSensors`（`PrtgValueAssessment.Candidates` 已有 static 多載接受外部來源，PrtgValueAssessment.cs:186-200）；`deviceNames` 改成 `Select(Objid, Name)` 的投影；守門來源在同一趟共用一個實例（`PrtgMirrorGuardSource` 已設計成「同一趟內快取」，PrtgResourceGuardSource.cs:29-34，但 Pipeline 兩處各自 new）。
- 影響：`PrtgValueAssessment.Candidates`（instance 版）呼叫端；Compute 的守門判斷依賴「整張鏡像」（PrtgScopeDevices.cs:109-114，`corehealth` 判定逐感測器）。
- 風險：低。沿用既有 static 多載，不新增機制。

### C-9 [中] 狀態變更階段：一裝置一 Task、每頁每日分桶一次寫入
- 現況：Fetch:835（`Task.WhenAll(ids.Select(...))`）；寫入 Fetch:1111-1116。
- 成本隨：範圍裝置數（3000 個常駐 Task，每個有 `semaphore.WaitAsync(ct)` 取消註冊）；寫入往返＝裝置頁數×日分桶數。
- 等價改法：改為 N 個工作迴圈從佇列取裝置（N＝`maxConcurrency`），Task 數隨併發而非裝置數；把同一頁的寫入合併為一次 `AppendStateChanges`（內部已 500 批）。
- 影響：`RunDevicesWithPauseAsync` 同時服務感測器逐台、快照 Drain、探測（`allowPagerRetry:false` 路徑）；重排輪（`requeue`）與 `onSettled` 進度語意、停損（`session.Stopped`）流程；`PrtgFetchService` 單元測試覆蓋這些路徑。
- 風險：中。暫停閘／停損／重排的時序是行為契約。收益有限（Task 本體輕）——除非實測看到 Task 常駐成本，否則排後。

### C-10 [中] 觸發式取數的輪詢與重複讀對應
- 現況：PrtgTriggeredValueFetcher.cs:69（每日再讀整份對應）、109-185（每次輪詢 `QueryLightweight` 整日高/中紀錄＋issues）、189-210（輪詢 30 秒、上限 8 小時；第一天輪詢至分析結束，之後每天 2 次掃描）。
- 成本隨：輪詢次數（分析時間/30 秒）× 該日高/中紀錄數（含 `LoadLightweightIssues`，EfAnalysisRecordStore.cs:1320-1378）。保守策略（預設）略過整段（Pipeline:801-816），激進策略才走。
- 等價改法：輪詢查詢改成只取 `RecordId > 上次游標` 的新增／變更列；對應讀取沿用 Pipeline 已算的 `plan.DeviceToHost`（但 `DeviceToHost` 已過濾評估主機範圍，觸發式用的是「全部 ok 對應主機」，兩者範圍不同，需分開；只能共用「該日整份對應列」）。
- 影響：`IAnalysisRecordQuery.QueryLightweight` 被多處使用（`RecordListQueryService.ToListItem` 等，EfAnalysisRecordStore.cs:1355-1357）；`PrtgBackfillRunner.cs:65`、`PrtgFullBackfillPlan.cs:55-62`、`PrtgBackfillService.cs:533` 是同族呼叫。
- 風險：中。僅激進策略受益；先確認生產設定是否為保守（預設）。

### C-11 [中] workflow 對帳在 finally 逐日逐主機 1 讀＋1 寫
- 現況：Pipeline:943-964；`Get`（HostDayWorkflowState.cs:115）＋`SetPrtg`（494-500，永遠寫，`PrtgAttemptAtUtc` 每次變）。
- 成本隨：回望日數 × touched 主機數（≈ 全部主機）。
- 等價改法：狀態不變（Parent 非 Succeeded 直接 `continue` 已有；`prtgState` 與既有相同且 `PrtgFailure` 相同時略過寫入——但 `PrtgAttemptAtUtc` 是否被讀端消費要先 grep，若有消費端則不能略過）。
- 影響：`HostDayWorkflowService`、AI 排程與郵件冪等依賴 `PrtgAttemptAtUtc`／`PrtgEvidenceVersion`（`HostDayWorkflowFingerprint`，CLAUDE.md 紅線「工作流父指紋」）；`HostDayWorkflowRecoveryHostedService`。
- 風險：中高。工作流是 AI／郵件腿的狀態機，不屬 PRTG 專屬，這裡只標出成本，建議歸給別條流程的盤點決定。

### C-12 [中] `TryRefreshToday` 每次冷快取＋整份重算並被多入口觸發
- 現況：PrtgHostMapRefresher.cs:50-69（每次 `new HostStore(_backend.Blob("hosts"))`＋`MapForDate`＝`GetAllDevices` 全表＋`GetManualMaps`＋`GetIpExcludes`＋對每台裝置 DNS 解析＋`ReplaceHostMapForDate` 整日替換）。入口：每次主機異動（HostAdminService.cs:415/693/719）、NetiqHostService.cs:257、人工對應／排除（PrtgHostMappingAdminController.cs:265/382/411/478/515）。批次匯入／探索若逐台觸發會逐台重算整份（未追到批次入口是否合併呼叫——**不確定**）。
- 成本隨：每次異動 × PRTG 全站裝置數（對應表整日重寫）。
- 等價改法：改用 DI 的單例 `IHostStore`（有版本快取）；多次異動在短視窗內合併成一次重算（需產品確認「立即可見」語意）。
- 影響：同一對應表被夜間路徑、觸發式取數、趨勢評估讀取；對應版本遞增會使監看政策頁 fence 失效（§5.2）與校準報告過期。
- 風險：中。立即性是使用者預期（回應內夾 `RemapWarning`）。

### C-13 [低] `PrtgRuleEvaluator` 每日重建字典與 `FoldByDevice` 二次方
- 現況：PrtgRuleEvaluator.cs:83-86、138-140、358-390。
- 等價改法：由呼叫端預建 `sensorCategories/sensorTypes`（輸入每日相同），`FoldByDevice` 先 `GroupBy(DeviceObjid)`。
- 影響：`Evaluate` 公開簽章被 Pipeline 與 CalibrationService 呼叫；純函式、可單元測試。
- 風險：低；收益低。

### C-14 [低] 歷史弱佐證重評每批 Bump
- 現況：PrtgRiskReviewHostedService.cs:23（只要 `changed > 0`），`RunBatch` 的 `count++` 不論有無實際修改（PrtgRiskProjectionStore.cs:58）。
- 等價改法：回傳「真正被改寫內容的列數」才 Bump；或整輪結束 Bump 一次。
- 影響：`DataVersionStamp` 下游全部快取（§5.2）；每批 100 筆 × 數千批的全站快取失效。
- 風險：低。

### C-15 [低] 監看政策頁 Preview 的 N+1 與指紋
- 現況：PrtgMonitoringController.cs:372-374（每次序列化全主機＋SHA256）、405（每列 `ReadRecent`）。
- 影響：只影響該頁；已屬讀取端。
- 風險：低；非夜間路徑。

---

## 8. 不確定或沒讀到

- 沒量測，所有「列數／毫秒」都是由程式碼推得：`lf_prtg_values`、`lf_prtg_state_changes`、`lf_prtg_host_map` 在 3000 台／180 天的實際列數與 SQL Server 執行計畫（C-1、C-3、C-4 的「推論」均需以實際查詢計畫或 `SqlPerformanceMonitor` 的 `prtg:*` 計時驗證；`Store` 內 `_performance.Measure("prtg:…")` 已有埋點，部分查詢無埋點，如 `GetHostMapForDay`、`GetOkHostMapDates`、`GetReadinessValues`、`GetReadinessMaps`）。
- `AttachCase` → `AttachNewDay` → `DispatchDay`（HostDayPostProcessor.cs:240-304 之後的 `IssueCaseCoordinator`／`NightlyDispatch` 內部）的資料庫成本沒追，屬交辦／案件流程。
- `backend.RecordStore(new HostKey{...})` 的建構成本沒讀（Pipeline:745，每台有 finding 的主機每日一次）。
- `PrtgCrossDay.ApplyDetailed`、`PrtgFindingMapper`、`SuppressionFilter.ActiveForHost` 的複雜度沒讀（Pipeline:711-727，每主機每日呼叫 `ActiveForHost(allSuppressions, …, DateTime.Now)`，對每個主機掃全部抑制）。
- `PrtgBackfillService`／`PrtgBackfillRunner`／`PrtgFullBackfillPlan`／`PrtgProbe*`／`PrtgResourceGuard*`／`CalibrationService`／`PrtgScopeEstimateController`／`PrtgHostMappingAdminController` 只列呼叫點，沒有逐步讀完；歷史回填逐日是否同樣有 §2.2 的重複讀取沒有確認（它也呼叫 `GetHostMapForDay` 逐日，PrtgBackfillRunner.cs:65）。
- `RetentionPruner`（RetentionPruner.cs:193）如何排程 PRTG `Prune`、以及它與夜間路徑／快照寫入是否互相擋住沒有追（`Prune` 對 `lf_prtg_values` 用 `BatchedPrune.Run`＋`Count(v => v.CreatedAt < cutoff)`，Store:1081-1092）。
- `PrtgSnapshotHostedService.BeforeOperationScopeCapture`、`PrtgSnapshotAlert`、`PrtgSnapshotDiagnosticsService` 讀端、`HostPrtgDetailService`、`PrtgEffectivenessService`、`PrtgConflictQueryService` 等 Web 讀取端沒讀。
- 「`Revision` 是否保證每次寫入都變」（C-2 的等價改法 1）只確認了 `SystemSettingsStore.Touch`（ISystemSettingsStore.cs:21-25）；匯入／其他寫入路徑沒查。
- 快照 `Checkpoint` 的實際次數取決於 `HttpContent` 流每次 `ReadAsync` 回傳大小，4096 位元組 chunk 的次數為上界推論，未實測。
- `PrtgFetchConcurrency` 是否有最大值夾制沒找到（只看到預設值 2，SystemSettings.cs:429-432；`SystemSettingsService.cs:477` 設定時的驗證沒讀），這影響 §4.1「等待者很少」的前提。
- `SqlPerformanceMonitor` 是否在 `lf_prtg_*` 之外的 blob 讀寫（診斷 blob、佇列 blob、設定 blob）有埋點，沒查；若沒有，C-2、C-5、C-7 的收益在現有監控上看不到。
