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
    public static async Task RunAsync(
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, IReadOnlyList<DateTime> days, Task analysisTask,
        PrtgResourceGuard? guard = null,
        IPrtgStructureSyncGate? structureSyncGate = null)
    {
        if (days == null || days.Count == 0)
        {
            throw new ArgumentException("Days list must contain at least one day.", nameof(days));
        }

        var newest = days[0].Date;
        var oldest = days[^1].Date;

        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress, prtgFindings) = ctx;

        var prtgConsole = new PrefixedRunConsole(console, "[PRTG] ");
        string? prtgOutcome = null;
        var totalTriggerHosts = 0;
        var totalTargetSensors = 0;
        var totalFailedSensors = 0;
        var totalValuesWritten = 0;

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

            using var client = PrtgClientFactory.Create(systemSettings);

            var fetchService = new PrtgFetchService(client, backend.PrtgStore(), prtgConsole, guard);

            var strategyProfile = PrtgFetchStrategy.Profile(systemSettings.PrtgFetchStrategy);
            var strategyLabel = strategyProfile.NightlyExactValues ? "激進" : "保守";
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
            var syncStopwatch = Stopwatch.StartNew();
            PrtgFetchResult? fetchResult = null;
            var syncFailed = false;
            try
            {
                progress?.Report(RunPhases.PrtgSync, 0, 0);
                fetchResult = await fetchService.FetchDayAsync(
                    newest, systemSettings.PrtgFetchConcurrency, ct,
                    syncStructure: !skipStructureSync, fetchValues: false,
                    progress: (stage, done, total) => progress?.Report(stage, done, total),
                    stateChangesFrom: oldest.AddDays(-1));

                var summary = $"PRTG 每日擷取完成（{newest:yyyy-MM-dd}）：裝置 {fetchResult.Devices}、感測器 {fetchResult.Sensors}、" +
                              $"狀態變更 {fetchResult.StateChanges}、數值 {fetchResult.Values}" +
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

            // 2. PRTG 主機對應重算只對 newest
            PrtgHostMapResult? mapResult = null;
            try
            {
                var hostMapper = new PrtgHostMapper(backend.PrtgStore(), hostStore, prtgConsole, new PrtgAddressResolver());
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

            syncStopwatch.Stop();

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

            // 4. 送出契約 7 的日期範圍訊號（在逐日迴圈開始前，一次）
            progress?.Report(RunPhases.PrtgDateRange, days.Count, 0);

            // 規則庫判定（迴圈前判一次、印一次既有訊息，所有日期發佈空結果）
            var prtgRules = KnownIssueCatalog.Rules
                .Where(r => string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && r.Enabled)
                .ToList();

            var enabledRuleCodes = prtgRules
                .Where(r => !string.IsNullOrEmpty(r.PrtgRuleCode))
                .Select(r => r.PrtgRuleCode!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rulesAvailable = enabledRuleCodes.Count > 0;
            if (!rulesAvailable)
            {
                prtgConsole.WriteLine("規則庫尚無啟用中的 PRTG 規則（升級後請至「規則維護」頁套用內建規則更新），本次略過規則評估。");
            }

            var downMinutes = PrtgRuleCatalog.DefaultDownMinutes;
            var flapCount = PrtgRuleCatalog.DefaultFlapCount;
            var warningMinutes = PrtgRuleCatalog.DefaultWarningMinutes;

            foreach (var rule in prtgRules)
            {
                if (string.Equals(rule.PrtgRuleCode, PrtgRuleEvaluator.RuleDown, StringComparison.OrdinalIgnoreCase))
                {
                    downMinutes = rule.PrtgThreshold;
                }
                else if (string.Equals(rule.PrtgRuleCode, PrtgRuleEvaluator.RuleFlapping, StringComparison.OrdinalIgnoreCase))
                {
                    flapCount = rule.PrtgThreshold;
                }
                else if (string.Equals(rule.PrtgRuleCode, PrtgRuleEvaluator.RuleWarning, StringComparison.OrdinalIgnoreCase))
                {
                    warningMinutes = rule.PrtgThreshold;
                }
            }

            var thresholds = new PrtgRuleThresholds(downMinutes, flapCount, warningMinutes);

            var prtgStore = backend.PrtgStore();
            var whitelist = new HashSet<string>(systemSettings.PrtgSensorTypeWhitelist ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var allSensors = prtgStore.GetSensorStatuses();
            var filteredSensors = whitelist.Count == 0
                ? allSensors
                : allSensors.Where(s => whitelist.Contains(s.SensorType)).ToList();

            var allowedSensorObjids = filteredSensors.Select(s => s.Objid).ToHashSet();
            var sensorToDevice = filteredSensors
                .GroupBy(s => s.Objid)
                .ToDictionary(g => g.Key, g => g.First().DeviceObjid);

            var sensorStatuses = filteredSensors
                .Select(s => (s.Objid, s.DeviceObjid, s.Status))
                .ToList();

            var dayFindingsCounts = new Dictionary<DateTime, int>();
            var dayAttributedCounts = new Dictionary<DateTime, int>();
            var dayMapAvailable = new Dictionary<DateTime, bool>();
            var dayRuleTriggerHosts = new Dictionary<DateTime, HashSet<long>>();

            // 最新一天用剛重算的當日對應；過去日取「該日或之前最近一日」的既有對應，不硬造（docs/PRTG-SPEC.md §5）。
            // 視窗與觸發式取數同一個，規則歸戶與取數看到的主機才會一致。
            List<PrtgHostMapRow> ResolveHostMapRows(DateTime d) => d == newest
                ? prtgStore.GetHostMapForDate(newest)
                : prtgStore.GetLatestHostMapWithDate(PrtgTriggeredValueFetcher.HostMapLookbackDays, anchor: d).Rows;

            // 5. 逐日迴圈（days 的順序，由近到遠）
            for (var i = 0; i < days.Count; i++)
            {
                progress?.Report(RunPhases.PrtgDateRange, days.Count, i + 1);
                var day = days[i].Date;
                if (days.Count > 1)
                {
                    prtgConsole.WriteLine($"第 {i + 1}／{days.Count} 天（{day:yyyy-MM-dd}）");
                }

                if (!rulesAvailable)
                {
                    var hostMapRows = ResolveHostMapRows(day);
                    var mapAvail = hostMapRows.Any(r => r.MapStatus == PrtgMapStatus.Ok && r.HostId.HasValue);
                    dayMapAvailable[day] = mapAvail;
                    dayFindingsCounts[day] = 0;
                    dayAttributedCounts[day] = 0;
                    dayRuleTriggerHosts[day] = new HashSet<long>();
                    prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
                    continue;
                }

                try
                {
                    var hostMapRows = ResolveHostMapRows(day);

                    var deviceToHost = new Dictionary<long, long>();
                    foreach (var row in hostMapRows)
                    {
                        if (row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue)
                        {
                            deviceToHost[row.DeviceObjid] = row.HostId.Value;
                        }
                    }

                    var mapAvailable = deviceToHost.Count > 0;
                    dayMapAvailable[day] = mapAvailable;

                    var allChanges = prtgStore.GetStateChanges(day.Date.AddDays(-1), day.Date.AddDays(1));
                    var changes = allChanges.Where(c => allowedSensorObjids.Contains(c.SensorObjid)).ToList();

                    var findings = PrtgRuleEvaluator.Evaluate(
                        day, changes, sensorToDevice, sensorStatuses, thresholds, enabledRuleCodes,
                        includeSilent: day == newest);

                    dayFindingsCounts[day] = findings.Count;

                    if (!mapAvailable)
                    {
                        // 理由：用今天的對應套到過去日會把裝置掛到錯的主機上，而且看起來與真的一樣（docs/PRTG-SPEC.md §5「回填不做主機對應」同一條線）。
                        prtgConsole.WriteLine($"{day:yyyy-MM-dd} 無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶");
                        dayAttributedCounts[day] = 0;
                        dayRuleTriggerHosts[day] = new HashSet<long>();
                        prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
                    }
                    else
                    {
                        var findingsByHost = new Dictionary<long, List<LogIssueSignature>>();
                        var triggerHosts = new HashSet<long>();
                        foreach (var finding in findings)
                        {
                            if (deviceToHost.TryGetValue(finding.DeviceObjid, out var hostId))
                            {
                                if (!findingsByHost.TryGetValue(hostId, out var hostFindings))
                                {
                                    hostFindings = new List<LogIssueSignature>();
                                    findingsByHost[hostId] = hostFindings;
                                }
                                hostFindings.Add(PrtgFindingMapper.ToSignature(finding, day));
                                triggerHosts.Add(hostId);
                            }
                        }

                        dayAttributedCounts[day] = findingsByHost.Count;
                        dayRuleTriggerHosts[day] = triggerHosts;

                        prtgFindings.Publish(day, findingsByHost.ToDictionary(
                            kv => kv.Key, kv => (IReadOnlyList<LogIssueSignature>)kv.Value));

                        try
                        {
                            var involvedHosts = findingsByHost.Count;
                            var appendedHosts = 0;
                            var pendingHosts = 0;

                            var hostsById = hostStore.GetAll().ToDictionary(h => h.HostId);

                            foreach (var (hostId, hostFindings) in findingsByHost)
                            {
                                var hostName = hostsById.TryGetValue(hostId, out var webHost) ? webHost.HostName : string.Empty;
                                var hostRecordStore = backend.RecordStore(new HostKey { HostId = hostId, HostName = hostName });

                                if (prtgFindings.AttachExclusive(hostId, day, () => hostRecordStore.AttachPrtgFindings(hostId, day, hostFindings, useAi)))
                                {
                                    appendedHosts++;
                                    HostDayPostProcessor.AttachCase(caseCoordinator, hostName, day, hostFindings.ToList(), "[PRTG] ");
                                }
                                else pendingHosts++;
                            }

                            var summary = $"PRTG 規則評估完成（{day:yyyy-MM-dd}）：finding {findings.Count} 筆、涉及主機 {involvedHosts} 台、" +
                                          $"本階段追加 {appendedHosts} 台（其餘 {pendingHosts} 台由分析路徑就地處理）";
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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
                    if (!dayFindingsCounts.ContainsKey(day)) dayFindingsCounts[day] = 0;
                    if (!dayAttributedCounts.ContainsKey(day)) dayAttributedCounts[day] = 0;
                    if (!dayMapAvailable.ContainsKey(day)) dayMapAvailable[day] = false;
                    if (!dayRuleTriggerHosts.ContainsKey(day)) dayRuleTriggerHosts[day] = new HashSet<long>();
                    if (!prtgFindings.IsPublished(day))
                    {
                        prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
                    }
                }
            }

            // 7. 迴圈結束後才送 PrtgFindingsReady（一次）
            progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);

            // 8. 觸發式數值取數
            var dayTriggeredResults = new Dictionary<DateTime, PrtgTriggeredFetchResult>();
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
                        var ruleHosts = dayRuleTriggerHosts.TryGetValue(day, out var th) ? th : new HashSet<long>();
                        var dayResult = await triggeredFetcher.RunAsync(
                            day, systemSettings.PrtgSensorTypeWhitelist, systemSettings.PrtgFetchConcurrency,
                            () => analysisTask.IsCompleted, ct, extraTriggerHosts: ruleHosts,
                            progress: (stage, done, total) => progress?.Report(stage, done, total),
                            scope: systemSettings.PrtgValueFetchScope,
                            extraScopeHosts: scopeHostIds);

                        dayTriggeredResults[day] = dayResult;
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

            // 每天另外收集一筆 PrtgDayStat（契約 6），迴圈後呼叫 runRecorder.RecordPrtgDays(...)
            var dayStats = new List<PrtgDayStat>(days.Count);
            foreach (var day in days)
            {
                var trigRes = dayTriggeredResults.TryGetValue(day, out var r) ? r : null;
                var dayFailedSensors = trigRes?.FailedSensors ?? 0;

                string dayOutcome;
                if (syncFailed)
                {
                    dayOutcome = BatchRun.PrtgOutcomeFailed;
                }
                else if (dayFailedSensors > 0 || (fetchResult != null && fetchResult.Failures > 0))
                {
                    dayOutcome = BatchRun.PrtgOutcomePartial;
                }
                else
                {
                    dayOutcome = BatchRun.PrtgOutcomeSuccess;
                }

                dayStats.Add(new PrtgDayStat(
                    Date: day,
                    Outcome: dayOutcome,
                    Findings: dayFindingsCounts.TryGetValue(day, out var fc) ? fc : 0,
                    AttributedHosts: dayAttributedCounts.TryGetValue(day, out var ac) ? ac : 0,
                    MapAvailable: dayMapAvailable.TryGetValue(day, out var ma) && ma,
                    TriggerHosts: trigRes?.TriggerHosts ?? 0,
                    TargetSensors: trigRes?.TargetSensors ?? 0,
                    FailedSensors: dayFailedSensors));
            }
            runRecorder.RecordPrtgDays(dayStats);

            // 9. outcome 判定沿用現有規則，改用累加後的數字
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
                    prtgFindings.Publish(day, new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
                    anyBackfilled = true;
                }
            }

            if (anyBackfilled)
            {
                progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);
            }

            if (prtgOutcome != null)
            {
                runRecorder.RecordPrtgOutcome(
                    prtgOutcome,
                    Math.Max(0, totalTargetSensors - totalFailedSensors),
                    totalFailedSensors,
                    totalTriggerHosts);
            }

            // 完工訊號帶結果數字（取數主機數／目標 sensor 數）
            progress?.Report(
                RunPhases.PrtgDone,
                totalTriggerHosts,
                totalTargetSensors);
        }
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
