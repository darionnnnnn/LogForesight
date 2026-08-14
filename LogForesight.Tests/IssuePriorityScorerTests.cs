using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>優先度分數最小版（回饋十九輪批次G3）：常數係使用者定案，這裡釘住公式本身沒有算錯，
/// 不是在驗證常數合不合理——常數合不合理是定案時已經決定的事。</summary>
public class IssuePriorityScorerTests
{
    private static IssuePriorityScorer.ScoreInput BaseInput(
        IssueSeverity severity = IssueSeverity.High, double hostRatio = 1.0,
        double? deviation = null, int fleetFirstSeenDaysAgo = 100,
        int openHostCount = 1, int hostCount = 1, string tier = WebHost.TierStandard) => new(
        severity, hostRatio, deviation, fleetFirstSeenDaysAgo, openHostCount, hostCount, tier);

    [Theory]
    [InlineData(IssueSeverity.Critical, 1.0)]
    [InlineData(IssueSeverity.High, 1.0)]
    [InlineData(IssueSeverity.Medium, 0.6)]
    [InlineData(IssueSeverity.Low, 0.3)]
    public void 嚴重度權重(IssueSeverity severity, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(severity: severity));

        Assert.Equal(expected, result.SeverityWeight);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 1.0)]
    public void 影響率係數(double hostRatio, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(hostRatio: hostRatio));

        Assert.Equal(expected, result.HostRatioFactor, 3);
    }

    [Fact]
    public void 無基準時偏離權重固定為1點2()
    {
        var result = IssuePriorityScorer.Score(BaseInput(deviation: null));

        Assert.Equal(1.2, result.SpreadWeight);
    }

    [Theory]
    [InlineData(1.0, 0.6)]     // 偏離倍數 1（沒有偏離）→ 下限
    [InlineData(4.0, 1.0)]     // 0.6 + 0.2×log2(4) = 0.6 + 0.4 = 1.0
    [InlineData(0.5, 0.6)]     // d<1 時 max(d,1)=1，同樣夾在下限（不因「比基準少」而降到下限以下）
    public void 偏離權重公式(double deviation, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(deviation: deviation));

        Assert.Equal(expected, result.SpreadWeight, 3);
    }

    [Fact]
    public void 偏離權重上限夾到1點6()
    {
        // d=1024 → 0.6+0.2×10=2.6，應被夾到 1.6
        var result = IssuePriorityScorer.Score(BaseInput(deviation: 1024));

        Assert.Equal(1.6, result.SpreadWeight);
    }

    [Theory]
    [InlineData(0, 1.3)]
    [InlineData(7, 1.3)]
    [InlineData(8, 1.1)]
    [InlineData(30, 1.1)]
    [InlineData(31, 1.0)]
    [InlineData(365, 1.0)]
    public void 新穎度權重門檻(int daysAgo, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(fleetFirstSeenDaysAgo: daysAgo));

        Assert.Equal(expected, result.NoveltyWeight);
    }

    [Theory]
    [InlineData(0, 10, 0.5)]     // 全部已處理 → 折半（呼應 §10.6 不霸佔版面的精神）
    [InlineData(5, 10, 0.75)]
    [InlineData(10, 10, 1.0)]    // 全部未處理 → 上限
    public void 未處理權重(int openHostCount, int hostCount, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(openHostCount: openHostCount, hostCount: hostCount));

        Assert.Equal(expected, result.OpenWeight, 3);
    }

    [Theory]
    [InlineData(WebHost.TierCore, 1.2)]
    [InlineData(WebHost.TierStandard, 1.0)]
    [InlineData(WebHost.TierTest, 0.7)]
    public void 主機分級權重(string tier, double expected)
    {
        var result = IssuePriorityScorer.Score(BaseInput(tier: tier));

        Assert.Equal(expected, result.TierWeight);
    }

    /// <summary>總分＝100×六個係數連乘——用一組已知輸入手算驗證公式本身接對了，
    /// 不是各自測完成分後才發現合成時漏乘了一項。</summary>
    [Fact]
    public void 總分為六個係數的連乘()
    {
        var result = IssuePriorityScorer.Score(new IssuePriorityScorer.ScoreInput(
            MaxSeverity: IssueSeverity.High,        // severityW=1.0
            HostRatio: 1.0,                          // hostRatioFactor=1.0
            BaselineDeviationMultiplier: 4.0,        // spreadW=1.0
            FleetFirstSeenDaysAgo: 0,                // noveltyW=1.3
            OpenHostCount: 10, HostCount: 10,        // openW=1.0
            HighestAffectedTier: WebHost.TierCore)); // tierW=1.2

        // 100 × 1.0 × 1.0 × 1.0 × 1.3 × 1.0 × 1.2 = 156
        Assert.Equal(156.0, result.Total, 3);
    }
}
