using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 掃描匯入精靈選 Stub／真連線的判斷（§13，回饋第九輪）：**預設真連線**，離線示範資料
/// 只在「非 Production 且 DB 開關 UseOfflineDemoData 開啟」時採用——取代原本 appsettings
/// Netiq:DiscoveryClient（Auto 讓 Development 預設假資料、方向顛倒）。純函數
/// <see cref="ServiceCollectionExtensions.UseStubNetiqClient"/> 單獨測試，不必跑完整 DI 容器。
/// </summary>
public class NetiqDiscoveryClientSelectionTests
{
    [Fact]
    public void Production_一律真連線_即使開關開啟()
    {
        Assert.False(ServiceCollectionExtensions.UseStubNetiqClient(isProduction: true, useOfflineDemoData: true));
        Assert.False(ServiceCollectionExtensions.UseStubNetiqClient(isProduction: true, useOfflineDemoData: false));
    }

    [Fact]
    public void 非Production_依開關決定()
    {
        Assert.True(ServiceCollectionExtensions.UseStubNetiqClient(isProduction: false, useOfflineDemoData: true));
        Assert.False(ServiceCollectionExtensions.UseStubNetiqClient(isProduction: false, useOfflineDemoData: false));
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
