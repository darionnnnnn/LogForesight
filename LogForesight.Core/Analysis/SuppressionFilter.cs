namespace LogForesight.Core.Analysis;

/// <summary>
/// 純函數：從完整的抑制清單篩出「這台主機、現在生效中」的規則 Id 集合，以及「已到期」的項目
/// （到期不自動刪除，只是不再生效——由人工在「規則維護」頁解除或編輯 suppressions 清理，
/// 見 docs/RULES-SPEC.md）。比對時間點固定用呼叫端傳入的 now，不使用 DateTime.Now 讓判斷可測試。
///
/// **範圍判定**（回饋十三輪 F）：Host／Group／Site 三種抑制範圍中，只有 Host 純粹比對主機名稱；
/// Group 需要知道「這台主機屬於哪些群組」，而群組成員資格存在 <c>WebHost.GroupIds</c>，
/// 這個純函數類別不該依賴 store 去查——維持「呼叫端解析、這裡只做比對」的分工，
/// <paramref name="hostGroupIds"/> 由呼叫端傳入這台主機目前所屬的群組 Id 集合。
/// </summary>
internal static class SuppressionFilter
{
    /// <summary>本機、現在生效中的完整抑制項目（含 Reason，供報告/體檢顯示用）</summary>
    public static List<RuleSuppression> ActiveForHost(
        List<RuleSuppression> all, string host, IReadOnlyCollection<long> hostGroupIds, DateTime now) =>
        all.Where(s => IsForHost(s, host, hostGroupIds) && !IsExpired(s, now)).ToList();

    /// <summary>把抑制項目投影成規則 Id 集合供快速比對。RuleId 的比對一律不分大小寫，
    /// 這個規則只在這裡定義一次，呼叫端不需要自己記得挑對 StringComparer。</summary>
    public static HashSet<string> ToRuleIdSet(List<RuleSuppression> suppressions) =>
        suppressions.Select(s => s.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<RuleSuppression> ExpiredForHost(
        List<RuleSuppression> all, string host, IReadOnlyCollection<long> hostGroupIds, DateTime now) =>
        all.Where(s => IsForHost(s, host, hostGroupIds) && IsExpired(s, now)).ToList();

    private static bool IsForHost(RuleSuppression s, string host, IReadOnlyCollection<long> hostGroupIds) => s.Scope switch
    {
        SuppressionScopes.Site => true,
        SuppressionScopes.Group => s.HostGroupId.HasValue && hostGroupIds.Contains(s.HostGroupId.Value),
        // Host（含舊資料：Scope 欄位改版前不存在，反序列化預設值即 Host，語意與改版前逐位相同）
        _ => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
    };

    private static bool IsExpired(RuleSuppression s, DateTime now) =>
        s.ExpiresAt != null && s.ExpiresAt.Value <= now;
}
