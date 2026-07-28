using LogForesight.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P1-2：<see cref="RecordQueryService.Search"/> 的兩條路徑——
/// 沒有 Statuses/Overdue 篩選時走 <see cref="IRecordRepository.QueryPage"/>（SQL 端排序＋分頁，
/// 只為當頁載入處理狀態）；有篩選時退回既有的「全撈→算處理狀態→篩選→排序→分頁」邏輯
/// （那段邏輯本身不是這次改動的對象，這裡只驗證分支沒有把它弄壞）。
///
/// 這是 <see cref="RecordQueryService.Search"/> 第一次有專屬測試——實際串接真正的
/// <see cref="EfAnalysisRecordStore"/>＋<see cref="RecordRepository"/>，而非重新實作一份簡化邏輯，
/// 這樣測到的是真正會跑進 SQL 分頁下推的那條路徑。
/// </summary>
public class RecordQueryServiceSearchTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly RecordQueryService _service;

    public RecordQueryServiceSearchTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        _service = new RecordQueryService(
            repository,
            new NullReportReader(),
            _hosts,
            _users,
            new FakeHostGroupStore(),
            visibility,
            _handlingStore,
            _issueHandlingStore,
            new FakeNoiseMarkStore(),
            new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(),
            _settingsStore);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => _hosts.Upsert(new WebHost { HostName = name });

    private void AddRecord(WebHost host, DateTime date, string risk, bool correlation = false, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            Headline = $"{host.HostName} {date:MM-dd}",
            CorrelationAlerts = correlation ? new List<string> { "跨主機同時重啟" } : new List<string>(),
            TopIssues = issues.ToList()
        });

    // ── 快速路徑（無 Statuses/Overdue）─────────────────────────────────────────

    [Fact]
    public void Search_無篩選條件_排序依風險等級_關聯訊號_日期()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today.AddDays(-2), "低");
        AddRecord(b, DateTime.Today, "高", correlation: false);
        AddRecord(a, DateTime.Today.AddDays(-1), "高", correlation: true);

        var result = _service.Search(new RecordSearchRequest { Page = 1, PageSize = 50 });

        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "HOST-A", "HOST-B", "HOST-A" }, result.Items.Select(i => i.HostName));
        Assert.Equal(
            new[] { DateTime.Today.AddDays(-1), DateTime.Today, DateTime.Today.AddDays(-2) },
            result.Items.Select(i => DateTime.Parse(i.Date)));
    }

    [Fact]
    public void Search_無篩選條件_分頁正確()
    {
        var host = AddHost("HOST-A");
        for (var i = 0; i < 5; i++)
            AddRecord(host, DateTime.Today.AddDays(-i), "低");

        var page1 = _service.Search(new RecordSearchRequest { Page = 1, PageSize = 2 });
        var page2 = _service.Search(new RecordSearchRequest { Page = 2, PageSize = 2 });

        Assert.Equal(5, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.NotEqual(page1.Items[0].Date, page2.Items[0].Date);
    }

    /// <summary>快速路徑只為當頁載入處理狀態，仍必須正確帶出處理人與狀態文字</summary>
    [Fact]
    public void Search_無篩選條件_正確帶出處理狀態與處理人()
    {
        var host = AddHost("HOST-A");
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });
        AddRecord(host, DateTime.Today, "高");
        _handlingStore.Save(new RecordHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today,
            Status = HandlingStatuses.InProgress,
            HandlerId = user.UserId
        });

        var result = _service.Search(new RecordSearchRequest());

        var item = Assert.Single(result.Items);
        Assert.Equal(HandlingStatuses.InProgress, item.HandlingStatus);
        Assert.Equal("王小明", item.HandlerName);
    }

    [Fact]
    public void Search_無篩選條件_授權範圍以外的主機不出現()
    {
        var visible = AddHost("HOST-A");
        // 直接建一台不在 FakeHostStore 授權清單裡的紀錄（用不存在的 HostId 模擬）：
        // AlwaysVisibleService 依 _hosts.GetAll() 決定可見範圍，未登錄的主機不會出現在可見清單，
        // 因此其紀錄應在 RecordRepository 的可見範圍過濾中被排除
        AddRecord(visible, DateTime.Today, "高");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = 9999, Host = "HOST-GHOST", Date = DateTime.Today, RiskLevel = "高", Headline = "ghost"
        });

        var result = _service.Search(new RecordSearchRequest());

        Assert.Single(result.Items);
        Assert.Equal("HOST-A", result.Items[0].HostName);
    }

    // ── 慢速路徑（Statuses / Overdue）：驗證分支沒有弄壞既有邏輯 ──────────────────

    [Fact]
    public void Search_依Statuses篩選_走慢速路徑仍正確篩選()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today, "高"); // 無問題、無日層級狀態 → open
        AddRecord(b, DateTime.Today, "高"); // 無問題、有日層級 resolved
        _handlingStore.Save(new RecordHandling { HostName = b.HostName, Date = DateTime.Today, Status = HandlingStatuses.Resolved });

        var result = _service.Search(new RecordSearchRequest { Statuses = new List<string> { HandlingStatuses.Resolved } });

        Assert.Single(result.Items);
        Assert.Equal("HOST-B", result.Items[0].HostName);
        Assert.Equal(HandlingStatuses.Resolved, result.Items[0].HandlingStatus);
    }

    /// <summary>日層級 DueDate 過期且未結案＝逾期；未過期或已結案都不算</summary>
    [Fact]
    public void Search_依Overdue篩選_走慢速路徑仍正確篩選()
    {
        var overdue = AddHost("HOST-OVERDUE");
        var onTrack = AddHost("HOST-ONTRACK");
        AddRecord(overdue, DateTime.Today, "高");
        AddRecord(onTrack, DateTime.Today, "高");
        _handlingStore.Save(new RecordHandling
        {
            HostName = overdue.HostName, Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, DueDate = DateTime.Today.AddDays(-1)
        });
        _handlingStore.Save(new RecordHandling
        {
            HostName = onTrack.HostName, Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, DueDate = DateTime.Today.AddDays(7)
        });

        var result = _service.Search(new RecordSearchRequest { Overdue = true });

        Assert.Single(result.Items);
        Assert.Equal("HOST-OVERDUE", result.Items[0].HostName);
        Assert.True(result.Items[0].IsOverdue);
    }

    [Fact]
    public void Search_Statuses為空清單_視同不篩選走快速路徑()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高");

        // 空清單（不是 null）應等同「沒有篩選」，走快速路徑也要能正確回傳
        var result = _service.Search(new RecordSearchRequest { Statuses = new List<string>() });

        Assert.Single(result.Items);
    }

    // ── 嚴重度可見性（docs/SHARED-STANDARDS-PLAN.md S1）：釘住查詢頁分組視圖的漏套修正 ──

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity, IssueCategory category) =>
        new() { LogName = "System", Source = source, EventId = eventId, Severity = severity, Category = category, Count = 1 };

    /// <summary>
    /// 修正前：SearchByHost／SearchByDate 的類別聚合完全不過濾嚴重度可見性，
    /// SiteHidden 模式下這裡仍會列出未勾選層級的類別——這正是原本的漏套。
    /// 過濾現由 RecordRepository 統一套用，SearchByHost 不必自己判斷即可繼承正確結果。
    /// </summary>
    [Fact]
    public void SearchByHost_SiteHidden模式下類別聚合不含被隱藏層級()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高", issues: new[]
        {
            Issue("disk", 153, IssueSeverity.Critical, IssueCategory.Storage),
            Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource)
        });
        _severityVisibility.VisibleSeverities = new HashSet<string> { "Critical", "High" };

        var result = _service.SearchByHost(new RecordSearchRequest());

        var group = Assert.Single(result.Items);
        Assert.Equal(new[] { "Storage" }, group.Categories);
    }
}

/// <summary>永遠回 null——Search 不會實際讀報告全文，這裡只是滿足建構式依賴</summary>
internal class NullReportReader : IReportReader
{
    public string? Read(string reportRef) => null;
}
