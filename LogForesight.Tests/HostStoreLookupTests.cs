using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機單筆查找不再複製整份清單（回饋三十四輪 A5）。呼叫點在逐主機迴圈裡，
/// 3682 台規模下每查一台就複製 3682 個參考純粹是 GC 壓力。
/// 這裡守的是「換讀取路徑後語意不變」：查得到、查不到回 null、更新後看得到新值。
/// </summary>
public class HostStoreLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-hoststore-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _store;

    public HostStoreLookupTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
        _store = new HostStore(_backend.Blob("hosts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private WebHost Add(string name, string ip) => _store.Upsert(new WebHost
    {
        HostName = name, IpAddress = ip, Os = WebHost.OsWindows, Source = "netiq", Active = true
    });

    [Fact]
    public void 依編號與名稱都查得到()
    {
        var host = Add("SRV-DC01", "10.0.0.1");
        Add("SRV-DC02", "10.0.0.2");

        Assert.Equal("SRV-DC01", _store.Get(host.HostId)?.HostName);
        Assert.Equal(host.HostId, _store.FindByName("srv-dc01")?.HostId);   // 名稱不分大小寫
    }

    [Fact]
    public void 查無資料回null()
    {
        Add("SRV-DC01", "10.0.0.1");

        Assert.Null(_store.Get(9999));
        Assert.Null(_store.FindByName("SRV-NOPE"));
    }

    [Fact]
    public void 更新後查得到新值()
    {
        var host = Add("SRV-DC01", "10.0.0.1");

        host.IpAddress = "10.9.9.9";
        _store.Upsert(host);

        Assert.Equal("10.9.9.9", _store.Get(host.HostId)?.IpAddress);
    }

    [Fact]
    public void 取整份清單仍是可安全增刪的複本()
    {
        Add("SRV-DC01", "10.0.0.1");

        var all = _store.GetAll();
        all.Clear();   // 動到回傳的清單不得影響共用快照

        Assert.Single(_store.GetAll());
    }

    // ── 第 78 輪批次 2a（2-3）：索引綁在快照上，語意與線性 FirstOrDefault 相同 ──────────────

    private void AddRaw(long id, string name) =>
        _store.MutateBatch(hosts => hosts.Add(new WebHost { HostId = id, HostName = name, Source = "netiq", Active = true }));

    [Fact]
    public void 同名多台取清單中第一台_名稱不分大小寫()
    {
        AddRaw(10, "Dup-Host");
        AddRaw(11, "DUP-HOST");
        AddRaw(12, "other");

        Assert.Equal(10, _store.FindByName("dup-host")!.HostId);
        Assert.Equal(10, _store.FindByName("DUP-HOST")!.HostId);
        Assert.Equal(12, _store.FindByName("OTHER")!.HostId);
        Assert.Null(_store.FindByName("missing"));
    }

    [Fact]
    public void 同編號兩筆取第一筆()
    {
        AddRaw(7, "first");
        AddRaw(7, "second");

        Assert.Equal("first", _store.Get(7)!.HostName);
        Assert.Null(_store.Get(8));
    }

    [Fact]
    public void 別的實例寫入後下一次查找看得到新內容()
    {
        AddRaw(1, "a");
        Assert.Null(_store.FindByName("late"));
        Assert.Null(_store.Get(2));

        var other = new HostStore(_backend.Blob("hosts"));   // 另一個實例（等同別的請求或行程）
        other.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 2, HostName = "late", Source = "netiq", Active = true });
            hosts.Single(h => h.HostId == 1).HostName = "renamed";
        });

        Assert.Equal(2, _store.FindByName("LATE")!.HostId);
        Assert.Equal("late", _store.Get(2)!.HostName);
        Assert.Null(_store.FindByName("a"));
        Assert.Equal("renamed", _store.Get(1)!.HostName);
    }

    [Fact]
    public void 一千台連查一千次_索引只建一次且版本變動才重建()
    {
        _store.MutateBatch(hosts =>
        {
            for (var i = 1; i <= 1000; i++)
                hosts.Add(new WebHost { HostId = i, HostName = $"srv-{i:0000}", Source = "netiq", Active = true });
        });

        var before = _store.LookupIndexBuilds;
        for (var i = 1; i <= 1000; i++)
        {
            Assert.Equal($"srv-{i:0000}", _store.Get(i)!.HostName);
            if (i % 2 == 0) Assert.Equal(i, _store.FindByName($"SRV-{i:0000}")!.HostId);
        }
        Assert.Equal(1, _store.LookupIndexBuilds - before);

        _store.MutateBatch(hosts => hosts.Add(new WebHost { HostId = 1001, HostName = "extra", Source = "netiq", Active = true }));
        Assert.Equal(1001, _store.FindByName("extra")!.HostId);
        Assert.Equal(2, _store.LookupIndexBuilds - before);
    }
}
