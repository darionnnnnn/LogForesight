using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskAssessmentServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Theory]
    [InlineData(35, 100)]
    [InlineData(730, 5)]
    public void EffectiveBatchSizeBoundsExpectedHistoricalPoints(int recentWindowDays, int expected)
    {
        var rule = new KnownIssueRule
        {
            PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional with { RecentWindowDays = recentWindowDays }
        };

        Assert.Equal(expected, PrtgDiskAssessmentService.EffectiveBatchSize(rule));
    }

    [Fact]
    public void EnableGuard沒有語意證據時立即回傳且不掃描一般sensor母體()
    {
        var evidence = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var assessment = new PrtgDiskAssessmentService(new EfPrtgStore(_fx.NewContext), new FakeHostStore(),
            new FakeSystemSettingsStore(), evidence,
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));

        Assert.False(assessment.HasAnyReadySemanticCandidate(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), null));
    }

    [Fact]
    public void EnableGuard只查有語意證據的sensor並以目前mapping判定()
    {
        var evidence = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        evidence.ConfirmManually(new PrtgDiskSemanticContext(401, 301, 7, "snmpdiskfree", "free", "Free", "%", 1,
            "descending-danger"), 9, "Manually confirmed percent free channel.", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "active", Active = true });
        var assessment = new PrtgDiskAssessmentService(new EfPrtgStore(_fx.NewContext), hosts,
            new FakeSystemSettingsStore(), evidence,
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));

        Assert.False(assessment.HasAnyReadySemanticCandidate(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), null));
    }

    [Fact]
    public void EnableGuardFindsReadySemanticCandidateAfterFirstHundredEvidenceIds()
    {
        var completedDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var today = DateTime.Today;
        const long deviceId = 2101, sensorId = 1101;
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = deviceId,
                Category = PrtgSensorCategories.Disk, SensorType = "SNMP Disk Free", Name = "Disk C:" });
            var values = new List<PrtgValueRow>();
            var maps = new List<PrtgHostMapRow>();
            for (var offset = -27; offset <= 0; offset++)
            {
                var date = completedDay.ToDateTime(TimeOnly.MinValue).AddDays(offset);
                maps.Add(Map(deviceId, date, 1));
                for (var hour = 0; hour < 12; hour++)
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = date.AddHours(hour),
                        AvgValue = 75, MinValue = 75, MaxValue = 75, Coverage = 100,
                        Quality = PrtgDataQuality.Ok, CreatedAt = today });
            }
            db.PrtgHostMaps.AddRange(maps);
            db.PrtgValues.AddRange(values);
            db.SaveChanges();
        }
        var evidence = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        for (long id = 1001; id <= sensorId; id++)
            evidence.ConfirmManually(new PrtgDiskSemanticContext(id, id == sensorId ? deviceId : 3000 + id, 1,
                "SNMP Disk Free", "free", "Free", "%", 1,
                "descending-danger"), 9, "Manually confirmed percent free channel.", DateTime.UtcNow,
                PrtgDiskAssessmentService.ParserSemanticVersion);
        var verifications = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        verifications.Save(new PrtgDiskVerificationResult(sensorId, deviceId, 1, "SNMP Disk Free", "Verified",
            "Typed channel values matched.", "free", "Free", "%", 1, "descending-danger", 1, true,
            DateTime.UtcNow, today.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion));
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "active", Active = true });
        var assessment = new PrtgDiskAssessmentService(new EfPrtgStore(_fx.NewContext), hosts,
            new FakeSystemSettingsStore(), evidence, verifications);

        var longWindowRule = new KnownIssueRule
        {
            PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional with { RecentWindowDays = 730 }
        };
        Assert.True(assessment.HasAnyReadySemanticCandidate(completedDay, longWindowRule));
    }

    [Fact]
    public void CandidateLookupUsesNewestSuccessfulMappingBeforeTodayAndHonorsBatchLimit()
    {
        var today = new DateTime(2026, 9, 24);
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 3; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 100 + i });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 200 + i, DeviceObjid = 100 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree", Name = $"disk-{i}" });
            }
            db.PrtgHostMaps.AddRange(
                Map(101, today.AddDays(-1), 7), // newest successful mapping in lookback
                Map(101, today.AddDays(-2), 8),
                Map(102, today.AddDays(-29), 7), // outside bounded lookback
                Map(103, today.AddDays(-2), 7),
                Map(103, today.AddDays(-1), 7, PrtgMapStatus.Conflict)); // newer conflict invalidates old success
            db.SaveChanges();
        }

        var store = new EfPrtgStore(_fx.NewContext);
        var (total, batch) = store.GetLatestMappedReadinessSensors(new long[] { 7 }, today, today.AddDays(-28), 1);
        Assert.Equal(1, total);
        var selected = Assert.Single(batch);
        Assert.Equal(201L, selected.Objid);
        Assert.Equal(7L, selected.HostId);
        Assert.Empty(store.GetLatestMappedReadinessSensors(Array.Empty<long>(), today, today.AddDays(-28), 100).Rows);

        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 900 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 901, DeviceObjid = 900,
                Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree" });
            db.PrtgHostMaps.AddRange(Map(900, today.AddDays(-2), 7), Map(900, today.AddDays(-1), 7, PrtgMapStatus.Conflict));
            db.SaveChanges();
        }
        var afterConflict = store.GetLatestMappedReadinessSensors(new long[] { 7 }, today, today.AddDays(-28), 100);
        Assert.DoesNotContain(afterConflict.Rows, row => row.Objid == 901);
    }

    [Fact]
    public void AssessmentCandidatePageIncludesMappingAtDay29LikeReadinessPage()
    {
        var today = DateTime.Today;
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 8801 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 8802, DeviceObjid = 8801,
                Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree", Name = "disk-day-29" });
            db.PrtgHostMaps.Add(Map(8801, today.AddDays(-29), 1));
            db.SaveChanges();
        }
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "active-71", Active = true });
        var evidence = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var assessment = new PrtgDiskAssessmentService(new EfPrtgStore(_fx.NewContext), hosts,
            new FakeSystemSettingsStore(), evidence,
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));

        var page = assessment.Assess(DateOnly.FromDateTime(today.AddDays(-1)), null);

        Assert.Equal(1, page.CandidateCount);
        Assert.Equal(8802L, Assert.Single(page.Rows).SensorObjid);
    }

    [Fact]
    public void CandidatePagesUseStableSensorIdOrderingAndOffset()
    {
        var today = new DateTime(2026, 9, 24);
        using (var db = _fx.NewContext())
        {
            for (var i = 0; i < 3; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 500 + i });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 600 + i, DeviceObjid = 500 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree" });
                db.PrtgHostMaps.Add(Map(500 + i, today.AddDays(-1), 7));
            }
            db.SaveChanges();
        }
        var store = new EfPrtgStore(_fx.NewContext);
        var page1 = store.GetLatestMappedReadinessSensors(new long[] { 7 }, today, today.AddDays(-28), 2, 0);
        var page2 = store.GetLatestMappedReadinessSensors(new long[] { 7 }, today, today.AddDays(-28), 2, 2);
        Assert.Equal(3, page1.Total);
        Assert.Equal(new long[] { 600, 601 }, page1.Rows.Select(x => x.Objid));
        Assert.Equal(new long[] { 602 }, page2.Rows.Select(x => x.Objid));
    }

    [Fact]
    public void CandidateQueryHostScopeExcludesUnselectedHosts()
    {
        var today = new DateTime(2026, 9, 24);
        using (var db = _fx.NewContext())
        {
            for (var i = 0; i < 2; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 700 + i });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 800 + i, DeviceObjid = 700 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree" });
                db.PrtgHostMaps.Add(Map(700 + i, today.AddDays(-1), 70 + i));
            }
            db.SaveChanges();
        }
        var store = new EfPrtgStore(_fx.NewContext);
        var scoped = store.GetLatestMappedReadinessSensors(new long[] { 70 }, today, today.AddDays(-28), 100);
        Assert.Equal(1, scoped.Total);
        Assert.Equal(800L, Assert.Single(scoped.Rows).Objid);
        Assert.DoesNotContain(scoped.Rows, row => row.HostId == 71);
    }

    [Fact]
    public void PointLookupFiltersSensorAndCurrentActiveHostMapInSql()
    {
        var today = new DateTime(2026, 9, 24);
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 3; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 300 + i });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 400 + i, DeviceObjid = 300 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree", Name = $"disk-{i}" });
                db.PrtgHostMaps.Add(Map(300 + i, i == 1 ? today.AddDays(-1) : today, i == 1 ? 7 : 8));
            }
            db.PrtgHostMaps.Add(Map(301, today, 7, PrtgMapStatus.Conflict));
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 304 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 404, DeviceObjid = 304,
                Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree" });
            db.PrtgHostMaps.Add(Map(304, today.AddDays(-1), 7));
            db.SaveChanges();
        }

        var store = new EfPrtgStore(_fx.NewContext);
        var found = store.GetCurrentReadinessSensorById(401, new long[] { 7 }, today, today.AddDays(-28));
        Assert.Null(found); // newest conflicting row blocks fallback to a previous Ok mapping
        Assert.Equal(404L, store.GetCurrentReadinessSensorById(404, new long[] { 7 }, today, today.AddDays(-28))?.Objid);
        Assert.Null(store.GetCurrentReadinessSensorById(402, new long[] { 7 }, today, today.AddDays(-28)));
        Assert.Null(store.GetCurrentReadinessSensorById(403, new long[] { 7 }, today.AddDays(-1), today.AddDays(-28)));
        Assert.Null(store.GetCurrentReadinessSensorById(499, new long[] { 7 }, today, today.AddDays(-28)));
    }

    [Fact]
    public void ManualPercentEvidenceQualifiesWithSuccessfulTypedProbeEvenWhenProbeOmitsSemantics()
    {
        var evidence = ManualEvidence();
        var sensor = SensorTuple();
        var probe = Probe(status: "NeedsManualReview");

        Assert.True(PrtgDiskAssessmentService.MatchesVerifiedPercent(probe, evidence, sensor));
        Assert.False(PrtgDiskAssessmentService.MatchesVerifiedPercent(null, evidence, sensor));
    }

    [Theory]
    [InlineData("Free Capacity")]
    [InlineData("Available Capacity")]
    public void CapacityChannelLabelsAreAcceptedByCoreSemanticAssessment(string channelName)
    {
        var evidence = ManualEvidence() with { MainChannelName = channelName };

        Assert.True(PrtgDiskAssessmentService.MatchesVerifiedPercent(Probe("NeedsManualReview"), evidence, SensorTuple()));
    }

    [Theory]
    [InlineData("ChannelName", "CPU utilization")]
    [InlineData("Unit", "bytes")]
    [InlineData("Direction", "ascending-danger")]
    public void ManualPercentEvidenceRejectsContradictoryReportedProbeMetadata(string field, string value)
    {
        var evidence = ManualEvidence();
        var probe = Probe(status: "NeedsManualReview") with
        {
            ChannelName = field == "ChannelName" ? value : "Free",
            Unit = field == "Unit" ? value : "%",
            Direction = field == "Direction" ? value : "descending-danger"
        };

        Assert.False(PrtgDiskAssessmentService.MatchesVerifiedPercent(probe, evidence, SensorTuple()));
    }

    [Fact]
    public void ManualPercentEvidenceRejectsContradictoryCurrentMirrorUnit()
    {
        Assert.False(PrtgDiskAssessmentService.MatchesVerifiedPercent(Probe("NeedsManualReview"), ManualEvidence(),
            SensorTuple() with { Unit = "bytes" }));
    }

    private static PrtgDiskSemanticEvidence ManualEvidence() => new(
        401, 301, 7, "snmpdiskfree", "free", "Free", "%", 1, "descending-danger",
        PrtgDiskSemanticEvidenceSource.Manual, 9, "Manually confirmed percent free channel.",
        new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc), PrtgDiskAssessmentService.ParserSemanticVersion);

    private static PrtgDiskVerificationResult Probe(string status) => new(
        401, 301, 7, "snmpdiskfree", status, "Typed values match stored values.", null, null,
        null, null, null, 1, true, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 23), PrtgDiskAssessmentService.ParserSemanticVersion);

    private static (long Objid, long DeviceObjid, long HostId, string Name, string SensorType,
        string Category, string? Unit, bool Paused, bool DevicePaused) SensorTuple() =>
        (401, 301, 7, "Disk C:", "snmpdiskfree", PrtgSensorCategories.Disk, null, false, false);

    private static PrtgHostMapRow Map(long device, DateTime date, long host, string status = PrtgMapStatus.Ok) =>
        new() { DeviceObjid = device, MapDate = date.Date, HostId = host, MapStatus = status };
}
