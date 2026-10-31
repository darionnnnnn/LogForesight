using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Analysis;
using System.Diagnostics;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 每日路徑（docs/PRTG-SPEC.md §3）：結構與狀態變更同步 → 主機對應 → 規則評估 →
/// 發佈 finding 登錄簿並補追加 → 觸發式數值取數。與本機／NetIQ 分析**並行**執行。
///
/// 從 <see cref="AnalysisOrchestrator"/> 抽出：整條 pipeline 連同五個階段各自的容錯
/// 原本是 orchestrator 裡的一個三百行方法，讓那個檔案同時是「三路並行的組裝點」與
/// 「PRTG 的實作細節」。抽出後 orchestrator 只剩組裝與 Task.WhenAll。
///
/// **失敗隔離是這條路徑的核心契約**：每個階段各自 try/catch，PRTG 出任何問題都不能讓
/// 本機與 NetIQ 的分析成果作廢；只有取消訊號穿透。
/// </summary>
internal static class PrtgDailyPipeline
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <param name="manualWorkGate">
    /// 手動 PRTG 作業（同步結構與對應、歷史回填）的閘門（docs/PRTG-SPEC.md §5a）。Core 不認識 Web 的服務，
    /// 由呼叫端注入；未接上時傳 null＝行為與沒有這個機制時完全相同。
    /// 同步成功結束時跳過本趟結構同步——鏡像剛更新過，重做一次沒有意義；回填則最多等一段時間。
    /// </param>
    /// <param name="hostIds">
    /// 監看裝置只算這些主機（指定主機更新）；null＝全站。必填——漏傳時靜默退回全站，就是對整台 PRTG 做全範圍擷取。
    /// 非 null 時範圍為 partial：感測器鏡像與範圍外資料都不清除。
    /// </param>
    public static async Task RunAsync(
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, IReadOnlyList<DateTime> days, Task analysisTask,
        IReadOnlyCollection<long>? hostIds,
        PrtgResourceGuard? guard = null,
        IPrtgManualWorkGate? manualWorkGate = null,
        PrtgRequestBudget? requestBudget = null)
    {
        if (days == null || days.Count == 0)
        {
            throw new ArgumentException("日期清單至少要有一天。", nameof(days));
        }
        // 契約：純日期、由近到遠且不重複。帶時分的值會讓逐日狀態表查不到鍵；
        // 升冪傳入會讓狀態變更區間只涵蓋末幾天、對應重算對錯天，而且沒有任何徵兆。
        if (days.Any(d => d != d.Date))
            throw new ArgumentException("日期清單必須是純日期（不含時分）。", nameof(days));
        for (var i = 1; i < days.Count; i++)
        {
            if (days[i] >= days[i - 1])
                throw new ArgumentException("日期清單必須由近到遠且不重複。", nameof(days));
        }

        var newest = days[0].Date;
        var oldest = days[^1].Date;

        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress, prtgFindings, dispatch, workflow, prtgEnabled) = ctx;

        var prtgConsole = new PrefixedRunConsole(console, "[PRTG] ");
        string? prtgOutcome = null;
        var totalTriggerHosts = 0;
        var totalTargetSensors = 0;
        var totalFailedSensors = 0;
        var totalValuesWritten = 0;
        // 數值趨勢評估（磁碟／記憶體／CPU／Ping）擲例外的類別；任一類失敗整趟為 Partial，最新日說明列出類別
        var failedTrendLabels = new List<string>();
        string? interruptedDayReason = null;

        // 逐日累計（規則評估、歸戶、觸發式取數）在 finally 才寫進執行紀錄：
        // 中途被停止或擲例外時，已經評估完的日子也要留下逐日結果，執行總表才不會整排「—」。
        var dayStates = new Dictionary<DateTime, PrtgDayState>();
        PrtgFetchResult? fetchResult = null;

        // 趨勢評估的失敗外殼：取消穿透；其他例外記 Log 與主控台，並把類別記到整趟與最新日（任一類失敗＝Partial）。
        // 評估本體各類自行保留（閾值型別、finding 建構與規則選取不同）；規則缺失或沒有候選 sensor 不是失敗，不會進到這裡。
        void EvaluateTrend(string label, Action evaluate)
        {
            try
            {
                evaluate();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failedTrendLabels.Add(label);
                if (dayStates.TryGetValue(newest, out var newestState)) newestState.TrendFailedLabels.Add(label);
                Log.Error(ex, "PRTG {Label}趨勢評估失敗；既有 findings 照常發布", label);
                prtgConsole.WriteLine($"  ⚠ {label}趨勢評估失敗，既有 findings 照常發布：" + ex.Message);
            }
        }
        var syncFailed = false;
        // 逐日「無產出」判定要用的事實（在 try 內決定，finally 寫逐日結果時用）
        var rulesAvailableForStat = true;
        var sensorMirrorEmpty = false;
        var conservativeStrategy = false;

        var parentToken = ct;
        var planned = new Dictionary<DateTime, PrtgPlannedDay>();
        PrtgOperationScope? operation = null;
        try
        {
            var systemSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            // 策略一讀到設定就定：連線建立前就失敗或取消的那趟，紀錄裡的策略才不會是預設的激進
            conservativeStrategy = !PrtgFetchStrategy.Profile(systemSettings.PrtgFetchStrategy).NightlyExactValues;
            if (!systemSettings.PrtgEnabled ||
                string.IsNullOrWhiteSpace(systemSettings.PrtgUrl) ||
                !PrtgClientFactory.HasUsableCredentials(systemSettings))
            {
                prtgConsole.WriteLine("PRTG 未啟用或尚未設定認證資訊，略過。");
                prtgOutcome = BatchRun.PrtgOutcomeDisabled;
                return;
            }

            try
            {
                var guardObjidsBefore = systemSettings.PrtgResourceGuardSensorObjids;
                var autoDetected = await PrtgResourceGuardAutoDetect.EnsureAsync(
                    backend,
                    systemSettings,
                    new SentinelStore(backend.Blob("sentinels")).GetAll(),
                    prtgConsole,
                    () => PrtgClientFactory.Create(systemSettings, budget: requestBudget),
                    parentToken);
                var reloadedSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                if (!PrtgResourceGuardAutoDetect.SetEquals(reloadedSettings.PrtgResourceGuardSensorObjids, guardObjidsBefore))
                {
                    systemSettings = reloadedSettings;
                }
                // 守門實例在批次啟動時就以舊清單（空）建好了：偵測到的目標直接換上，當晚就守得到
                if (autoDetected != null) guard?.ReplaceTargets(autoDetected);
            }
            catch (OperationCanceledException) when (parentToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                prtgConsole.WriteLine($"[PRTG資源守門] 自動偵測未完成（{ex.Message}），本趟不守門，取數照常進行。");
            }

            using var operationScope = new PrtgOperationScope(systemSettings,
                () => new SystemSettingsStore(backend.Blob("system_settings")).Get(), parentToken, new PrtgScopeRevisionReader(backend, hostStore).Read, "每日評估");
            operation = operationScope;
            ct = operationScope.Token;
            operationScope.Checkpoint();
            using var client = PrtgClientFactory.Create(systemSettings, budget: requestBudget);
            client.OperationCheckpoint = operationScope.Checkpoint;
            // 監看政策的主機／sensor 清單是可選縮圈：空＝全部已對應主機；有主機清單時本趟範圍只含清單內的啟用主機。
            var monitoringPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var evaluationHostSnapshot = hostStore.CapturePrtgSnapshot();
            var evaluationHosts = evaluationHostSnapshot.Hosts.ToDictionary(host => host.HostId, host => host.ToWebHost());
            var requestedHostIds = hostIds;
            if (monitoringPolicy.HostIds.Count > 0)
                hostIds = SelectActivePilotHostIds(monitoringPolicy.HostIds, requestedHostIds, () => evaluationHosts.Values.ToList());

            var fetchService = PrtgFetchService.Create(client, backend, backend.PrtgStore(), systemSettings, prtgConsole, guard);

            var strategyProfile = PrtgFetchStrategy.Profile(systemSettings.PrtgFetchStrategy);
            var strategyLabel = strategyProfile.NightlyExactValues ? "激進" : "保守";
            prtgConsole.WriteLine($"PRTG 取數策略：{strategyLabel}（快照間隔 {strategyProfile.SnapshotIntervalMinutes} 分鐘）。");

            // 0. 手動 PRTG 作業佔用中就先等它（docs/PRTG-SPEC.md §5a）：同步成功結束時鏡像是最新的，
            //    本趟跳過自己的結構同步；回填與本趟同時打同一台 PRTG 會互相拖慢，最多等一段時間。
            var skipStructureSync = false;
            var running = manualWorkGate?.Running ?? PrtgManualWork.None;
            if (running != PrtgManualWork.None)
            {
                prtgConsole.WriteLine(running == PrtgManualWork.StructureSync
                    ? $"手動觸發的「同步結構與對應」進行中（自 {DateTime.Now:HH:mm} 起等待），等它完成後再繼續（本趟不重複同步結構）。"
                    : $"PRTG 歷史回填進行中（自 {DateTime.Now:HH:mm} 起等待），最多等 {IPrtgManualWorkGate.BackfillMaxWait.TotalMinutes:0} 分鐘；可在排程作業頁 PRTG 卡停止回填。");
                runRecorder.Milestone(running == PrtgManualWork.StructureSync
                    ? "PRTG：等待手動同步結構與對應完成"
                    : "PRTG：等待歷史回填結束");
                progress?.Report(RunPhases.PrtgWaitSync, 0, 0);
                var outcome = await manualWorkGate!.WaitUntilIdleAsync(running, ct);
                skipStructureSync = outcome == PrtgManualWaitOutcome.MirrorFresh;
                switch (outcome)
                {
                    case PrtgManualWaitOutcome.MirrorFresh:
                        prtgConsole.WriteLine("手動同步已完成，沿用剛更新的鏡像結構。");
                        break;
                    case PrtgManualWaitOutcome.SyncNotFresh:
                        // 兩種情況共用這條路：等到上限對方還沒結束（PRTG 端卡住），
                        // 或它結束了但沒成功（被中止、有階段失敗）。兩者的鏡像都不能當成新的，
                        // 本趟照常自己同步。寫成「已完成、沿用」會讓這一晚鏡像其實不完整卻處處顯示正常。
                        prtgConsole.WriteLine("  ⚠ 手動同步未成功結束（卡住、被中止或有階段失敗）；本趟改為自行同步結構。");
                        runRecorder.Milestone("PRTG：手動同步未成功結束，改為自行同步結構");
                        break;
                    case PrtgManualWaitOutcome.BackfillEnded:
                        prtgConsole.WriteLine("歷史回填已結束，繼續本趟 PRTG 取數。");
                        break;
                    case PrtgManualWaitOutcome.BackfillStillRunning:
                        prtgConsole.WriteLine($"  ⚠ 歷史回填等了 {IPrtgManualWorkGate.BackfillMaxWait.TotalMinutes:0} 分鐘仍在執行，PRTG 查詢將與它同時進行。");
                        runRecorder.Milestone("PRTG：回填仍在執行，PRTG 查詢將與它同時進行");
                        break;
                }
            }

            // 1. 結構同步＋狀態變更只做一次（區間覆蓋 oldest.AddDays(-1) 到今天，目標日為 newest）
            // 2. PRTG 主機對應重算只對 newest，在 scopeProvider 裡做（裝置同步之後、感測器同步之前）：
            //    一律重算：鏡像在同步失敗時不會縮小（upsert 只增不減、過期清除只在裝置階段完整成功後才做），
            //    拿既有鏡像重算是安全的，且能反映白天主機主檔的異動。裝置未更新時只多印一行說明。
            //    FetchDayAsync 各階段自帶 catch，取消以外幾乎不會整個擲出；真的擲出而 provider 沒被呼叫時
            //    mapResult 為 null，不另外補做對應。
            PrtgHostMapResult? mapResult = null;
            var syncStopwatch = Stopwatch.StartNew();
            try
            {
                progress?.Report(RunPhases.PrtgSync, 0, 0);
                fetchResult = await fetchService.FetchDayAsync(
                    newest, systemSettings.PrtgFetchConcurrency, ct,
                    devicesRefreshed =>
                    {
                        var mirrorStore = backend.PrtgStore();
                        var resolver = new PrtgAddressResolver();
                        if (!devicesRefreshed && !skipStructureSync)
                        {
                            prtgConsole.WriteLine("  ⚠ 裝置結構本趟未成功更新，主機對應依既有鏡像重算。");
                        }
                        try
                        {
                            var hostMapper = new PrtgHostMapper(mirrorStore, hostStore, prtgConsole, resolver);
                            mapResult = hostMapper.MapForDate(newest);
                            runRecorder.Milestone($"PRTG 主機對應完成（{newest:yyyy-MM-dd}）：{mapResult.HostSummary}");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "PRTG 主機對應失敗，不影響擷取與分析成果");
                            prtgConsole.WriteLine($"\n  ✗ PRTG 主機對應失敗：{ex.Message}");
                        }
                        var sentinels = new SentinelStore(backend.Blob("sentinels")).GetAll();
                        var scope = PrtgScopeDevices.Compute(mirrorStore, hostStore, new PrtgMirrorGuardSource(mirrorStore),
                            systemSettings, sentinels, prtgConsole, resolver, hostIds, includeGuardInPartial: requestedHostIds == null);
                        if (scope.IsPartial)
                        {
                            prtgConsole.WriteLine($"本趟 PRTG 只處理指定的 {hostIds!.Count} 台主機（{scope.DeviceObjids.Count} 台裝置）。");
                        }
                        return scope;
                    },
                    syncStructure: !skipStructureSync, fetchValues: false,
                    progress: (stage, done, total) => progress?.Report(stage, done, total),
                    stateChangesFrom: oldest.AddDays(-1));

                // 只報監看裝置（主機清單內的主機對應到的裝置與守門目標），不報 PRTG 全站裝置數（docs/PRTG-SPEC.md §1）
                var scopeText = fetchResult.Scope is { } fetchScope ? $"監看裝置 {fetchScope.DeviceObjids.Count} 台、" : "";
                var summary = $"PRTG 每日擷取完成（{newest:yyyy-MM-dd}）：{scopeText}感測器 {fetchResult.Sensors}、" +
                              $"狀態變更新增 {fetchResult.StateChanges}、數值 {fetchResult.Values}" +
                              (fetchResult.Failures > 0 ? $"、失敗階段 {fetchResult.Failures}" : "");

                prtgConsole.WriteLine(summary);
                runRecorder.Milestone(summary);
                // 失敗原因進「重要紀錄」：只計數的話執行詳情只看得到「部分失敗」，不知道是哪個階段
                foreach (var failure in fetchResult.StageFailures)
                {
                    runRecorder.Milestone($"PRTG 擷取階段失敗：{(failure.Length > 300 ? failure[..299] + "…" : failure)}");
                }
            }
            catch (OperationCanceledException)
            {
                // 取消訊號必須穿透，讓上層統一處理中斷
                throw;
            }
            catch (Exception ex)
            {
                syncFailed = true;
                // 與 NetIQ 機房分析的失敗邊界一致：PRTG 擷取出問題不該讓整趟分析失敗，
                // 外部系統失聯或異常不影響本機與 NetIQ 的分析成果，只記錄失敗留給下次排程或手動回補
                Log.Error(ex, "PRTG 每日擷取失敗，本機與 NetIQ 分析結果不受影響");
                prtgConsole.WriteLine($"\n  ✗ PRTG 每日擷取失敗：{ex.Message}（本機與 NetIQ 分析結果不受影響）");
            }

            syncStopwatch.Stop();
            operationScope.CompletedStage("來源同步已返回（詳見成功／失敗狀態）");

            operationScope.Checkpoint();

            // 結構同步與主機對應都成功時，將狀態寫入 blob（保留手動同步時相同的結構資訊）
            // 任一條件不成立就不寫（保留上一次狀態），失敗只記 Log.Warn 不影響其他階段
            var nightlySyncStatus = BuildNightlySyncStatus(
                skipStructureSync,
                fetchResult,
                syncFailed,
                mapResult,
                newest,
                syncStopwatch.Elapsed,
                DateTime.Now);

            if (nightlySyncStatus != null)
            {
                try
                {
                    var syncStatusStore = new PrtgStructureSyncStatusStore(backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
                    syncStatusStore.Update(existing => existing.CopyFrom(nightlySyncStatus));
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "夜間結構同步狀態寫入 blob 失敗，不影響 PRTG 其他階段與 outcome");
                }
            }

            // 本趟自己的結構同步與主機對應全部成功，才考慮清除監看範圍外的數值與狀態變更（其餘保護在 PrtgScopePurge 內）
            if (!skipStructureSync && !syncFailed && fetchResult is { Failures: 0 } && mapResult != null)
            {
                var parentRunId = runRecorder.RunId;
                PrtgScopeResult? finalScope = fetchResult.Scope;
                if (fetchResult.DevicesRefreshed && finalScope is { IsPartial: false })
                {
                    // FetchDayAsync computes the scope between device and sensor sync. Re-read it only
                    // after all successful stages so the automatic purge fence includes this parent's
                    // final sensor/resource mirror and preserve set.
                    var purgeStore = backend.PrtgStore();
                    finalScope = PrtgScopeDevices.Compute(purgeStore, hostStore, new PrtgMirrorGuardSource(purgeStore),
                        new SystemSettingsStore(backend.Blob("system_settings")).Get(),
                        new SentinelStore(backend.Blob("sentinels")).GetAll(), prtgConsole,
                        new PrtgAddressResolver(), hostIds: null);
                }
                PrtgScopePurge.RunAfterStructureSync(backend.PrtgStore(), finalScope, fetchResult.DevicesRefreshed,
                    parentRunId.ToString(System.Globalization.CultureInfo.InvariantCulture), prtgConsole, ct,
                    () => runRecorder.OwnsRun(parentRunId));
            }

            // 日期範圍訊號（不是進度）：Web 端據此擋住本趟範圍內的 AI 待補、顯示第 i／N 天。迴圈前先送一次「尚未開始逐日」
            progress?.Report(RunPhases.PrtgDateRange, days.Count, 0);

            // 規則庫判定（迴圈前判一次、印一次既有訊息，所有日期發佈空結果）
            var prtgRules = KnownIssueCatalog.Rules
                .Where(r => string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && r.Enabled)
                .ToList();

            var rulesAvailable = prtgRules.Any(r => !string.IsNullOrEmpty(r.PrtgRuleCode));
            rulesAvailableForStat = rulesAvailable;
            if (!rulesAvailable)
            {
                prtgConsole.WriteLine("規則庫尚無啟用中的 PRTG 規則（升級後請至「規則維護」頁套用內建規則更新），本次略過規則評估。");
            }

            var prtgStore = backend.PrtgStore();
            // 規則評估母體＝鏡像中的感測器＝取數範圍內裝置的未暫停感測器（感測器鏡像只同步範圍內裝置）。
            // 取數白名單不參與：它是為數值取數量體設計的（預設不含 Ping），拿來過濾規則母體會讓主機失聯（Ping Down）永遠命中不了。
            var allSensors = prtgStore.GetSensorStatuses();
            sensorMirrorEmpty = allSensors.Count == 0;
            var sensorNames = allSensors
                .GroupBy(s => s.Objid)
                .ToDictionary(g => g.Key, g => g.First().SensorName);
            var deviceNames = prtgStore.GetAllDevices()
                .GroupBy(d => d.Objid)
                .ToDictionary(g => g.Key, g => g.First().Name);

            var sensorToDevice = allSensors
                .GroupBy(s => s.Objid)
                .ToDictionary(g => g.Key, g => g.First().DeviceObjid);

            var sensorStatuses = allSensors
                .Select(s => new PrtgSensorStatusInput(s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Category))
                .ToList();
            var reportedDuplicateRuleWarnings = new HashSet<string>();

            // 最新一天用剛重算的當日對應；過去日取「該日或之前最近一日」的既有對應，沒有時沿用之後最早的一份（docs/PRTG-SPEC.md §4）。
            // 取法與維護頁就緒度、規則試算同一個（PrtgValueAssessment.ResolveHostMapRows），規則歸戶與數值評估看到的主機才會一致。
            // 回傳的日期晚於 d 即為沿用。

            // 包裝層：合成問題靜音項目（以紀錄日 day 判定，見下方逐主機標記）
            var allSuppressions = new MuteAwareSuppressionStore(
                new SuppressionStore(backend.Blob("suppressions")), new IssueOwnerStore(backend.Blob("issue_owners"))).LoadAll();
            var allMutes = SuppressionFilter.MutesOf(allSuppressions);

            // 評估主機：啟用中、未合併；指定主機更新（或監看政策的主機縮圈）時只算那幾台。
            var evaluationHostIds = evaluationHosts.Values
                .Where(h => h.Active && h.MergedInto == null && (hostIds == null || hostIds.Contains(h.HostId)))
                .Select(h => h.HostId).ToHashSet();
            // 監看政策的 sensor 清單是可選縮圈：空＝不限。silent 是裝置層判定，看的是整台裝置，不受此清單限制。
            var policySensorIds = monitoringPolicy.SensorIds.ToHashSet();

            // 兩段式：處理最新一天的當下，較舊日期的 finding 還沒評估也還沒寫進資料庫，
            // 跨日判定只查資料庫會少算。所以先對全部日期評估並歸戶（不發佈），再逐日標註、抑制、發佈、追加。
            // 第一段（由近到遠）：主機對應、狀態型規則評估、映射成簽章、歸戶。不發佈、不追加。
            for (var i = 0; i < days.Count; i++)
            {
                operationScope.Checkpoint();
                // 逐日評估是整條路徑最耗時的段落（每天一次狀態變更查詢），進度在這裡報；第二段只發佈與追加，不再重報
                ct.ThrowIfCancellationRequested();
                progress?.Report(RunPhases.PrtgDateRange, days.Count, i + 1);
                var day = days[i].Date;
                var state = dayStates[day] = new PrtgDayState();
                var plan = planned[day] = new PrtgPlannedDay();
                if (days.Count > 1)
                {
                    prtgConsole.WriteLine($"第 {i + 1}／{days.Count} 天（{day:yyyy-MM-dd}）");
                }

                try
                {
                    var (mapDate, mapRows) = PrtgValueAssessment.ResolveHostMapRows(prtgStore, day, newest);
                    var deviceToHost = DeviceToHost(mapRows, evaluationHostIds);
                    if (mapDate is { } borrowed && borrowed.Date > day.Date && deviceToHost.Count > 0)
                    {
                        state.MapBorrowedFrom = borrowed.Date;
                        prtgConsole.WriteLine($"{day:yyyy-MM-dd} 之前沒有主機對應，沿用 {borrowed:yyyy-MM-dd} 的對應");
                    }
                    plan.DeviceToHost = deviceToHost;
                    state.MapAvailable = deviceToHost.Count > 0;

                    // 規則庫沒有 PRTG 規則：該日照樣發佈空結果（「算不出東西」與「還沒算完」要分得出來）
                    if (!rulesAvailable)
                    {
                        plan.DayEvaluated = true;
                        continue;
                    }

                    var changes = prtgStore.GetStateChanges(day.AddDays(-1), day.AddDays(1))
                        .Where(change => policySensorIds.Count == 0 || policySensorIds.Contains(change.SensorObjid))
                        .ToList();
                    var findings = PrtgRuleEvaluator.Evaluate(
                        day, changes, sensorToDevice, sensorStatuses, prtgRules,
                        sensorNames, deviceNames, includeSilent: day == newest);
                    foreach (var warning in findings.DuplicateRuleWarnings)
                    {
                        if (reportedDuplicateRuleWarnings.Add(warning))
                        {
                            prtgConsole.WriteLine(warning);
                        }
                    }
                    state.Findings = findings.Count;

                    if (!state.MapAvailable)
                    {
                        // 往回與往後都沒有任何對應（從未完成過一次主機對應）
                        prtgConsole.WriteLine($"{day:yyyy-MM-dd} 無主機對應可用（尚未完成任何一次主機對應），PRTG finding 未歸戶");
                        plan.DayEvaluated = true;
                        continue;
                    }

                    var findingsByHost = new Dictionary<long, List<LogIssueSignature>>();
                    foreach (var finding in findings)
                    {
                        if (deviceToHost.TryGetValue(finding.DeviceObjid, out var hostId))
                        {
                            AddFinding(findingsByHost, hostId, PrtgFindingMapper.ToSignature(finding, day, hostId));
                            state.TriggerHosts.Add(hostId);
                        }
                    }
                    state.AttributedHosts = findingsByHost.Count;
                    plan.FindingsByHost = findingsByHost;
                    plan.DayEvaluated = true;
                    // 折疊前總數：被合併掉的不在 findings 裡，「其中已合併 c 筆」才會是 N 的子集
                    plan.FindingCount = findings.Count + findings.MergedCount;
                    plan.AcknowledgedCount = findings.Count(f => f.Acknowledged);
                    plan.MergedCount = findings.MergedCount;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    plan.FindingsByHost = null;
                    plan.DayEvaluated = false;
                    Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
                }
            }

            // 數值資料就緒度：與維護頁就緒度卡同一個計算與同一種對應取法（DeviceToHostOn），兩處數字才會相同。
            var semanticOverrides = PrtgSensorTypeCategoryMap.ParseSemanticOverrides(systemSettings.PrtgSensorTypeCategoryOverrides);
            if (newest < DateTime.Today)
            {
                try
                {
                    var readinessAssessment = new PrtgValueAssessment(prtgStore, semanticOverrides);
                    var readinessDay = DateOnly.FromDateTime(newest);
                    var readiness = PrtgValueReadiness.Compute(readinessAssessment,
                        readinessAssessment.DeviceToHostOn(readinessDay, readinessDay, evaluationHostIds), evaluationHostIds,
                        policySensorIds, readinessDay, ct);
                    prtgConsole.WriteLine(PrtgValueReadiness.FormatLine(readiness));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn(ex, "數值資料就緒度計算失敗");
                    prtgConsole.WriteLine("數值資料就緒度計算失敗：" + ex.Message);
                }
            }

            // 數值規則（目前為 disk_free_trend）只讀評估前已落盤的完整日，只評估最新的已完成日；
            // 不等待本趟觸發式取數、不接觸 PRTG API；每頁最多 100 顆（docs/PRTG-SPEC.md §9b）。
            var diskRule = SelectSingleRule(prtgRules, PrtgRuleEvaluator.RuleDiskFreeTrend, PrtgSensorCategories.Disk, prtgConsole);
            if (diskRule?.PrtgDiskTrendThresholds is { } diskThresholds && newest < DateTime.Today &&
                planned.TryGetValue(newest, out var newestPlan) && newestPlan.DeviceToHost.Count > 0)
            {
                EvaluateTrend("磁碟", () =>
                {
                    var assessment = new PrtgValueAssessment(prtgStore, semanticOverrides);
                    var completedDay = DateOnly.FromDateTime(newest);
                    var candidates = assessment.Candidates(newestPlan.DeviceToHost, evaluationHostIds, policySensorIds,
                        PrtgSensorCategories.Disk);
                    var reasons = new Dictionary<PrtgValueReason, int>();
                    var added = 0;
                    foreach (var page in candidates.Chunk(PrtgValueAssessment.PageSize))
                    {
                        operationScope.Checkpoint(); ct.ThrowIfCancellationRequested();
                        var pageDeviceNames = prtgStore.GetDeviceNamesByObjids(page.Select(s => s.DeviceObjid).Distinct().ToList());
                        foreach (var row in assessment.AssessPage(page, completedDay,
                                     diskThresholds.RecentWindowDays, diskThresholds.MinimumValidDays, completedDay))
                        {
                            reasons[row.Reason] = reasons.GetValueOrDefault(row.Reason) + 1;
                            var deviceName = PrtgValueAssessment.DeviceDisplayName(pageDeviceNames, row.Sensor.DeviceObjid);
                            if (PrtgValueAssessment.DiskTrendFinding(row, diskRule, deviceName, completedDay, out _) is not { } finding) continue;
                            newestPlan.FindingsByHost ??= new Dictionary<long, List<LogIssueSignature>>();
                            AddFinding(newestPlan.FindingsByHost, row.Sensor.HostId,
                                PrtgFindingMapper.ToSignature(finding, newest, row.Sensor.HostId));
                            dayStates[newest].TriggerHosts.Add(row.Sensor.HostId);
                            added++;
                        }
                    }
                    newestPlan.FindingCount += added;
                    dayStates[newest].Findings += added;
                    dayStates[newest].AttributedHosts = newestPlan.FindingsByHost?.Count ?? 0;
                    prtgConsole.WriteLine(candidates.Count == 0
                        ? $"磁碟趨勢評估（{newest:yyyy-MM-dd}）：無可評估 sensor（範圍內沒有已對應的磁碟 sensor）。"
                        : $"磁碟趨勢評估（{newest:yyyy-MM-dd}）：{candidates.Count} 顆，" +
                          string.Join("、", reasons.OrderBy(pair => pair.Key).Select(pair =>
                              $"{PrtgValueReasonText.Label(pair.Key)} {pair.Value}")) + $"；新增 finding {added} 筆。");
                });
            }

            // 數值趨勢規則（記憶體／CPU／Ping）
            if (newest < DateTime.Today && planned.TryGetValue(newest, out var trendPlan) && trendPlan.DeviceToHost.Count > 0)
            {
                var otherTrendCodes = new[]
                {
                    PrtgRuleEvaluator.RuleMemoryUsedTrend,
                    PrtgRuleEvaluator.RuleCpuLoadTrend,
                    PrtgRuleEvaluator.RulePingLatencyTrend
                };

                foreach (var code in otherTrendCodes)
                {
                    var category = PrtgRuleCatalog.TrendSensorCategory(code);
                    if (category is null) continue;
                    var rule = SelectSingleRule(prtgRules, code, category, prtgConsole);
                    if (rule?.PrtgTrendThresholds is not { } thresholds) continue;

                    var label = code switch
                    {
                        PrtgRuleEvaluator.RuleMemoryUsedTrend => "記憶體使用",
                        PrtgRuleEvaluator.RuleCpuLoadTrend => "CPU 負載",
                        PrtgRuleEvaluator.RulePingLatencyTrend => "Ping 延遲",
                        _ => code
                    };

                    EvaluateTrend(label, () =>
                    {
                        var assessment = new PrtgValueAssessment(prtgStore, semanticOverrides);
                        var completedDay = DateOnly.FromDateTime(newest);
                        var candidates = assessment.Candidates(trendPlan.DeviceToHost, evaluationHostIds, policySensorIds, category);
                        var reasons = new Dictionary<PrtgValueReason, int>();
                        var added = 0;
                        foreach (var page in candidates.Chunk(PrtgValueAssessment.PageSize))
                        {
                            operationScope.Checkpoint(); ct.ThrowIfCancellationRequested();
                            var pageDeviceNames = prtgStore.GetDeviceNamesByObjids(page.Select(s => s.DeviceObjid).Distinct().ToList());
                            foreach (var row in assessment.AssessPage(page, completedDay,
                                         thresholds.WindowDays, thresholds.MinimumValidDays, completedDay))
                            {
                                reasons[row.Reason] = reasons.GetValueOrDefault(row.Reason) + 1;
                                var deviceName = PrtgValueAssessment.DeviceDisplayName(pageDeviceNames, row.Sensor.DeviceObjid);
                                if (PrtgValueAssessment.TrendFinding(row, rule, deviceName, completedDay, out _) is not { } finding) continue;
                                trendPlan.FindingsByHost ??= new Dictionary<long, List<LogIssueSignature>>();
                                AddFinding(trendPlan.FindingsByHost, row.Sensor.HostId,
                                    PrtgFindingMapper.ToSignature(finding, newest, row.Sensor.HostId));
                                dayStates[newest].TriggerHosts.Add(row.Sensor.HostId);
                                added++;
                            }
                        }
                        trendPlan.FindingCount += added;
                        dayStates[newest].Findings += added;
                        dayStates[newest].AttributedHosts = trendPlan.FindingsByHost?.Count ?? 0;
                        prtgConsole.WriteLine(candidates.Count == 0
                            ? $"{label}趨勢評估（{newest:yyyy-MM-dd}）：無可評估 sensor（範圍內沒有已對應的 {category} sensor）。"
                            : $"{label}趨勢評估（{newest:yyyy-MM-dd}）：{candidates.Count} 顆，" +
                              string.Join("、", reasons.OrderBy(pair => pair.Key).Select(pair =>
                                  $"{PrtgValueReasonText.Label(pair.Key)} {pair.Value}")) + $"；新增 finding {added} 筆。");
                    });
                }
            }

            // 跨日歷史：本趟全部日期的 EventKey 合併後一次查詢（查詢內部依 500 分批），各日再切自己的窗口。
            // 查詢失敗只影響標註與升級，不影響發佈與追加。
            var dbHitDates = new Dictionary<string, HashSet<DateTime>>(StringComparer.Ordinal);
            var allEventKeys = planned.Values
                .Where(p => p.FindingsByHost != null)
                .SelectMany(p => p.FindingsByHost!.Values.SelectMany(list => list))
                .Select(s => s.EventKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (allEventKeys.Count > 0)
            {
                try
                {
                    // 靜音不排除：跨日判定是分析側事實（sensor 連續幾天命中），不是讀取側的顯示
                    dbHitDates = backend.IssueAggregateQuery(hostStore).GetPrtgFindingHitDates(
                        IssueExclusion.None, allEventKeys, oldest.AddDays(-PrtgRuleCatalog.CrossDayWindowDays), newest);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PRTG 跨日歷史查詢失敗，本趟只以本趟日期判定跨日");
                    prtgConsole.WriteLine($"  ⚠ PRTG 跨日歷史查詢失敗：{ex.Message}（本趟只以本趟日期判定跨日）");
                }
            }

            // 第二段（由近到遠）：跨日標註 → 抑制標記 → 發佈 → 補追加與案件掛接
            for (var i = 0; i < days.Count; i++)
            {
                operationScope.Checkpoint();
                var day = days[i].Date;
                var plan = planned[day];

                try
                {
                    if (plan.FindingsByHost == null)
                    {
                        prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>(), new Dictionary<long, IReadOnlySet<string>>());
                        // 第一段已完整評估、只是沒有可歸戶的 finding（沒有規則、沒有任何對應）：仍是評估完成。
                        // 不標的話 NeedsReevaluation 恆為 true，排程每晚把這些日子挑回來重評、永遠收不掉。
                        if (plan.DayEvaluated) prtgFindings.MarkEvaluated(day);
                        continue;
                    }

                    var findingsByHost = plan.FindingsByHost;
                    var daySignatures = findingsByHost.Values.SelectMany(list => list).ToList();

                    // 歷史＝資料庫命中 ∪ 本趟較舊日期的已歸戶簽章。本趟有評估的日期只認本趟結果：
                    // 重跑時資料庫裡那幾天是上一趟寫的，門檻或規則改過後可能已不成立，混進來會把舊命中算成歷史。
                    var history = new Dictionary<string, HashSet<DateTime>>(StringComparer.Ordinal);
                    foreach (var key in daySignatures.Select(s => s.EventKey).Distinct(StringComparer.Ordinal))
                    {
                        history[key] = dbHitDates.TryGetValue(key, out var dates)
                            ? new HashSet<DateTime>(dates.Where(d => !(planned.TryGetValue(d.Date, out var p) && p.FindingsByHost != null)))
                            : new HashSet<DateTime>();
                    }
                    foreach (var (otherDay, otherPlan) in planned)
                    {
                        if (otherDay >= day || otherPlan.FindingsByHost == null) continue;
                        foreach (var sig in otherPlan.FindingsByHost.Values.SelectMany(list => list))
                        {
                            if (history.TryGetValue(sig.EventKey, out var dates)) dates.Add(otherDay);
                        }
                    }

                    var (escalatedCount, chronicCount) = PrtgCrossDay.Apply(daySignatures, history, day);

                    var suppressedCount = 0;
                    var suppressedPatternIdsByHost = new Dictionary<long, IReadOnlySet<string>>();
                    foreach (var (hostId, hostFindings) in findingsByHost)
                    {
                        var (hostName, hostGroupIds) = evaluationHosts.TryGetValue(hostId, out var webHost)
                            ? (webHost.HostName, (IReadOnlyCollection<long>)webHost.GroupIds)
                            : (string.Empty, (IReadOnlyCollection<long>)Array.Empty<long>());
                        var activeSuppressions = SuppressionFilter.ActiveForHost(allSuppressions, hostName, hostGroupIds, DateTime.Now);
                        suppressedCount += SuppressionFilter.MarkSuppressed(hostFindings, activeSuppressions, allMutes, day);
                        // 跨來源佐證的關聯抑制與 finding 抑制用同一份有效抑制，兩條追加路徑從登錄簿取用
                        suppressedPatternIdsByHost[hostId] = SuppressionFilter.ToCorrelationPatternIdSet(activeSuppressions);
                    }

                    prtgFindings.Publish(day, findingsByHost.ToDictionary(
                        pair => pair.Key, pair => (IReadOnlyList<LogIssueSignature>)pair.Value), suppressedPatternIdsByHost);
                    if (plan.DayEvaluated) prtgFindings.MarkEvaluated(day);

                    try
                    {
                        var involvedHosts = findingsByHost.Count;
                        var appendedHosts = 0;
                        var pendingHosts = 0;
                        var prtgOnlyHosts = 0;
                        var corroboratedCount = 0;

                        foreach (var (hostId, hostFindings) in findingsByHost)
                        {
                            operationScope.Checkpoint();
                            var hostName = evaluationHosts.TryGetValue(hostId, out var webHost) ? webHost.HostName : string.Empty;
                            var hostRecordStore = backend.RecordStore(new HostKey { HostId = hostId, HostName = hostName });

                            var hostCorroborated = 0;
                            if (prtgFindings.AttachExclusive(hostId, day, () => hostRecordStore.AttachPrtgFindings(
                                    hostId, day, hostFindings, prtgFindings.SuppressedPatternIdsFor(hostId, day), out hostCorroborated, useAi)))
                            {
                                appendedHosts++;
                                corroboratedCount += hostCorroborated;
                                HostDayPostProcessor.AttachCase(caseCoordinator, dispatch, hostName, day, hostFindings.ToList(), "[PRTG] ");
                            }
                            // 當日沒有任何紀錄（沒有日誌分析）：PRTG 自己成立一筆主機日，日誌分析晚到時原地更新為該來源。
                            else if (prtgFindings.AttachExclusive(hostId, day, () => hostRecordStore.AppendPrtgOnlyIfAbsent(
                                         hostId, hostName, day, hostFindings, useAi)))
                            {
                                prtgOnlyHosts++;
                                HostDayPostProcessor.AttachCase(caseCoordinator, dispatch, hostName, day, hostFindings.ToList(), "[PRTG] ");
                            }
                            else pendingHosts++;
                        }

                        var summary = $"PRTG 規則評估完成（{day:yyyy-MM-dd}）：finding {plan.FindingCount} 筆（其中已抑制 {suppressedCount} 筆、已於 PRTG 確認 {plan.AcknowledgedCount} 筆、已合併 {plan.MergedCount} 筆、跨日升級 {escalatedCount} 筆、長期 Down {chronicCount} 筆、跨來源佐證（補追加階段）{corroboratedCount} 筆）、涉及主機 {involvedHosts} 台、" +
                                      $"本階段追加 {appendedHosts} 台、PRTG 獨立主機日 {prtgOnlyHosts} 台（其餘 {pendingHosts} 台無變動：finding 已在紀錄中，或只剩被抑制的 finding）";
                        prtgConsole.WriteLine(summary);
                        runRecorder.Milestone(summary);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "PRTG finding 補追加失敗，不影響分析成果");
                        prtgConsole.WriteLine("  ✗ PRTG finding 補追加失敗：" + ex.Message);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    plan.DayEvaluated = false;
                    Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
                    if (!prtgFindings.IsPublished(day))
                    {
                        prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>(), new Dictionary<long, IReadOnlySet<string>>());
                    }
                }
            }

            // 全部日期發佈完才送就緒訊號（一次）：AI 排程等的是「本趟範圍內的 finding 都到齊」
            progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);
            operationScope.CompletedStage("逐日判定與補追加已返回");

            // 觸發式數值取數
            if (!strategyProfile.NightlyExactValues)
            {
                prtgConsole.WriteLine("取數策略為保守，夜間不逐顆查詢歷史值，數值由快照供應。");
                if (days.Count > 1)
                {
                    prtgConsole.WriteLine($"其餘 {days.Count - 1} 天的 PRTG 數值不在立即執行內取，請用排程作業頁的「開始回填」。");
                }
            }
            else
            {
                try
                {
                    progress?.Report(RunPhases.PrtgTriggered, 0, 0);
                    var triggeredFetcher = new PrtgTriggeredValueFetcher(
                        fetchService, backend.PrtgStore(), backend.RecordStore(), prtgConsole);

                    var (scopeHostIds, unresolvedHosts) = PrtgValueFetchScope.ResolveHostNames(
                        systemSettings.PrtgValueFetchExtraHosts,
                        hostStore.CapturePrtgSnapshot().Hosts.Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

                    if (unresolvedHosts.Count > 0)
                    {
                        prtgConsole.WriteLine($"  ⚠ 取數範圍的指定主機有 {unresolvedHosts.Count} 個對不到主機主檔，已略過：" +
                                              string.Join("、", unresolvedHosts.Take(10)));
                    }

                    var effectiveScopeText = PrtgValueFetchScope.EffectiveScope(
                        systemSettings.PrtgValueFetchScope,
                        systemSettings.PrtgSensorTypeWhitelist == null || systemSettings.PrtgSensorTypeWhitelist.Count == 0);

                    var scopeText = effectiveScopeText switch
                    {
                        PrtgValueFetchScope.AllMapped => "全部已對應主機",
                        PrtgValueFetchScope.TriggeredPlusList => "觸發主機＋指定清單",
                        _ => "觸發主機"
                    };

                    foreach (var day in days)
                    {
                        operationScope.Checkpoint();
                        var state = dayStates[day];
                        var dayResult = await triggeredFetcher.RunAsync(
                            day, systemSettings.PrtgSensorTypeWhitelist, systemSettings.PrtgFetchConcurrency,
                            () => analysisTask.IsCompleted, ct, extraTriggerHosts: state.TriggerHosts,
                            progress: (stage, done, total) => progress?.Report(stage, done, total),
                            scope: systemSettings.PrtgValueFetchScope,
                            extraScopeHosts: scopeHostIds);

                        state.Triggered = dayResult;
                        totalTriggerHosts += dayResult.TriggerHosts;
                        totalTargetSensors += dayResult.TargetSensors;
                        totalValuesWritten += dayResult.ValuesWritten;
                        totalFailedSensors += dayResult.FailedSensors;

                        var summary = $"PRTG 觸發式取數完成（{day:yyyy-MM-dd}，範圍：{scopeText}）：主機 {dayResult.TriggerHosts} 台、" +
                                      $"sensor {dayResult.TargetSensors} 個、數值 {dayResult.ValuesWritten} 筆" +
                                      (dayResult.FailedSensors > 0 ? $"、失敗 sensor {dayResult.FailedSensors} 個" : "");

                        prtgConsole.WriteLine(summary);
                        runRecorder.Milestone(summary);

                        if (effectiveScopeText == PrtgValueFetchScope.AllMapped && dayResult.TriggerHosts == 0)
                        {
                            runRecorder.Milestone(
                                $"PRTG 取數範圍為「全部已對應主機」，但 {day:yyyy-MM-dd} 沒有任何已對應的 PRTG 主機，本次未取得數值");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PRTG 觸發式取數失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 觸發式取數失敗：{ex.Message}");
                }
            }

            // outcome 判定沿用現有規則，改用累加後的數字
            if (syncFailed)
            {
                prtgOutcome = BatchRun.PrtgOutcomeFailed;
            }
            else if ((fetchResult != null && fetchResult.Failures > 0) || totalFailedSensors > 0 || failedTrendLabels.Count > 0)
            {
                prtgOutcome = BatchRun.PrtgOutcomePartial;
            }
            else
            {
                prtgOutcome = BatchRun.PrtgOutcomeSuccess;
            }
        }
        catch (OperationCanceledException) when (!parentToken.IsCancellationRequested && operation?.SettingsChanged == true)
        {
            prtgOutcome = BatchRun.PrtgOutcomePartial;
            interruptedDayReason = "未完成評估：PRTG 設定或範圍在執行中變更；請以目前範圍重新執行。";
            prtgConsole.WriteLine("PRTG 設定在執行中變更；已完成的資料保留，停止後續 PRTG 工作。NetIQ／本機分析繼續。");
            runRecorder.Milestone("PRTG 設定在執行中變更，後續 PRTG 工作已停止");
        }
        catch (OperationCanceledException)
        {
            if (prtgEnabled)
            {
                prtgOutcome = BatchRun.PrtgOutcomePartial;
                interruptedDayReason = "未完成評估：作業已取消；保留已提交資料，請明確重新執行。";
            }
            // 取消訊號必須穿透，讓上層統一處理中斷
            throw;
        }
        catch (Exception ex)
        {
            prtgOutcome = BatchRun.PrtgOutcomeFailed;
            Log.Error(ex, "PRTG 每日擷取初始化失敗，本機與 NetIQ 分析結果不受影響");
            prtgConsole.WriteLine($"\n  ✗ PRTG 每日擷取初始化失敗：{ex.Message}（本機與 NetIQ 分析結果不受影響）");
        }
        finally
        {
            // 對 days 中尚未發佈的每一天補發佈空結果；若曾經有任何補發，送一次 PrtgFindingsReady
            var anyBackfilled = false;
            foreach (var day in days)
            {
                if (!prtgFindings.IsPublished(day))
                {
                    prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>(), new Dictionary<long, IReadOnlySet<string>>());
                    anyBackfilled = true;
                }
            }

            // 只對本趟寫過的主機日對帳 PRTG 腿；分析紀錄是權威，workflow 只是可重建的側欄。
            // 該日 PRTG 評估完成（含「沒有對應、沒有規則」這種算不出東西的完成）＝成功；本趟中斷＝等待；初始化失敗＝降級。
            var completedRun = prtgOutcome is BatchRun.PrtgOutcomeSuccess or BatchRun.PrtgOutcomeNoOutput or BatchRun.PrtgOutcomePartial;
            foreach (var day in days)
            {
                var dayEvaluated = planned.TryGetValue(day.Date, out var dayPlan) && dayPlan.DayEvaluated;
                foreach (var hostId in workflow?.ParentHostIdsForDay(day) ?? Array.Empty<long>())
                {
                    try
                    {
                        var prior = workflow!.Get(hostId, day);
                        if (prior == null || prior.Parent != WorkflowLegState.Succeeded) continue;
                        var prtgState = !prtgEnabled ? WorkflowLegState.Disabled :
                            completedRun && dayEvaluated ? WorkflowLegState.Succeeded :
                            completedRun ? WorkflowLegState.Waiting : WorkflowLegState.Degraded;
                        workflow.SetPrtg(hostId, day, prtgState, evidenceReady: prtgState == WorkflowLegState.Succeeded,
                            failure: prtgState == WorkflowLegState.Waiting ? "prtg-day-not-evaluated" :
                                prtgState == WorkflowLegState.Degraded ? "prtg-evaluation-failed" : null);
                    }
                    catch (Exception workflowError)
                    {
                        Log.Warn(workflowError, "PRTG workflow reconciliation failed for {HostId}/{Day}; analysis data remains authoritative", hostId, day);
                    }
                }
            }
            workflow?.ReleaseTouchedDays(days.Select(day => day.Date));

            if (anyBackfilled)
            {
                progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);
            }

            // 保留已處理日期的實際結果；取消時另標明尚未進入评估的日期，避免讀者把缺項當零風險。
            // PRTG 未啟用仍維持 null，不宣稱這些日期已經執行過。
            List<PrtgDayStat>? dayStats = null;
            if (dayStates.Count > 0)
            {
                var stageFailed = fetchResult != null && fetchResult.Failures > 0;
                var historyAffectingFailure = fetchResult?.HistoryAffectingFailure ?? false;
                dayStats = days
                    .Where(dayStates.ContainsKey)
                    .Select(day =>
                    {
                        var s = dayStates[day];
                        var failedSensors = s.Triggered?.FailedSensors ?? 0;
                        var (outcome, note) = ClassifyDay(syncFailed, failedSensors > 0 || stageFailed || s.TrendFailedLabels.Count > 0,
                            rulesAvailableForStat, sensorMirrorEmpty, s.MapAvailable, conservativeStrategy);
                        if (s.TrendFailedLabels.Count > 0 && outcome == BatchRun.PrtgOutcomePartial)
                            note = $"{string.Join("、", s.TrendFailedLabels)}趨勢證據未完整；尚未完成風險判定。";
                        if (stageFailed && note is null && outcome == BatchRun.PrtgOutcomePartial)
                            note = StageFailureNote(fetchResult!.StageFailures);
                        if (s.MapBorrowedFrom is { } borrowedFrom)
                            note = (note is null ? "" : note + "；") + $"沿用 {borrowedFrom:yyyy-MM-dd} 的主機對應";
                        return new PrtgDayStat(day, outcome, s.Findings, s.AttributedHosts, s.MapAvailable,
                            s.Triggered?.TriggerHosts ?? 0, s.Triggered?.TargetSensors ?? 0, failedSensors, note,
                            // 只有影響過去日資料的失敗才重評：結構（裝置／感測器鏡像）沒取齊、狀態變更沒取齊（下一趟成功即收斂）。
                            // 單一裝置的感測器查詢固定失敗這類不影響已落地資料的失敗，重評治不好，標了只會讓「待補評估」永遠掛著
                            NeedsReevaluation: syncFailed || historyAffectingFailure || !prtgFindings.IsEvaluated(day));
                    })
                    .ToList();

                // 整趟：逐日全部無產出才算無產出，否則維持原判定
                if (prtgOutcome == BatchRun.PrtgOutcomeSuccess &&
                    dayStats.Count > 0 && dayStats.All(d => d.Outcome == BatchRun.PrtgOutcomeNoOutput))
                {
                    prtgOutcome = BatchRun.PrtgOutcomeNoOutput;
                }
                else if (prtgOutcome == BatchRun.PrtgOutcomeSuccess && dayStats.Any(d => d.Outcome == BatchRun.PrtgOutcomePartial))
                {
                    prtgOutcome = BatchRun.PrtgOutcomePartial;
                }
            }

            if (interruptedDayReason is not null)
            {
                dayStats ??= [];
                dayStats.AddRange(days.Where(day => !dayStates.ContainsKey(day))
                    .Select(day => new PrtgDayStat(day, BatchRun.PrtgOutcomePartial, 0, 0, false, 0, 0, 0, interruptedDayReason,
                        NeedsReevaluation: true)));
                var statsByDay = dayStats.ToDictionary(stat => stat.Date.Date);
                dayStats = days.Select(day => statsByDay[day.Date]).ToList();
            }

            if (prtgOutcome != null)
            {
                runRecorder.RecordPrtgOutcome(
                    prtgOutcome,
                    Math.Max(0, totalTargetSensors - totalFailedSensors),
                    totalFailedSensors,
                    totalTriggerHosts,
                    prtgEnabled ? (conservativeStrategy ? PrtgFetchStrategy.Conservative : PrtgFetchStrategy.Aggressive) : null);
            }

            if (dayStats != null)
            {
                runRecorder.RecordPrtgDays(dayStats);
            }
            else if (prtgOutcome == BatchRun.PrtgOutcomeFailed)
            {
                // 逐日迴圈之前就失敗（結構同步擲例外）：每一天都沒評估，整排記失敗；
                // 不記的話總表會把這幾天當成沒有逐日統計的舊紀錄去猜
                runRecorder.RecordPrtgDays(days
                    .Select(day => new PrtgDayStat(day, BatchRun.PrtgOutcomeFailed, 0, 0, false, 0, 0, 0,
                        NeedsReevaluation: true))
                    .ToList());
            }

            // 完工訊號帶結果數字（取數主機數／目標 sensor 數）
            progress?.Report(
                RunPhases.PrtgDone,
                totalTriggerHosts,
                totalTargetSensors);
        }
    }

    /// <summary>保留試點設定順序，並以一次 host snapshot 過濾啟用中的 NetIQ 未合併主機。</summary>
    internal static long[] SelectActivePilotHostIds(IEnumerable<long> policyHostIds,
        IEnumerable<long>? requestedHostIds, Func<List<WebHost>> readHosts)
    {
        var requested = requestedHostIds?.ToHashSet();
        var eligible = readHosts().Where(h => h.Source == "netiq" && h.Active && h.MergedInto == null)
            .Select(h => h.HostId).ToHashSet();
        return policyHostIds.Where(id => (requested is null || requested.Contains(id)) && eligible.Contains(id)).ToArray();
    }

    /// <summary>
    /// 整趟共用階段（結構、狀態變更、清除、分類）失敗時的逐日說明：這些階段不分日，失敗會讓每一天都是部分失敗，
    /// 說明要講出是哪個階段，使用者才知道該看哪裡（完整訊息在執行詳情的重要紀錄）。
    /// </summary>
    internal static string StageFailureNote(IReadOnlyList<string> stageFailures)
    {
        if (stageFailures.Count == 0) return "整趟擷取有階段失敗，詳見重要紀錄。";
        var first = stageFailures[0].Replace("✗", "").Trim();
        if (first.Length > 120) first = first[..119] + "…";
        return stageFailures.Count == 1
            ? $"整趟擷取階段失敗：{first}"
            : $"整趟擷取 {stageFailures.Count} 個階段失敗，第一個：{first}";
    }

    /// <summary>規則庫沒有啟用中的 PRTG 規則時的逐日原因。</summary>
    public const string NoteNoRules = "規則庫沒有啟用中的 PRTG 規則（請至規則維護頁套用內建規則更新）";

    /// <summary>感測器鏡像為空時的逐日原因。</summary>
    public const string NoteEmptySensorMirror = "PRTG 感測器鏡像是空的（請至 PRTG 維護頁執行同步結構與對應）";

    /// <summary>該日沒有任何主機對應可用時的逐日原因。</summary>
    public const string NoteNoHostMap = "沒有任何主機對應到 PRTG 裝置（請檢查主機清單 IP 與 PRTG 裝置位址）";

    /// <summary>保守取數策略成功時的逐日說明。</summary>
    public const string NoteSnapshotValues = "數值由快照供應";

    /// <summary>
    /// 單日 PRTG 結局與原因。失敗、部分成功照舊；原本會判成功的日子，
    /// 若沒有任何可評估的對象（無規則 → 鏡像空 → 無主機對應，取第一個成立的原因）改判無產出。
    /// finding 為 0 但規則確實評估過仍是成功。
    /// </summary>
    public static (string Outcome, string? Note) ClassifyDay(
        bool syncFailed, bool anyFailure, bool rulesAvailable, bool sensorMirrorEmpty, bool mapAvailable,
        bool conservativeStrategy)
    {
        if (syncFailed)
        {
            return (BatchRun.PrtgOutcomeFailed, null);
        }
        if (anyFailure)
        {
            return (BatchRun.PrtgOutcomePartial, null);
        }
        if (!rulesAvailable)
        {
            return (BatchRun.PrtgOutcomeNoOutput, NoteNoRules);
        }
        if (sensorMirrorEmpty)
        {
            return (BatchRun.PrtgOutcomeNoOutput, NoteEmptySensorMirror);
        }
        if (!mapAvailable)
        {
            return (BatchRun.PrtgOutcomeNoOutput, NoteNoHostMap);
        }
        return (BatchRun.PrtgOutcomeSuccess, conservativeStrategy ? NoteSnapshotValues : null);
    }

    /// <summary>某日的 device → 主機：只取 ok 對應、且主機在本趟評估範圍內。</summary>
    private static Dictionary<long, long> DeviceToHost(IEnumerable<PrtgHostMapRow> rows, IReadOnlySet<long> evaluationHostIds)
    {
        var deviceToHost = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            if (row.MapStatus == PrtgMapStatus.Ok && row.HostId is { } hostId && evaluationHostIds.Contains(hostId))
            {
                deviceToHost[row.DeviceObjid] = hostId;
            }
        }
        return deviceToHost;
    }

    private static void AddFinding(Dictionary<long, List<LogIssueSignature>> findingsByHost, long hostId, LogIssueSignature signature)
    {
        if (!findingsByHost.TryGetValue(hostId, out var hostFindings))
        {
            findingsByHost[hostId] = hostFindings = new List<LogIssueSignature>();
        }
        hostFindings.Add(signature);
    }

    /// <summary>同代碼、同分類有多條啟用規則時取 Id 最小的一條並警告一次（與狀態型規則挑選同一個原則）。</summary>
    private static KnownIssueRule? SelectSingleRule(IReadOnlyList<KnownIssueRule> rules, string code, string category, IRunConsole console)
    {
        var matches = rules.Where(rule =>
                string.Equals(rule.PrtgRuleCode, code, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.PrtgSensorCategory, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
        if (matches.Length > 1)
        {
            var warning = $"PRTG 規則 {code}（分類 {category}）有 {matches.Length} 筆啟用，採用 Id 最小的「{matches[0].Id}」。";
            Log.Warn(warning);
            console.WriteLine("  ⚠ " + warning);
        }
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// 第一段留給第二段的逐日結果。<see cref="FindingsByHost"/> 為 null＝該日發佈空結果
    /// （規則庫沒有 PRTG 規則、無主機對應可用、評估失敗）。
    /// </summary>
    private sealed class PrtgPlannedDay
    {
        /// <summary>該日的 device → 主機（數值規則用最新一天的這份挑候選）。</summary>
        public IReadOnlyDictionary<long, long> DeviceToHost { get; set; } = new Dictionary<long, long>();
        public Dictionary<long, List<LogIssueSignature>>? FindingsByHost;
        /// <summary>該日 PRTG 評估是否完成（含沒有規則、沒有對應這種完成但算不出東西的情況）。</summary>
        public bool DayEvaluated;
        public int FindingCount;
        public int AcknowledgedCount;
        public int MergedCount;
    }

    /// <summary>某一天在這趟裡累計出來的結果，最後寫成 <see cref="PrtgDayStat"/>。</summary>
    private sealed class PrtgDayState
    {
        public int Findings;
        public int AttributedHosts;
        public bool MapAvailable;
        /// <summary>該日之前沒有對應、沿用之後那份時的對應日；逐日說明會註明。</summary>
        public DateTime? MapBorrowedFrom;
        /// <summary>擲例外的數值趨勢評估類別（磁碟、記憶體使用…）；非空＝該日趨勢證據未完整。</summary>
        public List<string> TrendFailedLabels { get; } = new();
        /// <summary>規則命中的主機，觸發式取數會把它們併進候選（規則命中但風險未上調的主機也要取數）。</summary>
        public HashSet<long> TriggerHosts { get; } = new();
        public PrtgTriggeredFetchResult? Triggered;
    }

    /// <summary>
    /// 決定夜間取數是否要寫入結構同步狀態，以及產生對應的狀態物件。
    /// 任一條件不成立（跳過結構同步、擷取擲例外、有失敗階段、對應失敗）回傳 null；全部成功才回傳狀態。
    /// 失敗的夜間同步若覆寫一筆「成功的手動同步」，畫面會從「上次成功」變成「上次失敗」，
    /// 但鏡像其實是手動那次的完整資料；反過來鏡像不完整時不能宣稱成功。
    /// </summary>
    internal static PrtgStructureSyncStatus? BuildNightlySyncStatus(
        bool structureSyncSkipped,
        PrtgFetchResult? fetchResult,
        bool fetchThrew,
        PrtgHostMapResult? mapResult,
        DateTime day,
        TimeSpan elapsed,
        DateTime now)
    {
        if (structureSyncSkipped || fetchThrew || fetchResult == null || fetchResult.Failures > 0 || mapResult == null)
        {
            return null;
        }

        return new PrtgStructureSyncStatus
        {
            CompletedAt = now,
            Success = true,
            ErrorMessage = null,
            ElapsedSeconds = elapsed.TotalSeconds,
            Devices = fetchResult.Devices,
            Sensors = fetchResult.Sensors,
            MapDate = day,
            MapOk = mapResult.Ok,
            MapManual = mapResult.Manual,
            MapConflict = mapResult.Conflict,
            MapUnmatched = mapResult.Unmatched,
            MapSkippedNoIp = mapResult.SkippedNoIp,
            MapSkippedExcluded = mapResult.SkippedExcluded,
            MapSkippedManualSibling = mapResult.SkippedManualSibling,
            MapMappedHosts = mapResult.MappedHosts,
            MapUnmappedHosts = mapResult.UnmappedHosts,
            MapNameMismatch = mapResult.NameMismatch,
            Source = PrtgStructureSyncStatus.SourceNightly
        };
    }
}
