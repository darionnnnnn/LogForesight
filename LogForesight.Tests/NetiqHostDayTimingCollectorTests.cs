using System.Diagnostics;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class NetiqHostDayTimingCollectorTests
{
    private static long TicksForMs(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);

    /// <summary>收集器是 singleton：逐主機日明細（最多 60 萬列）在趟結束後要能被回收，只留計數與 p95（第 77 輪 M-1）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 結束後逐主機日明細可被回收_計數與p95不變(bool viaComplete)
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(61);
        collector.AddPlanned(61, 100);
        for (var i = 1; i <= 100; i++)
            collector.RecordCommitted(61, i, new DateTime(2026, 10, 1), TicksForMs(i));

        var detail = MemoryProbe.Track(() => DetailOf(collector));
        if (viaComplete) collector.Complete(61, pipelineCompleted: true);
        else collector.Begin(61);   // 同一趟重入：標為不完整並結束

        Assert.True(MemoryProbe.IsCollected(detail), "結束後仍持有逐主機日明細");
        var read = collector.Read(61)!;
        Assert.Equal(100, read.ObservedHostDays);
        Assert.Equal(100, read.DurationSampleCount);
        if (viaComplete) Assert.Equal(95, read.P95Milliseconds!.Value, precision: 2);
        else Assert.Null(read.P95Milliseconds);

        // 結束後的記錄一律被拒，不會寫進新的空集合
        collector.RecordCommitted(61, 999, new DateTime(2026, 10, 1), TicksForMs(1));
        Assert.Equal(100, collector.Read(61)!.ObservedHostDays);
    }

    private static object DetailOf(NetiqHostDayTimingCollector collector)
    {
        var run = typeof(NetiqHostDayTimingCollector).GetField("_run", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(collector)!;
        return run.GetType().GetProperty("Rows")!.GetValue(run)!;
    }

    [Fact]
    public void Read_usesExactNearestRankP95OnlyAfterTerminalRun()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(51);
        collector.AddPlanned(51, 100);
        for (var i = 1; i <= 100; i++)
            collector.RecordCommitted(51, i, new DateTime(2026, 10, 1), TicksForMs(i));

        var running = collector.Read(51)!;
        Assert.Equal("running", running.Status);
        Assert.Null(running.P95Milliseconds);

        collector.Complete(51, pipelineCompleted: true);
        var completed = collector.Read(51)!;
        Assert.Equal("complete", completed.Status);
        Assert.Equal(100, completed.ExpectedHostDays);
        Assert.Equal(100, completed.CommittedHostDays);
        Assert.Equal(100, completed.DurationSampleCount);
        Assert.Equal(95, completed.P95Milliseconds!.Value, precision: 2);
        for (var i = 0; i < 5; i++)
            Assert.Equal(completed.P95Milliseconds, collector.Read(51)!.P95Milliseconds);
    }

    [Fact]
    public void Begin_sameCompletedRunInvalidatesCachedP95WithoutChangingPriorCounts()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(60);
        collector.AddPlanned(60, 2);
        collector.RecordCommitted(60, 1, new DateTime(2026, 10, 1), TicksForMs(4));
        collector.RecordCommitted(60, 2, new DateTime(2026, 10, 1), TicksForMs(8));
        collector.Complete(60, pipelineCompleted: true);
        Assert.NotNull(collector.Read(60)!.P95Milliseconds);

        collector.Begin(60);
        var replay = collector.Read(60)!;
        Assert.Equal("incomplete", replay.Status);
        Assert.Equal(2, replay.ExpectedHostDays);
        Assert.Equal(2, replay.CommittedHostDays);
        Assert.Null(replay.P95Milliseconds);
    }

    [Fact]
    public void Read_withSourceFailure_hasNoP95()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(52);
        collector.AddPlanned(52, 2);
        collector.RecordCommitted(52, 1, new DateTime(2026, 10, 1), TicksForMs(4));
        collector.RecordSourceFailure(52, 2, new DateTime(2026, 10, 1));
        collector.Complete(52, pipelineCompleted: true);

        var snapshot = collector.Read(52)!;
        Assert.Equal("incomplete", snapshot.Status);
        Assert.Equal(1, snapshot.SourceFailedHostDays);
        Assert.Null(snapshot.P95Milliseconds);
    }

    [Fact]
    public void Read_withDuplicateOrMissingHostDay_hasNoP95()
    {
        var duplicateCollector = new NetiqHostDayTimingCollector();
        duplicateCollector.Begin(53);
        duplicateCollector.AddPlanned(53, 1);
        duplicateCollector.RecordCommitted(53, 1, new DateTime(2026, 10, 1), TicksForMs(4));
        duplicateCollector.RecordCommitted(53, 1, new DateTime(2026, 10, 1), TicksForMs(5));
        duplicateCollector.Complete(53, pipelineCompleted: true);
        Assert.Equal(1, duplicateCollector.Read(53)!.DuplicateHostDays);
        Assert.Null(duplicateCollector.Read(53)!.P95Milliseconds);

        var missingCollector = new NetiqHostDayTimingCollector();
        missingCollector.Begin(54);
        missingCollector.AddPlanned(54, 2);
        missingCollector.RecordCommitted(54, 1, new DateTime(2026, 10, 1), TicksForMs(4));
        missingCollector.Complete(54, pipelineCompleted: true);
        Assert.Equal(1, missingCollector.Read(54)!.ObservedHostDays);
        Assert.Equal(1, missingCollector.Read(54)!.UnknownHostDays);
        Assert.Null(missingCollector.Read(54)!.P95Milliseconds);
    }

    [Fact]
    public void Read_afterFailedFinalizationIsImmutableAndNotRunning()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(56);
        collector.AddPlanned(56, 1);
        collector.RecordCommitted(56, 1, new DateTime(2026, 10, 1), TicksForMs(7));
        collector.Complete(56, pipelineCompleted: false);

        var failed = collector.Read(56)!;
        Assert.Equal("incomplete", failed.Status);
        Assert.Null(failed.P95Milliseconds);
        collector.RecordCommitted(56, 2, new DateTime(2026, 10, 1), TicksForMs(9));
        collector.AddPlanned(56, 4);
        collector.Complete(56, pipelineCompleted: true);

        var afterLateWrites = collector.Read(56)!;
        Assert.Equal(failed, afterLateWrites);
    }

    [Fact]
    public void Begin_sameRunIdCannotEraseExistingEvidenceAndEmptyRunHasNoP95()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(57);
        collector.AddPlanned(57, 1);
        collector.RecordCommitted(57, 1, new DateTime(2026, 10, 1), TicksForMs(3));
        collector.Begin(57);
        var repeated = collector.Read(57)!;
        Assert.Equal("incomplete", repeated.Status);
        Assert.Equal(1, repeated.ExpectedHostDays);
        Assert.Equal(1, repeated.CommittedHostDays);
        Assert.Null(repeated.P95Milliseconds);

        collector.Begin(58);
        collector.Complete(58, pipelineCompleted: true);
        var empty = collector.Read(58)!;
        Assert.Equal("incomplete", empty.Status);
        Assert.Null(empty.P95Milliseconds);
    }

    [Fact]
    public void MissingBatchRunRegistrationDoesNotStartTimingOrReusePreviousSnapshot()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(59);
        collector.AddPlanned(59, 1);
        collector.RecordCommitted(59, 1, new DateTime(2026, 10, 1), TicksForMs(8));
        collector.Complete(59, pipelineCompleted: true);
        var previous = collector.Read()!;

        using var recorder = new BatchRunRecorder(null, "test-host", Array.Empty<string>());
        Assert.Equal(0, recorder.RunId);
        Assert.Null(AnalysisOrchestrator.TryBeginHostDayTiming(recorder.RunId, collector));
        Assert.Equal(previous, collector.Read());
        Assert.Null(collector.Read(0));
    }

    [Fact]
    public void Read_afterOverflow_isIncompleteAndBounded()
    {
        var collector = new NetiqHostDayTimingCollector();
        collector.Begin(55);
        collector.AddPlanned(55, NetiqHostDayTimingCollector.MaximumExactHostDays + 1);
        collector.Complete(55, pipelineCompleted: true);

        var snapshot = collector.Read(55)!;
        Assert.True(snapshot.Overflow);
        Assert.Equal("incomplete", snapshot.Status);
        Assert.Null(snapshot.P95Milliseconds);
    }
}
