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
    /// 是否分析本機主機（回饋十八輪批次D，↔ ScheduleOptions.LocalAnalysisEnabled）：預設 true。
    /// 停用時本機逐日分析整段跳過，只跑 NetIQ 機房分析——與 <see cref="RunScope.NetiqHosts"/>
    /// 語意上的差異是：Scope 決定「這次執行的範圍設計」（例如指定主機更新只碰 NetIQ 一台），
    /// 這個欄位是「本機分析這個功能本身開不開」的獨立總開關，兩者交集才是最終要不要跑本機
    /// （見下方 RunAsync 對 localTask 的判斷）。
    /// </summary>
    public bool IncludeLocal { get; init; } = true;

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
/// RunLocalAnalysisAsync／RunNetiqAnalysisAsync 共用的參數集合（12 個原本各自逐一傳遞，
/// 兩個方法因此暴增到 16/17 個參數）。集中成一個 record 一次建構、貫穿本次執行，
/// 各方法只在額外需要自己專屬的依賴（如 analysisService／backend）時另外收參數。
/// </summary>
internal sealed record AnalysisRunContext(
    RunRequest Request, AppSettings Settings, RetentionOptions Retention, IRunConsole Console,
    CancellationToken Ct, EventLogService EventLogService, IssueCaseCoordinator CaseCoordinator,
    IRiskyEventStore RiskyEventStore, BatchRunRecorder RunRecorder, OrchestratorResult Result,
    bool UseAi, IRunProgress? Progress);

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

    /// <summary>
    /// 夜間分析自己那個連線池的上限（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3；回饋十三輪 D 擴充公式；
    /// 回饋十七輪批次E 再 +1）。
    ///
    /// 分析主線本身是**依序**的（同一台主機的趨勢比對需要前一天已寫入的歷史），真正的併發
    /// 來源是兩個正交維度：跨 Sentinel 平行（<see cref="NetiqOptions.MaxParallelServers"/>，
    /// 上限 <see cref="NetiqOptions.MaxParallelServersLimit"/>）與單一 Sentinel 內部的批次平行
    /// （<see cref="NetiqOptions.MaxParallelQueriesPerServer"/>，上限
    /// <see cref="NetiqOptions.MaxParallelQueriesPerServerLimit"/>，回饋十三輪 D）。兩者同時吃滿
    /// 上限時，同時活躍的「統計段寫入」數就是兩個上限的乘積——連線池必須至少這麼大，否則
    /// 池配額本身會變成新的隱性瓶頸；原本再 +1 是給零星的非分析寫入（如 BatchRunRecorder 里程碑）
    /// 一點餘裕。這兩個常數都是編譯期 const，此處運算式本身也維持編譯期常數，不必等執行期
    /// 讀到 NetiqOptions 才能決定池大小（建立 StorageBackend 當下還沒有 blob 可讀，見下方呼叫式）。
    ///
    /// **批次E 再 +1**：本機與 NetIQ 機房分析改並行執行後，本機的逐日分析迴圈（一次一條連線，
    /// 用完即還）現在會與 NetIQ 的峰值並行度同時競爭同一個池——原本的 +1 只打算覆蓋「零星的
    /// 非分析寫入」，沒有把「本機整條分析迴圈也在同時跑」算進去。連線池滿載時新請求只會排隊
    /// 等待（ADO.NET pool 的既有行為），不會逾時報錯或死結（本機迴圈不會在持有一條連線的同時
    /// 又去搶同一個池的第二條），但排隊等待會侵蝕批次E想省下的等待時間，多留一點餘裕。
    /// 不開放設定：這是「不要拖垮站台」的安全上限，不是效能旋鈕——真的需要更快，
    /// 該做的是拆獨立 worker（本輪已評估後決定不拆，見規劃 §8.4）。
    /// </summary>
    private const int AnalysisMaxPoolSize =
        NetiqOptions.MaxParallelServersLimit * NetiqOptions.MaxParallelQueriesPerServerLimit + 2;

    public async Task<OrchestratorResult> RunAsync(
        RunRequest request, AppSettings settings, string dataRoot,
        RetentionOptions retention, IRunConsole console, CancellationToken ct, IRunProgress? progress = null)
    {
        var runStopwatch = Stopwatch.StartNew();
        var result = new OrchestratorResult();

        try
        {
            // 本次執行共用同一個 StorageBackend（DbContext 工廠與 schema 確認只做一次）。
            // 連線池刻意與站台分開（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）：分析與站台跑在
            // 同一個行程，分析一跑就是數小時，共用池的話前景請求會排隊等連線——
            // 使用者看到的是「整站變慢」，而不是「分析變慢」。
            var backend = new StorageBackend(settings.Storage, dataRoot, AnalysisMaxPoolSize);

            var ruleStore = new KnownIssueRuleStore(backend.Blob("rules"));
            RuleBootstrapper.Run(ruleStore);

            // 同步內建規則的原廠種子鏡像（docs/WEB-SPEC.md §2.1 Phase 4）：Web 的「回復預設」需要
            // 一份使用者碰不到的原始內容才比較得出差異。放在 RuleBootstrapper 之後——那時規則庫已就緒。
            try
            {
                new RuleSeedStore(backend.Blob("rule_seeds"))
                    .Sync(KnownIssueSeed.CreateRules(), KnownIssueSeed.Version);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "規則種子鏡像同步失敗（不影響本次分析）：{0}", ex.Message);
            }

            var suppressionStore = new SuppressionStore(backend.Blob("suppressions"));
            var currentHost = Environment.MachineName;
            // 到期抑制的通知移到 hostStore.Touch 之後才印（回饋十三輪 F）：Group／Site 範圍的抑制
            // 判定需要知道本機的群組成員資格，那個資訊要等主機登記完成才拿得到，見下方。

            // 執行紀錄（docs/WEB-SPEC.md §2.1 Phase 4）：啟動時登記、結束時回填，讓 Web 的執行監控頁
            // 能回答「昨晚每台主機都跑了嗎、有沒有出問題」。掛上 NLog target 後 Warn 以上自動流入，
            // 不需要在既有程式碼各處加呼叫。建立失敗不影響分析（見 BatchRunRecorder）。
            BatchRunStore? batchRunStore = null;
            try
            {
                batchRunStore = new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
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
            var hostStore = new HostStore(backend.Blob("hosts"));
            var sentinelStore = new SentinelStore(backend.Blob("sentinels"));

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
            List<long> currentHostGroupIds = new();
            try
            {
                var touched = hostStore.Touch(currentHost, DateTime.Now);
                currentHostId = touched.HostId;
                currentHostGroupIds = touched.GroupIds;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "登記主機回報時間失敗（不影響本次分析）：{0}", ex.Message);
                console.WriteLine($"  ⚠ 登記主機回報時間失敗（不影響分析）：{ex.Message}");
            }

            // 到期抑制通知（回饋十三輪 F 移到這裡）：Group／Site 範圍的判定需要本機的群組成員資格，
            // 上面 Touch 完成後才拿得到（新主機或註冊失敗時 currentHostGroupIds 為空——
            // 只影響「還屬於哪些群組」，Host／Site 範圍的到期判定不受影響）。
            var allSuppressions = suppressionStore.LoadAll();
            var expiredSuppressions = SuppressionFilter.ExpiredForHost(allSuppressions, currentHost, currentHostGroupIds, DateTime.Now);
            if (expiredSuppressions.Count > 0)
            {
                // 同目標跨範圍並存時的語意修正（回饋十四輪 C1，回饋十五輪體檢批G 泛型化到抑制四型，
                // 見 SuppressionFilter.StillSuppressedElsewhere 的文件）：只看到期的那一筆會誤導
                // 使用者以為解除它就能恢復告警。
                var stillSuppressedTargets = SuppressionFilter.StillSuppressedElsewhere(
                    allSuppressions, currentHost, currentHostGroupIds, DateTime.Now);

                foreach (var expired in expiredSuppressions)
                {
                    // 非 Rule 型的 RuleId 恆為空字串（回饋十五輪體檢批G 修正）：顯示身分改用
                    // TargetLabel（建立時擷取的人話標籤，見 RuleSuppression.TargetLabel 文件），
                    // 否則這行訊息對 Signature/Correlation/Volume 三型會印出空白，看不出是哪個目標到期。
                    var label = expired.TargetType == SuppressionTargetTypes.Rule
                        ? expired.RuleId
                        : expired.TargetLabel ?? expired.TargetType;

                    console.WriteLine($"  ℹ 抑制已到期，恢復告警：{label}（原訂於 {expired.ExpiresAt:yyyy-MM-dd} 到期，" +
                                      $"原因：{expired.Reason}；未自動清理，可於「規則維護」頁的「告警抑制」分頁解除）" +
                                      (stillSuppressedTargets.Contains(SuppressionFilter.TargetIdentity(expired))
                                          ? "（此設定仍受其他範圍的抑制生效中，解除這筆不會恢復告警）"
                                          : ""));
                }
            }

            // 歷史 store 綁定「本機」識別：缺日判定與趨勢基準只看這台主機自己的紀錄。
            var ownerHost = new HostKey { HostId = currentHostId, HostName = currentHost };
            var historyService = backend.RecordStore(ownerHost);

            var reportService = new RiskReportService(aiService, reportSink, settings.Ai.DeepDiveMaxTokens);
            var analysisService = new LogAnalysisService(eventLogService, aiService, historyService, suppressionStore,
                settings.Analysis.ServerDescription, reportService, currentHost, currentHostId,
                hostGroupIds: currentHostGroupIds);
            // 風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）：每日分析完成後由呼叫端（下方主迴圈、
            // NetiqPipelineService）用 RiskyEventSelector 篩選並寫入，不改動 LogAnalysisService 本身
            var riskyEventStore = backend.RiskyEventStore();
            var permissionMonitor = new PermissionMonitorService(settings.Permissions,
                new PermissionSnapshotStore(backend.Blob("permission_snapshot")));
            var weeklyCheckupService = new WeeklyCheckupService(aiService, historyService, reportSink, suppressionStore, currentHostGroupIds);

            // 問題案件批次逐日掛接（docs/archive/FEEDBACK-4-PLAN.md §0.4-C）：Web 指派後建立的進行中案件，
            // 排程每天分析完新的一天就要把當日相符的問題掛進去（2.4）。
            // 走與 Web 端**同一組真表 store**（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3）：
            // 這三份自 blob 改真表之後，批次端若還構造舊的 blob store，就會變成
            // 「批次寫 blob、Web 讀資料表」——兩邊各看到一半的處理狀態，而且不會報錯。
            var caseCoordinator = new IssueCaseCoordinator(
                backend.IssueCaseStore(),
                backend.IssueHandlingStore(),
                backend.RecordHandlingStore(),
                backend.RecordStore(),
                hostStore,
                new IssueOwnerStore(backend.Blob("issue_owners")));

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
                    var permissionChangeStore = new PermissionChangeStore(backend.LogStore("perm_changes"), backend.Blob("perm_confirms"));
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

            // 1a. 保留期現況申報（只計數不清除）。
            //
            // **刻意用未限縮的 RecordStore()**：上面那行 historyService 綁定本機識別，
            // 只看得到這台機器自己的紀錄，而 NetIQ 機房數千台主機的紀錄不屬於本機——
            // 用它計數會回報接近 0，真實情況完全看不見。
            try
            {
                var allHostRecords = backend.RecordStore();
                var prunableRecords = allHostRecords.CountPrunableRecords(retention.RetentionDays);
                var prunableDetails = allHostRecords.CountPrunableDetails(retention.DetailRetentionDays);

                if (prunableRecords > 0 || prunableDetails > 0)
                {
                    var msg = $"目前尚未啟用自動清除，僅申報：若現在執行，將清掉 {prunableRecords} 列過期紀錄、{prunableDetails} 列過期詳情。";
                    console.WriteLine($"  ℹ {msg}");
                    runRecorder.Milestone(msg);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "保留期現況申報失敗（不影響本次分析）：{0}", ex.Message);
            }

            // 1b. 清理執行歷程／匯入紀錄／稽核紀錄（docs/archive/HISTORY.md P0-3）
            try
            {
                var runLogPruned = (batchRunStore?.Prune(retention.RunLogRetentionDays) ?? 0) +
                                    new ImportLogStore(backend.LogStore("import_logs")).Prune(retention.RunLogRetentionDays);
                if (runLogPruned > 0)
                    console.WriteLine($"已清除 {runLogPruned} 筆超過 {retention.RunLogRetentionDays} 天的執行歷程／匯入紀錄。");

                var auditPruned = new AuditLogStore(backend.LogStore("audit")).Prune(retention.AuditRetentionDays);
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

            // 1b-2. 清理處理狀態三表與處理歷程（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4／G4）。
            //
            // **必須排在 historyService.Prune 之後**：那一步決定了哪些日期的分析紀錄還在，
            // 這裡刪的正是「紀錄已經不在、卻還留著的處理狀態」。順序反過來的話，
            // 這一輪會漏掉剛被判定過期的那幾天，要等到下次執行才補上。
            //
            // 三個對象、三種保留天數，理由各自不同（見各 store 的 Prune 註解）：
            //   問題／日處理狀態 → RetentionDays（跟著分析紀錄，它們是紀錄的附屬狀態）
            //   已結案的案件     → RetentionDays（同上，但判準是結案時間，不是事件日期）
            //   處理歷程         → AuditRetentionDays（追責證據，性質接近稽核）
            try
            {
                var issueHandlingPruned = backend.IssueHandlingStore().Prune(retention.RetentionDays);
                var recordHandlingStore = backend.RecordHandlingStore();
                var dayHandlingPruned = recordHandlingStore.Prune(retention.RetentionDays);
                var casePruned = backend.IssueCaseStore().Prune(retention.RetentionDays);

                var handlingPruned = issueHandlingPruned + dayHandlingPruned + casePruned;
                if (handlingPruned > 0)
                    console.WriteLine($"已清除 {handlingPruned} 筆超過 {retention.RetentionDays} 天的處理狀態" +
                                      $"（問題 {issueHandlingPruned}／日 {dayHandlingPruned}／已結案 {casePruned}）。");

                var handlingLogPruned = recordHandlingStore.PruneLogs(retention.AuditRetentionDays);
                if (handlingLogPruned > 0)
                    console.WriteLine($"已清除 {handlingLogPruned} 筆超過 {retention.AuditRetentionDays} 天的處理歷程。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "處理狀態／處理歷程清理失敗（不影響本次分析）：{0}", ex.Message);
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

            // 本機／NetIQ 並行執行（回饋十七輪批次E，取代原本「本機跑完才進 NetIQ」的依序關係）：
            // 2000 台規模下本機回補多天時，NetIQ 機房分析原本要空等本機跑完才能開始，兩者其實
            // 互不依賴（不同主機、不同資料列）。NetIQ pipeline 內部本來就用 Parallel.ForEachAsync
            // 平行跑多個 Sentinel worker，這裡是把本機也當成「多一個並行 worker」。
            //
            // **runCtx 只建一份、兩路共用同一個實例**（尤其 CaseCoordinator／RiskyEventStore／
            // RunRecorder）——這是併發安全的關鍵前提，不是巧合：IssueCaseCoordinator 底下的
            // RecordHandlingLog.LogId 是行程內單一序號產生器（EfRecordHandlingStore._lastLogId），
            // 靠實例層級的鎖擋撞號，只有在「本機與 NetIQ 共用同一個 IssueCaseCoordinator 實例」時
            // 才安全；若未來改動讓任一路各自另外呼叫 backend.RecordHandlingStore() 建出第二個實例，
            // 兩個實例會各自對 DB 算 MAX(seq) 而互相不知道對方，序號會撞號。同理 BatchRunRecorder
            // 的 Finish()/Dispose() 未加鎖也是安全的——因為它們只在下方 WhenAll 之後的單一匯合點
            // 被呼叫一次，永遠不會被兩條路徑各自呼叫（Task.WhenAll 的語意保證：回傳的 Task 要等
            // 兩個輸入 Task 都進入終態才會完成，不會有「其中一條還在跑、外層就已經在收尾」的情況）。
            var runCtx = new AnalysisRunContext(
                request, settings, retention, console, ct, eventLogService, caseCoordinator,
                riskyEventStore, runRecorder, result, useAi, progress);

            // 本機路徑額外套一層前綴 console（回饋十七輪批次E-2）：並行後兩路的輸出會交錯，
            // 沒有標記的話讀執行詳情看不出哪一行是哪一路。NetIQ 路徑既有的逐 Sentinel logContext
            // 前綴（見 HostDayPostProcessor 呼叫點）不受影響，維持原樣。只換 Console 這一個欄位，
            // CaseCoordinator／RiskyEventStore／RunRecorder 仍是上面那個共用實例——見上方說明。
            var localCtx = runCtx with { Console = new PrefixedRunConsole(console, "[本機] ") };

            // 2~4. 本機逐日分析：NetiqHosts 範圍（Phase 3 手動觸發指定 NetIQ 主機）不動本機資料；
            // IncludeLocal=false（回饋十八輪批次D，排程設定「分析本機主機」關閉）整段跳過，
            // 只印一行讓執行詳情看得出是設定行為、不是漏跑（不是「未執行」）。
            Task localTask;
            if (request.Scope == RunScope.NetiqHosts)
            {
                localTask = Task.CompletedTask;
            }
            else if (!request.IncludeLocal)
            {
                console.WriteLine("\n本機分析已停用（排程設定「分析本機主機」關閉），本次僅執行 NetIQ 機房分析。");
                runRecorder.Milestone("本機分析已停用（排程設定），本次僅執行 NetIQ 機房分析");
                localTask = Task.CompletedTask;
            }
            else
            {
                localTask = RunLocalAnalysisAsync(localCtx, analysisService, historyService, currentHost, currentHostId, yesterday);
            }

            // 5b. NetIQ 機房分析（docs/archive/HISTORY.md 決策 B2、§4；Phase 4）：對 Web 主機頁登錄的
            //    NetIQ 主機逐一向 Sentinel 取事件、映射後餵進同一套 LogAnalysisService。LocalOnly
            //    範圍（Phase 3 手動觸發只跑本機）跳過這段。
            var netiqTask = request.Scope != RunScope.LocalOnly
                ? RunNetiqAnalysisAsync(runCtx, backend, hostStore, sentinelStore, aiService, suppressionStore, reportService)
                : Task.CompletedTask;

            // 失敗語意：任一路未攔截的例外都讓整趟判定失敗（維持既有的嚴格語意，見下方
            // catch）；已寫入的另一路結果不受影響並保留——兩路各自對不同主機寫入，冪等，
            // 下次執行的缺漏日回補機制會自動補上失敗的那一路。NetIQ 路徑本來就有自己的內部
            // try/catch（RunNetiqAnalysisAsync 尾端）吞掉非取消例外，只有取消會穿透；本機路徑
            // 沒有這層保護，維持「本機出問題就是整趟失敗」的既有嚴格度（本機通常只有一台，
            // 出問題多半是環境性的，值得當硬失敗訊號，不像 NetIQ 是「一台壞不該拖累其他台」）。
            await Task.WhenAll(localTask, netiqTask);

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

    /// <summary>
    /// 本機路徑專用的前綴 console（回饋十七輪批次E-2）：本機與 NetIQ 並行執行後，兩路的輸出會
    /// 交錯，替本機每一行加上統一前綴才分得清哪一行是哪一路——NetIQ 路徑既有的逐 Sentinel
    /// logContext 前綴（見 HostDayPostProcessor 呼叫點）已經在做同樣的事，這裡是把同一個原則
    /// 套用到本機這一路。空白／純換行訊息先在這裡濾掉（trim 後為空就不加前綴、原樣轉發），
    /// 不然會印出一行只有「[本機] 」的空洞前綴——WebRunConsole 自己也會 trim 整段訊息，
    /// 但如果先加了前綴，訊息就不再是空的，trim 救不回來。
    /// </summary>
    private sealed class PrefixedRunConsole : IRunConsole
    {
        private readonly IRunConsole _inner;
        private readonly string _prefix;

        public PrefixedRunConsole(IRunConsole inner, string prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public void WriteLine(string message = "")
        {
            var trimmed = message.Trim();
            _inner.WriteLine(trimmed.Length == 0 ? trimmed : $"{_prefix}{trimmed}");
        }
    }

    private async Task RunLocalAnalysisAsync(
        AnalysisRunContext ctx, LogAnalysisService analysisService, IAnalysisRecordStore historyService,
        string currentHost, long currentHostId, DateTime yesterday)
    {
        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress) = ctx;

        // 找出缺漏的日子。首次執行（本機歷史資料庫全空）回補 InitialHistoryDays 天，讓趨勢分析
        // 一開始就有更充足的基準資料；已有任何本機紀錄時只看趨勢窗口 TrendWindowDays 天。
        var lookbackDays = historyService.HasAnyRecord() ? TrendWindowDays : retention.InitialHistoryDays;
        var missingDates = MissingDateFinder.Find(historyService, lookbackDays);

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

            // 問題案件批次逐日掛接（2.4）、風險 log 暫存、AI 呼叫計數：任一步失敗只記警告，
            // 不擋分析主流程（見 HostDayPostProcessor，與 NetIQ 機房路徑共用同一套後續處理）
            HostDayPostProcessor.AttachCase(caseCoordinator, currentHost, date, record.TopIssues);
            HostDayPostProcessor.ReplaceRiskyEvents(
                riskyEventStore, retention.RiskyEventRetentionDays, date, record.TopIssues, logs, currentHostId);

            dayStopwatch.Stop();
            elapsedByDate[date] = dayStopwatch.Elapsed;
            runRecorder.RecordDayAnalyzed();

            HostDayPostProcessor.RecordAiCallIfApplicable(runRecorder, useAi, record);
            PrintResult(console, record, verbose: date == yesterday);
            console.WriteLine($"  ⏱ 本日耗時：{FormatElapsed(dayStopwatch.Elapsed)}");
            progress?.Report("local", ++localDone, missingDates.Count);

            // 逐日之間讓出執行緒（S-3，與 NetIQ 路徑同一個理由）：統計模式下這個迴圈幾乎
            // 全程同步，不讓出的話會一路佔住同一條 thread pool 執行緒到回補完所有缺漏日
            await Task.Yield();
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
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, ISentinelStore sentinelStore,
        AIService aiService, ISuppressionStore suppressionStore, RiskReportService reportService)
    {
        var (request, _, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress) = ctx;

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
            var netiqOptions = new NetiqOptionsStore(backend.Blob("netiq_options")).Get();
            // 手動觸發的一次性回補天數覆寫（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：只影響這次執行，
            // 不落地——netiqOptions 是本次呼叫剛從 store 讀出的獨立物件，就地覆寫不影響下次讀取的設定值
            if (request.BackfillOverride is { } backfillOverride)
            {
                netiqOptions.BackfillDays = backfillOverride;
            }
            var netiqPipeline = new NetiqPipelineService(
                backend, netiqOptions, sentinelStore, hostStore,
                eventLogService, aiService, suppressionStore, reportService, runRecorder, caseCoordinator, console,
                riskyEventStore, retention.RiskyEventRetentionDays, useAi, progress);

            var netiqResult = await netiqPipeline.RunAsync(netiqHostList, TrendWindowDays, ct);
            result.NetiqResult = netiqResult;
            runRecorder.Milestone($"NetIQ 機房分析完成：已完成跳過 {netiqResult.HostsSkippedUpToDate}、" +
                $"本次分析 {netiqResult.HostDaysAnalyzed} 個主機日、失敗 {netiqResult.HostsFailed} 個主機日" +
                (netiqResult.AiQueued > 0
                    ? $"、AI 分析 {netiqResult.AiCompleted} 件完成（放棄 {netiqResult.AiAbandoned}）"
                    : ""));

            // Warnings（Linux 主機不支援、Sentinel 失聯等）過去只印在 console 詳情，排程作業頁的
            // 里程碑列表完全看不到「這次有異常」（docs/archive/FEEDBACK-12-PLAN.md §1.3）。彙整成一條
            // 而非逐條灌爆里程碑列表——警告數可能隨 Sentinel／主機數量成長。
            if (netiqResult.Warnings.Count > 0)
            {
                var preview = string.Join("；", netiqResult.Warnings.Take(2));
                var suffix = netiqResult.Warnings.Count > 2 ? "…（完整清單見執行詳情）" : "";
                runRecorder.Milestone($"⚠ 本次有 {netiqResult.Warnings.Count} 項警告：{preview}{suffix}");
            }
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
        finally
        {
            // NetIQ 這一路收尾訊號（體檢輪修正）：不論成功、失敗或取消，只要曾經進入這個 try
            // （TotalHosts>0，代表這次執行真的有 netiq 進度可能被回報過），都要送一次完工訊號。
            // 本機與 NetIQ 改並行執行後（批次E），NetIQ 若比本機早跑完（主機少、本機在回補
            // 多天缺漏），SchedulerRunState.ProgressPhase 會停在 netiq 的最後一次回報值不再變
            // ——單一告示讀取端（/api/run-activity、健診）的 LatestActivity() 因此會一路顯示
            // 這個凍結的舊值，外觀上與「卡住」無法區分。"netiq-done" 是 SchedulerRunState 認得
            // 的特殊 phase 字面值（與 "local" 一樣是兩邊約定的字串慣例，見 WebRunProgress／
            // SchedulerRunState.ReportProgress），收到後會清空 netiq 的主／子進度欄位，讓
            // LatestActivity() 的優先序自然落回還在推進的本機。
            progress?.Report("netiq-done", 0, 0);
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
    public int DetailRetentionDays { get; init; } = 120;
}
