using System.Text.RegularExpressions;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 詢問 AI 的焦點判定（確定性規則，不呼叫 AI）：最新一則使用者訊息的關鍵字命中排最前面
/// （使用者此刻問的就是它），其後接請求帶來的 focusKeys（使用者指定或前輪延續）；都沒有時取嚴重度最高的前幾個。
/// 焦點有上限，命中排前面才不會在焦點滿了之後把剛問到的問題靜默丟掉。
/// 只在傳入的 topIssues（控制器以 GetDetail 取得、已過授權）範圍內選，不會憑空帶出範圍外的問題。
/// </summary>
public static class ChatFocusResolver
{
    public const string SourceManual = "manual";
    public const string SourceKeyword = "keyword";
    public const string SourceDefault = "default";

    /// <summary>焦點上限（暫定）</summary>
    public const int MaxFocus = 4;

    /// <summary>請求可帶的 focusKeys 上限：超出的不看（前端最多送 MaxFocus 個，這是防止異常請求的上界）</summary>
    public const int MaxRequestedKeys = 16;

    /// <summary>沒有任何指定或命中時，取嚴重度最高的前幾個（暫定）</summary>
    public const int DefaultFocusCount = 3;

    private const int MinTokenLength = 4;

    private static readonly Regex DigitRun = new(@"(?<!\d)\d{3,}(?!\d)", RegexOptions.Compiled);

    public static (List<IssueDto> Focus, string Source) Resolve(
        IReadOnlyList<IssueDto> topIssues, IReadOnlyList<string>? focusKeys, string latestUserMessage)
    {
        var manual = new List<IssueDto>();
        foreach (var key in (focusKeys ?? Array.Empty<string>()).Take(MaxRequestedKeys))
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var hit = topIssues.FirstOrDefault(i => IssueSignatureKeyComparer.Instance.Equals(i.IssueKey, key));
            if (hit != null && !manual.Contains(hit)) manual.Add(hit);
        }

        var message = latestUserMessage ?? string.Empty;
        var numbers = DigitRun.Matches(message).Select(m => m.Value).ToHashSet();
        var keyword = topIssues.Where(i => MatchesKeyword(i, message, numbers)).ToList();

        // 最新提問命中的在前，其餘指定／延續的焦點依原順序接在後面；超出上限丟的是較舊的延續焦點
        var merged = keyword.Concat(manual.Where(i => !keyword.Contains(i))).Take(MaxFocus).ToList();
        if (merged.Count == 0)
        {
            var top = topIssues
                .Select((issue, index) => (issue, index))
                .OrderByDescending(x => SeverityRank(x.issue.Severity))
                .ThenBy(x => x.index)
                .Take(DefaultFocusCount)
                .Select(x => x.issue)
                .ToList();
            return (top, SourceDefault);
        }

        return (merged, manual.Count > 0 ? SourceManual : SourceKeyword);
    }

    /// <summary>嚴重度字串的排序值（Low→Critical 遞增，無法解析為 -1）；詢問 AI 的焦點預設與整日摘要共用這一份</summary>
    internal static int SeverityRank(string severity) =>
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

    /// <summary>
    /// 整詞比對（不分大小寫）：token 前後不得緊鄰英數或底線——短來源名（su、cron）才不會被
    /// issue、sudo、result 這類字的片段誤命中；緊鄰中文、標點或連字號不算相連
    /// （「su的問題」「Security-Auditing」仍命中）。
    /// </summary>
    private static bool Contains(string message, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        for (var from = 0; from < message.Length;)
        {
            var idx = message.IndexOf(t, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var end = idx + t.Length;
            if ((idx == 0 || !IsWordChar(message[idx - 1])) && (end >= message.Length || !IsWordChar(message[end])))
                return true;
            from = idx + 1;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
