using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 總覽儀表板「風險類型」卡片改用問題類型數（IssueTypeCount）之測試。
/// 驗證跨主機、跨日期去重邏輯，以及既有 RiskItemCount 語意不受影響。
/// </summary>
public class DashboardCategoryIssueTypeCountTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();

    public DashboardCategoryIssueTypeCountTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext, _hosts);

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        IssueCategory category = IssueCategory.Storage,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning) => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = entryType,
            Category = category,
            Severity = severity,
            ElevatesDayRisk = elevates,
            Count = count
        };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId,
            Host = host,
            Date = date,
            RiskLevel = RiskLevels.Low,
            TopIssues = issues.ToList()
        });

    /// <summary>
    /// 驗收標準 2.1：同一個 (source, eventId) 出現在多台主機、多個日期時，IssueTypeCount 只算 1。
    /// 同時驗證 SourceName 大小寫不敏感（"disk"、"Disk"、"DISK" 視為同一個問題類型）。
    /// </summary>
    [Fact]
    public void 同一問題出現在多台主機多個日期_IssueTypeCount只算1()
    {
        var d0 = new DateTime(2026, 8, 1);
        // 主機 1 在第 0 天與第 1 天各出現一次 disk 153
        Add(1, "Host1", d0, Issue("disk", 153, count: 2));
        Add(1, "Host1", d0.AddDays(1), Issue("disk", 153, count: 3));

        // 主機 2 在第 1 天出現 Disk 153（大小寫不同）
        Add(2, "Host2", d0.AddDays(1), Issue("Disk", 153, count: 1));

        // 主機 3 在第 2 天出現 DISK 153
        Add(3, "Host3", d0.AddDays(2), Issue("DISK", 153, count: 4));

        var result = Query().AggregateByCategory(d0, d0.AddDays(5), null, null);

        var storage = Assert.Single(result);
        Assert.Equal(IssueCategory.Storage.ToString(), storage.Category);
        // 跨主機、跨日期去重後，問題類型只有 1 種 (disk, 153)
        Assert.Equal(1, storage.IssueTypeCount);
        // 既有 RiskItemCount: 相異 (存活主機, Source, EventId) 組合數 = 3 台主機
        Assert.Equal(3, storage.RiskItemCount);
        // 累計出現次數（主機×日）= 4 筆紀錄
        Assert.Equal(4, storage.CumulativeCount);
        // 相異存活主機數 = 3
        Assert.Equal(3, storage.AffectedHosts);
    }

    /// <summary>
    /// 驗收標準 2.2：同一台主機上的兩個不同問題，IssueTypeCount 算 2（且 RiskItemCount 的既有語意不變）。
    /// </summary>
    [Fact]
    public void 同一主機上兩個不同問題_IssueTypeCount算2且RiskItemCount語意不變()
    {
        var d0 = new DateTime(2026, 8, 1);
        // 主機 1 在第 0 天同時出現問題 153 與 154
        Add(1, "Host1", d0,
            Issue("disk", 153, count: 2, severity: IssueSeverity.High),
            Issue("disk", 154, count: 1, severity: IssueSeverity.Medium));

        // 主機 1 在第 1 天再次出現問題 153
        Add(1, "Host1", d0.AddDays(1),
            Issue("disk", 153, count: 3, severity: IssueSeverity.High));

        var result = Query().AggregateByCategory(d0, d0.AddDays(5), null, null);

        var storage = Assert.Single(result);
        Assert.Equal(IssueCategory.Storage.ToString(), storage.Category);
        // 同一台主機上的 2 個不同問題類型 (disk 153, disk 154)，去重後 IssueTypeCount = 2
        Assert.Equal(2, storage.IssueTypeCount);
        // 既有 RiskItemCount: (Host1, disk, 153) 與 (Host1, disk, 154) = 2
        Assert.Equal(2, storage.RiskItemCount);
        // 累計出現次數 = 3
        Assert.Equal(3, storage.CumulativeCount);
        // 相異存活主機數 = 1
        Assert.Equal(1, storage.AffectedHosts);
        // 嚴重度分桶依風險資訊最高嚴重度
        Assert.Equal(1, storage.HighCount);
        Assert.Equal(1, storage.MediumCount);
    }

    /// <summary>
    /// 嚴重度可見性過濾時，IssueTypeCount 應只計算通過過濾的問題類型。
    /// </summary>
    [Fact]
    public void 嚴重度過濾時_IssueTypeCount只計入可見問題類型()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "Host1", d0, Issue("disk", 153, severity: IssueSeverity.High));
        Add(2, "Host2", d0, Issue("disk", 154, severity: IssueSeverity.Low));

        var visibleSeverities = new HashSet<IssueSeverity> { IssueSeverity.High };
        var result = Query().AggregateByCategory(d0, d0, null, visibleSeverities);

        var storage = Assert.Single(result);
        Assert.Equal(1, storage.IssueTypeCount);
        Assert.Equal(1, storage.RiskItemCount);
        Assert.Equal(1, storage.HighCount);
        Assert.Equal(0, storage.LowCount);
    }

    /// <summary>
    /// 驗證 RecordStatsBuilder.BuildCategoryCards 正確將 CategoryAggregate.IssueTypeCount 映射至 DashboardCategoryDto。
    /// </summary>
    [Fact]
    public void RecordStatsBuilder_BuildCategoryCards_正確映射IssueTypeCount()
    {
        var agg = new CategoryAggregate
        {
            Category = "Storage",
            IssueTypeCount = 42,
            RiskItemCount = 100,
            CumulativeCount = 300,
            TotalEvents = 1500,
            HighCount = 10,
            MediumCount = 30,
            LowCount = 60,
            ElevatesCount = 5,
            AffectedHosts = 25
        };

        var cards = RecordStatsBuilder.BuildCategoryCards(new[] { agg });

        var card = Assert.Single(cards);
        Assert.Equal("Storage", card.Category);
        Assert.Equal(42, card.IssueTypeCount);
        Assert.Equal(100, card.RiskItemCount);
        Assert.Equal(300, card.CumulativeCount);
        Assert.Equal(1500, card.TotalEvents);
        Assert.Equal(10, card.HighCount);
        Assert.Equal(30, card.MediumCount);
        Assert.Equal(60, card.LowCount);
        Assert.Equal(5, card.ElevatesCount);
        Assert.Equal(25, card.AffectedHosts);
    }
}
