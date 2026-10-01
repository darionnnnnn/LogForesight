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
        if (request.SourceChangeMode is not ("" or "new" or "continue" or "unknown") ||
            request.HostIds == null || request.SensorIds == null || !request.IdentityConfirmed || request.CoreSystemId == null ||
            (request.SourceChangeMode != "unknown" && string.IsNullOrWhiteSpace(request.CoreSystemId)) || request.CoreSystemId.Length > 128 ||
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
    private static bool IdentityChanged(PrtgMonitoringPolicy p, PrtgMonitoringRequest r, string hint) =>
        p.CoreSystemId != r.CoreSystemId.Trim() || p.EndpointHint != hint || p.SourceGeneration.Length == 0 ||
        p.SourceTimeZoneId != r.SourceTimeZoneId || p.SourceCultureName != r.SourceCultureName;
    private static bool CanContinue(PrtgMonitoringPolicy p, PrtgMonitoringRequest r) =>
        p.SourceGeneration.Length > 0 && p.CoreSystemId.Length > 0 && p.CoreSystemId == r.CoreSystemId.Trim() &&
        p.SourceTimeZoneId == r.SourceTimeZoneId && p.SourceCultureName == r.SourceCultureName;

    [HttpPost("source-preview")]
    public IActionResult SourcePreview([FromBody] PrtgMonitoringRequest request)
    {
        var policy = Store().Get();
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        if (!policy.HostIds.All(visible.Contains)) return Forbid();
        if (policy.Revision != request.Revision) return Conflict(ApiResponse.Fail("conflict", "設定已變更，請重新載入後預覽。"));
        if (request.CoreSystemId == null) return BadRequest(ApiResponse.Fail("validation_failed", "請填來源身分。"));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        using var db = backend.CreateContext();
        var identities = policy.SensorIds.Select(id => new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + id)).Get()).ToArray();
        return Ok(ApiResponse<object>.Ok(new {
            Changed = IdentityChanged(policy, request, EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl)) || request.SourceChangeMode is "new" or "unknown",
            ContinuationAllowed = CanContinue(policy, request),
            AffectedHosts = policy.HostIds.Count, AffectedSensors = policy.SensorIds.Count,
            ExistingObservations = db.PrtgObservations.Count(o => policy.HostIds.Contains(o.HostId)),
            WarmingSensors = identities.Count(e => e.DiskSemanticValidFrom.HasValue),
            OpenPrtgCases = backend.IssueCaseStore().GetMany(hosts.GetAll().Where(h => policy.HostIds.Contains(h.HostId)).Select(h => h.HostName).ToArray())
                .Count(c => c.ClosedAt == null && c.PrtgEvidence != null),
            Message = "新來源重新暖機；無法確認時停止正式判定。原對應須重新核對，既有交辦與人工結論保留；延續只適用有身分證據的同 Core 搬址／憑證輪替，不以 URL 或相同文字 ID 單獨證明。" }));
    }

    [HttpGet("preview")]
    public IActionResult Preview([FromQuery] DateTime? day = null, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
    {
        if (offset < 0 || limit is < 1 or > 500 || day > DateTime.Today)
            return BadRequest(ApiResponse.Fail("validation_failed", "預覽日期不可在未來；每頁最多 500 筆。"));
        var targetDay = (day ?? DateTime.Today.AddDays(-1)).Date;
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        var allHosts = hosts.GetAll(); var fullScope = allHosts.All(h => visible.Contains(h.HostId));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get(); var policy = Store().Get();
        var maps = backend.PrtgStore().GetLatestHostMap().ToDictionary(m => m.DeviceObjid);
        var candidates = backend.PrtgStore().GetSensorStatuses().Where(s => visible.Count > 0 && (
            maps.TryGetValue(s.DeviceObjid, out var m) && m.HostId.HasValue ? visible.Contains(m.HostId.Value) : fullScope)
            )
            .OrderByDescending(s => policy.SensorIds.Contains(s.Objid)).ThenBy(s => s.Objid).ToArray();
        var (content, fallback) = RuleBootstrapper.LoadContent(new KnownIssueRuleStore(backend.Blob("rules")));
        var rules = fallback ? [] : RuleValidator.Validate(content.Rules).ValidRules.Where(r => r.Enabled && r.Platform == "prtg").ToArray();
        var rows = candidates.Skip(offset).Take(limit).Select(sensor =>
        {
            maps.TryGetValue(sensor.DeviceObjid, out var map);
            var host = allHosts.FirstOrDefault(h => h.HostId == map?.HostId);
            var proof = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid)).Get();
            var matching = rules.Where(r => PrtgFormalEligibility.RuleCategoryMatches(r, sensor.Category) &&
                (r.PrtgRuleCode is "down" or "warning" or "flapping" || r.PrtgRuleCode == "disk_free_trend" && sensor.Category == "disk" && r.PrtgSensorCategory == "disk"))
                .GroupBy(r => r.PrtgRuleCode).Select(g => g.OrderByDescending(r => r.PrtgSensorCategory != null).ThenBy(r => r.Id, StringComparer.Ordinal).First()).ToArray();
            var reasons = new List<string>();
            if (map?.MapStatus != PrtgMapStatus.Ok || host == null) reasons.Add(map?.MapStatus == PrtgMapStatus.Conflict ? "主機對應衝突" : "尚無有效主機對應");
            if (!PrtgFormalEligibility.HostAllowed(host, settings, policy)) reasons.Add("未啟用、來源未確認或主機不在有效 NetIQ 試點");
            if (!policy.SensorIds.Contains(sensor.Objid)) reasons.Add("感測器未納入試點");
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
            return new { sensor.Objid, sensor.SensorName, sensor.Category, HostId = host?.HostId, HostName = host?.HostName,
                Selected = policy.SensorIds.Contains(sensor.Objid), RuleIds = matching.Select(r => r.Id),
                ConfiguredForEvaluation = configured, NetiqQualified = parentQualified,
                CanPublishIfTrustedFinding = configured && parentQualified,
                Disposition = configured && parentQualified ? "可信命中後沿既有交辦／靜音／派工政策；無合格責任人保留未指派" : "不發布正式問題／交辦",
                Notification = configured && parentQualified ? "重大命中後依時效、去重、靜音及逐人權限／路由守門；不保證寄出" : "不通知",
                Reasons = reasons };
        }).ToArray();
        return Ok(ApiResponse<object>.Ok(new { Day = targetDay, SettingsRevision = settings.Revision, PolicyRevision = policy.Revision,
            ScopeRevision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                new PrtgScopeRevisionReader(backend, hosts).Read()))), Total = candidates.Length, Offset = offset,
            Complete = offset == 0 && rows.Length == candidates.Length, HasMore = offset + rows.Length < candidates.Length, Rows = rows,
            Limitations = "預覽是現在資格與條件，不預測命中、責任人或信箱實收；正式提交及通知仍重新守門。部分管理者不揭露不可歸戶資源。" }));
    }
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
}
