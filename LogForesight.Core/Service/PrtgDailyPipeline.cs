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
        IPrtgStructureSyncGate? structureSyncGate = null,
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
        var diskAssessmentFailed = false;
        string? interruptedDayReason = null;

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
        var planned = new Dictionary<DateTime, PrtgPlannedDay>();
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
            using var client = PrtgClientFactory.Create(systemSettings, budget: requestBudget);
            client.OperationCheckpoint = operationScope.Checkpoint;
            var monitoringPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var pilotReady = monitoringPolicy.Ready(systemSettings.PrtgUrl);
            var evaluationHostSnapshot = hostStore.CapturePrtgSnapshot();
            var evaluationHosts = evaluationHostSnapshot.Hosts.ToDictionary(host => host.HostId, host => host.ToWebHost());
            var requestedHostIds = hostIds;
            if (pilotReady)
                hostIds = SelectActivePilotHostIds(monitoringPolicy.HostIds, requestedHostIds, () => evaluationHosts.Values.ToList());
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
            var parentProducerSucceeded = true;
            var parentTaskAwaited = false;

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
            // partial 同步會保留其他主機的鏡像；歸戶也必須套本趟主機範圍，
            // 不能因舊鏡像仍在就替未選取、已停用或已合併主機保存新判定。
            var evaluationHostIds = evaluationHosts.Values
                .Where(h => PrtgFormalEligibility.HostAllowed(h, systemSettings, monitoringPolicy) && (hostIds == null || hostIds.Contains(h.HostId)))
                .Select(h => h.HostId).ToHashSet();
            var resourceModeSnapshots = new Dictionary<long, PrtgResourcePressureModeHostSnapshot>();
            var resourceModeSnapshotFailures = new HashSet<long>();
            foreach (var hostId in evaluationHostIds)
            {
                try
                {
                    resourceModeSnapshots[hostId] = new PrtgResourcePressureModeStore(backend.Blob(
                        PrtgResourcePressureModeStore.BlobKey(hostId))).ReadHostSnapshot(hostId);
                }
                catch (Exception modeSnapshotError) when (modeSnapshotError is not OperationCanceledException)
                {
                    resourceModeSnapshotFailures.Add(hostId);
                    Log.Warn(modeSnapshotError, "PRTG mode revocation snapshot unavailable for host {HostId}; its formal attach will be fenced", hostId);
                }
            }
            prtgStore.EnsureResourceAuthorityRevisions(evaluationHostIds);
            var resourceAuthorityRevisionsAtEvaluation = prtgStore.ReadResourceAuthorityRevisions(evaluationHostIds);
            var resourceObservationRevisionsAtEvaluation = prtgStore.ReadResourceObservationRevisions(evaluationHostIds);
            // A qualified trend is another reason of the same disk resource episode. Complete
            // its bounded history assessment before evaluating two-hour pressure and building manifests.
            var qualifiedTrendReasons = new Dictionary<long, IReadOnlyList<PrtgResourceFormalReasonObservation>>();
            var trendEvidenceFacts = new Dictionary<long, string>();
            var trendCatalog = PrtgResourceCurrentRuleCatalog.Load(backend);
            bool HasEnabledValueRule(string? category) => (category?.ToLowerInvariant() switch
            {
                PrtgSensorCategories.Cpu => trendCatalog.For(PrtgResourceFamily.Cpu),
                PrtgSensorCategories.Memory => trendCatalog.For(PrtgResourceFamily.Memory),
                PrtgSensorCategories.Disk => trendCatalog.For(PrtgResourceFamily.Disk),
                _ => null
            }) is not null;
            var trendRule = prtgRules.SingleOrDefault(rule => rule.Id == trendCatalog.DiskTrendRuleId);
            if (pilotReady && newest.Date < DateTime.Today && trendRule is not null && trendCatalog.DiskTrendEnabled)
            {
                try
                {
                    var assessment = new PrtgDiskAssessmentService(prtgStore, hostStore,
                        new SystemSettingsStore(backend.Blob("system_settings")),
                        new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
                        new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
                    var trendOperation = assessment.BeginAssessment(DateOnly.FromDateTime(newest.Date), newest.Date,
                        trendRule, PrtgDiskDecisionMode.Formal, evaluationHostIds.ToArray(), monitoringPolicy.SensorIds, evaluationHostSnapshot);
                    var trendEvidenceAsOfUtc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(newest.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local), DateTimeKind.Utc);
                    var qualifiedActiveTrendSensors = new HashSet<long>();
                    for (var offset = 0; ;)
                    {
                        operationScope.Checkpoint(); ct.ThrowIfCancellationRequested();
                        var page = assessment.AssessPage(trendOperation, offset, PrtgDiskAssessmentService.MaximumBatchSize);
                        var identities = prtgStore.GetResourceIdentities(page.Rows.Select(row => row.SensorObjid));
                        foreach (var row in page.Rows)
                        {
                            if (row.Readiness.Status != PrtgValueReadinessStatus.Ready ||
                                row.EvidenceValidity is not { IsValid: true } ||
                                !identities.TryGetValue(row.SensorObjid, out var identity) || !identity.Active ||
                                identity.PendingReconciliation || identity.SourceGeneration != monitoringPolicy.SourceGeneration ||
                                identity.DeviceId != row.DeviceObjid || identity.HostId != row.CurrentHostId ||
                                row.EvidenceFingerprint.Length != 64) continue;
                            var reasonState = row.Decision.Finding is not null ? PrtgResourceFormalReasonState.Active :
                                row.Decision.Exclusion == PrtgDiskDecisionExclusion.TrendNoHit
                                    ? PrtgResourceFormalReasonState.Recovered : PrtgResourceFormalReasonState.Unknown;
                            var summary = string.Concat(row.Decision.Reason.Where(character => !char.IsControl(character) &&
                                character != '<' && character != '>').Take(256));
                            qualifiedTrendReasons[row.SensorObjid] = [new(row.SensorObjid, identity.SourceGeneration,
                                identity.Generation, identity.ChannelGeneration,
                                identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "disk-seven-day-low-water-trend", reasonState, row.EvidenceFingerprint, summary,
                                EvidenceDay: DateTime.SpecifyKind(newest.Date, DateTimeKind.Unspecified),
                                SourceRuleId: trendRule.Id,
                                SourceRuleFingerprint: PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(trendRule),
                                EvidenceAsOfUtc: trendEvidenceAsOfUtc)];
                            trendEvidenceFacts[row.SensorObjid] = row.EvidenceFingerprint;
                            if (reasonState == PrtgResourceFormalReasonState.Active)
                                qualifiedActiveTrendSensors.Add(row.SensorObjid);
                        }
                        var assessedThrough = Math.Min(page.CandidateCount, page.Offset + page.AssessedCount);
                        prtgConsole.WriteLine($"磁碟候選評估 {assessedThrough}/{page.CandidateCount}；已有趨勢 finding {qualifiedActiveTrendSensors.Count} 筆，尚未提交判定。");
                        if (!page.HasMore) break;
                        if (page.AssessedCount <= 0) throw new InvalidOperationException("Disk trend page made no progress.");
                        offset += page.AssessedCount;
                    }
                    assessment.CompleteAssessment(trendOperation);
                    var liveCatalog = PrtgResourceCurrentRuleCatalog.Load(backend);
                    if (!liveCatalog.DiskTrendEnabled || liveCatalog.DiskTrendRuleId != trendRule.Id ||
                        liveCatalog.DiskTrendRuleFingerprint != trendCatalog.DiskTrendRuleFingerprint)
                        throw new InvalidOperationException("Disk trend rule changed during history assessment.");
                    operationScope.Checkpoint();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    qualifiedTrendReasons.Clear(); trendEvidenceFacts.Clear(); diskAssessmentFailed = true;
                    Log.Error(error, "PRTG disk trend evidence is incomplete; preserving existing resource reasons");
                    prtgConsole.WriteLine("  ⚠ 磁碟趨勢證據未完整：" + error.Message);
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
                var state = dayStates[day] = new PrtgDayState { DiskAssessmentFailed = diskAssessmentFailed };
                var plan = planned[day] = new PrtgPlannedDay();
                foreach (var (hostId, snapshot) in resourceModeSnapshots)
                {
                    plan.ModeBlobVersionsByHost[hostId] = snapshot.BlobVersion;
                    plan.ModeFenceRequiredByHost.Add(hostId);
                    foreach (var revocation in snapshot.Revocations)
                    {
                        if (!plan.PendingModeRevocationsByHost.TryGetValue(hostId, out var pending))
                            plan.PendingModeRevocationsByHost[hostId] = pending = new List<PrtgResourcePressureModeRevocation>();
                        pending.Add(revocation);
                        if (revocation.SourceGeneration.Length > 0 && revocation.ResourceGeneration.Length > 0)
                        {
                            AddPressureGenerationFence(plan, hostId, new(revocation.SensorObjid,
                                revocation.SourceGeneration, revocation.ResourceGeneration));
                            AddExactModeRevocationFence(plan, hostId, new(revocation.SensorObjid,
                                revocation.SourceGeneration, revocation.ResourceGeneration));
                        }
                    }
                }
                foreach (var hostId in resourceModeSnapshotFailures)
                {
                    plan.ModeBlobVersionsByHost[hostId] = -1;
                    plan.ModeFenceRequiredByHost.Add(hostId);
                    plan.PendingReasonByHost[hostId] = "resource-mode-snapshot-unavailable";
                }
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
                    bool RequiresStateTimeline(PrtgSensorStatusInput sensor) => prtgRules.Any(rule =>
                        (rule.PrtgRuleCode is PrtgRuleEvaluator.RuleDown or PrtgRuleEvaluator.RuleWarning or PrtgRuleEvaluator.RuleFlapping) &&
                        PrtgFormalEligibility.RuleCategoryMatches(rule, sensor.Category));
                    // 規則庫沒有 PRTG 規則：該日照樣發佈空結果（「算不出東西」與「還沒算完」要分得出來）
                    var stateSemanticFactsByHost = new Dictionary<long, List<string>>();
                    var stateIdentityFactsByHost = new Dictionary<long, List<string>>();
                    var pageFindings = new List<PrtgFinding>();
                    var duplicateWarnings = new HashSet<string>(StringComparer.Ordinal);
                    // The background incremental worker owns PRTG history requests. Daily reads only
                    // persisted bounded pages; no fleet-sized dictionary retains raw timeline payloads.
                    var selectedStateSensors = sensorStatuses.Where(sensor => monitoringPolicy.SensorIds.Contains(sensor.Objid) &&
                            deviceToHost.ContainsKey(sensor.DeviceObjid) && RequiresStateTimeline(sensor))
                        .OrderBy(sensor => sensor.Objid).ToArray();
                    foreach (var sensorPage in selectedStateSensors.Chunk(12))
                    {
                        operationScope.Checkpoint(); ct.ThrowIfCancellationRequested();
                        var pageIds = sensorPage.Select(sensor => sensor.Objid).ToArray();
                        var evidencePage = PrtgSensorTimelineStore.ReadManyBoundedForSilentRule(backend.CreateContext, pageIds);
                        var identityPage = prtgStore.GetResourceIdentities(pageIds);
                        var applicableEvidence = new Dictionary<long, PrtgSensorTimelineEvidence>();
                        foreach (var pair in evidencePage)
                        {
                            if (!sensorToDevice.TryGetValue(pair.Key, out var device) ||
                                !deviceToHost.TryGetValue(device, out var mappedHost) || mappedHost != pair.Value.HostId ||
                                !identityPage.TryGetValue(pair.Key, out var identity) ||
                                !PrtgResourceQualification.IsCurrent(pair.Value, identity, monitoringPolicy.SourceGeneration,
                                    pair.Key, device, mappedHost)) continue;
                            applicableEvidence[pair.Key] = pair.Value;
                            var proof = pair.Value;
                            var begin = new DateTimeOffset(day); var finish = begin.AddDays(1);
                            var periods = proof.Periods(begin, finish);
                            var sensorInput = sensorStatuses.FirstOrDefault(sensor => sensor.Objid == pair.Key);
                            if (sensorInput is not null && RequiresStateTimeline(sensorInput) &&
                                periods.Sum(period => (period.Through - period.From).TotalSeconds) >= (finish - begin).TotalSeconds &&
                                periods.All(period => !string.Equals(period.Status, "Unknown", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!plan.ReevaluatedResources.TryGetValue(proof.HostId, out var resources))
                                    plan.ReevaluatedResources[proof.HostId] = resources = new();
                                resources[proof.SensorId] = proof.ResourceGeneration;
                            }
                            if (!stateIdentityFactsByHost.TryGetValue(proof.HostId, out var identityFacts))
                                stateIdentityFactsByHost[proof.HostId] = identityFacts = new List<string>();
                            identityFacts.Add($"{identity.SensorId}:{identity.DeviceId}:{identity.HostId}:{identity.Epoch}:{identity.Generation}:{identity.ResourceFingerprint}");
                            if (!stateSemanticFactsByHost.TryGetValue(proof.HostId, out var semanticFacts))
                                stateSemanticFactsByHost[proof.HostId] = semanticFacts = new List<string>();
                            semanticFacts.Add(HostDayWorkflowFingerprint.HashParts(periods.Select(period =>
                                $"{proof.SensorId}:{proof.ResourceGeneration}:{proof.ChannelGeneration}:{proof.DiskSemanticFingerprint}:{period.From:O}:{period.Through:O}:{period.Status}")));
                        }
                        var pageResult = PrtgCoveredRuleEvaluator.Evaluate(day, sensorPage, applicableEvidence, prtgRules);
                        pageFindings.AddRange(pageResult);
                        duplicateWarnings.UnionWith(pageResult.DuplicateRuleWarnings);
                    }
                    var silentRuleEnabled = prtgRules.Any(rule => rule.Enabled &&
                        string.Equals(rule.PrtgRuleCode, PrtgRuleEvaluator.RuleSilent, StringComparison.OrdinalIgnoreCase));
                    PrtgSilentPresenceEvaluation silentEvaluation;
                    try
                    {
                        silentEvaluation = new PrtgSilentPresenceFormalConsumer(backend).EvaluateWithReadiness(
                            day, monitoringPolicy, systemSettings.PrtgUrl, deviceToHost, prtgRules);
                        if (silentRuleEnabled && day == newest && silentEvaluation.Findings.Count == 0)
                            prtgConsole.WriteLine($"{day:yyyy-MM-dd} 靜默監測判定等待：沒有同來源日、完整且符合目前 policy/scope 的原生狀態快照與涵蓋時間軸。");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "PRTG 靜默監測來源證據載入失敗；本日不產生靜默 finding");
                        prtgConsole.WriteLine($"{day:yyyy-MM-dd} 靜默監測判定等待：來源證據無法驗證（{ex.Message}）。");
                        silentEvaluation = new PrtgSilentPresenceEvaluation([], deviceToHost.ToDictionary(
                            pair => pair.Key, pair => new PrtgSilentPresenceDeviceReadiness(pair.Key, pair.Value,
                                PrtgSilentPresenceReadinessState.Waiting, "source-proof-read-failed", string.Empty, [])));
                    }
                    var findings = new PrtgEvaluationResult(pageFindings, duplicateWarnings.Order().ToArray(), 0);
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
                        foreach (var hostId in evaluationHostIds) plan.PendingReasonByHost[hostId] = "host-map-unavailable-for-day";
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
                            var signature = PrtgFindingMapper.ToSignature(finding, day, hostId);
                            hostFindings.Add(signature);
                            plan.Observations.Add((hostId, finding, signature));
                            state.TriggerHosts.Add(hostId);
                        }
                    }
                    state.AttributedHosts = findingsByHost.Count;
                    var resourceFormalFindingCount = 0;
                    var resourceDecisionFactsByHost = new Dictionary<long, List<string>>();
                    var resourceReadinessCompleteByHost = new HashSet<long>();
                    foreach (var (deviceId, readiness) in silentEvaluation.Devices)
                    {
                        if (readiness.State is PrtgSilentPresenceReadinessState.QualifiedHit or PrtgSilentPresenceReadinessState.QualifiedNoHit or
                            PrtgSilentPresenceReadinessState.ExplicitlyExcluded)
                        {
                            if (!resourceDecisionFactsByHost.TryGetValue(readiness.HostId, out var facts))
                                resourceDecisionFactsByHost[readiness.HostId] = facts = new List<string>();
                            facts.Add($"silent:{deviceId}:{readiness.State}:{readiness.Reason}:{readiness.EvidenceFingerprint}");
                        }
                        if (readiness.State == PrtgSilentPresenceReadinessState.Waiting)
                            plan.PendingReasonByHost[readiness.HostId] = "silent-presence-" + readiness.Reason;
                        foreach (var finding in readiness.Findings)
                        {
                            if (!findingsByHost.TryGetValue(readiness.HostId, out var hostFindings))
                                findingsByHost[readiness.HostId] = hostFindings = new List<LogIssueSignature>();
                            var signature = PrtgFindingMapper.ToSignature(finding, day, readiness.HostId);
                            hostFindings.Add(signature);
                            plan.Observations.Add((readiness.HostId, finding, signature));
                            state.TriggerHosts.Add(readiness.HostId);
                            resourceFormalFindingCount++;
                        }
                    }
                    var silentReadyHosts = deviceToHost.Values.Distinct()
                        .Where(hostId => silentEvaluation.IsReadyForHost(hostId)).ToHashSet();
                    var selectedValueHosts = sensorStatuses.Where(sensor => monitoringPolicy.SensorIds.Contains(sensor.Objid) &&
                        deviceToHost.ContainsKey(sensor.DeviceObjid) && TryWorkflowFamily(sensor.Category, out _) && HasEnabledValueRule(sensor.Category))
                        .Select(sensor => deviceToHost[sensor.DeviceObjid]).ToHashSet();
                    foreach (var hostId in deviceToHost.Values.Distinct().Where(hostId => !selectedValueHosts.Contains(hostId)))
                    {
                        var hostSensorIds = selectedStateSensors.Where(sensor => deviceToHost[sensor.DeviceObjid] == hostId)
                            .Select(sensor => sensor.Objid).ToArray();
                        var epoch = PrtgResourcePeriodConsumer.ComputeSelectionEpoch(prtgStore.GetResourceIdentities(hostSensorIds).Values);
                        var fingerprint = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(monitoringPolicy, [hostId], hostSensorIds);
                        workflow?.BeginPrtgReadiness(hostId, day, epoch, fingerprint, []);
                        workflow?.ClosePrtgSelectedSensors(hostId, day, epoch, fingerprint);
                        resourceReadinessCompleteByHost.Add(hostId);
                    }

                    // Publish the bounded live resource consumer as a separate finite readiness
                    // pass. Its selection is frozen globally before paging; only assessments whose
                    // complete two-hour window maps to this host day can change its daily findings.
                    if (pilotReady)
                    {
                        var familySensors = sensorStatuses.Where(sensor => monitoringPolicy.SensorIds.Contains(sensor.Objid) &&
                                deviceToHost.ContainsKey(sensor.DeviceObjid) &&
                                TryWorkflowFamily(sensor.Category, out _) && HasEnabledValueRule(sensor.Category))
                            .GroupBy(sensor => sensor.Objid).Select(group => group.First())
                            .Select(sensor => (Sensor: sensor, HostId: deviceToHost[sensor.DeviceObjid],
                                Family: WorkflowFamily(sensor.Category!)))
                            .ToArray();
                        if (familySensors.Length == 0)
                        {
                            // No value rules are required for a state-only host. Its exact selected
                            // state sensor set and covered periods still gate formal completion.
                        }
                        else
                        {
                            var sensorIds = familySensors.Select(item => item.Sensor.Objid).Distinct().Order().ToArray();
                            var identities = prtgStore.GetResourceIdentities(sensorIds);
                            var readinessEpoch = PrtgResourcePeriodConsumer.ComputeSelectionEpoch(identities.Values);
                            var selectionFingerprint = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(
                                monitoringPolicy, evaluationHostIds, sensorIds);
                            var expectedByHost = familySensors.GroupBy(item => item.HostId).ToDictionary(group => group.Key,
                                group => group.GroupBy(item => item.Family).ToDictionary(family => family.Key, family => family.Count()));
                            var familyEnumByName = Enum.GetValues<PrtgWorkflowResourceFamily>()
                                .ToDictionary(family => family.ToString(), family => family, StringComparer.OrdinalIgnoreCase);
                            var readinessStarted = new HashSet<long>();
                            foreach (var (hostId, expectedCounts) in expectedByHost)
                            {
                                workflow?.BeginPrtgReadiness(hostId, day, readinessEpoch, selectionFingerprint,
                                    expectedCounts.Keys, expectedCounts);
                                readinessStarted.Add(hostId);
                            }

                            {
                                var completeDayWindow = day.Date < DateTime.Today;
                                try
                                {
                                    if (!completeDayWindow)
                                    {
                                        foreach (var hostId in readinessStarted)
                                            plan.PendingReasonByHost[hostId] = "resource-period-day-not-closed";
                                    }
                                    else
                                    {
                                        var authorityNowUtc = DateTime.UtcNow;
                                        var consumer = new PrtgResourcePeriodConsumer(backend,
                                            new SystemSettingsStore(backend.Blob("system_settings")));
                                        var returnedSensorIds = new HashSet<long>();
                                        var returnedFamilies = new HashSet<PrtgWorkflowResourceFamily>();
                                        for (var offset = 0; offset < sensorIds.Length; offset += PrtgResourcePeriodConsumer.MaximumBatchSize)
                                        {
                                            operationScope.Checkpoint();
                                            var pageIds = sensorIds.Skip(offset).Take(PrtgResourcePeriodConsumer.MaximumBatchSize).ToArray();
                                            var pageHosts = familySensors.Where(item => pageIds.Contains(item.Sensor.Objid))
                                                .Select(item => item.HostId).Distinct().ToArray();
                                            // This is a host calendar day, not an instant. Scheduler dates may
                                            // carry Local kind; preserve the stored parent while making the
                                            // consumer's calendar-day representation explicit at this seam.
                                            var hostCalendarDay = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
                                            var consumption = consumer.EvaluateClosedHostDayBatch(pageIds, hostCalendarDay, authorityNowUtc,
                                                diskReasonObservations: day.Date == newest.Date ? qualifiedTrendReasons : null, evaluationHostIds: pageHosts);
                                            var closedDay = consumption.ClosedDay;
                                            var expectedPageEpoch = PrtgResourcePeriodConsumer.ComputeSelectionEpoch(
                                                pageIds.Where(identities.ContainsKey).Select(id => identities[id]));
                                            var expectedPageSelection = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(
                                                monitoringPolicy, pageHosts, pageIds);
                                            if (closedDay is null || closedDay.EvidenceDay.Date != day.Date ||
                                                !closedDay.SelectedSensorObjids.Order().SequenceEqual(consumption.SelectedSensorObjids.Order()) ||
                                                closedDay.SelectionEpoch != consumption.SelectionEpoch ||
                                                consumption.SelectionEpoch != expectedPageEpoch ||
                                                consumption.SelectedSensorFingerprint != expectedPageSelection)
                                                throw new InvalidOperationException("Resource selection changed during page evaluation.");
                                            returnedSensorIds.UnionWith(consumption.SelectedSensorObjids);
                                            foreach (var (hostId, version) in consumption.ModeBlobVersionsByHost)
                                            {
                                                if (plan.ModeBlobVersionsByHost.TryGetValue(hostId, out var priorVersion) &&
                                                    priorVersion != version)
                                                    plan.ModeBlobVersionsByHost[hostId] = -1;
                                                else if (!plan.ModeBlobVersionsByHost.ContainsKey(hostId))
                                                    plan.ModeBlobVersionsByHost[hostId] = version;
                                            }
                                            foreach (var (hostId, resources) in consumption.ResourcePressureReevaluatedResourcesByHost)
                                            {
                                                foreach (var generation in resources)
                                                    AddPressureGenerationFence(plan, hostId, generation);
                                            }
                                            foreach (var revocation in consumption.PendingModeRevocations)
                                            {
                                                if (!plan.PendingModeRevocationsByHost.TryGetValue(revocation.HostId, out var pending))
                                                    plan.PendingModeRevocationsByHost[revocation.HostId] = pending = new List<PrtgResourcePressureModeRevocation>();
                                                if (!pending.Any(item => item.SensorObjid == revocation.SensorObjid &&
                                                        item.Family == revocation.Family && item.SourceGeneration == revocation.SourceGeneration &&
                                                        item.ResourceGeneration == revocation.ResourceGeneration))
                                                    pending.Add(revocation);
                                                if (revocation.SourceGeneration.Length > 0 && revocation.ResourceGeneration.Length > 0)
                                                {
                                                    var exact = new PrtgResourceGenerationFence(revocation.SensorObjid,
                                                        revocation.SourceGeneration, revocation.ResourceGeneration);
                                                    AddPressureGenerationFence(plan, revocation.HostId, exact);
                                                    AddExactModeRevocationFence(plan, revocation.HostId, exact);
                                                }
                                            }
                                            foreach (var enabledFamily in consumption.EnabledFamilies)
                                                if (familyEnumByName.TryGetValue(enabledFamily.ToString(), out var mappedFamily))
                                                    returnedFamilies.Add(mappedFamily);
                                            foreach (var assessment in consumption.Assessments)
                                            {
                                                if (!readinessStarted.Contains(assessment.HostId) ||
                                                    !familyEnumByName.TryGetValue(assessment.Family.ToString(), out var workflowFamily))
                                                    continue;
                                                var decision = assessment.Decision.Kind.ToString() switch
                                                {
                                                    "Hit" => PrtgWorkflowAssessment.Hit,
                                                    "NoHit" => PrtgWorkflowAssessment.NoHit,
                                                    "Recovery" => PrtgWorkflowAssessment.Recovery,
                                                    _ => PrtgWorkflowAssessment.Insufficient
                                                };
                                                var windowCountsValid = closedDay.ExpectedWindowCountsBySensor.TryGetValue(
                                                        assessment.SensorObjid, out var expectedWindows) &&
                                                    closedDay.EvaluatedWindowCountsBySensor.TryGetValue(
                                                        assessment.SensorObjid, out var evaluatedWindows) &&
                                                    expectedWindows is > 0 and <= 25 && evaluatedWindows <= expectedWindows;
                                                if (assessment.SingleWindowHostDay?.Date != day.Date ||
                                                    !windowCountsValid || closedDay.ExceededBatchRowBound ||
                                                    closedDay.RejectedSensorObjids.Contains(assessment.SensorObjid))
                                                    decision = PrtgWorkflowAssessment.Insufficient;
                                                if (assessment.Family == PrtgResourceFamily.Disk &&
                                                    assessment.SingleWindowHostDay?.Date == day.Date &&
                                                    (assessment.Decision.Kind is PrtgResourceDecisionKind.NoHit or PrtgResourceDecisionKind.Recovery) &&
                                                    day.Date == newest.Date && qualifiedTrendReasons.TryGetValue(assessment.SensorObjid, out var trendObservations) &&
                                                        trendObservations.Any(observation => observation.State is
                                                            PrtgResourceFormalReasonState.Active or PrtgResourceFormalReasonState.Recovered))
                                                {
                                                    if (!plan.DiskReevaluatedResources.TryGetValue(assessment.HostId, out var diskResources))
                                                        plan.DiskReevaluatedResources[assessment.HostId] = diskResources = new Dictionary<long, string>();
                                                    diskResources[assessment.SensorObjid] = assessment.ResourceGeneration;
                                                }
                                                workflow?.PublishPrtgResourceAssessment(assessment.HostId, day, readinessEpoch,
                                                    selectionFingerprint, workflowFamily, decision);
                                                if (assessment.SingleWindowHostDay?.Date == day.Date &&
                                                    assessment.Decision.Kind.ToString() != "Insufficient")
                                                {
                                                    if (!resourceDecisionFactsByHost.TryGetValue(assessment.HostId, out var facts))
                                                        resourceDecisionFactsByHost[assessment.HostId] = facts = new List<string>();
                                                    facts.Add($"{assessment.SensorObjid}:{assessment.Family}:{assessment.Decision.Kind}:{assessment.EvidenceFingerprint}");
                                                }
                                            }
                                            foreach (var qualified in consumption.QualifiedFormalFindings.Where(item =>
                                                item.EvidenceDay.Date == day.Date))
                                            {
                                                var finding = qualified.Finding;
                                                if (!deviceToHost.TryGetValue(finding.DeviceObjid, out var findingHostId)) continue;
                                                if (!findingsByHost.TryGetValue(findingHostId, out var hostFindings))
                                                    findingsByHost[findingHostId] = hostFindings = new List<LogIssueSignature>();
                                                var signature = PrtgFindingMapper.ToSignature(finding, day, findingHostId);
                                                hostFindings.Add(signature); resourceFormalFindingCount++;
                                                plan.Observations.Add((findingHostId, finding, signature));
                                                state.TriggerHosts.Add(findingHostId);
                                                if (finding.SensorObjid is { } sensorId && trendEvidenceFacts.TryGetValue(sensorId, out var trendFact))
                                                {
                                                    if (!resourceDecisionFactsByHost.TryGetValue(findingHostId, out var facts))
                                                        resourceDecisionFactsByHost[findingHostId] = facts = new List<string>();
                                                    facts.Add($"{sensorId}:qualified-trend:{trendFact}");
                                                }
                                            }
                                        }
                                        var finalEpoch = PrtgResourcePeriodConsumer.ComputeSelectionEpoch(
                                            prtgStore.GetResourceIdentities(sensorIds).Values);
                                        if (finalEpoch == readinessEpoch && returnedSensorIds.SetEquals(sensorIds) &&
                                            returnedFamilies.SetEquals(familySensors.Select(item => item.Family)))
                                        {
                                            foreach (var hostId in readinessStarted)
                                            {
                                                workflow?.ClosePrtgSelectedSensors(hostId, day, readinessEpoch, selectionFingerprint);
                                                if (workflow?.Get(hostId, day) is { PrtgReadinessComplete: true })
                                                    resourceReadinessCompleteByHost.Add(hostId);
                                            }
                                        }
                                        else
                                        {
                                            foreach (var hostId in readinessStarted)
                                                plan.PendingReasonByHost[hostId] = "resource-selection-changed-during-evaluation";
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception assessmentError)
                                {
                                    foreach (var hostId in readinessStarted)
                                        plan.PendingReasonByHost[hostId] = "resource-period-assessment-failed";
                                    Log.Warn(assessmentError, "PRTG bounded resource period assessment failed for {Day}", day);
                                    prtgConsole.WriteLine($"  ⚠ {day:yyyy-MM-dd} 資源期間評估未完成：{assessmentError.Message}");
                                }
                            }
                        }
                    }

                    // A complete evaluation manifest exists for every mapped host, including zero findings.
                    // Only compact fingerprints are persisted; per-resource evidence remains in its bounded stores.
                    var ruleFingerprint = HostDayWorkflowFingerprint.HashParts(prtgRules
                        .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                        .Select(rule => System.Text.Json.JsonSerializer.Serialize(rule)));
                    var closedExpectedSensorsByHost = sensorStatuses
                        .Where(sensor => monitoringPolicy.SensorIds.Contains(sensor.Objid) &&
                            sensorToDevice.TryGetValue(sensor.Objid, out var deviceId) && deviceToHost.ContainsKey(deviceId) &&
                            RequiresStateTimeline(sensor))
                        .Select(sensor => (HostId: deviceToHost[sensorToDevice[sensor.Objid]], sensor.Objid))
                        .GroupBy(item => item.HostId)
                        .ToDictionary(group => group.Key, group => group.Select(item => item.Objid).Distinct().ToHashSet());
                    var persistedWholeEvidenceHosts = workflow?.WholeEvidenceHostIdsForDay(day) ?? Array.Empty<long>();
                    var wholeEvidenceHosts = deviceToHost.Values
                        .Concat(workflow?.ParentHostIdsForDay(day) ?? Array.Empty<long>())
                        .Concat(persistedWholeEvidenceHosts)
                        .Distinct().ToArray();
                    plan.WholeEvidenceHosts.UnionWith(wholeEvidenceHosts);
                    foreach (var hostId in wholeEvidenceHosts)
                    {
                        if (!closedExpectedSensorsByHost.ContainsKey(hostId)) closedExpectedSensorsByHost[hostId] = new HashSet<long>();
                        if (!plan.ReevaluatedResources.ContainsKey(hostId)) plan.ReevaluatedResources[hostId] = new Dictionary<long, string>();
                    }
                    var authoritativeParents = new Dictionary<long, DailyAnalysisRecord>();
                    foreach (var hostId in deviceToHost.Values.Distinct())
                    {
                        if (!parentTaskAwaited)
                        {
                            parentTaskAwaited = true;
                            try { await analysisTask.ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception producerError)
                            {
                                parentProducerSucceeded = false;
                                Log.Warn(producerError, "Daily analysis producer failed before PRTG manifest publication");
                                prtgConsole.WriteLine("  ⚠ 分析工作失敗；PRTG 不建立替代主機日 parent。");
                            }
                        }
                        if (!parentProducerSucceeded)
                        {
                            plan.PendingReasonByHost[hostId] = "analysis-parent-producer-failed";
                            continue;
                        }
                        var host = evaluationHosts.GetValueOrDefault(hostId);
                        if (host == null) continue;
                        var parent = backend.RecordStore(new HostKey { HostId = hostId, HostName = host.HostName })
                            .ReadRecent(day, 1).FirstOrDefault(row => row.HostId == hostId && row.Date.Date == day.Date);
                        if (parent is { RecordId: > 0 } && parent.CanSupplementWithPrtg())
                        {
                            authoritativeParents[hostId] = parent;
                            plan.CapturedParentPrtgFingerprintsByHost[hostId] =
                                PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg));
                        }
                    }
                    var livePolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                    var liveRuleFingerprint = HostDayWorkflowFingerprint.HashParts(KnownIssueCatalog.Rules
                        .Where(rule => string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && rule.Enabled)
                        .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                        .Select(rule => System.Text.Json.JsonSerializer.Serialize(rule)));
                    var liveHostMap = ResolveHostMapRows(day).Where(row => row.MapStatus == PrtgMapStatus.Ok &&
                            row.HostId.HasValue && evaluationHostIds.Contains(row.HostId.Value))
                        .GroupBy(row => row.DeviceObjid).ToDictionary(group => group.Key, group => group.Last().HostId!.Value);
                    var publicationAuthorityCurrent = parentProducerSucceeded &&
                        string.Equals(livePolicy.Revision, monitoringPolicy.Revision, StringComparison.Ordinal) &&
                        string.Equals(livePolicy.SourceAuthorityFingerprint(systemSettings.PrtgUrl),
                            monitoringPolicy.SourceAuthorityFingerprint(systemSettings.PrtgUrl), StringComparison.Ordinal) &&
                        string.Equals(liveRuleFingerprint, ruleFingerprint, StringComparison.Ordinal) &&
                        deviceToHost.Count == liveHostMap.Count && deviceToHost.All(pair =>
                            liveHostMap.TryGetValue(pair.Key, out var liveHostId) && liveHostId == pair.Value);
                    if (!publicationAuthorityCurrent)
                        foreach (var hostId in deviceToHost.Values.Distinct())
                            plan.PendingReasonByHost[hostId] = parentProducerSucceeded
                                ? "formal-source-rule-or-host-mapping-drift" : "analysis-parent-producer-failed";
                    var completeManifestHosts = publicationAuthorityCurrent ? closedExpectedSensorsByHost
                        .Where(entry => prtgRules.Any(rule => !string.IsNullOrWhiteSpace(rule.PrtgRuleCode)) &&
                            resourceReadinessCompleteByHost.Contains(entry.Key) &&
                            silentReadyHosts.Contains(entry.Key) &&
                            authoritativeParents.ContainsKey(entry.Key) &&
                            plan.ReevaluatedResources.TryGetValue(entry.Key, out var covered) &&
                            entry.Value.SetEquals(covered.Keys))
                        .Select(entry => entry.Key).ToHashSet() : new HashSet<long>();
                    var partialManifestHosts = publicationAuthorityCurrent
                        ? deviceToHost.Values.Distinct().Where(hostId => !completeManifestHosts.Contains(hostId) &&
                            authoritativeParents.ContainsKey(hostId) &&
                            findingsByHost.TryGetValue(hostId, out var qualifiedFindings) && qualifiedFindings.Count > 0).ToHashSet()
                        : new HashSet<long>();
                    var manifestReadyHosts = completeManifestHosts.Union(partialManifestHosts).ToHashSet();
                    state.FormalEvidencePending = deviceToHost.Values.Distinct().Any(hostId => !completeManifestHosts.Contains(hostId));
                    foreach (var hostId in deviceToHost.Values.Distinct().Where(hostId => !manifestReadyHosts.Contains(hostId)))
                    {
                        if (!plan.PendingReasonByHost.ContainsKey(hostId))
                            plan.PendingReasonByHost[hostId] = !prtgRules.Any(rule => !string.IsNullOrWhiteSpace(rule.PrtgRuleCode))
                            ? "no-enabled-prtg-rules"
                            : "timeline-resource-semantic-or-sample-quality-not-ready";
                    }
                    plan.ManifestsByHost = manifestReadyHosts.ToDictionary(hostId => hostId, hostId =>
                    {
                        var parent = authoritativeParents[hostId];
                        var parentFindingFingerprint = plan.CapturedParentPrtgFingerprintsByHost[hostId];
                        var resourcesFingerprint = HostDayWorkflowFingerprint.HashParts(stateIdentityFactsByHost.GetValueOrDefault(hostId) ?? []);
                        var semanticFacts = (stateSemanticFactsByHost.GetValueOrDefault(hostId) ?? [])
                            .Concat(resourceDecisionFactsByHost.GetValueOrDefault(hostId) ?? []);
                        var semanticFingerprint = HostDayWorkflowFingerprint.HashParts(semanticFacts);
                        var hostMappingFingerprint = HostDayWorkflowFingerprint.HashParts(deviceToHost
                            .Where(pair => pair.Value == hostId).OrderBy(pair => pair.Key)
                            .Select(pair => PrtgSilentPresenceMappingFingerprint.Compute(pair.Key, pair.Value)));
                        var strategyFingerprint = HostDayWorkflowFingerprint.HashParts([
                            systemSettings.PrtgFetchStrategy, monitoringPolicy.SourceTimeZoneId,
                            monitoringPolicy.SourceCultureName, conservativeStrategy.ToString(),
                            monitoringPolicy.SourceAuthorityFingerprint(systemSettings.PrtgUrl),
                            hostMappingFingerprint]);
                        var findingFingerprint = PrtgFindingMapper.Fingerprint(findingsByHost.GetValueOrDefault(hostId) ?? []);
                        var waitReasonCodes = completeManifestHosts.Contains(hostId) ? new List<string>() :
                            BuildManifestWaitReasonCodes(hostId, plan, silentEvaluation, resourceReadinessCompleteByHost,
                                silentReadyHosts, closedExpectedSensorsByHost, plan.ReevaluatedResources);
                        var outcome = completeManifestHosts.Contains(hostId) ? "complete" : "partial";
                        return new PrtgDecisionManifest
                        {
                            ParentRecordId = parent.RecordId,
                            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
                            PolicyRevision = monitoringPolicy.Revision,
                            SourceGeneration = monitoringPolicy.SourceGeneration,
                            ResourceAuthorityRevision = resourceAuthorityRevisionsAtEvaluation.GetValueOrDefault(hostId),
                            ResourceModeBlobVersion = plan.ModeBlobVersionsByHost.GetValueOrDefault(hostId, -1),
                            ResourceModeFenceRequired = plan.ModeFenceRequiredByHost.Contains(hostId),
                            ResourceFingerprint = resourcesFingerprint,
                            SemanticFingerprint = semanticFingerprint,
                            StrategyFingerprint = strategyFingerprint,
                            HostMappingFingerprint = hostMappingFingerprint,
                            RuleFingerprint = ruleFingerprint,
                            EvidenceFingerprint = HostDayWorkflowFingerprint.HashParts([
                                monitoringPolicy.SourceGeneration, resourcesFingerprint, semanticFingerprint,
                                strategyFingerprint, hostMappingFingerprint, ruleFingerprint, outcome,
                                plan.ModeBlobVersionsByHost.GetValueOrDefault(hostId, -1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                string.Join("|", waitReasonCodes)]),
                            ParentFindingFingerprint = parentFindingFingerprint,
                            FindingFingerprint = findingFingerprint,
                            CompletedAtUtc = DateTime.UtcNow,
                            Outcome = outcome,
                            WaitReasonCodes = waitReasonCodes
                        };
                    });

                    plan.FindingsByHost = findingsByHost;
                    // 折疊前總數：被合併掉的不在 findings 裡，「其中已合併 c 筆」才會是 N 的子集
                    plan.FindingCount = findings.Count + findings.MergedCount + resourceFormalFindingCount;
                    state.Findings += resourceFormalFindingCount;
                    state.AttributedHosts = findingsByHost.Count;
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
                    state.FormalEvidencePending = true;
                    foreach (var hostId in evaluationHostIds) plan.PendingReasonByHost[hostId] = "prtg-rule-evaluation-failed";
                    Log.Error(ex, "PRTG 規則評估失敗，不影響分析成果");
                    prtgConsole.WriteLine($"\n  ✗ PRTG 規則評估失敗：{ex.Message}");
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
                    var finalPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                    var finalRuleFingerprint = HostDayWorkflowFingerprint.HashParts(KnownIssueCatalog.Rules
                        .Where(rule => string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && rule.Enabled)
                        .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                        .Select(rule => System.Text.Json.JsonSerializer.Serialize(rule)));
                    var finalHostMap = ResolveHostMapRows(day).Where(row => row.MapStatus == PrtgMapStatus.Ok &&
                            row.HostId.HasValue && evaluationHostIds.Contains(row.HostId.Value))
                        .GroupBy(row => row.DeviceObjid).ToDictionary(group => group.Key, group => group.Last().HostId!.Value);
                    var finalResourceAuthorityRevisions = prtgStore.ReadResourceAuthorityRevisions(
                        plan.ManifestsByHost?.Keys.AsEnumerable() ?? Enumerable.Empty<long>());
                    var finalResourceObservationRevisions = prtgStore.ReadResourceObservationRevisions(
                        plan.ManifestsByHost?.Keys.AsEnumerable() ?? Enumerable.Empty<long>());
                    var globalPublicationFence = string.Equals(finalPolicy.Revision, monitoringPolicy.Revision, StringComparison.Ordinal) &&
                        string.Equals(finalPolicy.SourceAuthorityFingerprint(systemSettings.PrtgUrl),
                            monitoringPolicy.SourceAuthorityFingerprint(systemSettings.PrtgUrl), StringComparison.Ordinal) &&
                        string.Equals(finalRuleFingerprint, HostDayWorkflowFingerprint.HashParts(prtgRules
                            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                            .Select(rule => System.Text.Json.JsonSerializer.Serialize(rule))), StringComparison.Ordinal);
                    var finalManifests = new Dictionary<long, PrtgDecisionManifest>();
                    if (globalPublicationFence && plan.ManifestsByHost is not null)
                    {
                        foreach (var (hostId, manifest) in plan.ManifestsByHost)
                        {
                            var currentHostMappingFingerprint = HostDayWorkflowFingerprint.HashParts(finalHostMap
                                .Where(pair => pair.Value == hostId).OrderBy(pair => pair.Key)
                                .Select(pair => PrtgSilentPresenceMappingFingerprint.Compute(pair.Key, pair.Value)));
                            if (!string.Equals(currentHostMappingFingerprint, manifest.HostMappingFingerprint, StringComparison.Ordinal) ||
                                manifest.SourceGeneration != finalPolicy.SourceGeneration ||
                                manifest.ResourceModeFenceRequired && manifest.ResourceModeBlobVersion < 0 ||
                                manifest.ResourceModeFenceRequired && backend.Blob(
                                    PrtgResourcePressureModeStore.BlobKey(hostId)).ReadVersion() != manifest.ResourceModeBlobVersion ||
                                manifest.ResourceAuthorityRevision <= 0 ||
                                !resourceAuthorityRevisionsAtEvaluation.TryGetValue(hostId, out var evaluatedAuthorityRevision) ||
                                manifest.ResourceAuthorityRevision != evaluatedAuthorityRevision ||
                                !finalResourceAuthorityRevisions.TryGetValue(hostId, out var finalAuthorityRevision) ||
                                manifest.ResourceAuthorityRevision != finalAuthorityRevision ||
                                !resourceObservationRevisionsAtEvaluation.TryGetValue(hostId, out var evaluatedObservationRevision) ||
                                !finalResourceObservationRevisions.TryGetValue(hostId, out var finalObservationRevision) ||
                                evaluatedObservationRevision != finalObservationRevision)
                                continue;
                            var host = evaluationHosts.GetValueOrDefault(hostId);
                            if (host is null) continue;
                            var parent = backend.RecordStore(new HostKey { HostId = hostId, HostName = host.HostName })
                                .ReadRecent(day, 1).FirstOrDefault(row => row.HostId == hostId && row.Date.Date == day.Date);
                            if (parent is not { RecordId: > 0 } || parent.RecordId != manifest.ParentRecordId ||
                                !parent.CanSupplementWithPrtg() ||
                                !string.Equals(HostDayWorkflowFingerprint.ForParentRecord(parent), manifest.ParentFingerprint, StringComparison.Ordinal) ||
                                !plan.CapturedParentPrtgFingerprintsByHost.TryGetValue(hostId, out var capturedParentFindingFingerprint) ||
                                !string.Equals(PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
                                    capturedParentFindingFingerprint, StringComparison.Ordinal))
                                continue;
                            finalManifests[hostId] = manifest;
                        }
                    }
                    var timerResourceAuthorityRevisions = prtgStore.ReadResourceAuthorityRevisions(finalManifests.Keys);
                    var timerResourceObservationRevisions = prtgStore.ReadResourceObservationRevisions(finalManifests.Keys);
                    foreach (var hostId in plan.WholeEvidenceHosts)
                    {
                        if (finalManifests.TryGetValue(hostId, out var qualifiedManifest) &&
                            qualifiedManifest.Outcome == "complete")
                        {
                            if (!timerResourceAuthorityRevisions.TryGetValue(hostId, out var currentAuthorityRevision) ||
                                qualifiedManifest.ResourceAuthorityRevision != currentAuthorityRevision ||
                                qualifiedManifest.ResourceModeFenceRequired && qualifiedManifest.ResourceModeBlobVersion < 0 ||
                                qualifiedManifest.ResourceModeFenceRequired && backend.Blob(
                                    PrtgResourcePressureModeStore.BlobKey(hostId)).ReadVersion() != qualifiedManifest.ResourceModeBlobVersion ||
                                !resourceObservationRevisionsAtEvaluation.TryGetValue(hostId, out var evaluatedObservationRevision) ||
                                !timerResourceObservationRevisions.TryGetValue(hostId, out var currentObservationRevision) ||
                                evaluatedObservationRevision != currentObservationRevision)
                            {
                                finalManifests.Remove(hostId);
                                plan.PendingReasonByHost[hostId] = "resource-profile-authority-changed";
                                dayStates[day].FormalEvidencePending = true;
                                workflow?.InvalidateWholePrtgEvidence(hostId, day, "resource-profile-authority-changed");
                                continue;
                            }
                            if (workflow is not null)
                            {
                                var readyAtUtc = DateTime.UtcNow;
                                if (!workflow.RecordWholePrtgEvidenceReady(hostId, day, qualifiedManifest, readyAtUtc.ToLocalTime()))
                                {
                                    finalManifests.Remove(hostId);
                                    plan.PendingReasonByHost[hostId] = "whole-evidence-workflow-fence-rejected";
                                    dayStates[day].FormalEvidencePending = true;
                                    workflow.InvalidateWholePrtgEvidence(hostId, day, "whole-evidence-workflow-fence-rejected");
                                }
                                else
                                {
                                    var completedAtUtc = DateTime.UtcNow;
                                    qualifiedManifest.CompletedAtUtc = completedAtUtc > readyAtUtc
                                        ? completedAtUtc : readyAtUtc;
                                }
                            }
                        }
                        else
                        {
                            workflow?.InvalidateWholePrtgEvidence(hostId, day,
                                plan.PendingReasonByHost.GetValueOrDefault(hostId, "necessary-evidence-not-ready"));
                        }
                    }
                    if (plan.FindingsByHost is not null)
                    {
                        foreach (var hostId in plan.FindingsByHost.Keys.Where(hostId => !finalManifests.ContainsKey(hostId)).ToArray())
                            plan.FindingsByHost.Remove(hostId);
                    }
                    if (plan.ManifestsByHost?.Keys.Any(hostId => !finalManifests.ContainsKey(hostId)) == true)
                        dayStates[day].FormalEvidencePending = true;
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

                    var hostsById = evaluationHosts;
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

                    // Cross-day escalation and suppression are part of the decision we attach.
                    // Keep the manifest's incoming finding fingerprint aligned with that final
                    // version; SQL recomputes the merged parent+finding fingerprint atomically.
                    foreach (var (hostId, manifest) in finalManifests)
                        manifest.FindingFingerprint = PrtgFindingMapper.Fingerprint(
                            findingsByHost.GetValueOrDefault(hostId) ?? []);

                    try
                    {
                        var involvedHosts = findingsByHost.Count;
                        var appendedHosts = 0;
                        var pendingHosts = 0;
                        var corroboratedCount = 0;

                        // The loop can drop hosts whose transaction preimage became stale. Snapshot the
                        // keys first so removing that host from the publication maps is safe.
                        foreach (var hostId in finalManifests.Keys.AsEnumerable()
                                     .Union(findingsByHost.Keys)
                                     .Union(plan.ExactModeRevocationGenerationsByHost.Keys).Distinct().ToArray())
                        {
                            operationScope.Checkpoint();
                            var hostFindings = findingsByHost.GetValueOrDefault(hostId) ?? [];
                            var hostName = hostsById.TryGetValue(hostId, out var webHost) ? webHost.HostName : string.Empty;
                            var hostRecordStore = backend.RecordStore(new HostKey { HostId = hostId, HostName = hostName });
                            var manifest = finalManifests.GetValueOrDefault(hostId);
                            var hasExactModeRevocations = plan.ExactModeRevocationGenerationsByHost.ContainsKey(hostId);
                            var modeOnlyRevocation = manifest == null && hasExactModeRevocations;
                            var reconciliation = manifest == null && !hasExactModeRevocations ? null : new PrtgStateReconciliationBatch(
                                monitoringPolicy.SourceGeneration,
                                manifest == null ? new Dictionary<long, string>() :
                                    plan.ReevaluatedResources.GetValueOrDefault(hostId) ?? new Dictionary<long, string>(),
                                manifest == null ? new Dictionary<long, string>() :
                                    plan.DiskReevaluatedResources.GetValueOrDefault(hostId) ?? new Dictionary<long, string>(),
                                manifest == null ? new HashSet<string>(StringComparer.Ordinal) :
                                    hostFindings.Select(finding => finding.EventKey).ToHashSet(StringComparer.Ordinal),
                                manifest == null ? plan.ExactModeRevocationGenerationsByHost.GetValueOrDefault(hostId) :
                                    plan.ResourcePressureReevaluatedResourcesByHost.GetValueOrDefault(hostId),
                                plan.ModeBlobVersionsByHost.GetValueOrDefault(hostId, -1),
                                plan.ModeFenceRequiredByHost.Contains(hostId));

                            var hostCorroborated = 0;
                            if (prtgFindings.AttachExclusive(hostId, day, () => manifest is not null && reconciliation is not null
                                    ? hostRecordStore.AttachPrtgFindingsWithReconciliation(hostId, day, hostFindings,
                                        suppressedPatternIdsByHost.GetValueOrDefault(hostId, new HashSet<string>()), out hostCorroborated, useAi,
                                        manifest!, reconciliation)
                                    : reconciliation is not null
                                        ? hostRecordStore.AttachPrtgModeRevocationsWithReconciliation(hostId, day, [],
                                            suppressedPatternIdsByHost.GetValueOrDefault(hostId, new HashSet<string>()),
                                            out hostCorroborated, useAi, reconciliation)
                                        : hostRecordStore.AttachPrtgFindings(hostId, day, hostFindings,
                                        suppressedPatternIdsByHost.GetValueOrDefault(hostId, new HashSet<string>()), out hostCorroborated, useAi)))
                            {
                                if (!modeOnlyRevocation && hostFindings.Count > 0) appendedHosts++;
                                corroboratedCount += hostCorroborated;
                                var captured = 0;
                                foreach (var observation in plan.Observations.Where(item => !modeOnlyRevocation && item.HostId == hostId))
                                {
                                    operationScope.Checkpoint();
                                    try
                                    {
                                        captured += backend.PrtgObservationStore().Capture(hostId, day, systemSettings.Revision,
                                            [(observation.Finding, observation.Signature)], systemSettings.PrtgUrl, runRecorder.RunId);
                                    }
                                    catch (Exception observationError) when (observationError is not OperationCanceledException)
                                    {
                                        Log.Warn(observationError, "PRTG 附加觀察快照寫入失敗；主機日 finding 已原子持久");
                                        prtgConsole.WriteLine("  ⚠ PRTG 附加觀察快照寫入失敗；正式主機日判定已持久化。");
                                    }
                                }
                                if (captured > 0)
                                    prtgConsole.WriteLine($"主機 {hostId} 獨立 PRTG 判定快照：新增 {captured} 筆（可信版本等待合格 NetIQ 日紀錄補追加；未知品質只作診斷）。");
                                if (!modeOnlyRevocation && hostFindings.Count > 0)
                                    HostDayPostProcessor.AttachCase(caseCoordinator, dispatch, hostName, day,
                                        hostFindings.ToList(), "[PRTG] ", workflow, hostId,
                                        manifest?.ParentRecordId ?? 0);
                            }
                            else
                            {
                                finalManifests.Remove(hostId);
                                findingsByHost.Remove(hostId);
                                suppressedPatternIdsByHost.Remove(hostId);
                                workflow?.InvalidateWholePrtgEvidence(hostId, day, "formal-manifest-parent-fence-rejected");
                                dayStates[day].FormalEvidencePending = true;
                                if (hostFindings.Count > 0) pendingHosts++;
                            }
                        }

                        prtgFindings.Publish(day, findingsByHost.ToDictionary(
                            pair => pair.Key, pair => (IReadOnlyList<LogIssueSignature>)pair.Value), suppressedPatternIdsByHost);
                        prtgFindings.PublishManifests(day, finalManifests);

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
            else if ((fetchResult != null && fetchResult.Failures > 0) || totalFailedSensors > 0 || diskAssessmentFailed)
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

            // Reconcile only host-days written by this run. The analysis row is authoritative; workflow blobs
            // are a recoverable sidecar and never decide whether a successful source write is kept.
            foreach (var day in days)
            {
                foreach (var hostId in workflow?.ParentHostIdsForDay(day) ?? Array.Empty<long>())
                {
                    try
                    {
                        var prior = workflow!.Get(hostId, day);
                        if (prior == null || prior.Parent != WorkflowLegState.Succeeded) continue;
                        var records = backend.RecordStore(new HostKey { HostId = hostId, HostName = prior.HostName })
                            .ReadRecent(day, 1);
                        var authoritative = records.FirstOrDefault(row => row.HostId == hostId && row.Date.Date == day.Date);
                        if (authoritative == null)
                        {
                            workflow.ParentDeleted(hostId, day);
                            continue;
                        }
                        var manifest = authoritative.PrtgManifest;
                        var currentRunManifest = prtgFindings.ManifestFor(hostId, day);
                        var validManifest = currentRunManifest != null && HostDayWorkflowFingerprint.HasValidPrtgManifest(authoritative) && manifest is { Version: 1 } &&
                            (manifest.Outcome is "complete" or "partial") &&
                            manifest.HostMappingFingerprint is { Length: 64 } mappingFingerprint && mappingFingerprint.All(Uri.IsHexDigit) &&
                            manifest.WaitReasonCodes is { Count: <= 16 } waitReasons && waitReasons.All(code => code is { Length: > 0 and <= 128 }) &&
                            (manifest.Outcome == "complete" ? waitReasons.Count == 0 : waitReasons.Count > 0) &&
                            StringComparer.Ordinal.Equals(currentRunManifest.EvidenceFingerprint, manifest.EvidenceFingerprint) &&
                            currentRunManifest.CompletedAtUtc == manifest.CompletedAtUtc &&
                            manifest.ParentRecordId == authoritative.RecordId &&
                            manifest.CompletedAtUtc.Kind == DateTimeKind.Utc &&
                            StringComparer.Ordinal.Equals(manifest.ParentFingerprint, HostDayWorkflowFingerprint.ForParentRecord(authoritative)) &&
                            StringComparer.Ordinal.Equals(manifest.FindingFingerprint,
                                PrtgFindingMapper.Fingerprint(authoritative.TopIssues.Where(PrtgFindingMapper.IsPrtg)));
                        var completedRun = prtgOutcome is BatchRun.PrtgOutcomeSuccess or BatchRun.PrtgOutcomeNoOutput or BatchRun.PrtgOutcomePartial;
                        var partialManifest = validManifest && manifest!.Outcome == "partial";
                        var prtgState = !prtgEnabled ? WorkflowLegState.Disabled :
                            completedRun && validManifest && !partialManifest ? WorkflowLegState.Succeeded :
                            completedRun && partialManifest ? WorkflowLegState.Degraded :
                            completedRun ? WorkflowLegState.Waiting : WorkflowLegState.Degraded;
                        var waitReason = planned.TryGetValue(day.Date, out var currentPlan) &&
                            currentPlan.PendingReasonByHost.TryGetValue(hostId, out var reason)
                                ? reason : "necessary-evidence-not-ready";
                        workflow.SetPrtg(hostId, day, prtgState, evidenceReady: prtgState == WorkflowLegState.Succeeded,
                            evidenceFingerprint: validManifest ? manifest!.EvidenceFingerprint : null,
                            now: validManifest ? manifest!.CompletedAtUtc.ToLocalTime() : null,
                            failure: partialManifest ? string.Join(",", manifest!.WaitReasonCodes) : prtgState == WorkflowLegState.Waiting ? waitReason :
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
                dayStats = days
                    .Where(dayStates.ContainsKey)
                    .Select(day =>
                    {
                        var s = dayStates[day];
                        var failedSensors = s.Triggered?.FailedSensors ?? 0;
                        var (outcome, note) = ClassifyDay(syncFailed, failedSensors > 0 || stageFailed || s.DiskAssessmentFailed || s.FormalEvidencePending,
                            rulesAvailableForStat, sensorMirrorEmpty, s.MapAvailable, conservativeStrategy);
                        if (s.DiskAssessmentFailed && outcome == BatchRun.PrtgOutcomePartial)
                            note = "磁碟趨勢證據未完整；尚未完成風險判定。";
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
                else if (prtgOutcome == BatchRun.PrtgOutcomeSuccess && dayStats.Any(d => d.Outcome == BatchRun.PrtgOutcomePartial))
                {
                    prtgOutcome = BatchRun.PrtgOutcomePartial;
                }
            }

            if (interruptedDayReason is not null)
            {
                dayStats ??= [];
                dayStats.AddRange(days.Where(day => !dayStates.ContainsKey(day))
                    .Select(day => new PrtgDayStat(day, BatchRun.PrtgOutcomePartial, 0, 0, false, 0, 0, 0, interruptedDayReason)));
                var statsByDay = dayStats.ToDictionary(stat => stat.Date.Date);
                dayStats = days.Select(day => statsByDay[day.Date]).ToList();
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

    /// <summary>保留試點設定順序，並以一次 host snapshot 過濾啟用中的 NetIQ 未合併主機。</summary>
    internal static long[] SelectActivePilotHostIds(IEnumerable<long> policyHostIds,
        IEnumerable<long>? requestedHostIds, Func<List<WebHost>> readHosts)
    {
        var requested = requestedHostIds?.ToHashSet();
        var eligible = readHosts().Where(h => h.Source == "netiq" && h.Active && h.MergedInto == null)
            .Select(h => h.HostId).ToHashSet();
        return policyHostIds.Where(id => (requested is null || requested.Contains(id)) && eligible.Contains(id)).ToArray();
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

    private static bool TryWorkflowFamily(string? category, out PrtgWorkflowResourceFamily family)
    {
        switch (category?.Trim().ToLowerInvariant())
        {
            case PrtgSensorCategories.Cpu: family = PrtgWorkflowResourceFamily.Cpu; return true;
            case PrtgSensorCategories.Memory: family = PrtgWorkflowResourceFamily.Memory; return true;
            case PrtgSensorCategories.Disk: family = PrtgWorkflowResourceFamily.Disk; return true;
            default: family = default; return false;
        }
    }

    private static PrtgWorkflowResourceFamily WorkflowFamily(string category) =>
        TryWorkflowFamily(category, out var family) ? family :
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported PRTG workflow resource family.");

    private static List<string> BuildManifestWaitReasonCodes(long hostId, PrtgPlannedDay plan,
        PrtgSilentPresenceEvaluation silentEvaluation, IReadOnlySet<long> resourceReadyHosts,
        IReadOnlySet<long> silentReadyHosts, IReadOnlyDictionary<long, HashSet<long>> expectedStateSensorsByHost,
        IReadOnlyDictionary<long, Dictionary<long, string>> coveredStateSensorsByHost)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (plan.PendingReasonByHost.TryGetValue(hostId, out var pendingReason)) reasons.Add(pendingReason);
        if (plan.PendingModeRevocationsByHost.TryGetValue(hostId, out var pendingModeRevocations) &&
            pendingModeRevocations.Any(revocation =>
            {
                // A wildcard marker records a bounded request to inspect this resource. It has no
                // generation authority and cannot withdraw a finding; keep it out of the host-day
                // completeness gate so an off resource with unavailable evidence does not remain
                // perpetually pending. Exact markers are inserted into the exact fence map above.
                if (revocation.SourceGeneration.Length == 0 || revocation.ResourceGeneration.Length == 0) return false;
                if (!plan.ResourcePressureReevaluatedResourcesByHost.TryGetValue(hostId, out var resources)) return true;
                return !resources.Any(fence => fence.SensorObjid == revocation.SensorObjid &&
                        fence.SourceGeneration == revocation.SourceGeneration &&
                        fence.ResourceGeneration == revocation.ResourceGeneration);
            }))
            reasons.Add("resource-mode-revocation-awaiting-current-pressure");
        if (!silentReadyHosts.Contains(hostId))
            foreach (var reason in silentEvaluation.WaitingReasonsForHost(hostId)) reasons.Add("silent-presence-" + reason);
        if (!resourceReadyHosts.Contains(hostId)) reasons.Add("resource-family-evidence-not-ready");
        if (!expectedStateSensorsByHost.TryGetValue(hostId, out var expected) ||
            !coveredStateSensorsByHost.TryGetValue(hostId, out var covered) || !expected.SetEquals(covered.Keys))
            reasons.Add("state-timeline-coverage-incomplete");
        if (reasons.Count == 0) reasons.Add("required-formal-proof-unavailable");
        return reasons.Order(StringComparer.Ordinal).Take(16)
            .Select(reason => string.Concat(reason.Where(character => !char.IsControl(character)).Take(128))).ToList();
    }

    /// <summary>
    /// 第一段留給第二段的逐日結果。<see cref="FindingsByHost"/> 為 null＝該日發佈空結果
    /// （規則庫沒有 PRTG 規則、無主機對應可用、評估失敗）。
    /// </summary>
    private sealed class PrtgPlannedDay
    {
        public HashSet<long> WholeEvidenceHosts { get; } = new();
        public Dictionary<long, Dictionary<long, string>> ReevaluatedResources { get; } = new();
        public Dictionary<long, Dictionary<long, string>> DiskReevaluatedResources { get; } = new();
        public Dictionary<long, List<PrtgResourceGenerationFence>> ResourcePressureReevaluatedResourcesByHost { get; } = new();
        public Dictionary<long, List<PrtgResourceGenerationFence>> ExactModeRevocationGenerationsByHost { get; } = new();
        public Dictionary<long, long> ModeBlobVersionsByHost { get; } = new();
        public HashSet<long> ModeFenceRequiredByHost { get; } = new();
        public Dictionary<long, List<PrtgResourcePressureModeRevocation>> PendingModeRevocationsByHost { get; } = new();
        public Dictionary<long, string> CapturedParentPrtgFingerprintsByHost { get; } = new();
        public List<(long HostId, PrtgFinding Finding, LogIssueSignature Signature)> Observations { get; } = new();
        public Dictionary<long, List<LogIssueSignature>>? FindingsByHost;
        public Dictionary<long, PrtgDecisionManifest>? ManifestsByHost;
        public Dictionary<long, string> PendingReasonByHost { get; } = new();
        public int FindingCount;
        public int AcknowledgedCount;
        public int MergedCount;
    }

    private static void AddPressureGenerationFence(PrtgPlannedDay plan, long hostId,
        PrtgResourceGenerationFence fence)
    {
        if (!plan.ResourcePressureReevaluatedResourcesByHost.TryGetValue(hostId, out var values))
            plan.ResourcePressureReevaluatedResourcesByHost[hostId] = values = new List<PrtgResourceGenerationFence>();
        if (!values.Contains(fence)) values.Add(fence);
        if (values.Count > PrtgStateReconciliationBatch.MaximumItems)
            throw new InvalidOperationException("PRTG pressure revocation batch exceeds its bounded item limit.");
    }

    private static void AddExactModeRevocationFence(PrtgPlannedDay plan, long hostId,
        PrtgResourceGenerationFence fence)
    {
        if (!plan.ExactModeRevocationGenerationsByHost.TryGetValue(hostId, out var values))
            plan.ExactModeRevocationGenerationsByHost[hostId] = values = new List<PrtgResourceGenerationFence>();
        if (!values.Contains(fence)) values.Add(fence);
        if (values.Count > PrtgStateReconciliationBatch.MaximumItems)
            throw new InvalidOperationException("PRTG exact mode revocation batch exceeds its bounded item limit.");
    }

    /// <summary>某一天在這趟裡累計出來的結果，最後寫成 <see cref="PrtgDayStat"/>。</summary>
    private sealed class PrtgDayState
    {
        public int Findings;
        public int AttributedHosts;
        public bool MapAvailable;
        public bool DiskAssessmentFailed;
        public bool FormalEvidencePending;
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
