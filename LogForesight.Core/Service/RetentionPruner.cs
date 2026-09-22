using System.Text.Json.Serialization;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 保留期清除（歷史紀錄、詳情、執行歷程／匯入／稽核、權限異動、風險 log 暫存、處理狀態與歷程、
/// 交辦單與其事件、報告全文、PRTG 鏡像、AI 診斷傾印檔）的唯一實作。
/// 分析執行（<see cref="AnalysisOrchestrator.RunAsync"/>）與「分析當天沒跑時的單獨清除」
/// （<see cref="AnalysisOrchestrator.RunRetentionOnlyAsync"/>）共用這一份——
/// 只依附分析執行的話，排程停用或錯過窗口的日子保留期就等於沒有生效。
/// 執行完把當天日期記進 <c>retention_state</c> blob，排程輪詢據此判斷今天是否還要補做。
/// </summary>
public static class RetentionPruner
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const string StateBlobKey = "retention_state";

    /// <param name="batchRunStore">執行紀錄 store；初始化失敗時為 null（沿用分析執行原本的語意：執行歷程本次不清）</param>
    public static void Run(StorageBackend backend, RetentionOptions retention, IRunConsole console, BatchRunStore? batchRunStore)
    {
        // 風險 log 暫存（清除用；分析路徑另有自己的實例，兩者都是無狀態的真表 store）
        var riskyEventStore = backend.RiskyEventStore();

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
            var workOrderPruned = backend.WorkOrderStore().PruneClosed(retention.RetentionDays);
            // 未結案交辦單的事件：追責紀錄，天數同處理歷程（AuditRetentionDays）；建單事件與每張單最新 200 筆保留
            var workOrderEventPruned = backend.WorkOrderStore().PruneOpenOrderEvents(retention.AuditRetentionDays);

            var handlingPruned = issueHandlingPruned + dayHandlingPruned + casePruned + workOrderPruned;
            if (handlingPruned + workOrderEventPruned > 0)
                console.WriteLine($"已清除 {handlingPruned} 筆超過 {retention.RetentionDays} 天的處理狀態" +
                                  $"（問題 {issueHandlingPruned}／日 {dayHandlingPruned}／已結案 {casePruned}／交辦單 {workOrderPruned}" +
                                  $"／交辦事件 {workOrderEventPruned}）。");

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

        // 記錄今天已執行保留清除：排程輪詢看到今天已做過就不再單獨補做
        try
        {
            new RetentionStateStore(backend.Blob(StateBlobKey))
                .Update(s => s.LastRunDate = DateTime.Today.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "保留清除執行日期記錄失敗：{0}", ex.Message);
        }
    }

    /// <summary>上次執行保留清除的日期；從未執行或內容無法解析時回 null</summary>
    public static DateTime? LastRunDate(StorageBackend backend)
    {
        var raw = new RetentionStateStore(backend.Blob(StateBlobKey)).Get().LastRunDate;
        return DateTime.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>retention_state blob 的內容：<c>{ "lastRunDate": "yyyy-MM-dd" }</c></summary>
    internal sealed class RetentionState
    {
        [JsonPropertyName("lastRunDate")]
        public string? LastRunDate { get; set; }
    }

    private sealed class RetentionStateStore : JsonBlobSingleton<RetentionState>
    {
        public RetentionStateStore(EfJsonBlobStore blob) : base(blob) { }
    }
}
