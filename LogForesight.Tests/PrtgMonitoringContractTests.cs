using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;
public sealed class PrtgMonitoringContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-monitoring-api-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly RecordingAuditService _audit = new();
    private readonly IDataProtectionProvider _protection = new ServiceCollection().AddDataProtection()
        .Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    private sealed class Visible(params long[] ids) : IVisibilityService
    {
        private long[] _ids = ids;
        public int SnapshotReadCount { get; private set; }
        public int? RevokeAtSnapshotRead { get; set; }
        public List<PrtgHostSnapshot> Snapshots { get; } = [];
        public bool CaseOnly { get; set; }
        public void SetVisibleHostIds(params long[] values) => _ids = values;
        public IReadOnlySet<long> GetVisibleHostIds() => _ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot)
        {
            SnapshotReadCount++;
            Snapshots.Add(snapshot);
            if (RevokeAtSnapshotRead == SnapshotReadCount) _ids = [2];
            return GetVisibleHostIds();
        }
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
    private PrtgMonitoringController Controller(Visible? visible = null, ICurrentUser? currentUser = null) => new(_backend, _hosts, visible ?? new(1, 2),
        currentUser ?? FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, new DataVersionStamp(), _protection);
    private PrtgMonitoringController ControllerWithRealVisibility(ICurrentUser currentUser, IUserStore? userStore = null)
    {
        var visibility = new VisibilityService(currentUser, userStore ?? new FakeUserStore(), new FakeUserGroupStore(),
            new FakeGroupAccessStore(), _hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        return new(_backend, _hosts, visibility, currentUser, _audit, new DataVersionStamp(), _protection);
    }
    private static PrtgMonitoringRequest Request(string revision = "") => new()
    { Revision = revision, IdentityConfirmed = true, CoreSystemId = "fixture-core", SourceTimeZoneId = TimeZoneInfo.Local.Id,
        SourceCultureName = "en-US", HostIds = [1], SensorIds = [100] };

    private static string DataString(IActionResult result, string name)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return document.RootElement.GetProperty("Data").GetProperty(name).GetString()!;
    }

    private static IActionResult Estimate(PrtgMonitoringController controller, PrtgMonitoringRequest request)
    {
        var get = controller.Get();
        if (get is not OkObjectResult) return get;
        request.CatalogueToken = DataString(get, "CatalogueToken");
        var estimate = controller.Estimate(request);
        if (estimate is OkObjectResult) request.EstimateToken = DataString(estimate, "EstimateToken");
        return estimate;
    }

    private static IActionResult Save(PrtgMonitoringController controller, PrtgMonitoringRequest request)
    {
        var estimate = Estimate(controller, request);
        return estimate is OkObjectResult ? controller.Put(request) : estimate;
    }
    [Fact]
    public void 合併可行動時間不能只填空白證據_拒絕不保存()
    {
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var incident = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "blank-evidence", OccurredAt = at,
            ConfirmedPositive = true, CombinedActionableAt = at.AddHours(-1), CombinedEvidenceAvailableAt = at.AddHours(-2), EvidenceReference = " \t" };
        Assert.IsType<BadRequestObjectResult>(api.Save(incident));
        Assert.Null(_backend.Blob("prtg_acceptance_labels_1").Read());
        incident.EvidenceReference = "independent incident ticket";
        Assert.IsType<OkObjectResult>(api.Save(incident));
        Assert.Contains("blank-evidence", _backend.Blob("prtg_acceptance_labels_1").Read());
    }

    [Fact]
    public void 預設事故分組包含新採樣語意_日常核對時間不切段()
    {
        Assert.IsType<OkObjectResult>(Save(Controller(), Request()));
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 100)).Update(p =>
        { p.HostId = 1; p.SensorId = 100; p.SourceGeneration = "source"; p.ResourceGeneration = "resource"; });
        string Segment(string id)
        {
            var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = id, Outcome = "unknown" };
            Assert.IsType<OkObjectResult>(api.Save(label)); return label.Segment;
        }
        PrtgTrustedSamplingBinding Binding(double scale, long revision) => PrtgTrustedSamplingBinding.Create(
            100, "3", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", scale, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis", "settings", "policy", new string('A', 64), "source", "resource", 1, "channel", revision);
        void Persist(PrtgTrustedSamplingBinding binding) => _backend.Blob(binding.StoreKey).Mutate(_ => (JsonSerializer.Serialize(binding), true));
        var original = Segment("original");
        var binding = Binding(1, 1); Persist(binding);
        var configured = Segment("configured"); Assert.NotEqual(original, configured);
        Persist(binding with { UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(configured, Segment("routine-observation"));
        Persist(Binding(0.5, 2));
        Assert.NotEqual(configured, Segment("new-scale"));
        // Existing labels retain the comparison segment from their original configuration.
        var labels = JsonSerializer.Deserialize<List<PrtgAcceptanceIncident>>(_backend.Blob("prtg_acceptance_labels_1").Read()!)!;
        Assert.Equal(configured, labels.Single(row => row.IncidentId == "configured").Segment);
    }

    [Fact]
    public void 預設事故分組隨正式模式切段_試算更新及不可見主機不影響()
    {
        Assert.IsType<OkObjectResult>(Save(Controller(), Request()));
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 100)).Update(p =>
        { p.HostId = 1; p.SensorId = 100; });
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        string Segment()
        {
            var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "mode-label", Outcome = "unknown" };
            Assert.IsType<OkObjectResult>(api.Save(label)); return label.Segment;
        }
        var at = DateTime.UtcNow;
        var grant = new PrtgResourcePressureModeGrant(1, 100, PrtgResourceFamily.Cpu, new string('A', 64),
            PrtgResourcePressureEvaluator.RulesVersion, "source", "resource", "channel", "epoch", "semantic",
            "strategy", "rule", new string('B', 64), new string('C', 64), true, false, at, at.AddHours(1));
        void Persist(PrtgResourcePressureModeGrant g) => new PrtgResourcePressureModeStore(
            _backend.Blob(PrtgResourcePressureModeStore.BlobKey(g.HostId))).Update(d => d.Grants = [g]);
        var hint = Segment(); Persist(grant);
        Assert.Equal(hint, Segment());
        Persist(grant with { FormalEnabled = true, ModeTransitionId = "activation-1" });
        var formal = Segment(); Assert.NotEqual(hint, formal);
        Persist(grant with { FormalEnabled = true, ModeTransitionId = "activation-1", UpdatedAtUtc = at.AddMinutes(1),
            TrialExpiresAtUtc = at.AddHours(2), TrialResultHash = new string('D', 64) });
        Assert.Equal(formal, Segment());
        // A malformed inaccessible mode document must not even be read.
        _backend.Blob(PrtgResourcePressureModeStore.BlobKey(2)).Mutate(_ => ("not-json", true));
        Assert.Equal(formal, Segment());
        Persist(grant with { FormalEnabled = false, ModeTransitionId = "disabled-1" });
        Assert.Equal(hint, Segment());
        Persist(grant with { FormalEnabled = true, ModeTransitionId = "activation-2" });
        Assert.NotEqual(formal, Segment());
    }

    [Fact]
    public void 預設事故分組以現有資源權威限制可見範圍_舊時間線不洩漏()
    {
        Assert.IsType<OkObjectResult>(Save(Controller(), Request()));
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 100)).Update(p =>
        { p.HostId = 1; p.SensorId = 100; });
        var identity = new PrtgResourceIdentity { SensorId = 100, HostId = 2, Epoch = 1, Generation = "private" };
        _backend.Blob(PrtgResourceIdentityStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(identity), true));
        _backend.Blob(PrtgTrustedSamplingBinding.StorePrefix + 100).Mutate(_ => ("not-json", true));
        _backend.Blob(PrtgResourcePressureModeStore.BlobKey(2)).Mutate(_ => ("not-json", true));
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        string Segment()
        {
            var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "scope-label", Outcome = "unknown" };
            Assert.IsType<OkObjectResult>(api.Save(label)); return label.Segment;
        }
        var before = Segment();
        identity.Epoch++;
        _backend.Blob(PrtgResourceIdentityStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(identity), true));
        Assert.Equal(before, Segment());
    }

    [Fact]
    public void 預設事故分組分頁讀取超過一百個Binding_未知時間線不冒用()
    {
        Assert.IsType<OkObjectResult>(Save(Controller(), Request()));
        var ids = Enumerable.Range(100, 101).Select(id => (long)id).ToArray();
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => p.SensorIds = ids.ToList());
        foreach (var id in ids)
        {
            var identity = new PrtgResourceIdentity { SensorId = id, HostId = 1, Epoch = 1, Generation = "resource", SourceGeneration = "source" };
            _backend.Blob(PrtgResourceIdentityStore.Prefix + id).Mutate(_ => (JsonSerializer.Serialize(identity), true));
            var binding = PrtgTrustedSamplingBinding.Create(id, "3", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent,
                "%", 1, "direct", "seconds", "UTC", "UTC", "synthetic-time-basis", "settings", "policy",
                new string('A', 64), "source", "resource", 1, "channel", 1);
            _backend.Blob(binding.StoreKey).Mutate(_ => (JsonSerializer.Serialize(binding), true));
        }
        // Existing oversized evidence remains unknown; the summary never reads an unbounded blob.
        _backend.Blob(PrtgSensorTimelineStore.Prefix + 100).Mutate(_ =>
            (new string('x', PrtgSensorTimelineStore.MaxSilentReadBytes + 1), true));
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "paged-label", Outcome = "unknown" };
        Assert.IsType<OkObjectResult>(api.Save(label));
        var before = label.Segment;
        _backend.Blob(PrtgResourceIdentityStore.Prefix + 200).Mutate(raw =>
        {
            var identity = JsonSerializer.Deserialize<PrtgResourceIdentity>(raw!)!;
            identity.ChannelGeneration = "changed-last-page";
            return (JsonSerializer.Serialize(identity), true);
        });
        label.Segment = "";
        Assert.IsType<OkObjectResult>(api.Save(label));
        Assert.NotEqual(before, label.Segment);
    }

    [Fact]
    public void 預設驗收分組隨資源語意修訂改變_日常探測時間不切段()
    {
        Save(Controller(), Request());
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
        Assert.IsType<OkObjectResult>(Save(Controller(), Request()));
        var store = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)); var initial = store.Get();
        Assert.True(initial.Ready("https://fixture.example"));
        Assert.IsType<ConflictObjectResult>(Save(Controller(), Request()));
        Assert.Equal(initial.Revision, store.Get().Revision);
        Assert.IsType<OkObjectResult>(Save(Controller(), Request(initial.Revision)));
        Assert.Equal(initial.SourceGeneration, store.Get().SourceGeneration);
        var next = Request(store.Get().Revision); next.SourceCultureName = "zh-TW"; next.SourceChangeMode = "new";
        Assert.IsType<OkObjectResult>(Save(Controller(), next));
        Assert.NotEqual(initial.SourceGeneration, store.Get().SourceGeneration);
    }

    [Fact]
    public void 同Core搬址必須明確選擇_延續要證據_新Core與未知不能冒稱延續()
    {
        var controller = Controller(); Assert.IsType<OkObjectResult>(Save(controller, Request()));
        var store = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)); var old = store.Get();
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgUrl = "https://moved.example");
        var request = Request(old.Revision);
        Assert.IsType<OkObjectResult>(Estimate(controller, request));
        Assert.IsType<OkObjectResult>(controller.SourcePreview(request));
        Assert.IsType<BadRequestObjectResult>(Save(controller, request));
        request.SourceChangeMode = "continue";
        Assert.IsType<BadRequestObjectResult>(Save(controller, request));
        request.ContinuityConfirmed = true; request.ContinuityEvidenceReference = "管理者搬遷核對工單 123";
        Assert.IsType<OkObjectResult>(Save(controller, request));
        Assert.Equal(old.SourceGeneration, store.Get().SourceGeneration); Assert.Equal(old.ValidFrom, store.Get().ValidFrom);
        Assert.Equal(request.ContinuityEvidenceReference, store.Get().ContinuityEvidenceReference);
        request.Revision = store.Get().Revision; request.CoreSystemId = "replacement-core";
        Assert.IsType<BadRequestObjectResult>(Save(controller, request));
        request.SourceChangeMode = "new";
        Assert.IsType<OkObjectResult>(Save(controller, request));
        Assert.NotEqual(old.SourceGeneration, store.Get().SourceGeneration);
        request.Revision = store.Get().Revision; request.SourceChangeMode = "unknown"; request.CoreSystemId = "";
        Assert.IsType<OkObjectResult>(Save(controller, request));
        Assert.False(store.Get().Ready("https://moved.example"));
        Assert.IsType<ConflictObjectResult>(controller.SourcePreview(Request()));
    }

    [Fact]
    public void 預覽未對應與試點排除_等待Netiq_沒有副作用_空權限零筆()
    {
        var controller = Controller(); Assert.IsType<OkObjectResult>(Save(controller, Request()));
        _backend.PrtgStore().UpsertSensors([new() { Objid = 200, DeviceObjid = 99, Name = "Unmapped", SensorType = "ping" }], DateTime.Now);
        var preview = Assert.IsType<OkObjectResult>(controller.Preview());
        var json = JsonSerializer.Serialize(preview.Value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("尚無有效主機對應", json);
        Assert.Contains("感測器未納入試點", json);
        Assert.Contains("等待目標日 NetIQ 成功分析", json);
        var restricted = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(Controller(new(1)).Preview()).Value);
        Assert.DoesNotContain("Unmapped", restricted); Assert.DoesNotContain("PRIVATE", restricted);
        var empty = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(Controller(new()).Preview()).Value);
        Assert.Contains("\"Total\":0", empty);
        Assert.IsType<BadRequestObjectResult>(controller.Preview(limit: 501));
        using var db = _backend.CreateContext(); Assert.Empty(db.PrtgObservations); Assert.Empty(db.IssueCases); Assert.Empty(db.WorkOrders);
    }

    [Fact]
    public void 首次唯讀評估預覽不會把記憶體種子寫進正式規則庫()
    {
        var rulesBlob = _backend.Blob("rules");
        Assert.Equal(0, rulesBlob.ReadVersion());
        Assert.IsType<OkObjectResult>(Controller().Preview());
        Assert.Equal(0, rulesBlob.ReadVersion());
        Assert.False(new KnownIssueRuleStore(rulesBlob).Exists);
    }

    [Fact]
    public void 局部管理者不能覆寫含不可見主機的試點_案件例外不能設定整台主機()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => p.HostIds = [2]);
        Assert.IsType<ForbidResult>(Controller(new(1)).Get());
        Assert.IsType<ForbidResult>(Controller(new(1) { CaseOnly = true }).Get());
        Assert.IsType<ForbidResult>(Controller(new()).Get());
        Assert.IsType<ForbidResult>(Save(Controller(new(1)), Request()));
        Assert.IsType<ForbidResult>(Save(Controller(new(1) { CaseOnly = true }), Request()));
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        { p.HostIds = [1]; p.SensorIds = [100]; });
        var payload = JsonSerializer.Serialize(((OkObjectResult)Controller(new(1)).Get()).Value);
        Assert.DoesNotContain("PRIVATE", payload);
        Assert.Contains("\"HostIds\":[1]", payload);
        Assert.Contains("\"SensorIds\":[100]", payload);
    }

    [Fact]
    public void ViewAll可檢視並修復停用合併非NetIQ及已移除的舊主機_保存只留下重新核對的選取()
    {
        _hosts.Upsert(new() { HostName = "PRIVATE", Source = "netiq", Active = false });
        _hosts.Upsert(new() { HostName = "MERGED", Source = "netiq", Active = true });
        var merged = _hosts.FindByName("MERGED")!;
        _hosts.Merge(merged.HostId, 1);
        _hosts.Upsert(new() { HostName = "LOCAL-ONLY", Source = "local", Active = true });
        var local = _hosts.FindByName("LOCAL-ONLY")!;
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var endpoint = EfPrtgObservationStore.SourceHintFor("https://fixture.example");
        policy.Update(p =>
        {
            p.Revision = "recoverable-revision"; p.CoreSystemId = "fixture-core"; p.SourceGeneration = "existing-generation";
            p.EndpointHint = endpoint; p.ValidFrom = DateTimeOffset.Now; p.SourceTimeZoneId = TimeZoneInfo.Local.Id;
            p.SourceCultureName = "en-US"; p.HostIds = [1, 2, merged.HostId, local.HostId, 9999]; p.SensorIds = [100];
        });

        var adminUser = FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll);
        var admin = ControllerWithRealVisibility(adminUser);
        var get = admin.Get();
        var ok = Assert.IsType<OkObjectResult>(get);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)))
        {
            var saved = document.RootElement.GetProperty("Data").GetProperty("SavedHosts").EnumerateArray()
                .ToDictionary(row => row.GetProperty("HostId").GetInt64(), row => row.GetProperty("Status").GetString());
            Assert.Equal("inactive", saved[2]);
            Assert.Equal("merged", saved[merged.HostId]);
            Assert.Equal("not-netiq", saved[local.HostId]);
            Assert.Equal("missing", saved[9999]);
        }

        var repair = Request(policy.Get().Revision);
        repair.HostIds = [1]; repair.SensorIds = [100];
        Assert.IsType<OkObjectResult>(Save(admin, repair));
        Assert.Equal(new long[] { 1 }, policy.Get().HostIds);
        Assert.Equal(new long[] { 100 }, policy.Get().SensorIds);
        Assert.IsType<OkObjectResult>(admin.Get());

        // A regular maintainer can see host 1 through the real owner path, but cannot read or
        // overwrite the now-hidden host 2 policy. The denied estimate/save must leave its blob untouched.
        policy.Update(p => { p.HostIds = [2]; p.SensorIds = [100]; });
        var beforeDeniedSave = policy.Get();
        var users = new FakeUserStore();
        var maintainer = users.Upsert(new WebUser { Account = "scope-maintainer", Active = true });
        _hosts.SetOwners(1, [maintainer.UserId]);
        var maintainOnly = ControllerWithRealVisibility(FakeCurrentUser.ForUser(maintainer.UserId, Capability.Maintain), users);
        Assert.IsType<ForbidResult>(maintainOnly.Get());
        Assert.IsType<ForbidResult>(Save(maintainOnly, Request(beforeDeniedSave.Revision)));
        Assert.Equal(beforeDeniedSave.Revision, policy.Get().Revision);
        Assert.Equal(beforeDeniedSave.HostIds, policy.Get().HostIds);
        Assert.Equal(beforeDeniedSave.SensorIds, policy.Get().SensorIds);
    }
    [Fact]
    public void 空清單或錯Sensor拒絕_不覆寫原設定()
    {
        var request = Request(); request.SensorIds = [999];
        Assert.IsType<BadRequestObjectResult>(Save(Controller(), request));
        request.SensorIds = null!;
        Assert.IsType<BadRequestObjectResult>(Save(Controller(), request));
        Assert.Empty(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision);
    }

    [Fact]
    public void 頁面權杖受保護且估算後目錄改變必須拒絕保存()
    {
        var controller = Controller();
        var get = controller.Get();
        var catalogueToken = DataString(get, "CatalogueToken");
        Assert.IsType<BadRequestObjectResult>(controller.HostPage("forged-cursor", "", catalogueToken));
        var hostPage = Assert.IsType<OkObjectResult>(controller.HostPage(null, "VISIBLE", catalogueToken));
        Assert.Contains("VISIBLE", JsonSerializer.Serialize(hostPage.Value));

        var request = Request();
        Assert.IsType<OkObjectResult>(Estimate(controller, request));
        _backend.PrtgStore().UpsertSensors([new() { Objid = 101, DeviceObjid = 10, Name = "new sensor", SensorType = "ping" }], DateTime.Now);
        Assert.IsType<ConflictObjectResult>(controller.Put(request));
        Assert.Empty(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision);
    }

    [Theory]
    [InlineData("catalogue")]
    [InlineData("map")]
    [InlineData("hosts")]
    [InlineData("settings")]
    [InlineData("policy")]
    [InlineData("visibility")]
    public void BrowseEstimateSave期間版本維度改變時拒絕且不再寫正式設定(string dimension)
    {
        var visible = new Visible(1, 2);
        var controller = Controller(visible);
        var request = Request();
        Assert.IsType<OkObjectResult>(Estimate(controller, request));

        switch (dimension)
        {
            case "catalogue":
                _backend.PrtgStore().UpsertSensors([new() { Objid = 101, DeviceObjid = 10, Name = "new", SensorType = "ping" }], DateTime.Now);
                break;
            case "map":
                _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
                    [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 1, HostName = "VISIBLE", MapStatus = PrtgMapStatus.Conflict }]);
                break;
            case "hosts":
                _hosts.Upsert(new() { HostId = 1, HostName = "VISIBLE-RENAMED", Source = "netiq", Active = true });
                break;
            case "settings":
                new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgUrl = "https://changed.example");
                break;
            case "policy":
                new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
                    .Update(p => p.Revision = Guid.NewGuid().ToString("N"));
                break;
            case "visibility":
                visible.SetVisibleHostIds(2);
                break;
        }

        var policyBlob = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        var result = controller.Put(request);
        Assert.True(result is ConflictObjectResult or ForbidResult, $"Unexpected response for {dimension}: {result.GetType().Name}");
        var after = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        Assert.Equal(policyBlob.Content, after.Content);
        Assert.Equal(policyBlob.Version, after.Version);
    }

    [Fact]
    public void SQL估算期間撤回群組可見權限_不回傳可保存估算權杖()
    {
        var visible = new Visible(1, 2);
        var controller = Controller(visible);
        var request = Request();
        request.CatalogueToken = DataString(controller.Get(), "CatalogueToken");
        var before = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        visible.Snapshots.Clear();
        visible.RevokeAtSnapshotRead = visible.SnapshotReadCount + 2; // Estimate initial capture then post-query authorization fence

        Assert.IsType<ConflictObjectResult>(controller.Estimate(request));

        var after = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.Version, after.Version);
        Assert.All(visible.Snapshots, snapshot => Assert.Same(visible.Snapshots[0], snapshot));
    }

    [Fact]
    public void SQL估算後PUT交易前撤回群組可見權限_同一HostSnapshot重算並拒絕保存()
    {
        var visible = new Visible(1, 2);
        var controller = Controller(visible);
        var request = Request();
        Assert.IsType<OkObjectResult>(Estimate(controller, request));
        var policyBlob = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        visible.Snapshots.Clear();
        visible.RevokeAtSnapshotRead = visible.SnapshotReadCount + 3; // PUT initial capture、post-query fence、政策交易 callback

        Assert.IsType<ConflictObjectResult>(controller.Put(request));

        var after = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        Assert.Equal(policyBlob.Content, after.Content);
        Assert.Equal(policyBlob.Version, after.Version);
        Assert.NotEmpty(visible.Snapshots);
        Assert.All(visible.Snapshots, snapshot => Assert.Same(visible.Snapshots[0], snapshot));
    }

    [Theory]
    [InlineData("missing-map")]
    [InlineData("conflict-map")]
    public void 選取沒有有效全域最新對應的Sensor必須拒絕(string mapping)
    {
        if (mapping == "missing-map")
            _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, []);
        else
            _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
                [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 1, HostName = "VISIBLE", MapStatus = PrtgMapStatus.Conflict }]);

        Assert.IsType<BadRequestObjectResult>(Estimate(Controller(), Request()));
        Assert.Empty(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision);
    }

    [Fact]
    public void 超過完整範圍上限明確拒絕而不截斷()
    {
        var controller = Controller();
        var tooManyHosts = Request(); tooManyHosts.HostIds = Enumerable.Range(1, 3001).Select(x => (long)x).ToList();
        Assert.IsType<BadRequestObjectResult>(controller.Estimate(tooManyHosts));

        var tooManySensors = Request(); tooManySensors.SensorIds = Enumerable.Range(1, 15001).Select(x => (long)x).ToList();
        Assert.IsType<BadRequestObjectResult>(controller.Estimate(tooManySensors));

        var unsupportedSixteenK = Request(); unsupportedSixteenK.SensorIds = Enumerable.Range(1, 16000).Select(x => (long)x).ToList();
        Assert.IsType<BadRequestObjectResult>(controller.Estimate(unsupportedSixteenK));

        var unsupportedExpansion = Request(); unsupportedExpansion.SensorIds = Enumerable.Range(1, 30000).Select(x => (long)x).ToList();
        var result = Assert.IsType<BadRequestObjectResult>(controller.Estimate(unsupportedExpansion));
        Assert.Contains("unsupported_capacity", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void 暫停Sensor不得留在新正式範圍()
    {
        var controller = Controller();
        _backend.PrtgStore().UpsertSensors([new() { Objid = 100, DeviceObjid = 10, Name = "Ping", SensorType = "ping", Paused = true }], DateTime.Now);
        Assert.IsType<BadRequestObjectResult>(Estimate(controller, Request()));
    }

    [Fact]
    public void V1匯入隔離跨站主機與人工對應_重複匯入不啟用正式判定()
    {
        Save(Controller(), Request());
        var package = PrtgDataTransfer.Export(_backend, DateTime.Today, DateTime.Today); package.FormatVersion = 1;
        package.ManualMaps = [new() { DeviceObjid = 10, HostId = 2 }];
        var beforePolicy = JsonSerializer.Serialize(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get());
        var beforeMaps = JsonSerializer.Serialize(_backend.PrtgStore().GetHostMapForDate(DateTime.Today));
        var beforeManualMaps = JsonSerializer.Serialize(_backend.PrtgStore().GetManualMaps());
        PrtgDataTransfer.Import(_backend, package); PrtgDataTransfer.Import(_backend, package);
        Assert.Equal(beforeMaps, JsonSerializer.Serialize(_backend.PrtgStore().GetHostMapForDate(DateTime.Today)));
        Assert.Equal(beforeManualMaps, JsonSerializer.Serialize(_backend.PrtgStore().GetManualMaps()));
        Assert.Equal(beforePolicy, JsonSerializer.Serialize(new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get()));
        using var db = _backend.CreateContext(); Assert.Empty(db.PrtgObservations); Assert.Empty(db.IssueCases); Assert.Empty(db.WorkOrders);
        Assert.Single(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));
    }
    [Fact]
    public void 驗收證據匯出與人工標籤依主機授權_不能偷標私有主機()
    {
        Save(Controller(), Request());
        var api = new PrtgAcceptanceController(_backend, new Visible(1), FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
        Assert.IsType<ForbidResult>(api.Save(new() { HostId = 2 }));
        var file = Assert.IsType<FileContentResult>(api.Export(DateTime.Today.AddDays(-1), DateTime.Today));
        var json = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.DoesNotContain("PRIVATE", json); Assert.Contains("ScopeComplete", json);
        Assert.Contains("manual-site-acceptance", json);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
