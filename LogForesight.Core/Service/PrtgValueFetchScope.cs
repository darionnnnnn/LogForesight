namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 數值取數的主機範圍（docs/PRTG-SPEC.md §3a）。
///
/// 預設只抓「當日出問題的主機」——實機環境有四萬多個 sensor，逐一擷取一晚跑不完，
/// 對從沒出過問題的主機取數也沒有分析價值。但值型規則要設計基線時會需要更廣的樣本，
/// 因此開放管理者放寬範圍。
///
/// 三種模式共用同一個「host → device → 白名單 sensor」的下游收斂，差別只在候選主機怎麼來。
/// </summary>
public static class PrtgValueFetchScope
{
    /// <summary>只抓觸發主機：當日高／中風險 ∪ PRTG 規則命中（預設，與加入此設定前的行為相同）</summary>
    public const string Triggered = "triggered";

    /// <summary>抓全部有 `ok` 主機對應的 device。**要求 sensor type 白名單非空**，否則等於對全部 sensor 取數</summary>
    public const string AllMapped = "all-mapped";

    /// <summary>觸發主機 ∪ 管理者指定的主機清單</summary>
    public const string TriggeredPlusList = "triggered-plus-list";

    public static bool IsValid(string? value) =>
        value is Triggered or AllMapped or TriggeredPlusList;

    /// <summary>不合法或未設定時一律回預設值——設定壞掉不該讓夜間批次改抓全機房。</summary>
    public static string Normalize(string? value) => IsValid(value) ? value! : Triggered;

    /// <summary>
    /// 依模式算出這次要取數的主機集合（三種模式的唯一判定點，取數與歷史回填共用）。
    /// </summary>
    /// <param name="scope">模式字面值；不合法時當作 <see cref="Triggered"/></param>
    /// <param name="triggeredHosts">觸發主機：當日高／中風險 ∪ 規則命中</param>
    /// <param name="mappedHosts">有 `ok` 主機對應的全部主機（<c>all-mapped</c> 模式用）</param>
    /// <param name="extraHosts">指定清單解析出的主機 id（<c>triggered-plus-list</c> 模式用）</param>
    public static IReadOnlyList<long> SelectHosts(
        string? scope,
        IEnumerable<long> triggeredHosts,
        IEnumerable<long> mappedHosts,
        IEnumerable<long> extraHosts)
    {
        return Normalize(scope) switch
        {
            AllMapped => mappedHosts.Distinct().ToList(),
            TriggeredPlusList => triggeredHosts.Concat(extraHosts).Distinct().ToList(),
            _ => triggeredHosts.Distinct().ToList()
        };
    }

    /// <summary>
    /// 指定主機名稱解析成主機 id：不分大小寫、去前後空白，
    /// **排除已停用與已合併（墓碑）的主機**（同 PRTG 主機對應的既有慣例）。
    /// 對不到的名稱由呼叫端回報，不靜默略過——管理者打錯字時必須看得到。
    /// </summary>
    public static (List<long> HostIds, List<string> Unresolved) ResolveHostNames(
        IEnumerable<string>? names, IEnumerable<(long HostId, string HostName, bool Active, bool Merged)> hosts)
    {
        var hostIds = new List<long>();
        var unresolved = new List<string>();
        if (names == null) return (hostIds, unresolved);

        var lookup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hosts)
        {
            if (!h.Active || h.Merged) continue;
            lookup.TryAdd(h.HostName.Trim(), h.HostId);
        }

        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            if (lookup.TryGetValue(name, out var id))
            {
                if (!hostIds.Contains(id)) hostIds.Add(id);
            }
            else
            {
                unresolved.Add(name);
            }
        }

        return (hostIds, unresolved);
    }
}
