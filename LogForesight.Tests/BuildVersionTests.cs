using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 64 輪 E-1：版本號不隨 commit 變，部署了哪一版要看 sha；這裡是唯一的解析點。</summary>
public class BuildVersionTests
{
    [Fact]
    public void 有sha_取短碼並組出標籤()
    {
        var v = BuildVersion.Parse("1.0.53.2+a3527417d6752638347e825e13da68033db03a10");
        Assert.Equal("1.0.53.2", v.Number);
        Assert.Equal("a352741", v.Sha);
        Assert.True(v.ShaKnown);
        Assert.Equal("1.0.53.2+a352741", v.Label);
    }

    [Theory]
    [InlineData("1.0.53.2")]        // 建置環境沒有 git
    [InlineData("1.0.53.2+")]       // 空的尾巴
    [InlineData("1.0.53.2+local")]  // 不是十六進位
    public void 沒有可用的sha_只留版本號且標示未知(string raw)
    {
        var v = BuildVersion.Parse(raw);
        Assert.Equal("1.0.53.2", v.Number);
        Assert.False(v.ShaKnown);
        Assert.Equal("1.0.53.2", v.Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 讀不到版本_回unknown不擲例外(string? raw)
    {
        var v = BuildVersion.Parse(raw);
        Assert.Equal("unknown", v.Number);
        Assert.False(v.ShaKnown);
    }

    [Fact]
    public void 目前的建置_有版本號()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildVersion.Current.Number));
        Assert.NotEqual("unknown", BuildVersion.Current.Number);
    }
}
