using LogForesight.Core.Analysis;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 儀表板彙總全面下推 SQL（SCALE-3000 S4）之後的行為契約。這一組存在的理由不是「數字算對」，
/// 而是**三件本來由 RecordRepository 保證、下推後要自己扛的事**：
///   1. 三層可見範圍（可見主機／日風險等級顯示範圍／可見嚴重度）不得被繞過；
///   2. 合併過的主機（墓碑列）的紀錄要折進存活主機，且同一頁的分項加總必須等於全站總數；
///   3. 儀表板與報表共用同一套聚合，兩頁的排行與待辦數字不可漂移。
/// 與 <see cref="ReportServiceTests"/> 同一套慣例：串接真正的 EF store 與聚合查詢，不用替身。
/// </summary>
public class DashboardServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly EfRecordHandlingStore _handlingStore;
    private readonly EfIssueHandlingStore _issueHandlingStore;
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly DashboardService _service;
    private readonly ReportService _reports;

    /// <summary>儀表板的期間終點錨在昨天（分析只產到昨天），測試資料一律以它為基準往前排</summary>
    private static readonly DateTime Anchor = DateTime.Today.AddDays(-1);

    public DashboardServiceTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _handlingStore = new EfRecordHandlingStore(_fixture.NewContext, new EfJsonLogStore(_fixture.NewContext, "handling"));
        _issueHandlingStore = new EfIssueHandlingStore(_fixture.NewContext);
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        var progress = new HandlingProgressCalculator(_issueHandlingStore, _handlingStore, _caseStore, _settingsStore);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var handling = new HandlingHistoryQueryService(
            _handlingStore, _issueHandlingStore, _caseStore, _hosts, _users, visibility, _settingsStore, repository, progress, aggregates);
        var issueRanking = new IssueRankingBuilder(aggregates, _hosts);
        var audit = new AuditLogStore(new EfJsonLogStore(_fixture.NewContext, "audit"));
        var currentUser = FakeCurrentUser.WithCapabilities();
        var permStore = new PermissionChangeStore(_fixture.NewContext);
        var permissionChanges = new PermissionChangeService(permStore, _hosts, visibility, currentUser, new RecordingAuditService(), _users, new NullReportReader());
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        var issueTodo = new IssueTodoQuery(aggregates, statusResolver);

        _service = new DashboardService(
            visibility, audit, currentUser, handling, permissionChanges,
            _hostGroups, issueRanking, _settingsStore, aggregates, issueTodo, _severityVisibility);
        // 兩頁一致的對照組：報表走同一組 store 與聚合
        _reports = new ReportService(repository, _hosts, visibility, handling, issueRanking, _settingsStore, aggregates, _severityVisibility);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name, long groupId, bool active = true)
    {
        var host = TestData.AddHost(_hosts, name);
        host.GroupIds = new List<long> { groupId };
        host.Active = active;
        return host;
    }

    private void AddRecord(WebHost host, int daysAgo, string risk, bool dataIncomplete = false, bool? securityLogAvailable = null) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Anchor.AddDays(-daysAgo), RiskLevel = risk,
            DataIncomplete = dataIncomplete, SecurityLogAvailable = securityLogAvailable
        });

    /// <summary>
    /// 一台被合併進 surviving 的墓碑主機：仍留在清單裡（Active=false、MergedInto 指向存活主機），
    /// 它的舊識別下有紀錄——這正是「群組加總與全站總數矛盾」的成因，每支合併相關的測試都要有它。
    /// </summary>
    private WebHost AddTombstone(string name, WebHost mergedInto)
    {
        var tomb = TestData.AddHost(_hosts, name);
        tomb.Active = false;
        tomb.MergedInto = mergedInto.HostId;
        tomb.GroupIds = new List<long>(mergedInto.GroupIds);
        return tomb;
    }

    [Fact]
    public void GetSummary_GroupRiskDays加總正確()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var h1 = AddHost("H1", 1);
        var h2 = AddHost("H2", 1);
        AddRecord(h1, 0, RiskLevels.High);
        AddRecord(h1, 1, RiskLevels.High);
        AddRecord(h2, 0, RiskLevels.High);
        AddRecord(h2, 1, RiskLevels.High);

        var summary = _service.GetSummary(7);

        var group = Assert.Single(summary.GroupRisk);
        Assert.Equal("G1", group.GroupName);
        Assert.Equal(2, group.HostCount);
        Assert.Equal(4, group.HighRiskDays);
    }

    /// <summary>
    /// B5 回歸：同一頁的「各群組高風險日加總」必須等於「全站高風險日」。改版前群組風險用
    /// 紀錄上的主機名稱建索引，墓碑列的紀錄對不上存活主機名稱，加總會比全站少——
    /// 兩個數字在同一頁上互相矛盾。全站數字刻意由另一支查詢（KPI 聚合）取得，
    /// 這條測試因此是在比對兩支不同查詢對同一份資料的答案。
    /// </summary>
    [Fact]
    public void GetSummary_含合併主機時_群組高風險日加總等於全站總數()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        _hostGroups.Upsert(new HostGroup { GroupId = 2, GroupName = "G2", Active = true });
        var a = AddHost("A", 1);
        var b = AddHost("B", 2);
        var oldA = AddTombstone("A-OLD", a);

        AddRecord(a, 0, RiskLevels.High);
        AddRecord(oldA, 1, RiskLevels.High);   // 舊識別下的高風險日
        AddRecord(oldA, 2, RiskLevels.Medium);
        AddRecord(b, 0, RiskLevels.High);
        AddRecord(b, 1, RiskLevels.Low);

        var summary = _service.GetSummary(7);

        Assert.Equal(3, summary.HighRiskDays);
        Assert.Equal(1, summary.MediumRiskDays);
        Assert.Equal(summary.HighRiskDays, summary.GroupRisk.Sum(g => g.HighRiskDays));
        Assert.Equal(summary.MediumRiskDays, summary.GroupRisk.Sum(g => g.MediumRiskDays));
        Assert.Equal(2, summary.GroupRisk.Single(g => g.GroupName == "G1").HighRiskDays);   // A 本身 1 天＋舊識別 1 天
    }

    [Fact]
    public void GetSummary_合併主機的紀錄計入存活主機的排行()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        var b = AddHost("B", 1);
        var oldA = AddTombstone("A-OLD", a);
        AddRecord(oldA, 0, RiskLevels.High);
        AddRecord(oldA, 1, RiskLevels.High);
        AddRecord(b, 0, RiskLevels.High);

        var summary = _service.GetSummary(7);

        var top = summary.HostRanking[0];
        Assert.Equal("A", top.HostName);          // 顯示成存活主機，不是 A-OLD
        Assert.Equal(2, top.HighRiskDays);
        Assert.DoesNotContain(summary.HostRanking, h => h.HostName == "A-OLD");
    }

    /// <summary>
    /// 兩頁一致：儀表板與報表的主機排行走同一支聚合與同一套排序（RecordStatsBuilder），
    /// 同一份資料、同一窗口，順序與數字必須相同——這是專案的既有不變式，下推 SQL 不得破壞它。
    /// </summary>
    [Fact]
    public void GetSummary_主機排行與報表同窗口一致()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        var b = AddHost("B", 1);
        var c = AddHost("C", 1);
        AddRecord(a, 0, RiskLevels.High); AddRecord(a, 1, RiskLevels.High);
        AddRecord(b, 0, RiskLevels.High); AddRecord(b, 1, RiskLevels.Medium);
        AddRecord(c, 0, RiskLevels.Medium);

        var dash = _service.GetSummary(7).HostRanking;
        var report = _reports.GetSummary(Anchor.AddDays(-6), Anchor).HostRanking;

        Assert.Equal(report.Count, dash.Count);
        for (var i = 0; i < report.Count; i++)
        {
            Assert.Equal(report[i].HostName, dash[i].HostName);
            Assert.Equal(report[i].HighRiskDays, dash[i].HighRiskDays);
            Assert.Equal(report[i].MediumRiskDays, dash[i].MediumRiskDays);
        }
    }

    /// <summary>
    /// 第二層可見範圍：日風險等級顯示範圍只勾「高」時，中風險日不得計入任何數字，
    /// 只有中風險日的主機不得出現在排行。這一層原本由 RecordRepository 套用，
    /// 直接下 SQL 之後要自己傳進去——漏傳的症狀是設定失效而不是報錯。
    /// </summary>
    [Fact]
    public void GetSummary_日風險等級只顯示高時_中風險日不計入且不進排行()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        var b = AddHost("B", 1);
        AddRecord(a, 0, RiskLevels.High);
        AddRecord(a, 1, RiskLevels.Medium);
        AddRecord(b, 0, RiskLevels.Medium);
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { RiskLevels.High };

        var summary = _service.GetSummary(7);

        Assert.Equal(1, summary.HighRiskDays);
        Assert.Equal(0, summary.MediumRiskDays);
        var only = Assert.Single(summary.HostRanking);
        Assert.Equal("A", only.HostName);
        Assert.Equal(0, only.MediumRiskDays);
        Assert.Equal(0, summary.GroupRisk.Single().MediumRiskDays);
        Assert.Equal(1, summary.Todo.TotalCount);
        Assert.Equal(1, summary.Todo.OpenCount);
    }

    /// <summary>可見範圍交集為空時整頁歸零，而不是（因為空集合被當「不限制」）變成全部可見。</summary>
    [Fact]
    public void GetSummary_日風險等級全部隱藏時_數字全為零()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        AddRecord(a, 0, RiskLevels.High);
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string>();

        var summary = _service.GetSummary(7);

        Assert.Equal(0, summary.HighRiskDays);
        Assert.Empty(summary.HostRanking);
        Assert.Equal(0, summary.Todo.TotalCount);
    }

    [Fact]
    public void GetSummary_待辦四計數與報表同窗口一致且含TotalCount()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        var b = AddHost("B", 1);
        var oldA = AddTombstone("A-OLD", a);
        AddRecord(a, 0, RiskLevels.High);
        AddRecord(oldA, 1, RiskLevels.High);      // 墓碑的待辦也要算
        AddRecord(b, 0, RiskLevels.Medium);
        AddRecord(b, 1, RiskLevels.Low);          // 低風險不進母體
        _handlingStore.Save(new RecordHandling
        {
            HostName = b.HostName, Date = Anchor, Status = HandlingStatuses.InProgress, UpdatedAt = DateTime.Now
        });

        var dash = _service.GetSummary(7).Todo;
        var report = _reports.GetSummary(Anchor.AddDays(-6), Anchor).Handling;

        Assert.Equal(3, dash.TotalCount);                    // 高 2（含墓碑）＋中 1
        Assert.Equal(report.TotalCount, dash.TotalCount);
        Assert.Equal(report.OpenCount, dash.OpenCount);
        Assert.Equal(report.InProgressCount, dash.InProgressCount);
        Assert.Equal(report.ResolvedCount, dash.ResolvedCount);
        Assert.Equal(1, dash.InProgressCount);
    }

    [Fact]
    public void GetSummary_涵蓋缺口日數_兩種觸發方式各計一次()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("A", 1);
        AddRecord(a, 0, RiskLevels.Low, dataIncomplete: true);
        AddRecord(a, 1, RiskLevels.Low, securityLogAvailable: false);
        AddRecord(a, 2, RiskLevels.Low, securityLogAvailable: true);   // 有安全記錄、資料完整：不算缺口
        AddRecord(a, 3, RiskLevels.Low);                                // 未知（null）：不算缺口

        Assert.Equal(2, _service.GetSummary(7).CoverageGapDays);
    }

    [Fact]
    public void GetSummary_SiteHidden隱藏嚴重度時_風險類型卡主機數與依問題視角去重主機數相等()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("HOST-A", 1);
        var b = AddHost("HOST-B", 1);
        var c = AddHost("HOST-C", 1);

        var highIssue = new LogIssueSignature
        {
            LogName = "Security", Source = "Auth", EventId = 4625,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Category = IssueCategory.Security, Severity = IssueSeverity.High
        };
        var lowIssue = new LogIssueSignature
        {
            LogName = "Security", Source = "Audit", EventId = 4624,
            EntryType = System.Diagnostics.EventLogEntryType.Information,
            Category = IssueCategory.Security, Severity = IssueSeverity.Low
        };

        _recordStore.Append(new DailyAnalysisRecord { HostId = a.HostId, Host = a.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { highIssue } });
        _recordStore.Append(new DailyAnalysisRecord { HostId = b.HostId, Host = b.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { highIssue } });
        _recordStore.Append(new DailyAnalysisRecord { HostId = c.HostId, Host = c.HostName, Date = Anchor, RiskLevel = RiskLevels.Low, TopIssues = new() { lowIssue } });

        // SiteHidden 模式：隱藏 Low，只顯示 High
        _severityVisibility.VisibleSeverities = new HashSet<string> { "High" };

        var summary = _service.GetSummary(7);
        var secCard = Assert.Single(summary.Categories, cat => cat.Category == "Security");

        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        var listService = new RecordListQueryService(
            repository, _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore,
            _severityVisibility, visibility, aggregates, statusResolver);

        var issueResult = listService.SearchByIssue(new RecordSearchRequest
        {
            From = Anchor.AddDays(-6),
            To = Anchor,
            Categories = new() { "Security" }
        });

        // 驗證：風險類型卡主機數（2 台：A、B）與依問題視角去重主機數相等
        Assert.Equal(2, secCard.AffectedHosts);
        Assert.Equal(secCard.AffectedHosts, issueResult.DistinctHostCount);
    }

    [Fact]
    public void 未處理問題KPI與依問題視角下鑽筆數一致_站台隱藏嚴重度()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("HOST-A", 1);
        var b = AddHost("HOST-B", 1);

        var highIssue = new LogIssueSignature
        {
            LogName = "Security", Source = "Auth", EventId = 4625,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Category = IssueCategory.Security, Severity = IssueSeverity.High
        };
        var lowIssue = new LogIssueSignature
        {
            LogName = "Security", Source = "Audit", EventId = 4624,
            EntryType = System.Diagnostics.EventLogEntryType.Information,
            Category = IssueCategory.Security, Severity = IssueSeverity.Low
        };

        _recordStore.Append(new DailyAnalysisRecord { HostId = a.HostId, Host = a.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { highIssue } });
        _recordStore.Append(new DailyAnalysisRecord { HostId = b.HostId, Host = b.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { lowIssue } });

        _settingsStore.Update(s => s.UnhandledSeverities = new() { "High", "Medium", "Low" });

        // SiteHidden 模式：隱藏 Low，只顯示 High
        _severityVisibility.VisibleSeverities = new HashSet<string> { "High" };

        var summary = _service.GetSummary(7);

        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        var listService = new RecordListQueryService(
            repository, _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore,
            _severityVisibility, visibility, aggregates, statusResolver);

        var issueResult = listService.SearchByIssue(new RecordSearchRequest
        {
            From = DateTime.Parse(summary.From),
            To = DateTime.Parse(summary.To),
            Statuses = new() { "open" }
        });

        // 驗證：隱藏 Low 後，KPI 與依問題視角下鑽都只剩 High 那個未處理問題（1 個）
        Assert.Equal(1, summary.IssueTodo.OpenIssueCount);
        Assert.Equal(summary.IssueTodo.OpenIssueCount, issueResult.Total);
    }

    [Fact]
    public void 未處理問題KPI與依問題視角下鑽筆數一致_來源名稱大小寫不同視為同一問題()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var a = AddHost("HOST-A", 1);
        var b = AddHost("HOST-B", 1);

        var issue1 = new LogIssueSignature
        {
            LogName = "System", Source = "Disk", EventId = 7,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Category = IssueCategory.Storage, Severity = IssueSeverity.High
        };
        var issue2 = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 7,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Category = IssueCategory.Storage, Severity = IssueSeverity.High
        };

        _recordStore.Append(new DailyAnalysisRecord { HostId = a.HostId, Host = a.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { issue1 } });
        _recordStore.Append(new DailyAnalysisRecord { HostId = b.HostId, Host = b.HostName, Date = Anchor, RiskLevel = RiskLevels.High, TopIssues = new() { issue2 } });

        var summary = _service.GetSummary(7);

        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        var listService = new RecordListQueryService(
            repository, _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore,
            _severityVisibility, visibility, aggregates, statusResolver);

        var issueResult = listService.SearchByIssue(new RecordSearchRequest
        {
            From = DateTime.Parse(summary.From),
            To = DateTime.Parse(summary.To),
            Statuses = new() { "open" }
        });

        // 驗證：Source 大小寫不同但 EventId 相同時，KPI 與依問題視角下鑽皆視為同一個未處理問題（1 個）
        Assert.Equal(1, summary.IssueTodo.OpenIssueCount);
        Assert.Equal(summary.IssueTodo.OpenIssueCount, issueResult.Total);
    }

    /// <summary>
    /// 群組風險概況的 UnhandledCount 與全站 KPI 卡「未處理問題」同口徑：只計未處理（Open），
    /// 不含「處理中」（InProgress）的問題。
    /// </summary>
    [Fact]
    public void GetSummary_群組風險概況的未處理數只計未處理問題()
    {
        _hostGroups.Upsert(new HostGroup { GroupId = 1, GroupName = "G1", Active = true });
        var host = AddHost("HOST-A", 1);

        var openIssue = new LogIssueSignature
        {
            LogName = "System", Source = "Disk", EventId = 7,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Category = IssueCategory.Storage, Severity = IssueSeverity.High
        };
        var inProgressIssue = new LogIssueSignature
        {
            LogName = "System", Source = "DCOM", EventId = 10016,
            EntryType = System.Diagnostics.EventLogEntryType.Warning,
            Category = IssueCategory.Service, Severity = IssueSeverity.High
        };

        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Anchor, RiskLevel = RiskLevels.High,
            TopIssues = new() { openIssue, inProgressIssue }
        });

        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = Anchor,
            IssueKey = IssueSignatureKey.For(inProgressIssue),
            Status = IssueHandlingStatuses.InProgress,
            DueDate = Anchor.AddDays(7)
        });

        var summary = _service.GetSummary(7);

        var group = Assert.Single(summary.GroupRisk);
        Assert.Equal(1, group.UnhandledCount);
        Assert.Equal(summary.IssueTodo.OpenIssueCount, group.UnhandledCount);
    }
}
