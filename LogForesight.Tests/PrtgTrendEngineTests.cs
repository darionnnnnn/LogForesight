using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgTrendEngineTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    #region 記憶體規則測試（EvaluateMemoryUsed）

    [Fact]
    public void 記憶體_正例1_恰達門檻命中_預估天數恰好七天()
    {
        // 28 日，每日上升 1.0 百分點，當前值 83.0%，(90 - 83) / 1.0 = 7.0 日
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 83.0 - (27 - i) * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(7.0, result.EstimatedDaysToTarget!.Value, 5);
        Assert.Equal(1.0, result.WorseningSlopePerDay!.Value, 5);
        Assert.StartsWith("近期 28 個有效日", result.Explanation);
        Assert.EndsWith("門檻為暫定，尚未實機校準。", result.Explanation);
        Assert.Contains("目前 83.0%", result.Explanation);
        Assert.Contains("穩健上升 1.00 百分點／日", result.Explanation);
        Assert.Contains("惡化日比例 100%", result.Explanation);
        Assert.Contains("預估 7.0 日達到門檻 90.0%", result.Explanation);
    }

    [Fact]
    public void 記憶體_正例2_明顯惡化命中()
    {
        // 28 日，每日上升 0.6 百分點，從 70.0% 到 86.2%，預估約 6.3 日達 90%
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 70.0 + i * 0.6))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.True(result.EstimatedDaysToTarget < 7.0);
        Assert.True(result.WorseningSlopePerDay >= 0.3);
    }

    [Fact]
    public void 記憶體_正例3_當前值已超過門檻_預估天數為零()
    {
        // 28 日，最後一日達到 93.5%（已超過 90% 門檻）
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 80.0 + i * 0.5))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.0, result.EstimatedDaysToTarget);
        Assert.Equal(93.5, result.CurrentValue!.Value, 5);
        Assert.Contains("預估 0.0 日達到門檻 90.0%", result.Explanation);
    }

    [Fact]
    public void 記憶體_正例4_斜率恰等於下限_命中()
    {
        // 每日上升恰為 0.3 百分點（MinimumSlopePerDay = 0.3）
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 88.5 - (27 - i) * 0.3))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.3, result.WorseningSlopePerDay!.Value, 5);
        Assert.Equal(5.0, result.EstimatedDaysToTarget!.Value, 5);
    }

    [Fact]
    public void 記憶體_正例5_惡化比例恰等於下限_命中()
    {
        // 31 日（30 次相鄰日變化），恰好 21 次上升、9 次平坦（變化為 0）
        // 21 / 30 = 0.70（恰等於 MinimumWorseningDayRatio = 0.70）
        var flatIndices = new HashSet<int> { 3, 6, 9, 12, 15, 18, 21, 24, 27 };
        var list = new List<(DateOnly Day, double UsedPercent)>();
        double current = 78.0;
        list.Add((Start, current));

        for (int i = 1; i <= 30; i++)
        {
            if (!flatIndices.Contains(i))
            {
                current += 0.5;
            }
            list.Add((Start.AddDays(i), current));
        }

        var result = PrtgTrendEngine.EvaluateMemoryUsed(list, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.70, result.WorseningDayRatio!.Value, 5);
        Assert.True(result.EstimatedDaysToTarget <= 7.0);
    }

    [Fact]
    public void 記憶體_反例1_門檻前一點點不命中_預估天數超出半天()
    {
        // (90 - 82.5) / 1.0 = 7.5 日，剛好超出 HorizonDays (7) 0.5 日
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 82.5 - (27 - i) * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
        Assert.Equal(7.5, result.EstimatedDaysToTarget!.Value, 5);
        Assert.Contains("超出 7.0 日預測期", result.Explanation);
    }

    [Fact]
    public void 記憶體_反例2_單日暴衝不命中()
    {
        // 27 日平坦（20%），第 28 日暴衝至 95%
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 27 ? 95.0 : 20.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
    }

    [Fact]
    public void 記憶體_反例3_低位平坦不命中()
    {
        // 28 日平坦低位 25%
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 25.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
        Assert.Equal(0.0, result.WorseningSlopePerDay!.Value, 5);
    }

    [Fact]
    public void 記憶體_反例4_有效日不足二十八日_資料不足()
    {
        var data = Enumerable.Range(0, 27)
            .Select(i => (Start.AddDays(i), 80.0 + i * 0.5))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("27／28", result.Explanation);
    }

    [Fact]
    public void 記憶體_反例5_重複日期_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i == 27 ? 26 : i), 80.0 + i * 0.5))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("重複日期", result.Explanation);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    public void 記憶體_反例6_範圍外數值_資料不足(double invalidValue)
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 10 ? invalidValue : 80.0 + i * 0.5))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
    }

    [Fact]
    public void 記憶體_反例7_門檻無效_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 80.0 + i * 0.5))
            .ToArray();

        var invalidThreshold = PrtgTrendThresholds.MemoryProvisional with { WindowDays = 10 };
        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, invalidThreshold);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("門檻無效", result.Explanation);
    }

    [Fact]
    public void 記憶體_同一輸入跑兩次結果相等()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 70.0 + i * 0.6))
            .ToArray();

        var r1 = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);
        var r2 = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(r1, r2);
    }

    [Fact]
    public void 記憶體_方向測試_可用百分比持續下降不可誤判為已用百分比上升()
    {
        // 方向寫反會讓下降的可用 % 被當成改善
        // 可用空間從 80% 持續下降到 53%，若錯誤判定為 HigherIsWorse 的惡化則會錯誤命中
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 80.0 - i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateMemoryUsed(data, PrtgTrendThresholds.MemoryProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
        Assert.True(result.WorseningSlopePerDay < 0);
        Assert.Equal(0.0, result.WorseningDayRatio);
    }

    #endregion

    #region CPU 規則測試（EvaluateCpuLoad）

    [Fact]
    public void CPU_正例1_恰達門檻命中_近期七日平均恰好七十()
    {
        // 28 日，前 21 日穩步上升，最後 7 日為 67, 68, 69, 70, 71, 72, 73
        // 平均 (67+68+69+70+71+72+73)/7 = 70.0
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 46.0 + i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.StartsWith("近期 28 個有效日", result.Explanation);
        Assert.EndsWith("門檻為暫定，尚未實機校準。", result.Explanation);
        Assert.Contains("目前 73.0%", result.Explanation);
        Assert.Contains("近 7 日平均 70.0%", result.Explanation);
        Assert.Contains("穩健上升 1.00 百分點／日", result.Explanation);
        Assert.Contains("惡化日比例 100%", result.Explanation);
    }

    [Fact]
    public void CPU_正例2_明顯惡化命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 50.0 + i * 1.2))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.True(result.WorseningSlopePerDay >= 0.3);
    }

    [Fact]
    public void CPU_正例3_當前值與近期平均已超過門檻_命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 60.0 + i * 0.8))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.True(result.CurrentValue >= 70.0);
    }

    [Fact]
    public void CPU_正例4_斜率恰等於下限_命中()
    {
        // 斜率恰好 0.3，近期平均大於 70
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 63.0 + i * 0.3))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.3, result.WorseningSlopePerDay!.Value, 5);
    }

    [Fact]
    public void CPU_正例5_惡化比例恰等於下限_命中()
    {
        // 31 日，恰 21 次上升、9 次平坦（21 / 30 = 0.70）
        var flatIndices = new HashSet<int> { 3, 6, 9, 12, 15, 18, 21, 24, 27 };
        var list = new List<(DateOnly Day, double UsedPercent)>();
        double current = 62.0;
        list.Add((Start, current));

        for (int i = 1; i <= 30; i++)
        {
            if (!flatIndices.Contains(i))
            {
                current += 0.5;
            }
            list.Add((Start.AddDays(i), current));
        }

        var result = PrtgTrendEngine.EvaluateCpuLoad(list, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.70, result.WorseningDayRatio!.Value, 5);
    }

    [Fact]
    public void CPU_反例1_近期平均距門檻差一點點_不命中()
    {
        // 最後 7 日平均為 69.9%，剛好低於 70.0% 門檻
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 27 ? 72.3 : 46.0 + i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
        Assert.Contains("69.9%", result.Explanation);
        Assert.Contains("未達門檻 70.0%", result.Explanation);
    }

    [Fact]
    public void CPU_反例2_單日暴衝不命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 27 ? 95.0 : 20.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
    }

    [Fact]
    public void CPU_反例3_低位平坦不命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 15.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
    }

    [Fact]
    public void CPU_反例4_有效日二十七天_資料不足()
    {
        var data = Enumerable.Range(0, 27)
            .Select(i => (Start.AddDays(i), 50.0 + i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("27／28", result.Explanation);
    }

    [Fact]
    public void CPU_反例5_重複日期_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i == 27 ? 26 : i), 50.0 + i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("重複日期", result.Explanation);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    [InlineData(double.PositiveInfinity)]
    public void CPU_反例6_範圍外數值_資料不足(double invalidValue)
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 5 ? invalidValue : 50.0 + i * 1.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
    }

    [Fact]
    public void CPU_反例7_門檻無效_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 50.0 + i * 1.0))
            .ToArray();

        var invalidThreshold = PrtgTrendThresholds.CpuProvisional with { TargetValue = -1 };
        var result = PrtgTrendEngine.EvaluateCpuLoad(data, invalidThreshold);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("門檻無效", result.Explanation);
    }

    [Fact]
    public void CPU_同一輸入跑兩次結果相等()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 50.0 + i * 1.0))
            .ToArray();

        var r1 = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);
        var r2 = PrtgTrendEngine.EvaluateCpuLoad(data, PrtgTrendThresholds.CpuProvisional);

        Assert.Equal(r1, r2);
    }

    #endregion

    #region Ping 規則測試（EvaluatePingLatency）

    [Fact]
    public void Ping_正例1_恰達門檻命中_近期中位數恰好五十()
    {
        // 28 日：基準期 21 日中位數 30 ms；最後 7 日為 47, 48, 49, 50, 51, 52, 53 ms（中位數 50.0 ms）
        // 50 >= 30 * 1.5 (45.0) 且 50 >= 50
        var data = Enumerable.Range(0, 28).Select(i =>
        {
            double val = i < 21 ? 20.0 + i * 1.0 : 47.0 + (i - 21);
            return (Start.AddDays(i), val);
        }).ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(50.0, result.CurrentValue!.Value, 5);
        Assert.StartsWith("近期 28 個有效日", result.Explanation);
        Assert.EndsWith("門檻為暫定，尚未實機校準。", result.Explanation);
        Assert.Contains("目前 50.0 ms", result.Explanation);
        Assert.Contains("基準期中位數 30.0 ms", result.Explanation);
        Assert.Contains("惡化日比例 100%", result.Explanation);
    }

    [Fact]
    public void Ping_正例2_明顯惡化命中()
    {
        // 基準期 20 ms，近期中位數 80 ms
        var data = Enumerable.Range(0, 28).Select(i =>
        {
            double val = i < 21 ? 20.0 : 80.0 + (i - 21);
            return (Start.AddDays(i), val);
        }).ToArray();

        // 加上少量正向變化以符合 worseningRatio
        var adjusted = data.Select((x, i) => (x.Item1, 20.0 + i * 2.0)).ToArray();
        var result = PrtgTrendEngine.EvaluatePingLatency(adjusted, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.True(result.CurrentValue >= 50.0);
    }

    [Fact]
    public void Ping_正例3_當前值遠超門檻_命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 30.0 + i * 2.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.True(result.CurrentValue > 70.0);
    }

    [Fact]
    public void Ping_正例4_倍數恰等於門檻_命中()
    {
        // 基準期中位數 = 33.333333333333336 ms，近期中位數 = 50.0 ms
        // 50.0 >= 33.333333333333336 * 1.5 (50.0)
        var data = Enumerable.Range(0, 28).Select(i =>
        {
            double val = i < 21
                ? 23.333333333333336 + i * 1.0 // 中位數為 i=10 時的 33.333333333333336
                : 47.0 + (i - 21);             // 中位數為 50.0
            return (Start.AddDays(i), val);
        }).ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(50.0, result.CurrentValue!.Value, 5);
    }

    [Fact]
    public void Ping_正例5_惡化比例恰等於下限_命中()
    {
        // 31 日（30 次相鄰日變化），恰好 18 次上升、12 次平坦（18 / 30 = 0.60）
        // 基準期（前 24 日）中位數約 23 ms，近期（後 7 日）中位數約 53 ms
        var list = new List<(DateOnly Day, double Milliseconds)>();
        double current = 20.0;
        list.Add((Start, current));

        // 前 23 步（基準期內部）：12 次 +0.5 ms，11 次平坦
        for (int i = 1; i <= 23; i++)
        {
            if (i % 2 == 1) current += 0.5;
            list.Add((Start.AddDays(i), current));
        }

        // 第 24 步（進入近期窗口的第 1 天）：上升 +25 ms（第 13 次上升）
        current += 25.0;
        list.Add((Start.AddDays(24), current));

        // 第 25~30 步（近期窗口其餘 6 步）：5 次 +1.0 ms，1 次平坦（第 14~18 次上升，第 12 次平坦）
        for (int i = 25; i <= 30; i++)
        {
            if (i != 28) current += 1.0;
            list.Add((Start.AddDays(i), current));
        }

        var result = PrtgTrendEngine.EvaluatePingLatency(list, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.Hit, result.Outcome);
        Assert.Equal(0.60, result.WorseningDayRatio!.Value, 5);
    }

    [Fact]
    public void Ping_反例1_近期中位數距門檻差一點點_不命中()
    {
        // 基準期中位數 30 ms，近期中位數 49.9 ms（未達 50.0 ms）
        var data = Enumerable.Range(0, 28).Select(i =>
        {
            double val = i < 21
                ? 20.0 + i * 1.0
                : 46.9 + (i - 21); // 中位數 49.9
            return (Start.AddDays(i), val);
        }).ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
        Assert.Contains("49.9", result.Explanation);
        Assert.Contains("未達門檻 50.0 ms", result.Explanation);
    }

    [Fact]
    public void Ping_反例2_單日暴衝不命中()
    {
        // 27 日平坦 20 ms，第 28 日暴衝至 150 ms
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 27 ? 150.0 : 20.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
    }

    [Fact]
    public void Ping_反例3_低位平坦不命中()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 15.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.NoHit, result.Outcome);
    }

    [Fact]
    public void Ping_反例4_有效日二十七天_資料不足()
    {
        var data = Enumerable.Range(0, 27)
            .Select(i => (Start.AddDays(i), 20.0 + i * 2.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("27／28", result.Explanation);
    }

    [Fact]
    public void Ping_反例5_重複日期_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i == 27 ? 26 : i), 20.0 + i * 2.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("重複日期", result.Explanation);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    public void Ping_反例6_範圍外數值_資料不足(double invalidValue)
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), i == 14 ? invalidValue : 20.0 + i * 2.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
    }

    [Fact]
    public void Ping_反例7_門檻無效_資料不足()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 20.0 + i * 2.0))
            .ToArray();

        var invalidThreshold = PrtgTrendThresholds.PingProvisional with { BaselineMultiplier = 0.5 };
        var result = PrtgTrendEngine.EvaluatePingLatency(data, invalidThreshold);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("門檻無效", result.Explanation);
    }

    [Fact]
    public void Ping_反例8_基準期點數小於近期天數_資料不足()
    {
        // 總日數 10，RecentDays = 6，基準期僅 10 - 6 = 4 日（小於 RecentDays）
        var custom = new PrtgTrendThresholds(10, 10, 0, 0.6, 50, 7, 6, 1.5);
        var data = Enumerable.Range(0, 10)
            .Select(i => (Start.AddDays(i), 20.0 + i * 4.0))
            .ToArray();

        var result = PrtgTrendEngine.EvaluatePingLatency(data, custom);

        Assert.Equal(PrtgTrendOutcome.InsufficientData, result.Outcome);
        Assert.Contains("基準期有效日不足", result.Explanation);
    }

    [Fact]
    public void Ping_同一輸入跑兩次結果相等()
    {
        var data = Enumerable.Range(0, 28)
            .Select(i => (Start.AddDays(i), 20.0 + i * 2.0))
            .ToArray();

        var r1 = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);
        var r2 = PrtgTrendEngine.EvaluatePingLatency(data, PrtgTrendThresholds.PingProvisional);

        Assert.Equal(r1, r2);
    }

    #endregion

    #region TheilSenSlope 與 WorseningRatio 小樣本精確值斷言

    [Fact]
    public void TheilSenSlope_奇數點對小樣本精確值斷言()
    {
        // 3 點：(Day 1, 10), (Day 2, 12), (Day 3, 11)
        // 點對斜率：(12-10)/1 = 2, (11-10)/2 = 0.5, (11-12)/1 = -1
        // 排序後：[-1, 0.5, 2]，中位數 = 0.5
        var points = new (DateOnly, double)[]
        {
            (Start.AddDays(1), 10.0),
            (Start.AddDays(2), 12.0),
            (Start.AddDays(3), 11.0)
        };

        var slope = PrtgTrendEngine.TheilSenSlope(points);

        Assert.Equal(0.5, slope, 5);
    }

    [Fact]
    public void TheilSenSlope_偶數點對小樣本精確值斷言()
    {
        // 4 點：(Day 1, 10), (Day 2, 14), (Day 4, 16), (Day 5, 22)
        // 6 個點對斜率：
        // (14-10)/1 = 4, (16-10)/3 = 2, (22-10)/4 = 3,
        // (16-14)/2 = 1, (22-14)/3 = 8/3 ≈ 2.666667, (22-16)/1 = 6
        // 排序：[1, 2, 2.666667, 3, 4, 6]，中位數 = (8/3 + 3) / 2 = 17 / 6 ≈ 2.8333333
        var points = new (DateOnly, double)[]
        {
            (Start.AddDays(1), 10.0),
            (Start.AddDays(2), 14.0),
            (Start.AddDays(4), 16.0),
            (Start.AddDays(5), 22.0)
        };

        var slope = PrtgTrendEngine.TheilSenSlope(points);

        Assert.Equal(17.0 / 6.0, slope, 5);
    }

    [Fact]
    public void WorseningRatio_上升惡化含零變化計入分母精確值斷言()
    {
        // 數值序列：10 -> 10 (delta 0), 10 -> 12 (delta 2), 12 -> 12 (delta 0), 12 -> 15 (delta 3)
        // 相鄰變化共 4 次，朝惡化（上升 > 0）共 2 次，零變化計入分母但不計入惡化
        // 比例 = 2 / 4 = 0.50
        var points = new (DateOnly, double)[]
        {
            (Start.AddDays(1), 10.0),
            (Start.AddDays(2), 10.0),
            (Start.AddDays(3), 12.0),
            (Start.AddDays(4), 12.0),
            (Start.AddDays(5), 15.0)
        };

        var ratio = PrtgTrendEngine.WorseningRatio(points, PrtgTrendDirection.HigherIsWorse);

        Assert.Equal(0.50, ratio, 5);
    }

    [Fact]
    public void WorseningRatio_下降惡化含零變化計入分母精確值斷言()
    {
        // 數值序列：50 -> 50 (delta 0), 50 -> 45 (delta -5), 45 -> 40 (delta -5)
        // 相鄰變化共 3 次，朝惡化（下降 < 0）共 2 次，零變化計入分母但不計入惡化
        // 比例 = 2 / 3 ≈ 0.666667
        var points = new (DateOnly, double)[]
        {
            (Start.AddDays(1), 50.0),
            (Start.AddDays(2), 50.0),
            (Start.AddDays(3), 45.0),
            (Start.AddDays(4), 40.0)
        };

        var ratio = PrtgTrendEngine.WorseningRatio(points, PrtgTrendDirection.LowerIsWorse);

        Assert.Equal(2.0 / 3.0, ratio, 5);
    }

    #endregion

    #region 門檻合理性驗證（IsValid）

    [Theory]
    [InlineData(35, 28, 0.3, 0.70, 90, 7, 7, 1.0, true)]
    [InlineData(27, 28, 0.3, 0.70, 90, 7, 7, 1.0, false)] // WindowDays < MinimumValidDays
    [InlineData(35, 1, 0.3, 0.70, 90, 7, 7, 1.0, false)]  // MinimumValidDays < 2
    [InlineData(401, 28, 0.3, 0.70, 90, 7, 7, 1.0, false)] // WindowDays > 400
    [InlineData(35, 28, -0.1, 0.70, 90, 7, 7, 1.0, false)] // MinimumSlopePerDay < 0
    [InlineData(35, 28, 0.3, 0.0, 90, 7, 7, 1.0, false)]   // MinimumWorseningDayRatio <= 0
    [InlineData(35, 28, 0.3, 1.01, 90, 7, 7, 1.0, false)]  // MinimumWorseningDayRatio > 1
    [InlineData(35, 28, 0.3, 0.70, -1, 7, 7, 1.0, false)]  // TargetValue < 0
    [InlineData(35, 28, 0.3, 0.70, 90, 0, 7, 1.0, false)]  // HorizonDays < 1
    [InlineData(35, 28, 0.3, 0.70, 90, 366, 7, 1.0, false)]// HorizonDays > 365
    [InlineData(35, 28, 0.3, 0.70, 90, 7, 0, 1.0, false)]  // RecentDays < 1
    [InlineData(35, 28, 0.3, 0.70, 90, 7, 29, 1.0, false)] // RecentDays > MinimumValidDays
    [InlineData(35, 28, 0.3, 0.70, 90, 7, 7, 0.99, false)] // BaselineMultiplier < 1
    public void 門檻合理性驗證(int w, int m, double slope, double ratio, double target, int h, int recent, double mult, bool expected)
    {
        var t = new PrtgTrendThresholds(w, m, slope, ratio, target, h, recent, mult);
        Assert.Equal(expected, t.IsValid());
    }

    #endregion
}
