using System.Text.RegularExpressions;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 詢問 AI 的焦點判定（確定性規則，不呼叫 AI）：使用者指定的 focusKeys 先保留順序，
/// 再依最新一則使用者訊息的關鍵字命中補上；都沒有時取嚴重度最高的前幾個。
/// 只在傳入的 topIssues（控制器以 GetDetail 取得、已過授權）範圍內選，不會憑空帶出範圍外的問題。
/// </summary>
public static class ChatFocusResolver
{
    public const string SourceManual = "manual";
    public const string SourceKeyword = "keyword";
    public const string SourceDefault = "default";

    /// <summary>焦點上限（暫定）</summary>
    public const int MaxFocus = 4;

    /// <summary>沒有任何指定或命中時，取嚴重度最高的前幾個（暫定）</summary>
    public const int DefaultFocusCount = 3;

    private const int MinTokenLength = 4;

    private static readonly Regex DigitRun = new(@"(?<!\d)\d{3,}(?!\d)", RegexOptions.Compiled);

    public static (List<IssueDto> Focus, string Source) Resolve(
        IReadOnlyList<IssueDto> topIssues, IReadOnlyList<string>? focusKeys, string latestUserMessage)
    {
        var manual = new List<IssueDto>();
        foreach (var key in focusKeys ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var hit = topIssues.FirstOrDefault(i => IssueSignatureKeyComparer.Instance.Equals(i.IssueKey, key));
            if (hit != null && !manual.Contains(hit)) manual.Add(hit);
        }

        var message = latestUserMessage ?? string.Empty;
        var numbers = DigitRun.Matches(message).Select(m => m.Value).ToHashSet();
        var keyword = topIssues.Where(i => !manual.Contains(i) && MatchesKeyword(i, message, numbers)).ToList();

        var merged = manual.Concat(keyword).Take(MaxFocus).ToList();
        if (merged.Count == 0)
        {
            var top = topIssues
                .Select((issue, index) => (issue, index))
                .OrderByDescending(x => SeverityOrder(x.issue.Severity))
                .ThenBy(x => x.index)
                .Take(DefaultFocusCount)
                .Select(x => x.issue)
                .ToList();
            return (top, SourceDefault);
        }

        return (merged, manual.Count > 0 ? SourceManual : SourceKeyword);
    }

    private static int SeverityOrder(string severity) =>
        Enum.TryParse<IssueSeverity>(severity, out var s) ? (int)s : -1;

    private static bool MatchesKeyword(IssueDto issue, string message, HashSet<string> numbers)
    {
        if (issue.EventId != 0 && numbers.Contains(issue.EventId.ToString())) return true;

        if (Contains(message, issue.Source)) return true;

        var lastSegment = LastSegment(issue.Source);
        if (lastSegment.Length >= MinTokenLength && Contains(message, lastSegment)) return true;

        if (!string.IsNullOrWhiteSpace(issue.RuleId))
        {
            var ruleId = issue.RuleId!.Trim();
            if (ruleId.Length >= MinTokenLength && Contains(message, ruleId)) return true;
        }

        return Contains(message, issue.SourceEventLabel);
    }

    private static string LastSegment(string source)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        var idx = source.LastIndexOfAny(new[] { '-', ':' });
        return idx >= 0 ? source[(idx + 1)..].Trim() : string.Empty;
    }

    private static bool Contains(string message, string? token) =>
        !string.IsNullOrWhiteSpace(token) && message.Contains(token.Trim(), StringComparison.OrdinalIgnoreCase);
}
