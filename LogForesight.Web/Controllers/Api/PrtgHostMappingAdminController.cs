using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 主機對應：未對應主機、名稱不符、衝突清單、人工對應與 IP 排除</summary>
[ApiController]
[Route("api/admin/settings")]
[Permission(Capability.Maintain)]
public class PrtgHostMappingAdminController : ControllerBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IAuditService _audit;
    private readonly StorageBackend _backend;
    private readonly IHostStore _hosts;
    private readonly IPrtgHostMapRefresher _mapRefresher;
    private readonly PrtgConflictQueryService _conflictQuery;

    public PrtgHostMappingAdminController(
        IAuditService audit,
        StorageBackend backend,
        IHostStore hosts,
        IPrtgHostMapRefresher mapRefresher,
        PrtgConflictQueryService conflictQuery)
    {
        _audit = audit;
        _backend = backend;
        _hosts = hosts;
        _mapRefresher = mapRefresher;
        _conflictQuery = conflictQuery;
    }

    /// <summary>
    /// 未對應到 PRTG 的清單主機（啟用、未合併、沒有 ok 對應），附原因；維護頁「未對應主機」清單用。
    /// 只列主機清單內的主機（docs/PRTG-SPEC.md §1），判定與主機頁篩選同一份（<see cref="PrtgHostCoverage"/>）。
    /// </summary>
    [RequireViewAll]
    [HttpGet("prtg-unmapped-hosts")]
    public ApiResponse<PrtgUnmappedHostPageDto> GetPrtgUnmappedHosts([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (normPage, normPageSize) = Paging.Normalize(page, pageSize);
        var store = _backend.PrtgStore();
        var (mapDate, rows) = store.GetLatestHostMapWithDate(PrtgHostMapLookup.LatestLookbackDays);
        var unmapped = PrtgHostCoverage.Unmapped(_hosts.GetAll(), rows, store.GetIpExcludes().Select(e => e.Ip), new PrtgAddressResolver());
        return ApiResponse<PrtgUnmappedHostPageDto>.Ok(new PrtgUnmappedHostPageDto
        {
            MapDate = mapDate,
            Total = unmapped.Count,
            Page = normPage,
            PageSize = normPageSize,
            Items = unmapped.Skip((normPage - 1) * normPageSize).Take(normPageSize)
                .Select(h => new PrtgUnmappedHostDto { HostId = h.HostId, HostName = h.HostName, IpAddress = h.IpAddress, Reason = h.Reason })
                .ToList()
        });
    }

    /// <summary>
    /// IP 已對上、但 PRTG 名稱與主機清單不同的主機（只提醒「主機名稱可能已變更」，不影響對應，docs/PRTG-SPEC.md §4）。
    /// 一台主機可能有多台 device 都不一致，每台主機只列一次、帶第一筆提醒文字。
    /// </summary>
    [RequireViewAll]
    [HttpGet("prtg-name-mismatch")]
    public ApiResponse<List<PrtgNameMismatchDto>> GetPrtgNameMismatch()
    {
        var hostById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var items = _backend.PrtgStore().GetLatestHostMapWithDate(PrtgHostMapLookup.LatestLookbackDays).Rows
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue &&
                        (m.Note ?? "").StartsWith(PrtgHostMapper.NoteNameMismatchPrefix, StringComparison.Ordinal))
            .GroupBy(m => m.HostId!.Value)
            .Select(g => new PrtgNameMismatchDto
            {
                HostId = g.Key,
                HostName = hostById.TryGetValue(g.Key, out var h) ? h.HostName : g.First().HostName ?? "",
                IpAddress = hostById.TryGetValue(g.Key, out var h2) ? h2.IpAddress : null,
                Note = g.OrderBy(m => m.DeviceObjid).First().Note ?? ""
            })
            .OrderBy(x => x.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ApiResponse<List<PrtgNameMismatchDto>>.Ok(items);
    }

    /// <summary>人工指派用的裝置搜尋上限：一次回太多等於把全站裝置清單搬回畫面。</summary>
    internal const int DeviceSearchLimit = 20;

    /// <summary>
    /// 人工指派時依名稱或 IP 搜尋 PRTG 裝置鏡像（至少 2 個字，最多 <see cref="DeviceSearchLimit"/> 筆）。
    /// 這是「主機清單外的 device 可以出現在畫面」的例外之一：只在管理者主動輸入條件時回傳（docs/PRTG-SPEC.md §1）。
    /// </summary>
    [RequireViewAll]
    [HttpGet("prtg-devices/search")]
    public ApiResponse<PrtgDeviceSearchResultDto> SearchPrtgDevices([FromQuery] string? q)
    {
        var needle = q?.Trim() ?? "";
        if (needle.Length < 2)
            throw DomainException.Validation("請輸入至少 2 個字（裝置名稱或 IP）。");
        var matches = _backend.PrtgStore().GetAllDevices()
            .Where(d => (d.Name ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        (d.Ip ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase))
            // IP 完全相同的排最前：使用者照主機 IP 搜尋時，那一台一定看得到
            .OrderBy(d => string.Equals(d.Ip?.Trim(), needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(DeviceSearchLimit + 1)
            .ToList();
        return ApiResponse<PrtgDeviceSearchResultDto>.Ok(new PrtgDeviceSearchResultDto
        {
            HasMore = matches.Count > DeviceSearchLimit,
            Items = matches.Take(DeviceSearchLimit)
                .Select(d => new PrtgDeviceSearchItemDto { Objid = d.Objid, Name = d.Name, GroupPath = d.GroupPath, Ip = d.Ip })
                .ToList()
        });
    }

    /// <summary>PRTG 主機對應衝突清單分頁</summary>
    [RequireViewAll]
    [HttpGet("prtg-host-map")]
    public ApiResponse<PrtgHostMapPageDto> GetPrtgHostMap(
        [FromQuery] string status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        // 只列衝突：PRTG 端有、主機清單沒有的 device（unmatched）不呈現在畫面（docs/PRTG-SPEC.md §1）；
        // 未對應的清單主機改由 prtg-unmapped-hosts 列出
        if (!string.Equals(status, PrtgMapStatus.Conflict, StringComparison.OrdinalIgnoreCase))
            throw DomainException.Validation("status 僅支援 conflict 查詢；未對應的主機請改用 prtg-unmapped-hosts。");

        var (normPage, normPageSize) = Paging.Normalize(page, pageSize);
        return ApiResponse<PrtgHostMapPageDto>.Ok(_conflictQuery.GetConflictPage(normPage, normPageSize));
    }

    // ── PRTG 人工主機對應（PRTG 第 2 輪任務E-1）──────────────────────────────────

    /// <summary>取得全部 PRTG 人工主機對應清單（含主機名稱，供畫面顯示）</summary>
    [RequireViewAll]
    [HttpGet("prtg-scope-revision")]
    public ApiResponse<long> GetPrtgScopeRevision() =>
        ApiResponse<long>.Ok(_backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion());

    private static long RequireScopeRevision(long? revision) => revision is >= 0 ? revision.Value
        : throw DomainException.Validation("缺少 PRTG 範圍版本，請重新載入頁面再儲存。");
    private T SavePrtgScope<T>(Func<T> action)
    {
        PrtgScopeMutationGuard.EnsureNoActiveScopePurge(_backend);
        try { return action(); }
        catch (PrtgScopeConflictException ex) { throw DomainException.Conflict(ex.Message); }
    }

    [RequireViewAll]
    [HttpGet("prtg-manual-map")]
    public ApiResponse<List<PrtgManualMapDto>> GetPrtgManualMaps()
    {
        var store = _backend.PrtgStore();
        var hostMap = _hosts.GetAll().ToDictionary(h => h.HostId);
        var manualMaps = store.GetManualMaps();

        var allDevices = store.GetAllDevices();
        var deviceByObjid = new Dictionary<long, PrtgDeviceRow>();
        var countByNormIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // 同 IP 上「也有人工對應」的 device 走的是人工分支，不是被略過——扣掉它們才是真正被略過的台數，
        // 否則同 IP 兩台各自人工對應時，兩列都會顯示「另有 1 台已略過」，使用者會去找一台不存在的裝置。
        var manualCountByNormIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manualObjids = manualMaps.Select(m => m.DeviceObjid).ToHashSet();

        foreach (var d in allDevices)
        {
            deviceByObjid[d.Objid] = d;
            var normIp = PrtgHostMapper.NormalizeIp(d.Ip);
            if (normIp != null)
            {
                countByNormIp[normIp] = countByNormIp.GetValueOrDefault(normIp) + 1;
                if (manualObjids.Contains(d.Objid))
                    manualCountByNormIp[normIp] = manualCountByNormIp.GetValueOrDefault(normIp) + 1;
            }
        }

        var dtos = manualMaps.Select(m =>
        {
            var skipped = 0;
            if (deviceByObjid.TryGetValue(m.DeviceObjid, out var dev))
            {
                var normIp = PrtgHostMapper.NormalizeIp(dev.Ip);
                if (normIp != null && countByNormIp.TryGetValue(normIp, out var totalCount))
                {
                    skipped = Math.Max(0, totalCount - manualCountByNormIp.GetValueOrDefault(normIp));
                }
            }

            return new PrtgManualMapDto
            {
                DeviceObjid = m.DeviceObjid,
                HostId = m.HostId,
                HostName = hostMap.TryGetValue(m.HostId, out var host) ? host.HostName : null,
                Note = m.Note,
                CreatedBy = m.CreatedBy,
                CreatedAt = m.CreatedAt,
                SameIpSkippedCount = skipped,
                MissingInMirror = !deviceByObjid.ContainsKey(m.DeviceObjid)
            };
        }).OrderBy(m => m.DeviceObjid).ToList();

        return ApiResponse<List<PrtgManualMapDto>>.Ok(dtos);
    }

    /// <summary>新增或更新一筆 PRTG 人工主機對應</summary>
    [RequireViewAll]
    [HttpPut("prtg-manual-map")]
    public ApiResponse<PrtgManualMapDto> SetPrtgManualMap([FromBody] SetPrtgManualMapRequest request)
    {
        var host = _hosts.Get(request.HostId);
        if (host == null || !host.Active || host.MergedInto != null)
            throw DomainException.Validation("指定的主機不存在或已停用。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var store = _backend.PrtgStore();
        var row = new PrtgManualMapRow
        {
            DeviceObjid = request.DeviceObjid,
            HostId = request.HostId,
            Note = request.Note,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now
        };
        SavePrtgScope(() => { store.UpsertManualMap(row, RequireScopeRevision(request.ExpectedScopeRevision)); return 0; });

        _audit.Record(
            action: AuditActions.PrtgManualMapSet,
            summary: $"設定 PRTG device {request.DeviceObjid} 人工對應到主機 {host.HostName}",
            targetKind: "prtg_manual_map",
            targetId: request.DeviceObjid.ToString(),
            detail: new
            {
                request.DeviceObjid,
                request.HostId,
                host.HostName,
                request.Note
            });

        var remapWarning = _mapRefresher.TryRefreshToday();

        var saved = store.GetManualMaps().FirstOrDefault(m => m.DeviceObjid == request.DeviceObjid);

        return ApiResponse<PrtgManualMapDto>.Ok(new PrtgManualMapDto
        {
            DeviceObjid = request.DeviceObjid,
            HostId = request.HostId,
            HostName = host.HostName,
            Note = request.Note,
            CreatedBy = saved?.CreatedBy ?? createdBy,
            CreatedAt = saved?.CreatedAt ?? row.CreatedAt,
            RemapWarning = remapWarning
        });
    }

    /// <summary>批次設定 PRTG 人工主機對應</summary>
    [RequireViewAll]
    [HttpPut("prtg-manual-map/batch")]
    public ApiResponse<PrtgManualMapBatchResultDto> SetPrtgManualMapBatch([FromBody] SetPrtgManualMapBatchRequest request)
    {
        PrtgScopeMutationGuard.EnsureNoActiveScopePurge(_backend);
        if (request == null)
            throw DomainException.Validation("請求內容不可為空。");

        if (request.DeviceObjids == null || request.DeviceObjids.Count == 0)
            throw DomainException.Validation("請提供至少一個欲指派的 PRTG 裝置。");

        if (request.DeviceObjids.Any(id => id <= 0))
            throw DomainException.Validation("PRTG 裝置編號必須為正數。");

        if (request.DeviceObjids.Distinct().Count() != request.DeviceObjids.Count)
            throw DomainException.Validation("欲指派的 PRTG 裝置清單包含重複項目。");

        if (request.DeviceObjids.Count > 100)
            throw DomainException.Validation("批次指派每次最多處理 100 筆裝置。");

        if (request.Note?.Length > 512)
            throw DomainException.Validation("指派說明不可超過 512 字。");

        var host = _hosts.Get(request.HostId);
        if (host == null || !host.Active || host.MergedInto != null)
            throw DomainException.Validation("指定的主機不存在或已停用。");

        var store = _backend.PrtgStore();
        var allDevices = store.GetAllDevices();
        var existingDeviceIds = allDevices.Select(d => d.Objid).ToHashSet();
        var unknownIds = request.DeviceObjids.Where(id => !existingDeviceIds.Contains(id)).ToList();
        if (unknownIds.Count > 0)
            throw DomainException.Validation($"指定的一或多個 PRTG 裝置不存在於裝置鏡像中：{string.Join(", ", unknownIds)}。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var expectedScopeRevision = RequireScopeRevision(request.ExpectedScopeRevision);
        if (_backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != expectedScopeRevision)
            throw DomainException.Conflict("PRTG 對應／排除範圍已被修改，請重新載入後核對。");
        var succeededIds = new List<long>();
        long? failedDeviceObjid = null;
        var notProcessedIds = new List<long>();
        string? failureMessage = null;
        var auditFailures = 0;

        for (int i = 0; i < request.DeviceObjids.Count; i++)
        {
            var deviceObjid = request.DeviceObjids[i];
            try
            {
                var row = new PrtgManualMapRow
                {
                    DeviceObjid = deviceObjid,
                    HostId = request.HostId,
                    Note = request.Note,
                    CreatedBy = createdBy,
                    CreatedAt = DateTime.Now
                };
                store.UpsertManualMap(row, expectedScopeRevision);
                expectedScopeRevision++;
                succeededIds.Add(deviceObjid);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "批次設定 PRTG 人工主機對應失敗，裝置 ID: {DeviceObjid}", deviceObjid);
                failedDeviceObjid = deviceObjid;
                failureMessage = ex is PrtgScopeConflictException ? ex.Message : "儲存裝置對應資料時發生伺服器錯誤。";
                for (int j = i + 1; j < request.DeviceObjids.Count; j++)
                    notProcessedIds.Add(request.DeviceObjids[j]);
                break;
            }

            // 對應已落盤後，稽核故障不能把該裝置誤報為未成功。
            try
            {
                _audit.Record(
                    action: AuditActions.PrtgManualMapSet,
                    summary: $"設定 PRTG device {deviceObjid} 人工對應到主機 {host.HostName}",
                    targetKind: "prtg_manual_map",
                    targetId: deviceObjid.ToString(),
                    detail: new
                    {
                        DeviceObjid = deviceObjid,
                        request.HostId,
                        host.HostName,
                        request.Note
                    });

            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG 人工對應已儲存，但稽核寫入失敗，裝置 ID: {DeviceObjid}", deviceObjid);
                auditFailures++;
            }
        }

        string? remapWarning = null;
        if (succeededIds.Count > 0)
        {
            remapWarning = _mapRefresher.TryRefreshToday();
        }

        return ApiResponse<PrtgManualMapBatchResultDto>.Ok(new PrtgManualMapBatchResultDto
        {
            SucceededIds = succeededIds,
            FailedDeviceObjid = failedDeviceObjid,
            NotProcessedIds = notProcessedIds,
            FailureMessage = failureMessage,
            AuditWarning = auditFailures > 0 ? $"有 {auditFailures} 筆對應已儲存，但稽核紀錄寫入失敗，請通知管理員檢查伺服器紀錄。" : null,
            RemapWarning = remapWarning
        });
    }

    /// <summary>刪除一筆 PRTG 人工主機對應</summary>
    [RequireViewAll]
    [HttpDelete("prtg-manual-map/{deviceObjid:long}")]
    public ApiResponse<PrtgDeleteResultDto> DeletePrtgManualMap(long deviceObjid, [FromQuery] long? expectedScopeRevision = null)
    {
        var store = _backend.PrtgStore();
        var deleted = SavePrtgScope(() => store.DeleteManualMap(deviceObjid, RequireScopeRevision(expectedScopeRevision)));

        _audit.Record(
            action: AuditActions.PrtgManualMapDelete,
            summary: $"刪除 PRTG device {deviceObjid} 的人工主機對應",
            targetKind: "prtg_manual_map",
            targetId: deviceObjid.ToString(),
            detail: new { DeviceObjid = deviceObjid, Deleted = deleted });

        var remapWarning = _mapRefresher.TryRefreshToday();

        return ApiResponse<PrtgDeleteResultDto>.Ok(new PrtgDeleteResultDto
        {
            Deleted = deleted > 0,
            RemapWarning = remapWarning
        });
    }

    // ── PRTG IP 排除清單（批次B 階段2）──────────────────────────────────────────

    /// <summary>取得全部 PRTG IP 排除清單，依 IP 排序</summary>
    [RequireViewAll]
    [HttpGet("prtg-ip-excludes")]
    public ApiResponse<List<PrtgIpExcludeDto>> GetPrtgIpExcludes()
    {
        var store = _backend.PrtgStore();
        var dtos = store.GetIpExcludes()
            .OrderBy(e => e.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(e => new PrtgIpExcludeDto
            {
                Ip = e.Ip,
                Note = e.Note,
                CreatedBy = e.CreatedBy,
                CreatedAt = e.CreatedAt
            })
            .ToList();

        return ApiResponse<List<PrtgIpExcludeDto>>.Ok(dtos);
    }

    /// <summary>新增或更新一筆 PRTG IP 排除</summary>
    [RequireViewAll]
    [HttpPut("prtg-ip-excludes")]
    public ApiResponse<PrtgIpExcludeDto> SetPrtgIpExclude([FromBody] SetPrtgIpExcludeRequest request)
    {
        var normIp = PrtgHostMapper.NormalizeIp(request?.Ip);
        if (normIp == null)
            throw DomainException.Validation(
                string.IsNullOrWhiteSpace(request?.Ip)
                    ? "IP 位址不能為空白。"
                    : $"「{request!.Ip.Trim()}」不是有效的 IP 位址。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var store = _backend.PrtgStore();
        var row = new PrtgIpExcludeRow
        {
            Ip = normIp,
            Note = request!.Note,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now
        };
        SavePrtgScope(() => { store.UpsertIpExclude(row, RequireScopeRevision(request!.ExpectedScopeRevision)); return 0; });

        _audit.Record(
            action: AuditActions.PrtgIpExcludeSet, // prtg_ip_exclude_set
            summary: $"設定 PRTG 排除 IP {normIp}",
            targetKind: "prtg_ip_exclude",
            targetId: normIp,
            detail: new
            {
                Ip = normIp,
                request.Note
            });

        var remapWarning = _mapRefresher.TryRefreshToday();

        var saved = store.GetIpExcludes().FirstOrDefault(e => e.Ip == normIp);

        return ApiResponse<PrtgIpExcludeDto>.Ok(new PrtgIpExcludeDto
        {
            Ip = normIp,
            Note = request.Note,
            CreatedBy = saved?.CreatedBy ?? createdBy,
            CreatedAt = saved?.CreatedAt ?? row.CreatedAt,
            RemapWarning = remapWarning
        });
    }

    /// <summary>刪除一筆 PRTG IP 排除</summary>
    [RequireViewAll]
    [HttpDelete("prtg-ip-excludes/{ip}")]
    public ApiResponse<PrtgDeleteResultDto> DeletePrtgIpExclude(string ip, [FromQuery] long? expectedScopeRevision = null)
    {
        // **刪除要把原字串交給 store**：正規化語意收緊為「只有合法 IP 才回值」之後，
        // 舊資料裡非 IP 的排除列（早期不驗證內容時存進去的）正規化會回 null。
        // 在這裡先正規化再擋 null，等於讓那些列永遠刪不掉——store 的原字串退路根本走不到。
        var raw = ip?.Trim();
        if (string.IsNullOrEmpty(raw))
            throw DomainException.Validation("要刪除的排除項目不能為空白。");

        var store = _backend.PrtgStore();
        var deleted = SavePrtgScope(() => store.DeleteIpExclude(raw, RequireScopeRevision(expectedScopeRevision)));

        // 稽核記使用者實際送出的值：正規化後的值可能與畫面上那筆不同（甚至是 null）
        _audit.Record(
            action: AuditActions.PrtgIpExcludeDelete, // prtg_ip_exclude_delete
            summary: $"刪除 PRTG 排除 IP {raw}",
            targetKind: "prtg_ip_exclude",
            targetId: raw,
            detail: new { Ip = raw, Deleted = deleted });

        var remapWarning = _mapRefresher.TryRefreshToday();

        return ApiResponse<PrtgDeleteResultDto>.Ok(new PrtgDeleteResultDto
        {
            Deleted = deleted > 0,
            RemapWarning = remapWarning
        });
    }
}
