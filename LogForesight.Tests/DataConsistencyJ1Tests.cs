using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 資料一致性（任務 J-1）：主機對應替換同一交易、單一主機日寫入的鍵控鎖。
/// </summary>
public class DataConsistencyJ1Tests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private static readonly KnownIssueRule SeedDownRule =
        KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-down-availability");

    private static readonly IReadOnlySet<string> NoPatternIds = new HashSet<string>();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReplaceHostMapForDate_第二批寫入失敗_整批回滾保留原本三列()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var mapDate = new DateTime(2026, 8, 31);

        var original = new List<PrtgHostMapRow>
        {
            new() { MapDate = mapDate, DeviceObjid = 101, HostName = "SRV-1", MapStatus = "matched" },
            new() { MapDate = mapDate, DeviceObjid = 102, HostName = "SRV-2", MapStatus = "matched" },
            new() { MapDate = mapDate, DeviceObjid = 103, HostName = "SRV-3", MapStatus = "matched" }
        };
        Assert.Equal(3, store.ReplaceHostMapForDate(mapDate, original));

        // 600 列：第一批（500 列）合法、第二批含必填欄位為 null 的列，SaveChanges 會在第二批擲例外
        var broken = Enumerable.Range(1, 600)
            .Select(i => new PrtgHostMapRow
            {
                MapDate = mapDate,
                DeviceObjid = 1000 + i,
                HostName = "NEW-" + i,
                MapStatus = i == 550 ? null! : "matched"
            })
            .ToList();

        Assert.ThrowsAny<Exception>(() => store.ReplaceHostMapForDate(mapDate, broken));

        using var ctx = _fx.NewContext();
        var rows = ctx.PrtgHostMaps.Where(m => m.MapDate == mapDate).Select(m => m.DeviceObjid).OrderBy(x => x).ToList();
        Assert.Equal(new long[] { 101, 102, 103 }, rows.Select(x => (long)x).ToArray());
    }

    // 刻意用同步 Task.Wait(500)：斷言的就是「另一條執行緒被行程內鎖擋住」，不涉及 async 同步內容
#pragma warning disable xUnit1031
    [Fact]
    public void AttachPrtgFindings_同主機日的鎖被佔用時等待_釋放後完成且結果正確()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        const long hostId = 101L;
        AppendDay(store, hostId, date);

        var sig = PrtgFindingMapper.ToSignature(
            new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule), date);

        var gate = EfAnalysisRecordStore.LockFor(hostId, date);
        Task<bool> attach;
        Monitor.Enter(gate);
        try
        {
            attach = Task.Factory.StartNew(
                () => store.AttachPrtgFindings(hostId, date, new[] { sig }, NoPatternIds, out _),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Assert.False(attach.Wait(500), "鎖被佔用時 AttachPrtgFindings 不該完成");
        }
        finally
        {
            Monitor.Exit(gate);
        }

        Assert.True(attach.Wait(TimeSpan.FromSeconds(10)), "釋放鎖後 AttachPrtgFindings 應完成");
        Assert.True(attach.Result);

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal("prtg:down:2001", Assert.Single(read.TopIssues).EventKey);
        using var ctx = _fx.NewContext();
        Assert.Single(ctx.TopIssues.Where(t => t.HostId == hostId));
    }
#pragma warning restore xUnit1031

    [Fact]
    public void AttachPrtgFindings_主機日被整批刪除後_回false不擲例外()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        const long hostId = 202L;
        AppendDay(store, hostId, date);

        Assert.Equal(1, store.DeleteDays(new[] { date }));

        var sig = PrtgFindingMapper.ToSignature(
            new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule), date);
        var result = store.AttachPrtgFindings(hostId, date, new[] { sig }, NoPatternIds, out var corroborated);

        Assert.False(result);
        Assert.Equal(0, corroborated);
        using var ctx = _fx.NewContext();
        Assert.Empty(ctx.DailyRecords.Where(r => r.HostId == hostId));
        Assert.Empty(ctx.TopIssues.Where(t => t.HostId == hostId));
    }

    private static void AppendDay(EfAnalysisRecordStore store, long hostId, DateTime date) =>
        store.Append(new DailyAnalysisRecord
        {
            HostId = hostId,
            Host = "SRV-" + hostId,
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        });
}
