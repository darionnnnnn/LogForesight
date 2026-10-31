using LogForesight.Web.Repositories;
using Xunit;

namespace LogForesight.Tests;

/// <summary>RecordRepository.ResolveVisibility：繞過 IRecordRepository 的呼叫端共用的可見範圍組合（純函式）</summary>
public class RecordRepositoryResolveVisibilityTests
{
    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items);

    [Fact]
    public void 兩者皆null_不限制且不是全隱藏()
    {
        var (risk, severities, nothing) = RecordRepository.ResolveVisibility(null, null);

        Assert.Null(risk);
        Assert.Null(severities);
        Assert.False(nothing);
    }

    [Fact]
    public void 日風險等級為空集合_NothingVisible為真()
    {
        var (risk, _, nothing) = RecordRepository.ResolveVisibility(Set(), null);

        Assert.NotNull(risk);
        Assert.Empty(risk!);
        Assert.True(nothing);
    }

    [Fact]
    public void 日風險等級有值_原樣取用且非全隱藏()
    {
        var (risk, _, nothing) = RecordRepository.ResolveVisibility(Set("High", "Medium"), null);

        Assert.Equal(new[] { "High", "Medium" }.OrderBy(x => x), risk!.OrderBy(x => x));
        Assert.False(nothing);
    }

    [Fact]
    public void 嚴重度空集合_轉為空集合但不算全隱藏()
    {
        // NothingVisible 只看日風險等級；嚴重度空集合交給 aggregate 介面的「零結果」語意
        var (_, severities, nothing) = RecordRepository.ResolveVisibility(null, Set());

        Assert.NotNull(severities);
        Assert.Empty(severities!);
        Assert.False(nothing);
    }

    [Fact]
    public void 嚴重度字串_忽略大小寫且非法字串被忽略()
    {
        var all = Enum.GetValues<IssueSeverity>();
        var valid = all[0];
        var (_, severities, _) = RecordRepository.ResolveVisibility(
            null, Set(valid.ToString().ToUpperInvariant(), "NotASeverity", ""));

        Assert.Equal(new[] { valid }, severities!.ToArray());
    }
}
