using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>第 78 輪批次 5a 的 5-3：前一期只算用到的欄位（輕量聚合 <see cref="IIssueAggregateQuery.AggregateHostTotals"/>）。</summary>
public sealed class IssueAggregateHostTotalsTests : IDisposable
{
    private static readonly DateTime End = new(2026, 8, 28);

    private readonly ITestOutputHelper _output;
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly SqlCommandRecorder _recorder = new();

    public IssueAggregateHostTotalsTests(ITestOutputHelper output)
    {
        _output = output;
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private LfDbContext Recording() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_fx.NewContext().Database.GetDbConnection()).AddInterceptors(_recorder).Options);

    private EfIssueAggregateQuery Query(bool ready = true, bool recording = true) =>
        new(recording ? Recording : _fx.NewContext, _hosts, null, () => ready);

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.Medium, int count = 1) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = LogEntryType.Warning,
        Category = IssueCategory.Other, Severity = severity, Count = count
    };

    private void Add(long hostId, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = $"H{hostId}", Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList()
        });

    private void Add(long hostId, DateTime date, params LogIssueSignature[] issues) => Add(hostId, date, RiskLevels.High, issues);

    private List<long> SeedHosts(int hostCount, int days)
    {
        var ids = new List<long>();
        for (var h = 0; h < hostCount; h++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"SRV-{h:D3}", Active = true });
            ids.Add(host.HostId);
            for (var d = 0; d < days; d++)
                Add(host.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.Medium, 2), Issue("cpu", 100), Issue("net", 404, IssueSeverity.High));
        }
        return ids;
    }

    private (long A, long B, long Tomb, long Surv) SeedMergeScenario(int days)
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var surv = _hosts.Upsert(new WebHost { HostName = "SURV" });
        var tomb = _hosts.Upsert(new WebHost { HostName = "TOMB", MergedInto = surv.HostId, Active = false });
        for (var d = 0; d < days; d++)
        {
            Add(a.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.Low), Issue("cpu", 100));
            Add(b.HostId, End.AddDays(-d), RiskLevels.Low, Issue("disk", 153, IssueSeverity.Medium, 2));
            if (d % 2 == 0) Add(tomb.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.High, 3));
            if (d % 2 == 1) Add(surv.HostId, End.AddDays(-d), Issue("disk", 153));
        }
        return (a.HostId, b.HostId, tomb.HostId, surv.HostId);
    }

    [Fact]
    public void A7_HostTotals與Aggregate的主機數與總次數逐欄相同_含合併與各母體條件()
    {
        var m = SeedMergeScenario(8);
        var from = End.AddDays(-7);
        var high = new HashSet<IssueSeverity> { IssueSeverity.High, IssueSeverity.Medium };
        var riskHigh = new HashSet<string> { RiskLevels.High };
        var query = Query(recording: false);

        foreach (var (hosts, sev, risk) in new (long[]?, IReadOnlySet<IssueSeverity>?, IReadOnlySet<string>?)[]
                 { (null, null, null), (new[] { m.A, m.Surv }, null, null), (null, high, null), (null, null, riskHigh),
                   (new[] { m.B }, high, riskHigh) })
        {
            var expected = query.Aggregate(IssueExclusion.None, from, End, hosts, sev, risk)
                .Select(a => (IssueProfile.KeyOf(a.Source, a.EventId), a.HostCount, a.TotalCount))
                .OrderBy(x => x.Item1.Item1, StringComparer.Ordinal).ThenBy(x => x.Item1.Item2).ToArray();
            var actual = query.AggregateHostTotals(IssueExclusion.None, from, End, hosts, sev, risk)
                .Select(a => (IssueProfile.KeyOf(a.Source, a.EventId), a.HostCount, a.TotalCount))
                .OrderBy(x => x.Item1.Item1, StringComparer.Ordinal).ThenBy(x => x.Item1.Item2).ToArray();
            Assert.Equal(expected, actual);
        }
        Assert.Empty(query.AggregateHostTotals(IssueExclusion.None, from, End, Array.Empty<long>()));
    }

    [Fact]
    public void A7_HostTotals_靜音排除生效且大小寫不同的同名來源合併()
    {
        var h1 = _hosts.Upsert(new WebHost { HostName = "A" });
        var h2 = _hosts.Upsert(new WebHost { HostName = "B" });
        Add(h1.HostId, End, Issue("disk", 153, count: 2), Issue("cron", 7));
        Add(h2.HostId, End, Issue("DISK", 153, count: 3), Issue("cron", 7));
        var mute = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "cron", EventId = 7, Mutes = { new MuteInterval { From = End, To = End } } }
        }, End);

        var rows = Query().AggregateHostTotals(mute, End, End, null);

        var disk = Assert.Single(rows);
        Assert.Equal(("DISK", 153), IssueProfile.KeyOf(disk.Source, disk.EventId));
        Assert.Equal(2, disk.HostCount);
        Assert.Equal(5, disk.TotalCount);
    }

    [Fact]
    public void A7_介面預設實作以Aggregate投影_既有替身不必改()
    {
        IIssueAggregateQuery fake = new FakeIssueAggregateQuery   // 只實作 Aggregate，沒有覆寫新方法
        {
            Result = new List<IssueAggregate> { new() { Source = "disk", EventId = 153, HostCount = 3, TotalCount = 40 } }
        };

        var row = Assert.Single(fake.AggregateHostTotals(IssueExclusion.None, End, End, null));

        Assert.Equal(("disk", 153, 3, 40L), (row.Source, row.EventId, row.HostCount, row.TotalCount));
    }

    [Fact]
    public void A7_前一期改用輕量聚合_指令數低於前一期完整Aggregate()
    {
        SeedHosts(6, 10);
        var query = Query();
        var from = End.AddDays(-4);
        query.Aggregate(IssueExclusion.None, from, End, null);
        query.AggregateHostTotals(IssueExclusion.None, from, End, null);

        var full = _recorder.Measure(() => query.Aggregate(IssueExclusion.None, from, End, null));
        var light = _recorder.Measure(() => query.AggregateHostTotals(IssueExclusion.None, from, End, null));

        _output.WriteLine($"前一期 完整 Aggregate 指令={full.Commands} 讀取={full.ReadCalls}；輕量 指令={light.Commands} 讀取={light.ReadCalls}");
        Assert.True(light.Commands < full.Commands);
        Assert.True(light.ReadCalls < full.ReadCalls);
    }

    [Fact]
    public void A7_排行一次Build的指令數_前一期改輕量()
    {
        SeedHosts(5, 14);
        var from = End.AddDays(-6);
        var aggregates = Query();
        var builder = new IssueRankingBuilder(aggregates, _hosts, new FixedIssueExclusionSource(IssueExclusion.None));
        builder.Build(from, End, null, totalHosts: 5);   // 暖機

        var (commands, _) = _recorder.Measure(() => builder.Build(from, End, null, totalHosts: 5));

        // 改動前 13 道（本期 Aggregate 5＋前期 Aggregate 5＋三道其他）；本期 Aggregate 因主機數與主機日數併成一道而為 4，
        // 前期改輕量為 2，合計 4＋2＋3＝9 道
        Assert.Equal(9, commands);
    }
}
