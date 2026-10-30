using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Core.RiskLevels（docs/archive/HISTORY.md S2）：日風險等級的單一事實來源。
/// </summary>
public class RiskLevelsTests
{
    [Theory]
    [InlineData("高", 3)]
    [InlineData("中", 2)]
    [InlineData("低", 1)]
    [InlineData("未知", 0)]
    [InlineData("", 0)]
    public void Rank_依高中低給對應權重(string level, int expected)
    {
        Assert.Equal(expected, RiskLevels.Rank(level));
    }

    [Theory]
    [InlineData("高", true)]
    [InlineData("中", true)]
    [InlineData("低", false)]
    [InlineData("未知", false)]
    public void IsActionable_只有高中算需要處理(string level, bool expected)
    {
        Assert.Equal(expected, RiskLevels.IsActionable(level));
    }

    [Theory]
    [InlineData("高", "低", "高")]
    [InlineData("低", "高", "高")]
    [InlineData("中", "中", "中")]
    [InlineData("未知", "低", "低")]
    public void MoreSevere_只能往上拉不能往下壓(string a, string b, string expected)
    {
        Assert.Equal(expected, RiskLevels.MoreSevere(a, b));
    }

    [Theory]
    [InlineData("高", "高")]
    [InlineData(" 中 ", "中")]
    [InlineData("低", "低")]
    [InlineData("高風險", "高")]
    [InlineData("中等", "中")]
    [InlineData("低風險", "低")]
    [InlineData("中度風險", "中")]
    [InlineData("其中有問題", "未知")]
    [InlineData("高度懷疑但中斷", "未知")]
    [InlineData("", "未知")]
    [InlineData("   ", "未知")]
    [InlineData(null, "未知")]
    [InlineData("高風險，中等", "未知")]
    [InlineData("風險：高", "未知")]
    [InlineData("這是一段完全解析失敗的原文", "未知")]
    public void Normalize_只接受短字串且首字為等級(string? text, string expected)
    {
        Assert.Equal(expected, RiskLevels.Normalize(text));
    }

    /// <summary>AtOrAbove（回饋十八輪批次A）：供郵件查詢下推使用，未知值 fail-open 回全集。</summary>
    [Theory]
    [InlineData("高", new[] { "高" })]
    [InlineData("中", new[] { "高", "中" })]
    [InlineData("低", new[] { "高", "中", "低" })]
    [InlineData("未知", new[] { "高", "中", "低" })]
    [InlineData("", new[] { "高", "中", "低" })]
    public void AtOrAbove_回傳達到指定等級以上的集合(string min, string[] expected)
    {
        Assert.Equal(expected, RiskLevels.AtOrAbove(min));
    }
}
