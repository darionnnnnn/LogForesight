using LogForesight.Web.Services;

namespace LogForesight.Web.Repositories;

/// <summary>
/// 分析紀錄的查詢組合（docs/WEB-SPEC.md §4.2 Repository 層）。
///
/// 這一層存在的核心理由：**一台主機可能有多個識別**——本身，加上所有已併入它的
/// 墓碑列（人工綁定新舊主機的結果）。查詢時必須把它們一起展開，否則合併之前的歷史
/// 會從畫面上消失。這個展開如果散落在各個 Service，遲早有人忘了做。
/// 集中在這裡，並且**強制**每次查詢都套用可見範圍——呼叫端拿不到「不過濾」的入口。
/// </summary>
public interface IRecordRepository
{
    /// <summary>依條件查詢（已套用目前登入者的可見範圍）</summary>
    List<DailyAnalysisRecord> Query(RecordQueryFilter filter);

    /// <summary>單筆紀錄；不在可見範圍內回 null（不區分「不存在」與「沒權限」）</summary>
    DailyAnalysisRecord? GetOne(long hostId, DateTime date);

    /// <summary>
    /// 主機 ID → 該主機的全部識別（本身＋已併入它的墓碑列，本身排在最前）。
    /// 查無主機時回空清單——空集合在查詢語意上就是「查不到任何資料」，是安全的失敗方向。
    /// </summary>
    List<HostKey> ResolveHostKeys(long hostId);

    /// <summary>主機名稱 → 主機（查無回 null）</summary>
    WebHost? ResolveHost(string hostName);
}

public class RecordRepository : IRecordRepository
{
    private readonly IAnalysisRecordQuery _records;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;

    public RecordRepository(IAnalysisRecordQuery records, IHostStore hosts, IVisibilityService visibility)
    {
        _records = records;
        _hosts = hosts;
        _visibility = visibility;
    }

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        var visible = VisibleHostKeys();
        var visibleIds = visible.Select(k => k.HostId).ToHashSet();

        // 呼叫端可以再縮小範圍（例如只看某幾台），但**不可能擴大**——
        // 交集永遠不超出可見範圍。這是第 3 層防線實際生效的地方。
        filter.Hosts = filter.Hosts == null
            ? visible
            : filter.Hosts.Where(k => visibleIds.Contains(k.HostId)).ToList();

        return _records.Query(filter);
    }

    public DailyAnalysisRecord? GetOne(long hostId, DateTime date)
    {
        if (!_visibility.GetVisibleHostIds().Contains(hostId)) return null;

        return _records.GetOne(ResolveHostKeys(hostId), date);
    }

    public List<HostKey> ResolveHostKeys(long hostId) =>
        HostIdentityResolver.Expand(_hosts.GetAll(), hostId);

    public WebHost? ResolveHost(string hostName) => _hosts.FindByName(hostName);

    /// <summary>
    /// 可見範圍的全部識別：可見主機本身，加上已併入它們的墓碑列。
    ///
    /// **墓碑列因此隨目標主機一起可見**，即使墓碑自己不在任何授權群組裡——這是刻意的：
    /// 併入代表管理員已確認「這兩列是同一台實體機器」，看得到這台機器的人就該看得到
    /// 它改名/重建之前的完整歷史，否則合併等於把歷史藏起來。
    /// </summary>
    private List<HostKey> VisibleHostKeys()
    {
        var visibleIds = _visibility.GetVisibleHostIds();
        var allHosts = _hosts.GetAll();

        return allHosts
            .Where(h => visibleIds.Contains(h.HostId))
            .SelectMany(h => HostIdentityResolver.Expand(allHosts, h.HostId))
            .DistinctBy(k => k.HostId)
            .ToList();
    }
}
