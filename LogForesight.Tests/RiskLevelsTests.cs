using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Core.RiskLevels（docs/SHARED-STANDARDS-PLAN.md S2）：日風險等級的單一事實來源。
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
    [InlineData("風險等級：高，理由是...", "高")]
    [InlineData("中度風險", "中")]
    [InlineData("這次判定為低", "低")]
    [InlineData("這是一段完全解析失敗的原文", "未知")]
    public void Normalize_從AI回傳文字擷取風險等級(string text, string expected)
    {
        Assert.Equal(expected, RiskLevels.Normalize(text));
    }
}
