using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>已知雜訊記憶（<see cref="NoiseMarkStore"/>）依版本快取（第 77 輪 D-4）：每次開風險日詳情都讀它，沒異動不該重讀整份。</summary>
public sealed class NoiseMarkCacheTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly SqlPerformanceMonitor _monitor = new(thresholdMs: 0);

    public void Dispose() => _fx.Dispose();

    private NoiseMarkStore Store() => new(new EfJsonBlobStore(_fx.NewContext, "noise_marks", _monitor));

    private long ContentReads() =>
        _monitor.Snapshot().TopSlowOperations.FirstOrDefault(o => o.Operation == "blob:noise_marks:Read")?.Count ?? 0;

    private static NoiseMark Mark(string host, string key, string note = "") =>
        new() { HostName = host, IssueKey = key, MarkedByAccount = "a", MarkedAt = DateTime.Now, Note = note };

    [Fact]
    public void 未異動時連續讀取只讀一次整份()
    {
        var store = Store();
        store.Save(Mark("SRV-01", "System|Disk|153|2"));

        for (var i = 0; i < 10; i++) Assert.Single(store.GetForHost("srv-01"));

        Assert.Equal(1, ContentReads());
    }

    [Fact]
    public void 儲存與刪除後讀到新內容()
    {
        var store = Store();
        store.Save(Mark("SRV-01", "System|Disk|153|2", "舊"));
        Assert.Equal("舊", store.Get("SRV-01", "System|Disk|153|2")!.Note);

        store.Save(Mark("SRV-01", "System|Disk|153|2", "新"));
        Assert.Equal("新", store.Get("SRV-01", "System|Disk|153|2")!.Note);

        store.Save(Mark("SRV-02", "System|Disk|153|2"));
        Assert.Equal(2, store.GetAll().Count);

        store.Delete("SRV-01", "System|Disk|153|2");
        Assert.Empty(store.GetForHost("SRV-01"));
        Assert.Single(store.GetAll());
    }
}
