using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 清理的分批與上限（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E2）。
///
/// 改動前兩支 Prune 都是「<c>.ToList()</c> 整批載入實體 → RemoveRange」——正常每晚只刪一天份
/// 沒問題，但停機幾天沒跑或調短保留天數時，一次要刪數十萬列，等於先把數 GB 的
/// ContentJson／log 文字讀進記憶體只為了刪掉它們，且是一筆超長交易。
///
/// 改動後：只撈主鍵、分批 <c>ExecuteDelete</c>、單次執行有列數上限，超過的留待下次。
/// 這組測試釘住的是**上限與分批確實生效**，以及**清理是冪等可續跑的**——
/// 沒有後者，「分幾晚刪完」這個設計就不成立。
/// </summary>
public class PruneBudgetTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // ── 分析紀錄 ────────────────────────────────────────────────────────────

    private EfAnalysisRecordStore RecordStore() => new(_fixture.NewContext, "test");

    private void AppendOldRecords(EfAnalysisRecordStore store, int count, int daysAgo)
    {
        for (var i = 0; i < count; i++)
        {
            store.Append(new DailyAnalysisRecord
            {
                HostId = 1,
                Host = "SRV-A",
                Date = DateTime.Today.AddDays(-daysAgo - i),
                RiskLevel = RiskLevels.Low,
                TopIssues = new List<LogIssueSignature>
                {
                    new() { LogName = "System", Source = "S", EventId = 1, Count = 1 }
                }
            });
        }
    }

    [Fact]
    public void 紀錄清理_超過上限的部分留待下次_且可續跑刪光()
    {
        var store = RecordStore();
        AppendOldRecords(store, count: 10, daysAgo: 100);

        // 上限 4、批次 3：一次只刪得掉 4 筆
        var first = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 3);
        Assert.Equal(4, first);
        Assert.Equal(6, store.Query(new RecordQueryFilter()).Count);

        // 續跑：清理是冪等的，剩下的下次繼續刪
        var second = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 3);
        var third = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 3);
        Assert.Equal(4, second);
        Assert.Equal(2, third);
        Assert.Empty(store.Query(new RecordQueryFilter()));
    }

    /// <summary>
    /// 刪主表不能留下孤兒的 lf_top_issues 列。舊寫法靠 FK cascade，但 cascade 是否生效
    /// 取決於 provider 與連線設定（SQLite 需 PRAGMA foreign_keys=ON），兩個後端不保證一致；
    /// 改為顯式先刪子表後，這件事在哪個後端都成立。
    /// </summary>
    [Fact]
    public void 紀錄清理_不留下孤兒的問題子列()
    {
        var store = RecordStore();
        AppendOldRecords(store, count: 3, daysAgo: 100);
        AppendOldRecords(store, count: 2, daysAgo: 1);   // 保留期內，不該被刪

        using (var ctx = _fixture.NewContext())
            Assert.Equal(5, ctx.TopIssues.Count());

        store.Prune(retentionDays: 90);

        using (var ctx = _fixture.NewContext())
        {
            Assert.Equal(2, ctx.DailyRecords.Count());
            // 子列數必須與剩下的主列對得上——多出來的就是孤兒
            Assert.Equal(2, ctx.TopIssues.Count());
            var liveRecordIds = ctx.DailyRecords.Select(r => r.RecordId).ToList();
            Assert.All(ctx.TopIssues.ToList(), t => Assert.Contains(t.RecordId, liveRecordIds));
        }
    }

    [Fact]
    public void 紀錄清理_保留期內的紀錄不受影響()
    {
        var store = RecordStore();
        AppendOldRecords(store, count: 2, daysAgo: 100);
        AppendOldRecords(store, count: 3, daysAgo: 1);

        var removed = store.Prune(retentionDays: 90);

        Assert.Equal(2, removed);
        Assert.Equal(3, store.Query(new RecordQueryFilter()).Count);
    }

    // ── append-only log ─────────────────────────────────────────────────────

    private void Backdate(string key, DateTime createdAt)
    {
        using var ctx = _fixture.NewContext();
        foreach (var row in ctx.LogLines.Where(l => l.LogKey == key).ToList())
            row.CreatedAt = createdAt;
        ctx.SaveChanges();
    }

    [Fact]
    public void 歷程清理_超過上限的部分留待下次_且可續跑刪光()
    {
        var store = _fixture.LogStore("bulk");
        for (var i = 0; i < 10; i++) store.AppendLine($"line-{i}");
        Backdate("bulk", DateTime.Today.AddDays(-100));

        Assert.Equal(4, store.Prune(DateTime.Today.AddDays(-90), maxRows: 4, batchSize: 3));
        Assert.Equal(6, store.ReadLines().Count);

        Assert.Equal(4, store.Prune(DateTime.Today.AddDays(-90), maxRows: 4, batchSize: 3));
        Assert.Equal(2, store.Prune(DateTime.Today.AddDays(-90), maxRows: 4, batchSize: 3));
        Assert.Empty(store.ReadLines());
    }

    /// <summary>分批刪除必須依 seq 由舊到新——不然「刪到一半停下」會留下時間上不連續的破洞</summary>
    [Fact]
    public void 歷程清理_由舊到新刪除()
    {
        var store = _fixture.LogStore("ordered");
        for (var i = 0; i < 6; i++) store.AppendLine($"line-{i}");
        Backdate("ordered", DateTime.Today.AddDays(-100));

        store.Prune(DateTime.Today.AddDays(-90), maxRows: 2, batchSize: 2);

        Assert.Equal(new[] { "line-2", "line-3", "line-4", "line-5" }, store.ReadLines());
    }
}
