using System.Text;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 風險日報告：當日風險等級「中」以上時輸出報告檔，讓使用者聚焦問題點。
/// 報告依問題類別分區塊（儲存裝置、硬體、安全、服務、備份、設定、資源），
/// 每個類別發一次獨立的深入分析呼叫——類別內的事件彼此相關該一起看
/// （如 disk/Ntfs/storahci 常是同一顆硬碟的故事），跨類別的整合判讀已由主分析完成，
/// 各類別結果並列呈現、不需合併結論，所以分開呼叫沒有整合問題。
/// 檔名標注當日發現的類別：export/2026-07-15_儲存裝置+安全.txt。
/// 一天一個檔案；無風險的日期不產生檔案。
/// </summary>
public class RiskReportService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>全檔原始 log 的總預算，平均分配給各類別（每類別至少 3 筆）</summary>
    private const int MaxRawLogsPerReport = 20;
    private const int MinRawLogsPerCategory = 3;
    private const int MaxIssuesPerCategory = 4;

    private const string DeepDiveSystemPrompt =
        "你是資深 Windows Server 維運與資安分析師。請針對已確認的問題深入分析可能原因、影響與處置方式，" +
        "只根據提供的資料判斷，不要臆測資料中不存在的事件。全程使用繁體中文。" +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出符合使用者指定結構的 JSON 物件。";

    private readonly AIService _aiService;
    private readonly string _exportDir;

    public RiskReportService(AIService aiService, string? exportDir = null)
    {
        _aiService = aiService;
        // 輸出到執行檔所在目錄下的 export（排程執行時 CurrentDirectory 可能是 system32，不可靠）
        _exportDir = exportDir ?? Path.Combine(AppContext.BaseDirectory, "export");
    }

    /// <summary>產生風險報告檔，回傳檔案完整路徑</summary>
    public async Task<string> GenerateAsync(DailyAnalysisRecord record, List<EventLogEntryData> logs, string serverDescription = "")
    {
        Directory.CreateDirectory(_exportDir);

        var focusIssues = SelectFocusIssues(record.TopIssues);

        // 依類別分組，嚴重度最高的類別排最前面
        var groups = focusIssues
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Max(i => i.Severity))
            .ThenByDescending(g => g.Sum(i => i.Count))
            .ToList();

        Log.Info("開始產生 {Date:yyyy-MM-dd} 風險報告：風險={Risk}, 重點類別={CategoryCount}",
            record.Date, record.RiskLevel, groups.Count);

        int logQuotaPerCategory = Math.Max(MinRawLogsPerCategory, MaxRawLogsPerReport / Math.Max(1, groups.Count));

        var sections = new List<CategorySection>();
        foreach (var group in groups)
        {
            var issues = group
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Count)
                .Take(MaxIssuesPerCategory)
                .ToList();

            var categoryLogs = SelectRawLogs(logs, issues, logQuotaPerCategory);

            // 每類別一次獨立深入分析呼叫（主分析摘要作為全局脈絡帶入，跨類別資訊不遺失）
            var deepDive = record.AiAnalyzed
                ? await DeepDiveAsync(record, group.Key, issues, categoryLogs, serverDescription)
                : null;

            if (record.AiAnalyzed && deepDive == null)
            {
                Log.Warn("{Date:yyyy-MM-dd} 【{Category}】深入分析失敗或無法解析，該區塊將標注從缺", record.Date, group.Key);
            }

            sections.Add(new CategorySection(group.Key, issues, categoryLogs, deepDive));
        }

        var path = Path.Combine(_exportDir, BuildFileName(record.Date, record.RiskLevel, sections));
        await File.WriteAllTextAsync(path, BuildReport(record, sections), Encoding.UTF8);
        Log.Info("風險報告已寫入：{Path}", path);
        return path;
    }

    /// <summary>類別的中文顯示名稱（區塊標題與檔名共用）</summary>
    internal static string CategoryZh(IssueCategory category) => category switch
    {
        IssueCategory.Storage => "儲存裝置",
        IssueCategory.Hardware => "硬體",
        IssueCategory.Security => "安全",
        IssueCategory.Service => "服務",
        IssueCategory.Resource => "資源",
        IssueCategory.Backup => "備份",
        IssueCategory.Config => "設定",
        _ => "其他"
    };

    /// <summary>
    /// 檔名：日期＋風險等級＋當日發現的類別，如 2026-07-15_高風險_儲存裝置+安全.txt。
    /// 風險等級緊接在日期之後，讓使用者列出 export 目錄時不用打開檔案就能一眼看出重要性；
    /// 「高風險」的中文排序也剛好在「中風險」之前，同一天多檔並列時更醒目。
    /// </summary>
    private static string BuildFileName(DateTime date, string riskLevel, List<CategorySection> sections)
    {
        var categories = sections.Select(s => CategoryZh(s.Category)).Distinct().ToList();
        var categorySuffix = categories.Count > 0 ? "_" + string.Join("+", categories) : "";
        return $"{date:yyyy-MM-dd}_{riskLevel}風險{categorySuffix}.txt";
    }

    /// <summary>挑出值得深入分析的重點問題：High 以上、頻率上升、或首次出現的 Medium 以上</summary>
    private static List<LogIssueSignature> SelectFocusIssues(List<LogIssueSignature> issues)
    {
        var focus = issues
            .Where(i => i.Severity >= IssueSeverity.High
                        || i.Trend == IssueTrend.Rising
                        || (i.Trend == IssueTrend.New && i.Severity >= IssueSeverity.Medium))
            .ToList();

        // 風險「中」可能來自頻率異常等總量訊號，沒有單一重點事件時取前幾名供參
        return focus.Count > 0 ? focus : issues.Take(3).ToList();
    }

    /// <summary>依問題分配額度挑選原始 log，避免單一高頻事件佔滿名額</summary>
    private static List<EventLogEntryData> SelectRawLogs(List<EventLogEntryData> logs, List<LogIssueSignature> issues, int maxTotal)
    {
        if (issues.Count == 0)
        {
            return new List<EventLogEntryData>();
        }

        int quota = Math.Max(2, maxTotal / issues.Count);
        var selected = new List<EventLogEntryData>();

        foreach (var issue in issues)
        {
            selected.AddRange(logs.Where(l =>
                    l.LogName == issue.LogName && l.Source == issue.Source &&
                    l.EventId == issue.EventId && l.EntryType == issue.EntryType)
                .Take(quota));
        }

        return selected.OrderBy(l => l.TimeGenerated).Take(maxTotal).ToList();
    }

    private async Task<DeepDiveResult?> DeepDiveAsync(DailyAnalysisRecord record, IssueCategory category,
        List<LogIssueSignature> issues, List<EventLogEntryData> rawLogs, string serverDescription)
    {
        var sb = new StringBuilder();

        if (serverDescription.Length > 0)
        {
            sb.AppendLine($"【伺服器環境】{serverDescription}");
        }

        sb.AppendLine($"{record.Date:yyyy-MM-dd} 的每日分析已判定風險等級「{record.RiskLevel}」。" +
                      $"本次請聚焦【{CategoryZh(category)}】類別的問題，逐一深入分析可能原因與處置方式。");
        sb.AppendLine();
        sb.AppendLine($"【全局脈絡】（跨類別的每日分析結論，供參考）{record.Summary}" +
                      (record.TrendAssessment.Length > 0 ? $" 趨勢：{record.TrendAssessment}" : ""));
        sb.AppendLine();
        sb.AppendLine($"【{CategoryZh(category)}類別的重點問題】");
        foreach (var issue in issues)
        {
            sb.AppendLine(FormatIssue(issue));
        }

        if (rawLogs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【相關原始 log】（依時間排序，可觀察事件先後順序與關聯）");
            foreach (var log in rawLogs)
            {
                sb.AppendLine(FormatRawLog(log, maxMessageLength: 300));
            }
        }

        sb.AppendLine();
        sb.AppendLine("請只回傳一個 JSON 物件（不要任何其他文字），每個重點問題一則分析、依嚴重程度排序：");
        sb.AppendLine("""
{
  "analyses": [
    {
      "problem": "問題簡述",
      "likely_causes": ["可能原因，依可能性高低排序"],
      "impact": "不處理會發生什麼",
      "next_steps": ["具體的調查或處置步驟"]
    }
  ]
}
""");

        var result = await _aiService.ChatJsonAsync<DeepDiveResult>(sb.ToString(), DeepDiveSystemPrompt);
        return result.Value;
    }

    private static string BuildReport(DailyAnalysisRecord record, List<CategorySection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine($"  LogForesight 風險報告  {record.Date:yyyy-MM-dd}    風險等級：{record.RiskLevel}");
        sb.AppendLine($"  問題類別：{(sections.Count > 0 ? string.Join("、", sections.Select(s => CategoryZh(s.Category))) : "（無重點類別）")}");
        sb.AppendLine($"  產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════════════════");

        // 整體摘要：跨類別的每日分析結論（風險等級的依據）
        sb.AppendLine();
        sb.AppendLine("■ 整體摘要");
        sb.AppendLine($"  {record.Summary}");
        if (record.TrendAssessment.Length > 0)
        {
            sb.AppendLine($"  趨勢：{record.TrendAssessment}");
        }
        foreach (var alert in record.CorrelationAlerts)
        {
            sb.AppendLine($"  🔗 {alert}");
        }
        foreach (var alert in record.TrendAlerts)
        {
            sb.AppendLine($"  ⚠ {alert}");
        }
        for (int i = 0; i < record.Recommendations.Count; i++)
        {
            sb.AppendLine($"  建議 {i + 1}：{record.Recommendations[i]}");
        }
        sb.AppendLine();

        // 各類別區塊：問題清單 → 該類別的 AI 深入分析 → 該類別的原始 log
        foreach (var section in sections)
        {
            sb.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"■【{CategoryZh(section.Category)}】重點問題 {section.Issues.Count} 項");
            sb.AppendLine();
            foreach (var issue in section.Issues)
            {
                sb.AppendLine(FormatIssue(issue));
            }

            sb.AppendLine();
            sb.AppendLine($"  ── AI 深入分析（{CategoryZh(section.Category)}） ──");
            if (section.DeepDive == null || section.DeepDive.Analyses.Count == 0)
            {
                sb.AppendLine("  （AI 深入分析未能執行：模型未啟動、呼叫失敗或回覆無法解析）");
            }
            else
            {
                for (int i = 0; i < section.DeepDive.Analyses.Count; i++)
                {
                    var item = section.DeepDive.Analyses[i];
                    sb.AppendLine($"  {i + 1}. {item.Problem}");
                    if (item.LikelyCauses.Count > 0)
                    {
                        sb.AppendLine("     可能原因：");
                        foreach (var cause in item.LikelyCauses)
                        {
                            sb.AppendLine($"       - {cause}");
                        }
                    }
                    if (item.Impact.Length > 0)
                    {
                        sb.AppendLine($"     影響：{item.Impact}");
                    }
                    if (item.NextSteps.Count > 0)
                    {
                        sb.AppendLine("     建議步驟：");
                        foreach (var step in item.NextSteps)
                        {
                            sb.AppendLine($"       - {step}");
                        }
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine($"  ── 相關原始 Log（{section.Logs.Count} 筆，供人工比對） ──");
            if (section.Logs.Count == 0)
            {
                sb.AppendLine("  （無對應的原始 log）");
            }
            foreach (var log in section.Logs)
            {
                sb.AppendLine(FormatRawLog(log, maxMessageLength: 500));
            }
            sb.AppendLine();
        }

        // 前置掃描結果：主分析前已由獨立 AI 呼叫篩選過的低嚴重度項目
        if (record.ScreenedTailCount > 0)
        {
            sb.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"■ 前置掃描（主分析篇幅外的 {record.ScreenedTailCount} 項較低嚴重度事件，已由獨立 AI 呼叫先行篩選）");
            if (record.ScreeningNotes.Count == 0)
            {
                sb.AppendLine("  AI 檢視後未發現隱藏異常，皆屬一般事件。");
            }
            foreach (var note in record.ScreeningNotes)
            {
                sb.AppendLine($"  - {note}");
            }
        }

        return sb.ToString();
    }

    private static string FormatIssue(LogIssueSignature i)
    {
        var sb = new StringBuilder();
        sb.Append($"- [{i.Severity}] {i.LogName}/{i.Source} EventId {i.EventId} x{i.Count}（{i.FirstSeen}~{i.LastSeen}）");
        if (i.KnownIssue != null)
        {
            sb.Append($"：{i.KnownIssue}");
        }
        sb.AppendLine();
        sb.Append($"  趨勢：{TrendZh(i)}");
        if (i.KeyDetails != null)
        {
            sb.AppendLine();
            sb.Append($"  {i.KeyDetails}");
        }
        return sb.ToString();
    }

    private static string TrendZh(LogIssueSignature i) => i.Trend switch
    {
        IssueTrend.New => "首次出現，近期歷史中從未發生",
        IssueTrend.Rising => $"頻率上升（昨日 x{i.PreviousDayCount?.ToString() ?? "-"}、近期平均 x{i.HistoryDailyAverage}）",
        IssueTrend.Recurring => $"重複出現（近期 {i.DaysSeenInHistory} 天有發生、平均 x{i.HistoryDailyAverage}）",
        IssueTrend.Declining => "頻率下降",
        _ => "無歷史可比對"
    };

    private static string FormatRawLog(EventLogEntryData log, int maxMessageLength)
    {
        var type = (int)log.EntryType == 0 ? "Critical" : log.EntryType.ToString();
        var message = string.Join(' ', log.Message.Split('\r', '\n', '\t').Where(p => p.Length > 0));
        if (message.Length > maxMessageLength)
        {
            message = message[..maxMessageLength] + "...";
        }
        return $"[{log.TimeGenerated:HH:mm:ss}] {log.LogName}/{log.Source} #{log.EventId} ({type})\n    {message}";
    }

    /// <summary>單一類別的報告區塊素材</summary>
    private record CategorySection(
        IssueCategory Category,
        List<LogIssueSignature> Issues,
        List<EventLogEntryData> Logs,
        DeepDiveResult? DeepDive);

    /// <summary>深入分析呼叫的 JSON 契約</summary>
    private class DeepDiveResult
    {
        [JsonPropertyName("analyses")]
        public List<DeepDiveItem> Analyses { get; set; } = new();
    }

    private class DeepDiveItem
    {
        [JsonPropertyName("problem")]
        public string Problem { get; set; } = string.Empty;

        [JsonPropertyName("likely_causes")]
        public List<string> LikelyCauses { get; set; } = new();

        [JsonPropertyName("impact")]
        public string Impact { get; set; } = string.Empty;

        [JsonPropertyName("next_steps")]
        public List<string> NextSteps { get; set; } = new();
    }
}
