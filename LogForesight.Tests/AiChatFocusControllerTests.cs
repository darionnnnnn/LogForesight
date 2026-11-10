using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>詢問 AI 對話端點：焦點判定、舊 IssueKey 相容、現場事件只在符合條件時查</summary>
public sealed class AiChatFocusControllerTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly FakeHostStore _hosts = new();
    private readonly CountingFetcher _fetcher = new();
    private readonly FakeWebAi _ai = new() { Response = "回答" };
    private readonly AiController _controller;
    private readonly WebHost _host;
    private readonly string _key4625;
    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    private sealed class CountingFetcher : ISentinelEventFetcher
    {
        public int Calls;
        public Task<LiveEventFetchResult?> FetchAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<LiveEventFetchResult?>(null);
        }
    }

    public AiChatFocusControllerTests()
    {
        var recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(recordStore, _hosts, visibility, new FakeSystemSettingsService());
        var detail = new RecordDetailQueryService(
            repository, new NullReportReader(), _hosts, new FakeUserStore(), new FakeHostGroupStore(), visibility,
            new FakeIssueHandlingStore(), new FakeIssueCaseStore(), new FakeNoiseMarkStore(), new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(), new FakeSystemSettingsStore(),
            new FixedIssueExclusionSource(IssueExclusion.None), new FakeIssueOwnerStore());

        _host = _hosts.Upsert(new WebHost { HostName = "SRV-F" });
        var sig4625 = TestData.Issue("Microsoft-Windows-Security-Auditing", 4625);
        recordStore.Append(new DailyAnalysisRecord
        {
            HostId = _host.HostId, Host = _host.HostName, Date = Day, RiskLevel = "高", Headline = "h", AiAnalyzed = true,
            TopIssues = new List<LogIssueSignature>
            {
                sig4625,
                TestData.Issue("Microsoft-Windows-Security-Auditing", 4735),
                TestData.Issue("Service Control Manager", 7031)
            }
        });
        _key4625 = IssueSignatureKey.For(sig4625);

        _controller = new AiController(
            new AiInsightService(_ai), null!, null!, detail,
            new RiskyEventLookupService(new FakeRiskyEventStore(), _fetcher), _hosts,
            new HandlingNoteAiService(_ai), new ManualAiThrottle(), new AutoAiThrottle(),
            FakeCurrentUser.ForUser(1), new RecordingAuditService(), new FakeSystemSettingsStore(), new AiAnalysisRunState());
    }

    public void Dispose() { _fixture.Dispose(); GC.SuppressFinalize(this); }

    private ChatRequest Req(string question, string? issueKey = null, List<string>? focusKeys = null) => new()
    {
        HostId = _host.HostId, Date = Day.ToString("yyyy-MM-dd"), IssueKey = issueKey ?? string.Empty, FocusKeys = focusKeys,
        Messages = new() { new ChatMessageDto { Role = "user", Content = question } }
    };

    [Fact]
    public async Task 只帶舊IssueKey_焦點一個_manual()
    {
        var res = await _controller.Chat(Req("這個嚴重嗎", issueKey: _key4625));

        var data = res.Data!;
        Assert.Equal("manual", data.FocusSource);
        var f = Assert.Single(data.Focus!);
        Assert.Equal(_key4625, f.IssueKey);
    }

    [Fact]
    public async Task focusKeys指向不在TopIssues的key_被忽略()
    {
        var res = await _controller.Chat(Req("整天怎麼樣", focusKeys: new() { "System|nope|1|1" }));

        var data = res.Data!;
        Assert.Equal("default", data.FocusSource);
        Assert.Equal(3, data.Focus!.Count);
        Assert.DoesNotContain(data.Focus, x => x.IssueKey == "System|nope|1|1");
    }

    [Fact]
    public async Task 首輪keyword單焦點_查現場事件_default焦點_不查()
    {
        await _controller.Chat(Req("7031 怎麼辦"));
        Assert.Equal(1, _fetcher.Calls);

        await _controller.Chat(Req("整天怎麼樣"));
        Assert.Equal(1, _fetcher.Calls);
    }
}
