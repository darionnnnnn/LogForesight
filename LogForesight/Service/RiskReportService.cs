using System.Text;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 風險日報告：當日風險等級「中」以上時輸出報告檔，讓使用者聚焦問題點。
/// 報告依問題類別分區塊（儲存裝置、硬體、安全、服務、備份、設定、資源）。
/// **處置參考的來源依類別分流**（2026-07-20 AI 角色轉換，見 docs/AI-ROLE-PLAN.md）：
/// 規則已命中的類別（Category ≠ Other）直接查 <see cref="KnownIssueCatalog"/> 的靜態知識庫，
/// 零 AI 呼叫、零延遲、零幻覺；只有 Other 類別（未命中規則、AI 唯一還需要判讀的地方）
/// 才發一次獨立的 AI 深入分析呼叫——類別內的事件彼此相關該一起看
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

    /// <summary>
    /// 深入分析 prompt 的字元硬上限（context 20480 token 的環境下，深入分析輸出保留 8192 token 後
    /// prompt 只剩約 10K token 空間，唯一貼近預算的呼叫）。異常情況下（如單筆事件訊息異常長）
    /// 沒有硬上限就可能爆 context；超出時從原始 log 區尾端截斷，問題清單與主分析摘要永不截斷。
    /// </summary>
    private const int MaxDeepDivePromptChars = 16 * 1024;

    private const string DeepDiveSystemPrompt =
        "你是資深 Windows Server 維運與資安分析師。請針對已確認的問題深入分析可能原因、影響與處置方式，" +
        "只根據提供的資料判斷，不要臆測資料中不存在的事件。" + PromptGuidelines.Language +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出符合使用者指定結構的 JSON 物件。";

    private readonly AIService _aiService;
    private readonly IReportSink _reportSink;
    private readonly int _deepDiveMaxTokens;

    public RiskReportService(AIService aiService, IReportSink reportSink, int deepDiveMaxTokens = 8192)
    {
        _aiService = aiService;
        _reportSink = reportSink;
        _deepDiveMaxTokens = deepDiveMaxTokens;
    }

    /// <summary>產生風險報告檔，回傳報告參照（今日為檔案完整路徑）</summary>
    /// <param name="host">主機識別，單機情境留空即可</param>
    /// <param name="activeSuppressions">本機現在生效中的抑制項目（含 Reason），用來在報告的
    /// 「已抑制的告警」區塊顯示原因；null/空清單時該區塊不輸出</param>
    public async Task<string> GenerateAsync(DailyAnalysisRecord record, List<EventLogEntryData> logs, string serverDescription = "",
        List<RuleSuppression>? activeSuppressions = null, string host = "")
    {
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

            // 規則已命中的類別直接查表渲染靜態知識庫內容，零 AI 呼叫（見 docs/AI-ROLE-PLAN.md）；
            // 只有 Other（未命中規則、AI 唯一還需要判讀的地方）才發一次深入分析呼叫
            var outcome = group.Key == IssueCategory.Other
                ? (record.AiAnalyzed
                    ? await DeepDiveAsync(record, group.Key, issues, categoryLogs, serverDescription)
                    : new DeepDiveOutcome(null, false, 0, categoryLogs.Count))
                : BuildStaticOutcome(issues, categoryLogs.Count);

            if (group.Key == IssueCategory.Other && record.AiAnalyzed && outcome.Result == null)
            {
                Log.Warn("{Date:yyyy-MM-dd} 【{Category}】深入分析失敗或無法解析，該區塊將標注從缺", record.Date, group.Key);
            }

            // 結構化落地：與報告全文（下方 BuildReport）並存，供未來 DB/查詢直接讀欄位，不用反解析文字報告
            if (outcome.Result != null && outcome.Result.Analyses.Count > 0)
            {
                record.DeepDives.Add(new CategoryDeepDive
                {
                    Category = group.Key,
                    Findings = outcome.Result.Analyses.Select(a => new DeepDiveFinding
                    {
                        Problem = a.Problem,
                        LikelyCauses = a.LikelyCauses,
                        Impact = a.Impact,
                        NextSteps = a.NextSteps
                    }).ToList()
                });
            }

            sections.Add(new CategorySection(group.Key, issues, categoryLogs, outcome.Result, outcome.Truncated, outcome.IncludedLogs));
        }

        var fileName = BuildFileName(record.Date, record.RiskLevel, sections);
        var reportRef = await _reportSink.WriteAsync(ReportKind.DailyRisk, host, fileName, BuildReport(record, sections, activeSuppressions));
        Log.Info("風險報告已寫入：{Path}", reportRef.Value);
        return reportRef;
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

    /// <summary>
    /// 組 prompt 時把「頭部」（問題清單、全局脈絡——永不截斷）與「原始 log 區」分開組裝，
    /// 原始 log 依時間順序累加，一旦逼近 <see cref="MaxDeepDivePromptChars"/> 就停止累加、
    /// 不是整批塞入後才發現超標。問題清單與統計數字不受影響，只有佐證用的原始 log 可能被截斷。
    /// </summary>
    private async Task<DeepDiveOutcome> DeepDiveAsync(DailyAnalysisRecord record, IssueCategory category,
        List<LogIssueSignature> issues, List<EventLogEntryData> rawLogs, string serverDescription)
    {
        var head = new StringBuilder();

        if (serverDescription.Length > 0)
        {
            head.AppendLine($"【伺服器環境】{serverDescription}");
        }

        head.AppendLine($"{record.Date:yyyy-MM-dd} 的每日分析已判定風險等級「{record.RiskLevel}」。" +
                      $"本次請聚焦【{CategoryZh(category)}】類別的問題，逐一深入分析可能原因與處置方式。");
        head.AppendLine();
        head.AppendLine($"【全局脈絡】（跨類別的每日分析結論，供參考）{record.Summary}" +
                      (record.TrendAssessment.Length > 0 ? $" 趨勢：{record.TrendAssessment}" : ""));
        head.AppendLine();
        head.AppendLine($"【{CategoryZh(category)}類別的重點問題】");
        foreach (var issue in issues)
        {
            head.AppendLine(FormatIssue(issue));
        }

        var footer = new StringBuilder();
        footer.AppendLine();
        footer.AppendLine("請只回傳一個 JSON 物件（不要任何其他文字），每個重點問題一則分析、依嚴重程度排序：");
        footer.AppendLine("""
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

        var logLines = rawLogs.Select(l => FormatRawLog(l, maxMessageLength: 300)).ToList();

        var sb = new StringBuilder(head.ToString());
        int included = logLines.Count;
        bool truncated = false;

        if (logLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【相關原始 log】（依時間排序，可觀察事件先後順序與關聯）");

            // 留給截斷註記與 footer 的餘裕，避免算到剛好卡在邊界
            int budget = MaxDeepDivePromptChars - head.Length - footer.Length - 200;
            int used = 0;
            included = 0;
            foreach (var line in logLines)
            {
                if (used + line.Length > budget)
                {
                    truncated = true;
                    break;
                }
                sb.AppendLine(line);
                used += line.Length;
                included++;
            }

            if (truncated)
            {
                sb.AppendLine($"（原始 log 因 prompt 長度上限已截斷，僅列出 {included}/{logLines.Count} 筆；問題清單與統計數字不受影響）");
                Log.Warn("{Date:yyyy-MM-dd}【{Category}】深入分析原始 log 因 {Limit} 字元上限截斷：{Included}/{Total} 筆",
                    record.Date, category, MaxDeepDivePromptChars, included, logLines.Count);
            }
        }
        sb.Append(footer);

        var result = await _aiService.ChatJsonAsync<DeepDiveResult>(sb.ToString(), DeepDiveSystemPrompt, maxTokens: _deepDiveMaxTokens,
            label: $"deepdive-{record.Date:yyyyMMdd}-{category}");
        return new DeepDiveOutcome(result.Value, truncated, included, logLines.Count);
    }

    /// <summary>
    /// 規則已命中的類別直接查 KnownIssueCatalog 的靜態知識庫渲染，不呼叫 AI：同一 Event ID 的
    /// 原因/處置幾乎不變，寫死比每次重新生成更快、更一致、零幻覺，AI 服務不可用時也不會從缺。
    /// 理論上該類別的每個問題都命中過規則（Category 正是規則分類來的），查不到規則屬防禦性情況，
    /// 略過該問題而非產生空白區塊。
    /// </summary>
    private static DeepDiveOutcome BuildStaticOutcome(List<LogIssueSignature> issues, int totalLogs)
    {
        var analyses = new List<DeepDiveItem>();
        foreach (var issue in issues)
        {
            var rule = KnownIssueCatalog.FindRule(issue.Source, issue.EventId);
            if (rule == null || rule.PlainExplanation.Length == 0)
            {
                continue;
            }

            analyses.Add(new DeepDiveItem
            {
                Problem = rule.PlainExplanation,
                Impact = rule.Impact,
                LikelyCauses = rule.LikelyCauses.ToList(),
                NextSteps = rule.NextSteps.ToList()
            });
        }

        // 查表零延遲，原始 log 不受 prompt 篇幅限制，視為「全數已涵蓋」
        return new DeepDiveOutcome(new DeepDiveResult { Analyses = analyses }, false, totalLogs, totalLogs);
    }

    private static string BuildReport(DailyAnalysisRecord record, List<CategorySection> sections, List<RuleSuppression>? activeSuppressions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine($"  LogForesight 風險報告  {record.Date:yyyy-MM-dd}    風險等級：{record.RiskLevel}");
        sb.AppendLine($"  問題類別：{(sections.Count > 0 ? string.Join("、", sections.Select(s => CategoryZh(s.Category))) : "（無重點類別）")}");
        sb.AppendLine($"  產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════════════════");

        // 白話總覽：置頂，主管/非技術讀者看完這段即可結束（2026-07-20 AI 角色轉換，見 docs/AI-ROLE-PLAN.md）。
        // 技術細節（趨勢數字、關聯訊號、原始 log）全部保留在下方區塊，供維運人員查證。
        sb.AppendLine();
        sb.AppendLine("■ 白話總覽");
        if (record.Headline.Length > 0)
        {
            sb.AppendLine($"  {record.Headline}");
        }
        sb.AppendLine($"  {record.Summary}");
        if (record.Action.Length > 0)
        {
            sb.AppendLine($"  現在該做：{record.Action}");
        }
        sb.AppendLine();

        // 技術摘要：趨勢數字、關聯訊號、覆蓋率申報——供查證與後續調查
        sb.AppendLine("■ 技術摘要");
        if (record.UncoveredChecks.Count > 0)
        {
            sb.AppendLine("  ⚠ 本次未能檢查的項目（權限或來源限制，非「已檢查且無異常」）：");
            foreach (var check in record.UncoveredChecks)
            {
                sb.AppendLine($"    - {check}");
            }
        }
        if (record.DataIncomplete)
        {
            sb.AppendLine("  ⚠ 本日部分事件來源的保留歷史不足以涵蓋整天，統計數字可能偏低，非真實反映當日狀況。");
        }
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

            // Other 以外的類別已由規則命中，處置參考直接查靜態知識庫（零 AI 呼叫）；
            // 只有 Other 類別（未命中規則、AI 唯一還需要判讀的地方）才是真正的 AI 深入分析
            bool isStaticSection = section.Category != IssueCategory.Other;
            sb.AppendLine();
            sb.AppendLine(isStaticSection
                ? "  ── 處置參考（知識庫） ──"
                : $"  ── AI 深入分析（{CategoryZh(section.Category)}） ──");
            if (section.LogsTruncatedInPrompt)
            {
                sb.AppendLine($"  （原始 log 篇幅超出深入分析 prompt 上限，AI 僅參考其中 {section.LogsIncludedInPrompt}/{section.Logs.Count} 筆；" +
                              "下方「相關原始 Log」仍完整列出全部證據）");
            }
            if (section.DeepDive == null || section.DeepDive.Analyses.Count == 0)
            {
                sb.AppendLine(isStaticSection
                    ? "  （知識庫查無對應處置參考）"
                    : "  （AI 深入分析未能執行：模型未啟動、呼叫失敗或回覆無法解析）");
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

        // 已抑制的告警：本機維護者關閉通知的規則，仍列出讓看報告的人知道「有東西被關掉了」——
        // 偵測與紀錄照常，只是不吵、不拉風險（見 docs/RULES-PLAN.md 語意邊界）
        var suppressedIssues = record.TopIssues.Where(i => i.Suppressed).ToList();
        if (suppressedIssues.Count > 0)
        {
            sb.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"■ 已抑制的告警 {suppressedIssues.Count} 項（通知已關閉，偵測與紀錄照常）");
            foreach (var issue in suppressedIssues)
            {
                var reason = activeSuppressions?.FirstOrDefault(s =>
                    s.RuleId.Equals(issue.RuleId, StringComparison.OrdinalIgnoreCase))?.Reason;
                sb.AppendLine($"  - [{issue.Severity}] {issue.LogName}/{issue.Source} EventId {issue.EventId} x{issue.Count}" +
                              $"：{issue.KnownIssue}");
                sb.AppendLine($"    抑制原因：{reason ?? "（原因未知，可能是設定檔異動或匯入時未帶入）"}");
            }
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
        DeepDiveResult? DeepDive,
        bool LogsTruncatedInPrompt,
        int LogsIncludedInPrompt);

    /// <summary>DeepDiveAsync 的結果：分析內容 + 原始 log 是否因 prompt 上限被截斷</summary>
    private record DeepDiveOutcome(DeepDiveResult? Result, bool Truncated, int IncludedLogs, int TotalLogs);

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
