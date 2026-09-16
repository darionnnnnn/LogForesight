using System.Collections.Concurrent;
using System.Reflection;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪 B7 C6：詢問 AI 現場取數的快取是 static 字典、10 分鐘 TTL，
/// 但原本**沒有條目上限也沒有清理**——鍵是主機＋日期＋來源＋EventId，組合無限，
/// 過期條目沒有任何人會來清，長期執行就是單調成長的慢速記憶體洩漏。
///
/// 比照 <see cref="SummaryCache"/> 的既有慣例：超過條目上限整批清除。
/// </summary>
[Collection("SentinelLiveFetchCacheState")]
public sealed class SentinelEventFetchCacheBoundTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly NetiqOptionsStore _optionsStore;
    private readonly SentinelEventFetchService _service;

    public SentinelEventFetchCacheBoundTests()
    {
        _optionsStore = new NetiqOptionsStore(_fixture.Blob("netiq_options"));
        // 開關要開，否則在寫入快取之前就回 null（那是另外三道防線，見 SentinelEventFetchServiceTests）
        _optionsStore.Update(o => o.ChatLiveFetchEnabled = true);
        _service = new SentinelEventFetchService(new FakeNetiqServerCatalog(Array.Empty<string>()), _optionsStore);
        SentinelEventFetchService.ClearCache();
    }

    public void Dispose()
    {
        SentinelEventFetchService.ClearCache();
        _fixture.Dispose();
    }

    private static WebHost Host() => new() { HostName = "SRV-A", NetiqServer = "S1", IpAddress = "10.0.0.1" };

    private static int MaxEntries => (int)typeof(SentinelEventFetchService)
        .GetField("MaxCacheEntries", BindingFlags.Static | BindingFlags.NonPublic)!
        .GetValue(null)!;

    private async Task FillAsync(int count)
    {
        var day = new DateTime(2026, 1, 1);
        for (var i = 0; i < count; i++)
        {
            // 每次都是不同的鍵（日期逐日推進）——這就是正式路徑長期執行的成長方式
            await _service.FetchAsync(Host(), day.AddDays(i), "disk", 153);
        }
    }

    [Fact]
    public async Task 超過條目上限後字典不再成長()
    {
        var max = MaxEntries;
        Assert.True(max > 0, "條目上限必須是正數");

        await FillAsync(max + 50);

        Assert.True(SentinelEventFetchService.CacheEntryCount <= max,
            $"快取條目數 {SentinelEventFetchService.CacheEntryCount} 超過上限 {max}");
        Assert.True(SentinelEventFetchService.CacheEntryCount < max + 50,
            "快取沒有被清理，仍隨查詢次數單調成長");
    }

    [Fact]
    public async Task 未達上限前照常累積_不是每次都清掉()
    {
        await FillAsync(5);

        Assert.Equal(5, SentinelEventFetchService.CacheEntryCount);
    }

    [Fact]
    public async Task TTL行為不變_寫入的條目十分鐘後到期()
    {
        var before = DateTime.UtcNow;
        await FillAsync(1);

        var cache = (ConcurrentDictionary<string, (DateTime Expiry, LiveEventFetchResult? Result)>)
            typeof(SentinelEventFetchService)
                .GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;

        var entry = Assert.Single(cache);
        var ttl = entry.Value.Expiry - before;
        Assert.InRange(ttl.TotalMinutes, 9.9, 10.1);
    }
}
