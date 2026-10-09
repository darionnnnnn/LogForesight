using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureHintStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    [Fact]
    public void Upsert_IsDurableAndExactReplayIsIdempotent()
    {
        var store = Store(20);
        var hint = Hint(20, DateTime.UtcNow);

        Assert.True(store.Upsert(hint));
        Assert.False(Store(20).Upsert(hint));
        var persisted = Assert.Single(Store(20).GetCurrent(20, hint.AsOfUtc.AddMinutes(1)));
        Assert.Equal(hint, persisted);
    }

    [Fact]
    public void OlderReplayCannotReplaceNewerResourceState()
    {
        var store = Store(20);
        var older = Hint(20, DateTime.UtcNow.AddMinutes(-10));
        var newer = Hint(20, older.AsOfUtc.AddMinutes(1)) with
        { Kind = PrtgResourceDecisionKind.Hit, ReasonCode = "both-completed-hour-thresholds-hit" };

        Assert.True(store.Upsert(newer));
        Assert.False(store.Upsert(older));
        Assert.Equal(newer, Assert.Single(store.GetCurrent(20, newer.AsOfUtc)));
    }

    [Fact]
    public void EvidenceCutoffWinsOverLaterProcessingAuthority()
    {
        var store = Store(20);
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        var newerEvidence = Hint(20, now) with { EvidenceAsOfUtc = now.AddMinutes(-5) };
        var laterProcessedOldEvidence = Hint(20, now.AddMinutes(2)) with
        {
            EvidenceAsOfUtc = now.AddMinutes(-10),
            Kind = PrtgResourceDecisionKind.Hit,
            ReasonCode = "both-completed-hour-thresholds-hit"
        };

        Assert.True(store.Upsert(newerEvidence));
        Assert.False(store.Upsert(laterProcessedOldEvidence));
        Assert.Equal(newerEvidence, Assert.Single(store.GetCurrent(20, now.AddMinutes(3))));
    }

    [Fact]
    public void SameEvidenceCanRefreshOnlyUnderNewerCurrentAuthority()
    {
        var store = Store(20);
        var evidenceAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-10), DateTimeKind.Utc);
        var first = Hint(20, evidenceAt.AddMinutes(1)) with { EvidenceAsOfUtc = evidenceAt };
        var refreshed = first with
        {
            AsOfUtc = first.AsOfUtc.AddMinutes(1),
            Kind = PrtgResourceDecisionKind.Hit,
            ReasonCode = "both-completed-hour-thresholds-hit"
        };
        var oldAuthorityReplay = first with
        {
            Kind = PrtgResourceDecisionKind.Recovery,
            ReasonCode = "both-completed-hour-averages-below-85"
        };

        Assert.True(store.Upsert(first));
        Assert.True(store.Upsert(refreshed));
        Assert.False(store.Upsert(oldAuthorityReplay));
        Assert.Equal(refreshed, Assert.Single(store.GetCurrent(20, refreshed.AsOfUtc)));
    }

    [Fact]
    public void ConflictingSameAsOfResultIsRejected()
    {
        var store = Store(20);
        var hint = Hint(20, DateTime.UtcNow);
        Assert.True(store.Upsert(hint));

        var conflicting = hint with { Kind = PrtgResourceDecisionKind.Hit,
            ReasonCode = "both-completed-hour-thresholds-hit" };
        Assert.Throws<InvalidDataException>(() => store.Upsert(conflicting));
    }

    [Fact]
    public void BoundedReadFiltersExpiredAndFutureHints()
    {
        var store = Store(20);
        var now = DateTime.UtcNow;
        store.Upsert(Hint(20, now.AddDays(-15)));
        store.Upsert(Hint(20, now.AddMinutes(1)));
        Assert.Empty(store.GetCurrent(20, now));
    }

    [Fact]
    public void HostScopedBlobCannotBeReadThroughAnotherHostKey()
    {
        var hint = Hint(20, DateTime.UtcNow);
        Assert.True(Store(20).Upsert(hint));
        Assert.Empty(Store(21).GetCurrent(21, hint.AsOfUtc.AddMinutes(1)));
    }

    [Fact]
    public void FreeFormReasonAndOutOfRangeMetricAreRejected()
    {
        var store = Store(20);
        var hint = Hint(20, DateTime.UtcNow);
        Assert.Throws<ArgumentException>(() => store.Upsert(hint with { ReasonCode = "host=secret.example" }));
        Assert.Throws<ArgumentException>(() => store.Upsert(hint with { LatestHourAveragePercent = 1000 }));
    }

    private PrtgResourcePressureHintStore Store(long hostId) =>
        new(_fixture.Blob(PrtgResourcePressureHintStore.BlobKey(hostId)));

    private static PrtgResourcePressureHint Hint(long hostId, DateTime asOfUtc) => new(
        hostId, 30, 40, PrtgResourceFamily.Cpu, PrtgResourceDecisionKind.NoHit,
        "two-hour-pressure-threshold-not-met", DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc),
        new string('A', 64), "source-1", "resource-1", "channel-1", "1", "semantic-1", "strategy-1",
        80, 81, 100);

    public void Dispose() => _fixture.Dispose();
}
