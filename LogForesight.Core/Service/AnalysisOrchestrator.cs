using System.Diagnostics;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 執行範圍（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.2）：Full＝排程觸發的完整執行
/// （本機＋全部 NetIQ 主機）；LocalOnly＝只跑本機；NetiqHosts＝只跑指定的 NetIQ 主機
/// （<see cref="RunRequest.HostIds"/>）。後兩者是手動觸發（立即執行／指定主機更新）的載體。
/// </summary>
public enum RunScope
{
    Full,
    LocalOnly,
    NetiqHosts
}

/// <summary>一次執行的請求參數。</summary>
public class RunRequest
{
    public RunScope Scope { get; init; } = RunScope.Full;

    /// <summary>僅 <see cref="RunScope.NetiqHosts"/> 時有意義：要分析的 NetIQ 主機 HostId 清單。</summary>
    public IReadOnlyList<long>? HostIds { get; init; }

    /// <summary>手動觸發的一次性回補天數覆寫（Phase 3）；null＝用 NetiqOptions.BackfillDays。</summary>
    public int? BackfillOverride { get; init; }

    public bool DebugDump { get; init; }

    /// <summary>
    /// <see cref="BatchRun.Trigger"/>：<c>"schedule"</c>｜<c>"manual:{帳號}"</c>
    /// （歷史紀錄中另有已退場的 console 批次寫入的 <c>"console"</c>）。
    /// </summary>
    public string? Trigger { get; init; }
}

/// <summary>單次執行的結果摘要，供呼叫端（Web 的 BatchRun 回填與失敗回饋）使用。</summary>
public class OrchestratorResult
{
    public bool Success { get; set; } = true;
    public string? FailureMessage { get; set; }
    public List<DailyAnalysisRecord> LocalResults { get; set; } = new();
    public NetiqPipelineResult? NetiqResult { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// 執行輸出的抽象：只抽「輸出去哪裡」，不抽「輸出什麼」——<see cref="AnalysisOrchestrator"/>
/// 內文保留原本的格式化邏輯（框線、emoji 分段），呼叫 <see cref="WriteLine"/> 取代直接呼叫
/// <c>Console.*</c>。唯一的實作是 Web 端 adapter（<c>WebRunConsole</c>），落地 NLog／
/// <see cref="BatchRunRecorder"/> milestone。
/// </summary>
public interface IRunConsole
{
    void WriteLine(string message = "");
}

/// <summary>
/// 執行進度回報（docs/archive/FEEDBACK-8-PLAN.md #2）：粒度是「主機日」（done/total），分本機／NetIQ
/// 兩階段回報，取代原本只有一行 <see cref="IRunConsole.WriteLine"/> 訊息、看不出量化進度的狀況。
/// NetIQ 段 total 隨平行掃描各 Sentinel 逐步累加（見 <c>NetiqPipelineService</c>）——只會變大、
/// 不會倒退，呼叫端（Web 狀態卡）據此畫進度條；null＝不回報（測試預設不傳）。
/// </summary>
public interface IRunProgress
{
    void Report(string phase, int done, int total);
}

/// <summary>
/// 分析主流程的單一入口（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.2）：權限檢查 → 清理 → 本機逐日分析 →
/// NetIQ 機房分析 → 體檢。原本寫在批次 console 的 <c>Program.cs</c>，Phase 2 抽出成共用單一入口，
/// console 專案退場（Phase 5，§1.5）後唯一的呼叫端是 Web 排程（<c>SchedulerHostedService</c>）。
///
/// **刻意不在這裡做的事**（維持呼叫端的職責）：
/// - <see cref="AppSettings"/> 載入與 <c>SystemSettings</c> DB 覆寫合併——呼叫端負責，
///   Web 排程每次觸發前重新讀取即可達成「每次執行重建服務」，不需要把設定來源寫死在這裡。
/// - 併發保護——行程內單一執行 gate（<c>SchedulerRunState</c>）與跨行程具名 Mutex
///   （<c>Global\LogForesight</c>，<c>NamedMutexGate</c>）都在呼叫端，本類別假設同一時間
///   只有一個執行個體在跑。
/// </summary>
public class AnalysisOrchestrator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // 趨勢比對窗口天數（涵蓋兩個完整週期，能分辨每週固定雜訊與異常趨勢）——分析語意常數，不開放設定
    private const int TrendWindowDays = 14;

    public async Task<OrchestratorResult> RunAsync(
        RunRequest request, AppSettings settings, string dataRoot,
        RetentionOptions retention, IRunConsole console, CancellationToken ct, IRunProgress? progress = null)
    {
        var runStopwatch = Stopwatch.StartNew();
        var result = new OrchestratorResult();

        try
        {
            var ruleStore = StorageFactory.CreateRuleStore(settings.Storage, dataRoot);
            RuleBootstrapper.Run(ruleStore);

            // 同步內建規則的原廠種子鏡像（docs/WEB-SPEC.md §2.1 Phase 4）：Web 的「回復預設」需要
            // 一份使用者碰不到的原始內容才比較得出差異。放在 RuleBootstrapper 之後——那時規則庫已就緒。
            try
            {
                StorageFactory.CreateRuleSeedStore(settings.Storage, dataRoot)
                    .Sync(KnownIssueSeed.CreateRules(), KnownIssueSeed.Version);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "規則種子鏡像同步失敗（不影響本次分析）：{0}", ex.Message);
            }

            var suppressionStore = StorageFactory.CreateSuppressionStore(settings.Storage, dataRoot);
            var currentHost = Environment.MachineName;
            var expiredSuppressions = SuppressionFilter.ExpiredForHost(suppressionStore.LoadAll(), currentHost, DateTime.Now);
            foreach (var expired in expiredSuppressions)
            {
                console.WriteLine($"  ℹ 抑制已到期，恢復告警：{expired.RuleId}（原訂於 {expired.ExpiresAt:yyyy-MM-dd} 到期，" +
                                  $"原因：{expired.Reason}；未自動清理，可於「規則維護」頁的「告警抑制」分頁解除）");
            }

            // 執行紀錄（docs/WEB-SPEC.md §2.1 Phase 4）：啟動時登記、結束時回填，讓 Web 的執行監控頁
            // 能回答「昨晚每台主機都跑了嗎、有沒有出問題」。掛上 NLog target 後 Warn 以上自動流入，
            // 不需要在既有程式碼各處加呼叫。建立失敗不影響分析（見 BatchRunRecorder）。
            BatchRunStore? batchRunStore = null;
            try
            {
                batchRunStore = StorageFactory.CreateBatchRunStore(settings.Storage, dataRoot);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "執行紀錄儲存初始化失敗（不影響本次分析）：{0}", ex.Message);
                // 可見回報（docs/archive/FEEDBACK-8-PLAN.md #3）：原本只寫 log，這趟執行會在執行監控頁
                // 整筆「消失」（不是顯示失敗，是查不到發生過），使用者只能翻 log 檔才查得到
                console.WriteLine($"  ⚠ 執行紀錄儲存初始化失敗（不影響本次分析，但這趟執行本次不會出現在執行監控）：{ex.Message}");
            }

            // 把取消權杖交給 recorder：優雅停止時 OperationCanceledException 會直接離開本 using 範圍，
            // Dispose 據此把這次執行回填成「已停止」而不是「異常中斷」（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）
            using var runRecorder = new BatchRunRecorder(batchRunStore, currentHost, Array.Empty<string>(), request.Trigger, ct,
                onRegistrationFailed: msg => console.WriteLine($"  ⚠ {msg}"));
            runRecorder.Milestone($"批次啟動（版本 {typeof(AnalysisOrchestrator).Assembly.GetName().Version}）");

            var eventLogService = new EventLogService();
            IPromptDumper dumper = request.DebugDump ? new FilePromptDumper() : new NullPromptDumper();
            var aiService = new AIService(settings.Ai, dumper);
            var reportSink = new FileReportSink(Path.Combine(dataRoot, "export")); // 風險報告輸出至資料根目錄下的 export

            // AI 是否已設定（docs/archive/FEEDBACK-7-PLAN.md）：未設定時自動短路成統計模式（規則/趨勢/關聯
            // 照常執行，只是不呼叫 AI），不再逐日嘗試打逾時再降級——那樣會讓整晚的排程被逾時
            // ×重試拖得又慢又沒意義。settings.Ai 在呼叫本方法前已套用過 DB 覆寫
            // （RuntimeSettingsResolver.ApplySystemSettingsOverrides，見呼叫端），此處讀到的
            // BaseUrl 就是本次執行的事實值。
            var useAi = settings.Ai.IsConfigured;
            if (!useAi)
            {
                console.WriteLine("\nℹ AI 未設定：本次以統計模式執行（規則分類、趨勢比對、跨 log 關聯照常進行，" +
                                   "僅白話摘要與體檢敘事從缺；設定「系統管理 > 設定 > AI 服務」後下次執行自動恢復）。");
                runRecorder.Milestone("AI 未設定，本次以統計模式執行");
            }

            // 登記本機於主機清單（docs/WEB-SPEC.md §2.1 Phase 1）：Web 的儀表板要能指出
            // 「哪些主機已經好幾天沒回報了」，而那個判斷需要一筆「這台主機最近何時執行過」的紀錄。
            // 同時取回主機 PK——那是分析紀錄與主機的關聯鍵（docs/archive/HISTORY.md），
            // 所以這段必須排在分析服務、以及本機歸戶的歷史 store 建立之前。
            // 刻意只呼叫 Touch——它只建立缺少的主機並更新回報時間，不碰 Web 維護的角色描述、
            // 群組與負責人（批次不知道那些欄位，用空值蓋掉會把人工設定清光）。
            // 失敗不得中斷分析：此時 hostId 維持 0，當晚的紀錄改由主機名稱歸戶（查詢端的 fallback 路徑）。
            //
            // 以下三段都操作同一批主機/Sentinel 資料，共用同一組 store 實例。
            var hostStore = StorageFactory.CreateHostStore(settings.Storage, dataRoot);
            var sentinelStore = StorageFactory.CreateSentinelStore(settings.Storage, dataRoot);

            // SentinelId 回填（docs/archive/HISTORY.md 定案 4）：一次性遷移，冪等，排最前面——
            // 後面的孤兒掃描與 Pollable 判定都改看 SentinelId，沒先回填的話舊資料會被誤判成待歸屬。
            try
            {
                var backfill = SentinelIdBackfiller.Run(hostStore, sentinelStore);
                if (backfill.BackfilledCount > 0)
                    console.WriteLine($"  已回填 {backfill.BackfilledCount} 台主機的 SentinelId" +
                        (backfill.UnresolvedCount > 0 ? $"（另有 {backfill.UnresolvedCount} 台對不到現存 Sentinel，維持待歸屬）" : "。"));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SentinelId 回填失敗（不影響本次分析）：{0}", ex.Message);
            }

            // Sentinel 生命週期：Sentinel 被刪除時，停用其所屬 NetIQ 主機。
            // 排在 Touch 之前——不停用的孤兒主機會變成「看起來在監控、實際沒人看」的靜默黑洞。
            try
            {
                var sentinelIds = sentinelStore.GetAll().Select(s => s.SentinelId).ToList();
                var sweep = NetiqOrphanSweeper.Sweep(hostStore, sentinelIds);
                if (sweep.OrphanedCount > 0)
                    console.WriteLine($"  ⚠ 偵測到 Sentinel 已被刪除，已停用所屬 NetIQ 主機 {sweep.OrphanedCount} 台（可於 Web 重新綁定）");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Sentinel 孤兒主機掃描失敗（不影響本次分析）：{0}", ex.Message);
            }

            long currentHostId = 0;
            try
            {
                currentHostId = hostStore.Touch(currentHost, DateTime.Now).HostId;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "登記主機回報時間失敗（不影響本次分析）：{0}", ex.Message);
                console.WriteLine($"  ⚠ 登記主機回報時間失敗（不影響分析）：{ex.Message}");
            }

            // 歷史 store 綁定「本機」識別：缺日判定與趨勢基準只看這台主機自己的紀錄。
            var ownerHost = new HostKey { HostId = currentHostId, HostName = currentHost };
            var historyService = StorageFactory.CreateRecordStore(settings.Storage, dataRoot, ownerHost);

            var reportService = new RiskReportService(aiService, reportSink, settings.Ai.DeepDiveMaxTokens);
            var analysisService = new LogAnalysisService(eventLogService, aiService, historyService, suppressionStore,
                settings.Analysis.ServerDescription, reportService, currentHost, currentHostId);
            // 風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）：每日分析完成後由呼叫端（下方主迴圈、
            // NetiqPipelineService）用 RiskyEventSelector 篩選並寫入，不改動 LogAnalysisService 本身
            var riskyEventStore = StorageFactory.CreateRiskyEventStore(settings.Storage, dataRoot);
            var permissionMonitor = new PermissionMonitorService(settings.Permissions,
                StorageFactory.CreatePermissionSnapshotStore(settings.Storage, dataRoot));
            var weeklyCheckupService = new WeeklyCheckupService(aiService, historyService, reportSink, suppressionStore);

            // 問題案件批次逐日掛接（docs/archive/FEEDBACK-4-PLAN.md §0.4-C）：Web 指派後建立的進行中案件，
            // 排程每天分析完新的一天就要把當日相符的問題掛進去（2.4）。
            var caseCoordinator = new IssueCaseCoordinator(
                StorageFactory.CreateIssueCaseStore(settings.Storage, dataRoot),
                StorageFactory.CreateIssueHandlingStore(settings.Storage, dataRoot),
                StorageFactory.CreateHandlingStore(settings.Storage, dataRoot),
                StorageFactory.CreateRecordQuery(settings.Storage, dataRoot),
                hostStore);

            // 0. 權限/角色異動檢查：與每日事件分析各自獨立，反映「本次執行當下」的權限狀態
            //    （不是某個歷史日期的事），所以每次執行都做一次、不受歷史回補流程影響。
            console.WriteLine($"\n檢查權限異動（監控 {permissionMonitor.WatchedFolders.Count} 個資料夾 + 本機 Administrators 群組）...");
            var permissionCheck = permissionMonitor.Check();
            if (permissionCheck.Alerts.Count > 0)
            {
                console.WriteLine("  ╔══════════════════════════════════════════════════╗");
                console.WriteLine($"  ║  🔑 偵測到 {permissionCheck.Alerts.Count} 項權限／角色異動，請立即確認是否為授權操作！");
                foreach (var alert in permissionCheck.Alerts)
                {
                    console.WriteLine($"  ║  - {alert}");
                }
                console.WriteLine("  ╚══════════════════════════════════════════════════╝");

                console.WriteLine("\n  被異動項目明細（請逐項人工確認是否為正常異動）：");
                for (int i = 0; i < permissionCheck.Details.Count; i++)
                {
                    var d = permissionCheck.Details[i];
                    console.WriteLine($"    {i + 1}. {d.Target}｜{d.ChangeType}");
                    console.WriteLine($"       異動前：{d.Before}");
                    console.WriteLine($"       異動後：{d.After}");
                }

                var reportSb = new System.Text.StringBuilder();
                reportSb.AppendLine("LogForesight 權限異動報告");
                reportSb.AppendLine($"檢查時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                reportSb.AppendLine();
                reportSb.AppendLine("■ 異動告警");
                foreach (var alert in permissionCheck.Alerts)
                {
                    reportSb.AppendLine($"- {alert}");
                }
                reportSb.AppendLine();
                reportSb.AppendLine("■ 被異動項目明細（請逐項人工確認是否為正常/授權的異動）");
                for (int i = 0; i < permissionCheck.Details.Count; i++)
                {
                    var d = permissionCheck.Details[i];
                    reportSb.AppendLine($"{i + 1}. 對象：{d.Target}");
                    reportSb.AppendLine($"   異動類型：{d.ChangeType}");
                    reportSb.AppendLine($"   異動前：{d.Before}");
                    reportSb.AppendLine($"   異動後：{d.After}");
                    reportSb.AppendLine("   └ 請確認：此異動是否為您或授權人員的操作？若否，可能為入侵或誤設定，建議立即調查。");
                    reportSb.AppendLine();
                }

                var permissionFileName = $"{DateTime.Today:yyyy-MM-dd}_權限異動.txt";
                var permissionReportPath = await reportSink.WriteAsync(ReportKind.Permission, host: "", permissionFileName, reportSb.ToString());
                console.WriteLine($"  📄 權限異動報告（含逐項明細）：{permissionReportPath}");

                // 雙軌寫入（docs/WEB-SPEC.md §2.1 Phase 3）：上面的 console 告警與 txt 報告是既有輸出、
                // 一字未改；這裡另外把每筆異動寫成結構化紀錄，供 Web 的「權限異動待辦」逐筆確認。
                try
                {
                    var permissionChangeStore = StorageFactory.CreatePermissionChangeStore(settings.Storage, dataRoot);
                    var detectedAt = DateTime.Now;

                    permissionChangeStore.AppendChanges(permissionCheck.Details.Select((detail, index) => new PermissionChangeRecord
                    {
                        ChangeId = Guid.NewGuid().ToString("N"),
                        HostName = currentHost,
                        DetectedAt = detectedAt,
                        Target = detail.Target,
                        ChangeType = detail.ChangeType,
                        Before = detail.Before,
                        After = detail.After,
                        AlertText = index < permissionCheck.Alerts.Count ? permissionCheck.Alerts[index] : string.Empty
                    }));

                    console.WriteLine($"  ✓ 已寫入 {permissionCheck.Details.Count} 筆權限異動供 Web 逐筆確認");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "權限異動的結構化寫入失敗（不影響本次分析與既有報告）：{0}", ex.Message);
                    console.WriteLine($"  ⚠ 權限異動的結構化寫入失敗（既有報告不受影響）：{ex.Message}");
                }
            }
            else
            {
                console.WriteLine("  未偵測到權限異動。");
            }

            // 1. 清理超過保留天數的歷史紀錄，避免資料庫無限增長
            var pruned = historyService.Prune(retention.RetentionDays);
            if (pruned > 0)
            {
                console.WriteLine($"已清除 {pruned} 筆超過 {retention.RetentionDays} 天的歷史紀錄。");
            }

            // 1b. 清理執行歷程／匯入紀錄／稽核紀錄（docs/archive/HISTORY.md P0-3）
            try
            {
                var runLogPruned = (batchRunStore?.Prune(retention.RunLogRetentionDays) ?? 0) +
                                    StorageFactory.CreateImportLogStore(settings.Storage, dataRoot).Prune(retention.RunLogRetentionDays);
                if (runLogPruned > 0)
                    console.WriteLine($"已清除 {runLogPruned} 筆超過 {retention.RunLogRetentionDays} 天的執行歷程／匯入紀錄。");

                var auditPruned = StorageFactory.CreateAuditLogStore(settings.Storage, dataRoot).Prune(retention.AuditRetentionDays);
                if (auditPruned > 0)
                    console.WriteLine($"已清除 {auditPruned} 筆超過 {retention.AuditRetentionDays} 天的稽核紀錄。");

                // 風險 log 暫存清理（docs/archive/WEB-SCHEDULER-PLAN.md §2.2.3）
                var riskyEventPruned = riskyEventStore.Prune(retention.RiskyEventRetentionDays);
                if (riskyEventPruned > 0)
                    console.WriteLine($"已清除 {riskyEventPruned} 筆超過 {retention.RiskyEventRetentionDays} 天的風險 log 暫存。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "執行歷程／匯入／稽核紀錄／風險 log 暫存清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            // 1c. 清理過期的風險報告檔（docs/archive/HISTORY.md P1-4）
            try
            {
                var reportsPruned = ExportReportPruner.Prune(Path.Combine(dataRoot, "export"), retention.RetentionDays);
                if (reportsPruned > 0)
                    console.WriteLine($"已清除 {reportsPruned} 份超過 {retention.RetentionDays} 天的風險報告檔。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "風險報告檔清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            var yesterday = DateTime.Today.AddDays(-1);

            // 2~4. 本機逐日分析：NetiqHosts 範圍（Phase 3 手動觸發指定 NetIQ 主機）不動本機資料
            if (request.Scope != RunScope.NetiqHosts)
            {
                await RunLocalAnalysisAsync(
                    request, settings, retention, console, ct, analysisService, historyService, eventLogService,
                    caseCoordinator, riskyEventStore, runRecorder, currentHost, currentHostId, yesterday, result, useAi,
                    progress);
            }

            // 5b. NetIQ 機房分析（docs/archive/HISTORY.md 決策 B2、§4；Phase 4）：本機分析完成後，
            //    對 Web 主機頁登錄的 NetIQ 主機逐一向 Sentinel 取事件、映射後餵進同一套
            //    LogAnalysisService。LocalOnly 範圍（Phase 3 手動觸發只跑本機）跳過這段。
            if (request.Scope != RunScope.LocalOnly)
            {
                ct.ThrowIfCancellationRequested();
                await RunNetiqAnalysisAsync(
                    request, settings, dataRoot, console, ct, hostStore, sentinelStore, eventLogService, aiService,
                    suppressionStore, reportService, runRecorder, caseCoordinator, riskyEventStore, retention, result, useAi,
                    progress);
            }

            // 6. 體檢：週期性回顧（獨立於每日分析），距上次體檢達 CheckupIntervalDays 天（含補跑）就執行
            if (weeklyCheckupService.ShouldRun(DateTime.Today, settings.Analysis.CheckupIntervalDays))
            {
                console.WriteLine($"\n執行體檢（週期性回顧，以 {yesterday:yyyy-MM-dd} 為基準）...");
                var checkupStopwatch = Stopwatch.StartNew();
                var checkup = await weeklyCheckupService.RunAsync(yesterday, settings.Analysis.CheckupIntervalDays, settings.Analysis.ServerDescription, useAi: useAi);

                if (!checkup.Completed)
                {
                    console.WriteLine($"  ⚠ 體檢未完成（{checkup.Conclusion}），未寫入歷史，下次執行將自動重試。");
                }
                else
                {
                    historyService.AttachWeeklyCheckup(yesterday, checkup);

                    if (checkup.HasFindings)
                    {
                        console.WriteLine($"  📋 體檢有發現：{checkup.Conclusion}");
                        if (checkup.ReportFile != null)
                        {
                            console.WriteLine($"  📄 體檢報告：{checkup.ReportFile}");
                        }
                    }
                    else
                    {
                        console.WriteLine($"  體檢完成，無累積性異常。（{checkup.Conclusion}）");
                    }
                }
                console.WriteLine($"  ⏱ 體檢耗時：{FormatElapsed(checkupStopwatch.Elapsed)}");
                Log.Info("體檢：基準日={Date:yyyy-MM-dd}, 完成={Completed}, 有發現={HasFindings}, 耗時={ElapsedMs}ms",
                    yesterday, checkup.Completed, checkup.HasFindings, checkupStopwatch.ElapsedMilliseconds);
            }

            console.WriteLine($"\n歷史資料庫：{historyService.Location}");
            console.WriteLine($"總執行時間：{FormatElapsed(runStopwatch.Elapsed)}");
            console.WriteLine("--- 執行結束 ---");
            Log.Info("===== 執行結束，總耗時 {ElapsedMs}ms =====", runStopwatch.ElapsedMilliseconds);
            runRecorder.Milestone("執行結束");
            runRecorder.Finish(exitCode: 0);

            result.Elapsed = runStopwatch.Elapsed;
            return result;
        }
        catch (OperationCanceledException)
        {
            // 使用者手動停止（Phase 3）：停在主機日邊界，不是失敗——BatchRun 由呼叫端依情境回填
            console.WriteLine($"\n執行已停止（使用者取消）。");
            console.WriteLine($"總執行時間：{FormatElapsed(runStopwatch.Elapsed)}");
            Log.Info("執行已取消，總耗時 {ElapsedMs}ms", runStopwatch.ElapsedMilliseconds);
            result.Success = false;
            result.FailureMessage = "使用者取消";
            result.Elapsed = runStopwatch.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            // 全域防護：任何未預期的錯誤都要留下訊息並回報失敗，讓呼叫端（Web 的 BatchRun
            // 回填與排程狀態卡的「上次執行結果」）能監控到。
            console.WriteLine($"\n執行失敗：{ex}");
            console.WriteLine($"總執行時間：{FormatElapsed(runStopwatch.Elapsed)}");
            Log.Fatal(ex, "執行失敗，總耗時 {ElapsedMs}ms", runStopwatch.ElapsedMilliseconds);
            result.Success = false;
            result.FailureMessage = ex.Message;
            result.Elapsed = runStopwatch.Elapsed;
            return result;
        }
    }

    private async Task RunLocalAnalysisAsync(
        RunRequest request, AppSettings settings, RetentionOptions retention, IRunConsole console, CancellationToken ct,
        LogAnalysisService analysisService, IAnalysisRecordStore historyService, EventLogService eventLogService,
        IssueCaseCoordinator caseCoordinator, IRiskyEventStore riskyEventStore, BatchRunRecorder runRecorder,
        string currentHost, long currentHostId, DateTime yesterday, OrchestratorResult result, bool useAi,
        IRunProgress? progress)
    {
        // 找出缺漏的日子。首次執行（本機歷史資料庫全空）回補 InitialHistoryDays 天，讓趨勢分析
        // 一開始就有更充足的基準資料；已有任何本機紀錄時只看趨勢窗口 TrendWindowDays 天。
        var lookbackDays = historyService.HasAnyRecord() ? TrendWindowDays : retention.InitialHistoryDays;
        var missingDates = Enumerable.Range(1, lookbackDays)
            .Select(offset => DateTime.Today.AddDays(-offset))
            .Where(date => !historyService.HasRecord(date))
            .OrderBy(date => date)
            .ToList();

        if (missingDates.Count == 0)
        {
            // 白天手動執行時這是最常見的路徑（昨晚排程已分析過昨天）：措辭刻意講清楚
            // 「本機無需回補、不是本機未執行」（docs/archive/FEEDBACK-8-PLAN.md #3），
            // 避免與「本機根本沒被排進這次執行」混淆。Milestone 落地——NLog target 只收
            // Warn 以上，不記 Milestone 的話這句話不會出現在執行詳情，使用者事後查不到
            var skipMessage = $"本機近 {lookbackDays} 天皆已有分析紀錄，本次無需回補（非未執行；" +
                              $"最新至 {yesterday:yyyy-MM-dd}，同一天重複執行不會產生重複資料）。";
            console.WriteLine($"\n{skipMessage}");
            runRecorder.Milestone(skipMessage);
            Log.Info("{Date:yyyy-MM-dd} 已有分析紀錄，本次跳過", yesterday);
            return;
        }

        // 一次倒序掃描取回整個缺漏區間的事件，三個日誌來源平行掃描，並回傳資料完整性中繼資料。
        var rangeStart = missingDates[0];

        var channelNames = settings.Analysis.Channels.Count > 0
            ? settings.Analysis.Channels.Select(c => ChannelCatalog.Resolve(c).ChannelName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : ChannelCatalog.DefaultChannelNames;

        // 啟動誠實申報：Operational 頻道已啟用、但規則表沒有對應規則時，其 Information 等級事件不會被
        // 收集（掃了卻沒偵測）。
        foreach (var name in channelNames)
        {
            var policy = ChannelCatalog.Resolve(name);
            if (policy.Kind == ChannelInclusionKind.OperationalWatchlist && !KnownIssueCatalog.HasWatchlist(policy.ProviderProbe))
            {
                console.WriteLine($"  ⚠ 頻道「{name}」已啟用，但目前規則表沒有對應規則，其 Information 等級事件不會被收集。" +
                                  "請於「規則維護」頁匯入內建 Defender/RDP 規則。");
                Log.Warn("頻道 {Channel} 已啟用但無對應規則（watchlist 空），Information 事件未收集", name);
            }
        }

        console.WriteLine($"\n平行掃描頻道：{string.Join("、", channelNames)}，取得 {rangeStart:yyyy-MM-dd} ~ {yesterday:yyyy-MM-dd} 的事件...");
        var scanResult = await eventLogService.ScanRangeFromAllAsync(rangeStart, DateTime.Today, channelNames);
        var logsByDate = scanResult.Entries
            .GroupBy(l => l.TimeGenerated.Date)
            .ToDictionary(g => g.Key, g => g.ToList());
        console.WriteLine($"共取得 {scanResult.Entries.Count} 筆事件。");

        if (scanResult.SecurityAvailable == false)
        {
            console.WriteLine("  ⚠ Security log 本次無法讀取（需系統管理員權限），入侵跡象相關偵測將標記為未檢查。");
        }

        var missingChannels = scanResult.ChannelsMissing.Where(c => !c.Equals("Security", StringComparison.OrdinalIgnoreCase)).ToList();
        if (missingChannels.Count > 0)
        {
            console.WriteLine($"  · 下列頻道在本機不存在（未安裝對應角色，相關偵測不適用）：{string.Join("、", missingChannels)}");
        }
        var deniedChannels = scanResult.ChannelsDenied.Where(c => !c.Equals("Security", StringComparison.OrdinalIgnoreCase)).ToList();
        if (deniedChannels.Count > 0)
        {
            console.WriteLine($"  ⚠ 下列頻道存取被拒（需系統管理員權限，屬偵測盲區）：{string.Join("、", deniedChannels)}");
        }

        var channelAvailability = new ChannelAvailability
        {
            Read = scanResult.ChannelsRead,
            Denied = scanResult.ChannelsDenied,
            Missing = scanResult.ChannelsMissing
        };

        if (missingDates.Count > 1)
        {
            var aiClause = useAi ? "每天皆完整 AI 分析，" : "統計模式（AI 未設定），";
            console.WriteLine($"偵測到歷史資料有缺漏，回補 {missingDates.Count} 天（{aiClause}由最舊到最新，後面的日期能參照前面累積的歷史）。");
            console.WriteLine("（能回補多久取決於 Event Log 的保留量，太舊的事件可能已被覆蓋）");
        }

        // 逐日分析：趨勢比對依賴前面日期寫入的歷史，因此分析本身必須依序執行。
        var elapsedByDate = new Dictionary<DateTime, TimeSpan>();
        progress?.Report("local", 0, missingDates.Count);
        var localDone = 0;
        foreach (var date in missingDates)
        {
            // 取消語意＝停在「主機日」邊界：當前這一天分析完才停，不硬掐 AI 呼叫本身
            ct.ThrowIfCancellationRequested();

            console.WriteLine($"\n[{date:yyyy-MM-dd}] 分析中（{(useAi ? "含 AI 判讀" : "統計模式，AI 未設定")}）...");
            var dayStopwatch = Stopwatch.StartNew();

            var logs = logsByDate.TryGetValue(date, out var dayLogs) ? dayLogs : new List<EventLogEntryData>();
            var dataIncomplete = scanResult.IsDateIncomplete(date);
            var record = await analysisService.AnalyzeDayAsync(date, logs, useAi: useAi, historyDays: TrendWindowDays,
                dataIncomplete: dataIncomplete, securityLogAvailable: scanResult.SecurityAvailable, channels: channelAvailability);
            result.LocalResults.Add(record);

            // 問題案件批次逐日掛接（2.4）：失敗只記警告，不擋分析主流程
            try
            {
                var attach = caseCoordinator.AttachNewDay(currentHost, date, record.TopIssues, DateTime.Now);
                if (attach.AttachedCount > 0)
                    Log.Info("案件掛接：{Date:yyyy-MM-dd} 掛入 {Count} 個問題", date, attach.AttachedCount);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "案件掛接失敗（不影響分析結果，下次執行冪等補掛）：{0}", ex.Message);
            }

            // 風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）：同一失敗邊界哲學。
            if (RiskyEventSelector.WithinRetention(date, retention.RiskyEventRetentionDays, DateTime.Today))
            {
                try
                {
                    var riskyEvents = RiskyEventSelector.Select(record.TopIssues, logs, currentHostId, date);
                    riskyEventStore.ReplaceDay(currentHostId, date, riskyEvents);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "風險 log 暫存寫入失敗（不影響分析結果）：{0}", ex.Message);
                }
            }

            dayStopwatch.Stop();
            elapsedByDate[date] = dayStopwatch.Elapsed;
            runRecorder.RecordDayAnalyzed();

            // AiAnalyzed=false 有兩種意義：低風險日「刻意不呼叫」（正常）與呼叫失敗的降級（異常）。
            // useAi=false（AI 未設定）時這天本來就不會呼叫 AI，不該被記成「AI 呼叫失敗」。
            if (useAi && (record.AiAnalyzed || record.RiskLevel != RiskLevels.Low))
            {
                runRecorder.RecordAiCall(record.AiAnalyzed);
            }
            PrintResult(console, record, verbose: date == yesterday);
            console.WriteLine($"  ⏱ 本日耗時：{FormatElapsed(dayStopwatch.Elapsed)}");
            progress?.Report("local", ++localDone, missingDates.Count);
        }

        runRecorder.Milestone($"逐日分析完成：{result.LocalResults.Count} 天");

        // 執行結果總表：讓使用者一眼看到「哪幾天有問題、該打開哪個報告檔、花了多久」
        console.WriteLine("\n══════════ 本次執行結果 ══════════");
        foreach (var r in result.LocalResults)
        {
            console.WriteLine($"  {r.Date:yyyy-MM-dd}  風險【{r.RiskLevel}】  耗時 {FormatElapsed(elapsedByDate[r.Date])}" +
                              (r.ReportFile != null ? $"  → {r.ReportFile}" : ""));
        }

        var riskyCount = result.LocalResults.Count(r => r.ReportFile != null);
        if (riskyCount > 0)
        {
            console.WriteLine(
                $"\n  需要關注：{riskyCount} 天判定有風險，問題說明、AI 深入分析與原始 log 已輸出至上列報告檔。");
        }
        else
        {
            console.WriteLine("\n  所有日期風險等級為低，無需特別處置。");
        }

        Log.Info("本次執行結果：{Results}", string.Join(" | ", result.LocalResults.Select(r => $"{r.Date:MM-dd}={r.RiskLevel}")));
    }

    private async Task RunNetiqAnalysisAsync(
        RunRequest request, AppSettings settings, string dataRoot, IRunConsole console, CancellationToken ct,
        IHostStore hostStore, ISentinelStore sentinelStore, EventLogService eventLogService, AIService aiService,
        ISuppressionStore suppressionStore, RiskReportService reportService, BatchRunRecorder runRecorder,
        IssueCaseCoordinator caseCoordinator, IRiskyEventStore riskyEventStore, RetentionOptions retention,
        OrchestratorResult result, bool useAi, IRunProgress? progress)
    {
        var netiqHostList = HostListSelection.FromStore(hostStore, sentinelStore);

        // NetiqHosts 範圍（Phase 3 手動觸發指定主機）：篩到只剩請求的 HostId，其餘略過不查、不警告
        // ——那些主機本來就沒被要求這次更新，不是「被排除」。
        if (request.Scope == RunScope.NetiqHosts && request.HostIds is { Count: > 0 } wantedIds)
        {
            var wanted = wantedIds.ToHashSet();
            var filtered = new HostListResult();
            foreach (var (server, targets) in netiqHostList.ByServer)
            {
                var matched = targets.Where(t => wanted.Contains(t.HostId)).ToList();
                if (matched.Count > 0) filtered.ByServer[server] = matched;
            }
            netiqHostList = filtered;
        }

        if (netiqHostList.Warnings.Count > 0)
        {
            console.WriteLine($"\n  ⚠ NetIQ 主機清單有 {netiqHostList.Warnings.Count} 項需要注意：");
            foreach (var warning in netiqHostList.Warnings) console.WriteLine($"    - {warning}");
        }
        if (netiqHostList.TotalHosts == 0) return;

        try
        {
            var netiqOptions = StorageFactory.CreateNetiqOptionsStore(settings.Storage, dataRoot).Get();
            // 手動觸發的一次性回補天數覆寫（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：只影響這次執行，
            // 不落地——netiqOptions 是本次呼叫剛從 store 讀出的獨立物件，就地覆寫不影響下次讀取的設定值
            if (request.BackfillOverride is { } backfillOverride)
            {
                netiqOptions.BackfillDays = backfillOverride;
            }
            var netiqPipeline = new NetiqPipelineService(
                settings.Storage, dataRoot, netiqOptions, sentinelStore, hostStore,
                eventLogService, aiService, suppressionStore, reportService, runRecorder, caseCoordinator, console,
                riskyEventStore, retention.RiskyEventRetentionDays, useAi, progress);

            var netiqResult = await netiqPipeline.RunAsync(netiqHostList, TrendWindowDays, ct);
            result.NetiqResult = netiqResult;
            runRecorder.Milestone($"NetIQ 機房分析完成：已完成跳過 {netiqResult.HostsSkippedUpToDate}、" +
                $"本次分析 {netiqResult.HostDaysAnalyzed} 個主機日、失敗 {netiqResult.HostsFailed} 個主機日");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 與本機分析的失敗邊界一致：NetIQ 這段出問題不該讓已經完成的本機分析與寫入作廢，
            // 只記錄失敗、留給下次執行的缺漏日回補機制自動重試
            Log.Error(ex, "NetIQ 機房分析失敗，本機分析結果不受影響");
            console.WriteLine($"\n  ✗ NetIQ 機房分析失敗：{ex.Message}（本機分析結果不受影響，下次執行自動重試缺漏日）");
        }
    }

    private static string FormatElapsed(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小時 {span.Minutes} 分 {span.Seconds} 秒"
        : span.TotalMinutes >= 1 ? $"{span.Minutes} 分 {span.Seconds} 秒"
        : $"{span.Seconds} 秒";

    /// <summary>風險等級的行動語意對照，讓輸出不用另外解讀「中」「高」代表要做什麼（2026-07-20 AI 角色轉換）</summary>
    private static string RiskActionZh(string riskLevel) => riskLevel switch
    {
        "高" => "需要立即處理",
        "中" => "本週內確認",
        "低" => "無需動作",
        _ => "狀態未知"
    };

    private static void PrintResult(IRunConsole console, DailyAnalysisRecord record, bool verbose = false)
    {
        console.WriteLine($"  錯誤 {record.ErrorCount} 筆、警告 {record.WarningCount} 筆、稽核事件 {record.AuditEventCount} 筆，" +
                          $"風險等級：{record.RiskLevel}（{RiskActionZh(record.RiskLevel)}）");
        if (record.Headline.Length > 0)
        {
            console.WriteLine($"  {record.Headline}");
        }

        if (record.DataIncomplete)
        {
            console.WriteLine("  ⚠ 本日部分事件來源保留歷史不足以涵蓋整天，統計數字可能偏低（非真實反映當日狀況）。");
        }

        // Security 無權限時逐條列出因此停用的偵測項目——覆蓋率誠實申報，不是一句「讀取失敗」帶過
        if (record.UncoveredChecks.Count > 0)
        {
            console.WriteLine("  ⚠ 本次未能檢查的項目（權限或來源限制，非「已檢查且無異常」）：");
            foreach (var check in record.UncoveredChecks)
            {
                console.WriteLine($"    - {check}");
            }
        }

        // 主機級抑制（見 docs/RULES-SPEC.md）：本日有告警被抑制時列出摘要，讓使用者知道「有東西被關掉了」
        var suppressedIssues = record.TopIssues.Where(i => i.Suppressed).ToList();
        if (suppressedIssues.Count > 0)
        {
            var summary = string.Join("、", suppressedIssues.Select(i => $"{i.RuleId} x{i.Count}"));
            console.WriteLine($"  🔕 本日 {suppressedIssues.Count} 條告警已抑制（{summary}）");
        }

        // 高風險或命中「重大」旗標規則時，用醒目的紅色橫幅提醒使用者（被抑制的問題不佔用這個橫幅）。
        var criticalIssues = record.TopIssues.Where(i => i.ElevatesDayRisk && !i.Suppressed).ToList();
        if (record.RiskLevel == RiskLevels.High || criticalIssues.Count > 0)
        {
            console.WriteLine();
            console.WriteLine("  ╔══════════════════════════════════════════════════╗");
            console.WriteLine($"  ║  ⚠ 警告：{record.Date:yyyy-MM-dd} 偵測到需要立即關注的問題！");
            if (record.Headline.Length > 0)
            {
                console.WriteLine($"  ║  {record.Headline}");
            }
            foreach (var issue in criticalIssues)
            {
                console.WriteLine($"  ║  [{issue.Category}] {issue.Source} EventId {issue.EventId} x{issue.Count}");
                console.WriteLine($"  ║    → {issue.KnownIssue}");
            }
            console.WriteLine("  ╚══════════════════════════════════════════════════╝");
        }

        // 跨 log 關聯訊號：已知攻擊鏈/故障鏈組合，最重要的線索，紅色醒目顯示
        if (record.CorrelationAlerts.Count > 0)
        {
            console.WriteLine($"\n  🔗 關聯訊號（程式比對出的攻擊鏈/故障鏈組合）：");
            foreach (var alert in record.CorrelationAlerts)
            {
                console.WriteLine($"    - {alert}");
            }
        }

        // 程式比對歷史後發現的頻率異常（首次出現、頻率上升、總量突增），用黃色提醒
        if (record.TrendAlerts.Count > 0)
        {
            console.WriteLine($"\n  ⚠ 頻率異常／慢速惡化（與近期歷史比對）：");
            foreach (var alert in record.TrendAlerts)
            {
                console.WriteLine($"    - {alert}");
            }
        }

        if (record.AiAnalyzed && (verbose || record.RiskLevel == RiskLevels.High || criticalIssues.Count > 0 || record.TrendAlerts.Count > 0))
        {
            console.WriteLine($"\n  白話說明：{record.Summary}");
            if (record.TrendAssessment.Length > 0)
            {
                console.WriteLine($"  趨勢：{record.TrendAssessment}");
            }
            if (record.Action.Length > 0)
            {
                console.WriteLine($"  現在該做：{record.Action}");
            }
        }
        else if (!record.AiAnalyzed && verbose)
        {
            console.WriteLine($"  {record.Summary}");
        }

        // 有輸出風險報告時明確指引檔案位置，讓使用者知道去哪看細節
        if (record.ReportFile != null)
        {
            console.WriteLine(
                $"\n  📄 詳細風險報告（含 AI 深入分析與原始 log）：{record.ReportFile}");
        }
    }
}

/// <summary>
/// 保留天數的一次性快照（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.2「每次執行重建服務」）：
/// 由呼叫端在讀取 <c>SystemSettings</c> 後組出，<see cref="AnalysisOrchestrator"/> 本身
/// 不碰設定來源（DB 或 appsettings 預設值），維持「設定解析在呼叫端、執行邏輯在這裡」的分工。
/// </summary>
public record RetentionOptions
{
    public int InitialHistoryDays { get; init; } = 120;
    public int RetentionDays { get; init; } = 120;
    public int RunLogRetentionDays { get; init; } = 90;
    public int AuditRetentionDays { get; init; } = 730;
    public int RiskyEventRetentionDays { get; init; } = 14;
}
