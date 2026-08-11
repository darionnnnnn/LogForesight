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
    /// 這個規則只在這裡定義一次，呼叫端不需要自己記得挑對 StringComparer。
    /// 只取 TargetType=Rule 的項目（回饋十五輪 A）——這是既有行為的保證：舊資料反序列化後
    /// TargetType 預設就是 Rule，這裡加的過濾對既有安裝零行為影響，只是把「四型共用一份清單」
    /// 之後不該混進來的其他三型擋掉。</summary>
    public static HashSet<string> ToRuleIdSet(List<RuleSuppression> suppressions) =>
        suppressions.Where(s => s.TargetType == SuppressionTargetTypes.Rule)
            .Select(s => s.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>把抑制項目投影成簽章鍵集合（TargetType=Signature，回饋十五輪 A-1）：
    /// 未命中規則（RuleId==null）的問題簽章靠這個被抑制，補上 A1 指出的核心缺口——
    /// 過去只有命中規則的簽章才有抑制掛載點。比對不分大小寫，理由同 <see cref="ToRuleIdSet"/>。</summary>
    public static HashSet<string> ToSignatureKeySet(List<RuleSuppression> suppressions) =>
        suppressions.Where(s => s.TargetType == SuppressionTargetTypes.Signature && !string.IsNullOrEmpty(s.SignatureKey))
            .Select(s => s.SignatureKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>把抑制項目投影成關聯模式 Id 集合（TargetType=Correlation，回饋十五輪 A-1）：
    /// 供 LogAnalysisService 把命中的關聯訊號從「要吵」的清單移到「已抑制」的清單。</summary>
    public static HashSet<string> ToCorrelationPatternIdSet(List<RuleSuppression> suppressions) =>
        suppressions.Where(s => s.TargetType == SuppressionTargetTypes.Correlation && !string.IsNullOrEmpty(s.CorrelationPatternId))
            .Select(s => s.CorrelationPatternId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>本機是否有生效中的總量抑制（TargetType=Volume，回饋十五輪 A-1），
    /// <paramref name="volumeKind"/> 見 <see cref="VolumeKinds"/>。</summary>
    public static bool HasVolumeSuppression(List<RuleSuppression> suppressions, string volumeKind) =>
        suppressions.Any(s => s.TargetType == SuppressionTargetTypes.Volume &&
            string.Equals(s.VolumeKind, volumeKind, StringComparison.OrdinalIgnoreCase));

    public static List<RuleSuppression> ExpiredForHost(
        List<RuleSuppression> all, string host, IReadOnlyCollection<long> hostGroupIds, DateTime now) =>
        all.Where(s => IsForHost(s, host, hostGroupIds) && IsExpired(s, now)).ToList();

    /// <summary>
    /// 抑制項目的目標識別（TargetType＋對應欄位），供比對「兩筆抑制設定是否指向同一個目標」。
    /// 回饋十五輪體檢批G：<see cref="StillSuppressedElsewhere"/> 原本只比 RuleId（四型上線前
    /// 全部抑制都是 Rule 型），四型上線後若沿用只比 RuleId，Signature/Correlation/Volume
    /// 三型的 RuleId 恆為空字串，「仍受其他範圍抑制」的比對會恆假——不是崩潰，是悄悄失效。
    /// </summary>
    public static (string TargetType, string Key) TargetIdentity(RuleSuppression s) => s.TargetType switch
    {
        SuppressionTargetTypes.Signature => (s.TargetType, s.SignatureKey ?? ""),
        SuppressionTargetTypes.Correlation => (s.TargetType, s.CorrelationPatternId ?? ""),
        SuppressionTargetTypes.Volume => (s.TargetType, s.VolumeKind ?? ""),
        _ => (s.TargetType, s.RuleId)
    };

    /// <summary>
    /// 到期項目中，目標仍被同一主機另一筆生效中抑制覆蓋的集合（回饋十四輪 C1，回饋十五輪體檢批G
    /// 泛型化到四型）：同一個目標可能同時有 Site（永久）與 Host（已到期）兩筆抑制——只看到期的
    /// 那一筆會誤導使用者以為解除它就能恢復告警，但其實該目標仍受另一個範圍抑制，解除到期的那筆
    /// 不會有任何效果。用 <see cref="ActiveForHost"/> 反查：那個集合本身已排除到期項目，
    /// 不會自我比對出偽陽性。
    /// </summary>
    public static HashSet<(string TargetType, string Key)> StillSuppressedElsewhere(
        List<RuleSuppression> all, string host, IReadOnlyCollection<long> hostGroupIds, DateTime now)
    {
        var activeTargets = ActiveForHost(all, host, hostGroupIds, now).Select(TargetIdentity).ToHashSet();
        return ExpiredForHost(all, host, hostGroupIds, now)
            .Select(TargetIdentity)
            .Where(activeTargets.Contains)
            .ToHashSet();
    }

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
