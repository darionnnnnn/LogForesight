namespace LogForesight;

/// <summary>今晚要向 Sentinel 查詢的一台主機。HostId 是寫入分析紀錄時的關聯鍵</summary>
public record NetiqTarget(long HostId, string IpAddress, string RoleDesc);

public class HostListResult
{
    /// <summary>Sentinel 名稱 → 該台轄下要查詢的主機</summary>
    public Dictionary<string, List<NetiqTarget>> ByServer { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 被排除或有疑慮的項目。**這些不是可以吞掉的雜訊**——每一條都代表某台主機今晚
    /// 不會被檢查，而「沒查 ≠ 沒事」是本系統的核心原則，必須進 console 與機房總覽。
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// false = 清單來源本身不可用（Txt 模式下目錄不存在或沒有 txt）。
    /// 與「清單是空的」要分得開：前者是設定沒完成，後者是真的沒有主機要查。
    /// </summary>
    public bool SourceUsable { get; set; } = true;

    public int TotalHosts => ByServer.Values.Sum(list => list.Count);
}

/// <summary>
/// 機房分析的主機清單來源（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 D）。
/// 兩個實作對應 txt 與 Web 兩個「主人」，但**輸出結構完全相同**，
/// 呼叫端（機房 pipeline）不需要知道清單從哪來。
/// </summary>
public interface IHostListProvider
{
    /// <summary>供 console/log 顯示的來源描述</summary>
    string Description { get; }

    HostListResult GetHostList();
}

/// <summary>Web 主機頁維護的清單：直接讀主機清單資料，不做任何同步</summary>
public class StoreHostListProvider : IHostListProvider
{
    private readonly IHostStore _hosts;

    public StoreHostListProvider(IHostStore hosts) => _hosts = hosts;

    public string Description => "Web 維護的主機清單";

    public HostListResult GetHostList() => HostListSelection.FromStore(_hosts);
}

/// <summary>
/// txt 清單：先以 txt 覆寫主機清單資料（txt 是主人），再走與 Web 模式**完全相同**的挑選邏輯。
///
/// 兩個模式共用挑選尾段而不是各寫一份，是為了讓「換個來源、選出來的主機卻不一樣」
/// 這種 bug 在結構上不可能發生——差別只在「有沒有先同步 txt」。
/// </summary>
public class TxtHostListProvider : IHostListProvider
{
    private readonly IHostStore _hosts;
    private readonly string _directory;
    private readonly IReadOnlyCollection<string> _knownServers;

    public TxtHostListProvider(IHostStore hosts, string directory, IReadOnlyCollection<string> knownServers)
    {
        _hosts = hosts;
        _directory = directory;
        _knownServers = knownServers;
    }

    public string Description => $"txt 清單目錄 {_directory}";

    public HostListResult GetHostList()
    {
        var sync = NetiqTxtImporter.Sync(_hosts, _directory, _knownServers);

        if (!sync.DirectoryUsable)
        {
            var unusable = new HostListResult { SourceUsable = false };
            unusable.Warnings.AddRange(sync.Warnings);
            return unusable;
        }

        var result = HostListSelection.FromStore(_hosts);
        // 同步過程的警告排在前面：它們說明的是「清單本身有問題」，
        // 比「某台主機被規則排除」更靠近根因
        result.Warnings.InsertRange(0, sync.Warnings);
        return result;
    }
}

/// <summary>
/// 「從主機清單資料挑出今晚要查的主機」——兩個 provider 共用的單一實作。
/// 挑選規則本身在 <see cref="NetiqHostList"/>（Core），Web 畫面用的是同一份，
/// 所以畫面標示與批次行為不會分歧。
/// </summary>
internal static class HostListSelection
{
    public static HostListResult FromStore(IHostStore hosts)
    {
        var allHosts = hosts.GetAll();
        var result = new HostListResult();

        foreach (var host in NetiqHostList.Pollable(allHosts))
        {
            if (!result.ByServer.TryGetValue(host.NetiqServer!, out var list))
            {
                list = new List<NetiqTarget>();
                result.ByServer[host.NetiqServer!] = list;
            }

            list.Add(new NetiqTarget(host.HostId, host.IpAddress!, host.RoleDesc));
        }

        AddExclusionWarnings(allHosts, result);
        return result;
    }

    /// <summary>被規則排除的主機逐一列出——靜默排除等於製造一個沒人知道的監控盲區</summary>
    private static void AddExclusionWarnings(List<WebHost> allHosts, HostListResult result)
    {
        foreach (var pending in NetiqHostList.PendingAssignment(allHosts))
        {
            result.Warnings.Add($"{pending.HostName}：尚未確定所屬 Sentinel，本次不查詢");
        }

        foreach (var group in NetiqHostList.IpConflicts(allHosts))
        {
            foreach (var skipped in group.Skip(1))
            {
                result.Warnings.Add(
                    $"{skipped.HostName}：IP {skipped.IpAddress} 與 {group[0].HostName} 衝突，本次不查詢");
            }
        }
    }
}
