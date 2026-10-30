using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 59 輪 B6：Storage.Type 大小寫一致、紀錄分頁唯一次要排序鍵。</summary>
public class StorageTypeCaseAndPagingTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("SQLITE")]
    [InlineData(" Sqlite ")]
    public void Type大小寫不同仍走Sqlite後端並可讀寫(string type)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-b6-type-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new StorageSettings { Type = type, ConnectionString = $"Data Source={Path.Combine(dir, "t.db")}" };
            Assert.True(settings.IsSqlite);
            Assert.False(settings.IsSqlServer);
            var backend = new StorageBackend(settings, dir);
            var blob = backend.Blob("b6_blob");
            blob.Mutate<int>(_ => ("hello", 0));
            Assert.Equal("hello", blob.Read());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    public void SqlServer判定不分大小寫(string type)
    {
        var s = new StorageSettings { Type = type };
        Assert.True(s.IsSqlServer);
        Assert.False(s.IsSqlite);
    }

    [Theory]
    [InlineData("date", true)]
    [InlineData("date", false)]
    [InlineData("host", true)]
    [InlineData("host", false)]
    [InlineData("risk", true)]
    [InlineData("risk", false)]
    [InlineData(null, false)]
    public void QueryPage翻頁無重複無遺漏(string? sort, bool ascending)
    {
        using var fx = new EfSqliteFixture();
        var store = new EfAnalysisRecordStore(fx.NewContext, "test");
        var date = new DateTime(2026, 7, 1);
        // 120 台主機同日同風險：主排序鍵全部相同，只靠 RecordId 當最後一鍵才能穩定翻頁
        var sameHost = sort == "host";
        for (var i = 1; i <= 120; i++)
            store.Append(new DailyAnalysisRecord
            {
                HostId = sameHost ? 1 : i,
                Host = sameHost ? "SAME-HOST" : $"H{i}",
                Date = sameHost ? date.AddDays(-i) : date,
                RiskLevel = RiskLevels.High
            });

        var seen = new List<long>();
        for (var p = 1; p <= 5; p++)
            seen.AddRange(store.QueryPage(new RecordQueryFilter(), p, 25, sort, ascending).Items.Select(r => r.RecordId));

        Assert.Equal(120, seen.Count);
        Assert.Equal(120, seen.Distinct().Count());
        var last = store.QueryPage(new RecordQueryFilter(), 5, 25, sort, ascending);
        Assert.Equal(120, last.Total);
    }
}
