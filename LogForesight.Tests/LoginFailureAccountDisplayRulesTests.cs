using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 使用者名稱顯示規則擴接到登入失敗明細（回饋三十四輪 B3）。
/// 這是權限異動以外唯一「查詢時才組出」的 AD 帳號顯示欄位——分析當下就寫進紀錄內文與報告的
/// 帳號文字是入庫值，不在此列（要套新規則得重新分析，設定頁的說明也是這樣寫）。
/// </summary>
public class LoginFailureAccountDisplayRulesTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly RecordDetailQueryService _service;

    public LoginFailureAccountDisplayRulesTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());

        _service = new RecordDetailQueryService(
            repository,
            new NullReportReader(),
            _hosts,
            new FakeUserStore(),
            new FakeHostGroupStore(),
            visibility,
            new FakeIssueHandlingStore(),
            new FakeIssueCaseStore(),
            new FakeNoiseMarkStore(),
            new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(),
            _settings);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private (WebHost Host, DateTime Date) AddRecordWithLoginFailure(string account)
    {
        var host = TestData.AddHost(_hosts, "SRV-DC01");
        var date = DateTime.Today.AddDays(-1);

        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = "中",
            Headline = "登入失敗",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    EntryType = System.Diagnostics.EventLogEntryType.FailureAudit,
                    Count = 5,
                    Severity = IssueSeverity.High,
                    FirstSeen = "00:00",
                    LastSeen = "01:00",
                    LoginFailureDetails = new List<LoginFailureDetail>
                    {
                        new() { Account = account, Source = "10.0.0.9", Count = 5 }
                    }
                }
            }
        });

        return (host, date);
    }

    private List<LoginFailureDetailDto> DetailsOf(WebHost host, DateTime date) =>
        _service.GetDetail(host.HostId, date).TopIssues
            .SelectMany(i => i.LoginFailureDetails ?? new List<LoginFailureDetailDto>())
            .ToList();

    [Fact]
    public void 無規則時登入失敗帳號原樣顯示()
    {
        var (host, date) = AddRecordWithLoginFailure("台積電-王小明");

        Assert.Equal("台積電-王小明", Assert.Single(DetailsOf(host, date)).Account);
    }

    [Fact]
    public void 設定規則後登入失敗帳號套用規則()
    {
        var (host, date) = AddRecordWithLoginFailure("台積電-王小明");
        _settings.Update(s => s.AccountDisplayRules = "^台積電- =>");

        Assert.Equal("王小明", Assert.Single(DetailsOf(host, date)).Account);
    }

    [Fact]
    public void 規則不影響資料庫裡的原始帳號()
    {
        var (host, date) = AddRecordWithLoginFailure("台積電-王小明");
        _settings.Update(s => s.AccountDisplayRules = "^台積電- =>");
        _ = DetailsOf(host, date);

        // 規則只作用在顯示：清掉規則就該看到原值（資料本身沒被改寫）
        _settings.Update(s => s.AccountDisplayRules = "");
        Assert.Equal("台積電-王小明", Assert.Single(DetailsOf(host, date)).Account);
    }
}
