using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogForesight;

public class LogIssueSignature
{
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventLogEntryType EntryType { get; set; }

    public int Count { get; set; }

    /// <summary>當日首次/最後發生時間 (HH:mm)，用於判斷是集中爆發還是全天零星</summary>
    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;

    /// <summary>最多 3 則內容相異的範例訊息（各截 200 字）</summary>
    public List<string> SampleMessages { get; set; } = new();

    /// <summary>群組內相異訊息內容的數量——區分「同一服務掛 10 次」與「10 個服務各掛一次」</summary>
    public int DistinctMessageCount { get; set; }

    /// <summary>Security 事件從全部訊息彙總出的相關帳號與來源 IP（入侵分析關鍵資訊），非安全事件為 null</summary>
    public string? KeyDetails { get; set; }

    // 以下由 KnownIssueCatalog.Classify 填入
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueCategory Category { get; set; } = IssueCategory.Other;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueSeverity Severity { get; set; } = IssueSeverity.Low;

    /// <summary>命中規則表時的已知問題說明，未命中為 null</summary>
    public string? KnownIssue { get; set; }

    /// <summary>命中規則的穩定 Id（KnownIssueRule.Id），未命中為 null。落紀錄供未來管理頁的
    /// 頻率報表與抑制比對使用（用 Id 查詢，不依賴 (Source, EventId) 反推，規則演進後仍對得上）</summary>
    public string? RuleId { get; set; }

    /// <summary>true = 此簽章命中的規則已被本機的 suppressions 設定抑制——只影響「要不要吵」
    /// （通知、風險升級），偵測與紀錄照常，見 docs/RULES-PLAN.md 的語意邊界</summary>
    public bool Suppressed { get; set; }

    // 以下由 TrendAnalyzer.Apply 填入（與歷史紀錄比對後的頻率趨勢）
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueTrend Trend { get; set; } = IssueTrend.Unknown;

    /// <summary>前一日發生次數；前一日有紀錄但無此事件為 0，完全無前一日紀錄為 null</summary>
    public int? PreviousDayCount { get; set; }

    /// <summary>近期歷史中有出現的日子的平均每日次數，從未出現為 null</summary>
    public double? HistoryDailyAverage { get; set; }

    /// <summary>近期歷史中出現過的天數</summary>
    public int DaysSeenInHistory { get; set; }
}

public static class LogAggregator
{
    private static readonly Regex Ipv4Regex = new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>Security 事件訊息中帳號欄位的標籤（中英文系統皆支援）</summary>
    private static readonly string[] AccountLabels = { "Account Name:", "帳戶名稱:", "帳戶名稱：" };

    /// <summary>
    /// 依 (LogName, Source, EventId, EntryType) 分組統計並用規則表分類。
    /// 聚合是小模型策略的核心：把上千筆原始 log 壓成數十行統計，AI 只看摘要不看原文。
    /// 上限 50：超出主 prompt 呈現上限的部分會走前置掃描（分批給 AI 篩選），
    /// 不會直接消失，所以這裡不需要砍太兇；50 之後的極端尾巴才截斷。
    /// </summary>
    public static List<LogIssueSignature> Aggregate(List<EventLogEntryData> logs, int top = 50)
    {
        var signatures = logs
            .GroupBy(l => (l.LogName, l.Source, l.EventId, l.EntryType))
            .Select(g =>
            {
                var distinctMessages = g
                    .Select(e => Truncate(CleanMessage(e.Message), 200))
                    .Distinct()
                    .ToList();

                return new LogIssueSignature
                {
                    LogName = g.Key.LogName,
                    Source = g.Key.Source,
                    EventId = g.Key.EventId,
                    EntryType = g.Key.EntryType,
                    Count = g.Count(),
                    FirstSeen = g.Min(e => e.TimeGenerated).ToString("HH:mm"),
                    LastSeen = g.Max(e => e.TimeGenerated).ToString("HH:mm"),
                    SampleMessages = distinctMessages.Take(3).ToList(),
                    DistinctMessageCount = distinctMessages.Count,
                    KeyDetails = g.Key.LogName.Equals("Security", StringComparison.OrdinalIgnoreCase)
                        ? ExtractSecurityDetails(g.Select(e => e.Message))
                        : null
                };
            })
            .ToList();

        foreach (var sig in signatures)
        {
            KnownIssueCatalog.Classify(sig);
        }

        // 嚴重度優先、次數其次，確保截斷 top N 時重大問題一定被保留
        return signatures
            .OrderByDescending(s => s.Severity)
            .ThenByDescending(s => s.Count)
            .Take(top)
            .ToList();
    }

    /// <summary>
    /// 從 Security 事件的完整訊息（未截斷）彙總相異的帳號名稱與來源 IP。
    /// 範例訊息截 200 字常截不到這些欄位，但「50 次登入失敗是打同一帳號還是掃多帳號、
    /// 來自單一 IP 還是多個 IP」正是入侵分析最關鍵的判讀依據。公開給
    /// <see cref="LogAnalysisService"/> 重用，用來比對 4625（失敗）與 4624（成功）是否為同一組帳號/IP。
    /// </summary>
    public static (HashSet<string> Accounts, HashSet<string> Ips) ExtractAccountsAndIps(IEnumerable<string> rawMessages)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<string>();

        foreach (var message in rawMessages)
        {
            foreach (Match m in Ipv4Regex.Matches(message))
            {
                if (m.Value != "127.0.0.1" && m.Value != "0.0.0.0")
                {
                    ips.Add(m.Value);
                }
            }

            foreach (var line in message.Split('\n'))
            {
                foreach (var label in AccountLabels)
                {
                    int idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                    {
                        continue;
                    }

                    var value = line[(idx + label.Length)..].Trim();
                    if (value.Length > 0 && value != "-" && !value.EndsWith("$"))
                    {
                        accounts.Add(value);
                    }
                }
            }
        }

        return (accounts, ips);
    }

    private static string? ExtractSecurityDetails(IEnumerable<string> rawMessages)
    {
        var (accounts, ips) = ExtractAccountsAndIps(rawMessages);

        var parts = new List<string>();
        if (accounts.Count > 0)
        {
            parts.Add($"相關帳號({accounts.Count}個): {string.Join(", ", accounts.Take(5))}{(accounts.Count > 5 ? "…" : "")}");
        }
        if (ips.Count > 0)
        {
            parts.Add($"來源IP({ips.Count}個): {string.Join(", ", ips.Take(5))}{(ips.Count > 5 ? "…" : "")}");
        }

        return parts.Count > 0 ? string.Join("；", parts) : null;
    }

    private static string CleanMessage(string s) =>
        string.Join(' ', s.Split('\r', '\n', '\t').Where(p => p.Length > 0));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
