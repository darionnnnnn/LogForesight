using System.Data.Common;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskAssessmentRangeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly RangeCommandCounter _commands = new();
    private readonly EfPrtgStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore _verification;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgDiskAssessmentService _service;
    private readonly DateOnly _from = DateOnly.FromDateTime(DateTime.Today.AddDays(-5));

    public PrtgDiskAssessmentRangeTests()
    {
        _connection.Open();
        using (var db = NewContext()) db.Database.EnsureCreated();
        _store = new EfPrtgStore(NewContext);
        _evidence = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        _verification = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        _settings = new SystemSettingsStore(new EfJsonBlobStore(NewContext, "system_settings"));
        _hosts.Upsert(new WebHost { HostId = 1, HostName = "range-host", Active = true });
        _service = new PrtgDiskAssessmentService(_store, _hosts, _settings, _evidence, _verification);
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection).AddInterceptors(_commands).Options);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void RangeUsesDateSpecificDenominatorsAndFlattenedOffsetsAcrossDates()
    {
        SeedDevice(10, [101, 102], [(_from.AddDays(1), PrtgMapStatus.Ok, (long?)1)]);
        SeedDevice(20, [201], [(_from.AddDays(2), PrtgMapStatus.Ok, (long?)1)]);

        var operation = Begin(_from, _from.AddDays(2));
        Assert.Equal(0, operation.GetDailyCount(0));
        Assert.Equal(2, operation.GetDailyCount(1));
        Assert.Equal(3, operation.GetDailyCount(2));
        Assert.Equal(5, operation.CandidateCount);
        Assert.Equal(1, _commands.RangeCountStreams);

        _commands.Reset();
        var page = _service.AssessRangePage(operation, offset: 1, limit: 3);

        Assert.Equal(new[] { _from.AddDays(1), _from.AddDays(2), _from.AddDays(2) },
            page.Rows.Select(row => row.CompletedDate));
        Assert.Equal(new long[] { 102, 101, 102 }, page.Rows.Select(row => row.Assessment.SensorObjid));
        Assert.Equal(1, _commands.RangeCandidateSelects);
    }

    [Fact]
    public void RangeCountHonorsLookbackBoundaryAndConflictBarrierAndCountsPausedSensors()
    {
        SeedDevice(10, [101], [(_from.AddDays(-30), PrtgMapStatus.Ok, (long?)1)]);
        SeedDevice(20, [201], [(_from.AddDays(-29), PrtgMapStatus.Ok, (long?)1)]);
        SeedDevice(30, [301], [(_from.AddDays(-1), PrtgMapStatus.Ok, (long?)1),
            (_from, PrtgMapStatus.Conflict, null)]);
        SeedDevice(40, [401], [(_from, PrtgMapStatus.Ok, (long?)1)], paused: true);

        var operation = Begin(_from, _from.AddDays(2));

        Assert.Equal(3, operation.GetDailyCount(0));
        Assert.Equal(2, operation.GetDailyCount(1));
        Assert.Equal(1, operation.GetDailyCount(2));
        var page = _service.AssessRangePage(operation, 0, 100);
        Assert.DoesNotContain(page.Rows, row => row.Assessment.SensorObjid == 301);
        Assert.Contains(page.Rows, row => row.Assessment.SensorObjid == 401);
        Assert.Equal(_from, page.Rows.Single(row => row.Assessment.SensorObjid == 101).CompletedDate);
        Assert.Equal(new long[] { 201, 401 }, page.Rows
            .Where(row => row.CompletedDate == _from.AddDays(1))
            .Select(row => row.Assessment.SensorObjid));
        Assert.DoesNotContain(page.Rows, row => row.CompletedDate == _from.AddDays(1) && row.Assessment.SensorObjid == 101);
        Assert.Equal(new long[] { 401 }, page.Rows
            .Where(row => row.CompletedDate == _from.AddDays(2))
            .Select(row => row.Assessment.SensorObjid));
    }

    [Fact]
    public void RangeAllows730ZeroCandidateDatesThenOneLateCandidate()
    {
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-731));
        var through = from.AddDays(729);
        SeedDevice(10, [101], [(through, PrtgMapStatus.Ok, (long?)1)]);

        var operation = Begin(from, through);

        Assert.Equal(0, operation.GetDailyCount(0));
        Assert.Equal(0, operation.GetDailyCount(728));
        Assert.Equal(1, operation.GetDailyCount(729));
        Assert.Equal(1, operation.CandidateCount);
        var page = _service.AssessRangePage(operation, 0, 100);
        Assert.Single(page.Rows);
        Assert.Equal(through, page.Rows[0].CompletedDate);
    }

    [Fact]
    public void RangeRejectsMoreThan15000CandidatesOnOneDate()
    {
        using (var db = NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 10, Name = "large" });
            db.PrtgSensors.AddRange(Enumerable.Range(1, 15_001).Select(id => new PrtgSensorRow
            {
                Objid = id, DeviceObjid = 10, Category = PrtgSensorCategories.Disk, SensorType = "disk"
            }));
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = 10, MapDate = _from.ToDateTime(TimeOnly.MinValue), HostId = 1, MapStatus = PrtgMapStatus.Ok
            });
            db.SaveChanges();
        }

        Assert.Throws<InvalidOperationException>(() => Begin(_from, _from));
    }

    [Fact]
    public void RangePageCapsTotalHistoricalPointsAndReturnsShortPage()
    {
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-105));
        SeedDevice(10, Enumerable.Range(1, 100).Select(id => (long)id).ToArray(),
            Enumerable.Range(0, 100).Select(day => (from.AddDays(day), PrtgMapStatus.Ok, (long?)1)).ToArray());
        var through = from.AddDays(99);
        var thresholds = PrtgDiskTrendThresholds.Provisional with { RecentWindowDays = 365, MinimumValidDays = 28 };
        var rule = new KnownIssueRule { Id = "range-test", PrtgDiskTrendThresholds = thresholds };
        var operation = Begin(from, through, rule);

        var page = _service.AssessRangePage(operation, 0, 100);

        Assert.Equal(11, page.Rows.Count);
        Assert.True(page.Rows.Count * 365 * 24 <= PrtgDiskAssessmentService.MaximumExpectedHistoricalPointsPerBatch);
    }

    [Fact]
    public void RangeRejectsLateMappingMutationAndHonorsCancellation()
    {
        SeedDevice(10, [101], [(_from, PrtgMapStatus.Ok, (long?)1)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _service.BeginRangeAssessment(_from, _from,
            null, capturedHostSnapshot: ((IHostStore)_hosts).CapturePrtgSnapshot(), cancellationToken: cancellation.Token));

        var operation = Begin(_from, _from);
        Assert.Throws<OperationCanceledException>(() => _service.AssessRangePage(operation, 0, 1, cancellation.Token));
        _store.ReplaceHostMapForDate(_from.ToDateTime(TimeOnly.MinValue),
            [new PrtgHostMapRow { DeviceObjid = 10, MapDate = _from.ToDateTime(TimeOnly.MinValue),
                HostId = 1, MapStatus = PrtgMapStatus.Conflict }]);
        Assert.Throws<InvalidOperationException>(() => _service.CompleteRangeAssessment(operation));
    }

    [Theory]
    [InlineData("catalogue")]
    [InlineData("settings")]
    [InlineData("host")]
    [InlineData("evidence")]
    [InlineData("verification")]
    public void RangeRejectsLateVersionChangesAsAWholeOperation(string version)
    {
        SeedDevice(10, [101], [(_from, PrtgMapStatus.Ok, (long?)1)]);
        var operation = Begin(_from, _from);
        _service.AssessRangePage(operation, 0, 1);

        switch (version)
        {
            case "catalogue":
                _store.UpsertSensors([new PrtgSensorRow { Objid = 101, DeviceObjid = 10,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "changed" }], DateTime.Now);
                break;
            case "settings":
                _settings.Update(value => value.PrtgSensorTypeWhitelist = ["other"]);
                break;
            case "host":
                _hosts.Upsert(new WebHost { HostId = 1, HostName = "range-host", Active = false });
                break;
            case "evidence":
                _evidence.ConfirmManually(new PrtgDiskSemanticContext(101, 10, 1, "disk", "free", "Free", "%", 1,
                    "descending-danger"), 3, "late evidence", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
                break;
            case "verification":
                _verification.Save(new PrtgDiskVerificationResult(101, 10, 1, "disk", "Verified", "late result",
                    "free", "Free", "%", 1, "descending-danger", 1, true, DateTime.UtcNow,
                    _from.ToDateTime(TimeOnly.MinValue), PrtgDiskAssessmentService.ParserSemanticVersion));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }

        Assert.Throws<InvalidOperationException>(() => _service.CompleteRangeAssessment(operation));
    }

    [Fact]
    public void DirectControllerFixtureRejectsNullBodyAndNullRuleWithoutMvcBinding()
    {
        var service = new RuleAdminService(new FakeRuleStore(), new FakeRuleSeedStore(), new FakeSuppressionStore(),
            new FakeUserStore(), FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(),
            new FakeHostGroupStore(), null!, new FakeIssueAggregateQuery());
        var controller = new RulesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.Equal(ApiErrorCodes.ValidationFailed,
            Assert.Throws<DomainException>(() => controller.PreviewDiskTrend(null!)).Code);
        Assert.Equal(ApiErrorCodes.ValidationFailed,
            Assert.Throws<DomainException>(() => controller.PreviewDiskTrend(new DiskTrendRulePreviewRequest { Rule = null! })).Code);
    }

    [Fact]
    public void RangeOperationIsOwnerBoundAndCannotBeReadOrCompletedTwice()
    {
        SeedDevice(10, [101], [(_from, PrtgMapStatus.Ok, (long?)1)]);
        var operation = Begin(_from, _from);
        var otherService = new PrtgDiskAssessmentService(_store, _hosts, _settings, _evidence, _verification);

        Assert.Throws<InvalidOperationException>(() => otherService.AssessRangePage(operation, 0, 1));
        Assert.Throws<InvalidOperationException>(() => otherService.CompleteRangeAssessment(operation));

        _service.CompleteRangeAssessment(operation);
        Assert.Throws<InvalidOperationException>(() => _service.AssessRangePage(operation, 0, 1));
        Assert.Throws<InvalidOperationException>(() => _service.CompleteRangeAssessment(operation));
    }

    private PrtgDiskAssessmentRangeOperation Begin(DateOnly from, DateOnly through, KnownIssueRule? rule = null) =>
        _service.BeginRangeAssessment(from, through, rule, capturedHostSnapshot: ((IHostStore)_hosts).CapturePrtgSnapshot());

    private void SeedDevice(long deviceId, long[] sensorIds,
        (DateOnly Day, string Status, long? HostId)[] mappings, bool paused = false)
    {
        using var db = NewContext();
        db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId, Name = $"device-{deviceId}" });
        db.PrtgSensors.AddRange(sensorIds.Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = deviceId, Category = PrtgSensorCategories.Disk,
            SensorType = "disk", Name = $"disk-{id}", Paused = paused
        }));
        db.PrtgHostMaps.AddRange(mappings.Select(mapping => new PrtgHostMapRow
        {
            DeviceObjid = deviceId, MapDate = mapping.Day.ToDateTime(TimeOnly.MinValue),
            HostId = mapping.HostId, MapStatus = mapping.Status
        }));
        db.SaveChanges();
    }

    private sealed class RangeCommandCounter : DbCommandInterceptor
    {
        public int RangeCandidateSelects { get; private set; }
        public int RangeCountStreams { get; private set; }
        public void Reset()
        {
            RangeCandidateSelects = 0;
            RangeCountStreams = 0;
        }
        private void Record(DbCommand command)
        {
            if (!command.CommandText.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase)) return;
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) RangeCountStreams++;
            else RangeCandidateSelects++;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }
    }
}
