using LogForesight.Core.Analysis;
using LogForesight.Core.Models;

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
    /// 每個規則代碼由哪個評估器消費（規則驗證據此拒絕沒有評估器的代碼）：
    /// 狀態型（down／flapping／warning）與 silent 由 <see cref="PrtgRuleEvaluator"/> 讀 messages 與鏡像狀態判定；
    /// 數值型由 <see cref="PrtgValueAssessment"/> 讀已落盤的小時值判定。
    /// </summary>
    public static string? FormalConsumerFor(string? ruleCode) => ruleCode switch
    {
        PrtgRuleEvaluator.RuleDown or PrtgRuleEvaluator.RuleFlapping or PrtgRuleEvaluator.RuleWarning or
            PrtgRuleEvaluator.RuleSilent => nameof(PrtgRuleEvaluator),
        PrtgRuleEvaluator.RuleDiskFreeTrend or PrtgRuleEvaluator.RuleMemoryUsedTrend or
            PrtgRuleEvaluator.RuleCpuLoadTrend or PrtgRuleEvaluator.RulePingLatencyTrend => nameof(PrtgValueAssessment),
        _ => null
    };

    /// <summary>跨日判定回望窗口（日，不含當日）</summary>
    public const int CrossDayWindowDays = 14;
    /// <summary>連續出現達此日數（含當日）即升一級嚴重度</summary>
    public const int EscalateConsecutiveDays = 3;
    /// <summary>窗口內命中次數（含當日）達此數即升一級嚴重度</summary>
    public const int EscalateHitsInWindow = 3;
    /// <summary>down 連續達此日數視為長期 Down（沒人移除的死 sensor）：Detail 加註「已連續 M 日，故障尚未恢復」；
    /// 故障風險照舊保留，重複提醒交給通知層（見 <see cref="PrtgCrossDay"/>）</summary>
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

        [PrtgRuleEvaluator.RuleMemoryUsedTrend] = new(
            IssueCategory.Resource, IssueSeverity.High, false,
            "記憶體使用率持續攀升，可能即將不足", 0),

        [PrtgRuleEvaluator.RuleCpuLoadTrend] = new(
            IssueCategory.Resource, IssueSeverity.Medium, false,
            "CPU 使用率長期上升且已偏高", 0),

        [PrtgRuleEvaluator.RulePingLatencyTrend] = new(
            IssueCategory.Service, IssueSeverity.Medium, false,
            "Ping 回應時間持續惡化", 0),
    };

    /// <summary>
    /// 已移除、不再評估的規則代碼（兩小時資源壓力）。資料庫裡留有這些代碼的規則只能保持停用，
    /// 規則頁顯示 <see cref="RetiredRuleNote"/>；歷史 finding 保留可讀。
    /// </summary>
    public static readonly IReadOnlySet<string> RetiredRuleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "resource_cpu_sustained_pressure", "resource_memory_sustained_pressure", "resource_disk_pressure"
    };

    public const string RetiredRuleNote = "此規則代碼已移除，不再評估；只能保持停用，歷史問題仍可查閱。";

    public static bool IsRetired(string? ruleCode) => ruleCode is not null && RetiredRuleCodes.Contains(ruleCode);

    /// <summary>依規則代碼查詢規則資訊</summary>
    public static bool TryGetRule(string ruleCode, out PrtgRuleInfo rule) =>
        Rules.TryGetValue(ruleCode, out rule!);

    /// <summary>取得指定規則資訊（僅供已知規則代碼常數呼叫；未知代碼擲例外，代表程式錯誤）</summary>
    public static PrtgRuleInfo GetRule(string ruleCode) =>
        Rules[ruleCode];

    /// <summary>四條數值趨勢規則代碼（依評估順序）；分類對應見 <see cref="TrendSensorCategory"/>。</summary>
    public static readonly IReadOnlyList<string> ValueTrendCodes =
    [
        PrtgRuleEvaluator.RuleDiskFreeTrend, PrtgRuleEvaluator.RuleMemoryUsedTrend,
        PrtgRuleEvaluator.RuleCpuLoadTrend, PrtgRuleEvaluator.RulePingLatencyTrend
    ];

    /// <summary>判斷是否為數值趨勢規則代碼（disk_free_trend 與三個新趨勢代碼，不分大小寫）</summary>
    public static bool IsValueTrend(string? ruleCode) => ruleCode?.ToLowerInvariant() switch
    {
        PrtgRuleEvaluator.RuleDiskFreeTrend or
        PrtgRuleEvaluator.RuleMemoryUsedTrend or
        PrtgRuleEvaluator.RuleCpuLoadTrend or
        PrtgRuleEvaluator.RulePingLatencyTrend => true,
        _ => false
    };

    /// <summary>依數值趨勢規則代碼查詢對應的 sensor 語意分類（不分大小寫）</summary>
    public static string? TrendSensorCategory(string? ruleCode) => ruleCode?.ToLowerInvariant() switch
    {
        PrtgRuleEvaluator.RuleDiskFreeTrend => PrtgSensorCategories.Disk,
        PrtgRuleEvaluator.RuleMemoryUsedTrend => PrtgSensorCategories.Memory,
        PrtgRuleEvaluator.RuleCpuLoadTrend => PrtgSensorCategories.Cpu,
        PrtgRuleEvaluator.RulePingLatencyTrend => PrtgSensorCategories.Availability,
        _ => null
    };
}
