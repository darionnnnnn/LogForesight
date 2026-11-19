using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>帳本單列損毀的失敗隔離：只影響該主機日，記警告後覆寫，其餘主機照常寫入。</summary>
public sealed class NetiqSourceAttemptLedgerCorruptionTests : IDisposable
{
    private static readonly DateTime Day = DateTime.Today.AddDays(-1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-netiq-ledger-corrupt-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public NetiqSourceAttemptLedgerCorruptionTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "ledger.db")}"
        }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void SeedRaw(long hostId, string line)
    {
        using var ctx = _backend.CreateContext();
        ctx.LogLines.Add(new LogLineRow { LogKey = NetiqSourceAttempt.LogKey(Day, hostId), Line = line, CreatedAt = DateTime.Now });
        ctx.SaveChanges();
    }

    private static string Valid(long hostId, long runId, string status, int retries = 0) => JsonSerializer.Serialize(
        new NetiqSourceAttempt.Receipt(runId, hostId, Day, status, DateTime.UtcNow.AddMinutes(-30), RetryRuns: retries,
            RetryDay: Day == DateTime.Today ? Day : DateTime.Today, CountedRunId: runId));

    public static IEnumerable<object[]> CorruptLines()
    {
        yield return new object[] { "{malformed" };
        yield return new object[] { new string('x', 5000) };
        yield return new object[] { "null" };
        yield return new object[] { JsonSerializer.Serialize(new NetiqSourceAttempt.Receipt(5, 99_999, Day, "failed", DateTime.UtcNow)) }; // 主機對不上
        yield return new object[] { JsonSerializer.Serialize(new NetiqSourceAttempt.Receipt(5, 1, Day, "不認得的狀態", DateTime.UtcNow)) };
    }

    [Theory]
    [MemberData(nameof(CorruptLines))]
    public void 帳本一列損毀_該列被新回執覆寫_其餘主機照常寫入_不擲例外(string corruptLine)
    {
        const long bad = 1, good = 2, fresh = 3;
        SeedRaw(bad, corruptLine);
        SeedRaw(good, Valid(good, runId: 5, NetiqAttemptStatus.Failed, retries: 1));

        NetiqSourceAttempt.RecordBatch(_backend, [bad, good, fresh], Day, runId: 9, NetiqAttemptStatus.Success);

        var ledger = NetiqSourceAttempt.ReadLatest(_backend, Day, Day); // 全部列合法，讀取不再擲 InvalidDataException
        Assert.Equal(3, ledger.Count);
        Assert.All(new[] { bad, good, fresh }, id => Assert.Equal(NetiqAttemptStatus.Success, ledger[(id, Day)].Status));
        Assert.Equal(9, ledger[(bad, Day)].RunId);
        using var ctx = _backend.CreateContext();
        Assert.Equal(1, ctx.LogLines.Count(r => r.LogKey == NetiqSourceAttempt.LogKey(Day, bad))); // 覆寫同一列，不另長一列
    }

    [Fact]
    public void 帳本沒有損毀_行為不變_累計計數照舊接續()
    {
        const long host = 1;
        SeedRaw(host, Valid(host, runId: 5, NetiqAttemptStatus.Failed, retries: 1));

        NetiqSourceAttempt.RecordBatch(_backend, [host], Day, runId: 9, NetiqAttemptStatus.Failed);

        var receipt = NetiqSourceAttempt.ReadLatest(_backend, Day, Day)[(host, Day)];
        Assert.Equal(2, receipt.RetryRuns); // 上一趟 1 次＋本趟 1 次，沒有被當成損毀而歸零
        Assert.Equal(9, receipt.CountedRunId);
    }

    [Fact]
    public void 帳本回執未損毀但較新_舊趟寫入仍被略過()
    {
        const long host = 1;
        var line = Valid(host, runId: 9, NetiqAttemptStatus.Success);
        SeedRaw(host, line);

        NetiqSourceAttempt.RecordBatch(_backend, [host], Day, runId: 8, NetiqAttemptStatus.Failed);

        using var ctx = _backend.CreateContext();
        Assert.Equal(line, ctx.LogLines.Single(r => r.LogKey == NetiqSourceAttempt.LogKey(Day, host)).Line);
    }
}
