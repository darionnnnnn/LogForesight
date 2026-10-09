using System.Data.Common;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;
using Xunit;

namespace LogForesight.Tests;

[CollectionDefinition("R10 metadata snapshot memory", DisableParallelization = true)]
public sealed class PrtgDiskMetadataSnapshotCollection { }

[Collection("R10 metadata snapshot memory")]
public sealed class PrtgDiskMetadataSnapshotTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly BlobReadCounter _counter = new();
    private readonly ITestOutputHelper _output;
    private readonly DateTime _day = new(2026, 9, 24);

    public PrtgDiskMetadataSnapshotTests(ITestOutputHelper output)
    {
        _output = output;
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection).AddInterceptors(_counter).Options);

    public void Dispose() => _connection.Dispose();

    private sealed class BlobReadCounter : DbCommandInterceptor
    {
        private readonly Dictionary<string, int> _boundedReads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _fullContentReads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _versionReads = new(StringComparer.Ordinal);
        public List<string> ExecutedSql { get; } = new();
        public int BoundedReads(string key) => _boundedReads.GetValueOrDefault(key);
        public int FullContentReads(string key) => _fullContentReads.GetValueOrDefault(key);
        public int VersionReads(string key) => _versionReads.GetValueOrDefault(key);
        public void Reset()
        { _boundedReads.Clear(); _fullContentReads.Clear(); _versionReads.Clear(); ExecutedSql.Clear(); }

        private void Record(DbCommand command)
        {
            ExecutedSql.Add(command.CommandText);
            if (!command.CommandText.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase)) return;
            foreach (DbParameter parameter in command.Parameters)
                if (parameter.Value is string key && key is PrtgDiskSemanticEvidenceStore.BlobKey or PrtgDiskVerificationResultStore.BlobKey)
                {
                    if (command.CommandText.Contains("substr", StringComparison.OrdinalIgnoreCase))
                        _boundedReads[key] = _boundedReads.GetValueOrDefault(key) + 1;
                    else if (command.CommandText.Contains("content", StringComparison.OrdinalIgnoreCase))
                        _fullContentReads[key] = _fullContentReads.GetValueOrDefault(key) + 1;
                    else if (command.CommandText.Contains("version", StringComparison.OrdinalIgnoreCase))
                        _versionReads[key] = _versionReads.GetValueOrDefault(key) + 1;
                }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        { Record(command); return result; }
    }

    private sealed class CallbackSettingsStore(Action onGet) : ISystemSettingsStore
    {
        private bool _called;
        public SystemSettings Get()
        {
            if (!_called) { _called = true; onGet(); }
            return new SystemSettings();
        }
        public SystemSettings Update(Action<SystemSettings> mutation) => throw new NotSupportedException();
    }

    private (PrtgDiskAssessmentService Service, PrtgDiskSemanticEvidenceStore Evidence,
        PrtgDiskVerificationResultStore Verification, FakeHostStore Hosts, long[] Ids) Seed(int count,
        ISystemSettingsStore? settings = null, bool writeMetadata = true)
    {
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray();
        using (var db = NewContext())
        {
            for (var i = 1; i <= count; i++)
            {
                var deviceId = 10_000L + i;
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId, Name = $"device-{i}" });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = i, DeviceObjid = deviceId, Category = PrtgSensorCategories.Disk,
                    SensorType = "disk", Name = $"disk-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = deviceId, MapDate = _day.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Ok
                });
            }

            if (writeMetadata)
            {
                var evidence = ids.ToDictionary(id => id, id => new PrtgDiskSemanticEvidence(
                    id, 10_000 + id, 1, "disk", "free", "Free", "%", 1,
                    "descending-danger", PrtgDiskSemanticEvidenceSource.Manual, 1,
                    "Typed semantic metadata fixture.", DateTime.SpecifyKind(_day, DateTimeKind.Utc),
                    PrtgDiskAssessmentService.ParserSemanticVersion));
                var probes = ids.ToDictionary(id => id, id => new PrtgDiskVerificationResult(
                    id, 10_000 + id, 1, "disk", "Verified", "typed result", "free", "Free", "%", 1,
                    "descending-danger", 1, true, DateTime.SpecifyKind(_day, DateTimeKind.Utc), _day,
                    PrtgDiskAssessmentService.ParserSemanticVersion));
                db.Blobs.Add(new BlobRow
                {
                    BlobKey = PrtgDiskSemanticEvidenceStore.BlobKey,
                    Content = JsonSerializer.Serialize(evidence, LfJsonOptions.Pretty),
                    UpdatedAt = DateTime.Now, Version = 1
                });
                db.Blobs.Add(new BlobRow
                {
                    BlobKey = PrtgDiskVerificationResultStore.BlobKey,
                    Content = JsonSerializer.Serialize(probes, LfJsonOptions.Pretty),
                    UpdatedAt = DateTime.Now, Version = 1
                });
            }
            db.SaveChanges();
        }

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "metadata-host", Active = true });
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        var service = new PrtgDiskAssessmentService(new EfPrtgStore(NewContext), hosts,
            settings ?? new FakeSystemSettingsStore(), evidenceStore, verificationStore);
        return (service, evidenceStore, verificationStore, hosts, ids);
    }

    [Fact]
    public void 有資料的兩份全字典每次作業各反序列化一次並由跨頁共用()
    {
        var (service, _, _, _, ids) = Seed(205);
        _counter.Reset();

        var first = service.AssessCandidatePage(DateOnly.FromDateTime(_day), null, limit: 100,
            selectedSensorObjids: ids);

        Assert.Equal(205, first.CandidateCount);
        Assert.Equal(100, first.Rows.Count);
        Assert.Equal(1, first.MetadataSnapshot!.EvidenceDeserializeCount);
        Assert.Equal(1, first.MetadataSnapshot.VerificationDeserializeCount);
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(1, _counter.VersionReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.VersionReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Contains("substr", string.Join("\n", _counter.ExecutedSql), StringComparison.OrdinalIgnoreCase);

        var second = service.AssessCandidatePage(DateOnly.FromDateTime(_day), null, limit: 100,
            offset: 100, selectedSensorObjids: ids, candidateSnapshot: first.CandidateSnapshot,
            metadataSnapshot: first.MetadataSnapshot);

        Assert.Same(first.MetadataSnapshot, second.MetadataSnapshot);
        Assert.Equal(100, second.Rows.Count);
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(2, _counter.VersionReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(2, _counter.VersionReads(PrtgDiskVerificationResultStore.BlobKey));
    }

    [Fact]
    public void HasAnyReadySemanticCandidate多批處理每個store只做一次有界讀取()
    {
        var (service, _, _, _, _) = Seed(205);
        _counter.Reset();

        Assert.False(service.HasAnyReadySemanticCandidate(DateOnly.FromDateTime(_day), null));

        Assert.Equal(1, _counter.BoundedReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(1, _counter.VersionReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.VersionReads(PrtgDiskVerificationResultStore.BlobKey));
    }

    [Fact]
    public void 一百個candidate也各只反序列化兩份metadata一次()
    {
        var (service, _, _, _, ids) = Seed(100);
        _counter.Reset();

        var page = service.AssessCandidatePage(DateOnly.FromDateTime(_day), null, limit: 100,
            selectedSensorObjids: ids);

        Assert.Equal(100, page.CandidateCount);
        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(1, page.MetadataSnapshot!.EvidenceDeserializeCount);
        Assert.Equal(1, page.MetadataSnapshot.VerificationDeserializeCount);
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(1, _counter.BoundedReads(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskSemanticEvidenceStore.BlobKey));
        Assert.Equal(0, _counter.FullContentReads(PrtgDiskVerificationResultStore.BlobKey));
    }

    [Fact]
    public void 版本0快照在blob首次插入後失效且評估整批拒絕()
    {
        PrtgDiskSemanticEvidenceStore? evidence = null;
        var settings = new CallbackSettingsStore(() => evidence!.ConfirmManually(
            new PrtgDiskSemanticContext(1, 10_001, 1, "disk", "free", "Free", "%", 1,
                "descending-danger"), 7, "Inserted during assessment.", DateTime.UtcNow,
            PrtgDiskAssessmentService.ParserSemanticVersion));
        var seeded = Seed(1, settings, writeMetadata: false);
        evidence = seeded.Evidence;
        var empty = evidence.CaptureSnapshot();
        Assert.Equal(0, empty.Version);
        Assert.Equal(0, empty.DeserializeCount);

        var rejected = Assert.Throws<InvalidOperationException>(() => seeded.Service.Assess(
            DateOnly.FromDateTime(_day), null, selectedSensorObjids: seeded.Ids));
        Assert.Contains("metadata", rejected.Message);
        Assert.True(evidence.CaptureSnapshot().Version > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasAnyReady的false返回前任一metadata變更都拒絕整個判定(bool mutateVerification)
    {
        PrtgDiskSemanticEvidenceStore? evidence = null;
        PrtgDiskVerificationResultStore? verification = null;
        var settings = new CallbackSettingsStore(() =>
        {
            if (mutateVerification)
                verification!.Save(new PrtgDiskVerificationResult(1, 10_001, 1, "disk", "Failed",
                    "Changed during readiness evaluation.", null, null, null, null, null, 0, null,
                    DateTime.UtcNow, _day, PrtgDiskAssessmentService.ParserSemanticVersion));
            else
                evidence!.ConfirmManually(new PrtgDiskSemanticContext(1, 10_001, 1, "disk", "free", "Free", "%", 1,
                    "descending-danger"), 7, "Changed during readiness evaluation.", DateTime.UtcNow,
                    PrtgDiskAssessmentService.ParserSemanticVersion);
        });
        var seeded = Seed(1, settings);
        evidence = seeded.Evidence;
        verification = seeded.Verification;

        var rejected = Assert.Throws<InvalidOperationException>(() => seeded.Service.HasAnyReadySemanticCandidate(
            DateOnly.FromDateTime(_day), null));

        Assert.Contains("metadata", rejected.Message);
        Assert.True((mutateVerification ? verification!.CaptureSnapshot().Version : evidence.CaptureSnapshot().Version) > 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasAnyReady的true返回前任一metadata變更都拒絕原本可用的判定(bool mutateVerification)
    {
        const long sensorId = 9_001;
        const long deviceId = 9_002;
        var completedDate = DateOnly.FromDateTime(_day);
        using (var db = NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = sensorId, DeviceObjid = deviceId, Category = PrtgSensorCategories.Disk,
                SensorType = "SNMP Disk Free", Name = "Disk C:"
            });
            var maps = new List<PrtgHostMapRow>();
            var values = new List<PrtgValueRow>();
            for (var offset = -27; offset <= 0; offset++)
            {
                var date = _day.AddDays(offset);
                maps.Add(new PrtgHostMapRow
                { DeviceObjid = deviceId, MapDate = date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
                for (var hour = 0; hour < 12; hour++)
                    values.Add(new PrtgValueRow
                    {
                        SensorObjid = sensorId, PeriodStart = date.AddHours(hour), AvgValue = 75,
                        MinValue = 75, MaxValue = 75, Coverage = 100, Quality = PrtgDataQuality.Ok,
                        CreatedAt = _day
                    });
            }
            db.PrtgHostMaps.AddRange(maps);
            db.PrtgValues.AddRange(values);
            db.SaveChanges();
        }

        var prtgStore = new EfPrtgStore(NewContext);
        var identity = PrtgResourceFixture.Bind(prtgStore,
            new EfJsonBlobStore(NewContext, PrtgMonitoringPolicyStore.BlobKey), sensorId, deviceId, 1,
            "metadata-source", "metadata-resource");
        identity = PrtgResourceFixture.BindChannel(prtgStore, identity, "free", "Free", "%", 1,
            "descending-danger");
        var evidence = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        evidence.ConfirmManually(new PrtgDiskSemanticContext(sensorId, deviceId, 1,
                "SNMP Disk Free", "free", "Free", "%", 1, "descending-danger"),
            7, "Ready semantic fixture.", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion,
            identity.SourceGeneration, identity.Generation, identity.ChannelGeneration, identity.Epoch);
        var verification = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        verification.Save(new PrtgDiskVerificationResult(sensorId, deviceId, 1, "SNMP Disk Free",
            "Verified", "Typed values match.", "free", "Free", "%", 1, "descending-danger", 1, true,
            DateTime.UtcNow, _day, PrtgDiskAssessmentService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "ready-host", Active = true });
        PrtgDiskAssessmentService CreateService(ISystemSettingsStore settings) => new(new EfPrtgStore(NewContext),
            hosts, settings, evidence, verification);

        PrtgResourceFixture.AuthorizeSeededDiskHistory(NewContext, sensorId, deviceId, 1,
            "SNMP Disk Free", identity.SourceGeneration);
        Assert.True(CreateService(new FakeSystemSettingsStore()).HasAnyReadySemanticCandidate(completedDate, null));
        var settingsWithMutation = new CallbackSettingsStore(() =>
        {
            if (mutateVerification)
                verification.Save(new PrtgDiskVerificationResult(sensorId, deviceId, 1, "SNMP Disk Free", "Failed",
                    "Changed during readiness evaluation.", null, null, null, null, null, 0, null,
                    DateTime.UtcNow, _day, PrtgDiskAssessmentService.ParserSemanticVersion));
            else
                evidence.ConfirmManually(new PrtgDiskSemanticContext(sensorId, deviceId, 1, "SNMP Disk Free", "free", "Free", "%", 1,
                    "descending-danger"), 7, "Changed during readiness evaluation.", DateTime.UtcNow,
                    PrtgDiskAssessmentService.ParserSemanticVersion);
        });

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            CreateService(settingsWithMutation).HasAnyReadySemanticCandidate(completedDate, null));

        Assert.Contains("metadata", rejected.Message);
    }

    [Fact]
    public void 任一metadata版本在捕獲後改變都會使typedSnapshot失效()
    {
        var (_, evidence, verification, _, ids) = Seed(1);
        var evidenceSnapshot = evidence.CaptureSnapshot();
        var verificationSnapshot = verification.CaptureSnapshot();

        evidence.ConfirmManually(new PrtgDiskSemanticContext(ids[0], 10_001, 1, "disk", "free", "Free", "%", 1,
                "descending-danger"), 7, "Updated evidence.", DateTime.UtcNow,
            PrtgDiskAssessmentService.ParserSemanticVersion);
        verification.Save(new PrtgDiskVerificationResult(ids[0], 10_001, 1, "disk", "Verified", "updated",
            "free", "Free", "%", 1, "descending-danger", 1, true, DateTime.UtcNow, _day,
            PrtgDiskAssessmentService.ParserSemanticVersion));

        Assert.False(evidence.IsSnapshotCurrent(evidenceSnapshot));
        Assert.False(verification.IsSnapshotCurrent(verificationSnapshot));
    }

    [Fact]
    public void malformed_nullRecord與超32Mi字元blob都拒絕整份快照()
    {
        var store = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        var verification = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));

        WriteBlob(PrtgDiskSemanticEvidenceStore.BlobKey, "{\"1\":");
        Assert.Throws<JsonException>(() => store.CaptureSnapshot());
        WriteBlob(PrtgDiskSemanticEvidenceStore.BlobKey, "{\"1\":null}");
        Assert.Throws<InvalidDataException>(() => store.CaptureSnapshot());
        WriteBlob(PrtgDiskSemanticEvidenceStore.BlobKey, "{\"1\":{\"SensorObjid\":1}}");
        Assert.Throws<InvalidDataException>(() => store.CaptureSnapshot());
        WriteBlob(PrtgDiskVerificationResultStore.BlobKey, "{\"1\":null}");
        Assert.Throws<InvalidDataException>(() => verification.CaptureSnapshot());
        WriteBlob(PrtgDiskVerificationResultStore.BlobKey, "{\"1\":{\"SensorObjid\":1}}");
        Assert.Throws<InvalidDataException>(() => verification.CaptureSnapshot());
        WriteBlob(PrtgDiskSemanticEvidenceStore.BlobKey, "null");
        Assert.Throws<JsonException>(() => store.CaptureSnapshot());

        var oversizedContent = "{}" + new string(' ', PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters);
        WriteBlob(PrtgDiskSemanticEvidenceStore.BlobKey, oversizedContent);
        var oversized = Assert.Throws<InvalidDataException>(() => store.CaptureSnapshot());
        Assert.Contains("上限", oversized.Message);
        WriteBlob(PrtgDiskVerificationResultStore.BlobKey, oversizedContent);
        Assert.Throws<InvalidDataException>(() => verification.CaptureSnapshot());
    }

    [Fact]
    public void 兩份正式Pretty字典各16000筆且含500字摘要仍在32Mi字元內完整接受()
    {
        const int metadataCount = 16_000;
        var verifiedAt = DateTime.SpecifyKind(_day, DateTimeKind.Utc);
        const string longSummary = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        Assert.Equal(500, longSummary.Length);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext,
            PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var fixturePeakWorkingSetBefore = process.PeakWorkingSet64;
        var fixtureAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        evidenceStore.Update(all =>
        {
            for (var id = 1; id <= metadataCount; id++)
                all[id] = new PrtgDiskSemanticEvidence(id, 10_000 + id, 1, "SNMP Disk Free",
                    "channel-available-percent", "Free Space Percent", "%", 1, "descending-danger",
                    PrtgDiskSemanticEvidenceSource.Manual, 1, longSummary, verifiedAt,
                    PrtgDiskAssessmentService.ParserSemanticVersion);
        });
        verificationStore.Update(all =>
        {
            for (var id = 1; id <= metadataCount; id++)
                all[id] = new PrtgDiskVerificationResult(id, 10_000 + id, 1, "SNMP Disk Free",
                    "Verified", longSummary, "channel-available-percent", "Free Space Percent", "%", 1,
                    "descending-danger", 5, true, verifiedAt, _day,
                    PrtgDiskAssessmentService.ParserSemanticVersion);
        });

        var allocatedBeforeCapture = GC.GetTotalAllocatedBytes(precise: true);
        process.Refresh();
        var capturePeakWorkingSetBefore = process.PeakWorkingSet64;
        var evidenceSnapshot = evidenceStore.CaptureSnapshot();
        var allocatedAfterEvidence = GC.GetTotalAllocatedBytes(precise: true);
        var verificationSnapshot = verificationStore.CaptureSnapshot();
        var allocatedAfterBoth = GC.GetTotalAllocatedBytes(precise: true);
        process.Refresh();
        var fixturePeakWorkingSetAfter = process.PeakWorkingSet64;

        Assert.Equal(metadataCount, evidenceSnapshot.Count);
        Assert.Equal(metadataCount, verificationSnapshot.Count);
        Assert.Equal(metadataCount, evidenceSnapshot.GetSensorIds().Length);
        Assert.InRange(evidenceSnapshot.SourceCharacters, 1, PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters);
        Assert.InRange(verificationSnapshot.SourceCharacters, 1, PrtgDiskVerificationResultStore.MaximumSnapshotCharacters);
        var peakIncrease = Math.Max(0, fixturePeakWorkingSetAfter - fixturePeakWorkingSetBefore);
        var captureAllocation = allocatedAfterBoth - allocatedBeforeCapture;
        Assert.InRange(captureAllocation, 0, 1024L * 1024 * 1024);
        Assert.InRange(peakIncrease, 0, 1024L * 1024 * 1024);
        _output.WriteLine($"16k Pretty metadata fixture: evidence={evidenceSnapshot.SourceCharacters:N0} chars, " +
            $"verification={verificationSnapshot.SourceCharacters:N0} chars; " +
            $"fixture allocated={allocatedAfterBoth - fixtureAllocatedBefore:N0} bytes; " +
            $"capture allocated={captureAllocation:N0} bytes " +
            $"(evidence={allocatedAfterEvidence - allocatedBeforeCapture:N0}, " +
            $"verification={allocatedAfterBoth - allocatedAfterEvidence:N0}); " +
            $"process peak working set before/after fixture={fixturePeakWorkingSetBefore:N0}/" +
            $"{fixturePeakWorkingSetAfter:N0} bytes, before/after capture=" +
            $"{capturePeakWorkingSetBefore:N0}/{fixturePeakWorkingSetAfter:N0} bytes, " +
            $"current working set={process.WorkingSet64:N0} bytes, peak increase={peakIncrease:N0} bytes.");
    }

    [Fact]
    public void 失敗與取消的核驗結果可用nullChannel共存於metadata快照()
    {
        var store = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext,
            PrtgDiskVerificationResultStore.BlobKey));
        var checkedAt = DateTime.SpecifyKind(_day, DateTimeKind.Utc);
        store.Update(all =>
        {
            all[1] = new PrtgDiskVerificationResult(1, 11, 1, "SNMP Disk Free", "Failed",
                "probe failed", null, null, null, null, null, 0, null, checkedAt, _day,
                PrtgDiskAssessmentService.ParserSemanticVersion);
            all[2] = new PrtgDiskVerificationResult(2, 22, 1, "SNMP Disk Free", "Cancelled",
                "probe cancelled", null, null, null, null, null, 0, null, checkedAt, _day,
                PrtgDiskAssessmentService.ParserSemanticVersion, Cancelled: true);
        });

        var snapshot = store.CaptureSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Null(snapshot.Get(1)!.ChannelIdentifier);
        Assert.Null(snapshot.Get(2)!.ChannelName);
        Assert.True(snapshot.Get(2)!.Cancelled);
    }

    [Fact]
    public void 三千試點主機只取一次清單並保留requested與policy順序()
    {
        var inventory = Enumerable.Range(1, 3000).Select(id => new WebHost
        {
            HostId = id,
            HostName = $"host-{id}",
            Source = id == 2997 ? "local" : "netiq",
            Active = id != 2999,
            MergedInto = id == 2998 ? 1 : null
        }).ToList();
        var readCount = 0;
        List<WebHost> ReadHosts() { readCount++; return inventory; }
        var policyOrder = Enumerable.Range(1, 3000).Select(id => (long)id).Reverse().ToArray();

        var selected = PrtgDailyPipeline.SelectActivePilotHostIds(policyOrder,
            new long[] { 3000, 2999, 2998, 2997, 2000, 1 }, ReadHosts);

        Assert.Equal(1, readCount);
        Assert.Equal(new long[] { 3000, 2000, 1 }, selected);
    }

    private void WriteBlob(string key, string content)
    {
        using var db = NewContext();
        var existing = db.Blobs.SingleOrDefault(b => b.BlobKey == key);
        if (existing is null)
            db.Blobs.Add(new BlobRow { BlobKey = key, Content = content, UpdatedAt = DateTime.Now, Version = 1 });
        else
        {
            existing.Content = content;
            existing.Version++;
            existing.UpdatedAt = DateTime.Now;
        }
        db.SaveChanges();
    }
}
