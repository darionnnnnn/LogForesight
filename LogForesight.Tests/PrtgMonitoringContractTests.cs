using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;
public sealed class PrtgMonitoringContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-monitoring-api-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly RecordingAuditService _audit = new();
    private sealed class Visible(params long[] ids) : IVisibilityService
    {
        public bool CaseOnly { get; set; }
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => CaseOnly;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }
    public PrtgMonitoringContractTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir); _hosts = new(_backend.Blob("hosts"));
        _hosts.Upsert(new() { HostName = "VISIBLE", Source = "netiq", Active = true });
        _hosts.Upsert(new() { HostName = "PRIVATE", Source = "netiq", Active = true });
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgUrl = "https://fixture.example");
        _backend.PrtgStore().UpsertDevices([new() { Objid = 10, Name = "fixture" }], DateTime.Now);
        _backend.PrtgStore().UpsertSensors([new() { Objid = 100, DeviceObjid = 10, Name = "Ping", SensorType = "ping" }], DateTime.Now);
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 1, HostName = "VISIBLE", MapStatus = PrtgMapStatus.Ok }]);
    }
    private PrtgMonitoringController Controller(Visible? visible = null) => new(_backend, _hosts, visible ?? new(1, 2),
        FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, new DataVersionStamp());
    private static PrtgMonitoringRequest Request(string revision = "") => new()
    { Revision = revision, IdentityConfirmed = true, CoreSystemId = "fixture-core", SourceTimeZoneId = TimeZoneInfo.Local.Id,
        SourceCultureName = "en-US", HostIds = [1], SensorIds = [100] };
    [Fact]
    public void 預設驗收分組隨資源語意修訂改變_日常探測時間不切段()
    {
        Controller().Put(Request());
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        var proof = new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 100));
        proof.Update(p => { p.HostId = 1; p.SensorId = 100; p.ResourceGeneration = "r1"; p.DiskSemanticFingerprint = "semantic1"; });
        string Segment()
        {
            var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "label", Outcome = "unknown" };
            Assert.IsType<OkObjectResult>(api.Save(label)); return label.Segment;
        }
        var first = Segment();
        proof.Update(p => p.DiskSemanticCheckedAt = DateTimeOffset.Now);
        Assert.Equal(first, Segment());
        proof.Update(p => p.DiskSemanticFingerprint = "semantic2");
        var semantic = Segment(); Assert.NotEqual(first, semantic);
        proof.Update(p => p.ResourceGeneration = "r2");
        Assert.NotEqual(semantic, Segment());
    }

    [Fact]
    public void 預防事故可以不填未發生時間_必填介入與前後量測_不能冒稱實際發生()
    {
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        var incident = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "prevented", Outcome = "prevented" };
        Assert.IsType<BadRequestObjectResult>(api.Save(incident));
        incident.DispositionAt = DateTimeOffset.Now.AddHours(-1); incident.ActionDetails = "擴容";
        incident.BeforeMeasurement = "磁碟 5%"; incident.AfterMeasurement = "同磁碟 50%"; incident.EvidenceReference = "維護工單";
        Assert.IsType<OkObjectResult>(api.Save(incident));
        incident.OccurredAt = DateTimeOffset.Now.AddMinutes(-1);
        Assert.IsType<BadRequestObjectResult>(api.Save(incident));
    }
    [Fact]
    public void 樂觀版本防覆蓋_只改範圍保留來源_換時區重設暖機()
    {
        Assert.IsType<OkObjectResult>(Controller().Put(Request()));
        var store = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)); var initial = store.Get();
        Assert.True(initial.Ready("https://fixture.example"));
        Assert.IsType<ConflictObjectResult>(Controller().Put(Request()));
        Assert.Equal(initial.Revision, store.Get().Revision);
        Assert.IsType<OkObjectResult>(Controller().Put(Request(initial.Revision)));
        Assert.Equal(initial.SourceGeneration, store.Get().SourceGeneration);
        var next = Request(store.Get().Revision); next.SourceCultureName = "zh-TW";
        Assert.IsType<OkObjectResult>(Controller().Put(next));
        Assert.NotEqual(initial.SourceGeneration, store.Get().SourceGeneration);
    }
    [Fact]
    public void 局部管理者不能覆寫含不可見主機的試點_案件例外不能設定整台主機()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => p.HostIds = [2]);
        Assert.IsType<ConflictObjectResult>(Controller(new(1)).Put(Request()));
        Assert.IsType<BadRequestObjectResult>(Controller(new(1) { CaseOnly = true }).Put(Request()));
        var payload = JsonSerializer.Serialize(((OkObjectResult)Controller(new(1)).Get()).Value);
        Assert.DoesNotContain("PRIVATE", payload);
    }
    [Fact]
    public void 空清單或錯Sensor拒絕_不覆寫原設定()
    {
        var request = Request(); request.SensorIds = [999];
        Assert.IsType<BadRequestObjectResult>(Controller().Put(request));
        request.SensorIds = null!;
        Assert.IsType<BadRequestObjectResult>(Controller().Put(request));
        Assert.Empty(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision);
    }
    [Fact]
    public void V1匯入隔離跨站主機與人工對應_重複匯入不啟用正式判定()
    {
        Controller().Put(Request());
        var package = PrtgDataTransfer.Export(_backend, DateTime.Today, DateTime.Today); package.FormatVersion = 1;
        package.ManualMaps = [new() { DeviceObjid = 10, HostId = 2 }];
        PrtgDataTransfer.Import(_backend, package); PrtgDataTransfer.Import(_backend, package);
        Assert.Empty(_backend.PrtgStore().GetManualMaps());
        Assert.Null(Assert.Single(_backend.PrtgStore().GetHostMapForDate(DateTime.Today)).HostId);
        Assert.False(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Ready("https://fixture.example"));
        using var db = _backend.CreateContext(); Assert.Empty(db.PrtgObservations); Assert.Empty(db.IssueCases); Assert.Empty(db.WorkOrders);
        Assert.Single(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));
    }
    [Fact]
    public void 驗收證據匯出與人工標籤依主機授權_不能偷標私有主機()
    {
        Controller().Put(Request());
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        Assert.IsType<ForbidResult>(api.Save(new() { HostId = 2 }));
        var file = Assert.IsType<FileContentResult>(api.Export(DateTime.Today.AddDays(-1), DateTime.Today));
        var json = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.DoesNotContain("PRIVATE", json); Assert.Contains("ScopeComplete", json);
        Assert.Contains("manual-site-acceptance", json);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
