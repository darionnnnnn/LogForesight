using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using LogForesight.Core.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/monitoring"), Permission(Capability.Maintain)]
public sealed partial class PrtgMonitoringController(StorageBackend backend, IHostStore hosts,
    IVisibilityService visibility, ICurrentUser user, IAuditService audit, DataVersionStamp stamp,
    IDataProtectionProvider protection) : ControllerBase
{
    private const int HostLimit = 3000;
    private const int SensorLimit = 15000;
    private const int PageSize = 100;
    private const string TokenPurpose = "LogForesight.PrtgMonitoring.R01.v1";

    [HttpGet]
    public IActionResult Get()
    {
        var policy = Store().Get();
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visible.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visible.Contains))) return Forbid();
        if (policy.HostIds.Count > HostLimit)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "既有主機範圍超過 3,000 筆；已拒絕回傳不完整修復清單。"));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var savedHosts = policy.HostIds.Distinct().Order().Select(id => DescribeSavedHost(sourceSnapshot, visible, id)).ToArray();
        var store = backend.PrtgStore();
        var fence = CaptureFence(policy, visible, store, sourceSnapshot.Version);
        if (!FenceMatches(fence, Store().Get(), VisibleHostIds(sourceSnapshot), store) ||
            fence.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機、授權或來源目錄在讀取期間已變更；請重新載入。"));
        return Ok(ApiResponse<object>.Ok(new { policy.Revision, policy.CoreSystemId, policy.ValidFrom,
            policy.SourceTimeZoneId, policy.SourceCultureName, SuggestedTimeZoneId = TimeZoneInfo.Local.Id,
            Enabled = settings.PrtgEnabled, Ready = policy.Ready(settings.PrtgUrl),
            HostIds = policy.HostIds.ToArray(), SensorIds = policy.SensorIds.ToArray(),
            SavedHosts = savedHosts, CanEdit = canViewAll || policy.HostIds.All(visible.Contains), SelectedHostCount = policy.HostIds.Count,
            SelectedSensorCount = policy.SensorIds.Count, CatalogueToken = ProtectToken(fence),
            HostCursor = ProtectToken(fence with { Kind = "hosts", Search = "", Offset = 0, HostHash = HashIds([]) }) }));
    }

    [HttpGet("hosts")]
    public IActionResult HostPage([FromQuery] string? cursor, [FromQuery] string? search = null,
        [FromQuery] string? catalogueToken = null)
    {
        if (NormalizeSearch(search).Length > 128)
            return BadRequest(ApiResponse.Fail("validation_failed", "搜尋文字最多 128 個字元。"));
        R01PageToken? token;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryReadFence(catalogueToken, out var fence, out var error)) return error!;
            token = fence! with { Kind = "hosts", Search = NormalizeSearch(search), Offset = 0, HostHash = HashIds([]) };
        }
        else if (!TryReadCursor(cursor, "hosts", search, [], out token, out var error)) return error!;
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var eligibleSnapshot = EligibleHosts(sourceSnapshot, visible);
        var eligible = eligibleSnapshot.Hosts;
        if (eligible.Count > HostLimit)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "NetIQ 可選主機超過 3,000 台；目前操作已拒絕，未截斷候選範圍。"));
        var store = backend.PrtgStore();
        if (!FenceMatches(token!, Store().Get(), visible, store)) return Conflict(ApiResponse.Fail("catalogue_changed", "主機、映射或可見範圍已變更；請重新載入目錄。"));
        var page = store.GetMonitoringHostPage(eligible.Keys.ToArray(), token!.Search, token.Offset, PageSize,
            string.IsNullOrWhiteSpace(cursor) ? null : token.Total);
        if (!FenceMatches(token, Store().Get(), VisibleHostIds(sourceSnapshot), store)) return Conflict(ApiResponse.Fail("catalogue_changed", "目錄在查詢期間已變更，請重新查詢。"));
        if (page.Total > HostLimit)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "目錄主機超過 3,000 台；請縮小範圍後重試。"));
        var names = eligible;
        var rows = page.Rows.Select(r => new { r.HostId, HostName = names.TryGetValue(r.HostId, out var host) ? host.HostName : r.HostName }).ToArray();
        var nextOffset = token.Offset + rows.Length;
        var next = nextOffset < page.Total ? ProtectToken(token with { Offset = nextOffset, Total = page.Total }) : null;
        return Ok(ApiResponse<object>.Ok(new { page.MapDate, page.Total, Rows = rows, NextCursor = next,
            CatalogueToken = ProtectToken(token with { Kind = "fence", Search = "", Offset = 0 }) }));
    }

    [HttpPost("sensors")]
    public IActionResult SensorPage([FromBody] PrtgMonitoringPageRequest request)
    {
        if (NormalizeSearch(request.Search).Length > 128 || request.Offset > 100000)
            return BadRequest(ApiResponse.Fail("validation_failed", "搜尋文字最多 128 個字元，頁面位置超出上限。"));
        if (request.HostIds is null || request.HostIds.Count is < 1 or > HostLimit || request.Offset < 0)
            return BadRequest(ApiResponse.Fail("validation_failed", "所選主機需為 1 至 3,000 台，頁面位置無效。"));
        R01PageToken? token;
        if (string.IsNullOrWhiteSpace(request.Cursor))
        {
            if (!TryReadFence(request.CatalogueToken, out var fence, out var error)) return error!;
            token = fence! with { Kind = "sensors", Search = NormalizeSearch(request.Search), Offset = 0,
                HostHash = HashIds(request.HostIds) };
        }
        else if (!TryReadCursor(request.Cursor, "sensors", request.Search, request.HostIds, out token, out var error)) return error!;
        if (token!.Offset != request.Offset) return BadRequest(ApiResponse.Fail("invalid_cursor", "頁面位置與伺服器游標不一致。"));
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var eligibleSnapshot = EligibleHosts(sourceSnapshot, visible);
        var eligible = eligibleSnapshot.Hosts;
        if (request.HostIds.Distinct().Count() != request.HostIds.Count || request.HostIds.Any(id => !eligible.ContainsKey(id)))
            return Forbid();
        var store = backend.PrtgStore();
        if (!FenceMatches(token, Store().Get(), visible, store)) return Conflict(ApiResponse.Fail("catalogue_changed", "目錄或授權已變更；請重新查詢。"));
        var mapDate = token.MapDate;
        if (!mapDate.HasValue) return Conflict(ApiResponse.Fail("catalogue_changed", "尚無有效的最新全域主機對應日期。"));
        var page = store.GetMonitoringSensorPage(mapDate.Value, request.HostIds, token.Search, request.Offset, PageSize,
            string.IsNullOrWhiteSpace(request.Cursor) ? null : token.Total);
        if (!FenceMatches(token, Store().Get(), VisibleHostIds(sourceSnapshot), store)) return Conflict(ApiResponse.Fail("catalogue_changed", "目錄在查詢期間已變更，請重新查詢。"));
        var nextOffset = request.Offset + page.Rows.Count;
        var next = nextOffset < page.Total ? ProtectToken(token with { Offset = nextOffset, Total = page.Total }) : null;
        return Ok(ApiResponse<object>.Ok(new { page.Total, Rows = page.Rows, NextCursor = next,
            CatalogueToken = ProtectToken(token with { Kind = "fence", Search = "", Offset = 0 }) }));
    }

    [HttpPost("estimate")]
    public IActionResult Estimate([FromBody] PrtgMonitoringRequest request)
    {
        if (!ValidBoundedSelection(request, out var reason, out var errorCode))
            return BadRequest(ApiResponse.Fail(errorCode, reason));
        var selectedHosts = request.HostIds.Distinct().Order().ToArray();
        var selectedSensors = request.SensorIds.Distinct().Order().ToArray();
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var eligibleSnapshot = EligibleHosts(sourceSnapshot, visible);
        var eligible = eligibleSnapshot.Hosts;
        if (eligible.Count > HostLimit) return Conflict(ApiResponse.Fail("scope_limit_exceeded", "NetIQ 主機超過 3,000 台，已拒絕完整目錄。"));
        if (selectedHosts.Any(id => !eligible.ContainsKey(id))) return Forbid();
        if (!TryReadFence(request.CatalogueToken, out var fence, out var error)) return error!;
        var store = backend.PrtgStore();
        if (!FenceMatches(fence!, Store().Get(), visible, store) || fence!.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "來源、可見範圍或目錄已變更；請重新載入後再估算。"));
        if (!fence.MapDate.HasValue || store.CountValidMonitoringSensors(fence.MapDate.Value, selectedHosts, selectedSensors) != selectedSensors.Length)
            return BadRequest(ApiResponse.Fail("invalid_selection", "所有 sensor 必須存在於目前最新全域對應日，且屬於所選主機；沒有 ID 會被截斷或忽略。"));
        var hostCandidates = store.GetMonitoringHostPage(eligible.Keys.ToArray(), null, 0, 1);
        var available = store.GetMonitoringSensorPage(fence.MapDate.Value, selectedHosts, null, 0, 1);
        if (!FenceMatches(fence, Store().Get(), VisibleHostIds(sourceSnapshot), store) || fence.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "估算期間來源、可見範圍或目錄已變更；請重新載入。"));
        var requestHash = HashRequest(request);
        var estimate = fence with { Kind = "estimate", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            HostHash = HashIds(selectedHosts), SensorHash = HashIds(selectedSensors), RequestHash = requestHash,
            SourceHash = EfPrtgObservationStore.SourceHintFor(new SystemSettingsStore(backend.Blob("system_settings")).Get().PrtgUrl) };
        var hostPages = (int)Math.Ceiling(hostCandidates.Total / (double)PageSize);
        var sensorPages = (int)Math.Ceiling(available.Total / (double)PageSize);
        var validationQueries = 2;
        return Ok(ApiResponse<object>.Ok(new { EstimateToken = ProtectToken(estimate), ExpiresAtUtc = estimate.ExpiresAtUtc,
            HostCount = selectedHosts.Length, SelectedSensorCount = selectedSensors.Length,
            EligibleHostCount = hostCandidates.Total, AvailableSensorCount = available.Total,
            HostCataloguePageRequestsAt100 = hostPages, SensorCataloguePageRequestsAt100 = sensorPages,
            SensorEligibilityChecksForEstimateAndSave = selectedSensors.Length,
            MinimumCataloguePageRequests = hostPages + sensorPages,
            MinimumQueryWork = hostPages + sensorPages + validationQueries + 2,
            QueryCost = "下限採完整 host/sensor 目錄頁數加估算與保存各一次的精確 sensor 範圍重驗，及兩項 SQL 候選計數；不代表實際 SQL 命令數、執行時間、磁碟容量或未測硬體吞吐保證。" }));
    }

    [HttpPut]
    public IActionResult Put([FromBody] PrtgMonitoringRequest request)
    {
        if (!ValidBoundedSelection(request, out var reason, out var errorCode)) return BadRequest(ApiResponse.Fail(errorCode, reason));
        if (!request.IdentityConfirmed)
            return BadRequest(ApiResponse.Fail("validation_failed", "正式保存前須確認來源身分與明列範圍。"));
        if (request.SourceChangeMode is not ("" or "new" or "continue" or "unknown"))
            return BadRequest(ApiResponse.Fail("validation_failed", "來源變更處理方式無效。"));
        var selectedHosts = request.HostIds.Distinct().Order().ToArray();
        var selectedSensors = request.SensorIds.Distinct().Order().ToArray();
        if (!TryReadToken(request.EstimateToken, out var estimate) || estimate!.Kind != "estimate" ||
            estimate.ExpiresAtUtc <= DateTimeOffset.UtcNow || estimate.HostHash != HashIds(request.HostIds) ||
            estimate.SensorHash != HashIds(request.SensorIds) || estimate.RequestHash != HashRequest(request))
            return Conflict(ApiResponse.Fail("estimate_required", "儲存前需對目前完整主機、sensor、來源資料重新估算；估算權杖無效或已逾期。"));
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var eligibleSnapshot = EligibleHosts(sourceSnapshot, visible);
        var eligible = eligibleSnapshot.Hosts;
        if (eligible.Count > HostLimit) return Conflict(ApiResponse.Fail("scope_limit_exceeded", "NetIQ 主機超過 3,000 台，已拒絕完整目錄。"));
        if (selectedHosts.Any(id => !eligible.ContainsKey(id))) return Forbid();
        var store = backend.PrtgStore();
        var sourceHint = EfPrtgObservationStore.SourceHintFor(new SystemSettingsStore(backend.Blob("system_settings")).Get().PrtgUrl);
        if (!FenceMatches(estimate, Store().Get(), VisibleHostIds(sourceSnapshot), store) || estimate.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "估算後來源、可見範圍或目錄已變更；請重新估算。"));
        if (estimate.SourceHash != sourceHint) return Conflict(ApiResponse.Fail("source_changed", "PRTG 連線來源已變更；請重新估算來源影響。"));
        if (!estimate.MapDate.HasValue || store.CountValidMonitoringSensors(estimate.MapDate.Value, selectedHosts, selectedSensors) != selectedSensors.Length)
            return Conflict(ApiResponse.Fail("selection_changed", "所選 ID 已不全屬於目前最新全域對應日；請重新選取與估算。"));
        var saveVisible = VisibleHostIds(sourceSnapshot);
        if (!FenceMatches(estimate, Store().Get(), saveVisible, store) || estimate.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "估算後來源、授權或目錄已變更；請重新估算。"));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(request.SourceTimeZoneId);
            _ = System.Globalization.CultureInfo.GetCultureInfo(request.SourceCultureName);
            var hint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            if (hint.Length == 0) throw new ArgumentException("請先儲存 PRTG 連線位址。");
            var result = Store().Update(p =>
            {
                var writeVisible = VisibleHostIds(sourceSnapshot);
                if (p.Revision != request.Revision || p.HostIds.Any(visibility.IsCaseGrantOnly) ||
                    (!user.Has(Capability.ViewAll) && !p.HostIds.All(writeVisible.Contains)) ||
                    !FenceMatches(estimate, p, writeVisible, store))
                    throw new InvalidOperationException("設定已變更或含不可見主機；重新載入後再修改。");
                var changed = IdentityChanged(p, request, hint);
                if (changed && p.SourceGeneration.Length > 0 && request.SourceChangeMode is not ("new" or "continue" or "unknown"))
                    throw new ArgumentException("來源已變更，請先預覽影響並選擇延續、新來源或無法確認。");
                if (request.SourceChangeMode == "continue" && (!CanContinue(p, request) ||
                    !request.ContinuityConfirmed || string.IsNullOrWhiteSpace(request.ContinuityEvidenceReference) ||
                    request.ContinuityEvidenceReference.Length > 1000))
                    throw new ArgumentException("延續須核對相同 Core、時區及語系，並提供來源身分核對證據；不確定請選新來源／無法確認。");
                if ((changed && request.SourceChangeMode != "continue") || request.SourceChangeMode is "new" or "unknown")
                { p.SourceGeneration = Guid.NewGuid().ToString("N"); p.ValidFrom = DateTimeOffset.Now;
                    p.ContinuityEvidenceReference = ""; p.ContinuityConfirmedAtUtc = null; }
                if (request.SourceChangeMode == "continue")
                { p.ContinuityEvidenceReference = request.ContinuityEvidenceReference.Trim(); p.ContinuityConfirmedAtUtc = DateTimeOffset.UtcNow; }
                p.CoreSystemId = request.SourceChangeMode == "unknown" ? "" : request.CoreSystemId.Trim(); p.EndpointHint = hint;
                p.SourceTimeZoneId = request.SourceTimeZoneId; p.SourceCultureName = request.SourceCultureName;
                p.HostIds = selectedHosts.ToList(); p.SensorIds = selectedSensors.ToList();
                p.Revision = Guid.NewGuid().ToString("N"); p.ConfirmedBy = user.UserId.ToString();
            });
            audit.Record("prtg_monitoring_confirm", "確認 Core 身分及 NetIQ 試點範圍；新身分不追認過去涵蓋。", "prtg", result.Revision);
            stamp.Bump();
            return Ok(ApiResponse<string>.Ok(result.Revision));
        }
        catch (ArgumentException ex) { return BadRequest(ApiResponse.Fail("validation_failed", ex.Message)); }
        catch (TimeZoneNotFoundException) { return BadRequest(ApiResponse.Fail("validation_failed", "找不到來源時區，請使用有效的 Windows 或 IANA 時區識別。")); }
        catch (InvalidTimeZoneException) { return BadRequest(ApiResponse.Fail("validation_failed", "來源時區資料無效。")); }
        catch (InvalidOperationException ex) { return Conflict(ApiResponse.Fail("conflict", ex.Message)); }
    }
    private PrtgMonitoringPolicyStore Store() => new(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
    private static bool IdentityChanged(PrtgMonitoringPolicy p, PrtgMonitoringRequest r, string hint) =>
        p.CoreSystemId != r.CoreSystemId.Trim() || p.EndpointHint != hint || p.SourceGeneration.Length == 0 ||
        p.SourceTimeZoneId != r.SourceTimeZoneId || p.SourceCultureName != r.SourceCultureName;
    private static bool CanContinue(PrtgMonitoringPolicy p, PrtgMonitoringRequest r) =>
        p.SourceGeneration.Length > 0 && p.CoreSystemId.Length > 0 && p.CoreSystemId == r.CoreSystemId.Trim() &&
        p.SourceTimeZoneId == r.SourceTimeZoneId && p.SourceCultureName == r.SourceCultureName;

    private IReadOnlySet<long> VisibleHostIds(PrtgHostSnapshot snapshot) => visibility.GetVisibleHostIds(snapshot)
        .Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();

    private static EligibleHostSnapshot EligibleHosts(PrtgHostSnapshot snapshot,
        IReadOnlySet<long> visible, IEnumerable<long>? requestedNames = null, bool includeAllNames = false)
    {
        var nameIds = requestedNames?.ToHashSet() ?? new HashSet<long>();
        var visibleNames = snapshot.Hosts.Where(h => (includeAllNames || visible.Contains(h.HostId)) && nameIds.Contains(h.HostId))
            .ToDictionary(h => h.HostId, h => h.HostName);
        var eligible = snapshot.Hosts.Where(h => visible.Contains(h.HostId) && h.Active &&
            h.MergedInto == null && h.Source == "netiq")
            .Take(HostLimit + 1)
            .ToDictionary(h => h.HostId, h => new EligibleHost(h.HostId, h.HostName));
        return new EligibleHostSnapshot(snapshot.Version, eligible, visibleNames);
    }

    private static PrtgSavedHost DescribeSavedHost(PrtgHostSnapshot snapshot, IReadOnlySet<long> visible, long id)
    {
        var host = snapshot.Find(id);
        var status = host is null ? "missing" : host.MergedInto.HasValue ? "merged" :
            !host.Active ? "inactive" : host.Source != "netiq" ? "not-netiq" :
            !visible.Contains(id) ? "not-visible" : "eligible";
        return new PrtgSavedHost(id, host?.HostName ?? $"已保存主機 #{id}", status);
    }

    private R01PageToken CaptureFence(PrtgMonitoringPolicy policy, IReadOnlySet<long> visible,
        EfPrtgStore store, long hostVersion) => new()
    {
        Kind = "fence", Owner = user.UserId.ToString(), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(20),
        PolicyRevision = policy.Revision, VisibleHash = HashIds(visible), HostVersion = hostVersion,
        PolicyStoreVersion = backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion(),
        ScopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(),
        CatalogueRevision = store.ReadCatalogueDataRevision(), MapRevision = store.ReadHostMapDataRevision(),
        SettingsRevision = new SystemSettingsStore(backend.Blob("system_settings")).Get().Revision,
        SettingsStoreVersion = backend.Blob("system_settings").ReadVersion(),
        MapDate = store.GetLatestHostMapDate()
    };

    private bool FenceMatches(R01PageToken token, PrtgMonitoringPolicy policy,
        IReadOnlySet<long> visible, EfPrtgStore store)
    {
        var hostVersion = backend.Blob("hosts").ReadVersion();
        return token.Owner == user.UserId.ToString() && token.ExpiresAtUtc > DateTimeOffset.UtcNow &&
            token.PolicyRevision == policy.Revision && token.VisibleHash == HashIds(visible) &&
            token.PolicyStoreVersion == backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion() &&
            token.HostVersion == hostVersion && token.ScopeRevision == backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() &&
            token.CatalogueRevision == store.ReadCatalogueDataRevision() && token.MapRevision == store.ReadHostMapDataRevision() &&
            token.SettingsRevision == new SystemSettingsStore(backend.Blob("system_settings")).Get().Revision &&
            token.SettingsStoreVersion == backend.Blob("system_settings").ReadVersion() &&
            token.MapDate == store.GetLatestHostMapDate();
    }

    private string ProtectToken(R01PageToken token) => protection.CreateProtector(TokenPurpose)
        .Protect(JsonSerializer.Serialize(token));

    private bool TryReadToken(string? text, out R01PageToken? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return false;
        try
        {
            token = JsonSerializer.Deserialize<R01PageToken>(protection.CreateProtector(TokenPurpose).Unprotect(text));
            return token != null && token.Owner == user.UserId.ToString() && token.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        { return false; }
    }

    private bool TryReadFence(string? text, out R01PageToken? token, out IActionResult? error)
    {
        error = null;
        if (!TryReadToken(text, out token) || token!.Kind != "fence")
        {
            error = BadRequest(ApiResponse.Fail("invalid_token", "目錄版本權杖無效或已逾期，請重新載入。"));
            return false;
        }
        return true;
    }

    private bool TryReadCursor(string? text, string kind, string? search, IReadOnlyCollection<long> hostIds,
        out R01PageToken? token, out IActionResult? error)
    {
        error = null;
        if (!TryReadToken(text, out token) || token!.Kind != kind || token.Search != NormalizeSearch(search) ||
            token.HostHash != HashIds(hostIds))
        {
            error = BadRequest(ApiResponse.Fail("invalid_cursor", "目錄游標無效、逾期或與目前搜尋範圍不符。"));
            return false;
        }
        return true;
    }

    private static string NormalizeSearch(string? search) => (search ?? "").Trim();
    private static string HashIds(IEnumerable<long> ids) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(",", ids.Distinct().Order()))));

    private sealed record EligibleHost(long HostId, string HostName);
    private sealed record EligibleHostSnapshot(long Version, Dictionary<long, EligibleHost> Hosts,
        Dictionary<long, string> VisibleHostNames);

    [HttpPost("source-preview")]
    public IActionResult SourcePreview([FromBody] PrtgMonitoringRequest request)
    {
        var policy = Store().Get();
        if (!ValidBoundedSelection(request, out var reason, out var errorCode)) return BadRequest(ApiResponse.Fail(errorCode, reason));
        if (!TryReadToken(request.EstimateToken, out var estimate) || estimate!.Kind != "estimate" ||
            estimate.RequestHash != HashRequest(request) || estimate.HostHash != HashIds(request.HostIds) ||
            estimate.SensorHash != HashIds(request.SensorIds))
            return Conflict(ApiResponse.Fail("estimate_required", "請先對目前精確範圍完成估算，再預覽來源影響。"));
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        if (policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!user.Has(Capability.ViewAll) && !policy.HostIds.All(visible.Contains))) return Forbid();
        if (policy.Revision != request.Revision || !FenceMatches(estimate, policy, visible, backend.PrtgStore()))
            return Conflict(ApiResponse.Fail("conflict", "設定、來源、權限或目錄已變更，請重新估算。"));
        if (request.CoreSystemId == null) return BadRequest(ApiResponse.Fail("validation_failed", "請填來源身分。"));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        if (estimate.SourceHash != EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl))
            return Conflict(ApiResponse.Fail("source_changed", "PRTG 連線來源已變更，請重新估算。"));
        var savedHosts = policy.HostIds.Distinct().Order().ToArray();
        var proposedHosts = request.HostIds.Distinct().Order().ToArray();
        var unionHosts = savedHosts.Concat(proposedHosts).Distinct().Order().ToArray();
        var eligibleSnapshot = EligibleHosts(sourceSnapshot, visible, unionHosts, user.Has(Capability.ViewAll));
        if (eligibleSnapshot.Hosts.Count > HostLimit)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "可管理 NetIQ 主機超過 3,000 台；已拒絕完整影響預覽。"));
        if (request.HostIds.Any(id => !eligibleSnapshot.Hosts.ContainsKey(id))) return Forbid();
        if (!estimate.MapDate.HasValue || backend.PrtgStore().CountValidMonitoringSensors(
                estimate.MapDate.Value, request.HostIds, request.SensorIds) != request.SensorIds.Distinct().Count())
            return Conflict(ApiResponse.Fail("selection_changed", "估算後所選 sensor 已移動、暫停或不再位於所選主機；請重新估算。"));
        var savedSensors = policy.SensorIds.Distinct().Order().ToArray();
        var proposedSensors = request.SensorIds.Distinct().Order().ToArray();
        var unionSensors = savedSensors.Concat(proposedSensors).Distinct().Order().ToArray();
        using var db = backend.CreateContext();
        var existingObservations = 0;
        foreach (var batch in unionHosts.Chunk(1000))
            existingObservations += db.PrtgObservations.Count(o => batch.Contains(o.HostId));
        var hostNames = unionHosts.Where(eligibleSnapshot.VisibleHostNames.ContainsKey)
            .Select(id => eligibleSnapshot.VisibleHostNames[id]).ToArray();
        var openCases = backend.IssueCaseStore().GetMany(hostNames)
            .Count(c => c.ClosedAt == null && c.PrtgEvidence != null);
        if (!FenceMatches(estimate, Store().Get(), VisibleHostIds(sourceSnapshot), backend.PrtgStore()) ||
            estimate.HostVersion != sourceSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "來源、權限或目錄在來源影響預覽期間變更；請重新估算。"));
        var changed = IdentityChanged(policy, request, EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl)) || request.SourceChangeMode is "new" or "unknown";
        var result = new {
            Changed = changed,
            ContinuationAllowed = CanContinue(policy, request),
            SavedHostIds = savedHosts, ProposedHostIds = proposedHosts, UnionHostIds = unionHosts,
            SavedSensorIds = savedSensors, ProposedSensorIds = proposedSensors, UnionSensorIds = unionSensors,
            AffectedHosts = unionHosts.Length, AffectedSensors = unionSensors.Length,
            ExistingObservations = existingObservations, WarmingSensors = "unknown (not read)",
            OpenPrtgCases = openCases, ExistingObservationsPreserved = true, ExistingCasesPreserved = true,
            Message = "新來源重新暖機；無法確認時停止正式判定。已保存的歷史觀察與案件保留；延續只適用有身分證據的同 Core 搬址／憑證輪替，不以 URL 或相同文字 ID 單獨證明。" };
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("preview")]
    public IActionResult Preview([FromQuery] DateTime? day = null, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
    {
        if (offset < 0 || offset > 30000 || limit is < 1 or > PageSize || day > DateTime.Today)
            return BadRequest(ApiResponse.Fail("validation_failed", "預覽日期不可在未來；每頁最多 100 筆，頁面位置不得超過 30,000。"));
        var targetDay = (day ?? DateTime.Today.AddDays(-1)).Date;
        var sourceSnapshot = hosts.CapturePrtgSnapshot();
        var visible = VisibleHostIds(sourceSnapshot);
        var hostVersion = sourceSnapshot.Version;
        var allHosts = sourceSnapshot.Hosts;
        var eligibleCount = allHosts.Count(h => visible.Contains(h.HostId) && h.Active && h.MergedInto == null && h.Source == "netiq");
        if (eligibleCount > HostLimit)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "可見 NetIQ 範圍超過 3,000 台；評估預覽已拒絕，不會回傳部分結果。"));
        var fullScope = visible.Count > 0 && eligibleCount > 0 && allHosts.All(h => visible.Contains(h.HostId));
        var visibleNetiqHosts = allHosts.Where(h => visible.Contains(h.HostId) && h.Active && h.MergedInto == null && h.Source == "netiq")
            .Select(h => h.HostId).ToArray();
        var settingsBlob = backend.Blob("system_settings");
        var settingsStoreVersion = settingsBlob.ReadVersion();
        var settings = new SystemSettingsStore(settingsBlob).Get();
        var policyStoreVersion = backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion();
        var policy = Store().Get();
        var scopeRevisionVersion = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var store = backend.PrtgStore();
        var catalogueRevision = store.ReadCatalogueDataRevision();
        var hostMapDataRevision = store.ReadHostMapDataRevision();
        var mapDate = store.GetLatestHostMapDate();
        var dataPage = store.GetMonitoringEvaluationPage(mapDate, visibleNetiqHosts, fullScope, offset, limit);
        var pageHostIds = dataPage.Rows.Where(row => row.HostId.HasValue).Select(row => row.HostId!.Value).ToHashSet();
        var hostById = allHosts.Where(h => pageHostIds.Contains(h.HostId)).ToDictionary(h => h.HostId);
        var policySensors = policy.SensorIds.ToHashSet();
        var rulesVersion = backend.Blob("rules").ReadVersion();
        var ruleStore = new KnownIssueRuleStore(backend.Blob("rules"));
        List<KnownIssueRule> sourceRules;
        if (!ruleStore.Exists)
        {
            sourceRules = KnownIssueSeed.CreateRules();
        }
        else
        {
            var loadedRules = ruleStore.Load();
            sourceRules = loadedRules.Success && loadedRules.Content != null
                ? loadedRules.Content.Rules
                : KnownIssueSeed.CreateRules();
        }
        var rules = RuleValidator.Validate(sourceRules).ValidRules
            .Where(r => r.Enabled && string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase)).ToArray();
        var hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            allHosts.OrderBy(h => h.HostId).Select(h => new
            { h.HostId, h.HostName, h.IpAddress, h.Active, h.MergedInto, h.SentinelId, h.Source })))));
        var sentinelVersion = backend.Blob("sentinels").ReadVersion();
        var sentinelRows = new SentinelStore(backend.Blob("sentinels")).GetAll().OrderBy(s => s.SentinelId)
            .Select(s => new { s.SentinelId, s.Active, s.BaseUrl });
        var sentinelFingerprint = JsonSerializer.Serialize(sentinelRows);
        if (HashIds(VisibleHostIds(sourceSnapshot)) != HashIds(visible) ||
            hostVersion != backend.Blob("hosts").ReadVersion() || sentinelVersion != backend.Blob("sentinels").ReadVersion() ||
            rulesVersion != backend.Blob("rules").ReadVersion())
            return Conflict(ApiResponse.Fail("catalogue_changed", "範圍或 sentinel 設定在預覽期間變更，請重新查詢。"));
        var scopeRevision = $"{scopeRevisionVersion}:{policyStoreVersion}:{rulesVersion}:{sentinelFingerprint}:{hostFingerprint}";
        var rows = dataPage.Rows.Select(sensor =>
        {
            hostById.TryGetValue(sensor.HostId ?? 0, out var host);
            var proof = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.SensorId)).Get();
            var matching = rules.Where(r => PrtgFormalEligibility.RuleCategoryMatches(r, sensor.Category) &&
                (r.PrtgRuleCode is "down" or "warning" or "flapping" || r.PrtgRuleCode == "disk_free_trend" && sensor.Category == "disk" && r.PrtgSensorCategory == "disk"))
                .GroupBy(r => r.PrtgRuleCode).Select(g => g.OrderByDescending(r => r.PrtgSensorCategory != null).ThenBy(r => r.Id, StringComparer.Ordinal).First()).ToArray();
            var reasons = new List<string>();
            if (sensor.MapStatus != PrtgMapStatus.Ok || host == null) reasons.Add(sensor.MapStatus == PrtgMapStatus.Conflict ? "主機對應衝突" : "尚無有效主機對應");
            if (!PrtgFormalEligibility.HostAllowed(host?.ToWebHost(), settings, policy)) reasons.Add("未啟用、來源未確認或主機不在有效 NetIQ 試點");
            if (!policySensors.Contains(sensor.SensorId)) reasons.Add("感測器未納入試點");
            if (matching.Length == 0) reasons.Add("沒有適用且啟用的正式規則");
            var configured = reasons.Count == 0;
            if (proof.HostId != host?.HostId || proof.SourceGeneration != policy.SourceGeneration || proof.ResourceGeneration.Length == 0 ||
                proof.MappingRevision != backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion()) reasons.Add("來源／資源／對應身分待確認");
            var begin = new DateTimeOffset(targetDay); var finish = begin.AddDays(1);
            var periods = proof.Periods(begin, finish);
            if (periods.Sum(p => (p.Through - p.From).TotalSeconds) < (finish - begin).TotalSeconds ||
                periods.Any(p => p.Status == "Unknown")) reasons.Add("目標日狀態涵蓋不完整；可能仍有局部可信判定，未覆蓋區間保持未知");
            var parent = host == null ? null : backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName }).ReadRecent(targetDay, 1).FirstOrDefault(r => r.Date == targetDay);
            var parentQualified = parent?.CanSupplementWithPrtg() == true;
            if (!parentQualified) reasons.Add("等待目標日 NetIQ 成功分析");
            if (matching.Any(r => r.PrtgRuleCode == "disk_free_trend"))
                reasons.Add("磁碟仍須正式當輪語意重驗與 28 日 readiness；預覽不取數、不解鎖資格");
            return new { Objid = sensor.SensorId, SensorName = sensor.Name, sensor.Category, HostId = host?.HostId, HostName = host?.HostName,
                Selected = policySensors.Contains(sensor.SensorId), RuleIds = matching.Select(r => r.Id),
                ConfiguredForEvaluation = configured, NetiqQualified = parentQualified,
                CanPublishIfTrustedFinding = configured && parentQualified,
                Disposition = configured && parentQualified ? "可信命中後沿既有交辦／靜音／派工政策；無合格責任人保留未指派" : "不發布正式問題／交辦",
                Notification = configured && parentQualified ? "重大命中後依時效、去重、靜音及逐人權限／路由守門；不保證寄出" : "不通知",
                Reasons = reasons };
        }).ToArray();
        if (HashIds(VisibleHostIds(sourceSnapshot)) != HashIds(visible) ||
            hostVersion != backend.Blob("hosts").ReadVersion() || sentinelVersion != backend.Blob("sentinels").ReadVersion() ||
            settingsStoreVersion != settingsBlob.ReadVersion() || policyStoreVersion != backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion() ||
            scopeRevisionVersion != backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() || rulesVersion != backend.Blob("rules").ReadVersion() ||
            catalogueRevision != store.ReadCatalogueDataRevision() || hostMapDataRevision != store.ReadHostMapDataRevision())
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機、政策、規則、目錄或來源範圍在預覽期間變更，請重新查詢。"));
        return Ok(ApiResponse<object>.Ok(new { Day = targetDay, SettingsRevision = settings.Revision, PolicyRevision = policy.Revision,
            ScopeRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeRevision))), Total = dataPage.Total, Offset = offset,
            Complete = offset == 0 && rows.Length == dataPage.Total, HasMore = offset + rows.Length < dataPage.Total, Rows = rows,
            Limitations = "預覽是現在資格與條件，不預測命中、責任人或信箱實收；正式提交及通知仍重新守門。部分管理者不揭露不可歸戶資源。" }));
    }
}

public sealed class PrtgMonitoringRequest
{
    public string SourceChangeMode { get; set; } = "";
    public bool ContinuityConfirmed { get; set; }
    public string ContinuityEvidenceReference { get; set; } = "";
    public string Revision { get; set; } = "";
    public string CoreSystemId { get; set; } = "";
    public string SourceTimeZoneId { get; set; } = "";
    public string SourceCultureName { get; set; } = "";
    public bool IdentityConfirmed { get; set; }
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
    public string CatalogueToken { get; set; } = "";
    public string? EstimateToken { get; set; }
}

public sealed class PrtgMonitoringPageRequest
{
    public string CatalogueToken { get; set; } = "";
    public string? Cursor { get; set; }
    public string? Search { get; set; }
    public List<long> HostIds { get; set; } = [];
    public int Offset { get; set; }
}

public sealed record PrtgSavedHost(long HostId, string HostName, string Status);

public sealed record R01PageToken
{
    public string Kind { get; init; } = "";
    public string Owner { get; init; } = "";
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string PolicyRevision { get; init; } = "";
    public long PolicyStoreVersion { get; init; }
    public string VisibleHash { get; init; } = "";
    public long HostVersion { get; init; }
    public long ScopeRevision { get; init; }
    public long CatalogueRevision { get; init; }
    public long MapRevision { get; init; }
    public DateTime? MapDate { get; init; }
    public string Search { get; init; } = "";
    public int Offset { get; init; }
    public string HostHash { get; init; } = "";
    public string SensorHash { get; init; } = "";
    public string RequestHash { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public int Total { get; init; }
    public string SettingsRevision { get; init; } = "";
    public long SettingsStoreVersion { get; init; }
}

public sealed partial class PrtgMonitoringController
{
    private static bool ValidBoundedSelection(PrtgMonitoringRequest request, out string reason, out string code)
    {
        reason = "";
        code = "validation_failed";
        if (request.HostIds is null || request.SensorIds is null)
        {
            reason = "主機與 sensor 清單必須明確提供。";
            return false;
        }
        if (request.HostIds.Count > HostLimit || request.HostIds.Any(id => id <= 0) ||
            request.HostIds.Distinct().Count() is < 1 or > HostLimit)
        {
            code = "scope_limit_exceeded";
            reason = "主機清單需含 1 至 3,000 個有效且去重的 ID；超出完整支援範圍會拒絕，不會截斷。";
            return false;
        }
        if (request.SensorIds.Count > SensorLimit && request.SensorIds.Count < 30000)
        {
            code = "scope_limit_exceeded";
            reason = "sensor 清單上限為 15,000；擴量至 30,000 尚不支援，清單不會截斷。";
            return false;
        }
        if (request.SensorIds.Count >= 30000)
        {
            code = "unsupported_capacity";
            reason = "30,000 顆 sensor 的擴量範圍目前不支援；請縮小至 15,000 顆以內。";
            return false;
        }
        if (request.SensorIds.Any(id => id <= 0) || request.SensorIds.Distinct().Count() is < 1 or > SensorLimit)
        {
            code = "scope_limit_exceeded";
            reason = "sensor 清單需含 1 至 15,000 個有效且去重的 ID；超出範圍會拒絕，不會截斷。";
            return false;
        }
        if (request.SourceTimeZoneId is null || request.SourceCultureName is null || request.CoreSystemId is null ||
            request.SourceChangeMode is null || request.ContinuityEvidenceReference is null ||
            request.SourceTimeZoneId.Length is < 1 or > 128 || request.SourceCultureName.Length is < 1 or > 64 ||
            request.CoreSystemId?.Length is > 256 || request.ContinuityEvidenceReference?.Length is > 1000)
        {
            reason = "來源資訊長度超出上限。";
            return false;
        }
        if (request.SourceChangeMode != "unknown" && string.IsNullOrWhiteSpace(request.CoreSystemId))
        {
            reason = "請填入 PRTG Core 身分，或明確選擇無法確認。";
            return false;
        }
        return true;
    }

    private static string HashRequest(PrtgMonitoringRequest request)
    {
        var canonical = new
        {
            request.Revision,
            CoreSystemId = request.CoreSystemId?.Trim() ?? "",
            SourceTimeZoneId = request.SourceTimeZoneId ?? "",
            SourceCultureName = request.SourceCultureName ?? "",
            SourceChangeMode = request.SourceChangeMode ?? "",
            request.ContinuityConfirmed,
            ContinuityEvidenceReference = request.ContinuityEvidenceReference?.Trim() ?? "",
            request.IdentityConfirmed,
            HostIds = request.HostIds?.Distinct().Order().ToArray() ?? [],
            SensorIds = request.SensorIds?.Distinct().Order().ToArray() ?? []
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }
}
