using System.Diagnostics;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋二十七輪作業 F3／F4／F5 的效能改動驗收。
///
/// 這一組釘的是「改快之後結果不能變」——效能改動最危險的失敗模式不是不夠快，
/// 是安靜地算錯（合併查詢漏掉一期的條件、快取串味、下推濾掉不該濾的列）。
/// </summary>
public class ReportPerformanceContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();

    public ReportPerformanceContractTests() => _records = new EfAnalysisRecordStore(_fx.NewContext, "test");

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext, _hosts);

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = EventLogEntryType.Warning,
        Category = IssueCategory.Other, Severity = severity, Count = 1
    };

    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList()
        });

    // ── F3：本期＋前期 KPI 合併查詢，結果必須與逐期呼叫逐欄位相同 ──────────────

    /// <summary>連續兩期（對比前一期）。</summary>
    [Fact]
    public void KpiPair_與逐期呼叫結果逐欄位相同_連續期間()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var to = new DateTime(2026, 8, 20);
        var from = to.AddDays(-6);
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-6);

        Add(a.HostId, "A", to, RiskLevels.High, Issue("disk", 7));
        Add(b.HostId, "B", to.AddDays(-2), RiskLevels.Medium, Issue("disk", 7), Issue("net", 9));
        Add(a.HostId, "A", previousTo, RiskLevels.High, Issue("disk", 7));
        Add(a.HostId, "A", previousFrom, RiskLevels.Low, Issue("net", 9));

        var query = Query();
        var expectedCurrent = query.AggregateReportKpi(from, to, null, null, null);
        var expectedPrevious = query.AggregateReportKpi(previousFrom, previousTo, null, null, null);

        var (current, previous) = query.AggregateReportKpiPair(from, to, previousFrom, previousTo, null, null, null);

        Assert.Equal(expectedCurrent, current);
        Assert.Equal(expectedPrevious, previous);
    }

    /// <summary>對比去年同期：兩期相隔一年、中間整段無關資料不得混進任一期。</summary>
    [Fact]
    public void KpiPair_與逐期呼叫結果逐欄位相同_去年同期且中間資料不混入()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var to = new DateTime(2026, 8, 20);
        var from = to.AddDays(-6);
        var previousTo = to.AddYears(-1);
        var previousFrom = from.AddYears(-1);

        Add(a.HostId, "A", to, RiskLevels.High, Issue("disk", 7));
        Add(a.HostId, "A", previousTo, RiskLevels.High, Issue("disk", 7));
        Add(a.HostId, "A", new DateTime(2026, 2, 1), RiskLevels.High, Issue("disk", 7));   // 兩期之間

        var query = Query();
        var expectedCurrent = query.AggregateReportKpi(from, to, null, null, null);
        var expectedPrevious = query.AggregateReportKpi(previousFrom, previousTo, null, null, null);

        var (current, previous) = query.AggregateReportKpiPair(from, to, previousFrom, previousTo, null, null, null);

        Assert.Equal(expectedCurrent, current);
        Assert.Equal(expectedPrevious, previous);
        Assert.Equal(1, current.HighRiskDays);
        Assert.Equal(1, previous.HighRiskDays);
    }

    /// <summary>合併過的主機在兩期都不得被算成兩台（合併語意不能因為合併查詢而走樣）。</summary>
    [Fact]
    public void KpiPair_主機合併後受影響主機數不重複計算()
    {
        var survivor = _hosts.Upsert(new WebHost { HostName = "B" });
        var merged = _hosts.Upsert(new WebHost { HostName = "A" });
        _hosts.Merge(merged.HostId, survivor.HostId);

        var to = new DateTime(2026, 8, 20);
        var from = to.AddDays(-6);
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-6);

        Add(merged.HostId, "A", to, RiskLevels.High, Issue("disk", 7));
        Add(survivor.HostId, "B", to.AddDays(-1), RiskLevels.High, Issue("disk", 7));

        var (current, _) = Query().AggregateReportKpiPair(from, to, previousFrom, previousTo, null, null, null);

        Assert.Equal(1, current.AffectedHosts);   // 不是 2
        Assert.Equal(2, current.HighRiskDays);    // 主機日數不受合併影響
    }

    /// <summary>可見範圍為空＝零結果（授權語意不得因為合併查詢而反轉成「不限制」）。</summary>
    [Fact]
    public void KpiPair_可見範圍為空時兩期都是零()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var to = new DateTime(2026, 8, 20);
        Add(a.HostId, "A", to, RiskLevels.High, Issue("disk", 7));

        var (current, previous) = Query().AggregateReportKpiPair(
            to.AddDays(-6), to, to.AddDays(-13), to.AddDays(-7), Array.Empty<long>(), null, null);

        Assert.Equal(new ReportKpiAggregate(0, 0, 0, 0, 0), current);
        Assert.Equal(new ReportKpiAggregate(0, 0, 0, 0, 0), previous);
    }

    // ── F4：問題排行快取 ────────────────────────────────────────────────────

    private static List<IssueRankingDto> Ranking(string source) => new()
    {
        new IssueRankingDto { Source = source, EventId = 1, HostCount = 1 }
    };

    [Fact]
    public void 排行快取_同鍵在TTL內回傳快取且回傳副本()
    {
        var now = new DateTime(2026, 8, 21, 10, 0, 0);
        var cache = new IssueRankingCache(() => now);
        var key = IssueRankingCache.KeyOf(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), new long[] { 1, 2 }, 2);

        cache.Set(key, Ranking("disk"));

        var first = cache.TryGet(key);
        Assert.NotNull(first);
        first!.Clear();   // 呼叫端亂改不得污染快取

        var second = cache.TryGet(key);
        Assert.NotNull(second);
        Assert.Single(second!);
    }

    [Fact]
    public void 排行快取_超過TTL即失效()
    {
        var now = new DateTime(2026, 8, 21, 10, 0, 0);
        var clock = now;
        var cache = new IssueRankingCache(() => clock);
        var key = IssueRankingCache.KeyOf(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), null, 5);

        cache.Set(key, Ranking("disk"));
        clock = now.AddSeconds(IssueRankingCache.TtlSeconds - 1);
        Assert.NotNull(cache.TryGet(key));

        clock = now.AddSeconds(IssueRankingCache.TtlSeconds);
        Assert.Null(cache.TryGet(key));
    }

    /// <summary>不同可見範圍是不同的鍵——授權範圍串味等於越權看到別人的資料。</summary>
    [Fact]
    public void 排行快取_可見範圍不同即不同鍵()
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);

        var keyA = IssueRankingCache.KeyOf(from, to, new long[] { 1, 2 }, 2);
        var keyB = IssueRankingCache.KeyOf(from, to, new long[] { 1 }, 2);
        var keyUnlimited = IssueRankingCache.KeyOf(from, to, null, 2);

        Assert.NotEqual(keyA, keyB);
        Assert.NotEqual(keyA, keyUnlimited);
        Assert.NotEqual(keyB, keyUnlimited);

        // 同一集合不同順序視為同一鍵
        Assert.Equal(keyA, IssueRankingCache.KeyOf(from, to, new long[] { 2, 1 }, 2));
    }

    [Fact]
    public void 排行快取_區間或主機總數不同即不同鍵()
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);
        var baseKey = IssueRankingCache.KeyOf(from, to, null, 2);

        Assert.NotEqual(baseKey, IssueRankingCache.KeyOf(from, to.AddDays(-1), null, 2));
        Assert.NotEqual(baseKey, IssueRankingCache.KeyOf(from.AddDays(-1), to, null, 2));
        Assert.NotEqual(baseKey, IssueRankingCache.KeyOf(from, to, null, 3));
    }
}
