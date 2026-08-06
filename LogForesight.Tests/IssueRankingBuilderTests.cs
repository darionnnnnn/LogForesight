using System.Diagnostics;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題排行的組裝層（docs/SCALE-FIX-PLAN-2026-08-06.md G3）。
///
/// **為什麼補這組測試**：P4 為聚合查詢寫了 9 條測試，卻沒有測「把聚合變成 DTO」那一層——
/// 而分類欄空白（D2）、變化幅度、IsNew、影響率全在這一層。D2 那種
/// 「欄位恆為空字串」的缺陷不會讓任何既有測試變紅，只會讓畫面少一欄，
/// 上線後才有人問「分類怎麼不見了」。
/// </summary>
public class IssueRankingBuilderTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;

    public IssueRankingBuilderTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private IssueRankingBuilder Builder() => new(new EfIssueAggregateQuery(_fx.NewContext));

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        IssueCategory category = IssueCategory.Storage,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning) => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = category, Severity = severity, ElevatesDayRisk = elevates, Count = count
        };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = RiskLevels.Low, TopIssues = issues.ToList()
        });

    /// <summary>
    /// D2 的回歸測試：分類欄過去被寫死成空字串（註解說「由呼叫端補」但沒有呼叫端補），
    /// 儀表板重點問題卡與報表問題排行的「分類」欄因此全部空白。
    /// </summary>
    [Fact]
    public void 分類欄要帶得出來()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, category: IssueCategory.Storage));

        var row = Builder().Build(d0, d0, null, totalHosts: 1).Single();

        Assert.Equal(IssueCategory.Storage.ToString(), row.Category);
    }

    [Fact]
    public void 主機數與期間跨度與密度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));

        var row = Builder().Build(d0, d0.AddDays(9), null, totalHosts: 4).Single();

        Assert.Equal(2, row.HostCount);
        Assert.Equal("2026-08-01", row.FirstSeen);
        Assert.Equal("2026-08-05", row.LastSeen);
        Assert.Equal(2, row.ActiveDays);
        Assert.Equal(10, row.PeriodDays);          // 含頭尾
        Assert.Equal(0.5, row.HostRatio);          // 2 ÷ 4
    }

    /// <summary>影響率的分母是可見主機總數；沒有主機時不得除零</summary>
    [Fact]
    public void 影響率_主機總數為零時回零而不是除零()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        var row = Builder().Build(d0, d0, null, totalHosts: 0).Single();

        Assert.Equal(0, row.HostRatio);
    }

    /// <summary>
    /// 「新出現」＝前一個**等長**期間完全沒有出現過。這是「今天有什麼不一樣」的唯一訊號，
    /// 也是把 DCOM 那種天天都有的雜訊與真正的新問題分開的關鍵。
    /// </summary>
    [Fact]
    public void 本期新出現_前期沒有紀錄()
    {
        var from = new DateTime(2026, 8, 5);
        var to = new DateTime(2026, 8, 11);          // 7 天
        Add(1, "A", from.AddDays(1), Issue("disk", 153));

        var row = Builder().Build(from, to, null, totalHosts: 1).Single();

        Assert.True(row.IsNew);
        Assert.Equal(0, row.PreviousHostCount);
    }

    [Fact]
    public void 非新出現_前期有紀錄時帶出前期主機數()
    {
        var from = new DateTime(2026, 8, 5);
        var to = new DateTime(2026, 8, 11);          // 前期＝07-29 ~ 08-04
        Add(1, "A", new DateTime(2026, 7, 30), Issue("disk", 153));
        Add(2, "B", new DateTime(2026, 7, 31), Issue("disk", 153));
        Add(1, "A", from.AddDays(1), Issue("disk", 153));

        var row = Builder().Build(from, to, null, totalHosts: 2).Single();

        Assert.False(row.IsNew);
        Assert.Equal(2, row.PreviousHostCount);      // 前期 2 台 → 本期 1 台＝收斂中
        Assert.Equal(1, row.HostCount);
    }

    /// <summary>距今天數回答「還要不要處理」——把「90 天前爆發過」與「今天正在發生」分開</summary>
    [Fact]
    public void 距今天數()
    {
        var today = DateTime.Today;
        Add(1, "A", today.AddDays(-3), Issue("disk", 153));

        var row = Builder().Build(today.AddDays(-10), today, null, totalHosts: 1).Single();

        Assert.Equal(3, row.DaysSinceLastSeen);
    }

    [Fact]
    public void 重大旗標與最高嚴重度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("disk", 153, severity: IssueSeverity.High, elevates: true));

        var row = Builder().Build(d0, d0, null, totalHosts: 2).Single();

        Assert.Equal(IssueSeverity.High.ToString(), row.MaxSeverity);
        Assert.True(row.ElevatesDayRisk);
    }

    /// <summary>可見範圍的授權語意與查詢層一致：空集合＝零結果，不是「不限制」</summary>
    [Fact]
    public void 可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        Assert.Empty(Builder().Build(d0, d0, Array.Empty<long>(), totalHosts: 0));
        Assert.Single(Builder().Build(d0, d0, null, totalHosts: 1));
    }

    /// <summary>
    /// 處理狀態以**完整簽章**為鍵，而排行以 (Source, EventId) 為單位——
    /// 一個群組底下的多個完整簽章要**合併**未處理／已處理主機數（§10.6 的前提）。
    /// </summary>
    [Fact]
    public void 處理概況_跨多個完整簽章合併()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, logName: "System", entryType: EventLogEntryType.Warning));
        Add(2, "B", d0, Issue("disk", 153, logName: "Application", entryType: EventLogEntryType.Error));

        var rollup = new Dictionary<string, IssueHandlingRollup>
        {
            [IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning)] = new(OpenHostCount: 1, ResolvedHostCount: 0),
            [IssueSignatureKey.For("Application", "disk", 153, EventLogEntryType.Error)] = new(OpenHostCount: 0, ResolvedHostCount: 1)
        };

        var row = Builder().Build(d0, d0, null, totalHosts: 2, rollup).Single();

        Assert.Equal(1, row.OpenHostCount);
        Assert.Equal(1, row.ResolvedHostCount);
    }

    /// <summary>預設排序：最高嚴重度 → 主機數 → 總次數（與依問題視角的既有預設一致）</summary>
    [Fact]
    public void 預設排序_嚴重度優先()
    {
        var d0 = new DateTime(2026, 8, 1);
        // 低嚴重度但影響很廣（DCOM 的角色）
        for (var i = 1; i <= 5; i++) Add(i, $"H{i}", d0, Issue("DCOM", 10016, severity: IssueSeverity.Low));
        // 高嚴重度但只有一台（disk 153 的角色）
        Add(9, "H9", d0, Issue("disk", 153, severity: IssueSeverity.High));

        var rows = Builder().Build(d0, d0, null, totalHosts: 6);

        Assert.Equal("disk", rows[0].Source);
        Assert.Equal("DCOM", rows[1].Source);
    }
}
