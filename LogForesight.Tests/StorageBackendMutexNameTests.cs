using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 64 輪 A-1：schema 互斥鎖只在 SQLite 依資料檔分名，SQL Server 維持原常數。</summary>
public class StorageBackendMutexNameTests
{
    private const string SqlServerName = @"Global\LogForesight-Schema";

    [Fact]
    public void SqlServer_鎖名維持原常數_與連線字串寫法無關()
    {
        Assert.Equal(SqlServerName, StorageBackend.BuildSchemaMutexName(false, "Server=a;Database=b"));
        Assert.Equal(SqlServerName, StorageBackend.BuildSchemaMutexName(false, "server=A;database=B;Pooling=true"));
    }

    [Fact]
    public void Sqlite_同一檔案的相對與絕對路徑_鎖名相同且不等於原常數()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-mutex-" + Guid.NewGuid().ToString("N"));
        var abs = Path.Combine(dir, "a.db");
        var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), abs);
        var byAbs = StorageBackend.BuildSchemaMutexName(true, $"Data Source={abs}");
        var byRel = StorageBackend.BuildSchemaMutexName(true, $"Data Source={rel}");
        Assert.Equal(byAbs, byRel);
        Assert.NotEqual(SqlServerName, byAbs);
        Assert.StartsWith(SqlServerName + "-", byAbs);
        Assert.DoesNotContain(dir, byAbs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sqlite_不同檔案_鎖名不同()
    {
        var a = StorageBackend.BuildSchemaMutexName(true, @"Data Source=C:\x\a.db");
        var b = StorageBackend.BuildSchemaMutexName(true, @"Data Source=C:\x\b.db");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sqlite_記憶體資料庫_以連線字串分名()
    {
        var a = StorageBackend.BuildSchemaMutexName(true, "Data Source=:memory:");
        var b = StorageBackend.BuildSchemaMutexName(true, "Data Source=shared-one;Mode=Memory;Cache=Shared");
        Assert.NotEqual(a, b);
        Assert.StartsWith(SqlServerName + "-", a);
    }

    [Fact]
    public void Sqlite_大小寫異寫的同一路徑_鎖名相同()
    {
        var a = StorageBackend.BuildSchemaMutexName(true, @"Data Source=C:\Data\x.db");
        var b = StorageBackend.BuildSchemaMutexName(true, @"Data Source=c:\data\X.DB");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Sqlite_路徑含memory字樣的檔案_仍依路徑分名()
    {
        var a = StorageBackend.BuildSchemaMutexName(true, @"Data Source=C:\memory\x.db");
        var b = StorageBackend.BuildSchemaMutexName(true, @"Data Source=c:\MEMORY\x.db;Pooling=False");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("Data Source=file:/C:/x/a.db")]
    [InlineData("這不是連線字串")]
    [InlineData("")]
    public void Sqlite_無法可靠正規化_退回原常數(string connectionString)
    {
        Assert.Equal(SqlServerName, StorageBackend.BuildSchemaMutexName(true, connectionString));
    }
}
