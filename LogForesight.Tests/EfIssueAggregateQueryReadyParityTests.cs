using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// K 驗收：同一份 source_key 已回填資料，對照 EfIssueAggregateQuery 的 legacy
/// （source_name.ToUpper）與 ready（source_key）路徑。這份檔案只驗證既有契約，
/// 不修改正式查詢碼，也不把耗時差異設成脆弱的門檻斷言。
/// </summary>
public sealed class EfIssueAggregateQueryReadyParityTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 1);
    private readonly EfSqliteFixture _fixture = new();
    private readonly FakeHostStore _hosts = new();
    private long _nextRecordId = 1;

    public EfIssueAggregateQueryReadyParityTests(ITestOutputHelper output) => Output = output;

    private ITestOutputHelper Output { get; }

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfIssueAggregateQuery Query(bool sourceKeyReady) =>
        new(_fixture.NewContext, _hosts, null, () => sourceKeyReady);

    [Fact]
    public void 大小寫一致資料_舊與Ready路徑的可比結果一致()
    {
        SeedComparableRows();

        var mute = IssueExclusion.From(new[]
        {
            new IssueProfile
            {
                SourceName = "network",
                EventId = 404,
                Mutes = { new MuteInterval { From = Day, To = Day.AddDays(2) } }
            }
        }, Day);

        var old = Query(sourceKeyReady: false);
        var ready = Query(sourceKeyReady: true);

        Assert.Equal(
            NormalizeAggregates(old.Aggregate(IssueExclusion.None, Day, Day.AddDays(2), null)),
            NormalizeAggregates(ready.Aggregate(IssueExclusion.None, Day, Day.AddDays(2), null)));

        Assert.Equal(
            old.CountCurrentlyMutedIssues(mute, Day, Day.AddDays(2), null, null, null),
            ready.CountCurrentlyMutedIssues(mute, Day, Day.AddDays(2), null, null, null));
        Assert.Equal(
            NormalizeMuted(old.CurrentlyMutedIssues(mute, Day, Day.AddDays(2), null, null, null)),
            NormalizeMuted(ready.CurrentlyMutedIssues(mute, Day, Day.AddDays(2), null, null, null)));

        Assert.Equal(
            NormalizeCategories(old.AggregateByCategory(IssueExclusion.None, Day, Day.AddDays(2), null, null)),
            NormalizeCategories(ready.AggregateByCategory(IssueExclusion.None, Day, Day.AddDays(2), null, null)));

        var wanted = new[] { (Source: "disk", EventId: 153) };
        Assert.Equal(
            NormalizeHostIds(old.HostIdsFor(IssueExclusion.None, wanted, Day, Day.AddDays(2))),
            NormalizeHostIds(ready.HostIdsFor(IssueExclusion.None, wanted, Day, Day.AddDays(2))));
        Assert.Equal(
            NormalizeHostIdsByIssue(old.HostIdsByIssue(IssueExclusion.None, wanted, Day, Day.AddDays(2), null)),
            NormalizeHostIdsByIssue(ready.HostIdsByIssue(IssueExclusion.None, wanted, Day, Day.AddDays(2), null)));
        Assert.Equal(
            old.IssueHostDayCount(IssueExclusion.None, "DISK", 153, Day, Day.AddDays(2), null, null, null),
            ready.IssueHostDayCount(IssueExclusion.None, "DISK", 153, Day, Day.AddDays(2), null, null, null));
        Assert.Equal(
            NormalizeDailyHostCounts(old.DailyHostCounts(IssueExclusion.None, wanted, Day, Day.AddDays(2), null)),
            NormalizeDailyHostCounts(ready.DailyHostCounts(IssueExclusion.None, wanted, Day, Day.AddDays(2), null)));
    }

    [Fact]
    public void 權限與去重反例_空集合不放大且大小寫變體只算一個問題()
    {
        SeedCaseVariantRows();

        foreach (var sourceKeyReady in new[] { false, true })
        {
            var query = Query(sourceKeyReady);
            var scoped = query.Aggregate(IssueExclusion.None, Day, Day.AddDays(1), new[] { 1L });
            var issue = Assert.Single(scoped);

            Assert.Equal(1, issue.HostCount);
            Assert.Equal(2, issue.DayCount);
            Assert.Equal(3, issue.TotalCount);
            Assert.Empty(query.Aggregate(IssueExclusion.None, Day, Day.AddDays(1), Array.Empty<long>()));

            var scopedByIssue = query.HostIdsByIssue(
                IssueExclusion.None, new[] { (Source: "dIsK", EventId: 153) }, Day, Day.AddDays(1), new[] { 1L });
            Assert.Equal(new[] { 1L }, scopedByIssue[("DISK", 153)].OrderBy(id => id));
        }
    }

    [Fact]
    public void 大量資料_記錄舊與Ready路徑耗時數值但不設效能門檻()
    {
        const int hostCount = 1200;
        const int dayCount = 6;
        const int issueKindsPerDay = 3;
        SeedLargeDataset(hostCount, dayCount, issueKindsPerDay);

        var from = Day;
        var to = Day.AddDays(dayCount - 1);
        var old = Query(sourceKeyReady: false);
        var ready = Query(sourceKeyReady: true);

        // 先讓兩條路徑各完成一次 EF/SQLite 初始化，避免把冷啟動成本誤當查詢差異。
        _ = old.Aggregate(IssueExclusion.None, from, to, null);
        _ = ready.Aggregate(IssueExclusion.None, from, to, null);

        var oldWatch = Stopwatch.StartNew();
        var oldResult = old.Aggregate(IssueExclusion.None, from, to, null);
        oldWatch.Stop();

        var readyWatch = Stopwatch.StartNew();
        var readyResult = ready.Aggregate(IssueExclusion.None, from, to, null);
        readyWatch.Stop();

        Assert.Equal(NormalizeAggregates(oldResult), NormalizeAggregates(readyResult));
        Output.WriteLine($"rows={hostCount * dayCount * issueKindsPerDay}; old={oldWatch.Elapsed.TotalMilliseconds:F2} ms; ready={readyWatch.Elapsed.TotalMilliseconds:F2} ms");
    }

    private void SeedComparableRows()
    {
        SeedRows(
            Row("disk", "DISK", 153, Day, 1, "Storage", 2),
            Row("disk", "DISK", 153, Day.AddDays(1), 2, "Storage", 3),
            Row("disk", "DISK", 153, Day.AddDays(2), 1, "Storage", 4),
            Row("network", "NETWORK", 404, Day, 1, "Network", 5),
            Row("network", "NETWORK", 404, Day.AddDays(1), 2, "Network", 6));
    }

    private void SeedCaseVariantRows()
    {
        SeedRows(
            Row("disk", "DISK", 153, Day, 1, "Storage", 1),
            Row("DISK", "DISK", 153, Day, 1, "Storage", 1),
            Row("DiSk", "DISK", 153, Day.AddDays(1), 1, "Storage", 1),
            Row("network", "NETWORK", 404, Day, 2, "Network", 9));
    }

    private void SeedLargeDataset(int hostCount, int dayCount, int issueKindsPerDay)
    {
        var rows = new List<TopIssueRow>(hostCount * dayCount * issueKindsPerDay);
        for (var host = 1; host <= hostCount; host++)
        {
            for (var day = 0; day < dayCount; day++)
            {
                for (var issue = 0; issue < issueKindsPerDay; issue++)
                {
                    var source = issue == 0 ? "disk" : issue == 1 ? "network" : "application";
                    var key = WorkOrderIssueKey.SourceKeyOf(source);
                    rows.Add(Row(source, key, 153 + issue, Day.AddDays(day), host, issue == 0 ? "Storage" : "Other", issue + 1));
                }
            }
        }

        SeedRows(rows.ToArray());
    }

    private TopIssueRow Row(string source, string sourceKey, int eventId, DateTime date, long hostId, string category, int eventCount) =>
        new()
        {
            RecordId = _nextRecordId++,
            SourceName = source,
            SourceKey = sourceKey,
            EventId = eventId,
            Category = category,
            SeverityRank = (int)IssueSeverity.Medium,
            HostId = hostId,
            RecordDate = date,
            EventCount = eventCount,
            ElevatesDayRisk = false,
            LogName = "System",
            EntryType = (int)System.Diagnostics.EventLogEntryType.Warning,
            EventKey = string.Empty
        };

    private void SeedRows(params TopIssueRow[] rows)
    {
        using var context = _fixture.NewContext();
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        context.DailyRecords.AddRange(rows.Select(row => new DailyRecordRow
        {
            RecordId = row.RecordId,
            HostId = row.HostId,
            HostName = $"HOST-{row.HostId}",
            RecordDate = row.RecordDate,
            RiskLevel = RiskLevels.Low,
            ContentJson = "{}",
            CreatedAt = row.RecordDate
        }));
        context.TopIssues.AddRange(rows);
        context.SaveChanges();
    }

    private static object[] NormalizeAggregates(IEnumerable<IssueAggregate> values) => values
        .OrderBy(x => x.Source, StringComparer.Ordinal)
        .ThenBy(x => x.EventId)
        .Select(x => new object[]
        {
            x.Source, x.EventId, x.Category, x.MaxSeverityRank, x.ElevatesDayRisk, x.HostCount,
            x.DayCount, x.ActiveDays, x.FirstSeen, x.LastSeen, x.TotalCount,
            string.Join("\u001f", x.IssueKeys.OrderBy(k => k, StringComparer.Ordinal))
        })
        .ToArray();

    private static object[] NormalizeMuted(IEnumerable<MutedIssueSummary> values) => values
        .OrderBy(x => x.Source, StringComparer.Ordinal)
        .ThenBy(x => x.EventId)
        .Select(x => new object[] { x.Source, x.EventId, x.Category, x.MaxSeverityRank })
        .ToArray();

    private static object[] NormalizeCategories(IEnumerable<CategoryAggregate> values) => values
        .OrderBy(x => x.Category, StringComparer.Ordinal)
        .Select(x => new object[]
        {
            x.Category, x.IssueTypeCount, x.RiskItemCount, x.CumulativeCount, x.AffectedHosts,
            x.TotalEvents, x.HighCount, x.MediumCount, x.LowCount, x.HighTypeCount,
            x.MediumTypeCount, x.LowTypeCount, x.ElevatesCount
        })
        .ToArray();

    private static long[] NormalizeHostIds(IEnumerable<long> values) => values.OrderBy(x => x).ToArray();

    private static object[] NormalizeHostIdsByIssue(IReadOnlyDictionary<(string SourceKey, int EventId), HashSet<long>> values) => values
        .OrderBy(x => x.Key.SourceKey, StringComparer.Ordinal)
        .ThenBy(x => x.Key.EventId)
        .Select(x => new object[] { x.Key.SourceKey, x.Key.EventId, NormalizeHostIds(x.Value) })
        .ToArray();

    private static object[] NormalizeDailyHostCounts(IEnumerable<IssueDailyHostCount> values) => values
        .OrderBy(x => x.Source, StringComparer.Ordinal)
        .ThenBy(x => x.EventId)
        .ThenBy(x => x.Date)
        .Select(x => new object[] { x.Source, x.EventId, x.Date, x.HostCount })
        .ToArray();
}
