using System.Diagnostics;
using LogForesight.Web.Controllers.Api;
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

    /// <summary>
    /// 帶條件的等值：riskLevels／visibleSeverities／非空 hostIds（含被合併的別名 id）三組
    /// 條件都要與逐期呼叫相同。沒有這三條的話，把合併查詢裡任一個條件整段拿掉，
    /// 只傳 null 的那幾條測試依然全綠（終檢抓到）。
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void KpiPair_帶條件時仍與逐期呼叫結果相同(bool withRiskLevels, bool withSeverities, bool withHostIds)
    {
        var survivor = _hosts.Upsert(new WebHost { HostName = "B" });
        var merged = _hosts.Upsert(new WebHost { HostName = "A" });
        _hosts.Merge(merged.HostId, survivor.HostId);
        var other = _hosts.Upsert(new WebHost { HostName = "C" });

        var to = new DateTime(2026, 8, 20);
        var from = to.AddDays(-6);
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-6);

        // 兩期都放進高／中／低三種風險日與高／低兩種嚴重度的問題，讓每個條件都真的篩掉東西
        Add(merged.HostId, "A", to, RiskLevels.High, Issue("disk", 7), Issue("net", 9, IssueSeverity.Low));
        Add(survivor.HostId, "B", to.AddDays(-1), RiskLevels.Medium, Issue("disk", 7));
        Add(other.HostId, "C", to.AddDays(-2), RiskLevels.Low, Issue("net", 9, IssueSeverity.Low));
        Add(survivor.HostId, "B", previousTo, RiskLevels.High, Issue("disk", 7));
        Add(other.HostId, "C", previousFrom, RiskLevels.Low, Issue("net", 9, IssueSeverity.Low));

        IReadOnlySet<string>? riskLevels = withRiskLevels ? new HashSet<string> { RiskLevels.High } : null;
        IReadOnlySet<IssueSeverity>? severities = withSeverities
            ? new HashSet<IssueSeverity> { IssueSeverity.High }
            : null;
        IReadOnlyCollection<long>? hostIds = withHostIds
            ? new[] { survivor.HostId, other.HostId }   // 只給存活 id，別名下的歷史也要被涵蓋
            : null;

        var query = Query();
        var expectedCurrent = query.AggregateReportKpi(from, to, hostIds, riskLevels, severities);
        var expectedPrevious = query.AggregateReportKpi(previousFrom, previousTo, hostIds, riskLevels, severities);

        var (current, previous) = query.AggregateReportKpiPair(
            from, to, previousFrom, previousTo, hostIds, riskLevels, severities);

        Assert.Equal(expectedCurrent, current);
        Assert.Equal(expectedPrevious, previous);
    }

    // ── F4：問題排行快取 ────────────────────────────────────────────────────

    private IssueRankingBuilder BuilderWith(FakeIssueAggregateQuery aggregates, IssueRankingCache? cache) =>
        new(aggregates, _hosts, cache: cache);

    /// <summary>
    /// 快取要在 Build 這一層真的生效——只測 Cache 物件本身等於在測一個字典加 TTL，
    /// 把 Build 裡的 Set／TryGet 整行刪掉那種測試照樣全綠（終檢抓到）。
    /// </summary>
    [Fact]
    public void 排行快取_同條件第二次Build不重打聚合()
    {
        var aggregates = new FakeIssueAggregateQuery();
        var now = new DateTime(2026, 8, 21, 10, 0, 0);
        var builder = BuilderWith(aggregates, new IssueRankingCache(() => now));
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);

        builder.Build(from, to, new long[] { 1, 2 }, 2);
        var afterFirst = aggregates.AggregateCallCount;
        Assert.True(afterFirst > 0);

        builder.Build(from, to, new long[] { 1, 2 }, 2);

        Assert.Equal(afterFirst, aggregates.AggregateCallCount);
    }

    /// <summary>不同可見範圍必須各自重算——共用結果等於讓人看到授權外的資料。</summary>
    [Fact]
    public void 排行快取_可見範圍不同時Build會重算()
    {
        var aggregates = new FakeIssueAggregateQuery();
        var now = new DateTime(2026, 8, 21, 10, 0, 0);
        var builder = BuilderWith(aggregates, new IssueRankingCache(() => now));
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);

        builder.Build(from, to, new long[] { 1, 2 }, 2);
        var afterFirst = aggregates.AggregateCallCount;

        builder.Build(from, to, new long[] { 1 }, 2);

        Assert.True(aggregates.AggregateCallCount > afterFirst, "可見範圍不同卻共用了快取結果");
    }

    /// <summary>TTL 過期後 Build 要重算，不是永遠回同一份。</summary>
    [Fact]
    public void 排行快取_TTL過期後Build會重算()
    {
        var aggregates = new FakeIssueAggregateQuery();
        var clock = new DateTime(2026, 8, 21, 10, 0, 0);
        var builder = BuilderWith(aggregates, new IssueRankingCache(() => clock));
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);

        builder.Build(from, to, null, 2);
        var afterFirst = aggregates.AggregateCallCount;

        clock = clock.AddSeconds(IssueRankingCache.TtlSeconds);
        builder.Build(from, to, null, 2);

        Assert.True(aggregates.AggregateCallCount > afterFirst, "TTL 過期後仍回傳快取結果");
    }

    /// <summary>沒有注入快取時行為不變（每次都重算）——快取是加值，不是必要條件。</summary>
    [Fact]
    public void 未注入快取時Build每次都重算()
    {
        var aggregates = new FakeIssueAggregateQuery();
        var builder = BuilderWith(aggregates, cache: null);
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);

        builder.Build(from, to, null, 2);
        var afterFirst = aggregates.AggregateCallCount;
        builder.Build(from, to, null, 2);

        Assert.True(aggregates.AggregateCallCount > afterFirst);
    }

    // ── B2：AI 用量 API（終檢指出規劃驗收要求的 API 測試原本沒做）────────────

    private SettingsController AiUsageController(AiUsageStore store, RecordingAuditService audit) =>
        new(new FakeSystemSettingsService(), store, audit);

    [Fact]
    public void AiUsageApi_讀取回傳今日與累計()
    {
        var now = new DateTime(2026, 8, 21, 9, 0, 0);
        var store = new AiUsageStore(_fx.Blob(AiUsageStore.BlobKey), () => now);
        store.Record(100, 50, 150, hasUsage: true);

        var dto = AiUsageDto.From(store, AiUsageDto.TableDays);

        Assert.Equal(150, dto.Total.TotalTokens);
        Assert.Equal(1, dto.Total.Calls);
        Assert.Equal("2026-08-21", dto.CountingSince);
        Assert.Single(dto.Days);
    }

    /// <summary>清空是不可復原的管理操作：必須歸零，而且必須留下稽核紀錄。</summary>
    [Fact]
    public void AiUsageApi_清空後歸零且留下稽核紀錄()
    {
        var store = new AiUsageStore(_fx.Blob(AiUsageStore.BlobKey));
        store.Record(100, 50, 150, hasUsage: true);

        var audit = new RecordingAuditService();
        var response = AiUsageController(store, audit).ResetAiUsage();

        Assert.Equal(0, response.Data!.Total.TotalTokens);
        Assert.Equal(0, response.Data.Total.Calls);
        Assert.Empty(response.Data.Days);

        var entry = Assert.Single(audit.Entries, e => e.TargetKind == "ai_usage");
        Assert.Contains("150", entry.Summary);   // 清空前的量要留在稽核裡，事後才查得到
    }


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
