namespace LogForesight.Web.Services;

/// <summary>
/// 風險日處理進度的推導與案件計數——日層級（DayHandlingCommandService）與問題層級
/// （IssueHandlingCommandService）都要用到同一套推導規則，集中在這裡避免兩邊各自算一套
/// 而在某個新呼叫端漏掉「日狀態由問題層級推導」（方案 B）這條規則。
/// </summary>
public class HandlingProgressCalculator
{
    private readonly IIssueHandlingStore _issueStore;
    private readonly IRecordHandlingStore _store;
    private readonly IIssueCaseStore _cases;
    private readonly ISystemSettingsStore _settings;
    private readonly IIssueExclusionSource _exclusions;

    public HandlingProgressCalculator(
        IIssueHandlingStore issueStore, IRecordHandlingStore store, IIssueCaseStore cases, ISystemSettingsStore settings,
        IIssueExclusionSource exclusions)
    {
        _issueStore = issueStore;
        _store = store;
        _cases = cases;
        _settings = settings;
        _exclusions = exclusions;
    }

    /// <summary>單筆推導：本次呼叫取一次靜音排除條件。</summary>
    public DayHandlingDerivation.DayProgress ComputeProgress(WebHost host, DateTime date, DailyAnalysisRecord record) =>
        ComputeProgress(_exclusions.Current(), host, date, record);

    /// <summary>呼叫端已在同一次請求取得靜音排除條件時用這個（逐筆迴圈不要每筆各取一次）。</summary>
    public DayHandlingDerivation.DayProgress ComputeProgress(
        IssueExclusion exclusion, WebHost host, DateTime date, DailyAnalysisRecord record)
    {
        var handlings = _issueStore.GetForDay(host.HostName, date);
        var dayLevel = _store.Get(host.HostName, date)?.Status;
        return DayHandlingDerivation.Derive(
            record.TopIssues, handlings, dayLevel, _settings.Get().ParseUnhandledSeverities(), exclusion, date);
    }

    /// <summary>record 可能不存在（分析尚未跑過），沒有紀錄就沒有推導狀態可算</summary>
    public DayHandlingDerivation.DayProgress? TryComputeProgress(WebHost host, DateTime date, DailyAnalysisRecord? record) =>
        record == null ? null : ComputeProgress(host, date, record);

    /// <summary>
    /// 本日問題中屬進行中案件的數量（docs/archive/FEEDBACK-4-PLAN.md §2）：面板「目前狀態」下方顯示
    /// 「N 項屬進行中案件」，讓使用者知道為什麼某些問題狀態會「自己動」（案件同步的結果）。
    /// record 為 null（分析尚未跑過）時回 0。
    /// </summary>
    public int OpenCaseCount(WebHost host, DailyAnalysisRecord? record)
    {
        if (record == null || record.TopIssues.Count == 0) return 0;
        var openKeys = _cases.GetOpenForHost(host.HostName).Select(c => c.IssueKey).ToHashSet(IssueSignatureKeyComparer.Instance);
        return record.TopIssues.Count(i => openKeys.Contains(IssueSignatureKey.For(i)));
    }
}
