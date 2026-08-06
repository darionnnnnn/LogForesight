using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題聚合（docs/SCALE-ISSUE-FIRST-PLAN.md P4／§4.2／根因 C）。
///
/// **這是需求「主視角改成問題」的資料層**：主機數與期間跨度是使用者明確要求的兩個數字，
/// 其餘是 §10.3「時間形狀」的訊號。改版前這些只能把整段期間的紀錄撈回記憶體再 GroupBy
/// （6000 台 × 30 天約 18 萬筆、近 GB 的 ContentJson），現在是一句 GROUP BY。
///
/// 語意的重點在兩處，測試逐條釘住：
///   - **可見範圍**：空集合＝零結果（不是「不限制」）——這個慣例反過來就是授權缺口。
///   - **主機數以存活主機 id 計**：合併過的主機不得被算成兩台（規劃 §8.1 缺陷 2）。
/// </summary>
public class IssueAggregateQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;

    public IssueAggregateQueryTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext);

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning) => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = IssueCategory.Other, Severity = severity, ElevatesDayRisk = elevates, Count = count
        };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = RiskLevels.Low,
            TopIssues = issues.ToList()
        });

    [Fact]
    public void 主機數與期間跨度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(1, "A", d0.AddDays(2), Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(10), null).Single();

        Assert.Equal(2, agg.HostCount);                      // 需求：包含此問題的主機數量
        Assert.Equal(d0, agg.FirstSeen);                     // 需求：期間跨度起點
        Assert.Equal(d0.AddDays(4), agg.LastSeen);           // 需求：期間跨度終點
        Assert.Equal(3, agg.ActiveDays);                     // 相異出現日數（密度的分子）
        Assert.Equal(3, agg.DayCount);                       // 主機日總數
    }

    [Fact]
    public void 總次數與最高嚴重度與重大旗標()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, count: 5, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("disk", 153, count: 7, severity: IssueSeverity.High, elevates: true));

        var agg = Query().Aggregate(d0, d0, null).Single();

        Assert.Equal(12, agg.TotalCount);
        Assert.Equal((int)IssueSeverity.High, agg.MaxSeverityRank);
        Assert.True(agg.ElevatesDayRisk);
    }

    /// <summary>同一台主機多天只算一台——「影響幾台」問的是機器數，不是紀錄數</summary>
    [Fact]
    public void 同一主機多天只算一台()
    {
        var d0 = new DateTime(2026, 8, 1);
        for (var i = 0; i < 5; i++) Add(1, "A", d0.AddDays(i), Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(10), null).Single();

        Assert.Equal(1, agg.HostCount);
        Assert.Equal(5, agg.ActiveDays);
        Assert.Equal(5, agg.DayCount);
    }

    [Fact]
    public void 期間外的紀錄不算()
    {
        var d0 = new DateTime(2026, 8, 10);
        Add(1, "A", d0.AddDays(-5), Issue("disk", 153));
        Add(1, "A", d0, Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(5), null).Single();

        Assert.Equal(d0, agg.FirstSeen);
        Assert.Equal(1, agg.ActiveDays);
    }

    /// <summary>
    /// 可見範圍的授權語意：**空集合＝零結果**，不是「不限制」。
    /// 這個慣例與 RecordQueryFilter.Hosts 一致；反過來就是授權缺口。
    /// </summary>
    [Fact]
    public void 可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        Assert.Empty(Query().Aggregate(d0, d0, Array.Empty<long>()));
        Assert.Single(Query().Aggregate(d0, d0, null));       // null＝不限制
    }

    [Fact]
    public void 可見範圍_只算授權內的主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0, new long[] { 1 }).Single();

        Assert.Equal(1, agg.HostCount);
    }

    /// <summary>
    /// 相異完整簽章：一個 (Source, EventId) 底下可能有多個 LogName／EntryType 組合，
    /// 而處理狀態是以完整簽章為鍵——沒有這份清單就 join 不到「這個問題有沒有結論」
    /// （規劃 §8.1 缺陷 1，§10.6 的前提）。
    /// </summary>
    [Fact]
    public void 相異完整簽章_供join處理狀態()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, logName: "System", entryType: EventLogEntryType.Warning));
        Add(2, "B", d0, Issue("disk", 153, logName: "Application", entryType: EventLogEntryType.Error));

        var agg = Query().Aggregate(d0, d0, null).Single();

        Assert.Equal(2, agg.IssueKeys.Count);
        Assert.Contains(IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning), agg.IssueKeys);
        Assert.Contains(IssueSignatureKey.For("Application", "disk", 153, EventLogEntryType.Error), agg.IssueKeys);
    }

    [Fact]
    public void 多個問題各自聚合()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153), Issue("DCOM", 10016));
        Add(2, "B", d0, Issue("DCOM", 10016));

        var result = Query().Aggregate(d0, d0, null).ToDictionary(a => (a.Source, a.EventId));

        Assert.Equal(1, result[("disk", 153)].HostCount);
        Assert.Equal(2, result[("DCOM", 10016)].HostCount);
    }

    [Fact]
    public void 沒有資料時回空清單()
    {
        Assert.Empty(Query().Aggregate(DateTime.Today, DateTime.Today, null));
    }
}
