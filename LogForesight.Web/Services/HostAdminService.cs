using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>與 hosts.js 既有的狀態 chip 值一一對應——後端篩選語意搬過來，前端不用改 chip 定義</summary>
public class HostSearchRequest
{
    public string? Query { get; set; }

    /// <summary>空字串=全部；local | netiq | pending | conflict | silent | ungrouped | inactive</summary>
    public string Status { get; set; } = "";

    public string? Sentinel { get; set; }
    public List<long>? GroupIds { get; set; }

    /// <summary>空白＝全部；'windows' | 'linux'（docs/LINUX-RULES.md）</summary>
    public string? Os { get; set; }

    /// <summary>空白＝全部；'mapped' | 'conflict' | 'unmatched'</summary>
    public string? PrtgMap { get; set; }

    /// <summary>name | source | ip | os | roleDesc | lastReport</summary>
    public string Sort { get; set; } = "name";

    /// <summary>asc | desc</summary>
    public string Dir { get; set; } = "asc";

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>主機維護（docs/WEB-SPEC.md §9.8 主機頁）</summary>
public class HostAdminService
{
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly INetiqServerCatalog _servers;
    private readonly INetiqHostService _netiqHosts;
    private readonly IAuditService _audit;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly EfPrtgStore? _prtgStore;

    /// <summary>未回報定義與儀表板「未回報主機」計數卡同一套規則（§5.4 D-4），兩邊數字才不會對不上</summary>
    private static readonly TimeSpan SilentCutoff = TimeSpan.FromDays(2);

    /// <summary>
    /// 新主機豁免無回報告警的寬限期（docs/archive/HISTORY.md 定案 9）：一個批次週期
    /// （批次通常每天跑一次）。剛匯入的主機在第一次批次跑完前 LastReportAt 必為空，
    /// 沒有寬限期的話一次匯入一批就會立刻在儀表板與這裡的「未回報」篩選觸發告警洪水。
    /// public 讓 DashboardService 引用同一個值——兩邊都用這個定義,不是各自寫一份數字。
    /// </summary>
    public static readonly TimeSpan NewHostGracePeriod = TimeSpan.FromHours(24);

    public HostAdminService(
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IUserStore users,
        INetiqServerCatalog servers,
        INetiqHostService netiqHosts,
        IAuditService audit,
        IUserDisplayNameService userDisplayNames,
        StorageBackend? backend = null,
        EfPrtgStore? prtgStore = null)
    {
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _servers = servers;
        _netiqHosts = netiqHosts;
        _audit = audit;
        _userDisplayNames = userDisplayNames;
        _prtgStore = prtgStore ?? backend?.PrtgStore();
    }

    public PagedResult<HostDto> GetHosts(HostSearchRequest request)
    {
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        var all = SortHosts(FilterHosts(request), request).ToList();
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        return new PagedResult<HostDto>
        {
            Items = all.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(h => HostDtoMapper.ToDto(h, groups, users, _userDisplayNames)).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = all.Count
        };
    }

    /// <summary>
    /// 「全選符合目前篩選的主機」（體檢 X3）。
    ///
    /// 為什麼需要一支專門的端點：批次改群組過去**只能逐筆勾**，而勾選狀態雖然跨頁保留，
    /// 「把某個網段的 500 台加進新群組」仍然是翻 50 頁、勾 500 次。實務上使用者會放棄，
    /// 改回去用 CSV——但 hosts.csv 已於回饋第十一輪 §2a 退役，退路被拿掉了。
    ///
    /// **與清單頁共用同一份 <see cref="FilterHosts"/>**：畫面說「符合 N 台」與實際選到的
    /// 必須是同一批，兩邊各寫一份篩選遲早漂移成不同結果。
    /// </summary>
    public HostIdListDto MatchingHostIds(HostSearchRequest request)
    {
        // 已併入其他主機的主機不能改群組（SetGroupsBatch 會略過並回報），
        // 全選時就先排除——讓「選了 N 台」與「實際會改 N 台」對得起來
        var matched = FilterHosts(request).Where(h => h.MergedInto == null).ToList();

        return new HostIdListDto
        {
            Total = matched.Count,
            Truncated = matched.Count > MaxSelectAllHosts,
            HostIds = SortHosts(matched, request).Take(MaxSelectAllHosts).Select(h => h.HostId).ToList()
        };
    }

    /// <summary>
    /// 單次「全選」的上限。取捨：體檢 X3 的實際情境是「一個網段 500 台」，2000 綽綽有餘；
    /// 上限存在的理由是讓請求主體與單次寫入量有界，超過時明確告知要分批，
    /// 而不是讓一次操作把整個機房掃進去卻沒人知道範圍。
    /// </summary>
    public const int MaxSelectAllHosts = 2000;

    private IEnumerable<WebHost> FilterHosts(HostSearchRequest request)
    {
        IEnumerable<WebHost> filtered = _hosts.GetAll();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var keyword = request.Query.Trim();
            filtered = filtered.Where(h =>
                h.HostName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (h.DisplayName ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (h.IpAddress ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Sentinel))
        {
            // 依 SentinelId 比對而不是名稱字串——篩選條件在 Sentinel 改名後仍要選得到同一批主機。
            // 名稱對不到任何現存 Sentinel 時查無資料，而不是意外落回「SentinelId==null」比對到待歸屬主機
            var sentinelId = _servers.GetServer(request.Sentinel)?.Id;
            filtered = sentinelId.HasValue
                ? filtered.Where(h => h.SentinelId == sentinelId.Value)
                : Enumerable.Empty<WebHost>();
        }

        if (request.GroupIds is { Count: > 0 })
        {
            var wantedGroups = request.GroupIds.ToHashSet();
            filtered = filtered.Where(h => h.GroupIds.Any(wantedGroups.Contains));
        }

        // 篩選值不合法時（前端沒有這種按鈕，但 API 是公開的）當作沒篩，不是回空清單——
        // 「打錯字就看不到任何主機」比忽略無效條件更難察覺
        var osFilter = WebHost.NormalizeOs(request.Os);
        if (osFilter != null)
        {
            filtered = filtered.Where(h => string.Equals(h.Os, osFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (request.Status == "conflict")
        {
            // IP 衝突需要跨主機比對，沿用既有的 NetiqHostList 衝突偵測（NetiqHostService.GetOverview 已算好）
            var conflictIds = _netiqHosts.GetOverview().IpConflicts
                .SelectMany(g => g.Hosts.Select(h => h.HostId))
                .ToHashSet();
            filtered = filtered.Where(h => conflictIds.Contains(h.HostId));
        }
        else if (!string.IsNullOrWhiteSpace(request.Status))
        {
            filtered = request.Status switch
            {
                "local" => filtered.Where(h => h.Source == "local" && h.Active),
                "netiq" => filtered.Where(h => h.Source == "netiq" && h.Active),
                "pending" => filtered.Where(h => h.Source == "netiq" && h.Active && h.MergedInto == null && h.SentinelId == null),
                "silent" => filtered.Where(h => h.Active && (h.LastReportAt == null
                    ? DateTime.Now - h.CreatedAt > NewHostGracePeriod
                    : DateTime.Now - h.LastReportAt.Value > SilentCutoff)),
                "ungrouped" => filtered.Where(h => h.Active && h.MergedInto == null && h.GroupIds.Count == 0),
                "inactive" => filtered.Where(h => !h.Active),
                _ => filtered
            };
        }

        if (!string.IsNullOrWhiteSpace(request.PrtgMap))
        {
            var prtgFilter = request.PrtgMap.Trim().ToLowerInvariant();
            var mapRows = _prtgStore?.GetLatestHostMap() ?? new List<PrtgHostMapRow>();
            if (mapRows.Count > 0)
            {
                if (prtgFilter == "mapped")
                {
                    var mappedIds = mapRows
                        .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
                        .Select(m => m.HostId!.Value)
                        .ToHashSet();
                    filtered = filtered.Where(h => mappedIds.Contains(h.HostId));
                }
                else if (prtgFilter == "conflict")
                {
                    var conflictIds = mapRows
                        .Where(m => m.MapStatus == PrtgMapStatus.Conflict && m.HostId.HasValue)
                        .Select(m => m.HostId!.Value)
                        .ToHashSet();
                    filtered = filtered.Where(h => conflictIds.Contains(h.HostId));
                }
                else if (prtgFilter == "unmatched")
                {
                    // unmatched 在主機映射表中是「PRTG 有 device 但無對應主機」（HostId 為 null）。
                    // 對主機清單而言，unmatched 語意為「這台主機目前沒有任何成功（ok）的 PRTG 對應」，即排除 mapped 主機。
                    var mappedIds = mapRows
                        .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
                        .Select(m => m.HostId!.Value)
                        .ToHashSet();
                    filtered = filtered.Where(h => !mappedIds.Contains(h.HostId));
                }
                // 無效篩選值依既有慣例當作沒篩，不回空清單
            }
        }

        return filtered;
    }

    /// <summary>清單頁與「全選符合篩選」共用同一份排序——全選的截斷順序才與畫面一致</summary>
    private static IEnumerable<WebHost> SortHosts(IEnumerable<WebHost> filtered, HostSearchRequest request)
    {
        // 一律先建升冪排序，Dir=desc 時整體 Reverse——單一比較邏輯，不必每個欄位各寫一份升冪/降冪
        IOrderedEnumerable<WebHost> sorted = request.Sort switch
        {
            "source" => filtered.OrderBy(h => h.Source, StringComparer.OrdinalIgnoreCase),
            "ip" => filtered.OrderBy(h => h.IpAddress ?? "", StringComparer.OrdinalIgnoreCase),
            "os" => filtered.OrderBy(h => h.Os, StringComparer.OrdinalIgnoreCase),
            "roleDesc" => filtered.OrderBy(h => h.RoleDesc, StringComparer.OrdinalIgnoreCase),
            "lastReport" => filtered.OrderBy(h => h.LastReportAt ?? DateTime.MinValue),
            _ => filtered.OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
        };

        return request.Dir == "desc" ? sorted.Reverse() : sorted;
    }

    public HostDto SaveHost(SaveHostRequest request)
    {
        var hostName = request.HostName.Trim();
        if (string.IsNullOrWhiteSpace(hostName))
            throw DomainException.Validation("主機名稱不可為空。");

        // 這是**同一份資料的另一條寫入路徑**（NetIQ 清單走 NetiqHostService）。
        // 驗證只掛在其中一條的話，從編輯表單就能繞過去存進不合格的值——
        // 而不合格的 IP／Sentinel 的後果是這台主機永遠查無資料，且完全沒有跡象
        if (!string.IsNullOrWhiteSpace(request.IpAddress) && !NetiqHostList.IsValidIp(request.IpAddress))
            throw DomainException.Validation($"「{request.IpAddress.Trim()}」不是有效的 IP 位址。");

        SentinelServer? sentinel = null;
        if (!string.IsNullOrWhiteSpace(request.NetiqServer))
        {
            sentinel = _servers.GetServer(request.NetiqServer);
            if (sentinel == null)
            {
                var known = _servers.GetServerNames();
                throw DomainException.Validation(
                    $"「{request.NetiqServer.Trim()}」不在已設定的 Sentinel 名單中" +
                    (known.Count == 0
                        ? "（尚未於 Sentinel 管理頁新增任何 Sentinel）。"
                        : $"，可選：{string.Join("、", known)}。"));
            }
        }

        var os = WebHost.NormalizeOs(request.Os)
                 ?? throw DomainException.Validation($"作業系統類型「{request.Os}」不合法，僅接受 windows 或 linux。");

        var tier = WebHost.NormalizeTier(request.Tier)
                   ?? throw DomainException.Validation($"主機分級「{request.Tier}」不合法，僅接受 core、standard 或 test。");

        var existing = _hosts.FindByName(hostName);
        var isNew = existing == null;

        var ipChanged = existing?.IpAddress != request.IpAddress;

        var saved = _hosts.Upsert(new WebHost
        {
            HostName = hostName,
            IpAddress = string.IsNullOrWhiteSpace(request.IpAddress) ? null : request.IpAddress.Trim(),
            IpUpdatedAt = ipChanged ? DateTime.Now : existing?.IpUpdatedAt,
            SentinelId = sentinel?.Id,
            NetiqServer = sentinel?.Name,
            RoleDesc = request.RoleDesc?.Trim() ?? "",
            Source = existing?.Source ?? "local",
            Os = os,
            Tier = tier,
            Active = request.Active,
            // 群組與負責人由專屬端點維護，避免「更新角色描述」意外清掉它們
            GroupIds = existing?.GroupIds ?? new List<long>(),
            OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
            // **刻意不傳 OrphanedFromSentinel（不是漏抄）**：Upsert 的既存分支會用傳入物件
            // 覆寫該欄位，不填＝標記被清除，而這正是要的行為——admin 在這裡明確編輯過這台
            // 主機（含重新指定所屬 Sentinel），人已表態，孤兒標記的使命就結束了。
            // 語意與 NetiqHostService.SetActive 同源（見該處長註解），與 OwnerCsvImporter
            // 「不該覆寫」的情況相反；做逐欄比對體檢時別把這裡一併「修」掉。
        });

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: isNew
                ? $"新增主機 {saved.HostName}（{saved.RoleDesc}）"
                : $"更新主機 {saved.HostName}：角色描述「{saved.RoleDesc}」、分級「{HostDtoMapper.TierText(saved.Tier)}」、狀態「{(saved.Active ? "啟用" : "停用")}」",
            targetKind: "host",
            targetId: saved.HostId.ToString(),
            detail: new { saved.HostName, saved.IpAddress, saved.NetiqServer, saved.RoleDesc, saved.Os, saved.Tier, saved.Active });

        return HostDtoMapper.ToDto(saved, _hostGroups.GetAll().ToDictionary(g => g.GroupId), _users.GetAll().ToDictionary(u => u.UserId), _userDisplayNames);
    }

    public HostDto SetHostGroups(long hostId, IEnumerable<long> groupIds)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var allGroups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var requested = groupIds.Distinct().ToList();

        NameFormat.EnsureAllKnown(requested, allGroups, "主機群組");

        var before = host.GroupIds.Select(id => allGroups.TryGetValue(id, out var g) ? g.GroupName : id.ToString()).ToList();
        var after = requested.Select(id => allGroups[id].GroupName).ToList();

        _hosts.SetGroups(hostId, requested);

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"變更主機 {host.HostName} 的群組：由「{NameFormat.Join(before)}」改為「{NameFormat.Join(after)}」" +
                     "（會影響哪些使用者看得到這台主機）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Before = before, After = after });

        return HostDtoMapper.ToDto(_hosts.Get(hostId)!, allGroups, _users.GetAll().ToDictionary(u => u.UserId), _userDisplayNames);
    }

    /// <summary>
    /// 批次改群組（docs/archive/FEEDBACK-5-PLAN.md §8）：一次 Mutate 完成，不逐台呼叫
    /// <see cref="SetHostGroups"/>——50 台就是 50 個請求＋50 次 blob 讀改寫。
    /// 已併入其他主機的主機由 <see cref="IHostStore.SetGroupsBatch"/> 略過並回報。
    /// </summary>
    public HostGroupsBatchResultDto SetGroupsBatch(IEnumerable<long> hostIds, IEnumerable<long> groupIds, string mode)
    {
        if (mode != "add" && mode != "replace")
            throw DomainException.Validation($"批次模式「{mode}」不合法，僅接受 add 或 replace。");

        var requestedHostIds = hostIds.Distinct().ToList();
        if (requestedHostIds.Count == 0)
            throw DomainException.Validation("請至少選擇一台主機。");

        var allGroups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var requestedGroupIds = groupIds.Distinct().ToList();
        NameFormat.EnsureAllKnown(requestedGroupIds, allGroups, "主機群組");

        var replace = mode == "replace";
        var result = _hosts.SetGroupsBatch(requestedHostIds, requestedGroupIds, replace);

        if (result.Updated.Count > 0)
        {
            var groupNames = requestedGroupIds.Select(id => allGroups[id].GroupName).ToList();
            var hostNames = result.Updated.Select(h => h.HostName).ToList();

            _audit.Record(
                action: AuditActions.HostUpdate,
                summary: $"批次{(replace ? "取代" : "加入")}群組「{NameFormat.Join(groupNames)}」：" +
                         $"共 {result.Updated.Count} 台主機（{NameFormat.Join(hostNames)}）" +
                         "（會影響哪些使用者看得到這些主機）",
                targetKind: "host",
                targetId: "batch",
                detail: new
                {
                    Mode = mode,
                    GroupIds = requestedGroupIds,
                    GroupNames = groupNames,
                    HostIds = result.Updated.Select(h => h.HostId),
                    HostNames = hostNames
                });
        }

        return new HostGroupsBatchResultDto
        {
            UpdatedCount = result.Updated.Count,
            Skipped = result.Skipped.Select(h => new SkippedHostDto { HostName = h.HostName, Reason = "已併入其他主機" }).ToList()
        };
    }

    /// <summary>
    /// 批次設定分級（回饋十九輪批次G）：一次 <see cref="IHostStore.MutateBatch{TResult}"/> 完成，
    /// 與 <see cref="SetGroupsBatch"/> 同一個理由——勾選 N 台要的是一次寫入，不是 N 次
    /// FindByName+Upsert 各自整份 blob 讀改寫。已併入其他主機的主機略過（分級對墓碑紀錄無意義，
    /// 同群組批次設定的既有慣例）。
    /// </summary>
    public HostTierBatchResultDto SetTierBatch(IEnumerable<long> hostIds, string tier)
    {
        var normalizedTier = WebHost.NormalizeTier(tier)
                              ?? throw DomainException.Validation($"主機分級「{tier}」不合法，僅接受 core、standard 或 test。");

        var requestedHostIds = hostIds.Distinct().ToList();
        if (requestedHostIds.Count == 0)
            throw DomainException.Validation("請至少選擇一台主機。");

        var updated = new List<WebHost>();
        var skipped = new List<WebHost>();
        _hosts.MutateBatch(list =>
        {
            var wanted = requestedHostIds.ToHashSet();
            foreach (var host in list.Where(h => wanted.Contains(h.HostId)))
            {
                if (host.MergedInto != null) { skipped.Add(host); continue; }
                host.Tier = normalizedTier;
                updated.Add(host);
            }
        });

        if (updated.Count > 0)
        {
            _audit.Record(
                action: AuditActions.HostUpdate,
                summary: $"批次設定分級「{HostDtoMapper.TierText(normalizedTier)}」：" +
                         $"共 {updated.Count} 台主機（{NameFormat.Join(updated.Select(h => h.HostName).ToList())}）",
                targetKind: "host",
                targetId: "batch",
                detail: new { Tier = normalizedTier, HostIds = updated.Select(h => h.HostId), HostNames = updated.Select(h => h.HostName) });
        }

        return new HostTierBatchResultDto
        {
            UpdatedCount = updated.Count,
            Skipped = skipped.Select(h => new SkippedHostDto { HostName = h.HostName, Reason = "已併入其他主機" }).ToList()
        };
    }

    public HostDto SetHostOwners(long hostId, IEnumerable<long> userIds)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var allUsers = _users.GetAll().ToDictionary(u => u.UserId);
        var requested = userIds.Distinct().ToList();

        NameFormat.EnsureAllKnown(requested, allUsers, "使用者");

        var before = host.OwnerUserIds.Select(id => allUsers.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList();
        var after = requested.Select(id => allUsers[id].Account).ToList();

        _hosts.SetOwners(hostId, requested);

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"變更主機 {host.HostName} 的負責人：由「{NameFormat.Join(before)}」改為「{NameFormat.Join(after)}」",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Before = before, After = after });

        return HostDtoMapper.ToDto(_hosts.Get(hostId)!, _hostGroups.GetAll().ToDictionary(g => g.GroupId), allUsers, _userDisplayNames);
    }

    public void MergeHost(long sourceHostId, long targetHostId)
    {
        if (sourceHostId == targetHostId)
            throw DomainException.Validation("來源與目標是同一台主機。");

        var source = _hosts.Get(sourceHostId) ?? throw DomainException.NotFound("找不到來源主機。");
        var target = _hosts.Get(targetHostId) ?? throw DomainException.NotFound("找不到目標主機。");

        if (source.MergedInto != null)
            throw DomainException.Conflict($"{source.HostName} 已經併入其他主機，請先解除原本的綁定。");

        // 目標本身是墓碑就會形成 A→B→C 的鏈。查詢的別名展開認得整條鏈（歷史不會掉），
        // 但鏈對使用者是純粹的困惑——併入一台已經停用的主機，畫面上看不出資料最後去了哪。
        // 擋在這裡，要求指向最終那台
        if (target.MergedInto != null)
            throw DomainException.Conflict(
                $"{target.HostName} 本身已併入其他主機，不能作為併入目標；請改以最終的那台主機為目標。");

        _hosts.Merge(sourceHostId, targetHostId);

        _audit.Record(
            action: AuditActions.HostMerge,
            summary: $"將主機 {source.HostName} 併入 {target.HostName}" +
                     $"（{source.HostName} 保留為墓碑紀錄並停用，綁錯可反向修復）",
            targetKind: "host",
            targetId: sourceHostId.ToString(),
            detail: new { Source = source.HostName, Target = target.HostName });
    }

    public void UnmergeHost(long hostId)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        if (host.MergedInto == null)
            throw DomainException.Validation($"{host.HostName} 沒有併入任何主機，不需要解除。");

        var mergedIntoId = host.MergedInto.Value;
        var target = _hosts.Get(mergedIntoId);

        _hosts.Unmerge(hostId);

        _audit.Record(
            action: AuditActions.HostUnmerge,
            summary: $"解除主機 {host.HostName} 與 {NameFormat.OrDeleted(target?.HostName, mergedIntoId)} 的綁定" +
                     $"（{host.HostName} 恢復啟用；合併時帶入對方的群組/負責人等設定不會自動收回，請一併確認）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Source = host.HostName, Target = target?.HostName });
    }
}
