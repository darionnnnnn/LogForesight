using LogForesight.Web.Services;

namespace LogForesight.Web.Repositories;

/// <summary>
/// 分析紀錄的查詢組合（docs/WEB-SPEC.md §4.2 Repository 層）。
///
/// 這一層存在的核心理由：**紀錄以主機「名稱」識別，授權以主機「ID」運作**。
/// 兩者的轉換如果散落在各個 Service，遲早有人忘了做，那就是一個授權漏洞。
/// 集中在這裡，並且**強制**每次查詢都套用可見範圍——
/// 呼叫端拿不到「不過濾」的入口。
/// </summary>
public interface IRecordRepository
{
    /// <summary>依條件查詢（已套用目前登入者的可見範圍）</summary>
    List<DailyAnalysisRecord> Query(RecordQueryFilter filter);

    /// <summary>單筆紀錄；不在可見範圍內回 null（不區分「不存在」與「沒權限」）</summary>
    DailyAnalysisRecord? GetOne(long hostId, DateTime date);

    /// <summary>主機 ID → 主機名稱（查無回 null）</summary>
    string? ResolveHostName(long hostId);

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
        var visibleHostNames = VisibleHostNames();

        // 呼叫端可以再縮小範圍（例如只看某幾台），但**不可能擴大**——
        // 交集永遠不超出可見範圍。這是第 3 層防線實際生效的地方。
        filter.HostNames = filter.HostNames == null
            ? visibleHostNames
            : filter.HostNames
                .Where(n => visibleHostNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();

        return _records.Query(filter);
    }

    public DailyAnalysisRecord? GetOne(long hostId, DateTime date)
    {
        if (!_visibility.GetVisibleHostIds().Contains(hostId)) return null;

        var hostName = ResolveHostName(hostId);
        return hostName == null ? null : _records.GetOne(hostName, date);
    }

    public string? ResolveHostName(long hostId) => _hosts.Get(hostId)?.HostName;

    public WebHost? ResolveHost(string hostName) => _hosts.FindByName(hostName);

    private List<string> VisibleHostNames()
    {
        var visibleIds = _visibility.GetVisibleHostIds();
        return _hosts.GetAll()
            .Where(h => visibleIds.Contains(h.HostId))
            .Select(h => h.HostName)
            .ToList();
    }
}
