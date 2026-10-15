using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// UTF-16 caps follow each document's actual typed maximums, with JSON escaping budgeted at
/// six output characters per input code unit: strategy has a 64-hex fingerprint, normalized
/// 10/11-char name, 5/15 minutes, two <=128-char timezone IDs and one DateTime (under 8 KiB);
/// timeline has a 32-char GUID, <=256-char policy source generation, 64-hex scope hash, fixed
/// producer outcome/reason literals, fixed-width dates/numbers and at most one previous summary
/// (under 8 KiB including both escaped 256-unit source generations). Timeline producer audit found no exception text persisted: the only reason
/// variants are the two capacity literals, and round outcomes are the finite Complete() literals
/// plus "no-selected-sensors"/"timeline-bootstrap-capacity-shortfall".
/// A hint has at most 128 rows; per row strings are reason<=96, profile hash<=64, five authority
/// strings<=128, epoch<=64 and 16 fact codes<=64 ASCII (under 2 MiB for the full document).
/// Episodes have at most 128 rows plus 128 watermarks; each episode has two reasons with
/// summary<=256 and rule id<=128, three generations<=128 and epoch<=64; watermark strings have
/// the same generation bounds and four 64-hex fingerprints (under 2 MiB combined).
/// </summary>
public sealed class PrtgBoundedResourceStateStoreTests : IDisposable
{
    private const long HostId = 54_001;
    private const long SensorId = 54_002;
    private readonly EfSqliteFixture _fixture = new();

    [Fact]
    public void FourResourceStateStoresRoundTripSmallProducerShapedDocuments()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        var episodeStore = new PrtgResourceFormalEpisodeStore(_fixture.Blob(
            PrtgResourceFormalEpisodeStore.BlobKey(HostId)));
        var watermark = new PrtgResourceFormalEvidenceWatermark(HostId, 54_003, SensorId,
            "source-generation", "resource-generation", "channel-generation", "1", now, now,
            Hash('A'), Hash('B'), Hash('C'), Hash('D'));
        episodeStore.Update(document => document.Watermarks.Add(watermark));
        Assert.Equal(watermark, Assert.Single(episodeStore.Get().Watermarks));

        var hintStore = new PrtgResourcePressureHintStore(_fixture.Blob(
            PrtgResourcePressureHintStore.BlobKey(HostId)));
        var hint = new PrtgResourcePressureHint(HostId, 54_003, SensorId, PrtgResourceFamily.Cpu,
            PrtgResourceDecisionKind.NoHit, "two-hour-pressure-threshold-not-met", now, Hash('A'),
            "source-generation", "resource-generation", "channel-generation", "1", "semantic-v1",
            "strategy-v1", 80, 81, 100, now, MissingFacts: ["sample_coverage"]);
        Assert.True(hintStore.Upsert(hint));
        var persistedHint = Assert.Single(hintStore.GetCurrent(HostId, now));
        Assert.Equal(hint with { MissingFacts = null }, persistedHint with { MissingFacts = null });
        Assert.Equal(new[] { "sample_coverage" }, persistedHint.MissingFacts);

        var timelineStore = new PrtgSensorTimelineProgressStore(_fixture.Blob(
            PrtgSensorTimelineProgressStore.BlobKey));
        timelineStore.Update(progress =>
        {
            progress.BootstrapCycleId = Guid.NewGuid().ToString("N");
            progress.BootstrapSourceGeneration = "source-generation";
            progress.BootstrapScopeFingerprint = Hash('A');
            progress.BootstrapCycleOutcome = "running";
            progress.BootstrapCycleReason = "bootstrap-running";
            progress.LastRoundOutcome = "monitoring-not-ready";
        });
        var timeline = timelineStore.Get();
        Assert.Equal("bootstrap-running", timeline.BootstrapCycleReason);
        Assert.Equal("monitoring-not-ready", timeline.LastRoundOutcome);

        var policy = new PrtgMonitoringPolicy
        {
            SourceGeneration = "source-generation", SourceTimeZoneId = "UTC",
            RawTimestampTimeZoneId = "UTC", AnalysisTimeZoneId = "UTC",
            TimeBasisEvidenceReference = "fixture-evidence", RawAuthorityRevision = 1
        };
        var strategyBlob = _fixture.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey);
        var strategyStore = new PrtgTrustedSamplingStrategyStateStore(strategyBlob);
        var first = strategyStore.GetCurrent(policy, PrtgFetchStrategy.Conservative, 15, now);
        var second = strategyStore.GetCurrent(policy, PrtgFetchStrategy.Conservative, 15, now.AddHours(1));
        Assert.Equal(first, second);
        Assert.Equal(first, PrtgTrustedSamplingStrategyStateStore.ReadCurrent(strategyBlob.Read(), policy,
            PrtgFetchStrategy.Conservative, 15));
    }

    [Fact]
    public void FormalEpisodeMutationRejectsPhysicalNulWithoutChangingStoredBytesOrVersion()
    {
        var key = PrtgResourceFormalEpisodeStore.BlobKey(HostId);
        var blob = SeedPhysicalNul(key);
        var store = new PrtgResourceFormalEpisodeStore(blob);
        AssertPhysicalNulRetained(key, blob, () => store.Update(_ => { }));
    }

    [Fact]
    public void PressureHintMutationRejectsPhysicalNulWithoutChangingStoredBytesOrVersion()
    {
        var key = PrtgResourcePressureHintStore.BlobKey(HostId);
        var blob = SeedPhysicalNul(key);
        var store = new PrtgResourcePressureHintStore(blob);
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var hint = new PrtgResourcePressureHint(HostId, 54_003, SensorId, PrtgResourceFamily.Cpu,
            PrtgResourceDecisionKind.NoHit, "two-hour-pressure-threshold-not-met", now, Hash('A'),
            "source-generation", "resource-generation", "channel-generation", "1", "semantic-v1",
            "strategy-v1", 80, 81, 100);
        AssertPhysicalNulRetained(key, blob, () => store.Upsert(hint));
    }

    [Fact]
    public void TimelineMutationRejectsPhysicalNulWithoutChangingStoredBytesOrVersion()
    {
        var key = PrtgSensorTimelineProgressStore.BlobKey;
        var blob = SeedPhysicalNul(key);
        var store = new PrtgSensorTimelineProgressStore(blob);
        AssertPhysicalNulRetained(key, blob, () => store.Update(_ => { }));
    }

    [Fact]
    public void StrategyMutationRejectsPhysicalNulWithoutChangingStoredBytesOrVersion()
    {
        var key = PrtgTrustedSamplingStrategyStateStore.BlobKey;
        var blob = SeedPhysicalNul(key);
        var store = new PrtgTrustedSamplingStrategyStateStore(blob);
        var policy = new PrtgMonitoringPolicy
        {
            SourceGeneration = "source-generation", SourceTimeZoneId = "UTC",
            RawTimestampTimeZoneId = "UTC", AnalysisTimeZoneId = "UTC",
            TimeBasisEvidenceReference = "fixture-evidence", RawAuthorityRevision = 1
        };
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        AssertPhysicalNulRetained(key, blob, () => store.GetCurrent(policy,
            PrtgFetchStrategy.Conservative, 15, now));
    }

    [Fact]
    public void MalformedExistingJsonIsRejectedInsteadOfRecreatedAsMissing()
    {
        AssertMalformedUntouched(PrtgResourceFormalEpisodeStore.BlobKey(HostId), blob =>
            () => new PrtgResourceFormalEpisodeStore(blob).Update(_ => { }), typeof(System.Text.Json.JsonException));
        AssertMalformedUntouched(PrtgResourcePressureHintStore.BlobKey(HostId), blob =>
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            var hint = new PrtgResourcePressureHint(HostId, 54_003, SensorId, PrtgResourceFamily.Cpu,
                PrtgResourceDecisionKind.NoHit, "two-hour-pressure-threshold-not-met", now, Hash('A'),
                "source-generation", "resource-generation", "channel-generation", "1", "semantic-v1",
                "strategy-v1", 80, 81, 100);
            return () => new PrtgResourcePressureHintStore(blob).Upsert(hint);
        }, typeof(System.Text.Json.JsonException));
        AssertMalformedUntouched(PrtgSensorTimelineProgressStore.BlobKey, blob =>
            () => new PrtgSensorTimelineProgressStore(blob).Update(_ => { }), typeof(System.Text.Json.JsonException));
        AssertMalformedUntouched(PrtgTrustedSamplingStrategyStateStore.BlobKey, blob =>
        {
            var policy = new PrtgMonitoringPolicy
            {
                SourceGeneration = "source-generation", SourceTimeZoneId = "UTC",
                RawTimestampTimeZoneId = "UTC", AnalysisTimeZoneId = "UTC",
                TimeBasisEvidenceReference = "fixture-evidence", RawAuthorityRevision = 1
            };
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            return () => new PrtgTrustedSamplingStrategyStateStore(blob).GetCurrent(policy,
                PrtgFetchStrategy.Conservative, 15, now);
        }, typeof(InvalidDataException));
    }

    private EfJsonBlobStore SeedPhysicalNul(string key)
    {
        var blob = _fixture.Blob(key);
        blob.Mutate(_ => ("{}", true));
        var content = "{}\0hidden";
        var bytes = Encoding.UTF8.GetBytes(content);
        using var context = _fixture.NewContext();
        context.Database.ExecuteSqlInterpolated(
            $"UPDATE lf_blobs SET content=CAST({bytes} AS TEXT) WHERE blob_key={key}");
        Assert.Equal(bytes, ReadPhysicalBytes(key));
        return blob;
    }

    private void AssertPhysicalNulRetained(string key, EfJsonBlobStore blob, Action mutation)
    {
        var expected = Encoding.UTF8.GetBytes("{}\0hidden");
        var version = blob.ReadVersion();
        Assert.Throws<InvalidDataException>(mutation);
        Assert.Equal(version, blob.ReadVersion());
        Assert.Equal(expected, ReadPhysicalBytes(key));
    }

    private void AssertMalformedUntouched(string key, Func<EfJsonBlobStore, Action> mutationFactory,
        Type expectedException)
    {
        var blob = _fixture.Blob(key);
        const string invalid = "not-json";
        blob.Mutate(_ => (invalid, true));
        var version = blob.ReadVersion();
        var mutation = mutationFactory(blob);
        Assert.Equal(expectedException, Record.Exception(mutation)?.GetType());
        Assert.Equal(version, blob.ReadVersion());
        Assert.Equal(invalid, blob.Read());
    }

    private byte[] ReadPhysicalBytes(string key)
    {
        using var context = _fixture.NewContext();
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State == System.Data.ConnectionState.Closed;
        if (wasClosed) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(content AS BLOB) FROM lf_blobs WHERE blob_key=$key";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$key";
            parameter.Value = key;
            command.Parameters.Add(parameter);
            return (byte[])command.ExecuteScalar()!;
        }
        finally { if (wasClosed) connection.Close(); }
    }

    private static string Hash(char value) => new(value, 64);

    public void Dispose() => _fixture.Dispose();
}
