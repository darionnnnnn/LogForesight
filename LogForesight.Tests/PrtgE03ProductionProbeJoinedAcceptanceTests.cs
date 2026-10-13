using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SQLite application-contract acceptance through the production probe service and trusted
/// sample consumers. The source transport is an explicit UTC synthetic fixture, not a native PRTG claim.
/// </summary>
public sealed class PrtgE03ProductionProbeJoinedAcceptanceTests : IDisposable
{
    private const long HostId = 43100;
    private const string SourceGeneration = "e03-production-probe-source";
    private const string Url = "https://e03-controlled-prtg.test";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-e03-probe-joined-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgMonitoringPolicyStore _policy;
    private readonly MutableTimeProvider _clock;
    private readonly Descriptor[] _resources =
    [
        new(43102, 43101, "SNMP CPU Load", "CPU Load", "3", 25.0834,
            PrtgTrustedQuantitySemantic.CpuLoadPercent),
        new(43104, 43103, "SNMP Memory", "Available Memory", "4", 25.0834,
            PrtgTrustedQuantitySemantic.MemoryAvailablePercent),
        new(43106, 43105, "SNMP Disk Free", "Free Space", "5", 90.0887,
            PrtgTrustedQuantitySemantic.DiskFreePercent)
    ];

    public PrtgE03ProductionProbeJoinedAcceptanceTests()
    {
        var realNow = DateTimeOffset.UtcNow;
        var anchor = new DateTimeOffset(realNow.Year, realNow.Month, realNow.Day,
            realNow.Hour, 0, 0, TimeSpan.Zero).AddHours(-4);
        _clock = new MutableTimeProvider(anchor);
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "e03-probe.db")}"
        }, _directory);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _settings.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = Url;
            settings.PrtgAuthMode = PrtgAuthModes.Token;
            settings.PrtgApiTokenEnc = CryptoHelper.Encrypt("synthetic-probe-token");
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        _policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        _policy.Update(policy =>
        {
            policy.Revision = "e03-production-probe-policy-v1";
            policy.CoreSystemId = "e03-controlled-source";
            policy.SourceGeneration = SourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(Url);
            policy.ValidFrom = _clock.GetUtcNow().AddDays(-2);
            policy.HostIds = [HostId];
            policy.SensorIds = _resources.Select(resource => resource.SensorId).ToList();
            policy.ConfirmedBy = "isolated-test-maintainer";
            // These UTC facts describe only this generated fixture: its response clock and raw OA dates
            // are constructed from UTC values. Unknown native deployment time bases remain untrusted.
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "controlled-fixture-explicit-utc-clock";
        });
        using (var db = _backend.CreateContext())
        {
            foreach (var resource in _resources)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = resource.DeviceId, Name = $"fixture-{resource.SensorType}" });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = resource.SensorId,
                    DeviceObjid = resource.DeviceId,
                    SensorType = resource.SensorType,
                    Category = resource.Semantic switch
                    {
                        PrtgTrustedQuantitySemantic.CpuLoadPercent => PrtgSensorCategories.Cpu,
                        PrtgTrustedQuantitySemantic.MemoryAvailablePercent => PrtgSensorCategories.Memory,
                        _ => PrtgSensorCategories.Disk
                    }
                });
                db.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = resource.DeviceId, HostId = HostId });
            }
            db.SaveChanges();
        }

        var store = _backend.PrtgStore();
        foreach (var resource in _resources)
        {
            store.BindObservedResource(resource.SensorId, HostId, SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(resource.DeviceId.ToString(CultureInfo.InvariantCulture),
                    resource.SensorType, $"fixture-created-{resource.SensorId}", 0));
        }
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules() });
    }

    [Fact]
    public async Task ProductionBindingProbeQualificationProfilesAndExactSamplesReachPeriodConsumer()
    {
        var bindingStore = new PrtgTrustedSamplingBindingStore(_backend, _clock);
        var strategyStore = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey));
        var profiles = new Dictionary<long, PrtgTrustedSamplingProfile>();
        var probeResults = new Dictionary<long, PrtgTrustedSamplingProbeRow>();

        foreach (var resource in _resources)
        {
            var settingsAtSave = _settings.Get();
            var policyAtSave = _policy.Get();
            var identity = _backend.PrtgStore().GetResourceIdentity(resource.SensorId);
            var saved = bindingStore.Save(new PrtgTrustedSamplingBindingUpdate(
                resource.SensorId, settingsAtSave.Revision, policyAtSave.Revision,
                identity.Epoch, identity.ChannelGeneration, 0,
                resource.ChannelId, resource.ChannelCaption, resource.Semantic,
                "%", 1, "direct", "seconds", "UTC", "UTC",
                "controlled-fixture-explicit-utc-clock"));

            var budget = new PrtgRequestBudget();
            budget.SetHistoricCoordinator(new SqlPrtgHistoricRequestCoordinator(_backend));
            using var handler = new SourceHandler(resource, _clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
            var service = new PrtgTrustedSamplingProbeService(_backend, settings =>
                PrtgClientFactory.Create(settings, handler, budget, _clock), _clock);
            var probe = Assert.Single(await service.ProbeAsync([resource.SensorId], CancellationToken.None,
                allowQualification: true,
                expectedBindingRevision: saved.BindingRevision,
                expectedBindingFingerprint: saved.BindingFingerprint));
            Assert.True(probe.Status == "ready", $"Expected ready, got {probe.Status}: {string.Join(",", probe.MissingAuthorityFields)}");
            Assert.True(probe.ProfileRecorded);
            Assert.True(probe.QualificationRecorded);
            Assert.Equal(resource.ChannelId, probe.NativePrimaryChannelPropertyId);
            Assert.Empty(probe.MissingAuthorityFields);

            var qualifiedBinding = Assert.IsType<PrtgTrustedSamplingBinding>(bindingStore.Get(resource.SensorId));
            Assert.Equal(saved.BindingRevision, qualifiedBinding.BindingRevision);
            Assert.NotEmpty(qualifiedBinding.QualificationProofReference);
            Assert.True(qualifiedBinding.QualificationObservedAtUtc.HasValue);
            Assert.True(qualifiedBinding.QualificationObservedAtUtc!.Value <= probe.ProbedAtUtc);
            var profile = Assert.Single(_backend.PrtgStore().GetTrustedSamplingProfiles([resource.SensorId]).Values);
            Assert.Equal(qualifiedBinding.QualificationProofReference, profile.QualificationProofReference);
            Assert.Equal(resource.ChannelId, profile.PrimaryChannelId);
            Assert.Equal(resource.Semantic, profile.Quantity);
            Assert.Equal("%", profile.Unit);
            Assert.Equal(1d, profile.Scale);
            Assert.Equal("direct", profile.Direction);
            Assert.Equal("UTC", profile.RawTimestampTimeZoneId);
            Assert.Equal("UTC", profile.SourceApiTimeZoneId);
            Assert.Equal("UTC", profile.AnalysisTimeZoneId);
            Assert.InRange(_clock.GetUtcNow() - profile.SourceMetadataObservedAtUtc, TimeSpan.Zero, TimeSpan.FromHours(24));
            profiles.Add(resource.SensorId, profile);
            probeResults.Add(resource.SensorId, probe);
        }

        var realNow = DateTime.UtcNow;
        var currentHour = new DateTime(realNow.Year, realNow.Month, realNow.Day,
            realNow.Hour, 0, 0, DateTimeKind.Utc);
        var firstHour = currentHour.AddHours(-2);
        var secondHour = firstHour.AddHours(1);
        var accumulator = new PrtgSnapshotAccumulator();
        var parser = new PrtgTrustedSnapshotParser();

        foreach (var resource in _resources)
        {
            var profile = profiles[resource.SensorId];
            var probe = probeResults[resource.SensorId];
            Assert.True(probe.ProbedAtUtc.UtcDateTime < firstHour,
                "Durable production probe and qualification must precede all trusted sample timestamps.");
            var identity = _backend.PrtgStore().GetResourceIdentity(resource.SensorId);
            var policy = _policy.Get();
            var now = _clock.GetUtcNow().UtcDateTime;
            var strategy = strategyStore.GetCurrent(policy, PrtgFetchStrategy.Conservative, 15, now);
            Assert.True(strategy.Ready);
            var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy,
                resource.SensorId, resource.SensorType, strategy, now, now);
            Assert.True(resolution.Ready, resolution.RejectionReason);
            Assert.Equal(profile.PrimaryChannelId, resolution.Context!.SelectedPrimaryChannelId);
            Assert.True(resolution.Context.RequireCurrentNativePrimaryChannelId);

            foreach (var hour in new[] { firstHour, secondHour })
            for (var slot = 0; slot < 4; slot++)
            {
                var measuredAt = hour.AddMinutes(slot * 15);
                var receivedAt = measuredAt.AddSeconds(10);
                var asOf = measuredAt.AddSeconds(20);
                var context = resolution.Context with { ReceivedAt = receivedAt, AsOf = asOf };
                var raw = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["objid"] = resource.SensorId,
                    ["lastvalue_raw"] = resource.ExactAvailableOrNativeValue,
                    ["lastcheck_raw"] = measuredAt.ToOADate(),
                    ["interval_raw"] = 60,
                    ["status_raw"] = 3,
                    ["primarychannel"] = long.Parse(resource.ChannelId, CultureInfo.InvariantCulture),
                    ["primarychannel_raw"] = long.Parse(resource.ChannelId, CultureInfo.InvariantCulture)
                });
                Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
                    parser.AddToAccumulator(raw, context, accumulator, out var rejection));
                Assert.Null(rejection);
            }
        }

        var drainAsOf = currentHour;
        _clock.SetUtcNow(new DateTimeOffset(drainAsOf));
        var rows = accumulator.DrainAll(4, drainAsOf);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.TrustedProof));
        Assert.All(rows, row => Assert.Equal(100, row.Coverage));
        Assert.Equal(6, _backend.PrtgStore().MergeSampledValues(rows));

        var result = new PrtgResourcePeriodConsumer(_backend, _settings).EvaluateBatch(
            _resources.Select(resource => resource.SensorId).ToArray(),
            DateTime.SpecifyKind(drainAsOf, DateTimeKind.Utc),
            DateTime.UtcNow);
        Assert.Empty(result.FormalFindings);
        var cpu = Assert.Single(result.Assessments, assessment => assessment.SensorObjid == 43102);
        Assert.Equal(25.0834, cpu.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(25.0834, cpu.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, cpu.Decision.WindowCoveragePercent);
        Assert.Equal(2, cpu.Decision.Window.Count);
        var memory = Assert.Single(result.Assessments, assessment => assessment.SensorObjid == 43104);
        Assert.Equal(74.9166, memory.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(74.9166, memory.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, memory.Decision.WindowCoveragePercent);
        Assert.Equal(2, memory.Decision.Window.Count);
        var disk = Assert.Single(result.Assessments, assessment => assessment.SensorObjid == 43106);
        Assert.Equal(90.0887, disk.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(90.0887, disk.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, disk.Decision.WindowCoveragePercent);
        Assert.Equal(2, disk.Decision.Window.Count);

        await AssertRawPingCompatibilityDiagnosticDoesNotAuthorizeRisk();
    }

    private async Task AssertRawPingCompatibilityDiagnosticDoesNotAuthorizeRisk()
    {
        var measured = DateTime.UtcNow.AddMinutes(-5);
        var measuredOa = measured.ToOADate().ToString("R", CultureInfo.InvariantCulture);
        var samples = new[] { new PrtgProbeRunner.SensorTypeSample("Ping", "ms", 22, 43200, "Up") };
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors", StringComparison.Ordinal)
                ? $"{{\"sensors\":[{{\"objid\":43200,\"lastvalue_raw\":25.0834,\"lastcheck_raw\":{measuredOa}}}]}}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":-1,\"name\":\"Ping Time\",\"unit\":\"ms\"}]}"
                    : "{\"histdata\":[]}"),
            new NoopProbeConsole(), samples, context: null,
            getHistoricXml: (_, _) => Task.FromResult(new PrtgSourceResponse(
                $"<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>{measuredOa}</datetime_raw>" +
                "<value channel=\"Ping Time\" channelid=\"-1\">25.0834 ms</value>" +
                "<value_raw channel=\"Ping Time\" channelid=\"-1\">25.0834</value_raw></item></histdata>",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var target = Assert.Single(evidence.Targets, item => item.Category == "ping");
        Assert.Equal("partial", target.Status);
        var channels = Assert.IsType<PrtgChannelsEvidence>(target.Channels);
        Assert.Equal("partial", channels.Status);
        Assert.Contains("response parent sensor id is unavailable", channels.ParentIdentity,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, channels.TotalRows);
        Assert.Equal("-1", Assert.Single(channels.Rows).ChannelId);
        var raw = Assert.IsType<PrtgRawChannelIdentityEvidence>(target.RawChannelIdentity);
        Assert.Equal("ok", raw.Status);
        Assert.Equal(1, raw.SampleCount);
        Assert.Equal("24.1.92.1554+", raw.SourceVersion);
        Assert.Equal("historic-xml", raw.ResponseShape);
        Assert.False(string.IsNullOrWhiteSpace(raw.ReceivedAtUtc));
        Assert.NotNull(raw.FirstRawOaDate);
        Assert.Equal(raw.FirstRawOaDate, raw.LastRawOaDate);
        var rawValue = Assert.Single(Assert.Single(raw.Samples).Channels);
        Assert.Equal("-1", rawValue.ChannelId);
        Assert.Equal("ping time", rawValue.SemanticCaption);
        Assert.Equal(25.0834, rawValue.RawValue, 8);
        Assert.Equal("unknown", raw.RawTimestampBasis);
        Assert.False(raw.AuthorizesFormalProfile);
        Assert.False(evidence.EvidenceReady);
        Assert.Null(new PrtgTrustedSamplingBindingStore(_backend).Get(43200));
        Assert.Empty(_backend.PrtgStore().GetTrustedSamplingProfiles([43200]));
        var pingPeriod = new PrtgResourcePeriodConsumer(_backend, _settings).EvaluateBatch(
            [43200], DateTime.UtcNow, DateTime.UtcNow);
        Assert.Empty(pingPeriod.Assessments);
        Assert.Empty(pingPeriod.FormalFindings);
        Assert.Empty(pingPeriod.QualifiedFormalFindings);
    }

    private sealed record Descriptor(long SensorId, long DeviceId, string SensorType,
        string ChannelCaption, string ChannelId, double ExactAvailableOrNativeValue,
        PrtgTrustedQuantitySemantic Semantic);

    private sealed class SourceHandler(Descriptor resource, DateTime measuredAtUtc) : HttpMessageHandler
    {
        private readonly DateTime _measuredAtUtc = DateTime.SpecifyKind(measuredAtUtc, DateTimeKind.Utc);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            string content;
            if (uri.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal))
                content = $"<prtg><result>{resource.ChannelId}</result></prtg>";
            else if (uri.AbsolutePath.EndsWith("historicdata.xml", StringComparison.Ordinal))
            {
                var oa = _measuredAtUtc.ToOADate().ToString("R", CultureInfo.InvariantCulture);
                content = $"<histdata totalcount=\"1\"><prtg-version>controlled-test-fixture</prtg-version><item><datetime_raw>{oa}</datetime_raw>" +
                    $"<value channel=\"{resource.ChannelCaption}\" channelid=\"{resource.ChannelId}\">{resource.ExactAvailableOrNativeValue.ToString("R", CultureInfo.InvariantCulture)} %</value>" +
                    $"<value_raw channel=\"{resource.ChannelCaption}\" channelid=\"{resource.ChannelId}\">{resource.ExactAvailableOrNativeValue.ToString("R", CultureInfo.InvariantCulture)}</value_raw></item></histdata>";
            }
            else if (uri.Query.Contains("content=sensors", StringComparison.Ordinal))
            {
                var sensor = new
                {
                    objid = resource.SensorId,
                    parentid = resource.DeviceId,
                    type = resource.SensorType,
                    sensor = resource.SensorType,
                    cumsince_raw = $"fixture-created-{resource.SensorId}",
                    status_raw = 3,
                    lastvalue_raw = resource.ExactAvailableOrNativeValue,
                    lastcheck_raw = _measuredAtUtc.ToOADate(),
                    interval_raw = 60,
                    raw_timestamp_timezone_id = "UTC"
                };
                content = JsonSerializer.Serialize(new { sensors = new[] { sensor } });
            }
            else
            {
                var channel = new
                {
                    objid = long.Parse(resource.ChannelId, CultureInfo.InvariantCulture),
                    name = resource.ChannelCaption,
                    unit = "%",
                    lastvalue_raw = resource.ExactAvailableOrNativeValue,
                    isprimary = true,
                    quantity = resource.Semantic.ToString(),
                    scale = 1d,
                    direction = "direct",
                    lastcheck_raw = _measuredAtUtc.ToOADate(),
                    interval_unit = "seconds",
                    semantic_version = "controlled-source-fixture-v1"
                };
                content = JsonSerializer.Serialize(new { channels = new[] { channel } });
            }
            var mediaType = uri.AbsolutePath.EndsWith(".xml", StringComparison.Ordinal) ? "application/xml" : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, mediaType) });
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value)
        {
            if (value.Offset != TimeSpan.Zero) throw new ArgumentException("The fixture clock must be UTC.", nameof(value));
            if (value < _utcNow) throw new ArgumentOutOfRangeException(nameof(value), "The fixture clock cannot move backwards.");
            _utcNow = value;
        }
    }

    private sealed class NoopProbeConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
