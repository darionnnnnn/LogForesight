using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// PRTG 主機對應衝突清單的分頁查詢（docs/PRTG-SPEC.md §4）：最新一日的衝突列、同 IP 裝置與候選主機組裝。
/// 授權（全站 PRTG 傳輸權限）與參數正規化留在控制器；這裡只負責讀資料與組 DTO。
/// </summary>
public class PrtgConflictQueryService
{
    private readonly StorageBackend? _backend;
    private readonly IHostStore? _hosts;
    private readonly PrtgDeviceIndexCache? _deviceIndexCache;

    public PrtgConflictQueryService(
        StorageBackend? backend,
        IHostStore? hosts = null,
        PrtgDeviceIndexCache? deviceIndexCache = null)
    {
        _backend = backend;
        _hosts = hosts;
        _deviceIndexCache = deviceIndexCache;
    }

    /// <summary>第 <paramref name="page"/> 頁的衝突清單；page／pageSize 須為已正規化的值</summary>
    public PrtgHostMapPageDto GetConflictPage(int page, int pageSize)
    {
        if (_backend == null)
        {
            return new PrtgHostMapPageDto
            {
                Page = page,
                PageSize = pageSize
            };
        }

        var backend = _backend;
        var store = backend.PrtgStore();
        var (mapDate, hostMaps) = store.GetLatestHostMapWithDate(PrtgHostMapLookup.LatestLookbackDays);

        var targetStatus = PrtgMapStatus.Conflict;

        // 依 DeviceObjid 排序後才分頁：GetLatestHostMapWithDate 的查詢沒有 ORDER BY，
        // 未排序就分頁時同一列可能在兩頁重複出現、也可能整列被跳過。
        var filteredRows = hostMaps
            .Where(m => string.Equals(m.MapStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeviceObjid)
            .ToList();

        var total = filteredRows.Count;
        var pagedRows = filteredRows
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // 裝置索引（objid → 裝置、正規化 IP → 同 IP 裝置）改走短 TTL 的跨請求快取
        // （回饋四十五輪 B5）：原本每翻一頁都把裝置全表讀回來重建一次索引，
        // 而裝置表是結構鏡像、一天只在夜間同步時變一次。
        // 沒注入快取時（單元測試直接 new controller）退回「當場建一份」，行為完全相同。
        var deviceIndex = _deviceIndexCache != null
            ? _deviceIndexCache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, () => store.GetAllDevices())
            : new PrtgDeviceIndex(store.GetAllDevices());

        var hostStore = _hosts ?? new HostStore(backend.Blob("hosts"));
        var activeHosts = hostStore.GetAll()
            .Where(h => h.Active && h.MergedInto == null)
            .ToList();

        var hostsByNormIp = new Dictionary<string, List<WebHost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in activeHosts)
        {
            var normIp = PrtgHostMapper.NormalizeIp(h.IpAddress);
            if (normIp == null) continue;
            if (!hostsByNormIp.TryGetValue(normIp, out var list))
            {
                list = new List<WebHost>();
                hostsByNormIp[normIp] = list;
            }
            list.Add(h);
        }

        var items = new List<PrtgConflictItemDto>(pagedRows.Count);
        foreach (var row in pagedRows)
        {
            var device = deviceIndex.ByObjid(row.DeviceObjid);
            var normIp = PrtgHostMapper.NormalizeIp(row.Ip);

            var sameDevices = deviceIndex.ByNormIp(normIp);

            var isMultiDevice = sameDevices.Count > 1;
            string conflictKind;
            var note = row.Note ?? "";
            // 衝突只剩兩種（docs/PRTG-SPEC.md §4）：名稱命中多台主機、一 IP 由多台主機共用。
            // 同 IP 多 device 已改為全部對應到該 IP 的主機，不再是衝突。
            conflictKind = note.StartsWith(PrtgHostMapper.NoteHostnameMultiHostsPrefix, StringComparison.Ordinal)
                ? "hostname-multi"
                : "multi-host";

            List<PrtgConflictDeviceDto> sameIpDevices;
            if (isMultiDevice)
            {
                sameIpDevices = sameDevices
                    .Select(d => new PrtgConflictDeviceDto
                    {
                        Objid = d.Objid,
                        Name = d.Name,
                        GroupPath = d.GroupPath
                    })
                    .ToList();
            }
            else
            {
                sameIpDevices = new List<PrtgConflictDeviceDto>
                {
                    new()
                    {
                        Objid = row.DeviceObjid,
                        Name = device?.Name,
                        GroupPath = device?.GroupPath
                    }
                };
            }

            List<PrtgCandidateHostDto> candidateHosts;
            if (normIp != null && hostsByNormIp.TryGetValue(normIp, out var matchedHosts))
            {
                candidateHosts = matchedHosts
                    .Select(h => new PrtgCandidateHostDto
                    {
                        HostId = h.HostId,
                        HostName = h.HostName,
                        IpAddress = h.IpAddress
                    })
                    .ToList();
            }
            else
            {
                candidateHosts = new List<PrtgCandidateHostDto>();
            }

            items.Add(new PrtgConflictItemDto
            {
                DeviceObjid = row.DeviceObjid,
                DeviceName = device?.Name,
                GroupPath = device?.GroupPath,
                Ip = row.Ip,
                HostName = isMultiDevice ? null : row.HostName,
                Note = row.Note,
                MapStatus = row.MapStatus ?? targetStatus,
                ConflictKind = conflictKind,
                SameIpDevices = sameIpDevices,
                CandidateHosts = candidateHosts
            });
        }

        return new PrtgHostMapPageDto
        {
            MapDate = mapDate,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items
        };
    }
}
