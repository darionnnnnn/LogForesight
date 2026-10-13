using System.Diagnostics;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgProbeLocalFactsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-probe-facts-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    public PrtgProbeLocalFactsTests()
    {
        Directory.CreateDirectory(_root);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_root, "probe.db")};Pooling=False" }, _root);
    }

    [Fact]
    public async Task SavedAuthorityCountsAndYesterdayQualificationAreExportedWithoutNamesOrPayloads()
    {
        var settings = new SystemSettings { PrtgUrl = "https://private-source.invalid", PrtgEnabled = true,
            PrtgResourceGuardSensorObjids = ["101", "102"] };
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.CoreSystemId = "secret-core-name"; p.SourceGeneration = "source-v1"; p.Revision = "rev";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl); p.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            p.HostIds = [1, 2]; p.SensorIds = [101, 102]; p.SourceTimeZoneId = "UTC";
            p.RawTimestampTimeZoneId = "UTC"; p.AnalysisTimeZoneId = "UTC";
            p.SourceCultureName = "en-US"; p.TimeBasisEvidenceReference = "secret-evidence-reference";
        });
        using (var db = _backend.CreateContext())
        {
            db.DailyRecords.AddRange(Parent(1, "{\"LogSource\":2,\"LatestNetiqAttemptStatus\":\"success\",\"private\":\"secret-log-payload\"}"),
                Parent(2, "{\"LogSource\":2,\"LatestNetiqAttemptStatus\":\"failed\"}"),
                Parent(3, "{\"LogSource\":1}"), Parent(4, "{\"LogSource\":2,\"LogSource\":1}"), Parent(5, "{broken"));
            db.SaveChanges();
        }
        NetiqSourceAttempt.RecordBatch(_backend, [1L, 2L], DateTime.Today.AddDays(-1), 1, "success");
        var facts = await PrtgProbeLocalFactsCollector.CollectAsync(_backend, settings, new FakeAi(), CancellationToken.None);
        Assert.Equal("observed", facts.Status);
        Assert.Equal("saved-explicit-authority", facts.AuthorityKind);
        Assert.Equal("UTC", facts.RawTimestampTimeZoneId);
        Assert.Equal(2, facts.PolicySensors); Assert.Equal(2, facts.ReadinessSampledSensors);
        Assert.Equal(2, facts.GuardOverrideSensors); Assert.Equal(0, facts.GuardSensorsInMirror);
        Assert.Equal(2, facts.NetiqReceipts); Assert.Equal(2, facts.NetiqSuccess);
        Assert.Equal(5, facts.NetiqParentRows); Assert.Equal(1, facts.NetiqEligibleParents);
        Assert.Equal("small-request-ready", facts.AiProbeStatus);
        Assert.False(facts.AuthorizesFormalProfile);
        var json = JsonSerializer.Serialize(facts);
        Assert.DoesNotContain("secret", json); Assert.DoesNotContain("private-source", json);
        Assert.DoesNotContain("secret-ai-detail", json);
    }

    [Fact]
    public async Task MissingAuthorityAndBrokenAiStayExplicitlyUnknownWithoutLeakingFailureDetails()
    {
        var facts = await PrtgProbeLocalFactsCollector.CollectAsync(_backend, new SystemSettings(), new FakeAi(true), CancellationToken.None);
        Assert.Equal("observed", facts.Status);
        Assert.Equal("unknown", facts.AuthorityKind);
        Assert.Equal("explicit-time-authority-missing-or-invalid", facts.ReadinessReason);
        Assert.Equal("failed-or-deadline", facts.AiProbeStatus);
        Assert.Equal("no-eligible-netiq-parent", facts.NetiqReason);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(facts));
    }

    [Fact]
    public async Task OneLegacyInvalidProfileIsExcludedWithoutErasingOtherSavedLocalFacts()
    {
        const long sensorId = 501;
        const long deviceId = 502;
        var settings = new SystemSettings { PrtgUrl = "https://private-source.invalid", PrtgEnabled = true };
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = "legacy-profile-fixture";
            policy.CoreSystemId = "legacy-profile-test-core";
            policy.SourceGeneration = "source-v1";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            policy.HostIds = [1]; policy.SensorIds = [sensorId];
            policy.SourceTimeZoneId = "UTC"; policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC"; policy.SourceCultureName = "en-US";
            policy.TimeBasisEvidenceReference = "saved-time-basis-proof";
        });
        using (var db = _backend.CreateContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId, Name = "fixture-device" });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = deviceId,
                SensorType = "disk", Category = PrtgSensorCategories.Disk });
            db.DailyRecords.Add(Parent(5, "{\"LogSource\":2}"));
            db.SaveChanges();
        }
        NetiqSourceAttempt.RecordBatch(_backend, [1L], DateTime.Today.AddDays(-1), 1, "success");
        _backend.Blob(PrtgTrustedSamplingProfile.StorePrefix + sensorId).Mutate(_ =>
            ("{\"SensorObjid\":501,\"SourceGeneration\":\"old\",\"MetadataDigest\":\"legacy-digest\"}", true));

        var profiles = new PrtgTrustedSamplingProfileStore(_backend).GetMany([sensorId]);
        Assert.Empty(profiles); // Invalid legacy material never becomes profile authority.
        var facts = await PrtgProbeLocalFactsCollector.CollectAsync(_backend, settings, null, CancellationToken.None);

        Assert.Equal("observed", facts.Status);
        Assert.Equal("saved-explicit-authority", facts.AuthorityKind);
        Assert.Equal("UTC", facts.RawTimestampTimeZoneId);
        Assert.Equal(1, facts.PolicyHosts);
        Assert.Equal(1, facts.PolicySensors);
        Assert.Equal(1, facts.MirrorDevices);
        Assert.Equal(1, facts.MirrorSensors);
        Assert.Equal(1, facts.ReadinessSampledSensors);
        Assert.Equal(0, facts.QualifiedProfiles);
        Assert.Equal("sampled-profile-qualification-incomplete", facts.ReadinessReason);
        Assert.Equal(1, facts.NetiqReceipts);
        Assert.Equal(1, facts.NetiqSuccess);
        Assert.Equal(1, facts.NetiqParentRows);
        Assert.DoesNotContain("legacy-digest", JsonSerializer.Serialize(facts));
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotCallAi()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var ai = new FakeAi();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrtgProbeLocalFactsCollector.CollectAsync(_backend, new SystemSettings(), ai, cts.Token));
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task SqliteProgressHandlerInterruptsSynchronousVmWorkAndIsRemovedOnDispose()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var timer = Stopwatch.StartNew();

        using (new PrtgProbeSqliteProgressDeadline(connection, cancellation.Token))
        {
            var interrupted = await Task.Run(() =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 100000000) SELECT sum(value) FROM numbers";
                try { command.ExecuteScalar(); return false; }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { return true; }
            });

            Assert.True(interrupted, "SQLite should abort synchronous VM execution after cancellation.");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {timer.Elapsed}.");
        }

        using var afterDispose = connection.CreateCommand();
        afterDispose.CommandText = "SELECT 42";
        Assert.Equal(42L, (long)afterDispose.ExecuteScalar()!);
    }

    [Fact]
    public async Task SqliteProgressDeadlineInterruptsVmWorkWithoutTimerCancellation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var timer = Stopwatch.StartNew();

        using (new PrtgProbeSqliteProgressDeadline(connection, CancellationToken.None,
            TimeSpan.FromMilliseconds(100)))
        {
            var interrupted = await Task.Run(() =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 100000000) SELECT sum(value) FROM numbers";
                try { command.ExecuteScalar(); return false; }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { return true; }
            });

            Assert.True(interrupted, "SQLite should abort synchronous VM execution at the monotonic deadline.");
            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CallerCancellationDuringCollectorLockWaitReturnsWithinFiveSeconds()
    {
        var path = Path.Combine(_root, "locked.db");
        var backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", SqliteWal = false,
            ConnectionString = $"Data Source={path};Pooling=False"
        }, _root);
        using (var db = backend.CreateContext()) db.Database.EnsureCreated();
        using var locker = new SqliteConnection($"Data Source={path};Pooling=False");
        locker.Open();
        using (var mode = locker.CreateCommand())
        { mode.CommandText = "PRAGMA journal_mode=DELETE"; mode.ExecuteScalar(); }
        using (var begin = locker.CreateCommand())
        { begin.CommandText = "BEGIN EXCLUSIVE"; begin.ExecuteNonQuery(); }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrtgProbeLocalFactsCollector.CollectAsync(
            backend, new SystemSettings(), null, cancellation.Token));
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Caller cancellation during a lock wait took {timer.Elapsed}.");
    }

    [Fact]
    public void ProfileQualificationUsesPersistedCurrentStrategyEffectiveHour()
    {
        var policy = new PrtgMonitoringPolicy
        {
            SourceGeneration = "source-v1", RawTimestampTimeZoneId = "UTC",
            TimeBasisEvidenceReference = "time-basis-proof", SourceTimeZoneId = "UTC",
            AnalysisTimeZoneId = "UTC"
        };
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy, PrtgFetchStrategy.Conservative, PrtgFetchStrategy.ConservativeIntervalMinutes,
                DateTime.UtcNow);
        Assert.True(strategy.Ready);
        var current = new PrtgTrustedSamplingProfile(101, "source-v1", "resource-v1", 1, "channel-v1",
            "cpu", "3", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "v1",
            strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromMinutes(15), "minutes", "UTC", "UTC", "UTC", DateTimeOffset.UtcNow,
            "metadata-ref", "sample-ref", true, 1, 1, DateTime.UtcNow.ToOADate(), DateTime.UtcNow.ToOADate(), "digest");

        Assert.True(PrtgProbeLocalFactsCollector.ProfileMatchesCurrentStrategy(current, strategy));
        Assert.False(PrtgProbeLocalFactsCollector.ProfileMatchesCurrentStrategy(
            current with { StrategyEffectiveFromHourUtc = strategy.EffectiveFromHourUtc.AddHours(-1) }, strategy));
        Assert.False(PrtgProbeLocalFactsCollector.ProfileMatchesCurrentStrategy(
            current with { StrategyFingerprint = "stale-fingerprint" }, strategy));
        Assert.False(PrtgProbeLocalFactsCollector.ProfileMatchesCurrentStrategy(current,
            strategy with { MissingFacts = ["strategy_fence_stale"] }));
    }

    private static DailyRecordRow Parent(long id, string json) => new()
        { HostId = id, HostName = "private-host", RecordDate = DateTime.Today.AddDays(-1), ContentJson = json, CreatedAt = DateTime.Now };
    private sealed class FakeAi(bool fail = false) : IAiProbeService
    {
        public int Calls { get; private set; }
        public AiProbeResult LatestResult => new(true, AiProbeStatus.Ready, "secret-ai-detail", DateTime.UtcNow);
        public Task<AiProbeResult> RefreshAsync(CancellationToken ct = default)
        { Calls++; ct.ThrowIfCancellationRequested(); return fail ? Task.FromException<AiProbeResult>(new Exception("secret-token")) : Task.FromResult(LatestResult); }
    }
    public void Dispose()
    { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
