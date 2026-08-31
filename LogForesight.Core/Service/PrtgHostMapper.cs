using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 裝置與主機主檔（WebHost）按日 IP 對應服務（批次 D 步驟 1）。
/// 純在記憶體中比對，並呼叫 <see cref="EfPrtgStore.ReplaceHostMapForDate"/> 就地取代該日對應。
/// </summary>
public sealed class PrtgHostMapper
{
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hostStore;
    private readonly IRunConsole _console;

    public PrtgHostMapper(EfPrtgStore store, IHostStore hostStore, IRunConsole console)
    {
        _store = store;
        _hostStore = hostStore;
        _console = console;
    }

    /// <summary>
    /// 執行指定日期的主機對應作業。
    /// </summary>
    /// <param name="mapDate">目標日期（只取 Date 部分）</param>
    /// <returns>對應結果統計</returns>
    public PrtgHostMapResult MapForDate(DateTime mapDate)
    {
        var targetDate = mapDate.Date;
        var now = DateTime.Now;

        // 1. 取得所有鏡像 devices
        var devices = _store.GetAllDevices();

        // 2. 從 HostStore 取出活躍主機清單（排除 Active == false 與有 MergedInto 的），建立 IP -> 主機清單字典
        var activeHosts = _hostStore.GetAll()
            .Where(h => h.Active && h.MergedInto == null)
            .OrderBy(h => h.HostId)
            .ToList();

        var hostLookup = new Dictionary<string, List<WebHost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in activeHosts)
        {
            var normIp = NormalizeIp(host.IpAddress);
            if (normIp == null) continue;

            if (!hostLookup.TryGetValue(normIp, out var list))
            {
                list = new List<WebHost>();
                hostLookup[normIp] = list;
            }
            list.Add(host);
        }

        var rows = new List<PrtgHostMapRow>();
        var okCount = 0;
        var conflictCount = 0;
        var unmatchedCount = 0;
        var skippedNoIp = 0;

        // 3. 裝置分流：無 IP 者跳過；有 IP 者按正規化 IP 分組
        var devicesWithIp = new List<PrtgDeviceRow>();
        foreach (var device in devices)
        {
            var normIp = NormalizeIp(device.Ip);
            if (normIp == null)
            {
                skippedNoIp++;
                continue;
            }
            devicesWithIp.Add(device);
        }

        var deviceGroups = devicesWithIp
            .GroupBy(d => NormalizeIp(d.Ip)!)
            .ToList();

        foreach (var group in deviceGroups)
        {
            var ip = group.Key;
            var groupDevices = group.ToList();

            // 若同一個 IP 對應多個 PRTG device：
            // 規格限制：全部標記 Conflict，Note 註明「此 IP 同時有 N 個 PRTG device」，
            // 且 HostId 與 HostName 絕不猜測，一律填 null。
            if (groupDevices.Count > 1)
            {
                foreach (var dev in groupDevices)
                {
                    conflictCount++;
                    rows.Add(new PrtgHostMapRow
                    {
                        MapDate = targetDate,
                        DeviceObjid = dev.Objid,
                        Ip = dev.Ip,
                        HostId = null,
                        HostName = null,
                        MapStatus = PrtgMapStatus.Conflict,
                        Note = $"此 IP 同時有 {groupDevices.Count} 個 PRTG device",
                        CreatedAt = now
                    });
                }
                continue;
            }

            // 單一 PRTG device 比對 NetIQ 主機清單
            var singleDev = groupDevices[0];
            if (!hostLookup.TryGetValue(ip, out var matchedHosts) || matchedHosts.Count == 0)
            {
                unmatchedCount++;
                rows.Add(new PrtgHostMapRow
                {
                    MapDate = targetDate,
                    DeviceObjid = singleDev.Objid,
                    Ip = singleDev.Ip,
                    HostId = null,
                    HostName = null,
                    MapStatus = PrtgMapStatus.Unmatched,
                    Note = "PRTG 有此 device，主機主檔查無對應 IP",
                    CreatedAt = now
                });
            }
            else if (matchedHosts.Count == 1)
            {
                var h = matchedHosts[0];
                okCount++;
                rows.Add(new PrtgHostMapRow
                {
                    MapDate = targetDate,
                    DeviceObjid = singleDev.Objid,
                    Ip = singleDev.Ip,
                    HostId = h.HostId,
                    HostName = TrimHostName(h.HostName),
                    MapStatus = PrtgMapStatus.Ok,
                    Note = null,
                    CreatedAt = now
                });
            }
            else
            {
                // 一 IP 多台主機（NetIQ 端多台）：
                // 沿用既有慣例對應到 HostId 最小的那台（matchedHosts 已先依 HostId 排序）
                // 標記 Conflict，Note 列出共用主機名稱（最多 5 個，超過標「等 N 台」）
                var minHost = matchedHosts[0];
                var candidateNames = matchedHosts.Count <= 5
                    ? string.Join("、", matchedHosts.Select(h => h.HostName))
                    : string.Join("、", matchedHosts.Take(5).Select(h => h.HostName)) + $" 等 {matchedHosts.Count} 台";

                conflictCount++;
                rows.Add(new PrtgHostMapRow
                {
                    MapDate = targetDate,
                    DeviceObjid = singleDev.Objid,
                    Ip = singleDev.Ip,
                    HostId = minHost.HostId,
                    HostName = TrimHostName(minHost.HostName),
                    MapStatus = PrtgMapStatus.Conflict,
                    Note = TrimNote($"IP 由 {matchedHosts.Count} 台主機共用，已對應到 HostId 最小者：{minHost.HostName}（{candidateNames}）"),
                    CreatedAt = now
                });
            }
        }

        // 依 DeviceObjid 排序以求寫入決定性
        // （TrimNote 定義見本類別下方）
        rows.Sort((a, b) => a.DeviceObjid.CompareTo(b.DeviceObjid));

        // 4. 整份就地取代該日對應
        _store.ReplaceHostMapForDate(targetDate, rows);

        // 5. Console 摘要與異常明細（前 20 筆）
        _console.WriteLine($"[主機對應] {targetDate:yyyy-MM-dd} 對應完成：ok={okCount}, conflict={conflictCount}, unmatched={unmatchedCount}, skipped_no_ip={skippedNoIp}");

        var auditRows = rows.Where(r => r.MapStatus != PrtgMapStatus.Ok).Take(20).ToList();
        if (auditRows.Count > 0)
        {
            _console.WriteLine($"[主機對應] 異常/未對應清單（前 {auditRows.Count} 筆）：");
            foreach (var row in auditRows)
            {
                _console.WriteLine($"  - Device {row.DeviceObjid} ({row.Ip ?? "無IP"}): {row.MapStatus} - {row.Note}");
            }
        }

        return new PrtgHostMapResult(okCount, conflictCount, unmatchedCount, skippedNoIp);
    }

    /// <summary>
    /// 唯一的 IP 正規化私有方法：去頭尾空白、轉小寫。若為 null 或全空白則回傳 null。
    /// </summary>
    private static string? NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return ip.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 說明文字的長度上限（欄位是 nvarchar(512)）。一個 IP 底下掛五台主機、名稱又長時，
    /// 組出來的說明可破 1500 字元；SQL Server 會擲截斷例外讓整份對應寫不進去，
    /// 而該日舊資料在寫入前已被刪除，等於淨損失一天的對應。SQLite 不報錯，兩邊語意還會分岔。
    /// </summary>
    private const int MaxNoteLength = 500;

    private static string TrimNote(string note) =>
        note.Length <= MaxNoteLength ? note : note[..(MaxNoteLength - 1)] + "…";

    /// <summary>
    /// host_name 欄是 nvarchar(255)，主機名來自可人工編輯的 JSON 主檔，無長度保證——同 Note 的截斷理由。
    /// </summary>
    private static string? TrimHostName(string? name) =>
        name != null && name.Length > 255 ? name[..255] : name;
}

/// <summary>
/// PRTG 主機對應執行統計結果
/// </summary>
public sealed record PrtgHostMapResult(
    int Ok,
    int Conflict,
    int Unmatched,
    int SkippedNoIp);
