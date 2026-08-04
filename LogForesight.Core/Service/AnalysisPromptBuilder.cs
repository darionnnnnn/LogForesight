using System.Text;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Service;

/// <summary>
/// 每日分析主 prompt 的組裝，與前置掃描（Other 類尾巴超出呈現上限時，先分批請 AI 逐項篩選）。
/// 從 <see cref="LogAnalysisService"/> 抽出——這兩件事都是「餵給 AI 看什麼」的關注點，與
/// AnalyzeDayAsync 本體的聚合/規則判定/寫入歷史流程分開。
/// </summary>
internal class AnalysisPromptBuilder
{
    // ── prompt 呈現上限 ──────────────────────────────────────────
    // 歷史資料庫照存完整資訊，這裡只限制「進 prompt 的量」：異常大量的日子（如硬碟垂死、
    // 遭受攻擊）事件種類會暴增，不設上限的話 prompt 可膨脹到 25KB 以上，稀釋小模型注意力。
    // 列表皆已依嚴重度排序，被折疊的一定是相對不重要的項目，且統計數字不受影響。
    internal const int MaxFlaggedInPrompt = 12;  // 規則命中問題最多逐項列 12 個
    internal const int MaxOthersInPrompt = 10;   // 未命中規則事件最多逐項列 10 個
    private const int MaxTrendAlertsInPrompt = 15;

    /// <summary>前置掃描每批的項目數（每批一次獨立 AI 呼叫，prompt 約 5KB）</summary>
    private const int ScreeningChunkSize = 20;

    private const int FlaggedSampleCount = 2;    // 重點問題每項附 2 則範例訊息
    private const int OtherSampleCount = 1;      // 其他事件每項附 1 則

    /// <summary>
    /// 2026-07-20 AI 角色轉換（見 docs/archive/HISTORY.md）：AI 不再是判斷風險或找根因的分析引擎，
    /// 那些已由規則/趨勢/關聯三層與 KnownIssueCatalog 的靜態知識庫負責。AI 唯一的職責是把
    /// 這些已經算好的結論翻譯成不懂 Event Log 的人也能看懂的白話——risk_level 仍要填，但只作為
    /// 安全網（只能把風險往上拉，不能往下壓，見 RiskLevels.MoreSevere），不是重新判斷的依據。
    /// </summary>
    public const string SystemPrompt =
        "你是資深 Windows Server 維運與資安分析師，同時也是把技術判讀翻譯成白話的溝通者。" +
        "以下資料已由程式完成規則比對、趨勢分析與風險判定，你的工作分兩部分：" +
        "(1) 依專業判斷填寫 risk_level，但這只是輔助判斷、不會讓程式判定的風險等級降低；" +
        "(2) 把結論轉譯成不懂 Event Log 的管理者也能看懂的白話——不要引用 Event ID 或程式碼層級術語，" +
        "只根據使用者提供的資料撰寫，不要臆測資料中不存在的事件。" + PromptGuidelines.Language +
        "直接以 { 開始輸出，不要有任何前言、推理過程或說明文字，也不要使用 markdown code fence，" +
        "回覆的第一個字元必須是 {，只輸出一個符合使用者指定結構的 JSON 物件。";

    private readonly AIService _aiService;

    public AnalysisPromptBuilder(AIService aiService) => _aiService = aiService;

    /// <summary>
    /// 超出主 prompt 呈現上限的 Other 類項目（前置掃描的對象；與 BuildPrompt 的分界一致）。
    /// 2026-07-20 AI 角色轉換後限縮：規則已命中的尾巴不再掃描——靜態知識庫已涵蓋處置建議，
    /// 不需要 AI 逐項篩選；只有 Other 類（未命中規則）才是 AI 唯一還需要判讀新型態問題的地方
    /// （見 docs/archive/HISTORY.md），與 RiskReportService 深析限縮到 Other 類的原則一致。
    /// </summary>
    public static List<LogIssueSignature> GetTailIssues(List<LogIssueSignature> issues) =>
        issues.Where(i => i.KnownIssue == null).Skip(MaxOthersInPrompt).ToList();

    /// <summary>
    /// 前置掃描：分批請 AI 逐項篩選尾巴項目，只回報值得注意者。
    /// 批次之間彼此獨立（逐項判斷是否為雜訊不需要全局脈絡），所以可以安全拆分呼叫。
    /// </summary>
    public async Task<ScreeningOutcome> ScreenTailAsync(DateTime date, List<LogIssueSignature> tailIssues)
    {
        var outcome = new ScreeningOutcome();

        foreach (var chunk in tailIssues.Chunk(ScreeningChunkSize))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{date:yyyy-MM-dd} 的 Windows Server 事件種類較多，主分析前請先篩選以下較低嚴重度的事件。" +
                          "逐項判斷是否值得納入主分析（入侵跡象、故障前兆、不尋常的模式）；一般性雜訊不要列出。");
            sb.AppendLine();
            for (int i = 0; i < chunk.Length; i++)
            {
                var item = chunk[i];
                sb.AppendLine($"{i + 1}. [{item.Severity}] {item.LogName}/{item.Source} EventId {item.EventId} x{item.Count}" +
                              $"（{item.FirstSeen}~{item.LastSeen}）：" +
                              (item.KnownIssue != null ? $"{item.KnownIssue}；" : "") +
                              (item.SampleMessages.FirstOrDefault() ?? ""));
                if (item.KeyDetails != null)
                {
                    sb.AppendLine($"   {item.KeyDetails}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("請只回傳一個 JSON 物件（不要任何其他文字），no 為上列項目編號；全部屬一般雜訊時 notable 給空陣列：");
            sb.AppendLine("""{"notable": [{"no": 1, "reason": "為何值得注意"}]}""");

            var result = await _aiService.ChatJsonAsync<ScreeningResult>(sb.ToString(), SystemPrompt, label: $"screening-{date:yyyyMMdd}");
            var parsed = result.Value;

            if (parsed == null)
            {
                outcome.FailedCount += chunk.Length;
                continue;
            }

            int valid = 0;
            foreach (var n in parsed.Notable)
            {
                if (n.No >= 1 && n.No <= chunk.Length)
                {
                    outcome.Notable.Add((chunk[n.No - 1], n.Reason));
                    valid++;
                }
            }
            outcome.CleanCount += chunk.Length - valid;
        }

        return outcome;
    }

    public static string BuildPrompt(DateTime date, List<LogIssueSignature> issues,
        int errorCount, int warningCount, int auditCount, List<DailyAnalysisRecord> history,
        List<string> trendAlerts, List<CorrelationFinding> correlations, ScreeningOutcome? screening,
        bool dataIncomplete, List<string> uncoveredChecks, string serverDescription)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"以下是 Windows Server 在 {date:yyyy-MM-dd}（{WeekdayZh(date)}）的事件日誌摘要" +
                      "（已聚合統計，且已由程式完成規則比對、趨勢分析與風險判定）。" +
                      "請依這些資料給出風險等級判斷，並把結論轉譯成白話讓不懂技術的人也能理解，" +
                      "特別注意硬體故障前兆與入侵跡象。");

        if (serverDescription.Length > 0)
        {
            sb.AppendLine($"【伺服器環境】{serverDescription}");
        }

        if (uncoveredChecks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【本次未能檢查的項目】（權限或來源限制，非「已檢查且無異常」，判讀時請留意這是偵測盲區）");
            foreach (var check in uncoveredChecks)
            {
                sb.AppendLine($"- {check}");
            }
        }

        if (dataIncomplete)
        {
            sb.AppendLine();
            sb.AppendLine("【資料完整性提醒】本日部分事件來源的保留歷史不足以涵蓋整天，統計數字可能偏低，非真實反映當日狀況。");
        }

        sb.AppendLine();
        sb.AppendLine($"【當日統計】錯誤 {errorCount} 筆、警告 {warningCount} 筆、安全稽核事件 {auditCount} 筆");

        if (trendAlerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【程式比對出的頻率異常】（程式已用歷史次數確定性比對，這些不是猜測，請務必納入評估）");
            foreach (var alert in trendAlerts.Take(MaxTrendAlertsInPrompt))
            {
                sb.AppendLine($"- {alert}");
            }
            if (trendAlerts.Count > MaxTrendAlertsInPrompt)
            {
                sb.AppendLine($"（另有 {trendAlerts.Count - MaxTrendAlertsInPrompt} 項頻率異常未逐項列出）");
            }
        }

        if (correlations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【程式比對出的關聯訊號】（多個獨立事件的已知攻擊鏈/故障鏈組合，由程式確定性比對，" +
                          "不是猜測——這些關聯是本次分析最重要的線索，風險評估與趨勢解讀必須以此為核心）");
            foreach (var c in correlations)
            {
                sb.AppendLine($"- [{c.Severity}] {c.Description}");
            }
        }

        var flagged = issues.Where(i => i.KnownIssue != null).ToList();
        var others = issues.Where(i => i.KnownIssue == null).ToList();

        if (flagged.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【規則已標記的重點問題】（程式依已知危險事件比對，請優先評估）");
            foreach (var i in flagged.Take(MaxFlaggedInPrompt))
            {
                AppendIssue(sb, i, history.Count, flagged: true);
            }
            // 規則命中的尾巴不再前置掃描（2026-07-20 限縮，見 GetTailIssues）——靜態知識庫已涵蓋
            // 處置建議，這裡固定顯示折疊統計行，不像 Other 類尾巴有掃描結果可以彙報
            if (flagged.Count > MaxFlaggedInPrompt)
            {
                var folded = flagged.Skip(MaxFlaggedInPrompt).ToList();
                sb.AppendLine($"（另有 {folded.Count} 個嚴重度較低的規則命中問題共 {folded.Sum(i => i.Count)} 筆，未逐項列出——處置建議見報告的「處置參考（知識庫）」區塊）");
            }
        }

        if (others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【其他事件】（未命中已知規則，請判斷是否有新型態問題）");
            foreach (var i in others.Take(MaxOthersInPrompt))
            {
                AppendIssue(sb, i, history.Count, flagged: false);
            }
            if (others.Count > MaxOthersInPrompt && screening == null)
            {
                var folded = others.Skip(MaxOthersInPrompt).ToList();
                sb.AppendLine($"（另有 {folded.Count} 種其他事件共 {folded.Sum(i => i.Count)} 筆，未逐項列出）");
            }
        }

        if (screening != null)
        {
            sb.AppendLine();
            sb.AppendLine("【前置掃描結果】（超出上方篇幅的較低嚴重度項目，已先由獨立 AI 呼叫逐項檢視）");
            foreach (var (issue, reason) in screening.Notable)
            {
                AppendIssue(sb, issue, history.Count, flagged: issue.KnownIssue != null);
                sb.AppendLine($"  掃描意見：{reason}");
            }
            if (screening.Notable.Count == 0 && screening.CleanCount > 0)
            {
                sb.AppendLine($"- 已檢視 {screening.CleanCount} 項，皆判定為一般雜訊。");
            }
            else if (screening.CleanCount > 0)
            {
                sb.AppendLine($"（另 {screening.CleanCount} 項經檢視判定為一般雜訊）");
            }
            if (screening.FailedCount > 0)
            {
                sb.AppendLine($"（{screening.FailedCount} 項掃描失敗未經檢視，僅計入當日統計）");
            }
        }

        if (issues.Count == 0)
        {
            sb.AppendLine("（當日無錯誤、警告或需注意的稽核事件）");
        }

        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【近期歷史】（每日總量變化參考；注意星期規律，例如每週固定維護重開機屬正常模式）");
            foreach (var h in history)
            {
                var topKeys = string.Join("、", h.TopIssues
                    .Where(i => i.Severity >= IssueSeverity.Medium)
                    .Take(3)
                    .Select(i => $"{i.Source}#{i.EventId}x{i.Count}"));

                sb.Append($"- {h.Date:MM-dd}({WeekdayZh(h.Date)})：錯誤{h.ErrorCount} 警告{h.WarningCount} 稽核{h.AuditEventCount} 風險{h.RiskLevel}");
                if (topKeys.Length > 0)
                {
                    sb.Append($" 重點:{topKeys}");
                }
                // 非低風險日附上當日 AI 結論，讓模型看得到先前判讀的語意脈絡（是否已知原因、是否已處理）
                if (RiskLevels.IsActionable(h.RiskLevel) && h.AiAnalyzed && h.Summary.Length > 0)
                {
                    sb.Append($" 當日結論:{TextTruncation.Truncate(h.Summary, 80)}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("請只回傳一個 JSON 物件（不要 markdown 圍欄、不要任何其他文字），結構如下：");
        sb.AppendLine("""
{
  "risk_level": "低、中、高 擇一（輔助判斷，不會讓程式判定的風險等級降低）",
  "headline": "一句話標題，讓不懂 Event Log 的人一眼看懂今天的狀況",
  "story": "用白話講清楚今天發生了什麼，避免使用 Event ID 或程式碼層級的專有術語",
  "trend_story": "依據上方頻率比對結果，這是新問題、正在惡化、還是延續中的已知問題，用白話接續之前的脈絡講",
  "action": "現在該做什麼、多急迫，例如「今天就要處理」「本週內確認」「持續觀察即可」"
}
""");

        return sb.ToString();
    }

    /// <summary>
    /// 輸出單一事件的完整資訊：嚴重度、發生時段（集中爆發 vs 全天零星）、趨勢比對數字、
    /// 訊息多樣性（同一問題重複 vs 多個不同對象）、Security 事件的帳號/IP 彙總
    /// </summary>
    private static void AppendIssue(StringBuilder sb, LogIssueSignature i, int historyDays, bool flagged)
    {
        var head = flagged ? $"[{i.Severity}/{i.Category}]" : $"[{EntryTypeText(i)}]";
        var time = i.Count > 1 ? $"（{i.FirstSeen}~{i.LastSeen}）" : $"（{i.FirstSeen}）";
        var known = flagged ? $"：{i.KnownIssue}" : "";
        sb.AppendLine($"- {head} {i.LogName}/{i.Source} EventId {i.EventId} x{i.Count}{time}{TrendText(i, historyDays)}{known}");

        // 歷史存 3 則範例，prompt 只放部分控制長度（重點問題 2 則、其他 1 則）；完整範例在風險報告與歷史紀錄
        var sampleCount = flagged ? FlaggedSampleCount : OtherSampleCount;
        var variety = i.DistinctMessageCount > 1 ? $"（共 {i.DistinctMessageCount} 種不同內容）" : "";
        sb.AppendLine($"  範例訊息{variety}：{string.Join(" ｜ ", i.SampleMessages.Take(sampleCount))}");

        if (i.KeyDetails != null)
        {
            sb.AppendLine($"  {i.KeyDetails}");
        }
    }

    /// <summary>EntryType 0 是 classic API 讀到的 Critical 等級事件，顯示為 Critical 而非數字</summary>
    private static string EntryTypeText(LogIssueSignature i) =>
        (int)i.EntryType == 0 ? "Critical" : i.EntryType.ToString();

    private static string WeekdayZh(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "週一",
        DayOfWeek.Tuesday => "週二",
        DayOfWeek.Wednesday => "週三",
        DayOfWeek.Thursday => "週四",
        DayOfWeek.Friday => "週五",
        DayOfWeek.Saturday => "週六",
        _ => "週日"
    };

    /// <summary>把 TrendAnalyzer 算好的比對數字附註在事件行後面，模型只需解讀、不需自己算</summary>
    private static string TrendText(LogIssueSignature i, int historyDays)
    {
        return i.Trend switch
        {
            IssueTrend.New => "（首次出現，歷史中從未發生）",
            IssueTrend.Rising => $"（頻率上升：近{historyDays}日平均 x{i.HistoryDailyAverage}" +
                                 (i.PreviousDayCount != null ? $"、昨日 x{i.PreviousDayCount}" : "") + "）",
            IssueTrend.Recurring => $"（重複出現：近{historyDays}日中 {i.DaysSeenInHistory} 天有發生，平均 x{i.HistoryDailyAverage}）",
            IssueTrend.Declining => $"（頻率下降：近{historyDays}日平均 x{i.HistoryDailyAverage}）",
            _ => ""
        };
    }

    /// <summary>前置掃描的彙總結果</summary>
    public class ScreeningOutcome
    {
        public List<(LogIssueSignature Issue, string Reason)> Notable { get; } = new();
        public int CleanCount { get; set; }
        public int FailedCount { get; set; }
    }

    /// <summary>前置掃描呼叫的 JSON 契約</summary>
    private class ScreeningResult
    {
        [JsonPropertyName("notable")]
        public List<ScreeningItem> Notable { get; set; } = new();
    }

    private class ScreeningItem
    {
        [JsonPropertyName("no")]
        public int No { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
