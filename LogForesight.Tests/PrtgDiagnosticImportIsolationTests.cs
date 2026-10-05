using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiagnosticImportIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-diag-import-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    public PrtgDiagnosticImportIsolationTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        _hosts = new(_backend.Blob("hosts"));
        _hosts.Upsert(new() { HostName = "one", Source = "netiq", Active = true });
        _hosts.Upsert(new() { HostName = "two", Source = "netiq", Active = true });
    }

    [Fact]
    public void Import_只隔離診斷包且重複內容同一識別()
    {
        const string sourceUrl = "https://fixture.example/prtg";
        var store = _backend.PrtgStore();
        store.UpsertDevices([new() { Objid = 10, Name = "formal" }], DateTime.Today);
        store.UpsertSensors([new() { Objid = 100, DeviceObjid = 10, Name = "formal sensor" }], DateTime.Today);
        store.AppendStateChanges([new() { SensorObjid = 100, ChangedAt = DateTime.Today.AddHours(1), Status = "Up", Quality = PrtgDataQuality.Ok }]);
        store.UpsertValues([new() { SensorObjid = 100, PeriodStart = DateTime.Today, AvgValue = 41, Quality = PrtgDataQuality.Ok }]);
        store.ReplaceHostMapForDate(DateTime.Today, [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 77, HostName = "formal", MapStatus = PrtgMapStatus.Ok }]);
        store.UpsertManualMap(new() { DeviceObjid = 10, HostId = 77 });
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(p => { p.CoreSystemId = "fixture-core"; p.SourceGeneration = "generation"; p.Revision = "revision"; p.EndpointHint = EfPrtgObservationStore.SourceHintFor(sourceUrl); p.ValidFrom = DateTime.Today; p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US"; p.HostIds = [77]; p.SensorIds = [100]; });
        Assert.True(policyStore.Get().Ready(sourceUrl));
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 100)).Update(t =>
        { t.SensorId = 100; t.HostId = 77; t.SourceGeneration = "generation"; t.ResourceGeneration = "resource"; t.IdentityFingerprint = "identity"; t.ValidFrom = DateTime.Today; t.QualityReason = "covered"; });
        new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Update(all =>
            all[100] = new PrtgDiskSemanticEvidence(100, 10, 77, "disk", "used", "Used", "%", 1, "up", PrtgDiskSemanticEvidenceSource.Manual, 1, "confirmed", DateTime.UtcNow, "v1"));
        var policyBefore = System.Text.Json.JsonSerializer.Serialize(policyStore.Get());
        var devicesBefore = System.Text.Json.JsonSerializer.Serialize(store.GetAllDevices());
        var sensorsBefore = System.Text.Json.JsonSerializer.Serialize(store.GetAllSensors());
        var changesBefore = System.Text.Json.JsonSerializer.Serialize(store.GetStateChanges(DateTime.Today, DateTime.Today.AddDays(1)));
        var valueBefore = System.Text.Json.JsonSerializer.Serialize(store.GetValues(DateTime.Today, DateTime.Today.AddDays(1)));
        var mapsBefore = System.Text.Json.JsonSerializer.Serialize(store.GetHostMapForDate(DateTime.Today));
        var manualBefore = System.Text.Json.JsonSerializer.Serialize(store.GetManualMaps());
        string ExistingBlobSnapshot()
        {
            using var context = _backend.CreateContext();
            return System.Text.Json.JsonSerializer.Serialize(context.Blobs.Where(b => !b.BlobKey.StartsWith("prtg_import_diagnostic_")).OrderBy(b => b.BlobKey).ToList());
        }
        string EvidenceSnapshot()
        {
            using var context = _backend.CreateContext();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                Observations = context.PrtgObservations.ToList(),
                Cases = context.IssueCases.ToList(),
                Orders = context.WorkOrders.ToList()
            });
        }
        var blobsBefore = ExistingBlobSnapshot();
        var evidenceBefore = EvidenceSnapshot();
        var pkg = new PrtgDataPackage { FormatVersion = 2, Devices = [new() { Objid = 10, Name = "foreign" }], Sensors = [new() { Objid = 100, DeviceObjid = 10, Name = "foreign" }], StateChanges = [new() { SensorObjid = 100, ChangedAt = DateTime.Today.AddHours(2), Status = "Down", Quality = PrtgDataQuality.Ok }], Values = [new() { SensorObjid = 100, PeriodStart = DateTime.Today, AvgValue = 99, Quality = PrtgDataQuality.Sampled }], HostMaps = [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 999, HostName = "foreign", MapStatus = PrtgMapStatus.Ok }], ManualMaps = [new() { DeviceObjid = 10, HostId = 999 }], Observations = [new() { SnapshotId = "foreign", HostId = 999 }], Timelines = [new() { SensorId = 100, HostId = 999, SourceGeneration = "foreign" }], SemanticEvidence = [new(100, 10, 999, "disk", "used", "Used", "%", 1, "up", PrtgDiskSemanticEvidenceSource.Manual, 1, "foreign", DateTime.UtcNow, "v2")] };
        var rawJson = System.Text.Json.JsonSerializer.Serialize(pkg);
        var first = PrtgDataTransfer.Import(_backend, pkg);
        var second = PrtgDataTransfer.Import(_backend, pkg);
        Assert.True(first.DiagnosticOnly);
        Assert.Equal((1, 1, 1, 1, 1, 1), (first.Devices, first.Sensors, first.StateChanges, first.Values, first.HostMaps, first.ManualMaps));
        Assert.Equal(first.DiagnosticId, second.DiagnosticId);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawJson))), first.DiagnosticId);
        Assert.Equal(rawJson, _backend.Blob("prtg_import_diagnostic_" + first.DiagnosticId).Read());
        Assert.Equal(valueBefore, System.Text.Json.JsonSerializer.Serialize(store.GetValues(DateTime.Today, DateTime.Today.AddDays(1))));
        Assert.Equal(devicesBefore, System.Text.Json.JsonSerializer.Serialize(store.GetAllDevices()));
        Assert.Equal(sensorsBefore, System.Text.Json.JsonSerializer.Serialize(store.GetAllSensors()));
        Assert.Equal(changesBefore, System.Text.Json.JsonSerializer.Serialize(store.GetStateChanges(DateTime.Today, DateTime.Today.AddDays(1))));
        Assert.Equal(mapsBefore, System.Text.Json.JsonSerializer.Serialize(store.GetHostMapForDate(DateTime.Today)));
        Assert.Equal(manualBefore, System.Text.Json.JsonSerializer.Serialize(store.GetManualMaps()));
        Assert.Equal(policyBefore, System.Text.Json.JsonSerializer.Serialize(policyStore.Get()));
        Assert.True(policyStore.Get().Ready(sourceUrl));
        Assert.Equal(blobsBefore, ExistingBlobSnapshot());
        Assert.Equal(evidenceBefore, EvidenceSnapshot());
        using var db = _backend.CreateContext();
        Assert.Single(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));
    }

    [Fact]
    public void InvalidPackage_不得留下隔離Blob()
    {
        SeedFormalSnapshot();
        var before = FormalSnapshot();
        foreach (var pkg in new[] { new PrtgDataPackage { FormatVersion = 8 }, new PrtgDataPackage { Purpose = "formal" } })
        {
            Assert.Throws<InvalidOperationException>(() => PrtgDataTransfer.Import(_backend, pkg));
            Assert.Equal(before, FormalSnapshot());
        }
        using var db = _backend.CreateContext();
        Assert.Empty(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));
    }

    [Fact]
    public void LegacyEfImport_拒絕且Ready正式資料不變()
    {
        SeedFormalSnapshot();
        var store = _backend.PrtgStore();
        store.UpsertSensors([new() { Objid = 6, DeviceObjid = 5, Name = "existing sensor" }], DateTime.Today);
        store.AppendStateChanges([new() { SensorObjid = 6, ChangedAt = DateTime.Today, Status = "Up", Quality = PrtgDataQuality.Ok }]);
        store.ReplaceHostMapForDate(DateTime.Today, [new() { MapDate = DateTime.Today, DeviceObjid = 5, HostId = 1, HostName = "existing", MapStatus = PrtgMapStatus.Ok }]);
        store.UpsertManualMap(new() { DeviceObjid = 5, HostId = 1 });
        const string sourceUrl = "https://fixture.example/prtg";
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => { p.EndpointHint = EfPrtgObservationStore.SourceHintFor(sourceUrl); p.HostIds = [1]; p.SensorIds = [6]; p.SourceTimeZoneId = "UTC"; p.SourceCultureName = "en-US"; });
        Assert.True(policy.Get().Ready(sourceUrl));
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 6)).Update(t =>
        { t.SensorId = 6; t.HostId = 1; t.SourceGeneration = "ready"; t.ValidFrom = DateTime.Today; });
        var before = FormalSnapshot();

        var ex = Assert.Throws<InvalidOperationException>(() => PrtgDataTransfer.Import(store,
            new PrtgDataPackage { Devices = [new() { Objid = 5, Name = "overwrite" }], Values = [new() { SensorObjid = 6, PeriodStart = DateTime.Today, AvgValue = 999, Quality = PrtgDataQuality.Sampled }] }));

        Assert.Contains("正式 PRTG 鏡像匯入已停用", ex.Message);
        Assert.Equal(before, FormalSnapshot());
    }

    [Fact]
    public void Format1診斷包仍可讀取且不修改正式資料()
    {
        SeedFormalSnapshot();
        var before = FormalSnapshot();
        var package = new PrtgDataPackage { FormatVersion = 1, Devices = [new() { Objid = 55, Name = "legacy diagnostic" }] };
        var result = PrtgDataTransfer.Import(_backend, package);

        Assert.True(result.DiagnosticOnly);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(package), _backend.Blob("prtg_import_diagnostic_" + result.DiagnosticId).Read());
        Assert.Equal(before, FormalSnapshot());
    }

    private void SeedFormalSnapshot()
    {
        _backend.PrtgStore().UpsertDevices([new() { Objid = 5, Name = "existing" }], DateTime.Today);
        _backend.PrtgStore().UpsertValues([new() { SensorObjid = 6, PeriodStart = DateTime.Today, AvgValue = 8, Quality = PrtgDataQuality.Ok }]);
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => { p.CoreSystemId = "valid"; p.SourceGeneration = "valid-generation"; p.Revision = "valid-revision"; p.ValidFrom = DateTime.Today; p.EndpointHint = "hint"; p.HostIds = [1]; p.SensorIds = [6]; p.SourceTimeZoneId = "UTC"; p.SourceCultureName = "en-US"; });
    }

    private string FormalSnapshot()
    {
        using var db = _backend.CreateContext();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Devices = _backend.PrtgStore().GetAllDevices(), Sensors = _backend.PrtgStore().GetAllSensors(),
            StateChanges = _backend.PrtgStore().GetStateChanges(DateTime.Today, DateTime.Today.AddDays(1)),
            Values = _backend.PrtgStore().GetValues(DateTime.Today, DateTime.Today.AddDays(1)),
            HostMaps = _backend.PrtgStore().GetHostMapForDate(DateTime.Today), ManualMaps = _backend.PrtgStore().GetManualMaps(),
            Policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get(),
            Timeline = new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 6)).Get(),
            Blobs = db.Blobs.Where(b => !b.BlobKey.StartsWith("prtg_import_diagnostic_")).OrderBy(b => b.BlobKey).ToList()
        });
    }

    [Fact]
    public void SettingsControllerImport_完整可見者隔離保存_受限案件及空可見仍拒絕()
    {
        var audit = new RecordingAuditService();
        var full = Controller(new TestVisibility([1, 2]), audit);
        var ok = full.ImportPrtgData(FileFor(new PrtgDataPackage()));
        Assert.True(ok.Data!.DiagnosticOnly);
        Assert.Contains(audit.Entries, e => e.Summary.Contains("診斷包已隔離保存，正式監控資料與判定未變更"));
        Assert.Throws<DomainException>(() => full.ImportPrtgData(FileFor(new PrtgDataPackage { Purpose = "formal" })));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1])).ImportPrtgData(FileFor(new())));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2], caseOnly: true)).ImportPrtgData(FileFor(new())));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([])).ImportPrtgData(FileFor(new())));
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).ImportPrtgData(FileFor(new())));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).ImportPrtgData(FileFor(new())));
        var noHosts = new HostStore(_backend.Blob("empty-hosts"));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([]), hosts: noHosts).ImportPrtgData(FileFor(new())));
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([]), hosts: noHosts).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).GetPrtgManualMaps());
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).GetPrtgManualMaps());
    }

    [Fact]
    public void SettingsControllerPermissionFilter_只允許Maintain能力()
    {
        var required = typeof(SettingsController).GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().Single().Arguments!.Cast<Capability[]>().Single();
        var audit = new RecordingAuditService();
        AuthorizationFilterContext Context() => new(new ActionContext(new DefaultHttpContext
        { Request = { Path = "/api/admin/settings/prtg-import" } }, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()), []);

        var maintainerContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Maintain), audit).OnAuthorization(maintainerContext);
        Assert.Null(maintainerContext.Result);
        var maintainedAction = Controller(new TestVisibility([1, 2]), audit)
            .ImportPrtgData(FileFor(new PrtgDataPackage { Devices = [new() { Objid = 77, Name = "maintainer diagnostic" }] }));
        Assert.True(maintainedAction.Data!.DiagnosticOnly);
        Assert.Equal("maintainer diagnostic", System.Text.Json.JsonSerializer.Deserialize<PrtgDataPackage>(
            _backend.Blob("prtg_import_diagnostic_" + maintainedAction.Data.DiagnosticId).Read()!)!.Devices.Single().Name);

        var restrictedContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Handle), audit).OnAuthorization(restrictedContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(restrictedContext.Result).StatusCode);
        var restrictedActionCalls = 0;
        if (restrictedContext.Result is null)
        {
            restrictedActionCalls++;
            Controller(new TestVisibility([1, 2]), audit).ImportPrtgData(FileFor(new PrtgDataPackage()));
        }
        Assert.Equal(0, restrictedActionCalls);
        using (var db = _backend.CreateContext())
            Assert.Single(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));
        Assert.Contains(audit.Entries, entry => entry.Result == LogForesight.Core.Models.AuditResult.Denied);
    }

    private SettingsController Controller(TestVisibility? visibility, RecordingAuditService? audit = null, HostStore? hosts = null, bool omitHosts = false) => new(new FakeSystemSettingsService(),
        new AiUsageStore(_backend.Blob("ai_usage")), audit ?? new RecordingAuditService(), backend: _backend, hosts: omitHosts ? null : hosts ?? _hosts, visibility: visibility);
    private static IFormFile FileFor(PrtgDataPackage package)
    {
        var stream = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(package));
        return new FormFile(stream, 0, stream.Length, "file", "package.json");
    }
    private sealed class TestVisibility(long[] ids, bool caseOnly = false) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => caseOnly;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
