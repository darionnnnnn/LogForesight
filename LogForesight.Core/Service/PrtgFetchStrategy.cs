namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 取數策略（docs/PRTG-SPEC.md §3b）。
///
/// 保守（預設）：快照間隔 15 分鐘，夜間不逐顆查詢歷史值（數值由快照供應）。
/// 激進：快照間隔 5 分鐘，夜間照舊逐顆查詢觸發主機歷史值。
/// </summary>
public static class PrtgFetchStrategy
{
    public const string Conservative = "conservative";
    public const string Aggressive = "aggressive";

    public const int ConservativeIntervalMinutes = 15;
    public const int AggressiveIntervalMinutes = 5;

    public static bool IsValid(string? value) =>
        value is Conservative or Aggressive;

    /// <summary>不合法或未設定時一律回保守策略。</summary>
    public static string Normalize(string? value) => IsValid(value) ? value! : Conservative;

    /// <summary>
    /// 依策略回傳設定參數：保守 (15, false)、激進 (5, true)；不合法值走保守。
    /// </summary>
    public static PrtgStrategyProfile Profile(string? strategy)
    {
        return Normalize(strategy) switch
        {
            Aggressive => new PrtgStrategyProfile(AggressiveIntervalMinutes, true),
            _ => new PrtgStrategyProfile(ConservativeIntervalMinutes, false)
        };
    }
}

/// <summary>
/// PRTG 取數策略設定組合。
/// </summary>
/// <param name="SnapshotIntervalMinutes">快照間隔分鐘數</param>
/// <param name="NightlyExactValues">夜間是否逐顆查詢歷史精確值</param>
public sealed record PrtgStrategyProfile(int SnapshotIntervalMinutes, bool NightlyExactValues);
