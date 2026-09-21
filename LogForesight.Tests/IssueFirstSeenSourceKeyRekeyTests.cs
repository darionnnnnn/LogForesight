using Xunit;

namespace LogForesight.Tests;

public sealed class IssueFirstSeenSourceKeyRekeyTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void TopIssue來源鍵回填後_舊Unicode首見日鍵合併最早日期且重跑冪等()
    {
        var oldDate = new DateTime(2026, 1, 2);
        var currentDate = new DateTime(2026, 2, 3);
        var orphanDate = new DateTime(2026, 3, 4);

        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.AddRange(
                new TopIssueRow
                {
                    RecordId = AddRecord(ctx),
                    SourceName = "évent",
                    EventId = 42,
                    RecordDate = currentDate
                },
                new TopIssueRow
                {
                    RecordId = AddRecord(ctx),
                    SourceName = "ÉVENT",
                    EventId = 42,
                    RecordDate = currentDate
                });
            ctx.IssueFirstSeen.AddRange(
                new IssueFirstSeenRow
                {
                    SourceKey = "évent",
                    EventId = 42,
                    SourceName = "évent",
                    FirstSeen = oldDate
                },
                new IssueFirstSeenRow
                {
                    SourceKey = "ÉVENT",
                    EventId = 42,
                    SourceName = "ÉVENT",
                    FirstSeen = currentDate
                },
                new IssueFirstSeenRow
                {
                    SourceKey = "孤兒來源",
                    EventId = 9001,
                    SourceName = "孤兒來源",
                    FirstSeen = orphanDate
                });
            ctx.SaveChanges();
        }

        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        backfiller.Run(CancellationToken.None);

        using (var verify = _fx.NewContext())
        {
            var rows = verify.IssueFirstSeen.OrderBy(x => x.EventId).ToList();
            Assert.Equal(2, rows.Count);
            var merged = Assert.Single(rows, x => x.EventId == 42);
            Assert.Equal("ÉVENT", merged.SourceKey);
            Assert.Equal(oldDate, merged.FirstSeen);
            var orphan = Assert.Single(rows, x => x.EventId == 9001);
            Assert.Equal("孤兒來源", orphan.SourceKey);
            Assert.Equal(orphanDate, orphan.FirstSeen);
        }

        backfiller.Run(CancellationToken.None);

        using (var verifyAgain = _fx.NewContext())
        {
            var rows = verifyAgain.IssueFirstSeen.OrderBy(x => x.EventId).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(oldDate, Assert.Single(rows, x => x.EventId == 42).FirstSeen);
            Assert.Equal(orphanDate, Assert.Single(rows, x => x.EventId == 9001).FirstSeen);
        }
    }

    [Fact]
    public void 沒有TopIssue證明的舊首見日列保留原鍵()
    {
        var firstSeen = new DateTime(2025, 12, 31);
        using (var ctx = _fx.NewContext())
        {
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
            {
                SourceKey = "évent",
                EventId = 77,
                SourceName = "évent",
                FirstSeen = firstSeen
            });
            ctx.SaveChanges();
        }

        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        backfiller.Run(CancellationToken.None);

        using var verify = _fx.NewContext();
        var row = Assert.Single(verify.IssueFirstSeen);
        Assert.Equal("évent", row.SourceKey);
        Assert.Equal(firstSeen, row.FirstSeen);
    }

    [Fact]
    public void 完成標記避免重掃且後續legacySeed會清除標記供下次重試()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = AddRecord(ctx),
                SourceName = "évent",
                EventId = 1,
                RecordDate = new DateTime(2026, 1, 1)
            });
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
            {
                SourceKey = "évent",
                EventId = 1,
                SourceName = "évent",
                FirstSeen = new DateTime(2026, 1, 1)
            });
            ctx.SaveChanges();
        }

        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        backfiller.Run(CancellationToken.None);

        using (var verify = _fx.NewContext())
            Assert.Equal("1", verify.Blobs.Single(x => x.BlobKey == SchemaUpgrader.IssueFirstSeenSourceKeyRekeyDoneBlobKey).Content);

        using (var ctx = _fx.NewContext())
        {
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = AddRecord(ctx),
                SourceName = "évent2",
                EventId = 2,
                RecordDate = new DateTime(2026, 1, 2)
            });
            ctx.SaveChanges();
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        using (var verify = _fx.NewContext())
        {
            Assert.DoesNotContain(verify.Blobs, x => x.BlobKey == SchemaUpgrader.IssueFirstSeenSourceKeyRekeyDoneBlobKey);
            Assert.Contains(verify.IssueFirstSeen, x => x.SourceKey == "éVENT2" && x.EventId == 2);
        }

        backfiller.Run(CancellationToken.None);

        using var final = _fx.NewContext();
        Assert.Equal("1", final.Blobs.Single(x => x.BlobKey == SchemaUpgrader.IssueFirstSeenSourceKeyRekeyDoneBlobKey).Content);
        Assert.Contains(final.IssueFirstSeen, x => x.SourceKey == "ÉVENT2" && x.EventId == 2);
        Assert.DoesNotContain(final.IssueFirstSeen, x => x.SourceKey == "évent2" && x.EventId == 2);
    }

    private static long AddRecord(LfDbContext ctx)
    {
        var record = new DailyRecordRow
        {
            HostId = 1,
            HostName = "A",
            RecordDate = new DateTime(2026, 2, 3),
            RiskLevel = "高",
            ContentJson = "{}"
        };
        ctx.DailyRecords.Add(record);
        ctx.SaveChanges();
        return record.RecordId;
    }
}
