using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪 J15：處理紀錄的寫入出口擋下問題鍵超過上限的那一筆。
/// 案件與逐日列兩張表都不會寫入超長鍵，處理紀錄若照寫就是對不到任何案件的孤立紀錄；
/// 上游只排除交辦與夜間派工，這裡守住其他所有會寫處理紀錄的路徑。
/// </summary>
public sealed class RecordHandlingOverlongKeyTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 10, 10);
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private EfRecordHandlingStore Store() =>
        new(_fx.NewContext, new EfJsonLogStore(_fx.NewContext, "handling_log"));

    private static RecordHandlingLog Log(string? issueKey) => new()
    {
        HostName = "SRV-A", Date = Day, Status = HandlingStatuses.Open, ActorAccount = "tester",
        Action = "status", IssueKey = issueKey
    };

    [Fact]
    public void 超長問題鍵的處理紀錄不寫入_同批其他紀錄照寫()
    {
        var store = Store();
        var atLimit = new string('k', IssueKeyLimit.Max);
        var overlong = new string('k', IssueKeyLimit.Max + 1);

        store.AppendLogs(new[] { Log(overlong), Log(atLimit), Log(null) });

        var logs = store.GetLogs("SRV-A", Day);
        Assert.Equal(2, logs.Count);
        Assert.DoesNotContain(logs, l => l.IssueKey == overlong);
        Assert.Contains(logs, l => l.IssueKey == atLimit);
        Assert.Contains(logs, l => l.IssueKey == null);
    }

    [Fact]
    public void 全部都是超長問題鍵時不寫任何紀錄()
    {
        var store = Store();
        store.AppendLogs(new[] { Log(new string('x', IssueKeyLimit.Max + 5)) });
        Assert.Empty(store.GetLogs("SRV-A", Day));
    }
}
