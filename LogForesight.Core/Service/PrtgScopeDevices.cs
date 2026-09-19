using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 取數範圍裝置集合的計算結果。四個計數是各項各自的不重複裝置數（重疊照算），只供輸出用。
/// </summary>
public sealed record PrtgScopeResult(IReadOnlySet<long> DeviceObjids, int Mapped, int Conflict, int Manual, int Guard);

/// <summary>
/// PRTG「取數範圍」裝置集合的唯一出口：ok 對應、與主機有關的 conflict、人工對應、守門裝置四項聯集。
/// conflict 只收「mapper 已指到主機」或「IP 屬於某台啟用中主機」的列——同 IP 多裝置但與主機無關的
/// conflict 在大型 PRTG 可能有數百台，整批收進來等於沒縮圈。
/// 守門裝置與 corehealth 所在裝置一定要在：全站鏡像縮圈後，守門的自動偵測與 corehealth fallback
/// 仍要找得到 Sentinel 主機與 PRTG core server。
/// </summary>
public static class PrtgScopeDevices
{
    public static PrtgScopeResult Compute(
        EfPrtgStore store, IHostStore hostStore, IPrtgResourceGuardSource guardSource,
        SystemSettings settings, IReadOnlyList<Sentinel> sentinels,
        IRunConsole console, IPrtgAddressResolver resolver)
    {
        var mapRows = store.GetLatestHostMap();

        var mapped = mapRows
            .Where(r => r.MapStatus == PrtgMapStatus.Ok)
            .Select(r => r.DeviceObjid)
            .ToHashSet();

        var hostIpLookup = PrtgHostMapper.BuildActiveHostIpLookup(hostStore, resolver);
        var conflict = new HashSet<long>();
        foreach (var row in mapRows.Where(r => r.MapStatus == PrtgMapStatus.Conflict))
        {
            if (row.HostId.HasValue)
            {
                conflict.Add(row.DeviceObjid);
                continue;
            }
            var ip = resolver.Resolve(row.Ip);
            if (ip != null && hostIpLookup.ContainsKey(ip))
                conflict.Add(row.DeviceObjid);
        }

        var manual = store.GetManualMaps()
            .Select(m => m.DeviceObjid)
            .ToHashSet();

        var guard = new HashSet<long>(PrtgResourceGuardTargets.ResolveDeviceObjids(guardSource, settings, sentinels, console, resolver));
        foreach (var sensor in guardSource.GetSensors())
        {
            if (PrtgResourceGuardTargets.IsCoreHealthSensor(sensor))
                guard.Add(sensor.DeviceObjid);
        }

        var all = new HashSet<long>(mapped);
        all.UnionWith(conflict);
        all.UnionWith(manual);
        all.UnionWith(guard);

        return new PrtgScopeResult(all, mapped.Count, conflict.Count, manual.Count, guard.Count);
    }
}
