using LogForesight.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SQLite 連線層記憶體設定（`Storage:SqliteCacheSizeKb`／`SqliteMmapSizeBytes`／`SqliteSoftHeapLimitBytes`）：
/// 以真實檔案型 Sqlite 連線讀回 PRAGMA，確認設定值確實送到每條連線，而不是只比對字串。
/// </summary>
public class SqlitePragmaInterceptorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-pragma-" + Guid.NewGuid().ToString("N"));

    public SqlitePragmaInterceptorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private StorageSettings Settings(long? cache = null, long? mmap = null, long? soft = null) => new()
    {
        Type = "Sqlite",
        ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")}",
        SqliteCacheSizeKb = cache,
        SqliteMmapSizeBytes = mmap,
        SqliteSoftHeapLimitBytes = soft
    };

    private static long ReadPragma(StorageBackend backend, string name)
    {
        using var ctx = backend.CreateContext();
        var conn = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void 留空時用預設值()
    {
        var backend = new StorageBackend(Settings(), _dir);
        Assert.Equal(-SqlitePragmaInterceptor.DefaultCacheSizeKb, ReadPragma(backend, "cache_size"));
        Assert.Equal(SqlitePragmaInterceptor.DefaultMmapSizeBytes, ReadPragma(backend, "mmap_size"));
    }

    [Fact]
    public void 設定值送到每條連線()
    {
        var backend = new StorageBackend(Settings(cache: 16384, mmap: 0), _dir);
        Assert.Equal(-16384, ReadPragma(backend, "cache_size"));
        Assert.Equal(0, ReadPragma(backend, "mmap_size"));
        // 第二條連線也一樣（連線池關閉，每條都是新連線）
        Assert.Equal(-16384, ReadPragma(backend, "cache_size"));
    }

    [Theory]
    [InlineData(0L, null, null, "Storage:SqliteCacheSizeKb")]
    [InlineData(-1L, null, null, "Storage:SqliteCacheSizeKb")]
    [InlineData(null, -1L, null, "Storage:SqliteMmapSizeBytes")]
    [InlineData(null, null, -1L, "Storage:SqliteSoftHeapLimitBytes")]
    public void 不合法值擲設定錯誤並點名鍵(long? cache, long? mmap, long? soft, string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqlitePragmaInterceptor.ResolveMemorySettings(Settings(cache, mmap, soft)));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void 軟上限在建構後生效()
    {
        // provider 要等第一條連線開啟才初始化，先建一個不設軟上限的 backend
        _ = new StorageBackend(Settings(), _dir);
        var previous = SQLitePCL.raw.sqlite3_soft_heap_limit64(-1);
        try
        {
            _ = new StorageBackend(Settings(soft: 256L * 1024 * 1024), _dir);
            Assert.Equal(256L * 1024 * 1024, SQLitePCL.raw.sqlite3_soft_heap_limit64(-1));
        }
        finally
        {
            // 行程全域狀態：還原，避免影響並行的其他測試
            SQLitePCL.raw.sqlite3_soft_heap_limit64(previous);
        }
    }
}
