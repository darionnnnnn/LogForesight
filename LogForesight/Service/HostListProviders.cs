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
/// 機房分析的主機清單來源。主機清單的主人固定為 Web 主機頁維護
/// （Txt 清單模式已退役，見 docs/NETIQ-WEB-CONFIG-PLAN.md 定案 12）。
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
    private readonly ISentinelStore _sentinels;

    public StoreHostListProvider(IHostStore hosts, ISentinelStore sentinels)
    {
        _hosts = hosts;
        _sentinels = sentinels;
    }

    public string Description => "Web 維護的主機清單";

    public HostListResult GetHostList() => HostListSelection.FromStore(_hosts, _sentinels);
}

/// <summary>「從主機清單資料挑出今晚要查的主機」。挑選規則本身在 <see cref="NetiqHostList"/>（Core），
/// Web 畫面用的是同一份，所以畫面標示與批次行為不會分歧。</summary>
internal static class HostListSelection
{
    public static HostListResult FromStore(IHostStore hosts, ISentinelStore sentinels)
    {
        var allHosts = hosts.GetAll();
        var allSentinels = sentinels.GetAll().ToDictionary(s => s.SentinelId);
        var result = new HostListResult();

        // 分組鍵用 Sentinel 現存的名稱（不是主機列上可能落後的 NetiqServer 快照）——
        // 識別鍵是 SentinelId（定案 4），這裡才是唯一真的需要「現在叫什麼名字」的地方
        // （查詢分組＋連線資訊要對得上）
        foreach (var host in NetiqHostList.Pollable(allHosts, id => allSentinels.TryGetValue(id, out var s) && s.Active))
        {
            var name = allSentinels[host.SentinelId!.Value].Name;
            if (!result.ByServer.TryGetValue(name, out var list))
            {
                list = new List<NetiqTarget>();
                result.ByServer[name] = list;
            }

            list.Add(new NetiqTarget(host.HostId, host.IpAddress!, host.RoleDesc));
        }

        AddExclusionWarnings(allHosts, allSentinels, result);
        return result;
    }

    /// <summary>被規則排除的主機逐一列出——靜默排除等於製造一個沒人知道的監控盲區</summary>
    private static void AddExclusionWarnings(
        List<WebHost> allHosts, Dictionary<long, Sentinel> allSentinels, HostListResult result)
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

        // 所屬 Sentinel 已停用（暫停輪巡）——與待歸屬／IP 衝突同一原則，不能靜默略過
        foreach (var host in NetiqHostList.Listed(allHosts).Where(h => h.SentinelId.HasValue))
        {
            if (allSentinels.TryGetValue(host.SentinelId!.Value, out var sentinel) && !sentinel.Active)
                result.Warnings.Add($"{host.HostName}：所屬 Sentinel「{sentinel.Name}」已停用，本次不查詢");
        }
    }
}
