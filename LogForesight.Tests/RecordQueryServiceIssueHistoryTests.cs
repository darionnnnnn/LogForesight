using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 「查看先前處理」（docs/archive/FEEDBACK-5-PLAN.md §4）：<see cref="RecordQueryService.GetDetail"/>
/// 的 <c>HasPriorHandling</c> 旗標與 <see cref="RecordQueryService.GetIssueHistory"/> 端點。
/// </summary>
public class RecordQueryServiceIssueHistoryTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly RecordDetailQueryService _service;

    private const string Source = "disk";
    private const int EventId = 153;
    private static readonly DateTime Today = DateTime.Today;

    public RecordQueryServiceIssueHistoryTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());

        _service = new RecordDetailQueryService(
            repository,
            new NullReportReader(),
            _hosts,
            _users,
            new FakeHostGroupStore(),
            visibility,
            _issueHandlingStore,
            _caseStore,
            new FakeNoiseMarkStore(),
            new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(),
            new FakeSystemSettingsStore());
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

    private void AddRecord(WebHost host, DateTime date, string risk = "低") =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            Headline = $"{host.HostName} {date:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System",
                    Source = Source,
                    EventId = EventId,
                    EntryType = System.Diagnostics.EventLogEntryType.Error,
                    Count = 3,
                    Severity = IssueSeverity.High,
                    FirstSeen = "00:00",
                    LastSeen = "01:00"
                }
            }
        });

    private static string IssueKey => IssueSignatureKey.For("System", Source, EventId, System.Diagnostics.EventLogEntryType.Error);

    // ── HasPriorHandling 旗標 ──────────────────────────────────────────────────

    [Fact]
    public void 從未結案過時HasPriorHandling為false()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);

        var detail = _service.GetDetail(host.HostId, Today);

        Assert.False(detail.TopIssues.Single().HasPriorHandling);
    }

    [Fact]
    public void 更早日期有結案類逐日標記時HasPriorHandling為true()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today.AddDays(-1));
        AddRecord(host, Today);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today.AddDays(-1), IssueKey = IssueKey,
            Status = IssueHandlingStatuses.Resolved, ActorAccount = "svc-lfadmin", UpdatedAt = DateTime.Now
        });

        var detail = _service.GetDetail(host.HostId, Today);

        Assert.True(detail.TopIssues.Single().HasPriorHandling);
    }

    [Fact]
    public void 更早日期只有進行中標記時HasPriorHandling為false()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today.AddDays(-1));
        AddRecord(host, Today);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today.AddDays(-1), IssueKey = IssueKey,
            Status = IssueHandlingStatuses.InProgress, ActorAccount = "svc-lfadmin", UpdatedAt = DateTime.Now
        });

        var detail = _service.GetDetail(host.HostId, Today);

        Assert.False(detail.TopIssues.Single().HasPriorHandling);
    }

    [Fact]
    public void 有已結案案件時HasPriorHandling為true_即使沒有對應逐日標記()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueKey, IssueLabel = $"{Source} {EventId}",
            Status = IssueHandlingStatuses.Resolved, FirstLinkedDate = Today.AddDays(-5), LastLinkedDate = Today.AddDays(-2),
            ClosedAt = Today.AddDays(-2), CreatedAt = Today.AddDays(-5), CreatedByAccount = "svc-lfadmin", UpdatedAt = DateTime.Now
        });

        var detail = _service.GetDetail(host.HostId, Today);

        Assert.True(detail.TopIssues.Single().HasPriorHandling);
    }

    [Fact]
    public void 未結案的進行中案件不算HasPriorHandling()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueKey, IssueLabel = $"{Source} {EventId}",
            Status = IssueHandlingStatuses.InProgress, FirstLinkedDate = Today.AddDays(-5), LastLinkedDate = Today,
            ClosedAt = null, CreatedAt = Today.AddDays(-5), CreatedByAccount = "svc-lfadmin", UpdatedAt = DateTime.Now
        });

        var detail = _service.GetDetail(host.HostId, Today);

        Assert.False(detail.TopIssues.Single().HasPriorHandling);
    }

    // ── GetIssueHistory ──────────────────────────────────────────────────────

    [Fact]
    public void GetIssueHistory只回傳結案類逐日標記_依日期新到舊()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today.AddDays(-5), IssueKey = IssueKey,
            Status = IssueHandlingStatuses.Resolved, ActorAccount = "user1", Note = "換硬碟解決", UpdatedAt = DateTime.Now
        });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today.AddDays(-2), IssueKey = IssueKey,
            Status = IssueHandlingStatuses.KnownNoise, ActorAccount = "user2", UpdatedAt = DateTime.Now
        });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today.AddDays(-1), IssueKey = IssueKey,
            Status = IssueHandlingStatuses.InProgress, ActorAccount = "user2", UpdatedAt = DateTime.Now
        });

        var history = _service.GetIssueHistory(host.HostId, Today, IssueKey);

        Assert.Equal(2, history.Entries.Count); // 進行中的那筆不列入
        Assert.Equal(Today.AddDays(-2).ToString("yyyy-MM-dd"), history.Entries[0].Date);
        Assert.Equal(Today.AddDays(-5).ToString("yyyy-MM-dd"), history.Entries[1].Date);
        Assert.Equal("換硬碟解決", history.Entries[1].Note);
        Assert.Equal("已知雜訊", history.Entries[0].StatusText);
    }

    [Fact]
    public void GetIssueHistory不含本日或未來日期的標記()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = Today, IssueKey = IssueKey,
            Status = IssueHandlingStatuses.Resolved, ActorAccount = "user1", UpdatedAt = DateTime.Now
        });

        var history = _service.GetIssueHistory(host.HostId, Today, IssueKey);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void GetIssueHistory回傳已結案案件摘要含處理人名稱()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        var handler = _users.Upsert(new WebUser { Account = "user1", DisplayName = "測試處理人" });
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueKey, IssueLabel = $"{Source} {EventId}",
            Status = IssueHandlingStatuses.Resolved, HandlerId = handler.UserId, Note = "已排除",
            FirstLinkedDate = Today.AddDays(-10), LastLinkedDate = Today.AddDays(-8),
            ClosedAt = Today.AddDays(-8), CreatedAt = Today.AddDays(-10), CreatedByAccount = "user1", UpdatedAt = DateTime.Now
        });

        var history = _service.GetIssueHistory(host.HostId, Today, IssueKey);

        var c = Assert.Single(history.Cases);
        Assert.Equal("測試處理人", c.HandlerName);
        Assert.Equal("已排除", c.Note);
        Assert.Equal(Today.AddDays(-8).ToString("yyyy-MM-dd"), c.ClosedAt);
    }

    [Fact]
    public void GetIssueHistory進行中案件不列入摘要()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, Today);
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueKey, IssueLabel = $"{Source} {EventId}",
            Status = IssueHandlingStatuses.InProgress, FirstLinkedDate = Today.AddDays(-5), LastLinkedDate = Today,
            ClosedAt = null, CreatedAt = Today.AddDays(-5), CreatedByAccount = "user1", UpdatedAt = DateTime.Now
        });

        var history = _service.GetIssueHistory(host.HostId, Today, IssueKey);

        Assert.Empty(history.Cases);
    }

    // ── A6：登入失敗明細與殘留標記的 DTO 轉換 ────────────────────────────────

    [Fact]
    public void 明細有值時DTO正確轉換()
    {
        var host = AddHost("HOST-LOGIN-1");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "高",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 43,
                    Severity = IssueSeverity.High,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = new List<LoginFailureDetail>
                    {
                        new()
                        {
                            Account = "alice",
                            IsComputerAccount = false,
                            Source = "10.0.0.1",
                            LogonType = 3,
                            ReasonCode = "bad_password",
                            Count = 40
                        },
                        new()
                        {
                            Account = "svc_backup",
                            IsComputerAccount = false,
                            Source = "10.0.0.2",
                            LogonType = 5,
                            ReasonCode = "password_expired",
                            Count = 3
                        }
                    }
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);

        Assert.NotNull(issue.LoginFailureDetails);
        Assert.Equal(2, issue.LoginFailureDetails.Count);

        var first = issue.LoginFailureDetails[0];
        Assert.Equal("alice", first.Account);
        Assert.False(first.IsComputerAccount);
        Assert.Equal("10.0.0.1", first.Source);
        Assert.Equal("網路登入", first.LogonTypeText);
        Assert.Equal("密碼錯誤", first.ReasonText);
        Assert.Equal(40, first.Count);

        var second = issue.LoginFailureDetails[1];
        Assert.Equal("svc_backup", second.Account);
        Assert.False(second.IsComputerAccount);
        Assert.Equal("10.0.0.2", second.Source);
        Assert.Equal("服務", second.LogonTypeText);
        Assert.Equal("密碼已過期", second.ReasonText);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public void 明細為null時DTO為null()
    {
        var host = AddHost("HOST-LOGIN-2");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "低",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System",
                    Source = Source,
                    EventId = EventId,
                    EntryType = System.Diagnostics.EventLogEntryType.Error,
                    Count = 1,
                    Severity = IssueSeverity.Low,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = null
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);

        Assert.Null(issue.LoginFailureDetails);
    }

    [Fact]
    public void 殘留旗標與依據正確傳遞()
    {
        var host = AddHost("HOST-LOGIN-3");
        const string expectedBasis = "疑似殘留憑證重試：alice 自 10.0.0.1 重複失敗 40 次（網路登入，密碼錯誤），近 7 天已重複出現";
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "中",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 40,
                    Severity = IssueSeverity.Medium,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    ResidualCredentialRetry = true,
                    ResidualCredentialBasis = expectedBasis
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);

        Assert.True(issue.ResidualCredentialRetry);
        Assert.Equal(expectedBasis, issue.ResidualCredentialBasis);
    }

    [Fact]
    public void LogonType為null時LogonTypeText為空字串()
    {
        var host = AddHost("HOST-LOGIN-4");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "高",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4771,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 10,
                    Severity = IssueSeverity.High,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = new List<LoginFailureDetail>
                    {
                        new()
                        {
                            Account = "bob",
                            IsComputerAccount = false,
                            Source = "10.0.0.5",
                            LogonType = null,
                            ReasonCode = "bad_password",
                            Count = 10
                        }
                    }
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);
        var item = Assert.Single(issue.LoginFailureDetails!);

        Assert.Equal(string.Empty, item.LogonTypeText);
        Assert.Equal("密碼錯誤", item.ReasonText);
    }

    [Fact]
    public void 電腦帳號旗標正確傳遞()
    {
        var host = AddHost("HOST-LOGIN-5");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "低",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 5,
                    Severity = IssueSeverity.Low,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = new List<LoginFailureDetail>
                    {
                        new()
                        {
                            Account = "WS01$",
                            IsComputerAccount = true,
                            Source = "192.168.1.50",
                            LogonType = 3,
                            ReasonCode = "bad_password",
                            Count = 5
                        }
                    }
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);
        var item = Assert.Single(issue.LoginFailureDetails!);

        Assert.True(item.IsComputerAccount);
        Assert.Equal("WS01$", item.Account);
    }

    [Fact]
    public void 未知ReasonCode轉為原因不明()
    {
        var host = AddHost("HOST-LOGIN-6");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = Today,
            RiskLevel = "低",
            Headline = $"{host.HostName} {Today:MM-dd}",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 2,
                    Severity = IssueSeverity.Low,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = new List<LoginFailureDetail>
                    {
                        new()
                        {
                            Account = "user_x",
                            IsComputerAccount = false,
                            Source = "10.0.0.9",
                            LogonType = 3,
                            ReasonCode = "some_custom_unknown_code",
                            Count = 2
                        }
                    }
                }
            }
        });

        var detail = _service.GetDetail(host.HostId, Today);
        var issue = Assert.Single(detail.TopIssues);
        var item = Assert.Single(issue.LoginFailureDetails!);

        Assert.Equal("原因不明", item.ReasonText);
    }
}
