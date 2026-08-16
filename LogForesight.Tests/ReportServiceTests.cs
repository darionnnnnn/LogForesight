using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/HISTORY.md #6：報表新增的管理者指標（TotalHosts／Handling）。
/// 與 <see cref="RecordQueryServiceSearchTests"/> 同一套慣例——串接真正的
/// EfAnalysisRecordStore＋RecordRepository，而非重新實作一份簡化邏輯。
/// </summary>
public class ReportServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        var progress = new HandlingProgressCalculator(_issueHandlingStore, _handlingStore, _caseStore, _settingsStore);
        var handling = new HandlingHistoryQueryService(
            _handlingStore, _issueHandlingStore, _caseStore, _hosts, _users, visibility, _settingsStore, repository, progress);

        // 問題排行／風險類型分布自 P4 起走 SQL 端聚合（lf_top_issues），與紀錄查詢共用同一個
        // EF fixture——用假實作會讓「排行只含可見主機」這條授權測試測不到真正的下推路徑
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var issueRanking = new IssueRankingBuilder(aggregates, _hosts);
        _service = new ReportService(repository, _hosts, visibility, handling, issueRanking, _settingsStore, aggregates);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

    private void AddRecord(WebHost host, DateTime date, string risk, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = risk, TopIssues = issues.ToList()
        });

    [Fact]
    public void GetSummary_TotalHosts與可見主機數一致()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(2, result.TotalHosts);
    }

    /// <summary>Handling 與儀表板待辦同一套 GetTodo 規則：母體只算高＋中風險日，低風險日不進母體</summary>
    [Fact]
    public void GetSummary_Handling母體只計高中風險日()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高");
        AddRecord(host, DateTime.Today.AddDays(-1), "低");

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.OpenCount);
    }

    [Fact]
    public void GetSummary_顯示範圍unresolved_排除已結案的風險日()
    {
        // §5：scope=unresolved 先過濾再聚合——已結案的高風險日不進 KPI/趨勢母體
        var openHost = AddHost("HOST-OPEN");
        var resolvedHost = AddHost("HOST-RES");
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153, Severity = IssueSeverity.High, Count = 1,
            EntryType = System.Diagnostics.EventLogEntryType.Error
        };
        AddRecord(openHost, DateTime.Today, "高", issue);        // 未處理
        AddRecord(resolvedHost, DateTime.Today, "高", issue);    // 已結案
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = resolvedHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

        var all = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "all");
        Assert.Equal(2, all.Kpi.HighRiskDays);

        var unresolved = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unresolved");
        Assert.Equal("unresolved", unresolved.HandlingScope);
        Assert.Equal(1, unresolved.Kpi.HighRiskDays);   // 只剩未處理那台
    }

    [Fact]
    public void GetSummary_顯示範圍unassigned_只留無處理人的風險日()
    {
        var assignedHost = AddHost("HOST-ASSIGNED");
        var unassignedHost = AddHost("HOST-UNASSIGNED");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小陳" });
        AddRecord(assignedHost, DateTime.Today, "高");
        AddRecord(unassignedHost, DateTime.Today, "高");
        _handlingStore.Save(new RecordHandling
        {
            HostName = assignedHost.HostName, Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, HandlerId = handler.UserId, UpdatedAt = DateTime.Now
        });

        var unassigned = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unassigned");
        Assert.Equal(1, unassigned.Kpi.HighRiskDays);   // 只剩沒有處理人那台
    }

    /// <summary>docs/archive/FEEDBACK-3-PLAN.md #8：日風險等級顯示設定套用在 RecordRepository 單一咽喉，
    /// ReportService 不自己過濾——這裡固定住「吃 repository 結果」的契約，KPI/趨勢母體
    /// 隨顯示設定縮小，而不是報表自己額外判斷一次</summary>
    [Fact]
    public void GetSummary_中風險日被隱藏時KPI與趨勢母體縮小()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高");
        AddRecord(host, DateTime.Today.AddDays(-1), "中");
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { "高" };

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Kpi.HighRiskDays);
        // 中風險日已被顯示設定藏起來，趨勢圖對應日期的中風險計數應為 0
        var hiddenDayPoint = result.Trend.Single(p => p.Date == DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        Assert.Equal(0, hiddenDayPoint.MediumRisk);
    }

    /// <summary>
    /// 回饋十三輪新增項3：「風險類型」卡片沒有像風險日詳情頁那樣的手動展開入口，不論
    /// SeverityDisplayMode 為 DefaultHidden 或 SiteHidden，一律只計入 UnhandledSeverities
    /// （預設 High+Medium，不含 Low）——「其他」類別本來就是低嚴重度雜訊大宗，
    /// 修正前最容易讓人以為「明明沒勾 Low，其他類別怎麼還在顯示」。
    /// </summary>
    [Fact]
    public void GetSummary_風險類型卡預設排除Low嚴重度問題_只剩Low的類別整卡消失()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高",
            new LogIssueSignature
            {
                LogName = "System", Source = "sec", EventId = 4625,
                Severity = IssueSeverity.High, Category = IssueCategory.Security, Count = 1
            },
            new LogIssueSignature
            {
                LogName = "System", Source = "noisy", EventId = 9999,
                Severity = IssueSeverity.Low, Category = IssueCategory.Other, Count = 1
            });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        // 「其他」類別只有一個 Low 嚴重度問題，預設未勾選 Low 時整卡不應出現
        Assert.DoesNotContain(result.Categories, c => c.Category == "Other");
        var security = Assert.Single(result.Categories);
        Assert.Equal("Security", security.Category);
    }

    [Fact]
    public void GetSummary_問題全數結案後計入ResolvedCount()
    {
        var host = AddHost("HOST-A");
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153, Severity = IssueSeverity.High, Count = 1
        };
        AddRecord(host, DateTime.Today, "高", issue);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today,
            IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.ResolvedCount);
        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(0, result.Handling.OpenCount);
    }

    // ── 問題排行（docs/archive/FEEDBACK-11-PLAN.md §8-2）───────────────────────────────

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity, int count) => new()
    {
        LogName = "System", Source = source, EventId = eventId, Severity = severity, Count = count
    };

    /// <summary>
    /// 依 Source＋EventId 聚合（與依問題視角同一把鍵）：跨主機跨日的次數加總、
    /// 相異主機數、相異主機日數；排序＝最高嚴重度 → 主機數 → 總次數。
    /// </summary>
    [Fact]
    public void GetSummary_問題排行跨主機跨日聚合()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today, "高", Issue("disk", 153, IssueSeverity.High, 3));
        AddRecord(a, DateTime.Today.AddDays(-1), "高", Issue("disk", 153, IssueSeverity.High, 2));
        AddRecord(b, DateTime.Today, "中", Issue("disk", 153, IssueSeverity.Medium, 5),
                                           Issue("svc", 7031, IssueSeverity.Medium, 1));

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        var top = result.IssueRanking[0];
        Assert.Equal("disk", top.Source);
        Assert.Equal(153, top.EventId);
        Assert.Equal("High", top.MaxSeverity);   // 期間內出現過的最高嚴重度
        Assert.Equal(2, top.HostCount);
        Assert.Equal(3, top.DayCount);           // 主機日：A 兩天＋B 一天
        Assert.Equal(10, top.TotalCount);
        Assert.Equal(2, result.RankedIssueCount);
        Assert.Null(result.IssueOthers);         // 只有兩個問題，沒有 Top 10 之外的
    }

    /// <summary>可見範圍過濾天生繼承自 repository——排行不是另一條繞過授權的查詢路徑</summary>
    [Fact]
    public void GetSummary_問題排行只含可見主機()
    {
        var visible = AddHost("HOST-A");
        AddRecord(visible, DateTime.Today, "高", Issue("disk", 153, IssueSeverity.High, 1));

        // 未登錄於主機清單的紀錄（等同不可見）不該出現在排行中
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = 9999, Host = "HOST-GHOST", Date = DateTime.Today, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { Issue("ghost", 1, IssueSeverity.High, 99) }
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.DoesNotContain(result.IssueRanking, i => i.Source == "ghost");
    }

    [Fact]
    public void GetSummary_主機名稱大小寫不同時仍能正確對應到處理狀態()
    {
        var host = AddHost("HoSt-CaSe");
        AddRecord(host, DateTime.Today, "高");
        // 存檔用全小寫
        _handlingStore.Save(new RecordHandling
        {
            HostName = "host-case", Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);
        // 如果有抓到狀態，就不會是 Open（預設）
        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.InProgressCount);
        Assert.Equal(0, result.Handling.OpenCount);
    }

    [Fact]
    public void GetSummary_同一天有多個問題層級標記時推導結果一致()
    {
        var host = AddHost("HOST-MULTI");
        var issue1 = Issue("disk", 1, IssueSeverity.High, 1);
        var issue2 = Issue("disk", 2, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today, "高", issue1, issue2);

        // 兩個問題分別有狀態
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue1),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue2),
            Status = IssueHandlingStatuses.InProgress, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.InProgressCount);
    }

    [Fact]
    public void GetSummary_unassigned範圍_日層級無處理人但問題屬進行中案件時不算未指派()
    {
        var host = AddHost("HOST-CASE-ASSIGN");
        var issue = Issue("disk", 1, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today, "高", issue);

        // 日層級沒有處理人，但有一個針對該問題的案件，有指派處理人
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\x", DisplayName = "案件負責人" });
        _caseStore.Save(new IssueCase
        {
            HostName = host.HostName, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress, HandlerId = handler.UserId,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unassigned");
        // 有案件負責人，所以不算 unassigned，因此高風險日應為 0
        Assert.Equal(0, result.Kpi.HighRiskDays);
    }
}
