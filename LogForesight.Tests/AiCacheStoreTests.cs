using Xunit;

namespace LogForesight.Tests;

/// <summary>AI 快取筆數上限（第 61 輪 N-9）：超過 500 筆依 CreatedAt 由舊到新移除。</summary>
public class AiCacheStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private AiCacheStore Store() => new(_fx.Blob("ai_cache"));

    private int Count()
    {
        using var ctx = _fx.NewContext();
        var raw = ctx.Blobs.Where(b => b.BlobKey == "ai_cache").Select(b => b.Content).FirstOrDefault();
        return raw == null ? 0 : System.Text.Json.JsonDocument.Parse(raw).RootElement.GetArrayLength();
    }

    [Fact]
    public void 寫入501個不同鍵_最舊被移除_總數維持上限()
    {
        var store = Store();
        for (var i = 0; i <= AiCacheStore.MaxEntries; i++) store.Put("k" + i, "v" + i);

        Assert.Null(store.Get("k0"));
        Assert.Equal("v" + AiCacheStore.MaxEntries, store.Get("k" + AiCacheStore.MaxEntries));
        Assert.Equal("v1", store.Get("k1"));
        Assert.Equal(AiCacheStore.MaxEntries, Count());
    }

    [Fact]
    public void 同鍵覆寫_不增加筆數()
    {
        var store = Store();
        for (var i = 0; i < 10; i++) store.Put("same", "v" + i);

        Assert.Equal("v9", store.Get("same"));
        Assert.Equal(1, Count());
    }
}
