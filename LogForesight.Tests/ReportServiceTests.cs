using LogForesight.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/WEB-FEEDBACK-PLAN.md #6：報表新增的管理者指標（TotalHosts／Handling）。
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
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());

        var handling = new HandlingService(
            _handlingStore, _issueHandlingStore, new FakeNoiseMarkStore(), repository, _hosts, _users,
            visibility, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), _settingsStore);

        _service = new ReportService(repository, _hosts, visibility, handling);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => _hosts.Upsert(new WebHost { HostName = name });

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
