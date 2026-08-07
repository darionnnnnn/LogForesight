using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 詢問 AI 現場取數的節流閘（docs/archive/FEEDBACK-4-PLAN.md §5）：這裡只釘住「不符資格時
/// 提早回 null、完全不嘗試連線」的三道防線——開關關閉、非 NetIQ 主機（無 NetiqServer／
/// IpAddress）、Sentinel 設定查無資料。實際的 Sentinel HTTP 查詢行為由
/// <see cref="SentinelClient"/> 自己的測試（SentinelClientTests）覆蓋，這裡不重複。
/// </summary>
public class SentinelEventFetchServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly NetiqOptionsStore _optionsStore;
    private readonly FakeNetiqServerCatalog _catalog = new(Array.Empty<string>());
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

    // ── BuildQuery：現場取數的 filter／投影欄位（docs/FEEDBACK-12-PLAN.md §4.6 體檢揪出）──
    // 純函數，不需要架假 Sentinel 伺服器——這裡驗證的正是「查詢本身怎麼組」，之前完全沒被測過。

    [Fact]
    public void BuildQuery_Windows主機用EventId子句()
    {
        var query = SentinelEventFetchService.BuildQuery(WebHost.OsWindows, "10.0.0.1", "disk", 153);

        Assert.Contains($"{SentinelFieldMap.EventId}:\"153\"", query.Filter);
        Assert.Contains("repip:10.0.0.1", query.Filter);
        Assert.DoesNotContain(SentinelFieldMap.LinuxProgram + ":", query.Filter);
        Assert.Equal(SentinelFieldMap.Source, query.SourceField);
        Assert.Contains(SentinelFieldMap.Source, query.ProjectionFields);
    }

    [Fact]
    public void BuildQuery_Linux主機改用program子句不含EventId()
    {
        var query = SentinelEventFetchService.BuildQuery(WebHost.OsLinux, "10.0.0.9", "sshd", eventId: 0);

        // 不能沿用 rv40（EventId）——Linux 事件沒有這個欄位，查了只會恆 0 筆
        Assert.DoesNotContain(SentinelFieldMap.EventId, query.Filter);
        Assert.Contains($"{SentinelFieldMap.LinuxProgram}:sshd*", query.Filter);
        Assert.Contains("repip:10.0.0.9", query.Filter);
        Assert.Equal(SentinelFieldMap.LinuxProgram, query.SourceField);
        Assert.Contains(SentinelFieldMap.LinuxProgram, query.ProjectionFields);
        Assert.DoesNotContain(SentinelFieldMap.Source, query.ProjectionFields);
    }

    [Fact]
    public void BuildQuery_os比對不分大小寫()
    {
        var query = SentinelEventFetchService.BuildQuery("LINUX", "10.0.0.9", "sshd", 0);

        Assert.Contains(SentinelFieldMap.LinuxProgram, query.ProjectionFields);
    }
}
