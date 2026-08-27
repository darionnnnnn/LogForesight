using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 案件授與可見性的**端到端**驗收（docs/archive/FEEDBACK-10-PLAN.md §7）。
///
/// 刻意串真正的 <see cref="VisibilityService"/>＋<see cref="RecordRepository"/>＋
/// <see cref="RecordDetailQueryService"/>＋<see cref="IssueHandlingCommandService"/>，
/// 不用 AlwaysVisibleService 之類的替身——授權缺口幾乎都出在「兩層各自都對、串起來卻不通」：
/// 本輪體檢就抓到 `EnsureVisible` 已放行、`RecordRepository.GetOne` 卻仍擋下的整合斷點
/// （被指派的人打不開自己的問題），以及放行之後若不限制 issueKey，
/// 換一個 key 打 API 就能碰到同一天其他不屬於他的問題。兩者都由這裡的測試釘住。
///
/// 場景固定為：使用者屬於「OO部門」，只授權看得到 SRV-OO；SRV-XX 完全沒授權，
/// 但他被指派了 SRV-XX 上「disk 153」這個問題的案件。
/// </summary>
public class CaseGrantVisibilityTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeSystemSettingsStore _settings = new();

    private readonly WebUser _user;
    private readonly WebHost _grantedHost;   // 沒授權、但有案件
    private readonly WebHost _ownHost;       // 部門授權看得到
    private readonly LogIssueSignature _grantedIssue = TestData.Issue("disk", 153);
    private readonly LogIssueSignature _otherIssue = TestData.Issue("app", 999);

    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    public CaseGrantVisibilityTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");

        var ooGroup = _userGroups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });
        _user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\wang",
            DisplayName = "王小明",
            GroupIds = new List<long> { ooGroup.GroupId }
        });

        _ownHost = _hosts.Upsert(new WebHost { HostName = "SRV-OO", GroupIds = new List<long> { 10 } });
        _grantedHost = _hosts.Upsert(new WebHost { HostName = "SRV-XX", GroupIds = new List<long> { 20 } });
        _access.SetForUserGroup(ooGroup.GroupId, new[] { 10L });

        // SRV-XX 這一天有兩個問題：一個被指派給他，一個不是
        AddRecord(_grantedHost, Day, _grantedIssue, _otherIssue);

        _cases.Save(new IssueCase
        {
            CaseId = "case-1",
            HostName = _grantedHost.HostName,
            IssueKey = IssueSignatureKey.For(_grantedIssue),
            IssueLabel = "disk 153",
            HandlerId = _user.UserId,
            Status = IssueHandlingStatuses.InProgress,
            FirstLinkedDate = Day,
            LastLinkedDate = Day
        });
    }

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private void AddRecord(WebHost host, DateTime date, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = "高",
            Headline = "整日敘事：這一天發生了很多事",
            Summary = "整日總覽",
            TrendAssessment = "趨勢判讀",
            Action = "建議處置",
            AiAnalyzed = true,
            ReportFile = "report.txt",
            TopIssues = issues.ToList(),
            CorrelationAlerts = new List<string> { "關聯訊號" },
            TrendAlerts = new List<string> { "趨勢異常" }
        });

    private VisibilityService Visibility() =>
        new(FakeCurrentUser.ForUser(_user.UserId, Capability.Handle), _users, _userGroups, _access, _hosts, _cases, _settings);

    private RecordQueryServiceFacade Query(IVisibilityService visibility) =>
        new(
            repository: new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService()),
            reports: new StubReportReader(),
            hosts: _hosts,
            users: _users,
            hostGroups: new FakeHostGroupStore(),
            visibility: visibility,
            handlings: _handlings,
            issueHandlings: _issueHandlings,
            cases: _cases,
            noiseMarks: new FakeNoiseMarkStore(),
            rules: new FakeRuleStore(),
            currentUser: FakeCurrentUser.ForUser(_user.UserId, Capability.Handle),
            settings: _settings,
            aggregates: new EfIssueAggregateQuery(_fixture.NewContext, _hosts),
            statusResolver: new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings));

    private HandlingServiceFacade Handling(IVisibilityService visibility)
    {
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());
        return new HandlingServiceFacade(
            store: _handlings,
            issueStore: _issueHandlings,
            cases: _cases,
            caseCoordinator: new IssueCaseCoordinator(_cases, _issueHandlings, _handlings, _recordStore, _hosts, new FakeIssueOwnerStore()),
            noiseMarks: new FakeNoiseMarkStore(),
            repository: repository,
            hosts: _hosts,
            users: _users,
            visibility: visibility,
            currentUser: FakeCurrentUser.ForUser(_user.UserId, Capability.Handle),
            audit: new RecordingAuditService(),
            settings: _settings);
    }

    /// <summary>
    /// **本輪體檢抓到的整合斷點**：EnsureVisible 放行、GetOne 卻擋下，被指派的人根本打不開頁面。
    /// 這是整個 §7 是否成立的第一道驗收。
    /// </summary>
    [Fact]
    public void 被指派者_打得開未授權主機的風險日詳情()
    {
        var detail = Query(Visibility()).GetDetail(_grantedHost.HostId, Day);

        Assert.True(detail.CaseGrantOnly);
        Assert.Equal(_grantedHost.HostName, detail.HostName);
    }

    /// <summary>只看得到被交辦的那個問題，同一天的其他問題不出現</summary>
    [Fact]
    public void 詳情頁_只列出被交辦的問題()
    {
        var detail = Query(Visibility()).GetDetail(_grantedHost.HostId, Day);

        var issue = Assert.Single(detail.TopIssues);
        Assert.Equal(IssueSignatureKey.For(_grantedIssue), issue.IssueKey);
    }

    /// <summary>整日敘事（白話總覽／關聯訊號／趨勢）與報告全文都不屬於「一個問題」的交辦範圍</summary>
    [Fact]
    public void 詳情頁_整日敘事與報告全文被裁掉()
    {
        var query = Query(Visibility());
        var detail = query.GetDetail(_grantedHost.HostId, Day);

        Assert.Equal("", detail.Headline);
        Assert.Equal("", detail.Summary);
        Assert.Equal("", detail.TrendAssessment);
        Assert.Equal("", detail.Action);
        Assert.False(detail.AiAnalyzed);
        Assert.Empty(detail.CorrelationAlerts);
        Assert.Empty(detail.TrendAlerts);
        Assert.False(detail.HasReport);

        // 直接打報告端點也拿不到（回 null 而非拋例外——見 GetReport 的說明）
        Assert.Null(query.GetReport(_grantedHost.HostId, Day));
    }

    /// <summary>
    /// **越權缺口**：放行主機之後若不限制 issueKey，換一個 key 直接打 API 就能標記
    /// 同一天其他不屬於他的問題。
    /// </summary>
    [Fact]
    public void 被指派者_不能標記未被交辦的問題()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Handling(Visibility()).SetIssueStatus(_grantedHost.HostId, Day, new SetIssueStatusRequest
            {
                IssueKey = IssueSignatureKey.For(_otherIssue),
                Status = IssueHandlingStatuses.Resolved
            }));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    /// <summary>被交辦的那個問題當然要標得動——否則交辦等於白交辦</summary>
    [Fact]
    public void 被指派者_可以標記被交辦的問題()
    {
        var result = Handling(Visibility()).SetIssueStatus(_grantedHost.HostId, Day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(_grantedIssue),
            Status = IssueHandlingStatuses.Resolved,
            Note = "已更換硬碟"
        });

        Assert.Equal(IssueHandlingStatuses.Resolved, result.Status);
    }

    /// <summary>批次套用同樣受白名單約束（整批擋下，不做一半）</summary>
    [Fact]
    public void 被指派者_批次標記含未交辦問題時整批擋下()
    {
        var handling = Handling(Visibility());

        Assert.Throws<DomainException>(() =>
            handling.SetIssueStatusBatch(_grantedHost.HostId, Day, new BatchSetIssueStatusRequest
            {
                IssueKeys = new List<string>
                {
                    IssueSignatureKey.For(_grantedIssue),
                    IssueSignatureKey.For(_otherIssue)
                },
                Status = IssueHandlingStatuses.Resolved
            }));

        // 整批擋下＝連合法的那一個也沒有被寫入
        Assert.Empty(_issueHandlings.GetForDay(_grantedHost.HostName, Day));
    }

    /// <summary>問題歷史端點以 issueKey 為參數，同樣要擋</summary>
    [Fact]
    public void 被指派者_不能查未被交辦問題的歷史()
    {
        Assert.Throws<DomainException>(() =>
            Query(Visibility()).GetIssueHistory(_grantedHost.HostId, Day, IssueSignatureKey.For(_otherIssue)));
    }

    /// <summary>
    /// 日層級狀態是整天的結論（由當天全部問題推導），不開放給只被交辦一個問題的人。
    /// </summary>
    [Fact]
    public void 被指派者_不能改日層級處理狀態()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Handling(Visibility()).Update(_grantedHost.HostId, Day, new UpdateHandlingRequest
            {
                Status = HandlingStatuses.Resolved
            }));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    /// <summary>
    /// 報告全文是整台主機當日的完整敘事，案件授與者不得取得——三種報告同一套判斷，
    /// 不能只擋住風險報告那一種。
    /// </summary>
    [Fact]
    public void 僅案件授與_三種報告全文皆取不到()
    {
        var query = Query(Visibility());

        Assert.Null(query.GetReportView(_grantedHost.HostId, Day, ReportKinds.DailyRisk));
        Assert.Null(query.GetReportView(_grantedHost.HostId, Day, ReportKinds.WeeklyCheckup));
        Assert.Null(query.GetReport(_grantedHost.HostId, Day));
    }

    /// <summary>
    /// 本來就有完整權限的主機不受任何影響——裁剪只針對「僅案件授與」的主機，
    /// 不能因為這個功能讓正常授權的頁面也變殘缺。
    /// </summary>
    [Fact]
    public void 有完整權限的主機_不被裁剪()
    {
        AddRecord(_ownHost, Day, _grantedIssue, _otherIssue);

        var detail = Query(Visibility()).GetDetail(_ownHost.HostId, Day);

        Assert.False(detail.CaseGrantOnly);
        Assert.Equal(2, detail.TopIssues.Count);
        Assert.Equal("整日敘事：這一天發生了很多事", detail.Headline);
        Assert.True(detail.HasReport);
    }
}
