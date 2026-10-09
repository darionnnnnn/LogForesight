using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgDiskTrendEvaluatorTests
{
    private static DateOnly Start => new(2026, 1, 1);
    private static PrtgDiskTrendDay[] Declining(int count = 30) => Enumerable.Range(0, count)
        .Select(i => new PrtgDiskTrendDay(Start.AddDays(i), 45 - i)).ToArray();

    [Fact]
    public void 近期持續下降且低水位並在處理窗內命中()
    {
        var result = PrtgDiskTrendEvaluator.Evaluate(Declining());
        Assert.Equal(PrtgDiskTrendOutcome.Hit, result.Outcome);
        Assert.Equal(1d, result.RobustDeclinePercentagePointsPerDay!.Value, 5);
        Assert.Contains("暫定", result.Explanation);
        Assert.Contains("百分點／日", result.Explanation);
    }

    [Fact]
    public void 零資料為資料不足() => Assert.Equal(PrtgDiskTrendOutcome.InsufficientData,
        PrtgDiskTrendEvaluator.Evaluate(Array.Empty<PrtgDiskTrendDay>()).Outcome);

    [Fact]
    public void 僅六日不命中() => Assert.Equal(PrtgDiskTrendOutcome.InsufficientData,
        PrtgDiskTrendEvaluator.Evaluate(Declining(6)).Outcome);

    [Fact]
    public void 固定值不命中() => Assert.Equal(PrtgDiskTrendOutcome.NoHit,
        PrtgDiskTrendEvaluator.Evaluate(Enumerable.Range(0, 30).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), 10)).ToArray()).Outcome);

    [Fact]
    public void 上升趨勢不命中() => Assert.Equal(PrtgDiskTrendOutcome.NoHit,
        PrtgDiskTrendEvaluator.Evaluate(Enumerable.Range(0, 30).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), 5 + i)).ToArray()).Outcome);

    [Fact]
    public void 單日突降不會被當成持續趨勢()
    {
        var data = Enumerable.Range(0, 30).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), i == 29 ? 5 : 40)).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.NoHit, PrtgDiskTrendEvaluator.Evaluate(data).Outcome);
    }

    [Fact]
    public void 單日突升不會扭曲穩健斜率()
    {
        var data = Declining().Select((x, i) => i == 14 ? x with { AvailablePercent = 95 } : x).ToArray();
        var result = PrtgDiskTrendEvaluator.Evaluate(data);
        Assert.Equal(PrtgDiskTrendOutcome.Hit, result.Outcome);
        Assert.InRange(result.RobustDeclinePercentagePointsPerDay!.Value, 0.8, 1.2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void Null或越界百分比為資料不足(double? invalid)
    {
        var data = Declining();
        data[10] = data[10] with { AvailablePercent = invalid };
        Assert.Equal(PrtgDiskTrendOutcome.InsufficientData, PrtgDiskTrendEvaluator.Evaluate(data).Outcome);
    }

    [Fact]
    public void 同一輸入結果穩定()
    {
        var data = Declining().Reverse().ToArray();
        Assert.Equal(PrtgDiskTrendEvaluator.Evaluate(data), PrtgDiskTrendEvaluator.Evaluate(data));
    }

    [Theory]
    [InlineData(27, PrtgDiskTrendOutcome.Hit)]
    [InlineData(27.001, PrtgDiskTrendOutcome.NoHit)]
    [InlineData(26.99, PrtgDiskTrendOutcome.Hit)]
    public void 高於低水位但七日內降到百分之二十時提前預警(double latest, PrtgDiskTrendOutcome expected)
    {
        var data = Enumerable.Range(0, 28).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), latest + 27 - i)).ToArray();
        var result = PrtgDiskTrendEvaluator.Evaluate(data);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(latest - 20, result.EstimatedDaysToLowWater!.Value, 5);
        Assert.Equal(expected == PrtgDiskTrendOutcome.Hit ? PrtgDiskTrendHitReason.PredictedLowWater : PrtgDiskTrendHitReason.None, result.HitReasons);
        if (expected == PrtgDiskTrendOutcome.Hit)
        {
            Assert.Equal("predicted-low-water", Assert.Single(result.Reasons));
            Assert.Contains("降到低水位 20.0%", result.Explanation);
        }
    }

    [Fact]
    public void 提前預警不能降低二十八日準備度或略過持續下降品質()
    {
        var baseline = Enumerable.Range(0, 28).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), 54 - i)).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.Hit, PrtgDiskTrendEvaluator.Evaluate(baseline).Outcome);
        Assert.Equal(PrtgDiskTrendOutcome.InsufficientData,
            PrtgDiskTrendEvaluator.Evaluate(baseline.Skip(1).ToArray(), PrtgDiskTrendThresholds.Provisional with { MinimumValidDays = 2 }).Outcome);
        var singleDrop = baseline.Select((x, i) => x with { AvailablePercent = i == 27 ? 25 : 60 }).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.NoHit, PrtgDiskTrendEvaluator.Evaluate(singleDrop).Outcome);
        var slow = baseline.Select((x, i) => x with { AvailablePercent = 22.4 + (27 - i) * 0.4 }).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.NoHit, PrtgDiskTrendEvaluator.Evaluate(slow).Outcome);
        var noisy = baseline.Select((x, i) => x with { AvailablePercent = 54 - i + (i % 2 == 0 ? 2 : 0) }).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.NoHit, PrtgDiskTrendEvaluator.Evaluate(noisy).Outcome);
    }

    [Fact]
    public void 舊門檻JSON保留七日預設且提前時間修改影響命中()
    {
        var json = "{\"LowWaterPercent\":20,\"MinimumDeclinePercentagePointsPerDay\":0.5,\"MaximumDaysToDepletion\":30,\"MinimumValidDays\":28,\"RecentWindowDays\":35,\"MinimumDecliningDayRatio\":0.7}";
        var old = System.Text.Json.JsonSerializer.Deserialize<PrtgDiskTrendThresholds>(json)!;
        Assert.Equal(7, old.MaximumDaysToLowWater);
        var data = Enumerable.Range(0, 28).Select(i => new PrtgDiskTrendDay(Start.AddDays(i), 54 - i)).ToArray();
        Assert.Equal(PrtgDiskTrendOutcome.Hit, PrtgDiskTrendEvaluator.Evaluate(data, old).Outcome);
        Assert.Equal(PrtgDiskTrendOutcome.NoHit, PrtgDiskTrendEvaluator.Evaluate(data, old with { MaximumDaysToLowWater = 6 }).Outcome);
    }
}
