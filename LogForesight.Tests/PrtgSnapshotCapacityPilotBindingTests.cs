using System.Net;
using System.Globalization;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotCapacityPilotBindingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-snapshot-capacity-pilot-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgMonitoringPolicyStore _policy;
    private readonly PrtgTrustedSamplingBindingStore _bindings;
    private readonly WebHost _host;

    public PrtgSnapshotCapacityPilotBindingTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _host = _hosts.Upsert(new WebHost { HostName = "capacity-pilot-host", IpAddress = "192.0.2.77", Active = true });
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _settings.Update(value =>
        {
            value.PrtgEnabled = true;
            value.PrtgUrl = "https://prtg.example.test";
            value.PrtgAuthMode = PrtgAuthModes.Token;
            value.PrtgApiTokenEnc = "capacity-pilot-fixture-token";
            value.PrtgTimeoutSeconds = 30;
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            value.PrtgValueFetchScope = PrtgValueFetchScope.AllMapped;
            value.PrtgSensorTypeWhitelist = ["CPU"];
        });
        _policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        _policy.Update(value =>
        {
            value.Revision = "capacity-pilot-policy-r1";
            value.CoreSystemId = "capacity-pilot-core";
            value.SourceGeneration = "capacity-pilot-generation";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(_settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            value.HostIds = [_host.HostId];
            value.SensorIds = [77];
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "capacity-pilot-time-basis";
        });
        _backend.PrtgStore().UpsertSensors([new PrtgSensorRow
        {
            Objid = 77, DeviceObjid = 22, Name = "CPU", SensorType = "CPU", Status = "Up", Paused = false
        }], DateTime.UtcNow);
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = _host.HostId, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        _backend.PrtgStore().BindObservedResource(77, _host.HostId, "capacity-pilot-generation",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
        _bindings = new PrtgTrustedSamplingBindingStore(_backend);
        var identity = _backend.PrtgStore().GetResourceIdentity(77);
        var currentSettings = _settings.Get();
        var currentPolicy = _policy.Get();
        _bindings.Save(new PrtgTrustedSamplingBindingUpdate(77, currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", currentPolicy.TimeBasisEvidenceReference));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FallbackCostPilotMeasuresAnUnqualifiedBindingWithoutCreatingAProfile()
    {
        Assert.Empty(new PrtgTrustedSamplingProfileStore(_backend).GetMany([77]));
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? Response("<prtg><result>3</result></prtg>", "application/xml")
                : Response(SnapshotJson())));
        var pilot = CreatePilot(handler);

        var result = await pilot.RunAsync(_hosts, _settings.Get(), [], CancellationToken.None);

        var evidence = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read();
        Assert.Equal(1, result.PotentialQualifiedSensors);
        Assert.Equal("fallback", result.PrimaryChannelCapabilityMode);
        Assert.Equal(4, result.RequestsSent);
        Assert.Equal(4, result.RequestsAttempted);
        Assert.Equal(4, result.MaximumRequestsPerRun);
        Assert.Equal(1, result.MatchingFallbackGroupSamples);
        Assert.Equal("capacity-unverified", result.Status); // one of five bounded samples is not admission
        Assert.Contains(evidence, sample => sample.EvidenceKind == "fallback-group" && sample.Outcome == "success" &&
            sample.FallbackGroupSensorCount == 1 && sample.FallbackGroupRequestCount == 3);
        Assert.Empty(new PrtgTrustedSamplingProfileStore(_backend).GetMany([77]));
    }

    [Fact]
    public async Task FallbackCostPilotDoesNotPersistEvidenceWhenSelectedBindingChangesMidBracket()
    {
        var propertyRequests = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal) && ++propertyRequests == 2)
                ReplaceSelectedBinding("4");
            return Task.FromResult(path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? Response("<prtg><result>3</result></prtg>", "application/xml")
                : Response(SnapshotJson()));
        });
        var pilot = CreatePilot(handler);

        var result = await pilot.RunAsync(_hosts, _settings.Get(), [], CancellationToken.None);

        var evidence = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read();
        Assert.Equal("capacity-unverified", result.Status);
        Assert.DoesNotContain(evidence, sample => sample.EvidenceKind == "fallback-group" && sample.Outcome == "success");
    }

    [Fact]
    public async Task FallbackPilotCountsSentAndAttemptedRequestsWhenAGroupCallReturnsInvalidStatus()
    {
        var tableRequests = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal))
                return Task.FromResult(Response("<prtg><result>3</result></prtg>", "application/xml"));
            tableRequests++;
            return Task.FromResult(tableRequests == 2
                ? Response("unavailable", "application/json", HttpStatusCode.ServiceUnavailable)
                : Response(SnapshotJson()));
        });
        var pilot = CreatePilot(handler);

        var result = await pilot.RunAsync(_hosts, _settings.Get(), [], CancellationToken.None);

        Assert.Equal(3, result.RequestsSent);
        Assert.Equal(3, result.RequestsAttempted);
        Assert.Equal(4, result.MaximumRequestsPerRun);
        Assert.DoesNotContain(new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read(),
            sample => sample.EvidenceKind == "fallback-group" && sample.Outcome == "success");
    }

    [Fact]
    public async Task FallbackModeWithoutQualifiedBindingsStillCollectsFiveBatchSamples()
    {
        var binding = _bindings.Get(77)!;
        using (var context = _backend.CreateContext())
        {
            var row = context.Blobs.Single(blob => blob.BlobKey == binding.StoreKey);
            context.Blobs.Remove(row);
            context.SaveChanges();
        }
        var selection = PrtgSnapshotTargetResolver.CreateSelection([77], 1, _backend, _settings.Get(), _policy.Get());
        Assert.Equal(0, selection.PotentialQualifiedSensorCount);
        var fallbackSelection = PrtgSnapshotTargetResolver.WithPrimaryChannelMode(selection,
            PrtgSnapshotTargetResolver.PrimaryChannelModeFallback);
        var now = DateTimeOffset.UtcNow;
        var store = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            store.Record(new PrtgSnapshotCapacitySample(fallbackSelection.ScopeFingerprint,
                fallbackSelection.EndpointFingerprint, fallbackSelection.RequestShapeFingerprint,
                now.AddSeconds(-index), 1_000, 1, "success", "fallback"));

        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? Response("<prtg><result>3</result></prtg>", "application/xml")
                : Response(SnapshotJson())));
        var pilot = CreatePilot(handler);
        var result = await pilot.RunAsync(_hosts, _settings.Get(), [], CancellationToken.None);

        Assert.Equal("capacity-qualified", result.Status);
        Assert.Equal(5, result.RequestsSent);
        Assert.Equal(5, result.RequestsAttempted);
        Assert.Equal(5, result.MaximumRequestsPerRun);
        Assert.Equal(5, result.MatchingFullBatchSamples);
    }

    [Fact]
    public void PrimaryChannelDetectionCountsOnlyCandidateRowsAndRecognizesRawStringIdAndMixedShape()
    {
        const string json = "{\"sensors\":[" +
            "{\"objid\":\"77\",\"PrimaryChannel_Raw\":\"3\"}," +
            "{\"objid\":78,\"primarychannel\":4}," +
            "{\"objid\":79}]}";

        Assert.Equal(1, PrtgSnapshotCapacityPilot.CountPrimaryChannelFields(json, [77]));
        Assert.Equal(1, PrtgSnapshotCapacityPilot.CountPrimaryChannelFields(json, [77, 79]));
    }

    private PrtgSnapshotCapacityPilot CreatePilot(HttpMessageHandler handler) => new(_backend, _ =>
        new PrtgClient("https://prtg.example.test", "token", 30, true, handler,
            PrtgAuthModes.Token, "", "", "", new PrtgRequestBudget()));

    private void ReplaceSelectedBinding(string channelId)
    {
        var identity = _backend.PrtgStore().GetResourceIdentity(77);
        var binding = _bindings.Get(77)!;
        var settings = _settings.Get();
        var policy = _policy.Get();
        _bindings.Save(new PrtgTrustedSamplingBindingUpdate(77, settings.Revision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, binding.BindingRevision, channelId, "CPU",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds", "UTC", "UTC",
            policy.TimeBasisEvidenceReference));
    }

    private static HttpResponseMessage Response(string content, string contentType = "application/json",
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    { Content = new StringContent(content, Encoding.UTF8, contentType) };

    private static string SnapshotJson()
    {
        var now = DateTime.UtcNow;
        return "{\"sensors\":[{\"objid\":77,\"lastvalue_raw\":42,\"interval\":\"60 s\",\"interval_raw\":60," +
            "\"lastcheck\":\"" + now.ToString("O", CultureInfo.InvariantCulture) + "\",\"lastcheck_raw\":" +
            now.ToOADate().ToString("R", CultureInfo.InvariantCulture) + ",\"status\":\"Up\"}]}";
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
