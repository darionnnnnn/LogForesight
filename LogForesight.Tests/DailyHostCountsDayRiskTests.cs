using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// A13：機房基準的每日台數（<see cref="IIssueAggregateQuery.DailyHostCounts"/>）要與主清單同一組日風險等級。
/// 「一台」＝該日該問題出現過的相異存活主機。限縮日風險等級後，基準每日台數不得超過期間主機數。
/// </summary>
public class DailyHostCountsDayRiskTests : IDisposable
{
    private static readonly DateTime Anchor = DateTime.Today.AddDays(-1);
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsService _settings = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private static readonly (string, int)[] Disk = { ("disk", 153) };

    public DailyHostCountsDayRiskTests() => _records = new EfAnalysisRecordStore(_fx.NewContext, "test");

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private WebHost Host(string name)
    {
        var h = TestData.AddHost(_hosts, name);
        h.GroupIds = new List<long> { 1 };
        return h;
    }

    private void Add(WebHost h, int daysAgo, string risk) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = h.HostId, Host = h.HostName, Date = Anchor.AddDays(-daysAgo), RiskLevel = risk,
            TopIssues = new() { new LogIssueSignature { LogName = "System", Source = "disk", EventId = 153, Count = 1, Severity = IssueSeverity.High } }
        });

    /// <summary>A：連三天（-2、-3、-4）高；B：同三天低。限縮到「高」時每天只剩 A（基準至少要 3 個出現日才算數）。</summary>
    private void Seed()
    {
        var a = Host("A");
        var b = Host("B");
        foreach (var d in new[] { 2, 3, 4 })
        {
            Add(a, d, RiskLevels.High);
            Add(b, d, RiskLevels.Low);
        }
    }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext, _hosts);

    private static readonly IReadOnlySet<string> HighOnly = new HashSet<string> { RiskLevels.High };

    [Fact]
    public void 不限日風險等級_兩台三天都算()
    {
        Seed();
        var rows = Query().DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, null);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.HostCount));
    }

    [Fact]
    public void 限縮到高_只算高風險日的主機()
    {
        Seed();
        var rows = Query().DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, HighOnly);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.HostCount));
    }

    [Fact]
    public void 限縮到高_該日只有低風險日的問題不產生列()
    {
        var b = Host("B");
        Add(b, 2, RiskLevels.Low);
        Assert.Empty(Query().DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, HighOnly));
        Assert.Single(Query().DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, null));
    }

    [Fact]
    public void 日風險等級空集合是零結果_不是不限制()
    {
        Seed();
        Assert.Empty(Query().DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, new HashSet<string>()));
    }

    [Fact]
    public void 基準每日台數不超過同條件期間主機數_SQL層()
    {
        Seed();
        var q = Query();
        var aggregate = Assert.Single(q.Aggregate(IssueExclusion.None, Anchor.AddDays(-6), Anchor, null, null, HighOnly));
        var daily = q.DailyHostCounts(IssueExclusion.None, Disk, Anchor.AddDays(-6), Anchor, null, HighOnly);
        Assert.True(daily.Max(d => d.HostCount) <= aggregate.HostCount);
    }

    [Fact]
    public void 問題查詢_限縮日風險等級時基準台數不超過期間主機數()
    {
        Seed();
        _settings.VisibleDayRiskLevels = new HashSet<string> { RiskLevels.High };
        var visibility = new AlwaysVisibleService(_hosts);
        var users = new FakeUserStore();
        var repository = new LogForesight.Web.Repositories.RecordRepository(_records, _hosts, visibility, _settings);
        var aggregates = Query();
        var cases = new FakeIssueCaseStore();
        var issueHandling = new EfIssueHandlingStore(_fx.NewContext);
        var handling = new EfRecordHandlingStore(_fx.NewContext, new EfJsonLogStore(_fx.NewContext, "handling"));
        var list = new RecordListQueryService(
            repository, _hosts, users, handling, issueHandling, cases, _settingsStore,
            _settings, visibility, aggregates, new OccurrenceStatusResolver(_hosts, issueHandling, cases, _settingsStore),
            new UserDisplayNameService(_settingsStore), new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None));

        var result = list.SearchByIssue(new RecordSearchRequest { From = Anchor.AddDays(-6), To = Anchor });

        var group = Assert.Single(result.Items);
        Assert.Equal(1, group.HostCount);
        Assert.True(group.BaselineLatestHostCount <= group.HostCount,
            $"基準最近一日台數 {group.BaselineLatestHostCount} 超過期間主機數 {group.HostCount}");
    }

    [Fact]
    public void 排行與主聚合同一個口徑_不限日風險等級時基準台數可大於零()
    {
        Seed();
        var builder = new IssueRankingBuilder(Query(), _hosts, new FixedIssueExclusionSource(IssueExclusion.None));
        var ranking = Assert.Single(builder.Build(Anchor.AddDays(-6), Anchor, null, totalHosts: 2));
        Assert.Equal(2, ranking.HostCount);
        Assert.True(ranking.BaselineLatestHostCount <= ranking.HostCount);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void DailyHostCounts的日風險等級條件兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var f = Anchor.AddDays(-6);
        // 與 DailyHostCounts 同一個出口（ApplyRiskLevels），不另寫一份條件
        var q = EfIssueAggregateQuery.ApplyRiskLevels(ctx, ctx.TopIssues.AsNoTracking()
            .Where(x => x.RecordDate >= f && x.RecordDate <= Anchor), f, Anchor, HighOnly);
        var sql = q.Select(x => new { x.HostId, x.SourceKey, x.SourceName, x.EventId, x.RecordDate }).ToQueryString();
        Assert.Contains("SELECT", sql.Split("WHERE", 2)[1], StringComparison.OrdinalIgnoreCase);
    }
}
