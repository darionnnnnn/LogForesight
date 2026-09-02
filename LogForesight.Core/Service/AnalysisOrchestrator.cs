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

    /// <summary>只補跑失敗或未執行的主機（略過已成功且有 AI 分析的），預設 false。</summary>
    public bool OnlyMissingOrFailed { get; init; }

    /// <summary>
    /// 重新分析模式：要不要重新分析已有紀錄的日子、重跑到什麼程度。預設 None（不重跑）。
    /// </summary>
    public RerunMode RerunMode { get; init; } = RerunMode.None;

    /// <summary>
    /// <see cref="BatchRun.Trigger"/>：<c>"schedule"</c>｜<c>"manual:{帳號}"</c>
    /// （歷史紀錄中另有已退場的 console 批次寫入的 <c>"console"</c>）。
    /// </summary>
    public string? Trigger { get; init; }
}

/// <summary>本機逐日分析的單日摘要（批次E）：執行結果總表與計數用，不持有分析內容。</summary>
public record LocalDaySummary(DateTime Date, string RiskLevel, bool HasReport);

/// <summary>單次執行的結果摘要，供呼叫端（Web 的 BatchRun 回填與失敗回饋）使用。</summary>
public class OrchestratorResult
{
    public bool Success { get; set; } = true;
    public string? FailureMessage { get; set; }
    /// <summary>
    /// 本機逐日分析的結果摘要（回饋三十五輪批次E）：只留消費端真正會用到的三個欄位。
    /// 原本整趟累積完整的 <see cref="DailyAnalysisRecord"/>（含 TopIssues 與樣本訊息），
    /// 一次 120 天的回補會讓這份清單在整趟執行期間持續佔用記憶體，
    /// 而消費端只需要「哪天、什麼風險、有沒有報告」。
    /// </summary>
    public List<LocalDaySummary> LocalResults { get; set; } = new();
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
            var aiService = new AIService(settings.Ai, dumper,
                new AiUsageStore(backend.Blob(AiUsageStore.BlobKey)));
            var reportSink = backend.ReportStore(); // 報告全文存進 lf_reports

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
                await reportSink.WriteAsync(ReportKind.Permission,
                    new HostKey { HostId = currentHostId, HostName = currentHost }, permissionFileName, reportSb.ToString());
                console.WriteLine("  📄 權限異動報告（含逐項明細）已產生，可於「權限異動檢核」頁檢視。");

                // 雙軌寫入（docs/WEB-SPEC.md §2.1 Phase 3）：上面的 console 告警與報告全文是既有輸出、
                // 一字未改；這裡另外把每筆異動寫成結構化紀錄，供 Web 的「權限異動檢核」逐筆確認。
                try
                {
                    var permissionChangeStore = backend.PermissionChanges();
                    var detectedAt = DateTime.Now;

                    permissionChangeStore.AppendChanges(permissionCheck.Details.Select((detail, index) => new PermissionChangeRecord
                    {
                        ChangeId = Guid.NewGuid().ToString("N"),
                        HostName = currentHost,
                        DetectedAt = detectedAt,
                        Target = detail.Target,
                        ChangeType = detail.ChangeType,
                        Category = detail.Category,
                        IsPrivilegedTarget = detail.IsPrivilegedTarget,
                        InitiatorAccount = detail.InitiatorAccount,
                        TargetAccount = detail.TargetAccount,
                        Before = detail.Before,
                        After = detail.After,
                        AlertText = index < permissionCheck.Alerts.Count ? permissionCheck.Alerts[index] : string.Empty,
                        Source = PermissionChangeSources.Local
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

            // 1. 清理超過保留天數的歷史紀錄，避免資料庫無限增長。
            //
            // **一律用未限縮的 RecordStore()，不要用上面的 historyService**（SCALE-3000 S2-3b）：
            // historyService 綁定「本機」識別（缺日判定與趨勢基準只看本機），而 NetIQ 機房
            // 數千台主機的紀錄不屬於本機。這裡若用它，保留期對絕大多數資料等於從來沒有生效——
            // 這正是 2026-08-16 以前的實際情況，而 docs/DB-SPEC.md 的保留策略表寫的是全表適用，
            // 文件與實作長期不一致。實測（RetentionScopeBenchmarks）：500 台 × 200 天的資料集，
            // 限縮到本機可清 0 筆、未限縮 39,500 筆。
            //
            // 限縮語意本身沒有錯，錯的是拿它來清理——store 該誠實，所以只改呼叫端。
            //
            // 兩層保留期（S2）：先刪整列，再把「留著但已過詳情保留期」的 content_json 清空。
            // **順序不可反**：先清詳情再刪列，等於為即將被刪的列白做一次 UPDATE。
            var allHostRecords = backend.RecordStore();

            var pruned = allHostRecords.Prune(retention.RetentionDays);
            if (pruned > 0)
            {
                console.WriteLine($"已清除 {pruned} 筆超過 {retention.RetentionDays} 天的歷史紀錄。");
            }

            try
            {
                var detailPruned = allHostRecords.PruneDetails(retention.RawEventRetentionDays);
                if (detailPruned > 0)
                {
                    console.WriteLine($"已清除 {detailPruned} 筆超過 {retention.RawEventRetentionDays} 天的紀錄詳情" +
                                      "（統計與問題清單保留，僅原始樣本訊息不可再查看）。");
                }

                // 積壓申報：單次清除有上限（見 EfAnalysisRecordStore.MaxPruneRowsPerRun），
                // 首次啟用時的積壓會分多晚排掉。沒有這行的話，使用者看到「清了還有」會以為壞掉。
                var remaining = allHostRecords.CountPrunableRecords(retention.RetentionDays);
                if (remaining > 0)
                {
                    // 只報筆數的話，「陸續清完」與「卡住了」在畫面上分不出來——
                    // 依單次上限估出還要幾次執行，使用者才知道是明天還是下個月
                    var perRun = EfAnalysisRecordStore.MaxPruneRowsPerRun;
                    var estimatedRuns = (remaining + perRun - 1) / perRun;
                    var msg = $"尚有 {remaining} 筆過期紀錄超出本次清除上限（每次上限 {perRun} 筆），" +
                              $"預估還需約 {estimatedRuns} 次執行清完。";
                    console.WriteLine($"  ℹ {msg}");
                    runRecorder.Milestone(msg);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "詳情清除或積壓申報失敗（不影響本次分析）：{0}", ex.Message);
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

                // 權限異動檢核（含 NetIQ 事件來源，3000 台規模下每天都會寫入）：性質是追責證據，
                // 跟稽核紀錄同一個保留天數
                var permPruned = backend.PermissionChanges()
                    .Prune(retention.AuditRetentionDays);
                if (permPruned > 0)
                    console.WriteLine($"已清除 {permPruned} 筆超過 {retention.AuditRetentionDays} 天的權限異動紀錄。");

                // 風險 log 暫存清理（docs/archive/WEB-SCHEDULER-PLAN.md §2.2.3）
                var riskyEventPruned = riskyEventStore.Prune(retention.RawEventRetentionDays);
                if (riskyEventPruned > 0)
                    console.WriteLine($"已清除 {riskyEventPruned} 筆超過 {retention.RawEventRetentionDays} 天的風險 log 暫存。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "執行歷程／匯入／稽核紀錄／風險 log 暫存清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            // 1b-2. 清理處理狀態三表與處理歷程（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4／G4）。
            //
            // **必須排在上面的分析紀錄清理（步驟 1）之後**：那一步決定了哪些日期的分析紀錄還在，
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

            // 1c. 清理過期的報告全文（風險／週檢／權限異動）——保留期是獨立設定
            // ReportRetentionDays，但上限受 RetentionDays 約束（見 RuntimeSettingsResolver）
            try
            {
                var reportsPruned = backend.ReportStore().Prune(retention.ReportRetentionDays);
                if (reportsPruned > 0)
                    console.WriteLine($"已清除 {reportsPruned} 份超過 {retention.ReportRetentionDays} 天的報告全文（風險／週檢／權限異動）。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "報告全文清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            try
            {
                var prtgPruned = backend.PrtgStore().Prune(retention.PrtgRetentionDays);
                if (prtgPruned > 0)
                    console.WriteLine($"已清除 {prtgPruned} 筆超過 {retention.PrtgRetentionDays} 天的 PRTG 鏡像資料。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PRTG 鏡像資料清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            try
            {
                var diagPruned = FilePromptDumper.Prune(retention.RunLogRetentionDays);
                if (diagPruned > 0)
                    console.WriteLine($"已清除 {diagPruned} 個超過 {retention.RunLogRetentionDays} 天的 AI 診斷傾印檔。");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "AI 診斷傾印檔清理失敗（不影響本次分析）：{0}", ex.Message);
            }

            var yesterday = DateTime.Today.AddDays(-1);

            // 本機／NetIQ 並行執行（回饋十七輪批次E，取代原本「本機跑完才進 NetIQ」的依序關係）：
            // 2000 台規模下本機回補多天時，NetIQ 機房分析原本要空等本機跑完才能開始，兩者其實
            // 互不依賴（不同主機、不同資料列）。NetIQ pipeline 內部本來就用 Parallel.ForEachAsync
            // 平行跑多個 Sentinel worker，這裡是把本機也當成「多一個並行 worker」。
            //
            // **runCtx 只建一份、兩路共用同一個實例**（尤其 CaseCoordinator／RiskyEventStore／
            // RunRecorder）——共用是刻意的，但**歷程序號已不再依賴它**：
            // EfRecordHandlingStore.AppendLog 每次寫入都重讀 DB 尾端取得起點，不留記憶體快取，
            // 所以多個實例並存也不會撞號（Web 端 Singleton 與這裡自建的 backend 本來就是兩份，
            // 舊的快取寫法在那個邊界上已經會重號）。同理 BatchRunRecorder
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
                localTask = RunLocalAnalysisAsync(localCtx, analysisService, historyService, backend.IssueHandlingStore(), currentHost, currentHostId, yesterday);
            }

            // 5b. NetIQ 機房分析（docs/archive/HISTORY.md 決策 B2、§4；Phase 4）：對 Web 主機頁登錄的
            //    NetIQ 主機逐一向 Sentinel 取事件、映射後餵進同一套 LogAnalysisService。LocalOnly
            //    範圍（Phase 3 手動觸發只跑本機）跳過這段。
            var netiqTask = request.Scope != RunScope.LocalOnly
                ? RunNetiqAnalysisAsync(runCtx, backend, hostStore, sentinelStore, aiService, suppressionStore, reportService)
                : Task.CompletedTask;

            var analysisTask = Task.WhenAll(localTask, netiqTask);
            var prtgTask = RunPrtgFetchAsync(runCtx, backend, hostStore, yesterday, analysisTask);

            // 失敗語意：任一路未攔截的例外都讓整趟判定失敗（維持既有的嚴格語意，見下方
            // catch）；已寫入的另一路結果不受影響並保留——兩路各自對不同主機寫入，冪等，
            // 下次執行的缺漏日回補機制會自動補上失敗的那一路。NetIQ 與 PRTG 路徑本來就有自己的內部
            // try/catch 吞掉非取消例外，只有取消會穿透；本機路徑
            // 沒有這層保護，維持「本機出問題就是整趟失敗」的既有嚴格度（本機通常只有一台，
            // 出問題多半是環境性的，值得當硬失敗訊號，不像 NetIQ/PRTG 是外部系統失聯不該拖累其他主機）。
            await Task.WhenAll(analysisTask, prtgTask);

            // 6. 體檢：週期性回顧（獨立於每日分析），距上次體檢達 CheckupIntervalDays 天（含補跑）就執行
            if (weeklyCheckupService.ShouldRun(DateTime.Today, settings.Analysis.CheckupIntervalDays))
            {
                console.WriteLine($"\n執行體檢（週期性回顧，以 {yesterday:yyyy-MM-dd} 為基準）...");
                var checkupStopwatch = Stopwatch.StartNew();
                var checkup = await weeklyCheckupService.RunAsync(yesterday, settings.Analysis.CheckupIntervalDays,
                    settings.Analysis.ServerDescription, host: new HostKey { HostId = currentHostId, HostName = currentHost }, useAi: useAi);

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
                            console.WriteLine("  📄 體檢報告已產生，可於該日的分析紀錄詳情檢視。");
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
        IIssueHandlingStore handlingStore, string currentHost, long currentHostId, DateTime yesterday)
    {
        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress) = ctx;

        // 回望天數（回饋三十四輪 C）：立即執行的「回望天數」是單一欄位，缺漏日與重跑日
        // 共用同一個窗口，且**本機與 NetIQ 都適用**——合併前這個欄位只影響 NetIQ，
        // 本機只吃重跑天數，填 30 天卻只回補 14 天是說不通的靜默不一致。
        // 留空（未指定）時維持本機既有預設：首次執行（歷史全空）回補 InitialHistoryDays 天
        // 讓趨勢一開始就有基準，已有紀錄時只看趨勢窗口 TrendWindowDays 天。
        // 夾在保留期上限內：Web 端觸發時已驗證過，這裡再夾一次是為了「保留期事後被調小」
        // 與未來新增呼叫端——回望超過保留期的日子分析完下輪就被清掉，白跑一趟。
        // 夾制只套在使用者明示的天數上：留空時的預設值（首次執行的 InitialHistoryDays）本來就受
        // 「保留天數不可小於首次回補天數」的設定驗證管，再夾一次會在既有 DB 的異常設定下
        // 把首跑回補靜默縮短（終檢抓到）。
        var lookbackDays = request.BackfillOverride is { } requested
            ? Math.Min(requested, NetiqOptions.GetEffectiveBackfillDaysLimit(retention.RetentionDays))
            : (historyService.HasAnyRecord() ? TrendWindowDays : retention.InitialHistoryDays);

        var missingDates = MissingDateFinder.Find(historyService, lookbackDays, requireAi: request.OnlyMissingOrFailed, useAi: useAi);

        var rerunDates = request.RerunMode != RerunMode.None
            ? RerunDateFinder.Find(historyService, handlingStore, currentHost, lookbackDays, request.RerunMode)
            : new List<DateTime>();
        var rerunDateSet = rerunDates.ToHashSet();

        var datesToAnalyze = missingDates.Union(rerunDates).OrderBy(date => date).ToList();

        if (datesToAnalyze.Count == 0)
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
        var rangeStart = datesToAnalyze[0];

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

        // 分塊掃描（回饋三十四輪 A1）：整個回補區間一次全載，一台吵雜主機 120 天可達數十 GB，
        // 這是排程執行占用 22GB 的主因之一。改成逐塊取數、逐塊分析、逐塊釋放——
        // 同一時間記憶體裡只有一塊（預設 7 天）的事件。逐日分析的順序不變（由舊到新，
        // 趨勢比對依賴前面日期寫入的歷史），只是取數的邊界跟著區塊走。
        var chunks = EventLogService.SplitDateRange(rangeStart, DateTime.Today, EventLogService.DefaultScanChunkDays)
            .Where(chunk => datesToAnalyze.Any(d => d >= chunk.Start && d < chunk.EndExclusive))
            .ToList();

        if (datesToAnalyze.Count > 1)
        {
            var aiClause = useAi ? "每天皆完整 AI 分析，" : "統計模式（AI 未設定），";
            console.WriteLine($"偵測到歷史資料有缺漏，回補 {datesToAnalyze.Count} 天（{aiClause}由最舊到最新，後面的日期能參照前面累積的歷史）。");
            console.WriteLine("（能回補多久取決於 Event Log 的保留量，太舊的事件可能已被覆蓋）");
        }

        // 逐日分析：趨勢比對依賴前面日期寫入的歷史，因此分析本身必須依序執行。
        var elapsedByDate = new Dictionary<DateTime, TimeSpan>();
        progress?.Report("local", 0, datesToAnalyze.Count);
        var localDone = 0;
        var rerunAnalyzedCount = 0;
        var rerunRetainedCount = 0;
        var totalEvents = 0;
        // 頻道可用性、Security 是否可讀、各來源可回溯到的最早時間都是「整份日誌」的性質，
        // 每個區塊掃描出來的結論相同，因此逐塊的中繼資料直接用於該塊的日期即可；
        // 讀取失敗／頻道不存在的申報只在第一個區塊印一次，不逐塊重複刷屏。
        var warningsPrinted = false;

        foreach (var (chunkStart, chunkEndExclusive) in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var chunkDates = datesToAnalyze.Where(d => d >= chunkStart && d < chunkEndExclusive).ToList();

            // 掃描結果只在這個區塊裡存活（回饋三十五輪批次E3）：ScanRangeFromAllAsync 回傳的
            // ScanResult 同時持有合併後的 Entries 與逐頻道的 BySource 兩份清單參照，
            // 原本整份在該區塊 14 天的分析期間全程不放。這裡先把逐日分組與逐日中繼資料取出來，
            // 讓 scanResult 在進入逐日迴圈前就離開範圍，只留下真正還要用的東西。
            Dictionary<DateTime, List<EventLogEntryData>> logsByDate;
            Dictionary<DateTime, bool> incompleteByDate;
            ChannelAvailability channelAvailability;
            bool? securityAvailable;
            {
                var scanResult = await eventLogService.ScanRangeFromAllAsync(chunkStart, chunkEndExclusive, channelNames);
                logsByDate = scanResult.Entries
                    .GroupBy(l => l.TimeGenerated.Date)
                    .ToDictionary(g => g.Key, g => g.ToList());
                incompleteByDate = chunkDates.ToDictionary(d => d, scanResult.IsDateIncomplete);
                securityAvailable = scanResult.SecurityAvailable;
                totalEvents += scanResult.Entries.Count;

            if (!warningsPrinted)
            {
                warningsPrinted = true;

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
            }

                channelAvailability = new ChannelAvailability
                {
                    Read = scanResult.ChannelsRead,
                    Denied = scanResult.ChannelsDenied,
                    Missing = scanResult.ChannelsMissing
                };
            }

            foreach (var date in chunkDates)
            {
                // 取消語意＝停在「主機日」邊界：當前這一天分析完才停，不硬掐 AI 呼叫本身
                ct.ThrowIfCancellationRequested();

                var isRerun = rerunDateSet.Contains(date.Date);
                var logs = logsByDate.TryGetValue(date, out var dayLogs) ? dayLogs : new List<EventLogEntryData>();
                // 這一天的事件取出後就從分組移除（批次E3）：區塊內的記憶體隨進度遞減，
                // 而不是整塊 14 天的事件全程壓在記憶體裡等最後一起回收。
                logsByDate.Remove(date);

                var dataIncomplete = incompleteByDate[date];

                // 重跑日：來源取不到資料、或這次取得的資料比當初殘缺時，保留原結果整天跳過——
                // 判定與 NetIQ 路徑共用同一個函式，不各寫一份
                if (isRerun && HostDayPostProcessor.ShouldRetainExistingDay(
                        logs.Count, dataIncomplete, securityAvailable == false))
                {
                    rerunRetainedCount++;
                    console.WriteLine($"[{date:yyyy-MM-dd}] 來源已無事件或資料不完整，保留原分析結果");
                    progress?.Report("local", ++localDone, datesToAnalyze.Count);
                    continue;
                }

                console.WriteLine($"\n[{date:yyyy-MM-dd}] 分析中（{(useAi ? "含 AI 判讀" : "統計模式，AI 未設定")}）...");
                var dayStopwatch = Stopwatch.StartNew();

                // 重跑日的舊紀錄由 AnalyzeDayAsync 在寫入前才刪（replaceExisting）——不在這裡先刪，
                // 否則分析途中拋例外會留下「舊的已刪、新的沒寫」的永久空白日
                if (isRerun) rerunAnalyzedCount++;

                var record = await analysisService.AnalyzeDayAsync(date, logs, useAi: useAi, historyDays: TrendWindowDays,
                    dataIncomplete: dataIncomplete, securityLogAvailable: securityAvailable, channels: channelAvailability,
                    replaceExisting: isRerun);
                result.LocalResults.Add(new LocalDaySummary(record.Date, record.RiskLevel, record.ReportFile != null));

                // 問題案件批次逐日掛接（2.4）、風險 log 暫存、AI 呼叫計數：任一步失敗只記警告，
                // 不擋分析主流程（見 HostDayPostProcessor，與 NetIQ 機房路徑共用同一套後續處理）
                HostDayPostProcessor.AttachCase(caseCoordinator, currentHost, date, record.TopIssues);
                HostDayPostProcessor.ReplaceRiskyEvents(
                    riskyEventStore, retention.RawEventRetentionDays, date, record.TopIssues, logs, currentHostId);

                dayStopwatch.Stop();
                elapsedByDate[date] = dayStopwatch.Elapsed;
                runRecorder.RecordDayAnalyzed();

                HostDayPostProcessor.RecordAiCallIfApplicable(runRecorder, useAi, record);
                PrintResult(console, record, verbose: date == yesterday);
                console.WriteLine($"  ⏱ 本日耗時：{FormatElapsed(dayStopwatch.Elapsed)}");
                progress?.Report("local", ++localDone, datesToAnalyze.Count);

                // 逐日之間讓出執行緒（S-3，與 NetIQ 路徑同一個理由）：統計模式下這個迴圈幾乎
                // 全程同步，不讓出的話會一路佔住同一條 thread pool 執行緒到回補完所有缺漏日
                await Task.Yield();
            }
        }

        console.WriteLine($"\n共取得 {totalEvents} 筆事件。");
        runRecorder.Milestone($"逐日分析完成：{result.LocalResults.Count} 天");

        if (request.RerunMode != RerunMode.None)
        {
            var rerunSummary = HostDayPostProcessor.RerunSummary(rerunAnalyzedCount, rerunRetainedCount);
            console.WriteLine($"\n  {rerunSummary}");
            runRecorder.Milestone(rerunSummary);
        }

        // 執行結果總表：讓使用者一眼看到「哪幾天有問題、哪幾天有報告可看、花了多久」。
        // 報告參照（lf_reports 的主鍵）對使用者沒有意義，console 只標示有無，內容到 Web 看。
        console.WriteLine("\n══════════ 本次執行結果 ══════════");
        foreach (var r in result.LocalResults)
        {
            console.WriteLine($"  {r.Date:yyyy-MM-dd}  風險【{r.RiskLevel}】  耗時 {FormatElapsed(elapsedByDate[r.Date])}" +
                              (r.HasReport ? "  → 已產生風險報告" : ""));
        }

        var riskyCount = result.LocalResults.Count(r => r.HasReport);
        if (riskyCount > 0)
        {
            console.WriteLine(
                $"\n  需要關注：{riskyCount} 天判定有風險，問題說明、AI 深入分析與原始 log 已寫入報告，" +
                "可於 Web 的分析紀錄詳情檢視。");
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
        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
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
            // 回望天數的有效上限＝目前的保留天數（超過保留期的回補下輪就被清掉，白跑）。
            // Web 端儲存與觸發時都驗證過，但這條路徑直接讀 blob——blob 裡若存著舊的大值、
            // 或 RetentionDays 事後被調小，不在這裡夾住就會回望超過保留期，
            // 連帶讓去重鍵的查詢窗口跨到同樣的天數。
            netiqOptions.BackfillDays = Math.Min(
                netiqOptions.BackfillDays,
                NetiqOptions.GetEffectiveBackfillDaysLimit(retention.RetentionDays));
            var netiqPipeline = new NetiqPipelineService(
                backend, netiqOptions, sentinelStore, hostStore,
                eventLogService, aiService, suppressionStore, reportService, runRecorder, caseCoordinator, console,
                riskyEventStore, retention.RawEventRetentionDays, useAi, progress,
                onlyMissingOrFailed: request.OnlyMissingOrFailed,
                permissionMappings: settings.Permissions.FieldMappings,
                rerunMode: request.RerunMode);

            var netiqResult = await netiqPipeline.RunAsync(netiqHostList, TrendWindowDays, ct);
            result.NetiqResult = netiqResult;
            runRecorder.Milestone($"NetIQ 機房分析完成：已完成跳過 {netiqResult.HostsSkippedUpToDate}、" +
                $"本次分析 {netiqResult.HostDaysAnalyzed} 個主機日、失敗 {netiqResult.HostsFailed} 個主機日" +
                (netiqResult.RerunDaysAnalyzed > 0 || netiqResult.RerunDaysRetained > 0
                    ? "、" + HostDayPostProcessor.RerunSummary(netiqResult.RerunDaysAnalyzed, netiqResult.RerunDaysRetained)
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

    private async Task RunPrtgFetchAsync(
        AnalysisRunContext ctx, StorageBackend backend, IHostStore hostStore, DateTime day, Task analysisTask)
    {
        var (request, settings, retention, console, ct, eventLogService, caseCoordinator, riskyEventStore,
            runRecorder, result, useAi, progress) = ctx;

        var prtgConsole = new PrefixedRunConsole(console, "[PRTG] ");

        try
        {
            var systemSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (!systemSettings.PrtgEnabled ||
                string.IsNullOrWhiteSpace(systemSettings.PrtgUrl) ||
                !PrtgClientFactory.HasUsableCredentials(systemSettings))
            {
                prtgConsole.WriteLine("PRTG 未啟用或尚未設定認證資訊，略過。");
                return;
            }

            using var client = PrtgClientFactory.Create(systemSettings);

            var fetchService = new PrtgFetchService(client, backend.PrtgStore(), prtgConsole);

            // 1. 結構與狀態變更同步（數值階段略過，改由下方觸發式取數執行）
            try
            {
                var fetchResult = await fetchService.FetchDayAsync(
                    day, systemSettings.PrtgFetchConcurrency, ct, syncStructure: true, fetchValues: false);

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
                runRecorder.Milestone($"PRTG 主機對應完成（{day:yyyy-MM-dd}）：ok={mapResult.Ok}, manual={mapResult.Manual}, conflict={mapResult.Conflict}, unmatched={mapResult.Unmatched}, skipped_no_ip={mapResult.SkippedNoIp}");
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

                var downMinutes = 60;
                var flapCount = 5;
                var warningMinutes = 240;

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

            // 4. PRTG 觸發式數值取數：獨立的 try/catch，與分析並行輪詢
            try
            {
                var triggeredFetcher = new PrtgTriggeredValueFetcher(
                    fetchService, backend.PrtgStore(), backend.RecordStore(), prtgConsole);
                var triggeredResult = await triggeredFetcher.RunAsync(
                    day, systemSettings.PrtgSensorTypeWhitelist, systemSettings.PrtgFetchConcurrency,
                    () => analysisTask.IsCompleted, ct, extraTriggerHosts: ruleTriggerHosts);

                var summary = $"PRTG 觸發式取數完成（{day:yyyy-MM-dd}）：問題主機 {triggeredResult.TriggerHosts} 台、" +
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

            // 5. PRTG finding 追加：獨立的 try/catch，在觸發式取數（分析完成）後追加至當日紀錄
            try
            {
                var involvedHosts = findingsByHost.Count;
                var appendedHosts = 0;
                var skippedHosts = 0;

                var allHosts = hostStore.GetAll();
                var hostsById = allHosts.ToDictionary(h => h.HostId);

                foreach (var (hostId, hostFindings) in findingsByHost)
                {
                    var hostName = hostsById.TryGetValue(hostId, out var webHost) ? webHost.HostName : string.Empty;
                    var hostKey = new HostKey { HostId = hostId, HostName = hostName };
                    var hostRecordStore = backend.RecordStore(hostKey);

                    var attached = hostRecordStore.AttachPrtgFindings(hostId, day, hostFindings);
                    if (attached)
                    {
                        appendedHosts++;
                    }
                    else
                    {
                        skippedHosts++;
                    }
                }

                var summary = $"PRTG 規則評估完成（{day:yyyy-MM-dd}）：finding {totalFindings} 筆、涉及主機 {involvedHosts} 台、已追加 {appendedHosts} 台（無當日紀錄 {skippedHosts} 台）";
                prtgConsole.WriteLine(summary);
                runRecorder.Milestone(summary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG finding 追加失敗，不影響分析成果");
                prtgConsole.WriteLine($"\n  ✗ PRTG finding 追加失敗：{ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // 取消訊號必須穿透，讓上層統一處理中斷
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PRTG 每日擷取初始化失敗，本機與 NetIQ 分析結果不受影響");
            prtgConsole.WriteLine($"\n  ✗ PRTG 每日擷取初始化失敗：{ex.Message}（本機與 NetIQ 分析結果不受影響）");
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

        // 有產生風險報告時明確指引去哪看細節（報告參照是主鍵，對使用者沒有意義，不印出來）
        if (record.ReportFile != null)
        {
            console.WriteLine(
                "\n  📄 詳細風險報告（含 AI 深入分析與原始 log）已產生，可於 Web 的分析紀錄詳情檢視。");
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
    public int InitialHistoryDays { get; init; } = SystemSettings.DefaultInitialHistoryDays;
    public int RetentionDays { get; init; } = SystemSettings.DefaultRetentionDays;
    public int RunLogRetentionDays { get; init; } = SystemSettings.DefaultRunLogRetentionDays;
    public int AuditRetentionDays { get; init; } = SystemSettings.DefaultAuditRetentionDays;
    public int RawEventRetentionDays { get; init; } = SystemSettings.DefaultRawEventRetentionDays;
    public int ReportRetentionDays { get; init; } = SystemSettings.DefaultReportRetentionDays;
    public int PrtgRetentionDays { get; init; } = SystemSettings.DefaultPrtgRetentionDays;

}

/// <summary>規則庫尚無 PRTG 規則時，用來跳出 <see cref="AnalysisOrchestrator"/> 規則評估段的控制流例外
/// （不是錯誤，呼叫端靜默吞掉、不記 error log）。</summary>
internal sealed class PrtgRulesUnavailableException : Exception { }
