using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel 名單改由 ISentinelStore 供應（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1）。</summary>
public class NetiqServerCatalogTests
{
    private readonly FakeSentinelStore _sentinels = new();

    private NetiqServerCatalog Create() => new(_sentinels);

    [Fact]
    public void GetServer_密碼解密後回傳供探索用戶端使用()
    {
        _sentinels.Upsert(new Sentinel
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc",
            PasswordEnc = CryptoHelper.Encrypt("hunter2")
        });

        var server = Create().GetServer("s1");   // 不分大小寫

        Assert.NotNull(server);
        Assert.Equal("S1", server!.Name);
        Assert.Equal("hunter2", server.Password);   // 已解密，供 SentinelRestDirectoryClient 直接用
        Assert.True(server.CanDiscover);
    }

    [Fact]
    public void IsKnownServer_含已停用的Sentinel()
    {
        _sentinels.Upsert(new Sentinel { Name = "S1", Active = false });

        // 停用不等於不存在（定案 5）：既有主機編輯表單不該因為 Sentinel 暫停輪巡就驗證失敗
        Assert.True(Create().IsKnownServer("S1"));
    }

    [Fact]
    public void GetServer_查無回null()
    {
        Assert.Null(Create().GetServer("不存在"));
        Assert.Null(Create().GetServer(null));
    }

    [Fact]
    public void GetServerNames_依名稱排序()
    {
        _sentinels.Upsert(new Sentinel { Name = "B" });
        _sentinels.Upsert(new Sentinel { Name = "A" });

        Assert.Equal(new[] { "A", "B" }, Create().GetServerNames());
    }
}
