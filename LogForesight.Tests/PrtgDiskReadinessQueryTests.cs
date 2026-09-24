using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskReadinessQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly DateTime _asOf = new(2026, 9, 24);
    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void LatestMappingCandidatesIncludeYesterdayOkAndExcludeNewConflictOrInactiveHost()
    {
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 4; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 100 + i });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 200 + i, DeviceObjid = 100 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = $"disk-{i}" });
            }
                db.PrtgHostMaps.AddRange(
                Map(101, _asOf.AddDays(-1), 7),
                Map(102, _asOf.AddDays(-1), 7), Map(102, _asOf, 7, PrtgMapStatus.Conflict),
                Map(103, _asOf.AddDays(-1), 8),
                Map(104, _asOf.AddDays(-29), 7));
            db.SaveChanges();
        }

        var store = new EfPrtgStore(_fx.NewContext);
        var hosts = new FakeHostStore(new WebHost { HostId = 7, HostName = "active", DisplayName = "Current Name", Active = true },
            new WebHost { HostId = 8, HostName = "inactive", Active = false });
        var service = Service(store, hosts);
        var result = service.Get(1, 1, _asOf.AddHours(12));
        Assert.Equal(2, result.CandidateSensors);
        Assert.Equal(201, Assert.Single(result.Rows).SensorObjid);
        Assert.Equal(new[] { "Current Name" }, result.Rows[0].MappedHosts);
        var second = service.Get(2, 1, _asOf.AddHours(12));
        Assert.Equal(204, Assert.Single(second.Rows).SensorObjid);
        Assert.Equal(new[] { "Current Name" }, second.Rows[0].MappedHosts);
        Assert.Equal(2, service.Get(1, 100, _asOf).CandidateSensors);
    }

    [Fact]
    public void ThirtyDayCandidateWindowKeepsPagedRowsAlignedWithSharedAssessor()
    {
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 110 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 210, DeviceObjid = 110,
                Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "older-map" });
            db.PrtgHostMaps.Add(Map(110, _asOf.AddDays(-29), 7));
            db.SaveChanges();
        }

        var result = Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7)).Get(1, 1, _asOf);
        var row = Assert.Single(result.Rows);

        Assert.Equal(1, result.CandidateSensors);
        Assert.Equal(210, row.SensorObjid);
        Assert.Equal(PrtgValueReadinessStatus.InsufficientData.ToString(), row.Status);
        Assert.NotEqual("無法取得共用評估結果", row.Reason);
    }

    [Fact]
    public void MatchingPersistedEvidenceAndTypedVerificationMakeReadyRowPreviewReadyWithoutExposingEvidence()
    {
        SeedOneDiskWithHistory(7, 28);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        var context = SemanticContext(7);
        evidenceStore.ConfirmManually(context, 3, "管理者確認的可用百分比頻道", _asOf.AddDays(-2).ToUniversalTime(), PrtgDiskAssessmentService.ParserSemanticVersion);
        verificationStore.Save(VerifiedResult(7));

        var row = Assert.Single(Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7), evidenceStore, verificationStore).Get(1, 20, _asOf).Rows);

        Assert.True(row.SemanticVerified);
        Assert.Equal("可試算", row.SemanticLabel);
        Assert.Equal(28, row.UsableDays);
        var json = JsonSerializer.Serialize(row);
        Assert.DoesNotContain("管理者確認", json);
        Assert.DoesNotContain("free space", json);
    }

    [Fact]
    public void ChangedCurrentHostMappingInvalidatesPreviouslyConfirmedEvidence()
    {
        SeedOneDiskWithHistory(8, 6);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(), PrtgDiskAssessmentService.ParserSemanticVersion);
        verificationStore.Save(VerifiedResult(7));

        var row = Assert.Single(Service(new EfPrtgStore(_fx.NewContext), ActiveHost(8), evidenceStore, verificationStore).Get(1, 20, _asOf).Rows);

        Assert.False(row.SemanticVerified);
        Assert.Equal("已失效", row.SemanticLabel);
        Assert.Contains("對應已變更", row.Reason);
    }

    [Fact]
    public void OldParserEvidenceIsInvalidatedAndSixDaysAreNeverReportedAsTwentyEightDayReady()
    {
        SeedOneDiskWithHistory(7, 6);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(), "old-parser");
        verificationStore.Save(VerifiedResult(7));

        var row = Assert.Single(Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7), evidenceStore, verificationStore).Get(1, 20, _asOf).Rows);

        Assert.False(row.SemanticVerified);
        Assert.Equal("已失效", row.SemanticLabel);
        Assert.Equal(6, row.UsableDays);
        Assert.Equal(28, row.RequiredDays);
        Assert.Equal(PrtgValueReadinessStatus.InsufficientData.ToString(), row.Status);
    }

    [Fact]
    public void SixDaysCanHaveVerifiedSemanticsButAreNotSemanticReadyForPreview()
    {
        SeedOneDiskWithHistory(7, 6);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(), PrtgDiskAssessmentService.ParserSemanticVersion);
        verificationStore.Save(VerifiedResult(7));

        var row = Assert.Single(Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7), evidenceStore, verificationStore).Get(1, 20, _asOf).Rows);

        Assert.True(row.SemanticVerified);
        Assert.False(row.SemanticReady);
        Assert.Equal("資料累積中", row.SemanticLabel);
        Assert.Equal(6, row.UsableDays);
        Assert.Equal(28, row.RequiredDays);
    }

    [Fact]
    public void GlobalSummaryIsStableAcrossPagesAndCountsLatestConflictsUnmappedAndPaused()
    {
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 5; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 300 + i, Paused = i == 4 });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 400 + i, DeviceObjid = 300 + i,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = $"disk-{i}", Paused = i == 5 });
            }
            db.PrtgHostMaps.AddRange(Map(301, _asOf.AddDays(-1), 7), Map(302, _asOf.AddDays(-1), 7),
                Map(302, _asOf, 7, PrtgMapStatus.Conflict), Map(304, _asOf.AddDays(-1), 7), Map(305, _asOf.AddDays(-1), 8));
            db.SaveChanges();
        }

        var hosts = new FakeHostStore(new WebHost { HostId = 7, HostName = "active", Active = true },
            new WebHost { HostId = 8, HostName = "disabled", Active = false });
        var service = Service(new EfPrtgStore(_fx.NewContext), hosts);
        var first = service.Get(1, 1, _asOf);
        var second = service.Get(2, 1, _asOf);
        Assert.Equal(5, first.GlobalMirrorCandidates);
        Assert.Equal(2, first.GloballyMappedActive);
        Assert.Equal(5, first.GloballyWhitelisted);
        Assert.Equal(2, first.GloballyPaused);
        Assert.Equal(1, first.GloballyConflicted);
        Assert.Equal(1, first.GloballyUnmapped);
        Assert.Equal(1, first.GloballyDisabledHost);
        Assert.True(first.ReadinessSummaryComputed);
        Assert.False(second.ReadinessSummaryComputed);
        Assert.Equal(0, second.ReadinessSummaryCandidateCount);
        Assert.Equal(2, first.CandidateSensors);
    }

    [Fact]
    public void HundredRowsSerializeTwentyEightCompactMasksPerSensorInsteadOfHourlyTimestamps()
    {
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 101; i++)
            {
                var device = 1000 + i;
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = device });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 2000 + i, DeviceObjid = device,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = $"disk-{i}" });
                db.PrtgHostMaps.Add(Map(device, _asOf.AddDays(-1), 7));
            }
            db.SaveChanges();
        }

        var page = Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7)).Get(1, 100, _asOf);
        var payload = JsonSerializer.Serialize(page);

        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(101, page.CandidateSensors);
        Assert.Equal(100, page.ReadinessSummaryCandidateCount);
        Assert.True(page.ReadinessSummaryCapped);
        Assert.Equal(28, page.Rows[0].MissingHourMasks.Count);
        Assert.Equal(2800, page.Rows.Sum(x => x.MissingHourMasks.Count));
        Assert.DoesNotContain("missingHours", payload, StringComparison.OrdinalIgnoreCase);
        Assert.All(page.Rows, row => Assert.All(row.MissingHourMasks, mask => Assert.InRange(mask, 0u, 0x00ff_ffffu)));
    }

    private PrtgDiskReadinessQueryService Service(EfPrtgStore store, FakeHostStore hosts) =>
        Service(store, hosts, new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));

    private PrtgDiskReadinessQueryService Service(EfPrtgStore store, FakeHostStore hosts,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verification)
    {
        var settings = new SystemSettingsStore(_fx.Blob("system_settings"));
        settings.Update(x => x.PrtgSensorTypeWhitelist = new() { "disk" });
        return new(store, hosts, settings, evidence, verification);
    }

    private FakeHostStore ActiveHost(long id) => new(new WebHost { HostId = id, HostName = $"host-{id}", Active = true });

    private void SeedOneDiskWithHistory(long hostId, int days)
    {
        using var db = _fx.NewContext();
        db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 100 });
        db.PrtgSensors.Add(new PrtgSensorRow { Objid = 200, DeviceObjid = 100,
            Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "disk-test" });
        for (var day = 1; day <= days; day++)
        {
            var date = _asOf.AddDays(-day);
            db.PrtgHostMaps.Add(Map(100, date, hostId));
            for (var hour = 0; hour < 12; hour++)
                db.PrtgValues.Add(new PrtgValueRow { SensorObjid = 200, PeriodStart = date.AddHours(hour),
                    Quality = PrtgDataQuality.Ok, Coverage = 100, AvgValue = 50 });
        }
        db.SaveChanges();
    }

    private static PrtgDiskSemanticContext SemanticContext(long hostId) =>
        new(200, 100, hostId, "disk", "free", "Free Space", "%", 1, "descending-danger");

    private PrtgDiskVerificationResult VerifiedResult(long hostId) =>
        new(200, 100, hostId, "disk", "Verified", "typed result", "free", "Free Space", "%", 1,
            "descending-danger", 12, true, _asOf.ToUniversalTime(), _asOf.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion);

    private sealed class FakeHostStore(params WebHost[] hosts) : IHostStore
    {
        public List<WebHost> GetAll() => hosts.ToList();
        public long DataVersion => 1;
        public WebHost? Get(long hostId) => hosts.FirstOrDefault(x => x.HostId == hostId);
        public WebHost? FindByName(string hostName) => hosts.FirstOrDefault(x => x.HostName == hostName);
        public WebHost Upsert(WebHost host) => throw new NotSupportedException();
        public WebHost Touch(string hostName, DateTime reportedAt, string source = "local") => throw new NotSupportedException();
        public WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt) => throw new NotSupportedException();
        public void SetGroups(long hostId, IEnumerable<long> groupIds) => throw new NotSupportedException();
        public void SetHighVolume(long hostId, bool isHighVolume) => throw new NotSupportedException();
        public HostGroupsBatchResult SetGroupsBatch(IEnumerable<long> hostIds, IEnumerable<long> groupIds, bool replace) => throw new NotSupportedException();
        public void SetOwners(long hostId, IEnumerable<long> userIds) => throw new NotSupportedException();
        public void Merge(long sourceHostId, long targetHostId) => throw new NotSupportedException();
        public void Unmerge(long hostId) => throw new NotSupportedException();
        public TResult MutateBatch<TResult>(Func<List<WebHost>, TResult> mutation) => throw new NotSupportedException();
        public void MutateBatch(Action<List<WebHost>> mutation) => throw new NotSupportedException();
    }

    private sealed class FakeSettings : ISystemSettingsService
    {
        public LogForesight.Web.Models.Dto.SystemSettingsDto Get() => new() { PrtgSensorTypeWhitelist = new() { "disk" } };
        public LogForesight.Web.Models.Dto.SystemSettingsDto Update(LogForesight.Web.Models.Dto.UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public LogForesight.Web.Models.Dto.SystemSettingsDto UpdatePrtg(LogForesight.Web.Models.Dto.UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public LogForesight.Web.Models.Dto.TestAdConnectionResultDto TestAdConnection(LogForesight.Web.Models.Dto.TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<LogForesight.Web.Models.Dto.TestMailResultDto> TestMail(LogForesight.Web.Models.Dto.TestMailRequest request) => throw new NotSupportedException();
        public Task<LogForesight.Web.Models.Dto.TestPrtgConnectionResultDto> TestPrtgAsync(LogForesight.Web.Models.Dto.TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void SixDaysOfUsableValuesRemainSensorSpecificAndLowCoverageDoesNotCount()
    {
        var hoursA = Hours(28, PrtgDataQuality.Ok, null);
        var hoursB = Hours(6, PrtgDataQuality.Ok, null);
        var low = Hours(28, PrtgDataQuality.Sampled, 25);
        var a = Evaluate(1, hoursA);
        var b = Evaluate(2, hoursB);
        var lowCoverage = Evaluate(3, low);
        Assert.Equal((PrtgValueReadinessStatus.Ready, 28), (a.Status, a.UsableDays));
        Assert.Equal((PrtgValueReadinessStatus.InsufficientData, 6), (b.Status, b.UsableDays));
        Assert.Equal(0, lowCoverage.UsableDays);
    }

    [Fact]
    public void HistoricalMissingOrChangedMappingIsUnknownEvenWhenValuesExist()
    {
        var maps = Enumerable.Range(1, 28).ToDictionary(i => _asOf.AddDays(-i), _ => (long?)7);
        maps.Remove(_asOf.AddDays(-5));
        var missing = PrtgValueReadiness.Evaluate(Input(4, Hours(28, PrtgDataQuality.Ok, null), maps), _asOf);
        Assert.Equal(PrtgValueReadinessStatus.Unknown, missing.Status);
        maps[_asOf.AddDays(-5)] = 8;
        var changed = PrtgValueReadiness.Evaluate(Input(4, Hours(28, PrtgDataQuality.Ok, null), maps), _asOf);
        Assert.Equal(PrtgValueReadinessStatus.Unknown, changed.Status);
    }

    private PrtgValueReadinessResult Evaluate(long id, IEnumerable<PrtgReadinessHour> hours) =>
        PrtgValueReadiness.Evaluate(Input(id, hours, Enumerable.Range(1, 28)
            .ToDictionary(i => _asOf.AddDays(-i), _ => (long?)7)), _asOf);

    private PrtgValueReadinessInput Input(long id, IEnumerable<PrtgReadinessHour> hours,
        IReadOnlyDictionary<DateTime, long?> maps) => new(id, 100, PrtgSensorCategories.Disk,
        true, true, false, false, true, "Percent", null, false, hours.ToArray(), maps);

    private IEnumerable<PrtgReadinessHour> Hours(int days, string quality, double? coverage) =>
        Enumerable.Range(0, days).SelectMany(d => Enumerable.Range(0, 12).Select(h =>
            new PrtgReadinessHour(_asOf.AddDays(-days + d).AddHours(h), quality, coverage)));

    private static PrtgHostMapRow Map(long device, DateTime date, long host,
        string status = PrtgMapStatus.Ok) => new() { DeviceObjid = device, MapDate = date.Date,
            HostId = host, MapStatus = status };
}
