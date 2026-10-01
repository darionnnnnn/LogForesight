using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/monitoring"), Permission(Capability.Maintain)]
public sealed class PrtgMonitoringController(StorageBackend backend, IHostStore hosts,
    IVisibilityService visibility, ICurrentUser user, IAuditService audit, DataVersionStamp stamp) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var policy = Store().Get();
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var maps = backend.PrtgStore().GetLatestHostMap().Where(m => m.HostId.HasValue && visible.Contains(m.HostId.Value) && m.MapStatus == PrtgMapStatus.Ok)
            .ToDictionary(m => m.DeviceObjid, m => m.HostId!.Value);
        var eligibleSensors = backend.PrtgStore().GetSensorStatuses().Where(s => maps.ContainsKey(s.DeviceObjid)).ToArray();
        var sensors = eligibleSensors.Select(s => new
        { SensorId = s.Objid, HostId = maps[s.DeviceObjid], Name = s.SensorName, s.SensorType, s.Category,
            Selected = policy.SensorIds.Contains(s.Objid),
            Evidence = policy.SensorIds.Contains(s.Objid) ? EvidenceSummary(s.Objid) : null })
            .OrderByDescending(s => s.Selected).ThenBy(s => s.SensorId).Take(500).ToArray();
        return Ok(ApiResponse<object>.Ok(new { policy.Revision, policy.CoreSystemId, policy.ValidFrom,
            policy.SourceTimeZoneId, policy.SourceCultureName, SuggestedTimeZoneId = TimeZoneInfo.Local.Id,
            Enabled = settings.PrtgEnabled, Ready = policy.Ready(settings.PrtgUrl), HostIds = policy.HostIds.Where(visible.Contains),
            SensorIds = sensors.Where(s => s.Selected).Select(s => s.SensorId), Sensors = sensors,
            SensorsTotal = eligibleSensors.Length, SensorsTruncated = eligibleSensors.Length > sensors.Length,
            CanEdit = policy.HostIds.All(visible.Contains),
            Hosts = hosts.GetAll().Where(h => visible.Contains(h.HostId) && h.Active && h.MergedInto == null && h.Source == "netiq")
                .Select(h => new { h.HostId, h.HostName }) }));
    }

    [HttpPut]
    public IActionResult Put([FromBody] PrtgMonitoringRequest request)
    {
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        var eligible = hosts.GetAll().Where(h => visible.Contains(h.HostId) && h.Active && h.MergedInto == null && h.Source == "netiq")
            .Select(h => h.HostId).ToHashSet();
        if (request.HostIds == null || request.SensorIds == null || !request.IdentityConfirmed || string.IsNullOrWhiteSpace(request.CoreSystemId) || request.CoreSystemId.Length > 128 ||
            request.HostIds.Count is < 1 or > 50 || request.SensorIds.Count is < 1 or > 100 ||
            request.HostIds.Any(h => !eligible.Contains(h)))
            return BadRequest(ApiResponse.Fail("validation_failed", "請確認 Core System ID，並選取可見且啟用的 NetIQ 試點（最多 50 台／100 顆 sensor）。"));
        var maps = backend.PrtgStore().GetLatestHostMap().Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && request.HostIds.Contains(m.HostId.Value))
            .Select(m => m.DeviceObjid).ToHashSet();
        var sensors = backend.PrtgStore().GetSensorStatuses().Where(s => maps.Contains(s.DeviceObjid)).Select(s => s.Objid).ToHashSet();
        if (request.SensorIds.Any(id => !sensors.Contains(id)))
            return BadRequest(ApiResponse.Fail("validation_failed", "sensor 必須有唯一、有效且屬於所選 NetIQ 主機的對應。請先同步與處理對應衝突。"));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(request.SourceTimeZoneId);
            _ = System.Globalization.CultureInfo.GetCultureInfo(request.SourceCultureName);
            var hint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            if (hint.Length == 0) throw new ArgumentException("請先儲存 PRTG 連線位址。");
            var result = Store().Update(p =>
            {
                if (p.Revision != request.Revision || !p.HostIds.All(visible.Contains))
                    throw new InvalidOperationException("設定已變更或含不可見主機；重新載入後再修改。");
                if (p.CoreSystemId != request.CoreSystemId.Trim() || p.EndpointHint != hint || p.SourceGeneration.Length == 0 ||
                    p.SourceTimeZoneId != request.SourceTimeZoneId || p.SourceCultureName != request.SourceCultureName)
                { p.SourceGeneration = Guid.NewGuid().ToString("N"); p.ValidFrom = DateTimeOffset.Now; }
                p.CoreSystemId = request.CoreSystemId.Trim(); p.EndpointHint = hint;
                p.SourceTimeZoneId = request.SourceTimeZoneId; p.SourceCultureName = request.SourceCultureName;
                p.HostIds = request.HostIds.Distinct().Order().ToList(); p.SensorIds = request.SensorIds.Distinct().Order().ToList();
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
    private object EvidenceSummary(long id)
    {
        var e = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + id)).Get();
        return new { e.QualityReason, e.LastAttemptAt, e.ValidFrom, e.SourceGeneration, e.ResourceGeneration,
            CoveredFrom = e.Coverage.FirstOrDefault()?.From, CoveredThrough = e.Coverage.LastOrDefault()?.Through,
            StateCount = e.States.Count, CoverageSpanCount = e.Coverage.Count,
            e.DiskSemanticValidFrom, e.DiskSemanticCheckedAt,
            DiskReadyAfter = e.DiskSemanticValidFrom?.AddDays(28) };
    }
}

public sealed class PrtgMonitoringRequest
{
    public string Revision { get; set; } = "";
    public string CoreSystemId { get; set; } = "";
    public string SourceTimeZoneId { get; set; } = "";
    public string SourceCultureName { get; set; } = "";
    public bool IdentityConfirmed { get; set; }
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
}
