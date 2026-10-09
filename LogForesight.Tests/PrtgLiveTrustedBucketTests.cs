using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgLiveTrustedBucketTests
{
    private static PrtgTrustedSample Sample(long id, string analysisZone) => new(id, 20,
        "source", "resource", "channel", "4", "percent-v1", "strategy", 15,
        new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 5, 10, 45, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 5, 10, 45, 2, DateTimeKind.Utc), TimeSpan.FromMinutes(5),
        PrtgTrustedSampleQuality.Good, "physical-" + id, "UTC", analysisZone);

    [Fact]
    public void 不同analysis時區各自保留未完成小時_結算rows與keys完全相符()
    {
        var accumulator = new PrtgSnapshotAccumulator();
        var asOf = new DateTime(2026, 10, 5, 10, 46, 0, DateTimeKind.Utc);
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(Sample(1, "UTC"), asOf, out _));
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(Sample(2, "Taipei Standard Time"), asOf, out _));
        accumulator.Add(3, new DateTime(2026, 10, 5, 17, 45, 0), 90, 25);
        var serverCutoff = new DateTime(2026, 10, 5, 18, 0, 0);
        var initial = accumulator.PreviewDrainBeforeSources(serverCutoff, asOf, 4, serverCutoff);
        Assert.Equal(3, Assert.Single(initial).SensorObjid);
        Assert.Equal(3, Assert.Single(accumulator.KeysForDrainBeforeSources(serverCutoff, asOf)).SensorObjid);
        var complete = asOf.Date.AddHours(11);
        var rows = accumulator.PreviewDrainBeforeSources(serverCutoff, complete, 4, serverCutoff);
        var keys = accumulator.KeysForDrainBeforeSources(serverCutoff, complete);
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows.Select(row => (row.PeriodStart, row.SensorObjid)).Order(),
            keys.Select(key => (key.Hour, key.SensorObjid)).Order());
        Assert.Equal(1, rows.Single(row => row.SensorObjid == 1).TrustVersion);
        Assert.Equal(1, rows.Single(row => row.SensorObjid == 2).TrustVersion);
    }

    [Fact]
    public void 不合格diagnostic與近未來sample都不得清除已取得的可信slot()
    {
        var accumulator = new PrtgSnapshotAccumulator();
        var sample = Sample(1, "UTC");
        var asOf = sample.MeasuredAt.AddMinutes(1);
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(sample, asOf, out _));
        Assert.False(accumulator.TryAddDiagnostic(1, DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified), 90, 25));
        var future = sample with { MeasuredAt = asOf.AddMinutes(1), ReceivedAt = asOf,
            PhysicalMeasurementId = "future-physical" };
        Assert.Equal(PrtgTrustedSampleDisposition.DeferredFuture, accumulator.AddTrusted(future, asOf, out _));
        var persisted = Assert.Single(accumulator.PreviewDrainAll(4, asOf));
        Assert.Equal(1, persisted.TrustVersion);
        Assert.Equal(20, persisted.AvgValue);
        Assert.Equal(25, persisted.Coverage);
        Assert.Equal(1, accumulator.SampleCount);
    }
}
