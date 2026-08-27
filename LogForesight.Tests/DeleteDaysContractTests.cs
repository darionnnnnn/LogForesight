using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordStore.DeleteDays"/> 的合約測試。
/// 驗證刪除指定主機日的分析紀錄時，主列與 lf_top_issues 子列被一併清除，
/// 且相鄰日、處理狀態（lf_issue_handling 等）與機房首見日（lf_issue_first_seen）不受影響。
/// </summary>
public class DeleteDaysContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private IAnalysisRecordStore CreateStore(HostKey? ownerHost = null) =>
        new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory", ownerHost);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DailyAnalysisRecord Record(DateTime date, string host = "SRV-TEST", long hostId = 1, string risk = "低", List<LogIssueSignature>? issues = null) => new()
    {
        Date = date,
        Host = host,
        HostId = hostId,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}",
        TopIssues = issues ?? new List<LogIssueSignature>()
    };

    private static LogIssueSignature SampleIssue(string source = "disk", int eventId = 153) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        Category = IssueCategory.Storage,
        Severity = IssueSeverity.Critical,
        Count = 1
    };

    [Fact]
    public void DeleteDays_刪除指定日後HasRecord回false且top_issues子列一併清除()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 7, 10);
        store.Append(Record(date, issues: new List<LogIssueSignature>
        {
            SampleIssue("disk", 153),
            SampleIssue("ntfs", 55)
        }));

        Assert.True(store.HasRecord(date));
        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.TopIssues.Count());
            Assert.Equal(1, ctx.DailyRecords.Count());
        }

        var deleted = store.DeleteDays(new[] { date });

        Assert.Equal(1, deleted);
        Assert.False(store.HasRecord(date));
        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(0, ctx.TopIssues.Count());
            Assert.Equal(0, ctx.DailyRecords.Count());
        }
    }

    [Fact]
    public void DeleteDays_相鄰日不受影響_前後日紀錄與其top_issues子列皆保留()
    {
        var store = CreateStore();
        var dayBefore = new DateTime(2026, 7, 9);
        var targetDay = new DateTime(2026, 7, 10);
        var dayAfter = new DateTime(2026, 7, 11);

        store.Append(Record(dayBefore, issues: new List<LogIssueSignature> { SampleIssue("disk", 153) }));
        store.Append(Record(targetDay, issues: new List<LogIssueSignature> { SampleIssue("disk", 154) }));
        store.Append(Record(dayAfter, issues: new List<LogIssueSignature> { SampleIssue("disk", 155) }));

        var deleted = store.DeleteDays(new[] { targetDay });

        Assert.Equal(1, deleted);
        Assert.True(store.HasRecord(dayBefore));
        Assert.False(store.HasRecord(targetDay));
        Assert.True(store.HasRecord(dayAfter));

        using var ctx = _fx.NewContext();
        var remainingIssues = ctx.TopIssues.OrderBy(t => t.RecordDate).ToList();
        Assert.Equal(2, remainingIssues.Count);
        Assert.Equal(dayBefore, remainingIssues[0].RecordDate);
        Assert.Equal(153, remainingIssues[0].EventId);
        Assert.Equal(dayAfter, remainingIssues[1].RecordDate);
        Assert.Equal(155, remainingIssues[1].EventId);
    }

    [Fact]
    public void DeleteDays_冪等_連續刪除第二次回傳0且空集合回傳0()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 7, 10);
        store.Append(Record(date));

        // 空集合回傳 0 且不拋例外
        Assert.Equal(0, store.DeleteDays(Array.Empty<DateTime>()));

        // 第一次刪除回傳 1
        Assert.Equal(1, store.DeleteDays(new[] { date }));
        Assert.False(store.HasRecord(date));

        // 第二次刪除同一天回傳 0 且不擲例外
        Assert.Equal(0, store.DeleteDays(new[] { date }));

        // 刪除不存在的日期回傳 0
        Assert.Equal(0, store.DeleteDays(new[] { new DateTime(2026, 7, 20) }));
    }

    [Fact]
    public void DeleteDays_重複日期_集合含同一天多次只刪除一次並回傳1()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 7, 10);
        store.Append(Record(date));

        var dates = new[]
        {
            date,
            new DateTime(2026, 7, 10, 8, 30, 0),
            new DateTime(2026, 7, 10, 23, 59, 59)
        };

        var deleted = store.DeleteDays(dates);

        Assert.Equal(1, deleted);
        Assert.False(store.HasRecord(date));
    }

    [Fact]
    public void DeleteDays_處理狀態與首見日不被觸碰_IssueHandlings與IssueFirstSeen維持不變()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 7, 10);
        const string host = "SRV-TEST";

        store.Append(Record(date, host: host, issues: new List<LogIssueSignature> { SampleIssue("disk", 153) }));

        // 塞入一列該主機該日的處理紀錄
        using (var ctx = _fx.NewContext())
        {
            ctx.IssueHandlings.Add(new IssueHandlingRow
            {
                HostName = host,
                HostNameKey = HostNameKey.Of(host),
                RecordDate = date.Date,
                IssueKey = "System|disk|153|Error",
                Status = "in_progress",
                ActorAccount = "admin",
                Note = "處理中",
                UpdatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(1, ctx.IssueHandlings.Count());
            Assert.Equal(1, ctx.IssueFirstSeen.Count());
        }

        var deleted = store.DeleteDays(new[] { date });

        Assert.Equal(1, deleted);
        Assert.False(store.HasRecord(date));

        // 斷言處理狀態與首見日筆數不變
        using (var ctx = _fx.NewContext())
        {
            var handling = Assert.Single(ctx.IssueHandlings.ToList());
            Assert.Equal(HostNameKey.Of(host), handling.HostNameKey);
            Assert.Equal(date.Date, handling.RecordDate);
            Assert.Equal("in_progress", handling.Status);

            var firstSeen = Assert.Single(ctx.IssueFirstSeen.ToList());
            Assert.Equal("DISK", firstSeen.SourceKey);
            Assert.Equal(153, firstSeen.EventId);
        }
    }

    [Fact]
    public void DeleteDays_多日一次刪除_傳入多個日期回傳正確筆數且全數清除()
    {
        var store = CreateStore();
        var d1 = new DateTime(2026, 7, 1);
        var d2 = new DateTime(2026, 7, 2);
        var d3 = new DateTime(2026, 7, 3);
        var d4 = new DateTime(2026, 7, 4);

        store.Append(Record(d1));
        store.Append(Record(d2));
        store.Append(Record(d3));
        store.Append(Record(d4));

        var deleted = store.DeleteDays(new[] { d1, d2, d3 });

        Assert.Equal(3, deleted);
        Assert.False(store.HasRecord(d1));
        Assert.False(store.HasRecord(d2));
        Assert.False(store.HasRecord(d3));
        Assert.True(store.HasRecord(d4));
    }

    [Fact]
    public void DeleteDays_限縮主機_只刪除本機紀錄且別台主機同日紀錄不受影響()
    {
        var ownerHost = new HostKey { HostId = 10, HostName = "HOST-A" };
        var storeA = CreateStore(ownerHost);

        var date = new DateTime(2026, 7, 10);
        storeA.Append(Record(date, host: "HOST-A", hostId: 10));

        var unscopedStore = CreateStore(null);
        unscopedStore.Append(Record(date, host: "HOST-B", hostId: 20));

        var deleted = storeA.DeleteDays(new[] { date });

        Assert.Equal(1, deleted);
        Assert.False(storeA.HasRecord(date));

        // HOST-B 在同日之紀錄仍然存在
        using var ctx = _fx.NewContext();
        var remaining = ctx.DailyRecords.Single();
        Assert.Equal("HOST-B", remaining.HostName);
        Assert.Equal(20, remaining.HostId);
    }
}
