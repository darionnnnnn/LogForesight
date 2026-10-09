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
/// PRTG 狀態變更與資源 pressure rule 的靜態描述對照表。
/// 集中管理規則分類、嚴重度、風險升級旗標、簡述與預設門檻，避免各處手抄分岔。
/// </summary>
public static class PrtgRuleCatalog
{
    public const int DefaultDownMinutes = 60;
    public const int DefaultFlapCount = 5;
    public const int DefaultWarningMinutes = 240;
    public const int DefaultSilentThreshold = 0;

    /// <summary>
    /// The formal code path each persisted rule code must reach. Silent is consumed by the
    /// covered evaluator only when it receives typed, complete presence evidence.
    /// </summary>
    public static string? FormalConsumerFor(string? ruleCode) => ruleCode switch
    {
        PrtgRuleEvaluator.RuleDown or PrtgRuleEvaluator.RuleFlapping or PrtgRuleEvaluator.RuleWarning =>
            nameof(PrtgCoveredRuleEvaluator),
        PrtgRuleEvaluator.RuleSilent => nameof(PrtgSilentAbsenceEvaluator),
        PrtgRuleEvaluator.RuleDiskFreeTrend => nameof(PrtgDailyPipeline),
        PrtgRuleEvaluator.RuleResourceCpuPressure or PrtgRuleEvaluator.RuleResourceMemoryPressure or
            PrtgRuleEvaluator.RuleResourceDiskPressure => nameof(PrtgResourcePeriodConsumer),
        _ => null
    };

    /// <summary>跨日判定回望窗口（日，不含當日）</summary>
    public const int CrossDayWindowDays = 14;
    /// <summary>連續出現達此日數（含當日）即升一級嚴重度</summary>
    public const int EscalateConsecutiveDays = 3;
    /// <summary>窗口內命中次數（含當日）達此數即升一級嚴重度</summary>
    public const int EscalateHitsInWindow = 3;
    /// <summary>down 連續達此日數視為長期 Down（沒人移除的死 sensor），不再拉高日風險</summary>
    public const int ChronicDownDays = 14;

    private static readonly Dictionary<string, PrtgRuleInfo> Rules = new(StringComparer.Ordinal)
    {
        [PrtgRuleEvaluator.RuleDown] = new(
            IssueCategory.Service,
            IssueSeverity.High,
            false,
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

        [PrtgRuleEvaluator.RuleDiskFreeTrend] = new(
            IssueCategory.Storage, IssueSeverity.High, false,
            "磁碟可用空間持續下降，可能在處理期間內耗盡", 0),

        [PrtgRuleEvaluator.RuleResourceCpuPressure] = new(
            IssueCategory.Resource, IssueSeverity.High, false,
            "CPU 使用率連續兩個完成小時達到 90%", 90),

        [PrtgRuleEvaluator.RuleResourceMemoryPressure] = new(
            IssueCategory.Resource, IssueSeverity.High, false,
            "記憶體使用率連續兩個完成小時達到 90%", 90),

        [PrtgRuleEvaluator.RuleResourceDiskPressure] = new(
            IssueCategory.Storage, IssueSeverity.High, false,
            "磁碟可用空間低水位或可信耗盡趨勢", 0),
    };

    /// <summary>依規則代碼查詢規則資訊</summary>
    public static bool TryGetRule(string ruleCode, out PrtgRuleInfo rule) =>
        Rules.TryGetValue(ruleCode, out rule!);

    /// <summary>取得指定規則資訊（僅供已知規則代碼常數呼叫；未知代碼擲例外，代表程式錯誤）</summary>
    public static PrtgRuleInfo GetRule(string ruleCode) =>
        Rules[ruleCode];
}
