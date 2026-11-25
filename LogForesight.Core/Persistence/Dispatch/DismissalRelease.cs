namespace LogForesight.Core.Persistence;

/// <summary>
/// 本趟因「不再打擾已滿 30 天」放行而寫入成員的累計（單 → 台數）：夜間派工與立即派工共用，趟末在單歷程各合併成一筆
/// <see cref="WorkOrderEventActions.DismissalReleased"/> 系統事件。
/// </summary>
public sealed class DismissalReleaseTally
{
    private readonly Dictionary<long, int> _byOrder = new();

    public void Add(long workOrderId, int count = 1)
    {
        if (count > 0) _byOrder[workOrderId] = _byOrder.GetValueOrDefault(workOrderId) + count;
    }

    /// <summary>每張單寫一筆事件（單號升冪），寫完清空</summary>
    public void Flush(WorkOrderCoordinator coordinator, DateTime occurredAt)
    {
        foreach (var (workOrderId, released) in _byOrder.OrderBy(p => p.Key))
            coordinator.RecordDismissalReleased(workOrderId, released, occurredAt);
        _byOrder.Clear();
    }
}

/// <summary>某個問題「目前規則」的嚴重度與重大旗標（「不再打擾」30 天解除的資格依據）</summary>
public readonly record struct IssueRuleFacts(IssueSeverity Severity, bool ElevatesDayRisk);

/// <summary>
/// 以問題簽章鍵查「目前規則」的嚴重度與重大旗標；規則不存在（自訂規則被刪、停用、認不出）回 null。
/// 由呼叫端傳入（規則可在畫面調整，Core 不複製規則、不用靜態表）；Web 端用 <see cref="DismissalRelease.LookupFrom"/> 由規則清單組出。
/// </summary>
public delegate IssueRuleFacts? IssueRuleLookup(string issueKey);

/// <summary>
/// 高嚴重度「不再打擾」30 天解除一次的唯一判定（<see cref="DispatchContext.DismissedCases"/>、保留清除、清單、
/// 新案件的「先前判定不處理」標記都呼叫這裡，不得另寫第二份天數或條件比較）。
///
/// 一件原本會擋的案件，同時符合下列全部時解除：
/// ① <see cref="IssueCase.BlocksRedispatch"/> 為 null（處理人回覆產生的；管理者明確勾選的 true 是永久）；
/// ② 該問題目前規則的嚴重度是高或帶重大旗標（規則不存在視為非高、不解除）；
/// ③ 結案日不早於 <see cref="EffectiveFrom"/>（上線前判定的不自動解除）；
/// ④ 判定日距結案日超過 <see cref="Days"/> 天：<b>以日曆日差計</b>（判定日.Date − 結案日.Date），第 30 天仍擋、第 31 天不擋。
/// </summary>
public static class DismissalRelease
{
    /// <summary>解除所需的天數（程式常數，不開設定）</summary>
    public const int Days = 30;

    /// <summary>生效日期：結案日早於此日的案件視為上線前判定，不自動解除。部署時確認。</summary>
    public static readonly DateTime EffectiveFrom = new(2026, 11, 25);

    /// <summary>
    /// 解除判定：<paramref name="asOf"/> 是判定當下的日期（閘門與清單用「今天」，新案件標記用該案件的建立時間）。
    /// </summary>
    public static bool IsReleased(IssueCase c, DateTime asOf, IssueRuleLookup rules) =>
        DaysUntilRelease(c, rules) is not null
        && (asOf.Date - c.ClosedAt!.Value.Date).Days > Days;

    /// <summary>
    /// 資格（①②③）成立時回傳「解除前的總天數」（＝<see cref="Days"/>），不成立回 null（永久或上線前判定）。
    /// 剩餘天數用 <see cref="RemainingDays"/>。
    /// </summary>
    public static int? DaysUntilRelease(IssueCase c, IssueRuleLookup rules) =>
        c.BlocksRedispatch == null && c.ClosedAt is { } closed && closed.Date >= EffectiveFrom.Date && IsHighOrMajor(c.IssueKey, rules)
            ? Days
            : null;

    /// <summary>距解除還有幾天（第 30 天＝1、第 31 天起不擋＝0）；沒有資格（永久或上線前判定）回 null</summary>
    public static int? RemainingDays(IssueCase c, DateTime today, IssueRuleLookup rules)
    {
        if (DaysUntilRelease(c, rules) is not { } total) return null;
        var elapsed = (today.Date - c.ClosedAt!.Value.Date).Days;
        return Math.Max(0, total + 1 - elapsed);
    }

    /// <summary>上線前判定：不處理類、沒有被管理者明確勾選、結案日早於生效日，且嚴重度上本來會解除——清單標示用</summary>
    public static bool IsPreLaunch(IssueCase c, IssueRuleLookup rules) =>
        c.BlocksRedispatch == null && c.ClosedAt is { } closed && closed.Date < EffectiveFrom.Date && IsHighOrMajor(c.IssueKey, rules);

    private static bool IsHighOrMajor(string issueKey, IssueRuleLookup rules) => IsHighOrMajor(rules(issueKey));

    private static bool IsHighOrMajor(IssueRuleFacts? facts) =>
        facts is { } f && (LegacySeverityRank.ToSeverity((int)f.Severity) == IssueSeverity.High || f.ElevatesDayRisk);

    /// <summary>
    /// 這個問題的「不再打擾」會不會在 30 天後解除（目前規則嚴重度高或重大）：畫面的後果提示據此換句，前端不自己判斷嚴重度。
    /// 這只回答問題層級的資格；個別案件還要看 <see cref="IsReleased"/> 的其餘條件（誰結的、哪天結的）。
    /// </summary>
    public static bool AppliesTo(IssueRuleLookup rules, string issueKey) => IsHighOrMajor(rules(issueKey));

    /// <summary><see cref="AppliesTo(IssueRuleLookup, string)"/> 的（來源, 事件 ID）版本（交辦單只存來源與事件 ID）</summary>
    public static bool AppliesTo(IReadOnlyList<KnownIssueRule> rules, string source, int eventId) =>
        KnownIssueCatalog.FindRuleByAggregateKey(rules, source, eventId) is { } rule
        && IsHighOrMajor(new IssueRuleFacts(rule.Severity, rule.ElevatesDayRisk));

    /// <summary>
    /// 新案件的「先前判定不處理」推導（讀取時算、不存欄位）：<paramref name="history"/> 是同主機的前案候選（已結案、可含多個問題）。
    /// 對每件 <paramref name="targets"/>，取同主機同問題、建立在它之前的最新一件未取消案件；那件是不處理類結案，
    /// 且以<b>本件的建立時間</b>為判定日符合 <see cref="IsReleased"/>（與派工閘門同一個解除條件、同一個函式），本件就是「期滿放行後重新派工」的案件。
    /// 回傳 CaseId → 前案；沒有的不在結果裡。
    /// </summary>
    public static Dictionary<string, IssueCase> PriorDismissalsOf(
        IEnumerable<IssueCase> targets, IEnumerable<IssueCase> history, IssueRuleLookup rules)
    {
        var byKey = new Dictionary<(string HostName, string IssueKey), List<IssueCase>>(HostIssueSignatureKeyComparer.Instance);
        foreach (var h in history)
        {
            if (h.Cancelled || h.ClosedAt == null) continue;
            var key = (HostNameKey.Of(h.HostName), h.IssueKey);
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<IssueCase>();
            list.Add(h);
        }

        var result = new Dictionary<string, IssueCase>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (!byKey.TryGetValue((HostNameKey.Of(target.HostName), target.IssueKey), out var candidates)) continue;

            var previous = candidates
                .Where(p => p.CreatedAt < target.CreatedAt && !string.Equals(p.CaseId, target.CaseId, StringComparison.Ordinal))
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.UpdatedAt)
                .ThenBy(p => p.CaseId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (previous != null
                && IssueHandlingStatuses.Dismissals.Contains(previous.Status)
                && IsReleased(previous, target.CreatedAt, rules))
                result[target.CaseId] = previous;
        }
        return result;
    }

    /// <summary>由目前規則清單組出查詢：以簽章鍵的（來源, 事件 ID）找規則，找不到回 null</summary>
    public static IssueRuleLookup LookupFrom(IReadOnlyList<KnownIssueRule> rules)
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, IssueRuleFacts?>(StringComparer.Ordinal);
        return key =>
        {
            return cache.GetOrAdd(key, k =>
                IssueSignatureKey.TryParseSignature(k) is { } sig
                && KnownIssueCatalog.FindRuleByAggregateKey(rules, sig.Source, sig.EventId) is { } rule
                    ? new IssueRuleFacts(rule.Severity, rule.ElevatesDayRisk)
                    : null);
        };
    }
}
