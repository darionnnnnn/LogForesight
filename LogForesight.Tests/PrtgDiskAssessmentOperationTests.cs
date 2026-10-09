using System.Data.Common;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskAssessmentOperationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CandidateCommandCounter _commands = new();
    private readonly DateOnly _completedDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
    private readonly DateTime _candidateThrough = DateTime.Today;

    public PrtgDiskAssessmentOperationTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection).AddInterceptors(_commands).Options);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void ThreeThousandCandidates_CountOnce_AndPagesReuseOneNonemptyMetadataCapture()
    {
        var fixture = Seed(3_000);
        _commands.Reset();

        var operation = fixture.Service.BeginAssessment(_completedDay, _candidateThrough,
            capturedHostSnapshot: ((IHostStore)fixture.Hosts).CapturePrtgSnapshot());
        var firstPage = fixture.Service.AssessPage(operation, 0, 20);
        var summary = fixture.Service.AssessPage(operation, 0, 100);

        Assert.Equal(3_000, operation.CandidateCount);
        Assert.Equal(20, firstPage.Rows.Count);
        Assert.Equal(100, summary.Rows.Count);
        Assert.Equal(1, operation.MetadataSnapshot.EvidenceDeserializeCount);
        Assert.Equal(1, operation.MetadataSnapshot.VerificationDeserializeCount);
        Assert.Equal(1, _commands.CandidateCountQueries);
        Assert.Equal(firstPage.Rows.Select(x => x.SensorObjid), summary.Rows.Take(20).Select(x => x.SensorObjid));

        fixture.Service.CompleteAssessment(operation);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.AssessPage(operation, 0, 1));
    }

    [Fact]
    public void LegacyEvidenceWithoutIdentityReturnsUnreadyRowAndDoesNotFailWholeRead()
    {
        var fixture = Seed(1);
        var operation = fixture.Service.BeginAssessment(_completedDay, _candidateThrough,
            capturedHostSnapshot: ((IHostStore)fixture.Hosts).CapturePrtgSnapshot());

        var page = fixture.Service.AssessPage(operation, 0, 1);
        var row = Assert.Single(page.Rows);

        Assert.False(row.EvidenceValidity?.IsValid ?? false);
        Assert.False(row.Readiness.SemanticReady);
        fixture.Service.CompleteAssessment(operation);
    }

    [Fact]
    public void IdentityAppearingAfterLegacyMetadataCaptureRejectsTheOperation()
    {
        var fixture = Seed(1);
        var operation = fixture.Service.BeginAssessment(_completedDay, _candidateThrough,
            capturedHostSnapshot: ((IHostStore)fixture.Hosts).CapturePrtgSnapshot());
        using (var db = NewContext())
        {
            PrtgResourceIdentityStore.Set(db, 1, "source-v1", 10_001, 1,
                "resource-v1", "disk|auto", "channel-v1", true, DateTimeOffset.UtcNow);
            db.SaveChanges();
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Service.CompleteAssessment(operation));
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("catalogue")]
    [InlineData("evidence")]
    [InlineData("verification")]
    [InlineData("settings")]
    [InlineData("host")]
    public void LateSourceMutationRejectsTheWholeOperation(string source)
    {
        var fixture = Seed(1);
        var operation = fixture.Service.BeginAssessment(_completedDay, _candidateThrough,
            capturedHostSnapshot: ((IHostStore)fixture.Hosts).CapturePrtgSnapshot());
        fixture.Service.AssessPage(operation, 0, 1);

        switch (source)
        {
            case "mapping":
                fixture.Store.ReplaceHostMapForDate(_candidateThrough,
                    [new PrtgHostMapRow { DeviceObjid = 10_001, MapDate = _candidateThrough,
                        HostId = 1, MapStatus = PrtgMapStatus.Ok }]);
                break;
            case "catalogue":
                fixture.Store.UpsertSensors([new PrtgSensorRow { Objid = 1, DeviceObjid = 10_001,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = "changed" }], DateTime.Now);
                break;
            case "evidence":
                fixture.Evidence.ConfirmManually(Context(1), 3, "changed evidence", DateTime.UtcNow,
                    PrtgDiskAssessmentService.ParserSemanticVersion);
                break;
            case "verification":
                fixture.Verification.Save(Verified(1));
                break;
            case "settings":
                fixture.Settings.Update(settings => settings.PrtgSensorTypeWhitelist = ["other"]);
                break;
            case "host":
                fixture.Hosts.Upsert(new WebHost { HostId = 1, HostName = "host-1", Active = false });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Service.CompleteAssessment(operation));
    }

    [Fact]
    public void CandidatePagesReuseOneImmutableHostSnapshotAndFinalFenceRejectsHostMutation()
    {
        var fixture = Seed(101);
        fixture.Hosts.ResetCallCounts();
        PrtgDiskCandidateSnapshot? candidateSnapshot = null;
        PrtgDiskMetadataSnapshot? metadataSnapshot = null;
        var assessed = 0;
        while (true)
        {
            var page = fixture.Service.AssessCandidatePage(_completedDay,
                rule: null, mode: PrtgDiskDecisionMode.Formal, limit: 100, offset: assessed,
                selectedHostIds: [1], candidateSnapshot: candidateSnapshot,
                candidateMappingRevision: null, candidatePolicyVersion: "daily-policy",
                metadataSnapshot: metadataSnapshot, deferMetadataFence: true);
            candidateSnapshot = page.CandidateSnapshot;
            metadataSnapshot = page.MetadataSnapshot;
            assessed += page.AssessedCount;
            if (!page.HasMore) break;
        }

        Assert.Equal(101, assessed);
        Assert.Equal(1, fixture.Hosts.GetAllCallCount);
        fixture.Service.ValidateAssessmentSnapshot(candidateSnapshot, metadataSnapshot);

        fixture.Hosts.Upsert(new WebHost { HostName = "host-1", Active = false });
        Assert.Throws<InvalidOperationException>(() => fixture.Service.ValidateAssessmentSnapshot(candidateSnapshot, metadataSnapshot));
    }

    private (PrtgDiskAssessmentService Service, EfPrtgStore Store, PrtgDiskSemanticEvidenceStore Evidence,
        PrtgDiskVerificationResultStore Verification, SystemSettingsStore Settings, FakeHostStore Hosts) Seed(int count)
    {
        var store = new EfPrtgStore(NewContext);
        using (var db = NewContext())
        {
            for (var i = 1; i <= count; i++)
            {
                var device = 10_000L + i;
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = device, Name = $"device-{i}" });
                db.PrtgSensors.Add(new PrtgSensorRow { Objid = i, DeviceObjid = device,
                    Category = PrtgSensorCategories.Disk, SensorType = "disk", Name = $"disk-{i}" });
                db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = device,
                    MapDate = _candidateThrough.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Ok });
            }

            var evidence = new Dictionary<long, PrtgDiskSemanticEvidence>
            {
                [1] = new(1, 10_001, 1, "disk", "free", "Free", "%", 1,
                    "descending-danger", PrtgDiskSemanticEvidenceSource.Manual, 1,
                    "metadata fixture", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion)
            };
            var verification = new Dictionary<long, PrtgDiskVerificationResult> { [1] = Verified(1) };
            db.Blobs.Add(new BlobRow { BlobKey = PrtgDiskSemanticEvidenceStore.BlobKey,
                Content = JsonSerializer.Serialize(evidence, LfJsonOptions.Pretty), Version = 1, UpdatedAt = DateTime.Now });
            db.Blobs.Add(new BlobRow { BlobKey = PrtgDiskVerificationResultStore.BlobKey,
                Content = JsonSerializer.Serialize(verification, LfJsonOptions.Pretty), Version = 1, UpdatedAt = DateTime.Now });
            db.SaveChanges();
        }

        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        var settings = new SystemSettingsStore(new EfJsonBlobStore(NewContext, "system_settings"));
        settings.Update(value => value.PrtgSensorTypeWhitelist = ["disk"]);
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "host-1", Active = true });
        var service = new PrtgDiskAssessmentService(store, hosts, settings, evidenceStore, verificationStore);
        return (service, store, evidenceStore, verificationStore, settings, hosts);
    }

    private static PrtgDiskSemanticContext Context(long sensorId) =>
        new(sensorId, 10_000 + sensorId, 1, "disk", "free", "Free", "%", 1, "descending-danger");

    private PrtgDiskVerificationResult Verified(long sensorId) =>
        new(sensorId, 10_000 + sensorId, 1, "disk", "Verified", "typed result", "free", "Free", "%", 1,
            "descending-danger", 1, true, DateTime.UtcNow, _candidateThrough.AddDays(-1),
            PrtgDiskAssessmentService.ParserSemanticVersion);

    private sealed class CandidateCommandCounter : DbCommandInterceptor
    {
        public int CandidateCountQueries { get; private set; }
        public void Reset() => CandidateCountQueries = 0;

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase))
                CandidateCountQueries++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        { Record(command); return result; }
    }
}
