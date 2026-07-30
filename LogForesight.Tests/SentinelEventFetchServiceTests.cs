using LogForesight.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 詢問 AI 現場取數的節流閘（docs/FEEDBACK-4-PLAN.md §5）：這裡只釘住「不符資格時
/// 提早回 null、完全不嘗試連線」的三道防線——開關關閉、非 NetIQ 主機（無 NetiqServer／
/// IpAddress）、Sentinel 設定查無資料。實際的 Sentinel HTTP 查詢行為由
/// <see cref="SentinelClient"/> 自己的測試（SentinelClientTests）覆蓋，這裡不重複。
/// </summary>
public class SentinelEventFetchServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly NetiqOptionsStore _optionsStore;
    private readonly NetiqHostServiceTests.FakeNetiqServerCatalog _catalog = new(Array.Empty<string>());
    private readonly SentinelEventFetchService _service;

    public SentinelEventFetchServiceTests()
    {
        _optionsStore = new NetiqOptionsStore(_fixture.Blob("netiq_options"));
        _service = new SentinelEventFetchService(_catalog, _optionsStore);
    }

    public void Dispose() => _fixture.Dispose();

    private static WebHost Host(string? netiqServer, string? ip) => new()
    {
        HostName = "SRV-A", NetiqServer = netiqServer, IpAddress = ip
    };

    [Fact]
    public async Task 開關預設關閉時回null()
    {
        var result = await _service.FetchAsync(Host("S1", "10.0.0.1"), DateTime.Today, "disk", 153);
        Assert.Null(result);
    }

    [Fact]
    public async Task 開關開啟但無NetiqServer時回null_本機直讀主機沒有這條路徑()
    {
        _optionsStore.Update(o => o.ChatLiveFetchEnabled = true);

        var result = await _service.FetchAsync(Host(null, "10.0.0.1"), DateTime.Today, "disk", 153);

        Assert.Null(result);
    }

    [Fact]
    public async Task 開關開啟但無IP時回null()
    {
        _optionsStore.Update(o => o.ChatLiveFetchEnabled = true);

        var result = await _service.FetchAsync(Host("S1", null), DateTime.Today, "disk", 153);

        Assert.Null(result);
    }

    [Fact]
    public async Task 開關開啟且是NetIQ主機但Sentinel設定查無資料時回null()
    {
        _optionsStore.Update(o => o.ChatLiveFetchEnabled = true);
        // _catalog 沒有註冊任何 Sentinel，GetServer("UNKNOWN") 回 null → 不嘗試連線

        var result = await _service.FetchAsync(Host("UNKNOWN", "10.0.0.1"), DateTime.Today, "disk", 153);

        Assert.Null(result);
    }
}
