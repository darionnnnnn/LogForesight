using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace LogForesight.Web.Services;

/// <summary>
/// 快取鍵裡「可見主機識別」那一段的產生器（<c>SummaryCache</c>、<c>IssueRankingCache</c>、
/// <c>ActionableSnapshotCache</c>、<c>NextUnhandledSequenceCache</c> 共用）。
/// 輸出＝識別由小到大排序、以逗號串接，與各快取原本自己組的字串逐字相同。
/// <para>3,000 台主機的片段約 20 KB，儀表板一次請求要組 3 次、報表 2 次，命中快取時也要組，
/// 所以對同一個集合物件只組一次。</para>
/// <para><b>只對 <see cref="FrozenSet{T}"/> 記憶</b>（真正不可變；<c>IVisibilityService.GetVisibleHostIds()</c> 回傳的就是它），
/// 以物件參考為鍵；其他型別（含同樣實作 <c>IReadOnlySet</c> 但可被修改的 <c>HashSet</c>、List、陣列）每次重算——
/// 這段字串是授權維度，集合被改動後拿到舊片段等於把 A 的快取端給 B。</para>
/// <para>記憶表用 <see cref="ConditionalWeakTable{TKey,TValue}"/>：鍵物件被回收時條目自動消失，
/// 不會變成長駐集合，也不持有集合本身（登錄見 LongLivedCollectionGuardTests）。</para>
/// </summary>
public static class VisibleHostKeyPart
{
    private static readonly ConditionalWeakTable<object, string> Memo = new();

    public static string Of(IReadOnlyCollection<long> hostIds)
    {
        if (hostIds is FrozenSet<long>)
            return Memo.GetValue(hostIds, static ids => Build((IReadOnlyCollection<long>)ids));
        return Build(hostIds);
    }

    private static string Build(IReadOnlyCollection<long> hostIds)
    {
        return string.Join(",", hostIds.OrderBy(id => id));
    }
}
