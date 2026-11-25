namespace LogForesight.Tests;

/// <summary>
/// 「不再打擾」30 天解除要查問題目前規則的嚴重度（<see cref="IssueRuleLookup"/>）：測試用的固定查詢。
/// <see cref="None"/>＝沒有任何規則（規則不存在視為非高嚴重度、不解除），給「不測解除」的案例用，行為等同解除功能之前。
/// </summary>
internal static class TestRules
{
    public static readonly IssueRuleLookup None = _ => null;

    /// <summary>所有問題都是高嚴重度、不帶重大旗標</summary>
    public static readonly IssueRuleLookup AllHigh = _ => new IssueRuleFacts(IssueSeverity.High, false);

    /// <summary>所有問題都是中嚴重度、不帶重大旗標</summary>
    public static readonly IssueRuleLookup AllMedium = _ => new IssueRuleFacts(IssueSeverity.Medium, false);

    /// <summary>所有問題都是中嚴重度但帶重大旗標</summary>
    public static readonly IssueRuleLookup MediumMajor = _ => new IssueRuleFacts(IssueSeverity.Medium, true);
}

/// <summary>固定「今天」的時鐘（本地時區＝UTC，免得測試機時區影響日曆日）</summary>
internal sealed class FixedLocalClock(DateTime local) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(local, DateTimeKind.Utc));
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
