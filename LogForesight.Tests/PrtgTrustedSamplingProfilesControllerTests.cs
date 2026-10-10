using System.Reflection;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingProfilesControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-trusted-binding-api-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgMonitoringPolicyStore _policy;
    private readonly WebHost _host;
    private readonly TestUser _user = new(Capability.Maintain);

    public PrtgTrustedSamplingProfilesControllerTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _settings = new(_backend.Blob("system_settings"));
        _settings.Update(value =>
        {
            value.PrtgEnabled = true;
            value.PrtgUrl = "https://source.example.test";
            value.PrtgAuthMode = PrtgAuthModes.Token;
            value.PrtgApiTokenEnc = "controller-input-fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        _host = _hosts.Upsert(new WebHost
        { HostName = "trusted-binding-api-fixture", IpAddress = "192.0.2.33", Active = true });
        _policy = new(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        _policy.Update(value =>
        {
            value.Revision = "policy-api-r1";
            value.CoreSystemId = "trusted-binding-api-fixture";
            value.SourceGeneration = "source-api-r1";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(_settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            value.HostIds = [_host.HostId];
            value.SensorIds = [11, 12];
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "api-fixture-time-basis";
        });
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = 22, HostId = _host.HostId,
            HostName = _host.HostName, Ip = _host.IpAddress, MapStatus = PrtgMapStatus.Ok
        }]);
        _backend.PrtgStore().UpsertSensors(new[] { 11L, 12L }.Select(id => new PrtgSensorRow
        { Objid = id, DeviceObjid = 22, SensorType = "CPU" }).ToArray(), DateTime.UtcNow);
        foreach (var sensorId in new long[] { 11, 12 })
            _backend.PrtgStore().BindObservedResource(sensorId, _host.HostId, "source-api-r1",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
    }

    [Fact]
    public void BindingsControllerDeclaresMaintainAndRejectsRequestsOutsideVisibleOrPolicyScope()
    {
        var attributes = typeof(PrtgTrustedSamplingProfilesController)
            .GetCustomAttributes<PermissionAttribute>(inherit: true).ToArray();
        var permission = Assert.Single(attributes);
        var required = Assert.IsType<Capability[]>(Assert.Single(permission.Arguments!));
        Assert.Equal(new[] { Capability.Maintain }, required);

        var http = new DefaultHttpContext();
        http.Request.Path = "/api/prtg/monitoring/trusted-sampling/bindings/11";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(action, new List<IFilterMetadata>());
        var audit = new RecordingAudit();
        new PermissionFilter(required, new TestUser(), audit).OnAuthorization(filterContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(filterContext.Result).StatusCode);
        Assert.True(audit.Denied);

        var controller = CreateController(new TestVisibility([]));
        Assert.IsType<ForbidResult>(controller.GetBinding(11));
        var outsidePolicy = Assert.IsType<BadRequestObjectResult>(controller.GetBinding(99));
        Assert.Equal("sensor_outside_policy", Assert.IsAssignableFrom<ApiResponse<object>>(outsidePolicy.Value).Error!.Code);
    }

    [Fact]
    public void NumericQuantityDtoSavesBindingAndGetReturnsFencedWaitingState()
    {
        var identity = _backend.PrtgStore().GetResourceIdentity(11);
        var settings = _settings.Get();
        var policy = _policy.Get();
        var json = $$"""{"sensorObjid":11,"expectedSettingsRevision":"{{settings.Revision}}","expectedPolicyRevision":"{{policy.Revision}}","expectedIdentityEpoch":{{identity.Epoch}},"expectedChannelGeneration":"{{identity.ChannelGeneration}}","expectedBindingRevision":0,"channelObjectId":"007","expectedCaption":"Load","quantity":1,"unit":"%","scale":1,"direction":"direct","intervalRawUnit":"seconds","rawTimestampTimeZoneId":"UTC","analysisTimeZoneId":"UTC","timeBasisEvidenceReference":"api-fixture-time-basis"}""";
        var request = Assert.IsType<PrtgTrustedSamplingBindingRequest>(JsonSerializer.Deserialize<PrtgTrustedSamplingBindingRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(PrtgTrustedQuantitySemantic.CpuLoadPercent, request.Quantity);
        Assert.Equal("007", request.ChannelObjectId);

        var controller = CreateController(new TestVisibility([_host.HostId]));
        var saved = Assert.IsType<OkObjectResult>(controller.SaveBinding(11, request));
        var savedDto = Assert.IsType<PrtgTrustedSamplingBindingStatusDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingStatusDto>>(saved.Value).Data);
        Assert.Equal("waiting", savedDto.Status);
        Assert.Contains("qualification_required", savedDto.MissingFacts);

        var get = Assert.IsType<OkObjectResult>(controller.GetBinding(11));
        var dto = Assert.IsType<PrtgTrustedSamplingBindingEditDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingEditDto>>(get.Value).Data);
        Assert.Equal(1, dto.ExpectedBindingRevision);
        Assert.Equal(settings.Revision, dto.ExpectedSettingsRevision);
        Assert.Equal(policy.Revision, dto.ExpectedPolicyRevision);
        Assert.Equal("7", dto.Binding!.ChannelObjectId);
        Assert.Equal("waiting", dto.Binding.Status);
        Assert.Equal("api-fixture-time-basis", dto.Binding.TimeBasisEvidenceReference);
    }

    [Fact]
    public void PostWriteCatalogueDriftReturnsCommitReceiptAndUnauthorizedSaveWritesNothing()
    {
        var unauthorized = CreateController(new TestVisibility([]));
        var unauthorizedRequest = BindingRequest(11, "70");
        Assert.IsType<ForbidResult>(unauthorized.SaveBinding(11, unauthorizedRequest));
        Assert.Null(new PrtgTrustedSamplingBindingStore(_backend).Get(11));
        Assert.IsType<ForbidResult>(unauthorized.SaveBindingsBatch(new PrtgTrustedSamplingBindingBatchRequest(
            [unauthorizedRequest, BindingRequest(12, "70")])));
        Assert.Null(new PrtgTrustedSamplingBindingStore(_backend).Get(12));

        var controller = CreateController(new TestVisibility([_host.HostId]));
        var captures = 0;
        controller.HostSnapshotProviderOverride = () =>
        {
            captures++;
            if (captures == 2)
                _hosts.Upsert(new WebHost { HostName = "binding-receipt-catalogue-drift", IpAddress = "192.0.2.34", Active = true });
            return _hosts.CapturePrtgSnapshot();
        };

        var response = Assert.IsType<OkObjectResult>(controller.SaveBinding(11, BindingRequest(11, "70")));
        var receipt = Assert.IsType<PrtgTrustedSamplingCommitReceipt>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingCommitReceipt>>(response.Value).Data);
        Assert.True(receipt.Committed);
        Assert.True(receipt.ReloadRequired);
        Assert.Equal(new long[] { 11 }, receipt.CommittedSensorObjids);
        Assert.Equal("70", new PrtgTrustedSamplingBindingStore(_backend).Get(11)!.ChannelObjectId);
    }

    [Fact]
    public async Task BatchStopsAfterFirstRowWhenCatalogueDriftsAndQualificationReceiptRequiresOwnWriteEvidence()
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        var first = BindingRequest(11, "71");
        var second = BindingRequest(12, "72");
        var captures = 0;
        controller.HostSnapshotProviderOverride = () =>
        {
            captures++;
            if (captures == 4)
                _hosts.Upsert(new WebHost { HostName = "binding-batch-catalogue-drift", IpAddress = "192.0.2.35", Active = true });
            return _hosts.CapturePrtgSnapshot();
        };
        var batchResponse = Assert.IsType<OkObjectResult>(controller.SaveBindingsBatch(
            new PrtgTrustedSamplingBindingBatchRequest([first, second])));
        var batchReceipt = Assert.IsType<PrtgTrustedSamplingCommitReceipt>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingCommitReceipt>>(batchResponse.Value).Data);
        Assert.Equal(new long[] { 11 }, batchReceipt.CommittedSensorObjids);
        var bindings = new PrtgTrustedSamplingBindingStore(_backend);
        Assert.Equal("71", bindings.Get(11)!.ChannelObjectId);
        Assert.Null(bindings.Get(12));

        var qualificationController = CreateController(new TestVisibility([_host.HostId]));
        var savedDto = Assert.IsType<PrtgTrustedSamplingBindingStatusDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingStatusDto>>(
                Assert.IsType<OkObjectResult>(qualificationController.SaveBinding(12, BindingRequest(12, "73"))).Value).Data);
        var qualificationRequest = new PrtgTrustedSamplingBindingQualificationRequest(
            savedDto.BindingRevision, savedDto.BindingFingerprint);
        var qualificationCaptures = 0;
        qualificationController.HostSnapshotProviderOverride = () =>
        {
            qualificationCaptures++;
            if (qualificationCaptures == 2)
                _hosts.Upsert(new WebHost { HostName = "binding-qualification-catalogue-drift", IpAddress = "192.0.2.36", Active = true });
            return _hosts.CapturePrtgSnapshot();
        };
        qualificationController.QualificationProbeOverride = (sensorId, request, _) =>
        {
            bindings.RecordQualification(sensorId, request.ExpectedBindingRevision,
                request.ExpectedBindingFingerprint, _settings.Get().Revision, _policy.Get().Revision,
                91, DateTime.UtcNow.ToOADate(), "source-api-r1", DateTimeOffset.UtcNow, new string('E', 64));
            var updatedIdentity = _backend.PrtgStore().GetResourceIdentity(sensorId);
            IReadOnlyList<PrtgTrustedSamplingProbeRow> rows = [new PrtgTrustedSamplingProbeRow(sensorId,
                "waiting", [], 22, "CPU", 3, 91, DateTime.UtcNow.ToOADate(), 60, [], false, [],
                updatedIdentity.Generation, updatedIdentity.Epoch, updatedIdentity.ChannelGeneration,
                DateTimeOffset.UtcNow, false) { QualificationRecorded = true }];
            return Task.FromResult(rows);
        };
        var qualificationResponse = Assert.IsType<OkObjectResult>(await qualificationController.QualifyBinding(
            12, qualificationRequest, CancellationToken.None));
        var qualificationReceipt = Assert.IsType<PrtgTrustedSamplingCommitReceipt>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingCommitReceipt>>(qualificationResponse.Value).Data);
        Assert.Equal(new long[] { 12 }, qualificationReceipt.CommittedSensorObjids);
        Assert.NotEmpty(bindings.Get(12)!.QualificationProofReference);

        var noWriteController = CreateController(new TestVisibility([_host.HostId]));
        var noWriteCaptures = 0;
        noWriteController.HostSnapshotProviderOverride = () =>
        {
            noWriteCaptures++;
            if (noWriteCaptures == 2)
                _hosts.Upsert(new WebHost { HostName = "binding-no-write-drift", IpAddress = "192.0.2.37", Active = true });
            return _hosts.CapturePrtgSnapshot();
        };
        noWriteController.QualificationProbeOverride = (sensorId, _, _) =>
        {
            var identity = _backend.PrtgStore().GetResourceIdentity(sensorId);
            IReadOnlyList<PrtgTrustedSamplingProbeRow> rows = [new PrtgTrustedSamplingProbeRow(sensorId,
                "waiting", [], 22, "CPU", 3, 91, DateTime.UtcNow.ToOADate(), 60, [], false, [],
                identity.Generation, identity.Epoch, identity.ChannelGeneration, DateTimeOffset.UtcNow, false)];
            return Task.FromResult(rows);
        };
        var currentBinding = bindings.Get(12)!;
        Assert.IsType<ConflictObjectResult>(await noWriteController.QualifyBinding(12,
            new(currentBinding.BindingRevision, currentBinding.BindingFingerprint), CancellationToken.None));
    }

    [Fact]
    public async Task BindingBatchAndQualificationEndpointsRejectMalformedOrUnboundedInput()
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        var identity = _backend.PrtgStore().GetResourceIdentity(12);
        var settings = _settings.Get();
        var policy = _policy.Get();
        var valid = new PrtgTrustedSamplingBindingRequest(12, settings.Revision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "api-fixture-time-basis");
        var batch = Assert.IsType<OkObjectResult>(controller.SaveBindingsBatch(
            new PrtgTrustedSamplingBindingBatchRequest([valid])));
        var result = Assert.Single(Assert.IsType<ApiResponse<IReadOnlyList<PrtgTrustedSamplingBindingBatchResult>>>(batch.Value).Data!);
        Assert.Equal("waiting", result.Status);
        Assert.NotNull(result.BindingRevision);

        var tooMany = Enumerable.Repeat(valid, 101).ToArray();
        Assert.IsType<BadRequestObjectResult>(controller.SaveBindingsBatch(
            new PrtgTrustedSamplingBindingBatchRequest(tooMany)));
        var invalidChannel = valid with { ChannelObjectId = "3x" };
        Assert.IsType<BadRequestObjectResult>(controller.SaveBinding(12, invalidChannel));
        var overflowChannel = valid with { ChannelObjectId = "99999999999999999999" };
        Assert.IsType<BadRequestObjectResult>(controller.SaveBinding(12, overflowChannel));
        Assert.IsType<BadRequestObjectResult>(await controller.QualifyBinding(12,
            new PrtgTrustedSamplingBindingQualificationRequest(0, "not-a-fingerprint"), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedProbeAndQualificationSourceJsonAreClassifiedWithoutWriting()
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        controller.ProbeOverride = (_, _) =>
            Task.FromException<IReadOnlyList<PrtgTrustedSamplingProbeRow>>(
                new JsonException("secret source body must not be returned"));

        var probe = Assert.IsType<ConflictObjectResult>(await controller.Probe(
            new PrtgTrustedSamplingProbeRequest([11]), CancellationToken.None));
        Assert.Equal("probe_source_shape_invalid", Assert.IsAssignableFrom<ApiResponse<object>>(probe.Value).Error!.Code);
        Assert.Empty(new PrtgTrustedSamplingProfileStore(_backend).GetMany([11]));

        var saved = Assert.IsType<PrtgTrustedSamplingBindingStatusDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingStatusDto>>(
                Assert.IsType<OkObjectResult>(controller.SaveBinding(11, BindingRequest(11, "74"))).Value).Data);
        var bindingStore = new PrtgTrustedSamplingBindingStore(_backend);
        var before = bindingStore.Get(11)!;
        controller.QualificationProbeOverride = (_, _, _) =>
            Task.FromException<IReadOnlyList<PrtgTrustedSamplingProbeRow>>(
                new JsonException("secret qualification source body must not be returned"));

        var qualification = Assert.IsType<ConflictObjectResult>(await controller.QualifyBinding(11,
            new(saved.BindingRevision, saved.BindingFingerprint), CancellationToken.None));
        var error = Assert.IsAssignableFrom<ApiResponse<object>>(qualification.Value).Error!;
        Assert.Equal("qualification_source_shape_invalid", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        var after = bindingStore.Get(11)!;
        Assert.Equal(before.BindingRevision, after.BindingRevision);
        Assert.Empty(after.QualificationProofReference);
    }

    [Fact]
    public void IdempotentSingleAndBatchSaveReportExistingQualificationAccurately()
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        var bindingStore = new PrtgTrustedSamplingBindingStore(_backend);

        var identity = _backend.PrtgStore().GetResourceIdentity(11);
        var single = new PrtgTrustedSamplingBindingRequest(11, _settings.Get().Revision, _policy.Get().Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "api-fixture-time-basis");
        var firstSingle = Assert.IsType<PrtgTrustedSamplingBindingStatusDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingStatusDto>>(
                Assert.IsType<OkObjectResult>(controller.SaveBinding(11, single)).Value).Data);
        Assert.Equal("waiting", firstSingle.Status);
        var singleIdentity = _backend.PrtgStore().GetResourceIdentity(11);
        var singleQualified = bindingStore.RecordQualification(11, firstSingle.BindingRevision,
            firstSingle.BindingFingerprint, _settings.Get().Revision, _policy.Get().Revision, 91,
            DateTime.UtcNow.ToOADate(), "source-api-r1", DateTimeOffset.UtcNow, new string('C', 64));
        singleIdentity = _backend.PrtgStore().GetResourceIdentity(11);
        var repeatedSingle = single with { ExpectedIdentityEpoch = singleIdentity.Epoch,
            ExpectedChannelGeneration = singleIdentity.ChannelGeneration,
            ExpectedBindingRevision = singleQualified.BindingRevision };
        var secondSingle = Assert.IsType<PrtgTrustedSamplingBindingStatusDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingStatusDto>>(
                Assert.IsType<OkObjectResult>(controller.SaveBinding(11, repeatedSingle)).Value).Data);
        Assert.Equal("qualified", secondSingle.Status);
        Assert.Empty(secondSingle.MissingFacts);

        var batchIdentity = _backend.PrtgStore().GetResourceIdentity(12);
        var batchRow = new PrtgTrustedSamplingBindingRequest(12, _settings.Get().Revision, _policy.Get().Revision,
            batchIdentity.Epoch, batchIdentity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "api-fixture-time-basis");
        var firstBatch = Assert.Single(Assert.IsType<ApiResponse<IReadOnlyList<PrtgTrustedSamplingBindingBatchResult>>>(
            Assert.IsType<OkObjectResult>(controller.SaveBindingsBatch(
                new PrtgTrustedSamplingBindingBatchRequest([batchRow]))).Value).Data!);
        Assert.Equal("waiting", firstBatch.Status);
        var batchCurrent = _backend.PrtgStore().GetResourceIdentity(12);
        var batchQualified = bindingStore.RecordQualification(12, firstBatch.BindingRevision!.Value,
            firstBatch.BindingFingerprint!, _settings.Get().Revision, _policy.Get().Revision, 91,
            DateTime.UtcNow.ToOADate(), "source-api-r1", DateTimeOffset.UtcNow, new string('D', 64));
        batchCurrent = _backend.PrtgStore().GetResourceIdentity(12);
        var repeatedBatchRow = batchRow with { ExpectedIdentityEpoch = batchCurrent.Epoch,
            ExpectedChannelGeneration = batchCurrent.ChannelGeneration,
            ExpectedBindingRevision = batchQualified.BindingRevision };
        var secondBatch = Assert.Single(Assert.IsType<ApiResponse<IReadOnlyList<PrtgTrustedSamplingBindingBatchResult>>>(
            Assert.IsType<OkObjectResult>(controller.SaveBindingsBatch(
                new PrtgTrustedSamplingBindingBatchRequest([repeatedBatchRow]))).Value).Data!);
        Assert.Equal("qualified", secondBatch.Status);
        Assert.Empty(secondBatch.MissingFacts);
    }

    [Fact]
    public void WrappedProbeDtoSerializesMaxInt64ChannelIdAsLosslessString()
    {
        var probe = new PrtgTrustedSamplingProbeRow(11, "waiting", [], 22, "CPU", 3, 95, 1,
            60, [new PrtgTrustedSamplingChannelProbeRow(long.MaxValue, "Large", "%", 95,
                true, null, null, null, null, null, null, null)], false, [], "resource-g1", 1,
            "channel-g1", DateTimeOffset.UtcNow, false, null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(ApiResponse<PrtgTrustedSamplingProbeRow>.Ok(probe), options);
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("data").GetProperty("channels")[0]
            .GetProperty("channelObjectId");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("9223372036854775807", id.GetString());
        var restored = JsonSerializer.Deserialize<PrtgTrustedSamplingProbeRow>(
            document.RootElement.GetProperty("data").GetRawText(), options);
        Assert.Equal(long.MaxValue, Assert.Single(restored!.Channels).ChannelObjectId);
    }

    [Fact]
    public void ProfilePagesExposeCurrentSourceContextForFullScopeCoverage()
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        var response = Assert.IsType<OkObjectResult>(controller.Get());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var page = json.RootElement.GetProperty("data");
        var settings = _settings.Get();
        var policy = _policy.Get();
        Assert.Equal(settings.Revision, page.GetProperty("settingsRevision").GetString());
        Assert.Equal(policy.Revision, page.GetProperty("policyRevision").GetString());
        Assert.Equal(policy.SourceGeneration, page.GetProperty("sourceGeneration").GetString());
        Assert.True(page.GetProperty("prtgEnabled").GetBoolean());
        Assert.Equal(PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            PrtgFetchStrategy.Conservative, 15), page.GetProperty("authorityContextFingerprint").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfilePagesRejectSettingsChangesDuringRead(bool disableSource)
    {
        var controller = CreateController(new TestVisibility([_host.HostId]));
        controller.HostSnapshotProviderOverride = () =>
        {
            _settings.Update(value =>
            {
                if (disableSource) value.PrtgEnabled = false;
                else value.BrandName = "Changed while profile page was read";
            });
            return _hosts.CapturePrtgSnapshot();
        };
        var response = Assert.IsType<ConflictObjectResult>(controller.Get());
        Assert.Equal("catalogue_changed", Assert.IsAssignableFrom<ApiResponse<object>>(response.Value).Error!.Code);
    }

    private PrtgTrustedSamplingBindingRequest BindingRequest(long sensorId, string channelId)
    {
        var identity = _backend.PrtgStore().GetResourceIdentity(sensorId);
        return new(sensorId, _settings.Get().Revision, _policy.Get().Revision, identity.Epoch,
            identity.ChannelGeneration, 0, channelId, "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", "api-fixture-time-basis");
    }

    private PrtgTrustedSamplingProfilesController CreateController(IVisibilityService visibility) => new(
        _backend, _hosts, visibility, _user,
        new PrtgTrustedSamplingProfileRefreshHostedService(_backend, _hosts,
            NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance));

    private sealed class TestUser(params Capability[] capabilities) : ICurrentUser
    {
        private readonly HashSet<Capability> _capabilities = capabilities.ToHashSet();
        public bool IsAuthenticated => true;
        public long UserId => 1;
        public string Account => "binding-api-test";
        public string DisplayName => "Binding API Test";
        public IReadOnlySet<Capability> Capabilities => _capabilities;
        public bool IsServerAdmin => false;
        public bool Has(Capability capability) => _capabilities.Contains(capability);
    }

    private sealed class TestVisibility(IEnumerable<long> visible) : IVisibilityService
    {
        private readonly HashSet<long> _visible = visible.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds() => _visible;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long hostId) { if (!_visible.Contains(hostId)) throw new UnauthorizedAccessException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
    }

    private sealed class RecordingAudit : IAuditService
    {
        public bool Denied { get; private set; }
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) =>
            Denied = action == "access_denied" && result == AuditResult.Denied;
        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
