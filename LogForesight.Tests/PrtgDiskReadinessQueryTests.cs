using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskReadinessQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly DateTime _asOf = new(2026, 9, 24);
    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Theory]
    [InlineData(null)]
    [InlineData("DISK")]
    public void 空白或大小寫不同的白名單與數值取數語意一致(string? configuredType)
    {
        SeedOneDiskWithHistory(7, 28);
        var store = new EfPrtgStore(_fx.NewContext);
        var service = Service(store, ActiveHost(7));
        var settings = new SystemSettingsStore(_fx.Blob("system_settings"));
        settings.Update(x => x.PrtgSensorTypeWhitelist = configuredType is null ? new() : new() { configuredType });

        var result = service.Get(1, 20, _asOf);

        Assert.Equal(1, result.GloballyWhitelisted);
        Assert.Equal(28, Assert.Single(result.Rows).UsableDays);
    }

    [Theory]
    [InlineData(4, 28)]
    [InlineData(3, 28)]
    [InlineData(2, 0)]
    public void ActualSnapshotAccumulatorAndSqlFeedTrustedCompletedDayReadiness(int slotsPerHour, int expectedDays)
    {
        SeedOneDiskWithHistory(7, 28, slotsPerHour);
        var result = Service(new EfPrtgStore(_fx.NewContext), ActiveHost(7)).Get(1, 20, _asOf);
        Assert.Equal(expectedDays, Assert.Single(result.Rows).UsableDays);
        using var db = _fx.NewContext();
        Assert.Equal(336, db.PrtgValues.Count());
        Assert.All(db.PrtgValues.AsNoTracking().ToArray(), row =>
        {
            Assert.Equal(PrtgDataQuality.Sampled, row.Quality);
            Assert.Equal(slotsPerHour * 25d, row.Coverage);
            Assert.Equal(slotsPerHour, PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots.Count);
        });
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(1000000)]
    public void SqlProofPrefixCannotPromoteUtf8OversizeOrTruncatedProof(int characters)
    {
        SeedOneDiskWithHistory(7, 1);
        using (var db = _fx.NewContext())
        {
            var row = db.PrtgValues.OrderBy(v => v.PeriodStart).First();
            row.TrustedProof = new string('漢', characters);
            db.SaveChanges();
        }
        var validatorCalls = 0;
        var values = new EfPrtgStore(_fx.NewContext).GetTrustedReadinessValues([200], _asOf.AddDays(-1),
            _asOf, 24, _ => { validatorCalls++; return true; });
        Assert.False(values[0].Trusted);
        Assert.Equal(11, validatorCalls);
        Assert.Equal(12, values.Count);
    }

    [Fact]
    public void SQLServerProvider_準備度白名單查詢可翻譯()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var sql = EfPrtgStore.BuildReadinessInventoryQuery(ctx, new[] { 7L }, new[] { "DISK" },
            _asOf, _asOf.AddDays(-30)).ToQueryString();

        Assert.Contains("UPPER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk", sql, StringComparison.OrdinalIgnoreCase);
    }

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
        Assert.Equal(PrtgValueReadinessStatus.Unknown.ToString(), row.Status);
        Assert.True(row.Reason.StartsWith(
            "Unknown：可信採樣的 AnalysisTimeZoneId 缺失或無效，無法定位磁碟歷史主機日。", StringComparison.Ordinal), row.Reason);
    }

    [Fact]
    public void MatchingPersistedEvidenceAndTypedVerificationMakeReadyRowPreviewReadyWithoutExposingEvidence()
    {
        SeedOneDiskWithHistory(7, 28);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        var identity = BindDiskIdentity(200, 100, 7);
        var context = SemanticContext(7);
        evidenceStore.ConfirmManually(context, 3, "管理者確認的可用百分比頻道", _asOf.AddDays(-2).ToUniversalTime(),
            PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration, identity.Generation,
            identity.ChannelGeneration, identity.Epoch);
        verificationStore.Save(VerifiedResult(7, identity));

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
        SeedOneDiskWithHistory(7, 6);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        var identity = BindDiskIdentity(200, 100, 7);
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(),
            PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration, identity.Generation,
            identity.ChannelGeneration, identity.Epoch);
        verificationStore.Save(VerifiedResult(7, identity));
        new EfPrtgStore(_fx.NewContext).ReplaceHostMapForDate(_asOf,
            [Map(100, _asOf, 8)]);

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
        var identity = BindDiskIdentity(200, 100, 7);
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(), "old-parser",
            identity.SourceGeneration, identity.Generation, identity.ChannelGeneration, identity.Epoch);
        verificationStore.Save(VerifiedResult(7, identity));

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
        var identity = BindDiskIdentity(200, 100, 7);
        evidenceStore.ConfirmManually(SemanticContext(7), 3, "管理者確認摘要", _asOf.ToUniversalTime(),
            PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration, identity.Generation,
            identity.ChannelGeneration, identity.Epoch);
        verificationStore.Save(VerifiedResult(7, identity));

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

    [Fact]
    public void FirstPageTwentyRowsAndHundredRowSummaryUseOneCurrentAsOfOperationAndOneHostListRead()
    {
        using (var db = _fx.NewContext())
        {
            for (var i = 1; i <= 101; i++)
            {
                var device = 1000 + i;
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = device });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = 2000 + i, DeviceObjid = device,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = $"disk-{i}" });
                db.PrtgHostMaps.Add(Map(device, _asOf, 7));
            }
            db.SaveChanges();
        }
        var hosts = ActiveHost(7);
        var evidence = new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var verification = new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey));
        var identity = BindDiskIdentity(2001, 1001, 7);
        evidence.ConfirmManually(new PrtgDiskSemanticContext(2001, 1001, 7, "disk", "free", "Free Space", "%", 1,
            "descending-danger"), 3, "private positive fixture", _asOf.AddDays(-1).ToUniversalTime(),
            PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration, identity.Generation,
            identity.ChannelGeneration, identity.Epoch);
        verification.Save(new PrtgDiskVerificationResult(2001, 1001, 7, "disk", "Verified", "typed result",
            "free", "Free Space", "%", 1, "descending-danger", 1, true, _asOf.ToUniversalTime(),
            _asOf.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));
        var service = Service(new EfPrtgStore(_fx.NewContext), hosts, evidence, verification);
        hosts.ResetGetAllCount();

        var page = service.Get(1, 20, _asOf);

        Assert.Equal(101, page.CandidateSensors);
        Assert.Equal(20, page.Rows.Count);
        Assert.Equal(100, page.ReadinessSummaryCandidateCount);
        Assert.True(page.ReadinessSummaryCapped);
        Assert.True(page.ReadinessSummaryComputed);
        Assert.Equal(1, hosts.GetAllCount);
        Assert.True(page.Rows[0].SemanticVerified);
        Assert.DoesNotContain("private positive fixture", JsonSerializer.Serialize(page));
    }

    [Fact]
    public void ReadinessAndMissingHourMasksShareOneValueReadPerPage()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var probe = new ReadinessValueReadBoundaryProbe(connection);
        var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection)
            .AddInterceptors(probe).Options;
        using (var db = new LfDbContext(options))
        {
            db.Database.EnsureCreated();
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 6100, Name = "value-snapshot-device" });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 6200, DeviceObjid = 6100,
                Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "value-snapshot-disk" });
            for (var day = -30; day <= 0; day++)
                db.PrtgHostMaps.Add(Map(6100, _asOf.AddDays(day), 7));
            db.SaveChanges();
        }

        Func<LfDbContext> contextFactory = () => new LfDbContext(options);
        var store = new EfPrtgStore(contextFactory);
        probe.Enabled = false;
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            new EfJsonBlobStore(contextFactory, PrtgMonitoringPolicyStore.BlobKey),
            new EfJsonBlobStore(contextFactory, PrtgTrustedSamplingStrategyStateStore.BlobKey),
            6200, 6100, 7, "disk", DateTime.SpecifyKind(_asOf.Date.AddDays(-35), DateTimeKind.Utc));
        store.MergeSampledValues(Enumerable.Range(-28, 28).SelectMany(day => Enumerable.Range(0, 12)
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(6200,
                _asOf.AddDays(day).AddHours(hour), profile))).ToArray());
        probe.Enabled = true;

        var service = Service(store, ActiveHost(7));

        var row = Assert.Single(service.Get(1, 20, _asOf).Rows);

        Assert.Equal(2, probe.ValueSelectCount);
        Assert.True(probe.MutatedAtDetailsBoundary);
        Assert.Equal(PrtgValueReadiness.WindowDays * 12, probe.UpdatedRows);
        Assert.Equal(PrtgValueReadinessStatus.Ready.ToString(), row.Status);
        Assert.Equal(DateOnly.FromDateTime(_asOf.AddDays(-28)), row.MissingHourWindowStart);
        Assert.Equal(PrtgValueReadiness.WindowDays, row.MissingHourMasks.Count);
        // The fixture's UTC 00-11 hours are Taipei Local 08-19; Local 00-07 and 20-23 are missing.
        Assert.All(row.MissingHourMasks, mask => Assert.Equal(0x00f0_00ffu, mask));
    }

    public static IEnumerable<object[]> TrustedHistoryPositiveProofs() => new[]
    {
        new object[] { "full-hour" }, new object[] { "sampled-75-percent" },
        new object[] { "zero-percent" }, new object[] { "one-hundred-percent" },
        new object[] { "matching-variable-summary" }
    };

    [Theory]
    [MemberData(nameof(TrustedHistoryPositiveProofs))]
    public void CurrentProfileAcceptsOnlyConsistentTrustedHistorySummaries(string scenario)
    {
        var (row, resolution) = TrustedHistoryFixture();
        row = scenario switch
        {
            "sampled-75-percent" => ChangeProof(row, proof => proof with { Slots = proof.Slots.Take(3).ToArray() }) with
            { Quality = PrtgDataQuality.Sampled, Coverage = 75 },
            "zero-percent" => RewriteValues(row, 0, 0, 0),
            "one-hundred-percent" => RewriteValues(row, 100, 100, 100),
            "matching-variable-summary" => RewriteValues(row, 15, 0, 30, [0, 10, 20, 30]),
            _ => row
        };

        Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(row, resolution), scenario);
    }

    [Fact]
    public void TrustedDiskProofRequiresEveryMeasuredAndReceivedTimeAtOrBeforeUtcCutoff()
    {
        var (row, resolution) = TrustedHistoryFixture();
        var proof = PrtgTrustedSampleProof.Deserialize(row.ProofPrefix!);
        var cutoff = proof.Slots.Max(slot => slot.ReceivedAt);

        Assert.True(proof.IsStructurallyValid());
        Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(row, resolution, cutoff));

        var oneTickLate = ChangeProof(row, original => original with
        {
            Slots = original.Slots.Select(slot => slot.Slot == 3
                ? slot with { ReceivedAt = cutoff.AddTicks(1) }
                : slot).ToArray()
        });
        Assert.True(PrtgTrustedSampleProof.Deserialize(oneTickLate.ProofPrefix!).IsStructurallyValid());
        Assert.False(PrtgDiskTrustedProofValidator.IsTrusted(oneTickLate, resolution, cutoff));
        Assert.False(PrtgDiskTrustedProofValidator.IsTrusted(row, resolution,
            new DateTime(cutoff.Ticks, DateTimeKind.Unspecified)));
    }

    public static IEnumerable<object[]> TrustedHistoryNegativeProofs() => new[]
    {
        "legacy-version", "missing-proof", "malformed-json", "truncated-prefix", "oversized-proof",
        "wrong-sensor", "wrong-source", "wrong-resource", "wrong-channel", "wrong-epoch",
        "wrong-semantic", "wrong-strategy", "wrong-effective-hour", "wrong-interval",
        "wrong-raw-zone", "wrong-analysis-zone", "wrong-hour", "wrong-average", "wrong-minimum",
        "wrong-maximum", "wrong-coverage", "wrong-quality", "duplicate-slot", "duplicate-physical-id",
        "legacy-quality-only"
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(TrustedHistoryNegativeProofs))]
    public void StaleOrInconsistentTrustedHistoryNeverCounts(string scenario)
    {
        var (row, resolution) = TrustedHistoryFixture();
        row = scenario switch
        {
            "legacy-version" or "legacy-quality-only" => row with { TrustVersion = 0 },
            "missing-proof" => row with { ProofPrefix = null, ProofLength = 0 },
            "malformed-json" => row with { ProofPrefix = "{bad", ProofLength = 4 },
            "truncated-prefix" => row with { ProofPrefix = row.ProofPrefix![..^1] },
            "oversized-proof" => row with { ProofLength = PrtgTrustedSampleProof.MaximumSerializedBytes + 1 },
            "wrong-sensor" => row with { SensorObjid = row.SensorObjid + 1 },
            "wrong-hour" => row with { PeriodStart = row.PeriodStart.AddHours(1) },
            "wrong-average" => row with { AvgValue = row.AvgValue + 1 },
            "wrong-minimum" => row with { MinValue = row.MinValue + 1 },
            "wrong-maximum" => row with { MaxValue = row.MaxValue + 1 },
            "wrong-coverage" => row with { Coverage = 75 },
            "wrong-quality" => row with { Quality = PrtgDataQuality.NoData },
            "wrong-source" => ChangeProof(row, proof => proof with { SourceGeneration = "other-source" }),
            "wrong-resource" => ChangeProof(row, proof => proof with { ResourceGeneration = "other-resource" }),
            "wrong-channel" => ChangeProof(row, proof => proof with { ChannelGeneration = "other-channel" }),
            "wrong-epoch" => ChangeProof(row, proof => proof with { ResourceEpoch = "other-epoch" }),
            "wrong-semantic" => ChangeProof(row, proof => proof with { SemanticVersion = "old-semantic" }),
            "wrong-strategy" => ChangeProof(row, proof => proof with { StrategyVersion = "old-strategy" }),
            "wrong-effective-hour" => ChangeProof(row, proof => proof with
                { StrategyEffectiveFromHour = proof.StrategyEffectiveFromHour.AddHours(-1) }),
            "wrong-interval" => ChangeProof(row, proof => proof with
                { ConfirmedScanInterval = TimeSpan.FromMinutes(5) }),
            "wrong-raw-zone" => ChangeProof(row, proof => proof with { RawTimestampTimeZoneId = "Pacific Standard Time" }),
            "wrong-analysis-zone" => ChangeProof(row, proof => proof with { AnalysisTimeZoneId = "Pacific Standard Time" }),
            "duplicate-slot" => ChangeProof(row, proof => proof with
                { Slots = proof.Slots.Select((slot, index) => index == 1 ? slot with { Slot = 0 } : slot).ToArray() }),
            "duplicate-physical-id" => ChangeProof(row, proof => proof with
                { Slots = proof.Slots.Select((slot, index) => index == 1 ? slot with { PhysicalIdHash = proof.Slots[0].PhysicalIdHash } : slot).ToArray() }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown proof scenario.")
        };

        Assert.False(PrtgDiskTrustedProofValidator.IsTrusted(row, resolution), scenario);
    }

    [Fact]
    public void FreshSameContractProbeMetadataKeepsHistoricalProofCurrent()
    {
        var (row, firstResolution) = TrustedHistoryFixture();
        Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(row, firstResolution));
        var store = new EfPrtgStore(_fx.NewContext);
        var first = store.GetTrustedSamplingProfiles([200])[200];
        var refreshedAt = first.SourceMetadataObservedAtUtc.AddSeconds(2);
        var refreshed = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey), _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey),
            200, 100, 7, "disk", first.StrategyEffectiveFromHourUtc, refreshedAt);
        var policyContext = store.GetTrustedSamplingPolicyContext("conservative", 15);
        var current = PrtgTrustedSamplingProfileResolver.Resolve(refreshed,
            store.GetResourceIdentity(200), policyContext.Policy, 200, "disk", policyContext.Strategy,
            refreshedAt.UtcDateTime.AddSeconds(1), refreshedAt.UtcDateTime.AddSeconds(1));

        Assert.NotEqual(first.MetadataDigest, refreshed.MetadataDigest);
        Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(row, current));
    }

    private (PrtgDiskReadinessProofProjection Row, PrtgTrustedSamplingProfileResolution Resolution) TrustedHistoryFixture()
    {
        SeedOneDiskWithHistory(7, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        var profile = store.GetTrustedSamplingProfiles([200])[200];
        var identity = store.GetResourceIdentity(200);
        var authority = store.GetTrustedSamplingPolicyContext("conservative", 15);
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, authority.Policy,
            200, "disk", authority.Strategy, DateTime.UtcNow, DateTime.UtcNow);
        using var db = _fx.NewContext();
        var value = db.PrtgValues.AsNoTracking().OrderBy(v => v.PeriodStart).First();
        return (Project(value), resolution);
    }

    private static PrtgDiskReadinessProofProjection Project(PrtgValueRow row)
    {
        return new(row.Id, row.SensorObjid, row.PeriodStart, row.AvgValue, row.MinValue, row.MaxValue,
            row.Coverage, row.Quality, row.TrustVersion, row.TrustedProof,
            row.TrustedProof is null ? 0 : row.TrustedProof.Length);
    }

    private static PrtgDiskReadinessProofProjection ChangeProof(PrtgDiskReadinessProofProjection row,
        Func<PrtgTrustedSampleProof, PrtgTrustedSampleProof> change)
    {
        var proof = PrtgTrustedSampleProof.Deserialize(row.ProofPrefix!);
        var serialized = PrtgTrustedSampleProof.Serialize(change(proof));
        return row with { ProofPrefix = serialized, ProofLength = serialized.Length };
    }

    private static PrtgDiskReadinessProofProjection RewriteValues(PrtgDiskReadinessProofProjection row,
        double average, double minimum, double maximum, double[]? values = null)
    {
        var projection = ChangeProof(row, proof => proof with
        {
            Slots = proof.Slots.Select((slot, index) => slot with { Value = values?[index] ?? average }).ToArray()
        });
        return projection with { AvgValue = average, MinValue = minimum, MaxValue = maximum };
    }

    [Fact]
    public void RealHostSnapshotAllowsViewAllAggregateWithInactiveAndMergedRows()
    {
        var hosts = new HostStore(_fx.Blob("readiness-viewall-hosts"));
        var active = hosts.Upsert(new WebHost { HostName = "active-readiness-host", Active = true });
        hosts.Upsert(new WebHost { HostName = "inactive-readiness-host", Active = false });
        hosts.Upsert(new WebHost { HostName = "merged-readiness-host", Active = false, MergedInto = active.HostId });
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 4100, Name = "device" });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 4200, DeviceObjid = 4100,
                Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "disk" });
            db.PrtgHostMaps.Add(Map(4100, _asOf, active.HostId));
            db.SaveChanges();
        }
        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var visibility = new VisibilityService(currentUser, new FakeUserStore(), new FakeUserGroupStore(),
            new FakeGroupAccessStore(), hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        var service = Service(new EfPrtgStore(_fx.NewContext), hosts,
            new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)), visibility, currentUser);

        var page = service.Get(1, 20, _asOf);

        Assert.Equal(1, page.CandidateSensors);
        Assert.Equal(4200, Assert.Single(page.Rows).SensorObjid);
    }

    [Fact]
    public void RealVisibilityStillRejectsServerAdminSubsetAndCaseGrantOnlyGlobalReadiness()
    {
        var hostStore = new HostStore(_fx.Blob("readiness-restricted-hosts"));
        hostStore.Upsert(new WebHost { HostName = "group-host", GroupIds = [71] });
        hostStore.Upsert(new WebHost { HostName = "hidden-host" });
        _ = hostStore.Upsert(new WebHost { HostName = "case-only-host" });
        _ = hostStore.Upsert(new WebHost { HostName = "case-owner-host", OwnerUserIds = [22] });

        var admin = FakeCurrentUser.ServerAdmin();
        var adminVisibility = new VisibilityService(admin, new FakeUserStore(), new FakeUserGroupStore(),
            new FakeGroupAccessStore(), hostStore, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        Assert.Throws<DomainException>(() => Service(new EfPrtgStore(_fx.NewContext), hostStore,
            new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)),
            adminVisibility, admin).Get(1, 20, _asOf));

        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var group = groups.Upsert(new UserGroup { GroupName = "subset", Role = UserRole.User });
        var subsetUser = users.Upsert(new WebUser { Account = "subset-user", GroupIds = [group.GroupId] });
        access.ReplaceAll([new GroupAccess { UserGroupId = group.GroupId, HostGroupId = 71 }]);
        var subsetCurrent = FakeCurrentUser.ForUser(subsetUser.UserId);
        var subsetVisibility = new VisibilityService(subsetCurrent, users, groups, access, hostStore,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        Assert.Throws<DomainException>(() => Service(new EfPrtgStore(_fx.NewContext), hostStore,
            new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)),
            subsetVisibility, subsetCurrent).Get(1, 20, _asOf));

        var caseHosts = new HostStore(_fx.Blob("readiness-case-only-hosts"));
        var caseHost = caseHosts.Upsert(new WebHost { HostName = "case-only-host" });
        var ownerUsers = new FakeUserStore();
        var owner = ownerUsers.Upsert(new WebUser { Account = "case-user" });
        caseHosts.Upsert(new WebHost { HostName = "case-owner-host", OwnerUserIds = [owner.UserId] });
        var caseStore = new FakeIssueCaseStore();
        caseStore.Save(new IssueCase
        {
            CaseId = "case-grant-readiness", HostName = "case-only-host", IssueKey = "App|disk|42|1",
            HandlerId = owner.UserId, Status = IssueHandlingStatuses.InProgress
        });
        var caseCurrent = FakeCurrentUser.ForUser(owner.UserId);
        var caseVisibility = new VisibilityService(caseCurrent, ownerUsers, new FakeUserGroupStore(),
            new FakeGroupAccessStore(), caseHosts, caseStore, new FakeSystemSettingsStore());
        Assert.True(caseVisibility.IsCaseGrantOnly(caseHost.HostId));
        Assert.Throws<DomainException>(() => Service(new EfPrtgStore(_fx.NewContext), caseHosts,
            new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)),
            caseVisibility, caseCurrent).Get(1, 20, _asOf));
    }

    [Fact]
    public void GroupGrantRevokedAtCandidateSqlBoundaryFailsClosedWithSameHostSnapshot()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var baseOptions = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options;
        using (var db = new LfDbContext(baseOptions))
        {
            db.Database.EnsureCreated();
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 5100, Name = "device" });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 5200, DeviceObjid = 5100,
                Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "disk" });
            db.PrtgHostMaps.Add(Map(5100, _asOf, 1));
            db.SaveChanges();
        }

        var hosts = new FakeHostStore(new WebHost { HostId = 1, HostName = "scoped-host", Active = true, GroupIds = [71] });
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var group = groups.Upsert(new UserGroup { GroupName = "request-scope", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "request-user", GroupIds = [group.GroupId] });
        var access = new FakeGroupAccessStore();
        access.ReplaceAll([new GroupAccess { UserGroupId = group.GroupId, HostGroupId = 71 }]);
        var revoke = new RevokeGroupAccessAtCandidateCount(access);
        var currentUser = FakeCurrentUser.ForUser(user.UserId);
        var visibility = new VisibilityService(currentUser, users, groups, access, hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        var store = new EfPrtgStore(() => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(revoke).Options));
        var query = Service(store, hosts,
            new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)), visibility, currentUser);
        hosts.ResetGetAllCount();

        Assert.Throws<DomainException>(() => query.Get(1, 20, _asOf));

        Assert.True(revoke.Revoked);
        Assert.Equal(1, hosts.GetAllCount);
    }

    private PrtgDiskReadinessQueryService Service(EfPrtgStore store, FakeHostStore hosts) =>
        Service(store, hosts, new PrtgDiskSemanticEvidenceStore(_fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));

    private PrtgDiskReadinessQueryService Service(EfPrtgStore store, FakeHostStore hosts,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verification)
    {
        var settings = new SystemSettingsStore(_fx.Blob("system_settings"));
        settings.Update(x => x.PrtgSensorTypeWhitelist = new() { "disk" });
        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        return new(store, hosts, settings, evidence, verification,
            new FixedVisibilityService(hosts.GetAll().Select(h => h.HostId)), currentUser);
    }

    private PrtgDiskReadinessQueryService Service(EfPrtgStore store, IHostStore hosts,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verification,
        IVisibilityService visibility, ICurrentUser currentUser)
    {
        var settings = new SystemSettingsStore(_fx.Blob("system_settings"));
        settings.Update(x => x.PrtgSensorTypeWhitelist = ["disk"]);
        return new(store, hosts, settings, evidence, verification, visibility, currentUser);
    }

    private FakeHostStore ActiveHost(long id) => new(new WebHost { HostId = id, HostName = $"host-{id}", Active = true });

    private void SeedOneDiskWithHistory(long hostId, int days, int slotsPerHour = 4)
    {
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 100 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 200, DeviceObjid = 100,
                Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "disk-test" });
            for (var day = 1; day <= days; day++)
            {
                var date = _asOf.AddDays(-day);
                db.PrtgHostMaps.Add(Map(100, date, hostId));
            }
            db.SaveChanges();
        }
        var store = new EfPrtgStore(_fx.NewContext);
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey), _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey),
            200, 100, hostId, "disk", DateTime.SpecifyKind(_asOf.Date.AddDays(-35), DateTimeKind.Utc));
        var accumulator = new PrtgSnapshotAccumulator();
        for (var day = 1; day <= days; day++)
        for (var hour = 0; hour < 12; hour++)
        for (var slot = 0; slot < slotsPerHour; slot++)
        {
            var measured = DateTime.SpecifyKind(_asOf.AddDays(-day).AddHours(hour).AddMinutes(slot * 15), DateTimeKind.Utc);
            var sample = new PrtgTrustedSample(200, 50, profile.SourceGeneration, profile.ResourceGeneration,
                profile.ChannelGeneration, profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
                profile.StrategyEffectiveFromHourUtc, measured, measured.AddSeconds(10),
                profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, $"measurement-{day}-{hour}-{slot}",
                profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId);
            Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(sample, sample.ReceivedAt));
        }
        var recovered = new PrtgSnapshotAccumulator();
        recovered.Restore(accumulator.Capture());
        var rows = recovered.DrainAll(4, DateTime.SpecifyKind(_asOf, DateTimeKind.Utc));
        Assert.Equal(days * 12, store.MergeSampledValues(rows));
    }

    private static PrtgDiskSemanticContext SemanticContext(long hostId) =>
        new(200, 100, hostId, "disk", "free", "Free Space", "%", 1, "descending-danger");

    private PrtgResourceIdentity BindDiskIdentity(long sensorId, long deviceId, long hostId)
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var identity = PrtgResourceFixture.Bind(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey), sensorId, deviceId, hostId, "readiness-source");
        return PrtgResourceFixture.BindChannel(store, identity, "free", "Free Space", "%", 1,
            "descending-danger");
    }

    private PrtgDiskVerificationResult VerifiedResult(long hostId, PrtgResourceIdentity identity,
        string? parserSemanticVersion = null) =>
        new(200, 100, hostId, "disk", "Verified", "typed result", "free", "Free Space", "%", 1,
            "descending-danger", 12, true, _asOf.ToUniversalTime(), _asOf.AddDays(-1),
            parserSemanticVersion ?? PrtgDiskAssessmentService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch);

    private sealed class FakeHostStore(params WebHost[] hosts) : IHostStore
    {
        public int GetAllCount { get; private set; }
        public void ResetGetAllCount() => GetAllCount = 0;
        public List<WebHost> GetAll() { GetAllCount++; return hosts.ToList(); }
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

    private sealed class RevokeGroupAccessAtCandidateCount(FakeGroupAccessStore access) : DbCommandInterceptor
    {
        private int _revoked;
        public bool Revoked => Volatile.Read(ref _revoked) != 0;

        private void Revoke(DbCommand command)
        {
            if (!command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase) ||
                Interlocked.Exchange(ref _revoked, 1) != 0) return;
            access.ReplaceAll([]);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Revoke(command); return result; }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        { Revoke(command); return result; }
    }

    private sealed class ReadinessValueReadBoundaryProbe(SqliteConnection connection) : DbCommandInterceptor
    {
        public bool Enabled { get; set; } = true;
        public int ValueSelectCount { get; private set; }
        public bool MutatedAtDetailsBoundary { get; private set; }
        public int UpdatedRows { get; private set; }

        private void Observe(DbCommand command)
        {
            if (!Enabled) return;
            var sql = command.CommandText;
            if (sql.Contains("lf_prtg_values", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                ValueSelectCount++;

            // GetReadinessSensorDetailsByIds runs after Core has materialized the page's values.
            // Change the stored rows at this seam: readiness and masks must still describe Core's
            // one captured value read, rather than combining it with a second Web-layer read.
            if (!MutatedAtDetailsBoundary && ValueSelectCount >= 2 &&
                sql.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("lf_prtg_devices", StringComparison.OrdinalIgnoreCase) &&
                !sql.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase))
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE lf_prtg_values SET quality = 'Corrupt' WHERE sensor_objid = 6200";
                UpdatedRows = update.ExecuteNonQuery();
                MutatedAtDetailsBoundary = true;
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Observe(command); return result; }
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
            new PrtgReadinessHour(_asOf.AddDays(-days + d).AddHours(h), quality, coverage, Trusted: true)));

    private static PrtgHostMapRow Map(long device, DateTime date, long host,
        string status = PrtgMapStatus.Ok) => new() { DeviceObjid = device, MapDate = date.Date,
            HostId = host, MapStatus = status };
}
