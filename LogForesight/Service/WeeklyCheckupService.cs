using System.Text;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 體檢：獨立於每日分析的週期性回顧。2026-07-20 重設計（見 docs/PLAN.md「核心設計決策 B」與
/// docs/AI-ROLE-PLAN.md）：原本「找出單看每天都不明顯、但整週合起來是持續累積或緩慢惡化的訊號」
/// 這件「發現」的工作，已改由每日全主機執行的確定性 <see cref="SlowTrendAnalyzer"/> 負責
/// （近 7 天 vs 前 7 天總量比較），偵測延遲從最壞一整個週期縮短到 1 天。體檢因此只剩下
/// 「講這段期間的故事」——把窗口內已經確定有訊號的日子，接續上次體檢的結論寫成一段回顧。
///
/// **due-date 輪巡取代固定星期**：不綁定「每週六」，改為「距上次體檢達 <see cref="AnalysisSettings.CheckupIntervalDays"/>
/// 天即到期」，是既有「距上次體檢 &gt;7 天自動補跑」機制的一般化——單機情境下等同「每 N 天做一次」；
/// 多主機規模下（見 docs/PLAN.md）到期主機會用主機識別雜湊錯峰虛擬回填上次體檢日，自然攤平不會集中尖峰，
/// 但那是機隊管理層的職責，不影響這裡的單機邏輯。
///
/// **確定性閘門**：窗口內任何一天有風險（非「低」）、趨勢異常或關聯訊號，才呼叫 AI 敘事；
/// 三層皆無訊號的窗口直接寫固定結論，不消耗 AI 呼叫——安靜的期間本來就沒有故事可講。
/// </summary>
public class WeeklyCheckupService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int MaxSignatureLines = 40;
    private const int MaxLastConclusionChars = 300;
    private const int MaxOutputTokens = 1536;

    private const string SystemPrompt =
        "你是資深 Windows Server 維運與資安分析師，同時也是把技術判讀翻譯成白話的溝通者。" +
        "程式已確定這段期間內有值得回顧的訊號（風險日、趨勢異常或關聯訊號），你的工作是把它們" +
        "接續上次體檢的結論，寫成一段給不懂 Event Log 的人也能看懂的回顧文字——不要引用 Event ID 或" +
        "程式碼層級術語，只根據使用者提供的資料撰寫，不要臆測資料中不存在的事件。全程使用繁體中文。" +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出一個符合使用者指定結構的 JSON 物件。";

    private readonly AIService _aiService;
    private readonly IAnalysisRecordReader _historyReader;
    private readonly IReportSink _reportSink;
    private readonly ISuppressionStore? _suppressionStore;

    /// <param name="suppressionStore">提供時，體檢報告固定列出本機生效中的抑制清單＋窗口期間各自的
    /// 發生次數（見 docs/RULES-PLAN.md 陷阱 4：暫時關閉的告警不該變成永久盲區）；null 時略過該區塊。</param>
    public WeeklyCheckupService(AIService aiService, IAnalysisRecordReader historyReader, IReportSink reportSink, ISuppressionStore? suppressionStore = null)
    {
        _aiService = aiService;
        _historyReader = historyReader;
        _reportSink = reportSink;
        _suppressionStore = suppressionStore;
    }

    /// <summary>
    /// 是否該執行本次體檢：距上次體檢已達 <paramref name="intervalDays"/> 天（含從未體檢過的情況，
    /// 立即執行以建立基準）。取代原本綁定固定星期幾的判斷——due-date 到期即做，
    /// 涵蓋機器關機、排程失敗導致錯過的補跑（尚無任何分析紀錄時不執行，沒有基準可回顧）。
    /// </summary>
    public bool ShouldRun(DateTime today, int intervalDays)
    {
        if (!_historyReader.HasAnyRecord())
        {
            return false;
        }

        var last = _historyReader.LastWeeklyCheckupDate();
        return last == null || (today.Date - last.Value.Date).TotalDays >= intervalDays;
    }

    /// <param name="intervalDays">體檢窗口天數，通常等於 <see cref="AnalysisSettings.CheckupIntervalDays"/></param>
    /// <param name="host">主機識別，單機情境留空即可</param>
    public async Task<WeeklyCheckupResult> RunAsync(DateTime checkupDate, int intervalDays, string serverDescription = "", string host = "")
    {
        var window = _historyReader.ReadRecent(checkupDate, intervalDays);

        // 確定性閘門：窗口內三層（風險等級/趨勢異常/關聯訊號）皆無訊號時，不呼叫 AI——
        // 安靜的期間沒有故事可講，直接寫固定結論。TrendAlerts 已含 SlowTrendAnalyzer 的慢速惡化告警，
        // 不需要另外重算一次「是否慢速上升」。
        if (!HasSignal(window))
        {
            Log.Info("{Date:yyyy-MM-dd} 體檢窗口內三層皆無訊號，閘門判定跳過 AI 敘事", checkupDate);
            return new WeeklyCheckupResult
            {
                CheckupDate = checkupDate,
                Completed = true,
                HasFindings = false,
                Conclusion = "本期無累積性異常，程式比對通過。"
            };
        }

        // 上次體檢結論可能落在窗口之外（體檢本身是週期性的，窗口內未必含上次體檢那天），
        // 用較寬的窗口另外找，抓不到就沒有延續性脈絡可帶入，不當錯誤處理
        var lastCheckup = _historyReader.ReadRecent(checkupDate, Math.Max(21, intervalDays * 3))
            .Where(d => d.WeeklyCheckup != null)
            .OrderByDescending(d => d.Date)
            .Select(d => d.WeeklyCheckup)
            .FirstOrDefault();

        // prompt 已在組裝時做輸入塑形（每簽章一行、40 行上限）控制在小模型可負擔範圍；
        // 送出前的 context 預算防線由 AIService.ChatAsync 統一負責（所有呼叫共用），這裡不重複檢查
        var prompt = BuildPrompt(checkupDate, window, serverDescription, lastCheckup);

        var result = await _aiService.ChatJsonAsync<WeeklyCheckupAiResult>(prompt, SystemPrompt,
            validate: r => r.Conclusion.Length > 0, maxTokens: MaxOutputTokens, label: $"checkup-{checkupDate:yyyyMMdd}");

        var outcome = new WeeklyCheckupResult { CheckupDate = checkupDate };

        if (!result.Success)
        {
            // AI 失敗不算「已完成的體檢」——呼叫端不寫入歷史，讓下次執行的補跑機制重試，
            // 而不是消耗掉這一期的體檢額度（違背「排程/AI 失敗時自動補跑」的設計意圖）
            outcome.Completed = false;
            outcome.HasFindings = false;
            outcome.Conclusion = $"（體檢 AI 呼叫未成功：{result.Error}）";
            Log.Warn("{Date:yyyy-MM-dd} 體檢 AI 呼叫失敗，本次不寫入歷史、下次執行將重試：{Error}", checkupDate, result.Error);
            return outcome;
        }

        // 閘門已判定窗口內有訊號才會走到這裡，HasFindings 定義上必為 true——
        // 不再讓 AI 額外判斷「有沒有發現」，避免與確定性閘門的結論互相矛盾
        outcome.HasFindings = true;
        outcome.Conclusion = result.Value!.Conclusion;

        // 檔名沿用既有 "_週檢.txt" 慣例（docs/PLAN.md 已承諾「輸出不變」），內部語意雖已從
        // 固定星期改為 due-date 輪巡，但對外的檔案格式與既有部署/查閱習慣不需要跟著變動
        var fileName = $"{checkupDate:yyyy-MM-dd}_週檢.txt";
        var activeSuppressions = _suppressionStore != null
            ? SuppressionFilter.ActiveForHost(_suppressionStore.LoadAll(), Environment.MachineName, DateTime.Now)
            : new List<RuleSuppression>();
        var content = BuildReportText(checkupDate, window, outcome, activeSuppressions);
        outcome.ReportFile = await _reportSink.WriteAsync(ReportKind.WeeklyCheckup, host, fileName, content);

        Log.Info("{Date:yyyy-MM-dd} 體檢完成：有發現={HasFindings}", checkupDate, outcome.HasFindings);
        return outcome;
    }

    /// <summary>窗口內任一天有風險、趨勢異常或關聯訊號，即視為「有訊號」——閘門判定的唯一依據</summary>
    private static bool HasSignal(List<DailyAnalysisRecord> window) =>
        window.Any(d => d.RiskLevel != "低" || d.TrendAlerts.Count > 0 || d.CorrelationAlerts.Count > 0);

    private static string BuildPrompt(DateTime checkupDate, List<DailyAnalysisRecord> window, string serverDescription,
        WeeklyCheckupResult? lastCheckup)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"以下是 Windows Server 在 {checkupDate:yyyy-MM-dd} 為止最近 {window.Count} 天的每日分析統計" +
                      "（程式已確定這段期間內有風險日、趨勢異常或關聯訊號），請做期間回顧，" +
                      "把這些已確定的訊號接續之前的脈絡寫成一段給人看的回顧文字。");

        if (serverDescription.Length > 0)
        {
            sb.AppendLine($"【伺服器環境】{serverDescription}");
        }

        if (lastCheckup != null)
        {
            sb.AppendLine();
            sb.AppendLine($"【上次體檢結論】（{lastCheckup.CheckupDate:yyyy-MM-dd}）{Truncate(lastCheckup.Conclusion, MaxLastConclusionChars)}");
        }

        sb.AppendLine();
        sb.AppendLine("【每日風險與摘要】");
        foreach (var day in window)
        {
            sb.AppendLine($"- {day.Date:MM-dd}：風險{day.RiskLevel}，錯誤{day.ErrorCount} 警告{day.WarningCount} 稽核{day.AuditEventCount}" +
                          (day.Summary.Length > 0 ? $"，結論：{Truncate(day.Summary, 60)}" : ""));
        }

        // 每個簽章一行，含窗口內逐日次數——彙整由程式先算好（確定性計算），AI 只需解讀不需自己加總
        var signatureLines = BuildSignatureLines(window);
        if (signatureLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【本期各問題簽章的逐日次數】（依嚴重度排序，程式已彙整，只需解讀趨勢）");
            foreach (var line in signatureLines.Take(MaxSignatureLines))
            {
                sb.AppendLine($"- {line}");
            }
            if (signatureLines.Count > MaxSignatureLines)
            {
                sb.AppendLine($"（另有 {signatureLines.Count - MaxSignatureLines} 種較低嚴重度的簽章未逐項列出）");
            }
        }

        sb.AppendLine();
        sb.AppendLine("請只回傳一個 JSON 物件（不要 markdown 圍欄、不要任何其他文字），結構如下：");
        sb.AppendLine("""
{
  "conclusion": "一段話回顧這段期間的狀況：接續上次體檢說要觀察的事後來如何，並指出本期是否有正在累積或惡化的訊號"
}
""");
        return sb.ToString();
    }

    /// <summary>
    /// 依 (LogName, Source, EventId) 彙整窗口內的逐日次數陣列——這是純算術（加總、排序），
    /// 由程式做掉，不該指望模型在腦中把多天的數字兜起來。
    /// </summary>
    private static List<string> BuildSignatureLines(List<DailyAnalysisRecord> window)
    {
        var bySignature = new Dictionary<(string LogName, string Source, int EventId),
            (IssueSeverity Severity, string? KnownIssue, Dictionary<DateTime, int> Daily)>();

        foreach (var day in window)
        {
            foreach (var issue in day.TopIssues)
            {
                var key = (issue.LogName, issue.Source, issue.EventId);
                if (!bySignature.TryGetValue(key, out var entry))
                {
                    entry = (issue.Severity, issue.KnownIssue, new Dictionary<DateTime, int>());
                }
                entry.Daily[day.Date] = issue.Count;
                if (issue.Severity > entry.Severity)
                {
                    entry = (issue.Severity, issue.KnownIssue ?? entry.KnownIssue, entry.Daily);
                }
                bySignature[key] = entry;
            }
        }

        return bySignature
            .OrderByDescending(kv => kv.Value.Severity)
            .ThenByDescending(kv => kv.Value.Daily.Values.Sum())
            .Select(kv =>
            {
                var dailyCounts = window.Select(d => kv.Value.Daily.TryGetValue(d.Date, out var c) ? c : 0).ToList();
                var trendNote = IsRisingAcrossWindow(dailyCounts) ? "（期內持續上升）" : "";
                var known = kv.Value.KnownIssue != null ? $"：{kv.Value.KnownIssue}" : "";
                return $"[{kv.Value.Severity}] {kv.Key.Source} EventId {kv.Key.EventId} 期內逐日：{string.Join(",", dailyCounts)}{trendNote}{known}";
            })
            .ToList();
    }

    /// <summary>簡單的粗略判定（給 AI 一個提示字樣，不是精確統計檢定；權威判定由 SlowTrendAnalyzer 負責）：
    /// 後半窗口總量明顯高於前半窗口</summary>
    private static bool IsRisingAcrossWindow(List<int> dailyCounts)
    {
        if (dailyCounts.Count < 4)
        {
            return false;
        }

        int mid = dailyCounts.Count / 2;
        int first = dailyCounts.Take(mid).Sum();
        int second = dailyCounts.Skip(mid).Sum();
        return second >= Math.Max(3, (int)(first * 1.5));
    }

    private static string BuildReportText(DateTime checkupDate, List<DailyAnalysisRecord> window, WeeklyCheckupResult outcome,
        List<RuleSuppression> activeSuppressions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine($"  LogForesight 體檢  {checkupDate:yyyy-MM-dd}");
        sb.AppendLine($"  產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine("■ 本期回顧結論");
        sb.AppendLine($"  {outcome.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("■ 本期每日概況");
        foreach (var day in window)
        {
            sb.AppendLine($"  {day.Date:yyyy-MM-dd}：風險{day.RiskLevel}，錯誤{day.ErrorCount} 警告{day.WarningCount} 稽核{day.AuditEventCount}");
        }

        // 固定列出生效中的抑制設定＋本期發生次數：防止「暫時關掉」變成永久盲區
        // （見 docs/RULES-PLAN.md 陷阱 4）——只要體檢確實有產生報告，這個提醒就一定在
        if (activeSuppressions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("■ 生效中的抑制設定（提醒：暫時關閉通知的告警，本期仍照常偵測與記錄）");
            foreach (var s in activeSuppressions)
            {
                int occurrences = window.SelectMany(d => d.TopIssues)
                    .Where(i => i.RuleId != null && i.RuleId.Equals(s.RuleId, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => i.Count);
                var expiry = s.ExpiresAt == null ? "永久" : $"至 {s.ExpiresAt:yyyy-MM-dd}";
                sb.AppendLine($"  - {s.RuleId}（{expiry}）：本期共發生 {occurrences} 次｜原因：{s.Reason}");
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private class WeeklyCheckupAiResult
    {
        [JsonPropertyName("conclusion")]
        public string Conclusion { get; set; } = string.Empty;
    }
}
