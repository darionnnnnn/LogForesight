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

        _service = new ReportService(repository, _hosts, visibility, handling);
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
}
