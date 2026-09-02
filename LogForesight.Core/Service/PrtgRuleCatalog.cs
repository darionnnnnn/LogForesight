using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>PRTG 規則的靜態定義資訊（分類、嚴重度、門檻等）</summary>
public sealed record PrtgRuleInfo(
    IssueCategory Category,
    IssueSeverity Severity,
    bool ElevatesDayRisk,
    string KnownIssue,
    int DefaultThreshold);

/// <summary>
/// PRTG 四條狀態變更規則的靜態對照表。
/// 集中管理規則分類、嚴重度、風險升級旗標、簡述與預設門檻，避免各處手抄分岔。
/// </summary>
public static class PrtgRuleCatalog
{
    public const int DefaultDownMinutes = 60;
    public const int DefaultFlapCount = 5;
    public const int DefaultWarningMinutes = 240;
    public const int DefaultSilentThreshold = 0;

    private static readonly Dictionary<string, PrtgRuleInfo> Rules = new(StringComparer.Ordinal)
    {
        [PrtgRuleEvaluator.RuleDown] = new(
            IssueCategory.Service,
            IssueSeverity.High,
            true,
            "監控 sensor 持續無回應，可能是服務或主機失聯",
            DefaultDownMinutes),

        [PrtgRuleEvaluator.RuleFlapping] = new(
            IssueCategory.Service,
            IssueSeverity.Medium,
            false,
            "監控 sensor 狀態頻繁震盪，可能是網路不穩或服務反覆重啟",
            DefaultFlapCount),

        [PrtgRuleEvaluator.RuleWarning] = new(
            IssueCategory.Resource,
            IssueSeverity.Medium,
            false,
            "監控 sensor 長時間處於警告狀態，資源可能接近上限",
            DefaultWarningMinutes),

        [PrtgRuleEvaluator.RuleSilent] = new(
            IssueCategory.Service,
            IssueSeverity.Medium,
            false,
            "該 device 的全部 sensor 皆無狀態，監控本身可能已失效",
            DefaultSilentThreshold),
    };

    /// <summary>依規則代碼查詢規則資訊</summary>
    public static bool TryGetRule(string ruleCode, out PrtgRuleInfo rule) =>
        Rules.TryGetValue(ruleCode, out rule!);

    /// <summary>取得指定規則資訊（僅供已知規則代碼常數呼叫；未知代碼擲例外，代表程式錯誤）</summary>
    public static PrtgRuleInfo GetRule(string ruleCode) =>
        Rules[ruleCode];
}
