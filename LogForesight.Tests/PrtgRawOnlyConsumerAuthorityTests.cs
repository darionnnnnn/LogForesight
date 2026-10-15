using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgRawOnlyConsumerAuthorityTests
{
    [Fact]
    public void SelectedSensorFingerprintIgnoresMessageFormatAndTracksRawAuthorityAndScope()
    {
        var captured = Policy(rawRevision: 0);
        var dateFormatOnly = Copy(captured, revision: "policy-revision-after-format",
            messageDateFormat: PrtgMessageDateTimeParser.Ymd24, messageRevision: 1);
        Assert.Equal(
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(captured, [1], [10]),
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(dateFormatOnly, [1], [10]));

        var rawAba = Copy(captured, revision: "policy-revision-after-aba", rawRevision: 2);
        Assert.NotEqual(
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(captured, [1], [10]),
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(rawAba, [1], [10]));
        Assert.NotEqual(
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(captured, [1], [10]),
            PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(captured, [2], [10]));
    }

    private static PrtgMonitoringPolicy Policy(long rawRevision) => new()
    {
        Revision = "policy-revision-before-format",
        CoreSystemId = "core-fixture",
        SourceGeneration = "source-fixture",
        EndpointHint = "endpoint-fixture",
        ValidFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        HostIds = [1],
        SensorIds = [10],
        SourceTimeZoneId = "UTC",
        SourceCultureName = "en-US",
        MessageDateFormat = PrtgMessageDateTimeParser.Ymd12,
        RawAuthorityRevision = rawRevision,
        RawTimestampTimeZoneId = "UTC",
        AnalysisTimeZoneId = "UTC",
        TimeBasisEvidenceReference = "raw-authority-fixture"
    };

    private static PrtgMonitoringPolicy Copy(PrtgMonitoringPolicy source,
        string? revision = null, string? messageDateFormat = null, long? messageRevision = null,
        long? rawRevision = null) => new()
    {
        Revision = revision ?? source.Revision,
        CoreSystemId = source.CoreSystemId,
        SourceGeneration = source.SourceGeneration,
        EndpointHint = source.EndpointHint,
        ValidFrom = source.ValidFrom,
        HostIds = source.HostIds.ToList(),
        SensorIds = source.SensorIds.ToList(),
        SourceTimeZoneId = source.SourceTimeZoneId,
        SourceCultureName = source.SourceCultureName,
        MessageDateFormat = messageDateFormat ?? source.MessageDateFormat,
        MessageTimeAuthorityRevision = messageRevision ?? source.MessageTimeAuthorityRevision,
        RawAuthorityRevision = rawRevision ?? source.RawAuthorityRevision,
        RawTimestampTimeZoneId = source.RawTimestampTimeZoneId,
        AnalysisTimeZoneId = source.AnalysisTimeZoneId,
        TimeBasisEvidenceReference = source.TimeBasisEvidenceReference
    };
}

[Collection("KnownIssueCatalogState")]
public sealed class PrtgRawOnlyClosedDayAuthorityTests : IDisposable
{
    private long HostId = 8103;
    private const long DeviceId = 8101;
    private const long SensorId = 8102;
    private static readonly DateTime CompletedDay = new(2026, 9, 30);
    private const string SyntheticUrl = "http://raw-authority-fixture.invalid";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-raw-only-period-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;

    public PrtgRawOnlyClosedDayAuthorityTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "period.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        SetDiskRules();
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ClosedDayConsumerAllowsDateFormatChangeAtItsFinalAuthorityFence()
    {
        var policy = SeedSmallRawAuthorityDay();
        var consumer = CreateConsumer(() => policy.Update(value =>
        {
            value.Revision = "date-format-only-revision";
            value.MessageDateFormat = PrtgMessageDateTimeParser.Ymd24;
            value.MessageTimeAuthorityRevision = checked(value.MessageTimeAuthorityRevision + 1);
        }));

        var result = consumer.EvaluateClosedHostDayBatch([SensorId], CompletedDay,
            DateTime.UtcNow, evaluationHostIds: [HostId]);

        var closed = Assert.IsType<PrtgResourceClosedDayMetadata>(result.ClosedDay);
        Assert.True(!closed.RejectedSensorObjids.Contains(SensorId), Describe(result, closed));
        Assert.True(closed.ExpectedWindowCountsBySensor.GetValueOrDefault(SensorId) > 0, Describe(result, closed));
        Assert.Equal(closed.ExpectedWindowCountsBySensor[SensorId],
            closed.EvaluatedWindowCountsBySensor.GetValueOrDefault(SensorId));
    }

    [Fact]
    public void ClosedDayConsumerRejectsRawAuthorityAbaAtItsFinalAuthorityFence()
    {
        var policy = SeedSmallRawAuthorityDay();
        Assert.Equal("UTC", policy.Get().RawTimestampTimeZoneId);
        var consumer = CreateConsumer(() =>
        {
            policy.Update(value => value.RawTimestampTimeZoneId = "Pacific Standard Time");
            policy.Update(value => value.RawTimestampTimeZoneId = "UTC");
        });

        var result = consumer.EvaluateClosedHostDayBatch([SensorId], CompletedDay,
            DateTime.UtcNow, evaluationHostIds: [HostId]);

        var closed = Assert.IsType<PrtgResourceClosedDayMetadata>(result.ClosedDay);
        Assert.Contains(SensorId, closed.RejectedSensorObjids);
        Assert.DoesNotContain(result.QualifiedFormalFindings, finding =>
            finding.Finding.SensorObjid == SensorId);
    }

    private PrtgResourcePeriodConsumer CreateConsumer(Action beforeAuthorityRecheck) =>
        new(_backend, new SystemSettingsStore(_backend.Blob("system_settings")))
        { BeforeClosedDayAuthorityRecheckForTesting = beforeAuthorityRecheck };

    private PrtgMonitoringPolicyStore SeedSmallRawAuthorityDay()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.PrtgEnabled = true;
            value.PrtgUrl = SyntheticUrl;
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        HostId = _hosts.Upsert(new WebHost
        {
            HostId = HostId, HostName = "RAW-AUTHORITY-HOST", Source = "netiq", Active = true,
            IpAddress = "192.0.2.81"
        }).HostId;
        var store = _backend.PrtgStore();
        store.UpsertDevices([new PrtgDeviceRow { Objid = DeviceId, Name = "RAW-AUTHORITY-DEVICE" }], DateTime.UtcNow);
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = SensorId, DeviceObjid = DeviceId, Name = "Disk C: free", SensorType = "SNMP Disk Free",
            Category = PrtgSensorCategories.Disk, Status = "Up"
        }], DateTime.UtcNow);
        store.ReplaceHostMapForDate(CompletedDay, [new PrtgHostMapRow
        {
            DeviceObjid = DeviceId, MapDate = CompletedDay, HostId = HostId,
            HostName = "RAW-AUTHORITY-HOST", MapStatus = PrtgMapStatus.Ok
        }]);

        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value =>
        {
            value.Revision = "raw-authority-fixture";
            value.CoreSystemId = "core-raw-authority-fixture";
            value.SourceGeneration = "raw-authority-source";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(SyntheticUrl);
            value.ValidFrom = new DateTimeOffset(CompletedDay.AddDays(-40), TimeSpan.Zero);
            value.HostIds = [HostId];
            value.SensorIds = [SensorId];
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "raw-authority-test-evidence";
        });

        var hostDayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(CompletedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(policy.Get().AnalysisTimeZoneId);
        var analysisDayStart = TimeZoneInfo.ConvertTimeFromUtc(hostDayStartUtc, analysisZone);
        var firstHourUtc = DateTime.SpecifyKind(
            DateTime.SpecifyKind(analysisDayStart, DateTimeKind.Unspecified).AddHours(-1), DateTimeKind.Utc);
        var effectiveHourUtc = firstHourUtc.AddDays(-2);
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _backend.Blob(PrtgMonitoringPolicyStore.BlobKey),
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey),
            SensorId, DeviceId, HostId, "SNMP Disk Free", effectiveHourUtc,
            sourceGeneration: "raw-authority-source");
        store.MergeSampledValues(Enumerable.Range(0, 25).Select(offset =>
            PrtgResourceFixture.TrustedDiskHour(SensorId, firstHourUtc.AddHours(offset), profile, 5)).ToArray());
        return policy;
    }

    private static string Describe(PrtgResourcePeriodConsumption result, PrtgResourceClosedDayMetadata closed)
    {
        var assessment = result.Assessments.SingleOrDefault(item => item.SensorObjid == SensorId);
        return $"Rejected={string.Join(',', closed.RejectedSensorObjids)}; " +
            $"expected={closed.ExpectedWindowCountsBySensor.GetValueOrDefault(SensorId)}; " +
            $"evaluated={closed.EvaluatedWindowCountsBySensor.GetValueOrDefault(SensorId)}; " +
            $"decision={assessment?.Decision.Kind}/{assessment?.Decision.ReasonCode}";
    }

    private void SetDiskRules()
    {
        var rules = KnownIssueSeed.CreateRules()
            .Where(rule => rule.Id is "builtin-prtg-disk-free-trend" or "builtin-prtg-resource-disk-pressure")
            .Select(rule => rule.CloneForSeedOverwrite(enabled: true)).ToList();
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }
}
