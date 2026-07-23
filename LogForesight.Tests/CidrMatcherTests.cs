using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>IPv4 網段比對（docs/SCALE-2000-PLAN.md §3）。邊界釘死，避免打錯的網段靜默命中錯的範圍。</summary>
public class CidrMatcherTests
{
    [Theory]
    [InlineData("10.1.2.0/24", "10.1.2.1", true)]
    [InlineData("10.1.2.0/24", "10.1.2.255", true)]
    [InlineData("10.1.2.0/24", "10.1.3.1", false)]     // 隔壁網段
    [InlineData("10.1.2.0/24", "10.1.2.0", true)]      // 網路位址本身
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("0.0.0.0/0", "203.0.113.9", true)]     // 全比對
    [InlineData("10.1.2.15/32", "10.1.2.15", true)]
    [InlineData("10.1.2.15/32", "10.1.2.16", false)]
    public void CIDR_比對(string pattern, string ip, bool expected)
    {
        var range = CidrMatcher.Parse(pattern);
        Assert.NotNull(range);
        Assert.Equal(expected, CidrMatcher.Matches(range!, ip));
    }

    [Theory]
    [InlineData("10.1.2.*", "10.1.2.7", true)]         // ＝/24
    [InlineData("10.1.2.*", "10.1.3.7", false)]
    [InlineData("10.1.*", "10.1.99.7", true)]          // ＝/16
    [InlineData("10.1.*", "10.2.0.1", false)]
    public void 萬用字元_比對(string pattern, string ip, bool expected)
    {
        var range = CidrMatcher.Parse(pattern);
        Assert.NotNull(range);
        Assert.Equal(expected, CidrMatcher.Matches(range!, ip));
    }

    [Fact]
    public void 單一IP_視為斜線32()
    {
        var range = CidrMatcher.Parse("192.168.1.1");
        Assert.NotNull(range);
        Assert.Equal(32, range!.PrefixLength);
        Assert.True(CidrMatcher.Matches(range, "192.168.1.1"));
        Assert.False(CidrMatcher.Matches(range, "192.168.1.2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.1.2")]              // 少一段
    [InlineData("10.1.2.3.4")]          // 多一段
    [InlineData("10.1.2.256")]          // 超出 255
    [InlineData("10.1.2.01")]           // 前導零
    [InlineData("10.1.2.0/33")]         // 前綴超出
    [InlineData("10.1.2.0/-1")]
    [InlineData("10.*.2.*")]            // 非尾端萬用
    [InlineData("*")]                   // 無固定前綴
    [InlineData("*.1.2.3")]
    [InlineData("abc.def")]
    [InlineData("10.1.2.x")]
    public void 非法格式_回null(string pattern)
    {
        Assert.Null(CidrMatcher.Parse(pattern));
    }

    [Fact]
    public void 非法IP不算命中()
    {
        var range = CidrMatcher.Parse("10.1.2.0/24");
        Assert.NotNull(range);
        Assert.False(CidrMatcher.Matches(range!, "not-an-ip"));
        Assert.False(CidrMatcher.Matches(range!, null));
    }
}
