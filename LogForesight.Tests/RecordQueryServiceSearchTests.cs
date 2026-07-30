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
    private readonly FakeIssueCaseStore _caseStore = new();
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
            _caseStore,
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

    /// <summary>
    /// 處理人 fallback（docs/FEEDBACK-4-PLAN.md §0.4-D／Q5）：日層級從未指派、但當日問題
    /// 屬進行中案件時，清單顯示案件處理人（後綴「（案件）」）——否則使用者會看到狀態已同步、
    /// 處理人卻空白的矛盾畫面。
    /// </summary>
    [Fact]
    public void Search_日層級未指派時_處理人欄fallback案件處理人()
    {
        var host = AddHost("HOST-A");
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\li", DisplayName = "小李" });
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        AddRecord(host, DateTime.Today, "高", issues: new[] { issue });

        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueSignatureKey.For(issue),
            IssueLabel = "disk 153", Status = IssueHandlingStatuses.InProgress, HandlerId = user.UserId,
            FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "a", UpdatedAt = DateTime.Now
        });

        var result = _service.Search(new RecordSearchRequest());

        var item = Assert.Single(result.Items);
        Assert.Equal(user.UserId, item.HandlerId);
        Assert.Equal("小李（案件）", item.HandlerName);
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

    /// <summary>
    /// docs/HISTORY.md #12：對外一律三態——日層級狀態為 wont_fix（不處理/誤報/
    /// 已知雜訊同理）時，清單的「已處理」chip 也要查得到，不能只精確比對 resolved。
    /// </summary>
    [Fact]
    public void Search_依Statuses篩選_WontFix視同已處理()
    {
        var a = AddHost("HOST-A");
        AddRecord(a, DateTime.Today, "高");
        _handlingStore.Save(new RecordHandling { HostName = a.HostName, Date = DateTime.Today, Status = HandlingStatuses.WontFix });

        var result = _service.Search(new RecordSearchRequest { Statuses = new List<string> { HandlingStatuses.Resolved } });

        Assert.Single(result.Items);
        Assert.Equal("HOST-A", result.Items[0].HostName);
        // 對外三態：清單顯示的是收斂後的 resolved，不是原始的 wont_fix
        Assert.Equal(HandlingStatuses.Resolved, result.Items[0].HandlingStatus);
        Assert.Equal("已處理", result.Items[0].HandlingStatusText);
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

    // ── 嚴重度可見性（docs/HISTORY.md S1）：釘住查詢頁分組視圖的漏套修正 ──

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

    // ── 表頭排序（docs/WEB-SPEC.md §9.2）─────────────────────────────────────

    [Fact]
    public void Search_快速路徑_指定SortKey時覆蓋預設緊急程度排序()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today.AddDays(-1), "高");
        AddRecord(b, DateTime.Today, "低");

        var result = _service.Search(new RecordSearchRequest { SortKey = "date", Ascending = true });

        // 日期升冪：不管風險等級，最早的日期排最前面——證明 SortKey 真的覆蓋了預設排序
        Assert.Equal("HOST-A", result.Items[0].HostName);
    }

    [Fact]
    public void Search_慢速路徑_指定SortKey時覆蓋預設緊急程度排序()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today.AddDays(-1), "高");
        AddRecord(b, DateTime.Today, "低");
        // Statuses 非空清單強制走慢速路徑（見類別註解）；未建立處理紀錄的日一律預設 open 外部狀態
        var result = _service.Search(new RecordSearchRequest
        {
            Statuses = new List<string> { HandlingStatuses.Open },
            SortKey = "date",
            Ascending = true
        });

        Assert.Equal(2, result.Items.Count);
        // 日期升冪：不管風險等級，最早的日期排最前面——證明 SortKey 真的覆蓋了預設排序
        Assert.Equal("HOST-A", result.Items[0].HostName);
    }

    [Fact]
    public void SearchByHost_依高風險日數升冪排序()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today, "高");
        AddRecord(a, DateTime.Today.AddDays(-1), "高");
        AddRecord(b, DateTime.Today, "高");

        var result = _service.SearchByHost(new RecordSearchRequest { SortKey = "highRisk", Ascending = true });

        Assert.Equal("HOST-B", result.Items[0].HostName);   // 1 天高風險，排最前
        Assert.Equal("HOST-A", result.Items[1].HostName);   // 2 天高風險
    }

    [Fact]
    public void SearchByDate_依主機數降冪排序()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today, "高");
        AddRecord(a, DateTime.Today.AddDays(-1), "高");
        AddRecord(b, DateTime.Today.AddDays(-1), "高");

        var result = _service.SearchByDate(new RecordSearchRequest { SortKey = "hostCount", Ascending = false });

        // AddDays(-1) 那天有 2 台主機、今天只有 1 台——降冪應把 2 台那天排最前
        Assert.Equal(2, result.Items[0].HostCount);
    }

    // ── 問題案件（docs/FEEDBACK-4-PLAN.md §2）───────────────────────────────────

    /// <summary>詳情頁的問題列帶回進行中案件的處理人／狀態／起始日，前端據此顯示連動徽章</summary>
    [Fact]
    public void GetDetail_問題有進行中案件時帶回案件資訊()
    {
        var host = AddHost("HOST-A");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小明" });
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        AddRecord(host, DateTime.Today, "高", issues: new[] { issue });

        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = host.HostName, IssueKey = IssueSignatureKey.For(issue),
            IssueLabel = "disk 153", Status = IssueHandlingStatuses.InProgress, HandlerId = handler.UserId,
            FirstLinkedDate = DateTime.Today.AddDays(-5), LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "a", UpdatedAt = DateTime.Now
        });

        var detail = _service.GetDetail(host.HostId, DateTime.Today);

        var dto = detail.TopIssues.Single();
        Assert.Equal("小明", dto.CaseHandlerName);
        Assert.Equal(IssueHandlingStatuses.InProgress, dto.CaseStatus);
        Assert.Equal(DateTime.Today.AddDays(-5).ToString("yyyy-MM-dd"), dto.CaseFirstLinkedDate);
    }

    /// <summary>沒有進行中案件時，案件欄位維持 null（既有行為不受影響）</summary>
    [Fact]
    public void GetDetail_問題無案件時案件欄位為null()
    {
        var host = AddHost("HOST-A");
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        AddRecord(host, DateTime.Today, "高", issues: new[] { issue });

        var detail = _service.GetDetail(host.HostId, DateTime.Today);

        var dto = detail.TopIssues.Single();
        Assert.Null(dto.CaseHandlerName);
        Assert.Null(dto.CaseStatus);
        Assert.Null(dto.CaseFirstLinkedDate);
    }
}

/// <summary>永遠回 null——Search 不會實際讀報告全文，這裡只是滿足建構式依賴</summary>
internal class NullReportReader : IReportReader
{
    public string? Read(string reportRef) => null;
}
