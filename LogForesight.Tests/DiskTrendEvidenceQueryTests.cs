using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class DiskTrendEvidenceQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    [Fact]
    public void SQLServerProvider_最新日期分組與join可翻譯()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var sql = EfAnalysisRecordStore.BuildLatestDiskTrendRows(ctx,
            new[] { (11L, "prtg:disk_free_trend:123"), (22L, "prtg:disk_free_trend:456") },
            DateTime.Today.AddDays(-29), DateTime.Today).ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source_key", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("host_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SQLite_只還原授權主機_sensor_最新嚴格簽章並忽略大量無關JSON()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var today = DateTime.Today;
        Append(11, "HOST-A", today.AddDays(-2), "123", "A-old");
        Append(11, "HOST-A", today, "123", new string('A', 260));
        Append(11, "HOST-A", today, "456", "wrong sensor");
        Append(22, "HOST-B", today.AddDays(-1), "456", "B-456");
        Append(22, "HOST-B", today, "123", "cross pair");
        Append(11, "HOST-A", today, "123", "forged source", source: "PRTG:warning");
        Append(33, "HOST-C", today, "789", "actual disk trend");
        Append(33, "HOST-C", today.AddDays(1), "789", "low-water only",
            ruleId: "builtin-prtg-resource-disk-pressure", reasons: ["disk-two-hour-low-water"]);

        // 30 日的無關資料故意放入無效 JSON：若查詢載入並 Deserialize 這些列，測試會直接失敗。
        using (var ctx = _fx.NewContext())
        {
            for (var i = 0; i < 1500; i++)
            {
                ctx.DailyRecords.Add(new DailyRecordRow
                {
                    HostId = 1000 + i, HostName = $"DECOY-{i}",
                    RecordDate = today.AddDays(-(i % 30)), ContentJson = "not-json"
                });
            }
            ctx.SaveChanges();
        }

        var timer = Stopwatch.StartNew();
        var result = store.QueryDiskTrendEvidence(new[] { (11L, "123"), (22L, "456") },
            today.AddDays(-29), today.AddDays(1));
        timer.Stop();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.HostId == 11 && x.SensorId == "123"
            && x.RecordDate == today && x.Detail == new string('A', 200));
        Assert.Contains(result, x => x.HostId == 22 && x.SensorId == "456"
            && x.Detail == "B-456");
        Assert.DoesNotContain(result, x => x.HostId == 11 && x.SensorId == "456");
        Assert.DoesNotContain(result, x => x.HostId == 22 && x.SensorId == "123");
        Assert.DoesNotContain(result, x => x.HostId == 33 && x.SensorId == "789");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"SQLite scoped evidence query took {timer.Elapsed}.");
    }

    [Fact]
    public void 新來源資源世代識別可查證且不誤配相似sensor編號()
    {
        var day = DateTime.Today;
        Append(11, "H", day, "123:source:resource", "generation evidence");
        Append(11, "H", day, "1234:source:resource", "wrong sensor");
        var result = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory")
            .QueryDiskTrendEvidence([(11L, "123")], day, day);
        Assert.Equal("generation evidence", Assert.Single(result).Detail);
    }

    private void Append(long hostId, string host, DateTime date, string sensor, string detail,
        string source = "PRTG:disk_free_trend", string? ruleId = null,
        List<string>? reasons = null) =>
        new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory").Append(new DailyAnalysisRecord
        {
            HostId = hostId,
            Host = host,
            Date = date,
            TopIssues = new()
            {
                new LogIssueSignature
                {
                    Source = source,
                    LogName = "PRTG",
                    EventId = 0,
                    EntryType = EventLogEntryType.Warning,
                    EventKey = $"prtg:disk_free_trend:{sensor}",
                    RuleId = ruleId,
                    PrtgResourceReasonCodes = reasons,
                    SampleMessages = new() { detail }
                }
            }
        });

    public void Dispose() => _fx.Dispose();
}
