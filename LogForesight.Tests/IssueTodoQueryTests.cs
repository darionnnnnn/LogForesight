using System.Diagnostics;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 待辦的問題口徑（回饋十九輪批次D2，外部審查§一-2）：儀表板 KPI 卡改成「未處理問題 X 個」，
/// 不是「未處理風險日 M」——2000 台環境下「未處理 1,340」對使用者等於沒有資訊。
/// </summary>
public class IssueTodoQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public IssueTodoQueryTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private IssueTodoQuery Query()
    {
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var resolver = new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings);
        return new IssueTodoQuery(aggregates, resolver);
    }

    private static LogIssueSignature Issue(
        string source, int eventId, IssueSeverity severity = IssueSeverity.High,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning) => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = IssueCategory.Storage, Severity = severity, Count = 1
        };

    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord { HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList() });

    /// <summary>沒有任何標記、嚴重度落在預設「需處理」名單內 → 未處理問題 1 個，影響 1 台</summary>
    [Fact]
    public void 未標記且嚴重度需處理_計入未處理問題()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, Issue("disk", 153));

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(1, todo.OpenIssueCount);
        Assert.Equal(1, todo.AffectedHostCount);
    }

    /// <summary>低風險日不計入母體——與 HandlingHistoryQueryService.GetTodo 既有定義一致</summary>
    [Fact]
    public void 低風險日不計入母體()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", DateTime.Today, RiskLevels.Low, Issue("disk", 153));

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(0, todo.OpenIssueCount);
    }

    /// <summary>同一個問題在多台主機都未處理只算一個問題，不是兩個</summary>
    [Fact]
    public void 同問題跨主機只算一個問題_但兩台都算受影響()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        Add(a.HostId, "A", DateTime.Today, RiskLevels.High, Issue("disk", 153));
        Add(b.HostId, "B", DateTime.Today, RiskLevels.High, Issue("disk", 153));

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(1, todo.OpenIssueCount);
        Assert.Equal(2, todo.AffectedHostCount);
    }

    /// <summary>已標記結案的問題不計入未處理</summary>
    [Fact]
    public void 已結案問題不計入未處理()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        var issue = Issue("disk", 153);
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, issue);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved
        });

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(0, todo.OpenIssueCount);
        Assert.Equal(0, todo.AffectedHostCount);
    }

    /// <summary>處理中（未逾期）計入 InProgressIssueCount，不計入 OpenIssueCount／OverdueIssueCount</summary>
    [Fact]
    public void 處理中未逾期_計入處理中不計入未處理或逾期()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        var issue = Issue("disk", 153);
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, issue);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress, DueDate = DateTime.Today.AddDays(7)
        });

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(0, todo.OpenIssueCount);
        Assert.Equal(1, todo.InProgressIssueCount);
        Assert.Equal(0, todo.OverdueIssueCount);
    }

    /// <summary>處理中但預計完成日已過 → 逾期，與依問題視角/儀表板卡片同一個口徑</summary>
    [Fact]
    public void 處理中但已逾期_計入逾期()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        var issue = Issue("disk", 153);
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, issue);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress, DueDate = DateTime.Today.AddDays(-1)
        });

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(1, todo.InProgressIssueCount);
        Assert.Equal(1, todo.OverdueIssueCount);
    }

    [Fact]
    public void 沒有可行動日時回全零()
    {
        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(0, todo.OpenIssueCount);
        Assert.Equal(0, todo.InProgressIssueCount);
        Assert.Equal(0, todo.OverdueIssueCount);
        Assert.Equal(0, todo.AffectedHostCount);
    }

    /// <summary>可見範圍的授權語意與其餘 SQL 聚合一致：空集合＝零結果</summary>
    [Fact]
    public void 可見範圍_空集合為零結果()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, Issue("disk", 153));

        var todo = Query().Build(DateTime.Today.AddDays(-6), DateTime.Today, Array.Empty<long>());

        Assert.Equal(0, todo.OpenIssueCount);
    }
}
