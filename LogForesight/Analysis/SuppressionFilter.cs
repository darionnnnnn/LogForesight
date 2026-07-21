namespace LogForesight;

/// <summary>
/// 純函數：從完整的抑制清單篩出「本機、現在生效中」的規則 Id 集合，以及「本機已到期」的項目
/// （到期不自動刪除，只是不再生效——由人工用 --unsuppress 或編輯 suppressions.json 清理，
/// 見 docs/RULES-PLAN.md）。比對時間點固定用呼叫端傳入的 now，不使用 DateTime.Now 讓判斷可測試。
/// </summary>
public static class SuppressionFilter
{
    /// <summary>本機、現在生效中的完整抑制項目（含 Reason，供報告/體檢顯示用）</summary>
    public static List<RuleSuppression> ActiveForHost(List<RuleSuppression> all, string host, DateTime now) =>
        all.Where(s => IsForHost(s, host) && !IsExpired(s, now)).ToList();

    /// <summary>把抑制項目投影成規則 Id 集合供快速比對。RuleId 的比對一律不分大小寫，
    /// 這個規則只在這裡定義一次，呼叫端不需要自己記得挑對 StringComparer。</summary>
    public static HashSet<string> ToRuleIdSet(List<RuleSuppression> suppressions) =>
        suppressions.Select(s => s.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<RuleSuppression> ExpiredForHost(List<RuleSuppression> all, string host, DateTime now) =>
        all.Where(s => IsForHost(s, host) && IsExpired(s, now)).ToList();

    private static bool IsForHost(RuleSuppression s, string host) =>
        s.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static bool IsExpired(RuleSuppression s, DateTime now) =>
        s.ExpiresAt != null && s.ExpiresAt.Value <= now;
}
