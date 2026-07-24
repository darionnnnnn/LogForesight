using Xunit;

namespace LogForesight.Tests;

/// <summary>一次性 SentinelId 回填（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 4）。</summary>
public class SentinelIdBackfillerTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();

    [Fact]
    public void 名稱對得到現存Sentinel_回填SentinelId並正規化大小寫()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "SENTINEL-A" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", NetiqServer = "sentinel-a" });

        var result = SentinelIdBackfiller.Run(_hosts, _sentinels);

        Assert.Equal(1, result.BackfilledCount);
        Assert.Equal(0, result.UnresolvedCount);
        var host = _hosts.FindByName("10.1.2.1")!;
        Assert.Equal(sentinel.SentinelId, host.SentinelId);
        Assert.Equal("SENTINEL-A", host.NetiqServer);   // 正規化為 Sentinel 現存的大小寫
    }

    [Fact]
    public void 名稱對不到任何現存Sentinel_維持null並計入未解析()
    {
        _hosts.Upsert(new WebHost { HostName = "10.1.2.2", Source = "netiq", NetiqServer = "已刪除的Sentinel" });

        var result = SentinelIdBackfiller.Run(_hosts, _sentinels);

        Assert.Equal(0, result.BackfilledCount);
        Assert.Equal(1, result.UnresolvedCount);
        Assert.Null(_hosts.FindByName("10.1.2.2")!.SentinelId);
    }

    [Fact]
    public void 已有SentinelId的主機不重新處理()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "SENTINEL-A" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.3", Source = "netiq", SentinelId = 999, NetiqServer = "SENTINEL-A" });

        var result = SentinelIdBackfiller.Run(_hosts, _sentinels);

        Assert.Equal(0, result.BackfilledCount);
        // 已存在的值不被回填邏輯覆寫（就算跟目前 Sentinel 的 id 對不上，也不是這個流程的職責）
        Assert.Equal(999, _hosts.FindByName("10.1.2.3")!.SentinelId);
        Assert.NotEqual(999, sentinel.SentinelId);
    }

    [Fact]
    public void 待歸屬主機NetiqServer本來就是空白_不計入未解析()
    {
        _hosts.Upsert(new WebHost { HostName = "10.1.2.4", Source = "netiq" });

        var result = SentinelIdBackfiller.Run(_hosts, _sentinels);

        Assert.Equal(0, result.BackfilledCount);
        Assert.Equal(0, result.UnresolvedCount);
    }

    [Fact]
    public void 冪等_跑第二次不再有任何異動()
    {
        _sentinels.Upsert(new Sentinel { Name = "SENTINEL-A" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", NetiqServer = "SENTINEL-A" });

        SentinelIdBackfiller.Run(_hosts, _sentinels);
        var second = SentinelIdBackfiller.Run(_hosts, _sentinels);

        Assert.Equal(0, second.BackfilledCount);
        Assert.Equal(0, second.UnresolvedCount);
    }
}
