using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>來源超過 255 字時，比對端與存檔鍵（SourceKeyOf）必須一致；issue_key 超長拒寫。</summary>
public sealed class SourceKeyLongSourceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly string LongSource = new string('s', 300);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DailyAnalysisRecord Record(DateTime date) => new()
    {
        Date = date,
        RiskLevel = "高",
        Headline = "h",
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = LongSource, EventId = 7, Count = 1, Severity = IssueSeverity.Critical }
        }
    };

    [Fact]
    public void UpsertFirstSeen_300字來源先晚後早_只有一列且首見日為較早日()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var late = new DateTime(2026, 10, 10);
        var early = new DateTime(2026, 10, 3);
        store.Append(Record(late));
        store.Append(Record(early));

        using var ctx = _fx.NewContext();
        var row = Assert.Single(ctx.IssueFirstSeen.ToList());
        Assert.Equal(early, row.FirstSeen);
        // SQLite 不強制欄位長度，所以要明確斷言存檔鍵是截斷後的鍵（SQL Server 會因超長而擲例外）
        Assert.Equal(WorkOrderIssueKey.SourceKeyOf(LongSource), row.SourceKey);
        Assert.Equal(255, row.SourceKey.Length);
    }

    [Fact]
    public void FirstSeenFor_300字來源能查到一筆()
    {
        // 直接以「截 255 的存檔鍵」建列，模擬正式站台實際存下來的樣子
        using (var seed = _fx.NewContext())
        {
            seed.IssueFirstSeen.Add(new IssueFirstSeenRow
            {
                SourceKey = WorkOrderIssueKey.SourceKeyOf(LongSource), EventId = 7,
                SourceName = LongSource[..200], FirstSeen = new DateTime(2026, 10, 3)
            });
            seed.SaveChanges();
        }

        var query = new EfIssueAggregateQuery(_fx.NewContext, new FakeHostStore());
        var result = query.FirstSeenFor(IssueExclusion.None, new[] { (LongSource, 7) });

        var pair = Assert.Single(result);
        Assert.Equal(new DateTime(2026, 10, 3), pair.Value);
    }

    [Fact]
    public void IsUniqueViolation_SqliteException19為真_其他為假()
    {
        Assert.True(EfAnalysisRecordStore.IsUniqueViolation(
            new DbUpdateException("x", new SqliteException("unique", 19))));
        Assert.False(EfAnalysisRecordStore.IsUniqueViolation(
            new DbUpdateException("x", new SqliteException("busy", 5))));
        Assert.False(EfAnalysisRecordStore.IsUniqueViolation(new DbUpdateException("x")));
    }

    [Fact]
    public void IssueHandling_issue_key超長_該列不寫_其他列照寫()
    {
        var store = new EfIssueHandlingStore(_fx.NewContext);
        var date = new DateTime(2026, 10, 3);
        store.SaveMany(new[]
        {
            new IssueHandling { HostName = "SRV-01", Date = date, IssueKey = new string('k', 600), Status = "in_progress", UpdatedAt = DateTime.Now },
            new IssueHandling { HostName = "SRV-01", Date = date, IssueKey = "ok", Status = "in_progress", UpdatedAt = DateTime.Now }
        });

        using var ctx = _fx.NewContext();
        var row = Assert.Single(ctx.IssueHandlings.ToList());
        Assert.Equal("ok", row.IssueKey);
    }
}
