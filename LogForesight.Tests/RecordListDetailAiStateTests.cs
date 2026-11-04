using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪 B-b-6：清單與詳情 DTO 帶出 <c>aiDegradedAttempts</c>（前端 <c>aiStateOf</c> 的輸入）。
/// 用真的 SQLite 儲存與真的查詢服務：清單有兩條讀取路徑（分頁走整份反序列化、
/// 帶狀態篩選走輕量投影），輕量投影漏帶欄位時前端會把降級待補誤顯示成一般完成。
/// </summary>
public sealed class RecordListDetailAiStateTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly RecordListQueryService _list;
    private readonly RecordDetailQueryService _detail;

    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    public RecordListDetailAiStateTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, new FakeSystemSettingsService());
        var settings = new FakeSystemSettingsStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();

        _list = new RecordListQueryService(
            repository, _hosts, new FakeUserStore(), new FakeHandlingStore(), issueHandlings, cases, settings,
            new FakeSystemSettingsService(), visibility, new FakeIssueAggregateQuery(),
            new OccurrenceStatusResolver(_hosts, issueHandlings, cases, settings),
            new UserDisplayNameService(settings),
            new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None));

        _detail = new RecordDetailQueryService(
            repository, new NullReportReader(), _hosts, new FakeUserStore(), new FakeHostGroupStore(), visibility,
            issueHandlings, cases, new FakeNoiseMarkStore(), new FakeRuleStore(), FakeCurrentUser.WithCapabilities(),
            new FakeSystemSettingsStore(), new FixedIssueExclusionSource(IssueExclusion.None), new FakeIssueOwnerStore());
    }

    public void Dispose() { _fixture.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>寫入一筆並直接把抽出欄設成指定的降級狀態（<c>Append</c> 一律把次數歸零）</summary>
    private WebHost Seed(string hostName, int attempts, bool pending)
    {
        var host = _hosts.Upsert(new WebHost { HostName = hostName });
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Day, RiskLevel = "中",
            Headline = "磁碟錯誤偏多（AI 摘要待補，第 2 次）", AiAnalyzed = true,
            TopIssues = new List<LogIssueSignature> { TestData.Issue("disk", 153) }
        });
        using var ctx = _fixture.NewContext();
        ctx.DailyRecords.Where(r => r.HostId == host.HostId && r.RecordDate == Day.Date)
            .ExecuteUpdate(s => s.SetProperty(r => r.AiDegradedAttempts, attempts).SetProperty(r => r.AiPending, pending));
        return host;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]     // 降級待補
    [InlineData(3, false)]    // 已放棄
    [InlineData(1, false)]    // 降級後已恢復
    public void 清單分頁路徑_帶出降級次數(int attempts, bool pending)
    {
        var host = Seed("SRV-LIST", attempts, pending);

        var page = _list.Search(new RecordSearchRequest { HostIds = new List<long> { host.HostId } });

        var item = Assert.Single(page.Items);
        Assert.Equal(attempts, item.AiDegradedAttempts);
        Assert.Equal(pending, item.AiPending);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void 清單輕量投影路徑_帶狀態篩選時也帶出降級次數(int attempts, bool pending)
    {
        var host = Seed("SRV-LITE", attempts, pending);

        var page = _list.Search(new RecordSearchRequest
        {
            HostIds = new List<long> { host.HostId },
            Statuses = new List<string> { "open" }
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(attempts, item.AiDegradedAttempts);
        Assert.Equal(pending, item.AiPending);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void 詳情_帶出降級次數(int attempts, bool pending)
    {
        var host = Seed("SRV-DETAIL", attempts, pending);

        var detail = _detail.GetDetail(host.HostId, Day);

        Assert.Equal(attempts, detail.AiDegradedAttempts);
        Assert.Equal(pending, detail.AiPending);
    }
}
