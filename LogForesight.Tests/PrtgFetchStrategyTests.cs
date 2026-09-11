using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgFetchStrategyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("CONSERVATIVE")]
    [InlineData("unknown-strategy")]
    public void Normalize_對無效值或空值_回退保守策略(string? input)
    {
        Assert.Equal(PrtgFetchStrategy.Conservative, PrtgFetchStrategy.Normalize(input));
    }

    [Theory]
    [InlineData(PrtgFetchStrategy.Conservative)]
    [InlineData(PrtgFetchStrategy.Aggressive)]
    public void Normalize_對合法值_原樣回傳(string input)
    {
        Assert.Equal(input, PrtgFetchStrategy.Normalize(input));
    }

    [Fact]
    public void Profile_保守策略_回傳15分鐘且夜間不查歷史值()
    {
        var profile = PrtgFetchStrategy.Profile(PrtgFetchStrategy.Conservative);
        Assert.Equal(15, profile.SnapshotIntervalMinutes);
        Assert.False(profile.NightlyExactValues);
    }

    [Fact]
    public void Profile_激進策略_回傳5分鐘且夜間逐顆查詢()
    {
        var profile = PrtgFetchStrategy.Profile(PrtgFetchStrategy.Aggressive);
        Assert.Equal(5, profile.SnapshotIntervalMinutes);
        Assert.True(profile.NightlyExactValues);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    public void Profile_亂值或空值_走保守策略(string? input)
    {
        var profile = PrtgFetchStrategy.Profile(input);
        Assert.Equal(15, profile.SnapshotIntervalMinutes);
        Assert.False(profile.NightlyExactValues);
    }

    [Fact]
    public void 間隔常數定義正確()
    {
        Assert.Equal(15, PrtgFetchStrategy.ConservativeIntervalMinutes);
        Assert.Equal(5, PrtgFetchStrategy.AggressiveIntervalMinutes);
    }
}
