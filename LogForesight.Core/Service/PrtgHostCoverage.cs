using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 主機清單內每台主機的 PRTG 對應狀態（docs/PRTG-SPEC.md §1、§4）——「哪些主機已對應、哪些沒有、為什麼沒有」的唯一判定。
/// 主機頁的「PRTG 對應」篩選與維護頁的「未對應主機」清單共用這一份，不各算一套。
/// 只談主機清單內的主機：PRTG 端有、主機清單沒有的 device 不在這裡出現。
/// </summary>
public static class PrtgHostCoverage
{
    /// <summary>未對應原因：主機沒有 IP 位址。</summary>
    public const string ReasonNoIp = "no-ip";

    /// <summary>未對應原因：主機的 IP 在 PRTG IP 排除清單。</summary>
    public const string ReasonIpExcluded = "ip-excluded";

    /// <summary>未對應原因：這個 IP 由多台清單主機共用（衝突，需人工指派）。</summary>
    public const string ReasonConflict = "conflict";

    /// <summary>未對應原因：PRTG 找不到這台主機的 IP，名稱也對不到。</summary>
    public const string ReasonNotInPrtg = "not-in-prtg";

    /// <summary>最新對應中至少有一台 device 以 <c>ok</c> 對到的主機。</summary>
    public static HashSet<long> MappedHostIds(IEnumerable<PrtgHostMapRow> latestMapRows) =>
        latestMapRows.Where(r => r.MapStatus == PrtgMapStatus.Ok && r.HostId.HasValue)
            .Select(r => r.HostId!.Value)
            .ToHashSet();

    /// <summary>
    /// 啟用、未合併、沒有任何 <c>ok</c> 對應的主機，依主機名稱排序並附原因。
    /// 原因的判定順序：衝突 → 沒有 IP → IP 已排除 → PRTG 查無（先講使用者能處理的具體原因）。
    /// </summary>
    public static IReadOnlyList<PrtgUnmappedHost> Unmapped(
        IEnumerable<WebHost> hosts, IReadOnlyCollection<PrtgHostMapRow> latestMapRows, IEnumerable<string> excludedIps)
    {
        var mapped = MappedHostIds(latestMapRows);
        var conflictHosts = latestMapRows
            .Where(r => r.MapStatus == PrtgMapStatus.Conflict && r.HostId.HasValue)
            .Select(r => r.HostId!.Value)
            .ToHashSet();
        var excluded = excludedIps.Select(PrtgAddress.Normalize).Where(ip => ip != null).Select(ip => ip!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return hosts
            .Where(h => h.Active && h.MergedInto == null && !mapped.Contains(h.HostId))
            .Select(h => new PrtgUnmappedHost(h.HostId, h.HostName, h.IpAddress, ReasonFor(h, conflictHosts, excluded)))
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReasonFor(WebHost host, HashSet<long> conflictHosts, HashSet<string> excluded)
    {
        if (conflictHosts.Contains(host.HostId)) return ReasonConflict;
        if (string.IsNullOrWhiteSpace(host.IpAddress)) return ReasonNoIp;
        var ip = PrtgAddress.Normalize(host.IpAddress);
        if (ip != null && excluded.Contains(ip)) return ReasonIpExcluded;
        return ReasonNotInPrtg;
    }
}

/// <summary>一台未對應到 PRTG 的清單主機。</summary>
public sealed record PrtgUnmappedHost(long HostId, string HostName, string? IpAddress, string Reason);
