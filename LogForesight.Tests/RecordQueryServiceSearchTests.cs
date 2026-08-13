using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P1-2：<see cref="RecordListQueryService.Search"/> 的兩條路徑——
/// 沒有 Statuses/Overdue 篩選時走 <see cref="IRecordRepository.QueryPage"/>（SQL 端排序＋分頁，
/// 只為當頁載入處理狀態）；有篩選時退回既有的「全撈→算處理狀態→篩選→排序→分頁」邏輯
/// （那段邏輯本身不是這次改動的對象，這裡只驗證分支沒有把它弄壞）。
///
/// 這是 <see cref="RecordListQueryService.Search"/> 第一次有專屬測試——實際串接真正的
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
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly RecordQueryServiceFacade _service;
    private readonly HandlingServiceFacade _handlingService;

    public RecordQueryServiceSearchTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        _service = new RecordQueryServiceFacade(
            repository: repository,
            reports: new NullReportReader(),
            hosts: _hosts,
            users: _users,
            hostGroups: new FakeHostGroupStore(),
            visibility: visibility,
            handlings: _handlingStore,
            issueHandlings: _issueHandlingStore,
            cases: _caseStore,
            noiseMarks: new FakeNoiseMarkStore(),
            rules: new FakeRuleStore(),
            currentUser: FakeCurrentUser.WithCapabilities(),
            settings: _settingsStore,
            issueOwners: _issueOwners);

        // 依問題視角的批次指派測試共用同一份主機/紀錄——HandlingService 與 RecordQueryService
        // 指向同一個 repository/_recordStore，Assign() 建的案在 SearchByIssue 查得到
        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts);
        _handlingService = new HandlingServiceFacade(
            store: _handlingStore,
            issueStore: _issueHandlingStore,
            cases: _caseStore,
            caseCoordinator: caseCoordinator,
            noiseMarks: new FakeNoiseMarkStore(),
            repository: repository,
            hosts: _hosts,
            users: _users,
            visibility: visibility,
            currentUser: FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            audit: new RecordingAuditService(),
            settings: _settingsStore);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

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
    /// 處理人 fallback（docs/archive/FEEDBACK-4-PLAN.md §0.4-D／Q5）：日層級從未指派、但當日問題
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
        // §9：HandlerName 改為純顯示名，「（案件）」標記改由 HandlerFromCase 旗標帶（前端組後綴），
        // 帳號另走 HandlerAccount（前端 formatUserName 組「顯示名稱(帳號)」）
        Assert.Equal("小李", item.HandlerName);
        Assert.Equal("DOMAIN\\li", item.HandlerAccount);
        Assert.True(item.HandlerFromCase);
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
    /// docs/archive/HISTORY.md #12：對外一律三態——日層級狀態為 wont_fix（不處理/誤報/
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

    // ── 嚴重度可見性（docs/archive/HISTORY.md S1）：釘住查詢頁分組視圖的漏套修正 ──

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

    // ── 問題案件（docs/archive/FEEDBACK-4-PLAN.md §2）───────────────────────────────────

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

    // ── 依問題視角（docs/archive/FEEDBACK-4-PLAN.md §4）─────────────────────────────────

    private static LogIssueSignature DiskIssue(IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = "disk", EventId = 153,
        EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = severity
    };

    [Fact]
    public void SearchByIssue_依主機數與嚴重度分組彙總()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        var issue = DiskIssue();
        AddRecord(a, DateTime.Today.AddDays(-1), "高", issues: new[] { issue });
        AddRecord(a, DateTime.Today, "高", issues: new[] { issue });   // 同主機第二天：算同一個問題、多一個風險日
        AddRecord(b, DateTime.Today, "高", issues: new[] { issue });

        var result = _service.SearchByIssue(new RecordSearchRequest());

        var group = Assert.Single(result.Items);
        Assert.Equal("disk", group.Source);
        Assert.Equal(153, group.EventId);
        Assert.Equal(2, group.HostCount);   // HOST-A、HOST-B 各一台
        Assert.Equal(3, group.DayCount);    // HOST-A 兩天 + HOST-B 一天
        Assert.Equal("High", group.MaxSeverity);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), group.LastSeen);
    }

    /// <summary>單台出現的問題也要列出來——這裡不像 ClusterSignatures 排除單機問題，
    /// 「依問題」視角就是要回答「有哪些問題、影響多大」，單機問題一樣是答案的一部分</summary>
    [Fact]
    public void SearchByIssue_單台主機的問題也列出()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高", issues: new[] { DiskIssue() });

        var result = _service.SearchByIssue(new RecordSearchRequest());

        Assert.Single(result.Items);
        Assert.Equal(1, result.Items[0].HostCount);
    }

    /// <summary>問題負責人 badge（回饋十八輪批次F）：有規則時 IssueOwnerNames 附上；
    /// 沒有規則時是空清單，不是 null——前端 group.issueOwnerNames?.length 判斷要有東西可判斷。</summary>
    [Fact]
    public void SearchByIssue_附上問題負責人()
    {
        var owner = _users.Upsert(new WebUser { Account = "DOMAIN\\owner", DisplayName = "OOO", Active = true });
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高", issues: new[] { DiskIssue() });
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { owner.UserId } });

        var result = _service.SearchByIssue(new RecordSearchRequest());

        Assert.Equal(new List<string> { "OOO(DOMAIN\\owner)" }, result.Items[0].IssueOwnerNames);
    }

    [Fact]
    public void SearchByIssue_沒有問題負責人規則時回空清單()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高", issues: new[] { DiskIssue() });

        var result = _service.SearchByIssue(new RecordSearchRequest());

        Assert.Empty(result.Items[0].IssueOwnerNames);
    }

    [Fact]
    public void SearchByIssue_處理概況三態彙總()
    {
        var processingHost = AddHost("HOST-PROCESSING");
        var resolvedHost = AddHost("HOST-RESOLVED");
        var unhandledHost = AddHost("HOST-UNHANDLED");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小陳" });
        var issue = DiskIssue();

        AddRecord(processingHost, DateTime.Today, "高", issues: new[] { issue });
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = processingHost.HostName, IssueKey = IssueSignatureKey.For(issue),
            IssueLabel = "disk 153", Status = IssueHandlingStatuses.InProgress, HandlerId = handler.UserId,
            FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "a", UpdatedAt = DateTime.Now
        });

        AddRecord(resolvedHost, DateTime.Today, "高", issues: new[] { issue });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = resolvedHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

        AddRecord(unhandledHost, DateTime.Today, "高", issues: new[] { issue });   // 從未標記過

        var result = _service.SearchByIssue(new RecordSearchRequest());

        var group = Assert.Single(result.Items);
        Assert.Equal(3, group.HostCount);
        Assert.Contains("1 台未處理", group.HandlingSummary);
        Assert.Contains("1 台處理中", group.HandlingSummary);
        Assert.Contains("1 台已處理", group.HandlingSummary);
        var groupHandler = Assert.Single(group.Handlers);
        Assert.Equal("小陳", groupHandler.DisplayName);
        Assert.Equal(handler.UserId, groupHandler.HandlerId);   // 前端靠 Id 把姓名連到工作頁
        // docs/archive/FEEDBACK-8-PLAN.md #6：帳號素材供前端組「顯示名稱(帳號)」
        Assert.Equal("DOMAIN\\h", groupHandler.Account);
    }

    [Fact]
    public void SearchByIssue_未指派過濾_只留處理人清單為空的問題()
    {
        // §10：未指派＝這個問題沒有任何主機被指派（Handlers 為空）
        var assignedHost = AddHost("HOST-ASSIGNED");
        var unassignedHost = AddHost("HOST-UNASSIGNED");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小陳" });
        var assignedIssue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        var unassignedIssue = new LogIssueSignature
        {
            LogName = "System", Source = "Ntfs", EventId = 55,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };

        AddRecord(assignedHost, DateTime.Today, "高", issues: new[] { assignedIssue });
        _caseStore.Save(new IssueCase
        {
            CaseId = "case-1", HostName = assignedHost.HostName, IssueKey = IssueSignatureKey.For(assignedIssue),
            IssueLabel = "disk 153", Status = IssueHandlingStatuses.InProgress, HandlerId = handler.UserId,
            FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "a", UpdatedAt = DateTime.Now
        });
        AddRecord(unassignedHost, DateTime.Today, "高", issues: new[] { unassignedIssue });

        var all = _service.SearchByIssue(new RecordSearchRequest());
        Assert.Equal(2, all.Items.Count);

        var unassignedOnly = _service.SearchByIssue(new RecordSearchRequest { Unassigned = true });
        var group = Assert.Single(unassignedOnly.Items);
        Assert.Equal("Ntfs", group.Source);
        Assert.Empty(group.Handlers);
    }

    [Fact]
    public void SearchByIssue_處理概況三態篩選_只留符合狀態的問題()
    {
        // §10：by-issue 支援 statuses（群組層級 GroupStatus 三態）
        var openHost = AddHost("HOST-OPEN");
        var resolvedHost = AddHost("HOST-RES");
        var openIssue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        var resolvedIssue = new LogIssueSignature
        {
            LogName = "System", Source = "Ntfs", EventId = 55,
            EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
        };
        AddRecord(openHost, DateTime.Today, "高", issues: new[] { openIssue });   // 從未標記＝未處理
        AddRecord(resolvedHost, DateTime.Today, "高", issues: new[] { resolvedIssue });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = resolvedHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(resolvedIssue),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

        var openOnly = _service.SearchByIssue(new RecordSearchRequest { Statuses = new() { "open" } });
        var group = Assert.Single(openOnly.Items);
        Assert.Equal("disk", group.Source);
        Assert.Equal("open", group.GroupStatus);
    }

    [Fact]
    public void Search_未指派過濾_只留無有效處理人的風險日()
    {
        // §5/§10：明細視角未指派＝日層級無處理人且無案件涵蓋
        var assignedHost = AddHost("HOST-ASSIGNED");
        var unassignedHost = AddHost("HOST-UNASSIGNED");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小陳" });
        var issue = DiskIssue();

        AddRecord(assignedHost, DateTime.Today, "高", issues: new[] { issue });
        _handlingStore.Save(new RecordHandling
        {
            HostName = assignedHost.HostName, Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, HandlerId = handler.UserId, UpdatedAt = DateTime.Now
        });
        AddRecord(unassignedHost, DateTime.Today, "高", issues: new[] { issue });

        var all = _service.Search(new RecordSearchRequest());
        Assert.Equal(2, all.Items.Count);

        var unassignedOnly = _service.Search(new RecordSearchRequest { Unassigned = true });
        var item = Assert.Single(unassignedOnly.Items);
        Assert.Equal("HOST-UNASSIGNED", item.HostName);
        Assert.Null(item.HandlerId);
    }

    /// <summary>
    /// docs/archive/FEEDBACK-8-PLAN.md #4：依問題視角的處理概況——觀察中（未到期）算處理中，
    /// 觀察到期算未處理（問題仍在發生，不是「有結論」）。
    /// </summary>
    [Fact]
    public void SearchByIssue_觀察中未到期算處理中_到期算未處理()
    {
        var observingHost = AddHost("HOST-OBSERVING");
        var expiredHost = AddHost("HOST-EXPIRED");
        var issue = DiskIssue();

        AddRecord(observingHost, DateTime.Today, "高", issues: new[] { issue });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = observingHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Observing, DueDate = DateTime.Today.AddDays(7), UpdatedAt = DateTime.Now
        });

        AddRecord(expiredHost, DateTime.Today, "高", issues: new[] { issue });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = expiredHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Observing, DueDate = DateTime.Today.AddDays(-1), UpdatedAt = DateTime.Now
        });

        var result = _service.SearchByIssue(new RecordSearchRequest());

        var group = Assert.Single(result.Items);
        Assert.Contains("1 台處理中", group.HandlingSummary);
        Assert.Contains("1 台未處理", group.HandlingSummary);
    }

    /// <summary>
    /// docs/archive/FEEDBACK-8-PLAN.md #5：上方風險層級 chips 篩的是「日風險」，但依問題視角顯示的是
    /// 「問題嚴重度」——高風險日裡本來就可能同時有低嚴重度的問題（規則命中不代表整批問題都同一
    /// 嚴重度），預設「高＋中」篩選下不該漏出低嚴重度問題組。
    /// </summary>
    [Fact]
    public void SearchByIssue_高風險日內的低嚴重度問題_預設高中篩選下不出現_勾低後出現()
    {
        var host = AddHost("HOST-A");
        var lowIssue = new LogIssueSignature
        {
            LogName = "System", Source = "noisy", EventId = 111,
            EntryType = System.Diagnostics.EventLogEntryType.Information, Severity = IssueSeverity.Low
        };
        AddRecord(host, DateTime.Today, "高", issues: new[] { lowIssue });

        var filtered = _service.SearchByIssue(new RecordSearchRequest { RiskLevels = new List<string> { "高", "中" } });
        Assert.Empty(filtered.Items);

        var withLow = _service.SearchByIssue(new RecordSearchRequest { RiskLevels = new List<string> { "高", "中", "低" } });
        Assert.Single(withLow.Items);

        var unfiltered = _service.SearchByIssue(new RecordSearchRequest());
        Assert.Single(unfiltered.Items);
    }

    /// <summary>
    /// 回饋十三輪新增項1：BuildFilter 的 Categories 是記錄層過濾（record.TopIssues 任一簽章屬選中
    /// 類別即整筆記錄通過，見 RecordFilterMatcher），但依問題視角把記錄的全部簽章都攤開分組——
    /// 修正前，篩「安全」時同一天裡的「其他」類問題（因為同記錄有安全類簽章而讓記錄通過過濾）
    /// 仍會混入清單。與上面嚴重度過濾（RiskLevels）同一種漏法，修法也相同：疊加問題層過濾。
    /// </summary>
    [Fact]
    public void SearchByIssue_篩安全類型_其他類型問題不應出現()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高", issues: new[]
        {
            Issue("security", 4625, IssueSeverity.High, IssueCategory.Security),
            Issue("other", 9999, IssueSeverity.Medium, IssueCategory.Other)
        });

        var filtered = _service.SearchByIssue(new RecordSearchRequest { Categories = new List<string> { "Security" } });

        var group = Assert.Single(filtered.Items);
        Assert.Equal("Security", group.Category);
    }

    [Fact]
    public void SearchByIssue_依主機數排序()
    {
        var issueA = DiskIssue();
        AddRecord(AddHost("HOST-A1"), DateTime.Today, "高", issues: new[] { issueA });
        AddRecord(AddHost("HOST-A2"), DateTime.Today, "高", issues: new[] { issueA });

        var issueB = new LogIssueSignature
        {
            LogName = "Application", Source = "other", EventId = 999,
            EntryType = System.Diagnostics.EventLogEntryType.Warning, Severity = IssueSeverity.High
        };
        AddRecord(AddHost("HOST-B1"), DateTime.Today, "高", issues: new[] { issueB });

        var result = _service.SearchByIssue(new RecordSearchRequest { SortKey = "hostCount", Ascending = true });

        Assert.Equal("other", result.Items[0].Source);   // 1 台，排最前（升冪）
        Assert.Equal("disk", result.Items[1].Source);    // 2 台
    }

    // ── 跨主機批次指派（docs/archive/FEEDBACK-4-PLAN.md §4）─────────────────────────────

    [Fact]
    public void BulkAssignIssueCase_對每台主機建案_已有案件的保留原處理人並列入略過()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var owner = _users.Upsert(new WebUser { Account = "DOMAIN\\owner", DisplayName = "原處理人" });
        var newHandler = _users.Upsert(new WebUser { Account = "DOMAIN\\new", DisplayName = "新處理人" });
        var issue = DiskIssue();
        AddRecord(host1, DateTime.Today, "高", issues: new[] { issue });
        AddRecord(host2, DateTime.Today, "高", issues: new[] { issue });

        // host1 先有案件
        _handlingService.Assign(host1.HostId, DateTime.Today, owner.UserId);

        var result = _handlingService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = new List<long> { host1.HostId, host2.HostId },
            HandlerId = newHandler.UserId
        });

        Assert.Equal(1, result.Created);   // 只有 host2 真的建案
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("HOST-A", skipped.HostName);
        // 全站統一的「顯示名稱(帳號)」（docs/archive/FEEDBACK-10-PLAN.md §6）
        Assert.Equal("原處理人(DOMAIN\\owner)", skipped.ExistingHandlerName);
    }

    // ── 回饋第十輪 §9（改派）／§12（分攤）────────────────────────────────────

    /// <summary>§12：Assignments 讓每台主機各有處理人——群組分攤靠這個欄位落盤</summary>
    [Fact]
    public void BulkAssignIssueCase_逐台指派不同處理人()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var first = _users.Upsert(new WebUser { Account = "DOMAIN\\a", DisplayName = "甲" });
        var second = _users.Upsert(new WebUser { Account = "DOMAIN\\b", DisplayName = "乙" });
        var issue = DiskIssue();
        AddRecord(host1, DateTime.Today, "高", issues: new[] { issue });
        AddRecord(host2, DateTime.Today, "高", issues: new[] { issue });

        var result = _handlingService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = new List<long> { host1.HostId, host2.HostId },
            Assignments = new List<IssueCaseAssignmentDto>
            {
                new() { HostId = host1.HostId, HandlerId = first.UserId },
                new() { HostId = host2.HostId, HandlerId = second.UserId }
            }
        });

        Assert.Equal(2, result.Created);
        Assert.Equal(first.UserId, _caseStore.GetOpen("HOST-A", IssueSignatureKey.For(issue))!.HandlerId);
        Assert.Equal(second.UserId, _caseStore.GetOpen("HOST-B", IssueSignatureKey.For(issue))!.HandlerId);
    }

    /// <summary>
    /// §9：勾了「改派」的主機才換人，其餘維持既有的「保留原處理人並列入略過」語意——
    /// 沒有這個分野，一次批次指派就會把別人手上的工作全部靜默搶走。
    /// </summary>
    [Fact]
    public void BulkAssignIssueCase_只改派勾選的主機()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var owner = _users.Upsert(new WebUser { Account = "DOMAIN\\owner", DisplayName = "原處理人" });
        var newHandler = _users.Upsert(new WebUser { Account = "DOMAIN\\new", DisplayName = "新處理人" });
        var issue = DiskIssue();
        AddRecord(host1, DateTime.Today, "高", issues: new[] { issue });
        AddRecord(host2, DateTime.Today, "高", issues: new[] { issue });

        // 兩台都先有 owner 的案件
        _handlingService.Assign(host1.HostId, DateTime.Today, owner.UserId);
        _handlingService.Assign(host2.HostId, DateTime.Today, owner.UserId);

        var result = _handlingService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = new List<long> { host1.HostId, host2.HostId },
            HandlerId = newHandler.UserId,
            ReassignHostIds = new List<long> { host1.HostId }
        });

        Assert.Equal(0, result.Created);
        var reassigned = Assert.Single(result.Reassigned);
        Assert.Equal("HOST-A", reassigned.HostName);
        Assert.Equal("HOST-B", Assert.Single(result.Skipped).HostName);

        Assert.Equal(newHandler.UserId, _caseStore.GetOpen("HOST-A", IssueSignatureKey.For(issue))!.HandlerId);
        Assert.Equal(owner.UserId, _caseStore.GetOpen("HOST-B", IssueSignatureKey.For(issue))!.HandlerId);
    }

    /// <summary>§12：群組候選人只列啟用中的成員——分攤給停用帳號等於那幾台沒人處理</summary>
    [Fact]
    public void GetHandlerCandidates_排除停用成員()
    {
        var active = _users.Upsert(new WebUser { Account = "DOMAIN\\a", DisplayName = "甲", GroupIds = new List<long> { 7 } });
        _users.Upsert(new WebUser { Account = "DOMAIN\\b", DisplayName = "乙", GroupIds = new List<long> { 7 }, Active = false });

        var candidates = _handlingService.GetHandlerCandidates(7);

        Assert.Equal(active.UserId, Assert.Single(candidates).UserId);
    }

    [Fact]
    public void PreviewIssueCaseAssign_列出受影響主機與既有處理人()
    {
        var host = AddHost("HOST-A");
        var owner = _users.Upsert(new WebUser { Account = "DOMAIN\\owner", DisplayName = "原處理人" });
        var issue = DiskIssue();
        AddRecord(host, DateTime.Today, "高", issues: new[] { issue });
        _handlingService.Assign(host.HostId, DateTime.Today, owner.UserId);

        var preview = _handlingService.PreviewIssueCaseAssign("disk", 153, null, null);

        var item = Assert.Single(preview.Hosts);
        Assert.Equal("HOST-A", item.HostName);
        Assert.Equal("原處理人", item.ExistingHandlerName);
        Assert.Equal(1, preview.TotalHostCount);
        Assert.False(preview.Truncated);
    }
}

/// <summary>永遠回 null——Search 不會實際讀報告全文，這裡只是滿足建構式依賴</summary>
internal class NullReportReader : IReportReader
{
    public string? Read(string reportRef) => null;
}
