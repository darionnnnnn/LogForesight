using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機清單來源（docs/NETIQ-WEB-CONFIG-PLAN.md）：主機清單的主人固定為 Web 主機頁維護
/// （Txt 清單模式已退役，定案 12）——這裡驗證挑選規則（待歸屬／IP 衝突／Sentinel 停用排除警告）。
/// </summary>
public class HostListProviderTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();

    private StoreHostListProvider Provider() => new(_hosts, _sentinels);

    [Fact]
    public void 待歸屬主機_不查詢並列出原因()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.0.0.9", IpAddress = "10.0.0.9", Source = "netiq", Active = true
        });

        var result = Provider().GetHostList();

        Assert.Equal(0, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("尚未確定所屬 Sentinel"));
    }

    [Fact]
    public void IP衝突_只查最早建立的那台並列出被跳過的()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "SENTINEL-A" });

        foreach (var name in new[] { "SRV-A", "SRV-B" })
        {
            _hosts.Upsert(new WebHost
            {
                HostName = name, IpAddress = "10.0.0.9",
                SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
            });
        }

        var result = Provider().GetHostList();

        Assert.Equal(1, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("衝突"));
    }

    [Fact]
    public void 分組鍵使用Sentinel現存名稱_不是主機列上可能落後的快照()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "改名前" });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.0.0.5", IpAddress = "10.0.0.5",
            SentinelId = sentinel.SentinelId, NetiqServer = "改名前", Source = "netiq", Active = true
        });

        // 模擬 Sentinel 已改名，但主機列上的顯示快照還沒被同步（正常情況下 SentinelAdminService
        // 會同步，這裡刻意製造落後來驗證分組鍵不依賴這份快照）
        _sentinels.Upsert(new Sentinel { SentinelId = sentinel.SentinelId, Name = "改名後" });

        var result = Provider().GetHostList();

        Assert.Equal(new[] { "改名後" }, result.ByServer.Keys);
    }

    [Fact]
    public void Sentinel已停用_暫停輪巡並列出原因_主機本身不動()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "SENTINEL-A", Active = false });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.0.0.7", IpAddress = "10.0.0.7",
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        });

        var result = Provider().GetHostList();

        Assert.Equal(0, result.TotalHosts);
        Assert.Contains(result.Warnings, w => w.Contains("已停用"));
        Assert.True(_hosts.FindByName("10.0.0.7")!.Active);   // 主機本身不動，跟刪除觸發的孤兒流程不同
    }
}
