using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgSnapshotAccumulatorTests
{
    [Fact]
    public void Trusted重播拒絕偽造slot與超時到達且失敗不覆蓋原資料()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var accumulator = new PrtgSnapshotAccumulator();
        accumulator.AddTrusted(Trusted(hour, "physical-one", 10), hour.AddSeconds(10));
        var original = Assert.Single(accumulator.Capture());
        var proof = Assert.IsType<PrtgTrustedSampleProof>(original.Trusted);
        var slot = Assert.Single(proof.Slots);
        var forged = proof with { Slots = [slot with { Slot = 1 }] };
        Assert.Throws<InvalidDataException>(() => accumulator.Restore([original with { Trusted = forged }]));
        var late = proof with { Slots = [slot with { ReceivedAt = hour.AddMinutes(10) }] };
        Assert.Throws<InvalidDataException>(() => accumulator.Restore([original with { Trusted = late }]));
        var row = Assert.Single(accumulator.PreviewDrainAll(4, hour.AddHours(1)));
        Assert.Equal(25, row.Coverage);
        Assert.Equal(10, row.AvgValue);
    }

    [Fact]
    public void TrustedPhysicalConflictIsRejectedAtomicallyWithoutChangingValueOrCoverage()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var sample = Trusted(hour, "same-physical", 10);
        var acc = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(sample, hour.AddSeconds(10), out var reason));
        Assert.Null(reason);
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(sample with { Value = 20 }, hour.AddSeconds(10), out reason));
        Assert.Equal("physical_measurement_conflict", reason);
        Assert.Equal(PrtgTrustedSampleDisposition.Duplicate, acc.AddTrusted(sample, hour.AddSeconds(10), out reason));
        Assert.Null(reason);
        Assert.Equal(1, acc.SampleCount);
        var row = Assert.Single(acc.PreviewDrainAll(4, hour.AddHours(1)));
        Assert.Equal(10, row.AvgValue);
        Assert.Equal(25, row.Coverage);
    }

    private static PrtgTrustedSample Trusted(DateTime measured, string physicalId, double value = 1,
        int strategyMinutes = 15, PrtgTrustedSampleQuality quality = PrtgTrustedSampleQuality.Good,
        string resourceGeneration = "resource-a", DateTime? received = null) => new(
        77, value, "source-a", resourceGeneration, "channel-a", "epoch-a", "semantic-v1", "strategy-v1",
        strategyMinutes, DateTime.SpecifyKind(new DateTime(measured.Year, measured.Month, measured.Day, measured.Hour, 0, 0), DateTimeKind.Utc),
        DateTime.SpecifyKind(measured, DateTimeKind.Utc), DateTime.SpecifyKind(received ?? measured.AddSeconds(5), DateTimeKind.Utc),
        TimeSpan.FromMinutes(1), quality, physicalId, "UTC", "UTC");

    [Fact]
    public void Trusted入口_物理樣本去重時槽替換與checkpoint重播只計唯一slot()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var acc = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(Trusted(hour.AddSeconds(1), "p0", 10), hour.AddSeconds(11)));
        Assert.Equal(PrtgTrustedSampleDisposition.Duplicate, acc.AddTrusted(Trusted(hour.AddSeconds(1), "p0", 10), hour.AddSeconds(11)));
        Assert.Equal(PrtgTrustedSampleDisposition.Replaced, acc.AddTrusted(Trusted(hour.AddMinutes(3), "p0-new", 20), hour.AddMinutes(3).AddSeconds(10)));
        Assert.Equal(25.0, Assert.Single(acc.PreviewDrainAll(4, hour.AddMinutes(4))).Coverage!.Value);
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(Trusted(hour.AddMinutes(15), "p1", 40), hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(Trusted(hour.AddMinutes(30), "p2", 60), hour.AddMinutes(30).AddSeconds(10)));

        var restored = new PrtgSnapshotAccumulator();
        restored.Restore(acc.Capture());
        var row = Assert.Single(restored.DrainAll(12, hour.AddHours(1)));
        Assert.Equal(75.0, row.Coverage!.Value);
        Assert.Equal(40.0, row.AvgValue!.Value);
        Assert.Equal(20.0, row.MinValue!.Value);
        Assert.Equal(60.0, row.MaxValue!.Value);
        Assert.Equal(1, row.TrustVersion);
        Assert.Contains("resource-a", row.TrustedProof!);
        Assert.Equal(3, PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots.Count);
    }

    [Fact]
    public void Trusted入口_晚到陳舊未來Paused與跨scope樣本不增加coverage()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var acc = new PrtgSnapshotAccumulator();
        var asOf = hour.AddSeconds(10);
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(Trusted(hour, "good"), asOf));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(Trusted(hour.AddMinutes(15), "stale",
            received: hour.AddMinutes(15).AddSeconds(5)), hour.AddMinutes(19)));
        Assert.Equal(PrtgTrustedSampleDisposition.DeferredFuture, acc.AddTrusted(Trusted(hour.AddMinutes(30), "future"), hour.AddMinutes(29)));
        var futureAcc = new PrtgSnapshotAccumulator();
        var futureSample = Trusted(hour.AddMinutes(30), "future-later");
        Assert.Equal(PrtgTrustedSampleDisposition.DeferredFuture, futureAcc.AddTrusted(futureSample, hour.AddMinutes(29)));
        Assert.Equal(0, futureAcc.SampleCount);
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, futureAcc.AddTrusted(futureSample, futureSample.MeasuredAt.AddSeconds(10)));
        Assert.Equal(25.0, Assert.Single(futureAcc.DrainAll(4, hour.AddHours(1))).Coverage!.Value);
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(Trusted(hour.AddMinutes(15), "paused",
            quality: PrtgTrustedSampleQuality.Paused), hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(Trusted(hour.AddMinutes(15), "unknown",
            quality: PrtgTrustedSampleQuality.Unknown), hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(Trusted(hour.AddMinutes(15), "probe",
            quality: PrtgTrustedSampleQuality.ProbeUnavailable), hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(Trusted(hour.AddMinutes(15), "scope",
            resourceGeneration: "resource-b"), hour.AddMinutes(15).AddSeconds(10)));
        var baseline = Trusted(hour.AddMinutes(15), "identity-check");
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(baseline with { SourceGeneration = "source-b" }, hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(baseline with { ChannelGeneration = "channel-b" }, hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(baseline with { ResourceEpoch = "epoch-b" }, hour.AddMinutes(15).AddSeconds(10)));
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected, acc.AddTrusted(baseline with { StrategyVersion = "strategy-v2" }, hour.AddMinutes(15).AddSeconds(10)));
        var row = Assert.Single(acc.DrainAll(4, hour.AddHours(1)));
        Assert.Equal(25.0, row.Coverage!.Value);
        Assert.Equal(1, PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots.Count);
    }

    [Fact]
    public void Trusted滿載checkpoint_一萬五千感測器各十二slot恢復後完整結算()
    {
        const int sensorCount = 15_000;
        const int slotsPerHour = 12;
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var accumulator = new PrtgSnapshotAccumulator();
        var accepted = 0;
        for (long sensor = 1; sensor <= sensorCount; sensor++)
        for (var slot = 0; slot < slotsPerHour; slot++)
        {
            var measured = hour.AddMinutes(slot * 5).AddSeconds(1);
            var sample = new PrtgTrustedSample(sensor, slot + 1, "source-r1", "resource-r1", "channel-r1",
                "epoch-r1", "semantic-v1", "strategy-v1", 5, hour, measured, measured.AddSeconds(5),
                TimeSpan.FromMinutes(1), PrtgTrustedSampleQuality.Good, $"sensor-{sensor}-slot-{slot}", "UTC", "UTC");
            if (accumulator.AddTrusted(sample, measured.AddSeconds(10)) == PrtgTrustedSampleDisposition.Accepted)
                accepted++;
        }

        Assert.Equal(sensorCount * slotsPerHour, accepted);
        Assert.Equal(sensorCount * slotsPerHour, accumulator.SampleCount);
        Assert.Equal(sensorCount, accumulator.EntryCount);
        var checkpoint = accumulator.Capture();
        Assert.Equal(sensorCount, checkpoint.Count);

        var restored = new PrtgSnapshotAccumulator();
        restored.Restore(checkpoint);
        Assert.Equal(sensorCount * slotsPerHour, restored.SampleCount);
        var firstProof = Assert.IsType<PrtgTrustedSampleProof>(Assert.Single(
            checkpoint.Where(row => row.SensorObjid == 1)).Trusted);
        var proofReloaded = PrtgTrustedSampleProof.Deserialize(PrtgTrustedSampleProof.Serialize(firstProof));
        Assert.True(proofReloaded.IsStructurallyValid());
        Assert.True(proofReloaded.MatchesHour(DateTime.SpecifyKind(hour, DateTimeKind.Unspecified)));
        Assert.Equal(slotsPerHour, proofReloaded.Slots.Count);

        var summaries = restored.DrainAll(slotsPerHour, hour.AddHours(1));
        Assert.Equal(sensorCount, summaries.Count);
        Assert.All(summaries, row =>
        {
            Assert.Equal(100, row.Coverage);
            Assert.Equal(6.5, row.AvgValue);
            Assert.Equal(1, row.MinValue);
            Assert.Equal(12, row.MaxValue);
            Assert.True(PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).MatchesHour(row.PeriodStart));
        });
    }

    [Fact]
    public void Trusted策略變更只在後續完整小時採新分母且舊Add不可升級()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var acc = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, acc.AddTrusted(Trusted(hour, "five", strategyMinutes: 5), hour.AddMinutes(1)));
        var restarted = new PrtgSnapshotAccumulator();
        restarted.Restore(acc.Capture());
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, restarted.AddTrusted(Trusted(hour.AddHours(1), "fifteen",
            strategyMinutes: 15), hour.AddHours(1).AddMinutes(1)));
        restarted.Add(77, hour.AddHours(2), 99, 25);
        var rows = restarted.DrainAll(12, hour.AddHours(3));
        Assert.Equal(8.3333333333, rows.Single(r => r.PeriodStart == hour).Coverage!.Value, precision: 8);
        Assert.Equal(25.0, rows.Single(r => r.PeriodStart == hour.AddHours(1)).Coverage!.Value);
        Assert.Equal(1, rows.Single(r => r.PeriodStart == hour).TrustVersion);
        Assert.Equal(1, rows.Single(r => r.PeriodStart == hour.AddHours(1)).TrustVersion);
        Assert.Equal(0, rows.Single(r => r.PeriodStart == hour.AddHours(2)).TrustVersion);
    }

    [Fact]
    public void 恢復後策略改變_涵蓋率仍採各樣本當時頻率()
    {
        var hour = DateTime.Today;
        var first = new PrtgSnapshotAccumulator();
        first.Add(1, hour, 10, sampleCoverage: 25);
        var restored = new PrtgSnapshotAccumulator();
        restored.Restore(first.Capture());
        restored.Add(1, hour.AddMinutes(15), 20, sampleCoverage: 100.0 / 12);
        var row = Assert.Single(restored.DrainAll(12, hour.AddHours(1)));
        Assert.Equal(25 + 100.0 / 12, row.Coverage!.Value, precision: 8);
        Assert.Equal(15, row.AvgValue);
    }

    [Fact]
    public void 恢復資料缺採集時頻率_不得用新設定補算涵蓋率()
    {
        var hour = DateTime.Today;
        var restored = new PrtgSnapshotAccumulator();
        restored.Restore(new[] { new PrtgSnapshotAccumulator.CheckpointRow(hour, 1, 40, 4, 10, 10) });
        Assert.Null(Assert.Single(restored.DrainAll(4, hour.AddHours(1))).Coverage);
    }

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
