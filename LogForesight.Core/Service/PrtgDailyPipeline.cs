using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Analysis;
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
    public static async Task RunAsync(
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, DateTime day, Task analysisTask,
        PrtgResourceGuard? guard = null)
    {
        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress, prtgFindings) = ctx;

        var prtgConsole = new PrefixedRunConsole(console, "[PRTG] ");
        string? prtgOutcome = null;
        PrtgTriggeredFetchResult? triggeredResult = null;

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

            // 1. 結構與狀態變更同步（數值階段略過，改由下方觸發式取數執行）
            // PRTG 進度 phase：prtg-sync（結構同步）、prtg-values（每日數值）、prtg-triggered（觸發式數值）、prtg-done（完工）
            PrtgFetchResult? fetchResult = null;
            var syncFailed = false;
            try
            {
                progress?.Report(RunPhases.PrtgSync, 0, 0);
                fetchResult = await fetchService.FetchDayAsync(
                    day, systemSettings.PrtgFetchConcurrency, ct, syncStructure: true, fetchValues: false,
                    (stage, done, total) => progress?.Report(stage, done, total));

                var summary = $"PRTG 每日擷取完成（{day:yyyy-MM-dd}）：裝置 {fetchResult.Devices}、感測器 {fetchResult.Sensors}、" +
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

            // 2. PRTG 主機對應：獨立的 try/catch，對應失敗不拖垮前面的擷取結果
            try
            {
                var hostMapper = new PrtgHostMapper(backend.PrtgStore(), hostStore, prtgConsole);
                var mapResult = hostMapper.MapForDate(day);
                runRecorder.Milestone($"PRTG 主機對應完成（{day:yyyy-MM-dd}）：ok={mapResult.Ok}, manual={mapResult.Manual}, conflict={mapResult.Conflict}, unmatched={mapResult.Unmatched}, skipped_no_ip={mapResult.SkippedNoIp}, skipped_excluded={mapResult.SkippedExcluded}, skipped_manual_sibling={mapResult.SkippedManualSibling}");
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

            // 3. PRTG 規則評估：獨立的 try/catch
            var ruleTriggerHosts = new HashSet<long>();
            var findingsByHost = new Dictionary<long, List<LogIssueSignature>>();
            var totalFindings = 0;
            try
            {
                var prtgStore = backend.PrtgStore();
                var whitelist = new HashSet<string>(systemSettings.PrtgSensorTypeWhitelist ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var allSensors = prtgStore.GetSensorStatuses();
                var filteredSensors = whitelist.Count == 0
                    ? allSensors
                    : allSensors.Where(s => whitelist.Contains(s.SensorType)).ToList();

                var allowedSensorObjids = filteredSensors.Select(s => s.Objid).ToHashSet();
                var allChanges = prtgStore.GetStateChanges(day.Date.AddDays(-1), day.Date.AddDays(1));
                var changes = allChanges.Where(c => allowedSensorObjids.Contains(c.SensorObjid)).ToList();

                var sensorToDevice = filteredSensors
                    .GroupBy(s => s.Objid)
                    .ToDictionary(g => g.Key, g => g.First().DeviceObjid);

                var sensorStatuses = filteredSensors
                    .Select(s => (s.Objid, s.DeviceObjid, s.Status))
                    .ToList();

                var prtgRules = KnownIssueCatalog.Rules
                    .Where(r => string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && r.Enabled)
                    .ToList();

                var enabledRuleCodes = prtgRules
                    .Where(r => !string.IsNullOrEmpty(r.PrtgRuleCode))
                    .Select(r => r.PrtgRuleCode!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 既有部署升級後規則庫裡還沒有 PRTG 規則（要到規則維護頁套用內建規則更新才會出現）。
                // 這時 enabledRuleCodes 是空集合、四條規則全部不啟用——必須說出來，
                // 否則「PRTG 規則零 finding」與「環境真的沒事」在畫面上長得一模一樣。
                if (enabledRuleCodes.Count == 0)
                {
                    prtgConsole.WriteLine("規則庫尚無啟用中的 PRTG 規則（升級後請至「規則維護」頁套用內建規則更新），本次略過規則評估。");
                    throw new PrtgRulesUnavailableException();
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

                var findings = PrtgRuleEvaluator.Evaluate(
                    day, changes, sensorToDevice, sensorStatuses, thresholds, enabledRuleCodes);

                totalFindings = findings.Count;

                var hostMapRows = prtgStore.GetHostMapForDate(day);
                var deviceToHost = new Dictionary<long, long>();
                foreach (var row in hostMapRows)
                {
                    if (row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue)
                    {
                        deviceToHost[row.DeviceObjid] = row.HostId.Value;
                    }
                }

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
                        ruleTriggerHosts.Add(hostId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PrtgRulesUnavailableException)
            {
                // 已在上面輸出說明；不是錯誤，不記 error log
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
            }

            // 3b. 發佈 finding 登錄簿並宣告就緒（docs/PRTG-SPEC.md §9）。
            // **規則評估失敗、規則庫尚無 PRTG 規則、零 finding 都要發佈**——「算不出東西」與
            // 「還沒算完」必須分得出來，不發佈的話 AI 分析排程會一路等到整趟取數結束。
            prtgFindings.Publish(findingsByHost.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<LogIssueSignature>)kv.Value));

            // 補追加「就緒之前就已落地」的主機日：分析與 PRTG 並行，規則評估完成時已經有
            // 一部分主機日寫進去了，它們錯過了寫入路徑的當場追加。之後才落地的由
            // HostDayPostProcessor.AttachPrtgFindings 在紀錄剛寫完時就地處理，不必輪詢。
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

                    // 依 EventKey 去重：與寫入路徑對同一天重複呼叫也不會產生重複列
                    if (hostRecordStore.AttachPrtgFindings(hostId, day, hostFindings, useAi))
                    {
                        appendedHosts++;

                        // 這條路的紀錄早在案件掛接跑完之後才被追加 finding，掛接看不到它們。
                        // 補掛一次（AttachNewDay 冪等，本來就是「下次執行冪等補掛」的語意），
                        // 否則 PRTG finding 永遠進不了問題案件與處理狀態鏈。
                        HostDayPostProcessor.AttachCase(caseCoordinator, hostName, day, hostFindings.ToList(), "[PRTG] ");
                    }
                    else pendingHosts++;
                }

                var summary = $"PRTG 規則評估完成（{day:yyyy-MM-dd}）：finding {totalFindings} 筆、涉及主機 {involvedHosts} 台、" +
                              $"已追加 {appendedHosts} 台（{pendingHosts} 台的當日紀錄尚未落地，稍後由分析路徑就地追加）";
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

            // 通知 Web 端：當日 PRTG finding 已到齊，AI 分析排程可以開始處理當日待補，
            // 不必等整趟取數結束（見 docs/DETECTION-SPEC.md 兩個獨立排程）。
            progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);

            // 4. PRTG 觸發式數值取數：獨立的 try/catch，與分析並行輪詢
            try
            {
                progress?.Report(RunPhases.PrtgTriggered, 0, 0);
                var triggeredFetcher = new PrtgTriggeredValueFetcher(
                    fetchService, backend.PrtgStore(), backend.RecordStore(), prtgConsole);
                // 取數範圍（docs/PRTG-SPEC.md §3a）：指定清單以主機名稱存放，這裡解析成 id；
                // 對不到的名稱要說出來——管理者打錯字時靜默略過會讓人以為設定生效了。
                var (scopeHostIds, unresolvedHosts) = PrtgValueFetchScope.ResolveHostNames(
                    systemSettings.PrtgValueFetchExtraHosts,
                    hostStore.GetAll().Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

                if (unresolvedHosts.Count > 0)
                {
                    prtgConsole.WriteLine($"  ⚠ 取數範圍的指定主機有 {unresolvedHosts.Count} 個對不到主機主檔，已略過：" +
                                          string.Join("、", unresolvedHosts.Take(10)));
                }

                triggeredResult = await triggeredFetcher.RunAsync(
                    day, systemSettings.PrtgSensorTypeWhitelist, systemSettings.PrtgFetchConcurrency,
                    () => analysisTask.IsCompleted, ct, extraTriggerHosts: ruleTriggerHosts,
                    progress: (stage, done, total) => progress?.Report(stage, done, total),
                    scope: systemSettings.PrtgValueFetchScope,
                    extraScopeHosts: scopeHostIds);

                var scopeText = PrtgValueFetchScope.Normalize(systemSettings.PrtgValueFetchScope) switch
                {
                    PrtgValueFetchScope.AllMapped => "全部已對應主機",
                    PrtgValueFetchScope.TriggeredPlusList => "觸發主機＋指定清單",
                    _ => "觸發主機"
                };

                var summary = $"PRTG 觸發式取數完成（{day:yyyy-MM-dd}，範圍：{scopeText}）：主機 {triggeredResult.TriggerHosts} 台、" +
                              $"sensor {triggeredResult.TargetSensors} 個、數值 {triggeredResult.ValuesWritten} 筆" +
                              (triggeredResult.FailedSensors > 0 ? $"、失敗 sensor {triggeredResult.FailedSensors} 個" : "");

                prtgConsole.WriteLine(summary);
                runRecorder.Milestone(summary);
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

            if (syncFailed)
            {
                prtgOutcome = BatchRun.PrtgOutcomeFailed;
            }
            else if ((fetchResult != null && fetchResult.Failures > 0) || (triggeredResult != null && triggeredResult.FailedSensors > 0))
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
            // 任何路徑（PRTG 停用、初始化失敗、取消）結束時若還沒宣告就緒，補一次空發佈。
            // 少了這道，AI 分析排程會一路等到整趟取數結束——等於這個機制沒做。
            if (!prtgFindings.IsReady)
            {
                prtgFindings.Publish(new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
                progress?.Report(RunPhases.PrtgFindingsReady, 0, 0);
            }

            if (prtgOutcome != null)
            {
                runRecorder.RecordPrtgOutcome(
                    prtgOutcome,
                    // 「已取」＝目標數扣掉失敗數；直接填目標數會讓畫面「sensor 100／失敗 40」讀成抓了 100 個
                    Math.Max(0, (triggeredResult?.TargetSensors ?? 0) - (triggeredResult?.FailedSensors ?? 0)),
                    triggeredResult?.FailedSensors ?? 0,
                    triggeredResult?.TriggerHosts ?? 0);
            }
            progress?.Report(RunPhases.PrtgDone, 0, 0);
        }
    }
}
