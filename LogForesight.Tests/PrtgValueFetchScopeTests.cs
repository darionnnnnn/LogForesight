using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 數值取數的主機範圍（docs/PRTG-SPEC.md §3a）：三種模式的唯一判定點，
/// 每日擷取與歷史回填共用同一份，不各寫一份。
/// </summary>
public class PrtgValueFetchScopeTests
{
    private static readonly long[] Triggered = { 1, 2 };
    private static readonly long[] Mapped = { 1, 2, 3, 4, 5 };
    private static readonly long[] Extra = { 9 };

    [Fact]
    public void 觸發模式只取觸發主機()
    {
        var hosts = PrtgValueFetchScope.SelectHosts(PrtgValueFetchScope.Triggered, Triggered, Mapped, Extra);
        Assert.Equal(new long[] { 1, 2 }, hosts);
    }

    [Fact]
    public void 全部已對應模式取全部有對應的主機()
    {
        var hosts = PrtgValueFetchScope.SelectHosts(PrtgValueFetchScope.AllMapped, Triggered, Mapped, Extra);
        Assert.Equal(Mapped, hosts);
    }

    [Fact]
    public void 觸發加清單模式取聯集且去重()
    {
        // 清單裡若混入本來就會被觸發的主機，不得重複取數
        var hosts = PrtgValueFetchScope.SelectHosts(
            PrtgValueFetchScope.TriggeredPlusList, Triggered, Mapped, new long[] { 2, 9 });

        Assert.Equal(new long[] { 1, 2, 9 }, hosts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("everything")]
    public void 不合法或未設定一律退回預設的觸發模式(string? raw)
    {
        // 設定壞掉不該讓夜間批次改抓全機房——這是「單向閘門」的反方向：預設要保守。
        Assert.Equal(PrtgValueFetchScope.Triggered, PrtgValueFetchScope.Normalize(raw));

        var hosts = PrtgValueFetchScope.SelectHosts(raw, Triggered, Mapped, Extra);
        Assert.Equal(new long[] { 1, 2 }, hosts);
    }

    [Fact]
    public void 三個模式字面值合法其餘不合法()
    {
        Assert.True(PrtgValueFetchScope.IsValid("triggered"));
        Assert.True(PrtgValueFetchScope.IsValid("all-mapped"));
        Assert.True(PrtgValueFetchScope.IsValid("triggered-plus-list"));
        Assert.False(PrtgValueFetchScope.IsValid("all"));
        Assert.False(PrtgValueFetchScope.IsValid(null));
    }

    // ── 指定主機名稱解析 ──────────────────────────────────────────────

    private static (long, string, bool, bool)[] Hosts() => new[]
    {
        (10L, "SRV-DB01", true, false),
        (11L, "SRV-APP02", true, false),
        (12L, "SRV-OLD", false, false),      // 已停用
        (13L, "SRV-MERGED", true, true)      // 已合併（墓碑）
    };

    [Fact]
    public void 主機名稱解析不分大小寫且去除空白()
    {
        var (ids, unresolved) = PrtgValueFetchScope.ResolveHostNames(
            new[] { "  srv-db01  ", "SRV-APP02" }, Hosts());

        Assert.Equal(new long[] { 10, 11 }, ids);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void 主機名稱解析排除已停用與已合併()
    {
        // 對到幽靈主機比對不到更糟：那筆取數會掛在一台不存在的主機上。
        var (ids, unresolved) = PrtgValueFetchScope.ResolveHostNames(
            new[] { "SRV-OLD", "SRV-MERGED" }, Hosts());

        Assert.Empty(ids);
        Assert.Equal(new[] { "SRV-OLD", "SRV-MERGED" }, unresolved);
    }

    [Fact]
    public void 主機名稱解析回報對不到的名稱()
    {
        // 打錯字必須看得到——靜默略過會讓管理者以為設定生效了。
        var (ids, unresolved) = PrtgValueFetchScope.ResolveHostNames(
            new[] { "SRV-DB01", "SRV-TYPO" }, Hosts());

        Assert.Equal(new long[] { 10 }, ids);
        Assert.Equal(new[] { "SRV-TYPO" }, unresolved);
    }

    [Fact]
    public void 主機名稱解析忽略空白行且去重()
    {
        var (ids, unresolved) = PrtgValueFetchScope.ResolveHostNames(
            new[] { "SRV-DB01", "   ", "", "SRV-DB01" }, Hosts());

        Assert.Equal(new long[] { 10 }, ids);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void 主機名稱解析對null回空結果()
    {
        var (ids, unresolved) = PrtgValueFetchScope.ResolveHostNames(null, Hosts());

        Assert.Empty(ids);
        Assert.Empty(unresolved);
    }
}
