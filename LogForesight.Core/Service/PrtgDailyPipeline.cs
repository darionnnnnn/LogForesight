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

    /// <param name="structureSyncGate">
    /// 手動觸發的「同步結構與對應」的閘門（docs/PRTG-SPEC.md §5a）。Core 不認識 Web 的服務，
    /// 由呼叫端注入；未接上時傳 null＝行為與沒有這個機制時完全相同。
    /// 同步正在跑時這一趟先等它結束，然後跳過自己的結構同步——鏡像剛更新過，重做一次沒有意義。
    /// </param>
    /// <param name="hostIds">
    /// 監看裝置只算這些主機（指定主機更新）；null＝全站。必填——漏傳時靜默退回全站，就是對整台 PRTG 做全範圍擷取。
    /// 非 null 時範圍為 partial：感測器鏡像與範圍外資料都不清除。
    /// </param>
    public static async Task RunAsync(
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, IReadOnlyList<DateTime> days, Task analysisTask,
        IReadOnlyCollection<long>? hostIds,
        PrtgResourceGuard? guard = null,
        IPrtgStructureSyncGate? structureSyncGate = null)
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
            runRecorder, result, useAi, progress, prtgFindings, dispatch) = ctx;

        var prtgConsole = new PrefixedRunConsole(console, "[PRTG] ");
        string? prtgOutcome = null;
        var totalTriggerHosts = 0;
        var totalTargetSensors = 0;
        var totalFailedSensors = 0;
        var totalValuesWritten = 0;

        // 逐日累計（規則評估、歸戶、觸發式取數）在 finally 才寫進執行紀錄：
        // 中途被停止或擲例外時，已經評估完的日子也要留下逐日結果，執行總表才不會整排「—」。
        var dayStates = new Dictionary<DateTime, PrtgDayState>();
        PrtgFetchResult? fetchResult = null;
        var syncFailed = false;
        // 逐日「無產出」判定要用的事實（在 try 內決定，finally 寫逐日結果時用）
        var rulesAvailableForStat = true;
        var sensorMirrorEmpty = false;
        var conservativeStrategy = false;

        var parentToken = ct;
        PrtgOperationScope? operation = null;
        try
        {
            var systemSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (!systemSettings.PrtgEnabled ||
                string.IsNullOrWhiteSpace(systemSettings.PrtgUrl) ||
                !PrtgClientFactory.HasUsableCredentials(systemSettings))
            {
                prtgConsole.WriteLine("PRTG 未啟用或尚未設定認證資訊，略過。");
                prtgOutcome = BatchRun.PrtgOutcomeDisabled;
                return;
            }

            using var operationScope = new PrtgOperationScope(systemSettings,
                () => new SystemSettingsStore(backend.Blob("system_settings")).Get(), parentToken, new PrtgScopeRevisionReader(backend, hostStore).Read, "每日評估");
            operation = operationScope;
            ct = operationScope.Token;
            operationScope.Checkpoint();
            using var client = PrtgClientFactory.Create(systemSettings);
            client.OperationCheckpoint = operationScope.Checkpoint;
            var monitoringPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var pilotReady = monitoringPolicy.Ready(systemSettings.PrtgUrl);
            var requestedHostIds = hostIds;
            if (pilotReady)
                hostIds = monitoringPolicy.HostIds.Where(id => (requestedHostIds == null || requestedHostIds.Contains(id)) &&
                    hostStore.GetAll().Any(h => h.HostId == id && h.Source == "netiq" && h.Active && h.MergedInto == null)).ToArray();
            else
                prtgConsole.WriteLine("尚未確認 Core 身分與試點清單；本趟只同步診斷資料，不發布正式 PRTG 判定。");

            var fetchService = new PrtgFetchService(client, backend.PrtgStore(),
                new PrtgFreshnessStore(backend.Blob(PrtgFreshnessStore.BlobKey)), prtgConsole,
                PrtgSensorTypeCategoryMap.ParseOverrides(systemSettings.PrtgSensorTypeCategoryOverrides).Map, guard);

            var strategyProfile = PrtgFetchStrategy.Profile(systemSettings.PrtgFetchStrategy);
            var strategyLabel = strategyProfile.NightlyExactValues ? "激進" : "保守";
            conservativeStrategy = !strategyProfile.NightlyExactValues;
            prtgConsole.WriteLine($"PRTG 取數策略：{strategyLabel}（快照間隔 {strategyProfile.SnapshotIntervalMinutes} 分鐘）。");

            // 0. 手動同步佔用中就先等它（docs/PRTG-SPEC.md §5a）。等完之後鏡像是最新的，
            //    本趟跳過自己的結構同步——重做一次要再爬一次整棵樹，沒有任何新資訊。
            var skipStructureSync = false;
            if (structureSyncGate != null && structureSyncGate.IsRunning)
            {
                prtgConsole.WriteLine("手動觸發的「同步結構與對應」進行中，等它完成後再繼續（本趟不重複同步結構）。");
                runRecorder.Milestone("PRTG：等待手動同步結構與對應完成");
                progress?.Report(RunPhases.PrtgWaitSync, 0, 0);
                skipStructureSync = await structureSyncGate.WaitUntilIdleAsync(ct);
                if (skipStructureSync)
                {
                    prtgConsole.WriteLine("手動同步已完成，沿用剛更新的鏡像結構。");
                }
                else
                {
                    // 兩種情況共用這條路：等到上限對方還沒結束（PRTG 端卡住），
                    // 或它結束了但沒成功（被中止、有階段失敗）。兩者的鏡像都不能當成新的，
                    // 本趟照常自己同步。寫成「已完成、沿用」會讓這一晚鏡像其實不完整卻處處顯示正常。
                    prtgConsole.WriteLine("  ⚠ 手動同步未成功結束（卡住、被中止或有階段失敗）；本趟改為自行同步結構。");
                    runRecorder.Milestone("PRTG：手動同步未成功結束，改為自行同步結構");
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
                            runRecorder.Milestone($"PRTG 主機對應完成（{newest:yyyy-MM-dd}）：ok={mapResult.Ok}, manual={mapResult.Manual}, conflict={mapResult.Conflict}, unmatched={mapResult.Unmatched}, skipped_no_ip={mapResult.SkippedNoIp}, skipped_excluded={mapResult.SkippedExcluded}, skipped_manual_sibling={mapResult.SkippedManualSibling}");
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

                var summary = $"PRTG 每日擷取完成（{newest:yyyy-MM-dd}）：裝置 {fetchResult.Devices}、感測器 {fetchResult.Sensors}、" +
                              $"狀態變更新增 {fetchResult.StateChanges}、數值 {fetchResult.Values}" +
                              (fetchResult.Failures > 0 ? $"、失敗階段 {fetchResult.Failures}" : "");

                prtgConsole.WriteLine(summary);
                runRecorder.Milestone(summary);
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
                PrtgScopePurge.RunAfterStructureSync(backend.PrtgStore(), fetchResult.Scope, fetchResult.DevicesRefreshed, prtgConsole);
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

            // 最新一天用剛重算的當日對應；過去日取「該日或之前最近一日」的既有對應，不硬造（docs/PRTG-SPEC.md §5）。
            // 視窗與觸發式取數同一個，規則歸戶與取數看到的主機才會一致。
            List<PrtgHostMapRow> ResolveHostMapRows(DateTime d) => d == newest
                ? prtgStore.GetHostMapForDate(newest)
                : prtgStore.GetLatestHostMapWithDate(PrtgTriggeredValueFetcher.HostMapLookbackDays, anchor: d).Rows;

            // 包裝層：合成問題靜音項目（以紀錄日 day 判定，見下方逐主機標記）
            var allSuppressions = new MuteAwareSuppressionStore(
                new SuppressionStore(backend.Blob("suppressions")), new IssueOwnerStore(backend.Blob("issue_owners"))).LoadAll();
            var allMutes = SuppressionFilter.MutesOf(allSuppressions);

            // 兩段式：處理最新一天的當下，較舊日期的 finding 還沒評估也還沒寫進資料庫，
            // 跨日判定只查資料庫會少算。所以先對全部日期評估並歸戶（不發佈），再逐日標註、抑制、發佈、追加。
            var planned = new Dictionary<DateTime, PrtgPlannedDay>();
            // partial 同步會保留其他主機的鏡像；歸戶也必須套本趟主機範圍，
            // 不能因舊鏡像仍在就替未選取、已停用或已合併主機保存新判定。
            var evaluationHostIds = hostStore.GetAll()
                .Where(h => PrtgFormalEligibility.HostAllowed(h, systemSettings, monitoringPolicy) && (hostIds == null || hostIds.Contains(h.HostId)))
                .Select(h => h.HostId).ToHashSet();
            var timelineEvidence = new Dictionary<long, PrtgSensorTimelineEvidence>();
            var currentMaps = ResolveHostMapRows(newest).Where(m => m.MapStatus == PrtgMapStatus.Ok &&
                m.HostId.HasValue && evaluationHostIds.Contains(m.HostId.Value)).ToDictionary(m => m.DeviceObjid, m => m.HostId!.Value);
            if (pilotReady)
            {
                var collector = new PrtgSensorTimelineCollector(backend, client);
                var selectedSensors = sensorStatuses.Where(s => monitoringPolicy.SensorIds.Contains(s.Objid) && currentMaps.ContainsKey(s.DeviceObjid)).ToArray();
                for (var index = 0; index < selectedSensors.Length; index++)
                {
                    operationScope.Checkpoint();
                    var sensor = selectedSensors[index];
                    timelineEvidence[sensor.Objid] = await collector.CollectAsync(sensor.Objid, currentMaps[sensor.DeviceObjid], monitoringPolicy, ct);
                    prtgConsole.WriteLine($"狀態涵蓋 {index + 1}/{selectedSensors.Length}：sensor {sensor.Objid}，{timelineEvidence[sensor.Objid].QualityReason}");
                }
            }

            // 第一段（由近到遠）：主機對應、評估、映射成簽章、歸戶。不發佈、不追加。
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
                    var deviceToHost = new Dictionary<long, long>();
                    foreach (var row in ResolveHostMapRows(day))
                    {
                        if (row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue && evaluationHostIds.Contains(row.HostId.Value))
                        {
                            deviceToHost[row.DeviceObjid] = row.HostId.Value;
                        }
                    }
                    state.MapAvailable = deviceToHost.Count > 0;

                    // 規則庫沒有 PRTG 規則：該日照樣發佈空結果（「算不出東西」與「還沒算完」要分得出來）
                    var applicableEvidence = timelineEvidence.Where(p => p.Value.SourceGeneration == monitoringPolicy.SourceGeneration &&
                        sensorToDevice.TryGetValue(p.Key, out var device) &&
                        deviceToHost.TryGetValue(device, out var mappedHost) && mappedHost == p.Value.HostId)
                        .ToDictionary(p => p.Key, p => p.Value);
                    foreach (var proof in applicableEvidence.Values)
                    {
                        var begin = new DateTimeOffset(day); var finish = begin.AddDays(1);
                        var periods = proof.Periods(begin, finish);
                        if (periods.Sum(p => (p.Through - p.From).TotalSeconds) >= (finish - begin).TotalSeconds &&
                            periods.All(p => !string.Equals(p.Status, "Unknown", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!plan.ReevaluatedResources.TryGetValue(proof.HostId, out var resources))
                                plan.ReevaluatedResources[proof.HostId] = resources = new();
                            resources[proof.SensorId] = proof.ResourceGeneration;
                        }
                    }
                    var findings = PrtgCoveredRuleEvaluator.Evaluate(day, sensorStatuses, applicableEvidence, prtgRules);
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
                        // 用今天的對應套到過去日會把裝置掛到錯的主機上，而且看起來與真的一樣（docs/PRTG-SPEC.md §5「回填不做主機對應」同一條線）。
                        prtgConsole.WriteLine($"{day:yyyy-MM-dd} 無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶");
                        continue;
                    }

                    var findingsByHost = new Dictionary<long, List<LogIssueSignature>>();
                    foreach (var finding in findings)
                    {
                        if (deviceToHost.TryGetValue(finding.DeviceObjid, out var hostId))
                        {
                            if (!findingsByHost.TryGetValue(hostId, out var hostFindings))
                            {
                                hostFindings = new List<LogIssueSignature>();
                                findingsByHost[hostId] = hostFindings;
                            }
                            var signature = PrtgFindingMapper.ToSignature(finding, day);
                            hostFindings.Add(signature);
                            plan.Observations.Add((hostId, finding, signature));
                            state.TriggerHosts.Add(hostId);
                        }
                    }
                    state.AttributedHosts = findingsByHost.Count;

                    plan.FindingsByHost = findingsByHost;
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
                    Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
                }
            }

            // 正式磁碟趨勢只讀取已落盤的日資料，且僅評估最新的已完成日。
            // 它不等待本趟 triggered fetch，也不接觸 PRTG API；分頁上限由 assessment service 強制為 100。
            if (pilotReady && newest.Date < DateTime.Today)
            {
                operationScope.Checkpoint();
                var diskRules = KnownIssueCatalog.Rules.Where(r =>
                    string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.PrtgRuleCode, PrtgDiskRuleDecision.RuleCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.PrtgSensorCategory, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase) && r.Enabled)
                    .OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
                if (diskRules.Length > 1)
                {
                    var warning = $"PRTG 磁碟趨勢規則有 {diskRules.Length} 筆相同代碼／disk 分類，正式評估採用 Id 最小的「{diskRules[0].Id}」。";
                    Log.Warn(warning);
                    prtgConsole.WriteLine("  ⚠ " + warning);
                }

                var diskRule = diskRules.FirstOrDefault();
                if (diskRule != null)
                {
                    try
                    {
                        var assessment = new PrtgDiskAssessmentService(prtgStore, hostStore,
                            new SystemSettingsStore(backend.Blob("system_settings")),
                            new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
                            new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
                        // 逐 sensor 重新核對通道，當前語意只能從第一次確認開始累積，不能追認先前 28 日。
                        var semanticStore = new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
                        foreach (var sensor in sensorStatuses.Where(s => monitoringPolicy.SensorIds.Contains(s.Objid) &&
                            s.Category == PrtgSensorCategories.Disk && timelineEvidence.ContainsKey(s.Objid)))
                        {
                            operationScope.Checkpoint();
                            var proof = timelineEvidence[sensor.Objid];
                            // 落地數值目前以 Web 主機時區解析；異時區不可用來宣稱可信的 28 日趨勢。
                            if (!TimeZoneInfo.FindSystemTimeZoneById(monitoringPolicy.SourceTimeZoneId).HasSameRules(TimeZoneInfo.Local))
                            {
                                timelineEvidence[sensor.Objid] = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid))
                                    .Update(e => { e.DiskSemanticCheckedAt = null; e.QualityReason = "disk-timezone-not-supported"; });
                                prtgConsole.WriteLine($"sensor {sensor.Objid} 來源與伺服器時區不同；磁碟趨勢暫不發布，狀態涵蓋另行判讀。");
                                continue;
                            }
                            using var semanticBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            semanticBudget.CancelAfter(TimeSpan.FromSeconds(20));
                            try
                            {
                                var localStart = DateTime.SpecifyKind(newest.Date, DateTimeKind.Local);
                                var points = prtgStore.GetValuesForSensor(sensor.Objid, localStart, localStart.AddDays(1))
                                    .Where(v => v.AvgValue.HasValue && (v.Quality == PrtgDataQuality.Ok ||
                                        v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                                    .Take(5).Select(v => new PrtgDiskSemanticPersistedPoint(
                                        DateTime.SpecifyKind(v.PeriodStart, DateTimeKind.Local).ToUniversalTime(), v.AvgValue!.Value)).ToArray();
                                var typed = await new PrtgDiskSemanticProbe(client).ProbeAsync(sensor.Objid,
                                    localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime(), points, semanticBudget.Token);
                                var now = DateTimeOffset.Now;
                                var fingerprint = System.Text.Json.JsonSerializer.Serialize(new
                                { typed.ChannelIdentifier, typed.ChannelName, typed.Unit, typed.Scale, typed.Direction });
                                var stored = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid)).Update(e =>
                                {
                                    e.DiskSemanticCheckedAt = now;
                                    if (typed.Status != PrtgDiskSemanticProbeStatus.Verified)
                                    { e.DiskSemanticValidFrom = null; e.DiskSemanticFingerprint = ""; return; }
                                    if (e.DiskSemanticFingerprint != fingerprint || e.DiskSemanticValidFrom == null)
                                    { e.DiskSemanticFingerprint = fingerprint; e.DiskSemanticValidFrom = now; }
                                });
                                timelineEvidence[sensor.Objid] = stored;
                                new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(new(
                                    sensor.Objid, sensor.DeviceObjid, proof.HostId, sensor.SensorType, typed.Status.ToString(), typed.Summary,
                                    typed.ChannelIdentifier, typed.ChannelName, typed.Unit, typed.Scale, typed.Direction,
                                    typed.ComparedPointCount, typed.ValuesMatch, now.UtcDateTime, newest, PrtgDiskAssessmentService.ParserSemanticVersion));
                                if (typed.Status == PrtgDiskSemanticProbeStatus.Verified)
                                    semanticStore.RecordAutomatedVerification(new(sensor.Objid, sensor.DeviceObjid, proof.HostId, sensor.SensorType,
                                        typed.ChannelIdentifier!, typed.ChannelName!, typed.Unit!, typed.Scale!.Value, typed.Direction!), true,
                                        "正式評估前重新核對主通道與落地樣本；暖機期間不追認先前數值。", now.UtcDateTime, PrtgDiskAssessmentService.ParserSemanticVersion);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            { timelineEvidence[sensor.Objid] = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid)).Update(e => e.DiskSemanticCheckedAt = null); prtgConsole.WriteLine($"sensor {sensor.Objid} 語意核對逾時；不發布磁碟趨勢。"); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            { timelineEvidence[sensor.Objid] = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid)).Update(e => e.DiskSemanticCheckedAt = null); Log.Warn(ex, "磁碟語意重新核對失敗 sensor={Sensor}", sensor.Objid); }
                        }
                        var pageOffset = 0;
                        var added = 0;
                        var batchSize = PrtgDiskAssessmentService.EffectiveBatchSize(diskRule);
                        while (true)
                        {
                            operationScope.Checkpoint();
                            ct.ThrowIfCancellationRequested();
                            var page = assessment.Assess(DateOnly.FromDateTime(newest), diskRule,
                                PrtgDiskDecisionMode.Formal, batchSize,
                                pageOffset, hostIds, monitoringPolicy.SensorIds);
                            foreach (var row in page.Rows)
                            {
                                if (row.Decision.Exclusion == PrtgDiskDecisionExclusion.TrendNoHit && timelineEvidence.TryGetValue(row.SensorObjid, out var recovered) &&
                                    recovered.DiskSemanticCheckedAt >= DateTimeOffset.Now.AddMinutes(-10) && recovered.DiskSemanticValidFrom != null &&
                                    recovered.DiskSemanticValidFrom <= new DateTimeOffset(newest.AddDays(-27)) &&
                                    monitoringPolicy.ValidFrom <= new DateTimeOffset(newest.AddDays(-27)) && recovered.QualityReason == "covered" &&
                                    recovered.SourceGeneration == monitoringPolicy.SourceGeneration &&
                                    recovered.MappingRevision == backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion())
                                {
                                    new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + row.SensorObjid))
                                        .Update(e => e.DiskIncidentStartedAt = null);
                                    recovered.DiskIncidentStartedAt = null;
                                    if (!planned[newest].DiskReevaluatedResources.TryGetValue(row.CurrentHostId, out var resources))
                                        planned[newest].DiskReevaluatedResources[row.CurrentHostId] = resources = new();
                                    resources[row.SensorObjid] = recovered.ResourceGeneration;
                                }
                                if (row.Decision.Finding is not { } finding) continue;
                                if (!timelineEvidence.TryGetValue(finding.SensorObjid!.Value, out var diskProof) ||
                                    diskProof.QualityReason != "covered" || diskProof.MappingRevision != backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() ||
                                    diskProof.SourceGeneration != monitoringPolicy.SourceGeneration ||
                                    diskProof.DiskSemanticValidFrom == null || diskProof.DiskSemanticCheckedAt == null ||
                                    diskProof.DiskSemanticCheckedAt.Value < DateTimeOffset.Now.AddMinutes(-10) ||
                                    diskProof.DiskSemanticValidFrom > new DateTimeOffset(newest.AddDays(-27)) ||
                                    monitoringPolicy.ValidFrom > new DateTimeOffset(newest.AddDays(-27))) continue;
                                var incident = diskProof.DiskIncidentStartedAt ?? new DateTimeOffset(newest.Date);
                                new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + finding.SensorObjid))
                                    .Update(e => e.DiskIncidentStartedAt ??= incident);
                                finding = finding with { SourceGeneration = diskProof.SourceGeneration,
                                    ResourceGeneration = diskProof.ResourceGeneration,
                                    IncidentStartedAt = incident };
                                var findingsByHost = planned[newest].FindingsByHost ??= new Dictionary<long, List<LogIssueSignature>>();
                                if (!findingsByHost.TryGetValue(row.CurrentHostId, out var hostFindings))
                                    findingsByHost[row.CurrentHostId] = hostFindings = new List<LogIssueSignature>();
                                var signature = PrtgFindingMapper.ToSignature(finding, newest);
                                hostFindings.Add(signature);
                                planned[newest].Observations.Add((row.CurrentHostId, finding, signature));
                                added++;
                                dayStates[newest].TriggerHosts.Add(row.CurrentHostId);
                            }
                            if (!page.HasMore) break;
                            pageOffset += page.AssessedCount;
                        }
                        if (added > 0)
                        {
                            var latestPlan = planned[newest];
                            latestPlan.FindingCount += added;
                            latestPlan.FindingsByHost ??= new Dictionary<long, List<LogIssueSignature>>();
                            dayStates[newest].Findings += added;
                            dayStates[newest].AttributedHosts = latestPlan.FindingsByHost.Count;
                            prtgConsole.WriteLine($"磁碟趨勢正式評估完成（{newest:yyyy-MM-dd}）：新增 finding {added} 筆。");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "PRTG 磁碟趨勢評估失敗；既有狀態型 findings 繼續發布");
                        prtgConsole.WriteLine("  ⚠ 磁碟趨勢評估失敗，既有狀態型 findings 照常發布：" + ex.Message);
                    }
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
                    foreach (var (hostId, resources) in plan.ReevaluatedResources)
                    {
                        operationScope.Checkpoint();
                        var key = hostStore.GetAll().First(h => h.HostId == hostId);
                        backend.RecordStore(new HostKey { HostId = hostId, HostName = key.HostName })
                            .ReconcilePrtgStateFindings(hostId, day, resources, monitoringPolicy.SourceGeneration,
                                (plan.FindingsByHost?.GetValueOrDefault(hostId) ?? []).Select(f => f.EventKey).ToHashSet());
                    }
                    foreach (var (hostId, resources) in plan.DiskReevaluatedResources)
                    {
                        operationScope.Checkpoint();
                        var key = hostStore.GetAll().First(h => h.HostId == hostId);
                        backend.RecordStore(new HostKey { HostId = hostId, HostName = key.HostName })
                            .ReconcilePrtgStateFindings(hostId, day, resources, monitoringPolicy.SourceGeneration,
                                (plan.FindingsByHost?.GetValueOrDefault(hostId) ?? []).Select(f => f.EventKey).ToHashSet(),
                                new HashSet<string> { PrtgDiskRuleDecision.RuleCode });
                    }
                    if (plan.FindingsByHost == null)
                    {
                        prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>(), new Dictionary<long, IReadOnlySet<string>>());
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

                    var hostsById = hostStore.GetAll().ToDictionary(h => h.HostId);
                    var suppressedCount = 0;
                    var suppressedPatternIdsByHost = new Dictionary<long, IReadOnlySet<string>>();
                    foreach (var (hostId, hostFindings) in findingsByHost)
                    {
                        var (hostName, hostGroupIds) = hostsById.TryGetValue(hostId, out var webHost)
                            ? (webHost.HostName, (IReadOnlyCollection<long>)webHost.GroupIds)
                            : (string.Empty, (IReadOnlyCollection<long>)Array.Empty<long>());
                        var activeSuppressions = SuppressionFilter.ActiveForHost(allSuppressions, hostName, hostGroupIds, DateTime.Now);
                        suppressedCount += SuppressionFilter.MarkSuppressed(hostFindings, activeSuppressions, allMutes, day);
                        // 跨來源佐證的關聯抑制與 finding 抑制用同一份有效抑制，兩條追加路徑從登錄簿取用
                        suppressedPatternIdsByHost[hostId] = SuppressionFilter.ToCorrelationPatternIdSet(activeSuppressions);
                    }

                    // 先保存不依賴日誌紀錄的判定快照。影子資料尚未通過共同讀取切換，
                    // 不據此建案／通知，也不把缺少來源身分與涵蓋證據的結果當作可信延續。
                    var captured = 0;
                    foreach (var group in plan.Observations.GroupBy(o => o.HostId))
                    {
                        operationScope.Checkpoint();
                        captured += backend.PrtgObservationStore().Capture(group.Key, day, systemSettings.Revision,
                            group.Select(o => (o.Finding, o.Signature)).ToArray(), systemSettings.PrtgUrl, runRecorder.RunId);
                    }
                    prtgConsole.WriteLine($"獨立 PRTG 判定快照：新增 {captured} 筆（可信版本等待合格 NetIQ 日紀錄補追加；未知品質只作診斷）。");

                    prtgFindings.Publish(day, findingsByHost.ToDictionary(
                        kv => kv.Key, kv => (IReadOnlyList<LogIssueSignature>)kv.Value), suppressedPatternIdsByHost);

                    try
                    {
                        var involvedHosts = findingsByHost.Count;
                        var appendedHosts = 0;
                        var pendingHosts = 0;
                        var corroboratedCount = 0;

                        foreach (var (hostId, hostFindings) in findingsByHost)
                        {
                            operationScope.Checkpoint();
                            var hostName = hostsById.TryGetValue(hostId, out var webHost) ? webHost.HostName : string.Empty;
                            var hostRecordStore = backend.RecordStore(new HostKey { HostId = hostId, HostName = hostName });

                            var hostCorroborated = 0;
                            if (prtgFindings.AttachExclusive(hostId, day, () => hostRecordStore.AttachPrtgFindings(
                                    hostId, day, hostFindings, prtgFindings.SuppressedPatternIdsFor(hostId, day), out hostCorroborated, useAi)))
                            {
                                appendedHosts++;
                                corroboratedCount += hostCorroborated;
                                HostDayPostProcessor.AttachCase(caseCoordinator, dispatch, hostName, day, hostFindings.ToList(), "[PRTG] ");
                            }
                            else pendingHosts++;
                        }

                        var summary = $"PRTG 規則評估完成（{day:yyyy-MM-dd}）：finding {plan.FindingCount} 筆（其中已抑制 {suppressedCount} 筆、已於 PRTG 確認 {plan.AcknowledgedCount} 筆、已合併 {plan.MergedCount} 筆、跨日升級 {escalatedCount} 筆、長期 Down {chronicCount} 筆、跨來源佐證（補追加階段）{corroboratedCount} 筆）、涉及主機 {involvedHosts} 台、" +
                                      $"本階段追加 {appendedHosts} 台、未追加 {pendingHosts} 台（可能由分析路徑處理；若當日沒有日誌分析紀錄，目前不會建立獨立 PRTG 問題）";
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
                        hostStore.GetAll().Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

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
            else if ((fetchResult != null && fetchResult.Failures > 0) || totalFailedSensors > 0)
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
            prtgConsole.WriteLine("PRTG 設定在執行中變更；已完成的資料保留，停止後續 PRTG 工作。NetIQ／本機分析繼續。");
            runRecorder.Milestone("PRTG 設定在執行中變更，後續 PRTG 工作已停止");
        }
        catch (OperationCanceledException)
        {
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

            if (anyBackfilled)
            {
                progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);
            }

            // 逐日結果：只記有進到逐日迴圈的日子（PRTG 未啟用時一天都沒有，PrtgDays 維持 null）。
            // 中途被停止的日子就記到哪算到哪，執行總表才看得出「哪幾天其實已經評估完了」。
            List<PrtgDayStat>? dayStats = null;
            if (dayStates.Count > 0)
            {
                var stageFailed = fetchResult != null && fetchResult.Failures > 0;
                dayStats = days
                    .Where(dayStates.ContainsKey)
                    .Select(day =>
                    {
                        var s = dayStates[day];
                        var failedSensors = s.Triggered?.FailedSensors ?? 0;
                        var (outcome, note) = ClassifyDay(syncFailed, failedSensors > 0 || stageFailed,
                            rulesAvailableForStat, sensorMirrorEmpty, s.MapAvailable, conservativeStrategy);
                        return new PrtgDayStat(day, outcome, s.Findings, s.AttributedHosts, s.MapAvailable,
                            s.Triggered?.TriggerHosts ?? 0, s.Triggered?.TargetSensors ?? 0, failedSensors, note);
                    })
                    .ToList();

                // 整趟：逐日全部無產出才算無產出，否則維持原判定
                if (prtgOutcome == BatchRun.PrtgOutcomeSuccess &&
                    dayStats.Count > 0 && dayStats.All(d => d.Outcome == BatchRun.PrtgOutcomeNoOutput))
                {
                    prtgOutcome = BatchRun.PrtgOutcomeNoOutput;
                }
            }

            if (prtgOutcome != null)
            {
                runRecorder.RecordPrtgOutcome(
                    prtgOutcome,
                    Math.Max(0, totalTargetSensors - totalFailedSensors),
                    totalFailedSensors,
                    totalTriggerHosts);
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
                    .Select(day => new PrtgDayStat(day, BatchRun.PrtgOutcomeFailed, 0, 0, false, 0, 0, 0))
                    .ToList());
            }

            // 完工訊號帶結果數字（取數主機數／目標 sensor 數）
            progress?.Report(
                RunPhases.PrtgDone,
                totalTriggerHosts,
                totalTargetSensors);
        }
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

    /// <summary>
    /// 第一段留給第二段的逐日結果。<see cref="FindingsByHost"/> 為 null＝該日發佈空結果
    /// （規則庫沒有 PRTG 規則、無主機對應可用、評估失敗）。
    /// </summary>
    private sealed class PrtgPlannedDay
    {
        public Dictionary<long, Dictionary<long, string>> ReevaluatedResources { get; } = new();
        public Dictionary<long, Dictionary<long, string>> DiskReevaluatedResources { get; } = new();
        public List<(long HostId, PrtgFinding Finding, LogIssueSignature Signature)> Observations { get; } = new();
        public Dictionary<long, List<LogIssueSignature>>? FindingsByHost;
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
            Source = PrtgStructureSyncStatus.SourceNightly
        };
    }
}
