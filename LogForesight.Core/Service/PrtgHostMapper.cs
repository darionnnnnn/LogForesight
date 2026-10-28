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
    public const string NoteHostnameMultiHostsPrefix = "hostname 比對到";
    /// <summary>IP 已對上主機，但 PRTG 裝置名稱抽出的主機名與主檔不同：只提醒「主機名稱可能已變更」，不影響對應。</summary>
    public const string NoteNameMismatchPrefix = "名稱不一致";
    public const string NoteHostnameOk = "hostname";

    /// <summary>
    /// 對應規則版本。改變對應規則時遞增：升級後第一次啟動以本機鏡像重算今天的對應，成功後刪除舊規則產生的其他日期對應，
    /// 過去日改沿用新對應（docs/PRTG-SPEC.md §4）。2＝IP 為主、同 IP 多 device 全部對應、名稱只補 IP 沒對到的主機。
    /// </summary>
    public const int RuleVersion = 2;

    /// <summary>記錄已套用對應規則版本的 blob 鍵。</summary>
    public const string RuleVersionBlobKey = "prtg_host_map_rule_version";

    private readonly EfPrtgStore _store;
    private readonly IHostStore _hostStore;
    private readonly IRunConsole _console;
    private readonly IPrtgAddressResolver _resolver;

    public PrtgHostMapper(EfPrtgStore store, IHostStore hostStore, IRunConsole console, IPrtgAddressResolver resolver)
    {
        _store = store;
        _hostStore = hostStore;
        _console = console;
        _resolver = resolver;
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

        var manualMaps = _store.GetManualMaps().ToDictionary(m => m.DeviceObjid);
        var excludedIps = _store.GetIpExcludes()
            .Select(e => NormalizeIp(e.Ip))
            .Where(ip => ip != null)
            .Select(ip => ip!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hostById = activeHosts.ToDictionary(h => h.HostId);

        var hostLookup = BuildActiveHostIpLookup(_hostStore, _resolver);

        // 主機主檔短名索引：對 activeHosts（已排除停用與合併）以 PrtgHostnameCandidates.ShortName(h.HostName) 為鍵，
        // 值為主機清單（依 HostId 排序）；null 鍵略過。
        var hostByShortName = new Dictionary<string, List<WebHost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in activeHosts)
        {
            var shortName = PrtgHostnameCandidates.ShortName(host.HostName);
            if (shortName == null) continue;
            if (!hostByShortName.TryGetValue(shortName, out var list))
            {
                list = new List<WebHost>();
                hostByShortName[shortName] = list;
            }
            list.Add(host);
        }

        List<WebHost> MatchHostsByHostname(PrtgDeviceRow dev)
        {
            var candidates = PrtgHostnameCandidates.Extract(dev.Name, dev.Ip);
            var matchedDict = new Dictionary<long, WebHost>();
            foreach (var cand in candidates)
            {
                if (hostByShortName.TryGetValue(cand, out var hosts))
                {
                    foreach (var h in hosts)
                    {
                        matchedDict.TryAdd(h.HostId, h);
                    }
                }
            }
            return matchedDict.Values.OrderBy(h => h.HostId).ToList();
        }

        // 規則二預備：分組前先算出本次有哪些正規化 IP，其底下至少有一台 device 走了人工對應
        var manualMappedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in devices)
        {
            if (manualMaps.TryGetValue(dev.Objid, out var manual) && hostById.ContainsKey(manual.HostId))
            {
                var normIp = _resolver.Resolve(dev.Ip);
                if (normIp != null)
                {
                    manualMappedIps.Add(normIp);
                }
            }
        }

        var rows = new List<PrtgHostMapRow>();
        var okCount = 0;
        var manualCount = 0;
        var conflictCount = 0;
        var unmatchedCount = 0;
        var skippedNoIp = 0;
        var skippedExcluded = 0;
        var skippedManualSibling = 0;
        var hostnameCount = 0;
        var nameMismatchCount = 0;
        var pendingHostnameCandidates = new List<(PrtgDeviceRow Device, WebHost Host)>();

        // hostname 比對到多台主機：不猜，標 Conflict 並列出候選主機名（最多 5 個）。無 IP 與 IP 查無主機兩條路共用。
        void AddHostnameMultiHostConflict(PrtgDeviceRow dev, List<WebHost> matched)
        {
            conflictCount++;
            var candidateNames = matched.Count <= 5
                ? string.Join("、", matched.Select(h => h.HostName))
                : string.Join("、", matched.Take(5).Select(h => h.HostName)) + $" 等 {matched.Count} 台";
            rows.Add(new PrtgHostMapRow
            {
                MapDate = targetDate,
                DeviceObjid = dev.Objid,
                Ip = dev.Ip,
                HostId = null,
                HostName = null,
                MapStatus = PrtgMapStatus.Conflict,
                Note = TrimNote($"{NoteHostnameMultiHostsPrefix} {matched.Count} 台主機：{candidateNames}"),
                CreatedAt = now
            });
        }

        void AddUnmatched(PrtgDeviceRow dev)
        {
            unmatchedCount++;
            rows.Add(new PrtgHostMapRow
            {
                MapDate = targetDate,
                DeviceObjid = dev.Objid,
                Ip = dev.Ip,
                HostId = null,
                HostName = null,
                MapStatus = PrtgMapStatus.Unmatched,
                Note = "PRTG 有此 device，主機主檔查無對應 IP",
                CreatedAt = now
            });
        }

        // 3. 裝置分流：人工對應優先；無 IP 者跳過；排除清單跳過；同 IP 已有人工指定跳過；有 IP 者按正規化 IP 分組
        var devicesWithResolvedIp = new List<(PrtgDeviceRow Device, string ResolvedIp)>();
        foreach (var device in devices)
        {
            var normIp = _resolver.Resolve(device.Ip);

            // 3.1 人工對應優先
            if (manualMaps.TryGetValue(device.Objid, out var manual))
            {
                if (hostById.TryGetValue(manual.HostId, out var targetHost))
                {
                    var noteText = "人工指定對應" + (!string.IsNullOrWhiteSpace(manual.Note) ? "：" + manual.Note : "");
                    if (normIp != null && excludedIps.Contains(normIp))
                    {
                        noteText += "（此 IP 在排除清單中，人工指定優先）";
                    }
                    okCount++;
                    manualCount++;
                    rows.Add(new PrtgHostMapRow
                    {
                        MapDate = targetDate,
                        DeviceObjid = device.Objid,
                        Ip = device.Ip,
                        HostId = targetHost.HostId,
                        HostName = TrimHostName(targetHost.HostName),
                        MapStatus = PrtgMapStatus.Ok,
                        Note = TrimNote(noteText),
                        CreatedAt = now
                    });
                    continue;
                }
                else
                {
                    _console.WriteLine($"[主機對應] 警告：Device {device.Objid} 的人工對應主機 (HostId={manual.HostId}) 已不存在或已停用，改採自動判定。");
                }
            }

            // 3.2 無 IP 者：若沒有生效中的人工對應，進入 hostname 退路
            if (normIp == null)
            {
                var matchedByHostname = MatchHostsByHostname(device);
                if (matchedByHostname.Count == 0)
                {
                    skippedNoIp++;
                }
                else if (matchedByHostname.Count >= 2)
                {
                    AddHostnameMultiHostConflict(device, matchedByHostname);
                }
                else
                {
                    pendingHostnameCandidates.Add((device, matchedByHostname[0]));
                }
                continue;
            }

            // 3.3 規則一：IP 命中排除清單 → 不產生對應列
            if (excludedIps.Contains(normIp))
            {
                skippedExcluded++;
                continue;
            }

            // 3.4 規則二：同 IP 已有 device 被人工指定 → 其餘 device 不進 IP 分組
            if (manualMappedIps.Contains(normIp))
            {
                skippedManualSibling++;
                continue;
            }

            devicesWithResolvedIp.Add((device, normIp));
        }

        var deviceGroups = devicesWithResolvedIp
            .GroupBy(x => x.ResolvedIp, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in deviceGroups)
        {
            var ip = group.Key;
            var groupItems = group.ToList();

            // 以 IP 為主（docs/PRTG-SPEC.md §4）：IP 命中恰好一台清單主機時，該 IP 底下所有 device 都對應到它——
            // 同一台伺服器在 PRTG 常被建成多個 device（Ping、WMI、SNMP 各一），全標衝突會讓這台主機完全拿不到 PRTG 資料。
            // 只想認其中一台時，對它做人工對應，同 IP 的其餘 device 會被略過（規則二）。
            if (hostLookup.TryGetValue(ip, out var matchedHosts) && matchedHosts.Count == 1)
            {
                var h = matchedHosts[0];
                foreach (var item in groupItems)
                {
                    okCount++;
                    var mismatch = NameMismatchNote(item.Device, h);
                    if (mismatch != null) nameMismatchCount++;
                    rows.Add(new PrtgHostMapRow
                    {
                        MapDate = targetDate,
                        DeviceObjid = item.Device.Objid,
                        Ip = item.Device.Ip,
                        HostId = h.HostId,
                        HostName = TrimHostName(h.HostName),
                        MapStatus = PrtgMapStatus.Ok,
                        Note = mismatch,
                        CreatedAt = now
                    });
                }
                continue;
            }

            if (matchedHosts is { Count: >= 2 })
            {
                // 一 IP 多台主機（NetIQ 端多台）：沿用既有慣例對應到 HostId 最小的那台並標 Conflict，
                // Note 列出共用主機名稱（最多 5 個，超過標「等 N 台」）
                var minHost = matchedHosts[0];
                var candidateNames = matchedHosts.Count <= 5
                    ? string.Join("、", matchedHosts.Select(x => x.HostName))
                    : string.Join("、", matchedHosts.Take(5).Select(x => x.HostName)) + $" 等 {matchedHosts.Count} 台";
                foreach (var item in groupItems)
                {
                    conflictCount++;
                    rows.Add(new PrtgHostMapRow
                    {
                        MapDate = targetDate,
                        DeviceObjid = item.Device.Objid,
                        Ip = item.Device.Ip,
                        HostId = minHost.HostId,
                        HostName = TrimHostName(minHost.HostName),
                        MapStatus = PrtgMapStatus.Conflict,
                        Note = TrimNote($"IP 由 {matchedHosts.Count} 台主機共用，已對應到 HostId 最小者：{minHost.HostName}（{candidateNames}）"),
                        CreatedAt = now
                    });
                }
                continue;
            }

            // IP 查無清單主機：逐台走名稱退路
            foreach (var item in groupItems)
            {
                var matchedByHostname = MatchHostsByHostname(item.Device);
                if (matchedByHostname.Count == 0)
                {
                    AddUnmatched(item.Device);
                }
                else if (matchedByHostname.Count >= 2)
                {
                    AddHostnameMultiHostConflict(item.Device, matchedByHostname);
                }
                else
                {
                    pendingHostnameCandidates.Add((item.Device, matchedByHostname[0]));
                }
            }
        }

        // 名稱退路只補「IP 完全沒對到」的主機：
        // - 命中的主機在本次結果中已有 Ok 列（IP 命中或人工對應）→ 該 device 不屬於這台主機，落 Unmatched（不列衝突）
        // - 否則命中同一台主機的 device 全部 Ok（與同 IP 多 device 的處理一致），Note＝hostname
        var okHostIds = rows
            .Where(r => r.MapStatus == PrtgMapStatus.Ok && r.HostId.HasValue)
            .Select(r => r.HostId!.Value)
            .ToHashSet();

        foreach (var group in pendingHostnameCandidates.GroupBy(x => x.Host.HostId))
        {
            var targetHost = group.First().Host;
            foreach (var item in group)
            {
                if (okHostIds.Contains(targetHost.HostId))
                {
                    AddUnmatched(item.Device);
                    continue;
                }

                okCount++;
                hostnameCount++;
                rows.Add(new PrtgHostMapRow
                {
                    MapDate = targetDate,
                    DeviceObjid = item.Device.Objid,
                    Ip = item.Device.Ip,
                    HostId = targetHost.HostId,
                    HostName = TrimHostName(targetHost.HostName),
                    MapStatus = PrtgMapStatus.Ok,
                    Note = NoteHostnameOk,
                    CreatedAt = now
                });
            }
        }

        // 依 DeviceObjid 排序以求寫入決定性
        // （TrimNote 定義見本類別下方）
        rows.Sort((a, b) => a.DeviceObjid.CompareTo(b.DeviceObjid));

        // 4. 整份就地取代該日對應
        _store.ReplaceHostMapForDate(targetDate, rows);

        // 5. Console 摘要：只印主機口徑的計數，不逐筆列出主機清單外的 device（docs/PRTG-SPEC.md §1）
        var mappedHosts = rows.Where(r => r.MapStatus == PrtgMapStatus.Ok && r.HostId.HasValue)
            .Select(r => r.HostId!.Value).Distinct().Count();
        _console.WriteLine($"[主機對應] {targetDate:yyyy-MM-dd} 對應完成：已對應主機 {mappedHosts} 台（裝置 {okCount} 台，其中人工 {manualCount}、名稱退路 {hostnameCount}）、" +
                           $"需處理衝突 {conflictCount} 筆、名稱不一致 {nameMismatchCount} 台");

        return new PrtgHostMapResult(okCount, conflictCount, unmatchedCount, skippedNoIp, manualCount, skippedExcluded, skippedManualSibling, hostnameCount,
            MappedHosts: mappedHosts, NameMismatch: nameMismatchCount);
    }

    /// <summary>
    /// 啟用中主機的 IP 索引：只收 Active 且未合併的主機，依 HostId 排序後以解析後 IP 為鍵，
    /// 同 IP 多台主機時清單內仍維持 HostId 由小到大。對應作業與取數範圍計算共用這一份。
    /// </summary>
    internal static Dictionary<string, List<WebHost>> BuildActiveHostIpLookup(IHostStore hostStore, IPrtgAddressResolver resolver)
    {
        var hostLookup = new Dictionary<string, List<WebHost>>(StringComparer.OrdinalIgnoreCase);
        var activeHosts = hostStore.GetAll()
            .Where(h => h.Active && h.MergedInto == null)
            .OrderBy(h => h.HostId);
        foreach (var host in activeHosts)
        {
            var normIp = resolver.Resolve(host.IpAddress);
            if (normIp == null) continue;

            if (!hostLookup.TryGetValue(normIp, out var list))
            {
                list = new List<WebHost>();
                hostLookup[normIp] = list;
            }
            list.Add(host);
        }
        return hostLookup;
    }

    /// <summary>
    /// 唯一的 IP 正規化公開方法。語意已收緊為「只有合法 IP 才回值，其餘回 null」。
    /// </summary>
    public static string? NormalizeIp(string? ip)
    {
        return PrtgAddress.Normalize(ip);
    }

    /// <summary>
    /// IP 對上的 device：只有「明確帶主機名」的證據才比對——裝置名稱括號內的字串、或 host 欄的 DNS 名稱。
    /// 不帶括號的裝置名稱（「Core Switch」「PRTG-Web-Ping」）是 PRTG 端自取的顯示名，拿來比對幾乎每台都會誤報。
    /// 有證據且沒有任何一個等於主檔短名時回提醒文字，不影響對應。
    /// </summary>
    internal static string? NameMismatchNote(PrtgDeviceRow device, WebHost host)
    {
        var bracketedName = device.Name != null && device.Name.Contains('(') ? device.Name : null;
        var candidates = PrtgHostnameCandidates.Extract(bracketedName, device.Ip);
        if (candidates.Count == 0) return null;
        var shortName = PrtgHostnameCandidates.ShortName(host.HostName);
        if (shortName != null && candidates.Contains(shortName, StringComparer.OrdinalIgnoreCase)) return null;
        return TrimNote($"{NoteNameMismatchPrefix}：PRTG「{candidates[0]}」，主機清單「{host.HostName}」");
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
    int SkippedNoIp,
    int Manual = 0,
    int SkippedExcluded = 0,
    int SkippedManualSibling = 0,
    int Hostname = 0,
    int MappedHosts = 0,
    int NameMismatch = 0);
