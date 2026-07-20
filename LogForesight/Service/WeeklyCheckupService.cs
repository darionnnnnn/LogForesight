using System.Text;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 每週體檢：獨立於每日分析的「週對週」回顧，找出單看每天都不明顯、但整週合起來是
/// 持續累積或緩慢惡化的訊號（補「慢速趨勢躲在每日 2 倍門檻下」的盲點——攻擊者或硬體劣化
/// 若採取每天小幅加量的節奏，會一直躲在 TrendAnalyzer 的單日比對之下，但週對週看得出斜線）。
/// 輸入在程式端先做週彙整（每簽章一行的逐日次數陣列），不是把 7 天 history 原樣塞給模型，
/// 控制 prompt 在小模型（context 20480）可負擔的範圍內。
/// </summary>
public class WeeklyCheckupService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int MaxSignatureLines = 40;
    private const int MaxLastConclusionChars = 300;
    private const int MaxOutputTokens = 1536;

    private const string SystemPrompt =
        "你是資深 Windows Server 維運與資安分析師，這次請做「週對週」的回顧比對，" +
        "找出單看每一天都不明顯、但整週合起來看是持續累積或緩慢惡化的訊號。" +
        "只根據使用者提供的資料判斷，不要臆測資料中不存在的事件。全程使用繁體中文。" +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出一個符合使用者指定結構的 JSON 物件。";

    private readonly AIService _aiService;
    private readonly IAnalysisRecordReader _historyReader;
    private readonly IReportSink _reportSink;

    public WeeklyCheckupService(AIService aiService, IAnalysisRecordReader historyReader, IReportSink reportSink)
    {
        _aiService = aiService;
        _historyReader = historyReader;
        _reportSink = reportSink;
    }

    /// <summary>
    /// 是否該執行本次週體檢：到了設定的星期幾，或距上次體檢已超過 7 天
    /// （涵蓋週末停機、排程失敗導致錯過的補跑；尚無任何分析紀錄時不執行，沒有基準可回顧）。
    /// </summary>
    public bool ShouldRun(DateTime today, DayOfWeek checkupDay)
    {
        if (_historyReader.ReadRecent(1).Count == 0)
        {
            return false;
        }

        var last = _historyReader.LastWeeklyCheckupDate();
        return today.DayOfWeek == checkupDay || last == null || (today.Date - last.Value.Date).TotalDays > 7;
    }

    /// <param name="host">主機識別，單機情境留空即可</param>
    public async Task<WeeklyCheckupResult> RunAsync(DateTime checkupDate, string serverDescription = "", string host = "")
    {
        var week = _historyReader.ReadRecent(7);

        // 上次體檢結論可能落在 7 天統計窗口之外（體檢本身是週期性的，窗口內未必含上次體檢那天），
        // 用較寬的窗口另外找，抓不到就沒有延續性脈絡可帶入，不當錯誤處理
        var lastCheckup = _historyReader.ReadRecent(21)
            .Where(d => d.WeeklyCheckup != null)
            .OrderByDescending(d => d.Date)
            .Select(d => d.WeeklyCheckup)
            .FirstOrDefault();

        // prompt 已在組裝時做輸入塑形（每簽章一行、40 行上限）控制在小模型可負擔範圍；
        // 送出前的 context 預算防線由 AIService.ChatAsync 統一負責（所有呼叫共用），這裡不重複檢查
        var prompt = BuildPrompt(checkupDate, week, serverDescription, lastCheckup);

        var result = await _aiService.ChatJsonAsync<WeeklyCheckupAiResult>(prompt, SystemPrompt,
            validate: r => r.Conclusion.Length > 0, maxTokens: MaxOutputTokens, label: $"weekly-{checkupDate:yyyyMMdd}");

        var outcome = new WeeklyCheckupResult { CheckupDate = checkupDate };

        if (!result.Success)
        {
            // AI 失敗不算「已完成的體檢」——呼叫端不寫入歷史，讓下次執行的補跑機制重試，
            // 而不是消耗掉這一週的體檢額度（違背「排程/AI 失敗時自動補跑」的設計意圖）
            outcome.Completed = false;
            outcome.HasFindings = false;
            outcome.Conclusion = $"（週體檢 AI 呼叫未成功：{result.Error}）";
            Log.Warn("{Date:yyyy-MM-dd} 週體檢 AI 呼叫失敗，本次不寫入歷史、下次執行將重試：{Error}", checkupDate, result.Error);
            return outcome;
        }

        outcome.HasFindings = result.Value!.HasFindings;
        outcome.Conclusion = result.Value.Conclusion;

        if (outcome.HasFindings)
        {
            var fileName = $"{checkupDate:yyyy-MM-dd}_週檢.txt";
            var content = BuildReportText(checkupDate, week, outcome);
            outcome.ReportFile = await _reportSink.WriteAsync(ReportKind.WeeklyCheckup, host, fileName, content);
        }

        Log.Info("{Date:yyyy-MM-dd} 週體檢完成：有發現={HasFindings}", checkupDate, outcome.HasFindings);
        return outcome;
    }

    private static string BuildPrompt(DateTime checkupDate, List<DailyAnalysisRecord> week, string serverDescription,
        WeeklyCheckupResult? lastCheckup)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"以下是 Windows Server 在 {checkupDate:yyyy-MM-dd} 為止最近 {week.Count} 天的每日分析統計，請做週對週回顧，" +
                      "找出單看每天都不明顯、但整週合起來是持續累積或緩慢惡化的訊號" +
                      "（例如每天都低於平常告警門檻，但整週合計是穩定上升的斜線）。");

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
        foreach (var day in week)
        {
            sb.AppendLine($"- {day.Date:MM-dd}：風險{day.RiskLevel}，錯誤{day.ErrorCount} 警告{day.WarningCount} 稽核{day.AuditEventCount}" +
                          (day.Summary.Length > 0 ? $"，結論：{Truncate(day.Summary, 60)}" : ""));
        }

        // 每個簽章一行，含週內逐日次數——週彙整由程式先算好（確定性計算），AI 只需解讀不需自己加總
        var signatureLines = BuildSignatureLines(week);
        if (signatureLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【本週各問題簽章的逐日次數】（依嚴重度排序，程式已彙整，只需解讀趨勢）");
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
  "has_findings": true 或 false（本週是否有單看每天都不明顯、但週對週值得額外提出的發現）,
  "conclusion": "一到三句話說明本週回顧結論；has_findings 為 false 時可簡述「本週無累積性異常」"
}
""");
        return sb.ToString();
    }

    /// <summary>
    /// 依 (LogName, Source, EventId) 彙整整週的逐日次數陣列——這是純算術（加總、排序），
    /// 由程式做掉，不該指望模型在腦中把 7 天的數字兜起來。
    /// </summary>
    private static List<string> BuildSignatureLines(List<DailyAnalysisRecord> week)
    {
        var bySignature = new Dictionary<(string LogName, string Source, int EventId),
            (IssueSeverity Severity, string? KnownIssue, Dictionary<DateTime, int> Daily)>();

        foreach (var day in week)
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
                var dailyCounts = week.Select(d => kv.Value.Daily.TryGetValue(d.Date, out var c) ? c : 0).ToList();
                var trendNote = IsRisingAcrossWeek(dailyCounts) ? "（週內持續上升）" : "";
                var known = kv.Value.KnownIssue != null ? $"：{kv.Value.KnownIssue}" : "";
                return $"[{kv.Value.Severity}] {kv.Key.Source} EventId {kv.Key.EventId} 週內逐日：{string.Join(",", dailyCounts)}{trendNote}{known}";
            })
            .ToList();
    }

    /// <summary>簡單的粗略判定（給 AI 一個提示字樣，不是精確統計檢定）：後半週總量明顯高於前半週</summary>
    private static bool IsRisingAcrossWeek(List<int> dailyCounts)
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

    private static string BuildReportText(DateTime checkupDate, List<DailyAnalysisRecord> week, WeeklyCheckupResult outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine($"  LogForesight 每週體檢  {checkupDate:yyyy-MM-dd}");
        sb.AppendLine($"  產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine("■ 本週回顧結論");
        sb.AppendLine($"  {outcome.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("■ 本週每日概況");
        foreach (var day in week)
        {
            sb.AppendLine($"  {day.Date:yyyy-MM-dd}：風險{day.RiskLevel}，錯誤{day.ErrorCount} 警告{day.WarningCount} 稽核{day.AuditEventCount}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private class WeeklyCheckupAiResult
    {
        [JsonPropertyName("has_findings")]
        public bool HasFindings { get; set; }

        [JsonPropertyName("conclusion")]
        public string Conclusion { get; set; } = string.Empty;
    }
}
