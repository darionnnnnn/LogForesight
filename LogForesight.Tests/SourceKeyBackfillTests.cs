using Xunit;

namespace LogForesight.Tests;

public sealed class SourceKeyBackfillTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void 舊列分批補來源鍵且預覽大小寫合併_重新執行冪等()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.AddRange(
                new TopIssueRow { RecordId = AddRecord(ctx), SourceName = "évent", EventId = 42, RecordDate = new DateTime(2026, 9, 1) },
                new TopIssueRow { RecordId = AddRecord(ctx), SourceName = "ÉVENT", EventId = 42, RecordDate = new DateTime(2026, 9, 2) });
            ctx.SaveChanges();
        }

        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        Assert.False(backfiller.IssueSourceKeyReady);
        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.SourceKeyProgress.Completed);
        Assert.True(backfiller.IssueSourceKeyReady);
        Assert.Equal(2, backfiller.SourceKeyProgress.Done);
        Assert.Single(backfiller.SourceMergePreview);
        Assert.Equal(new[] { "ÉVENT", "évent" }, backfiller.SourceMergePreview[0].Names);

        using (var ctx = _fx.NewContext())
            Assert.All(ctx.TopIssues.ToList(), row => Assert.Equal("ÉVENT", row.SourceKey));
        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.SourceKeyProgress.Completed);
        Assert.True(backfiller.IssueSourceKeyReady);
    }

    [Fact]
    public void 取消不修改舊列_下次可續跑()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.Add(new TopIssueRow { RecordId = AddRecord(ctx), SourceName = "Disk", EventId = 1, RecordDate = new DateTime(2026, 9, 1) });
            ctx.SaveChanges();
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        backfiller.Run(cts.Token);
        Assert.False(backfiller.SourceKeyProgress.Completed);
        Assert.False(backfiller.IssueSourceKeyReady);
        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.SourceKeyProgress.Completed);
        Assert.True(backfiller.IssueSourceKeyReady);
        using var verify = _fx.NewContext();
        Assert.Equal("DISK", verify.TopIssues.Single().SourceKey);
    }

    [Fact]
    public void 首見日種子_已回填來源鍵時合併Unicode大小寫且重跑冪等()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.AddRange(
                new TopIssueRow { RecordId = AddRecord(ctx), SourceName = "évent", SourceKey = "ÉVENT", EventId = 9,
                    RecordDate = new DateTime(2026, 8, 1) },
                new TopIssueRow { RecordId = AddRecord(ctx), SourceName = "ÉVENT", SourceKey = "ÉVENT", EventId = 9,
                    RecordDate = new DateTime(2026, 9, 1) });
            ctx.SaveChanges();
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        using (var verify = _fx.NewContext())
        {
            var row = Assert.Single(verify.IssueFirstSeen);
            Assert.Equal("ÉVENT", row.SourceKey);
            Assert.Equal(new DateTime(2026, 8, 1), row.FirstSeen);
            SchemaUpgrader.MergeIssueFirstSeenSeed(verify, force: true);
            Assert.Single(verify.IssueFirstSeen);
        }
    }

    private static long AddRecord(LfDbContext ctx)
    {
        var record = new DailyRecordRow { HostId = 1, HostName = "A", RecordDate = new DateTime(2026, 9, 1), RiskLevel = "高", ContentJson = "{}" };
        ctx.DailyRecords.Add(record);
        ctx.SaveChanges();
        return record.RecordId;
    }
}
