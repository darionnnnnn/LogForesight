using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgSnapshotAccumulatorTests
{
    [Fact]
    public void 同一小時三個樣本_平均極值與覆蓋率計算正確()
    {
        var acc = new PrtgSnapshotAccumulator();
        var h10 = new DateTime(2026, 9, 11, 10, 0, 0);
        var now = new DateTime(2026, 9, 11, 11, 0, 0);

        acc.Add(1001, h10.AddMinutes(5), 10.0);
        acc.Add(1001, h10.AddMinutes(20), 20.0);
        acc.Add(1001, h10.AddMinutes(35), 30.0);

        Assert.Equal(1, acc.BucketCount);
        Assert.Equal(3, acc.SampleCount);

        var rows = acc.DrainBefore(h10.AddHours(1), expectedSamplesPerHour: 12, now: now);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(1001, row.SensorObjid);
        Assert.Equal(h10, row.PeriodStart);
        Assert.Equal(20.0, row.AvgValue);
        Assert.Equal(10.0, row.MinValue);
        Assert.Equal(30.0, row.MaxValue);
        Assert.Equal(25.0, row.Coverage); // 3 / 12 * 100 = 25
        Assert.Equal(PrtgDataQuality.Sampled, row.Quality);
        Assert.Equal(now, row.CreatedAt);
    }

    [Fact]
    public void 跨小時不混算_各自獨立產出小時列()
    {
        var acc = new PrtgSnapshotAccumulator();
        var t1 = new DateTime(2026, 9, 11, 10, 5, 0);
        var t2 = new DateTime(2026, 9, 11, 11, 5, 0);
        var drainTime = new DateTime(2026, 9, 11, 12, 0, 0);

        acc.Add(2001, t1, 50.0);
        acc.Add(2001, t2, 80.0);

        Assert.Equal(2, acc.BucketCount);
        Assert.Equal(2, acc.SampleCount);

        var rows = acc.DrainBefore(drainTime, expectedSamplesPerHour: 12, now: drainTime);

        Assert.Equal(2, rows.Count);
        var r1 = rows.Single(r => r.PeriodStart == new DateTime(2026, 9, 11, 10, 0, 0));
        Assert.Equal(50.0, r1.AvgValue);
        Assert.Equal(50.0, r1.MinValue);
        Assert.Equal(50.0, r1.MaxValue);

        var r2 = rows.Single(r => r.PeriodStart == new DateTime(2026, 9, 11, 11, 0, 0));
        Assert.Equal(80.0, r2.AvgValue);
        Assert.Equal(80.0, r2.MinValue);
        Assert.Equal(80.0, r2.MaxValue);
    }

    [Fact]
    public void DrainBefore_只取早於界線之桶且當前小時保留_DrainAll全取()
    {
        var acc = new PrtgSnapshotAccumulator();
        var h10 = new DateTime(2026, 9, 11, 10, 0, 0);
        var h11 = new DateTime(2026, 9, 11, 11, 0, 0);

        acc.Add(3001, h10.AddMinutes(10), 1.0);
        acc.Add(3001, h11.AddMinutes(10), 2.0);

        Assert.Equal(2, acc.BucketCount);
        Assert.Equal(2, acc.SampleCount);

        // 抽取 11:00 之前（只取 10:00）
        var drainedBefore11 = acc.DrainBefore(h11, expectedSamplesPerHour: 12, now: h11);
        Assert.Single(drainedBefore11);
        Assert.Equal(h10, drainedBefore11[0].PeriodStart);

        // 累積器中仍保留 11:00 的桶
        Assert.Equal(1, acc.BucketCount);
        Assert.Equal(1, acc.SampleCount);

        // DrainAll 全取
        var drainedAll = acc.DrainAll(expectedSamplesPerHour: 12, now: h11.AddHours(1));
        Assert.Single(drainedAll);
        Assert.Equal(h11, drainedAll[0].PeriodStart);

        Assert.Equal(0, acc.BucketCount);
        Assert.Equal(0, acc.SampleCount);
    }

    [Fact]
    public void Coverage_超過期望樣本數時上限為100()
    {
        var acc = new PrtgSnapshotAccumulator();
        var h10 = new DateTime(2026, 9, 11, 10, 0, 0);

        for (var i = 0; i < 15; i++)
        {
            acc.Add(4001, h10.AddMinutes(i * 2), 10.0);
        }

        var rows = acc.DrainBefore(h10.AddHours(1), expectedSamplesPerHour: 12, now: h10.AddHours(1));
        Assert.Single(rows);
        Assert.Equal(100.0, rows[0].Coverage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Coverage_期望樣本數小於等於零時為null(int expectedSamples)
    {
        var acc = new PrtgSnapshotAccumulator();
        var h10 = new DateTime(2026, 9, 11, 10, 0, 0);

        acc.Add(5001, h10.AddMinutes(15), 42.0);

        var rows = acc.DrainBefore(h10.AddHours(1), expectedSamplesPerHour: expectedSamples, now: h10.AddHours(1));
        Assert.Single(rows);
        Assert.Null(rows[0].Coverage);
    }

    [Fact]
    public void Drain後累積器清空_BucketCount與SampleCount歸零()
    {
        var acc = new PrtgSnapshotAccumulator();
        var h10 = new DateTime(2026, 9, 11, 10, 0, 0);

        acc.Add(6001, h10.AddMinutes(5), 1.0);
        acc.Add(6002, h10.AddMinutes(10), 2.0);

        Assert.Equal(1, acc.BucketCount);
        Assert.Equal(2, acc.SampleCount);

        var rows = acc.DrainBefore(h10.AddHours(1), expectedSamplesPerHour: 12, now: h10.AddHours(1));
        Assert.Equal(2, rows.Count);

        Assert.Equal(0, acc.BucketCount);
        Assert.Equal(0, acc.SampleCount);

        // 再次 Drain 不拋錯且回傳空
        var empty = acc.DrainBefore(h10.AddHours(2), expectedSamplesPerHour: 12, now: h10.AddHours(2));
        Assert.Empty(empty);
    }

    [Fact]
    public void 多執行緒並行寫入與抽取_執行緒安全不漏失()
    {
        var acc = new PrtgSnapshotAccumulator();
        var baseTime = new DateTime(2026, 9, 11, 10, 0, 0);
        const int threads = 8;
        const int samplesPerThread = 500;

        Parallel.For(0, threads, threadId =>
        {
            for (var i = 0; i < samplesPerThread; i++)
            {
                acc.Add(threadId, baseTime.AddMinutes(i % 60), i);
            }
        });

        Assert.Equal(threads * samplesPerThread, acc.SampleCount);

        var all = acc.DrainAll(expectedSamplesPerHour: 60, now: DateTime.Now);
        Assert.Equal(threads, all.Count);
        Assert.Equal(0, acc.BucketCount);
        Assert.Equal(0, acc.SampleCount);
    }
}
