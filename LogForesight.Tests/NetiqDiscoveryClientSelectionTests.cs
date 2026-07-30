using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 掃描匯入精靈選 Stub／真連線的判斷（2026-07-29）：原本寫死「Development 用 Stub」，
/// 開發機因此永遠連不到真實 Sentinel 試掃；<c>Netiq:DiscoveryClient</c> 讓這個判斷可被明確覆寫。
/// 抽成純函數 <see cref="ServiceCollectionExtensions.ShouldUseStubNetiqClient"/> 單獨測試，
/// 不必為了測 DI 選型跑一個完整的 WebApplicationFactory。
/// </summary>
public class NetiqDiscoveryClientSelectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DiscoveryClient為Stub_一律用Stub_不論環境(bool isDevelopment)
    {
        Assert.True(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment, "Stub"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DiscoveryClient為Real_一律用真連線_不論環境(bool isDevelopment)
    {
        Assert.False(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment, "Real"));
    }

    [Fact]
    public void DiscoveryClient為Auto_Development用Stub()
    {
        Assert.True(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: true, "Auto"));
    }

    [Fact]
    public void DiscoveryClient為Auto_非Development用真連線()
    {
        Assert.False(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: false, "Auto"));
    }

    /// <summary>不合法值理論上被 Validate() 擋在啟動之前，這裡驗證的是防禦性穩妥：
    /// 萬一漏網，退回環境判斷而不是拋例外或恆真恆假。</summary>
    [Fact]
    public void DiscoveryClient為未知值_退回環境判斷()
    {
        Assert.True(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: true, "不合法的值"));
        Assert.False(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: false, "不合法的值"));
    }

    [Fact]
    public void 大小寫不拘()
    {
        Assert.True(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: false, "stub"));
        Assert.False(ServiceCollectionExtensions.ShouldUseStubNetiqClient(isDevelopment: true, "REAL"));
    }
}

/// <summary>
/// 固定台數／固定網段數是 <see cref="StubNetiqDirectoryClient"/> 這份示範資料產生器本身的邏輯，
/// 曾被誤以為是真實掃描功能的 bug（單一網段恆 35 台、兩網段各恆 23 台、恆最多 2 個網段）——
/// 這裡釘住「一定會在 Warnings 講清楚這是示範資料」，不能只寫在容易被忽略的 CoverageNote 小字裡。
/// </summary>
public class StubNetiqDirectoryClientTests
{
    [Fact]
    public async Task 掃描結果一律標示為離線示範資料()
    {
        var client = new StubNetiqDirectoryClient();
        var server = new SentinelServer { Name = "TEST-SENTINEL" };

        var result = await client.ListHostsAsync(server, "10.1.2", CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("示範資料"));
    }
}
