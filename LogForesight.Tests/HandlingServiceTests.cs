using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;
using static LogForesight.Tests.TestData;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態與指派（docs/WEB-SPEC.md §9.3）。
///
/// 這組測試釘住的核心語意是 2026-07-21 定案的**負責人與處理人分離**：
/// 「A 主機預設管理者是 OOO，但管理者覺得這個問題緊急先給 XXX 處理，
/// 這時管理者不變，但問題的處理者會被指派到 XXX 身上」。
/// </summary>
public class HandlingServiceTests : IDisposable
{
    private readonly FakeUserStore _users = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeNoiseMarkStore _noiseMarks = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeRecordRepository _repository;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly FakeSmtpMailSender _mailSender = new();
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private readonly WebUser _owner;
    private readonly WebUser _other;
    private readonly WebHost _host;

    public HandlingServiceTests()
    {
        _owner = _users.Upsert(new WebUser { Account = "DOMAIN\\ooo", DisplayName = "OOO" });
        _other = _users.Upsert(new WebUser { Account = "DOMAIN\\xxx", DisplayName = "XXX" });

        _host = _hosts.Upsert(new WebHost
        {
            HostName = "SRV-A",
            OwnerUserIds = new List<long> { _owner.UserId }
        });

        _repository = new FakeRecordRepository(_hosts);
        _repository.AddRecord(_host.HostName, Today);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlings, _repository, _hosts);
    }

    private static DateTime Today => DateTime.Today;

    private HandlingServiceFacade Create(params Capability[] capabilities) => Create(null, capabilities);

    private HandlingServiceFacade Create(IUserGroupStore? groups, params Capability[] capabilities)
    {
        var currentUser = FakeCurrentUser.ForUser(_other.UserId, capabilities);
        return new HandlingServiceFacade(
            store: _handlings,
            issueStore: _issueHandlings,
            cases: _cases,
            caseCoordinator: _caseCoordinator,
            noiseMarks: _noiseMarks,
            repository: _repository,
            hosts: _hosts,
            users: _users,
            visibility: new AlwaysVisibleService(_hosts),
            currentUser: currentUser,
            audit: _audit,
            settings: _settings,
            groups: groups,
            issueOwners: _issueOwners);
    }

    /// <summary>接上真的 <see cref="MailNotificationService"/>（回饋十八輪體檢輪）：專供上報通知
    /// 防重寄的迴歸測試——一般測試走上面沒有 mail 的 Create，通知行為靜默跳過即可。</summary>
    private MailNotificationService CreateMail()
    {
        var groups = new FakeUserGroupStore();
        var admins = groups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "admin1", Email = "admin1@test.local", Active = true, GroupIds = new List<long> { admins.GroupId } });
        _settings.Update(s => s.MailEnabled = true);
        return new MailNotificationService(
            _settings, _mailSender, _hosts, _users, groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlings, new MailNotifyStateStore(_fx.Blob("mail_notify_state")), _issueOwners);
    }

    private HandlingServiceFacade CreateWithMail(MailNotificationService mail, params Capability[] capabilities)
    {
        var currentUser = FakeCurrentUser.ForUser(_other.UserId, capabilities);
        return new HandlingServiceFacade(
            store: _handlings,
            issueStore: _issueHandlings,
            cases: _cases,
            caseCoordinator: _caseCoordinator,
            noiseMarks: _noiseMarks,
            repository: _repository,
            hosts: _hosts,
            users: _users,
            visibility: new AlwaysVisibleService(_hosts),
            currentUser: currentUser,
            audit: _audit,
            settings: _settings,
            issueOwners: _issueOwners,
            mail: mail);
    }

    /// <summary>
    /// 核心情境：admin 把處理人改派給非負責人。
    /// **主機負責人必須維持不變**——那是主機的長期屬性，不因單一事件改動。
    /// </summary>
    [Fact]
    public void 改派處理人_主機負責人不變()
    {
        var service = Create(Capability.Assign, Capability.Handle);

        var result = service.Assign(_host.HostId, Today, _other.UserId);

        Assert.Equal("XXX", result.HandlerName);
        Assert.Equal(new[] { "OOO" }, result.OwnerNames);

        // 主機資料本身沒有被改動
        Assert.Equal(new[] { _owner.UserId }, _hosts.Get(_host.HostId)!.OwnerUserIds);
    }

    [Fact]
    public void 改派_稽核摘要明確記載負責人不變()
    {
        Create(Capability.Assign).Assign(_host.HostId, Today, _other.UserId);

        // 會有兩筆指派稽核：系統自動帶入唯一負責人，以及本次的人工改派。
        // 兩者都該留下——自動帶入也是一次指派行為，事後要查得出處理人怎麼變成現在這樣
        var systemEntry = _audit.Entries.Single(e =>
            e.Action == AuditActions.HandlingAssign && e.Account == AuditActions.SystemAccount);
        Assert.Contains("自動帶入", systemEntry.Summary);

        var manualEntry = _audit.Entries.Last(e =>
            e.Action == AuditActions.HandlingAssign && e.Account != AuditActions.SystemAccount);
        Assert.Contains("處理人", manualEntry.Summary);
        Assert.Contains("XXX", manualEntry.Summary);
        Assert.Contains("負責人不變", manualEntry.Summary);
        Assert.Contains("OOO", manualEntry.Summary);
    }

    [Fact]
    public void 指派給人時_自動由未處理推進為處理中()
    {
        var result = Create(Capability.Assign).Assign(_host.HostId, Today, _other.UserId);

        Assert.Equal(HandlingStatuses.InProgress, result.Status);
    }

    /// <summary>
    /// 回饋十三輪，體檢 H1 殘餘：批次指派（IssueHandlingCommandService.BulkAssignIssueCase）
    /// 已有「對方沒有處理能力」的提示，日層級指派原本沒有——同一套「不擋、只提示」決策，
    /// 同一個 UserCapabilityResolver 事實來源（沒有群組、也不是任何主機負責人，兩條授權路徑都不成立）。
    /// </summary>
    [Fact]
    public void 指派給沒有Handle能力的人_仍成立但回報()
    {
        var noCapUser = _users.Upsert(new WebUser { Account = "DOMAIN\\intern", DisplayName = "小實習生" });

        var result = Create(Capability.Assign).Assign(_host.HostId, Today, noCapUser.UserId);

        Assert.Equal("小實習生", result.HandlerName);   // 指派仍然成立，不擋
        Assert.True(result.AssigneeCannotHandle);
    }

    /// <summary>負責人隱含 User 角色（§2b）也算數——不該被誤報</summary>
    [Fact]
    public void 指派給主機負責人_有Handle能力不回報()
    {
        var result = Create(Capability.Assign).Assign(_host.HostId, Today, _owner.UserId);

        Assert.False(result.AssigneeCannotHandle);
    }

    [Fact]
    public void 指派已結案的風險日_不改動狀態()
    {
        var service = Create(Capability.Assign, Capability.Handle);
        service.Update(_host.HostId, Today, new UpdateHandlingRequest { Status = HandlingStatuses.Resolved });

        var result = service.Assign(_host.HostId, Today, _other.UserId);

        Assert.Equal(HandlingStatuses.Resolved, result.Status);
    }

    [Fact]
    public void 取消指派_處理人清空()
    {
        var service = Create(Capability.Assign);
        service.Assign(_host.HostId, Today, _other.UserId);

        var result = service.Assign(_host.HostId, Today, null);

        Assert.Null(result.HandlerId);
        Assert.Null(result.HandlerName);
    }

    [Fact]
    public void 指派給停用帳號_被拒()
    {
        _other.Active = false;
        _users.Upsert(_other);

        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Assign).Assign(_host.HostId, Today, _other.UserId));

        Assert.Contains("停用", ex.Message);
    }

    /// <summary>負責人恰好一人時自動帶入——多人時不猜，猜錯會讓該處理的人以為有別人在處理</summary>
    [Fact]
    public void 負責人唯一_首次讀取自動帶入處理人()
    {
        var result = Create(Capability.Handle).Get(_host.HostId, Today);

        Assert.Equal("OOO", result.HandlerName);
    }

    [Fact]
    public void 負責人多人_不自動帶入()
    {
        _hosts.SetOwners(_host.HostId, new[] { _owner.UserId, _other.UserId });

        var result = Create(Capability.Handle).Get(_host.HostId, Today);

        Assert.Null(result.HandlerName);
    }

    [Fact]
    public void 無負責人_不自動帶入()
    {
        _hosts.SetOwners(_host.HostId, Array.Empty<long>());

        Assert.Null(Create(Capability.Handle).Get(_host.HostId, Today).HandlerName);
    }

    // ── 問題負責人優先於主機負責人（回饋十八輪批次F）──────────────────────────

    /// <summary>問題負責人恰一人時優先於主機負責人——主機負責人是 OOO，但這天的問題
    /// 有獨立的問題負責人 XXX，自動帶入的應該是 XXX。</summary>
    [Fact]
    public void 問題負責人恰一人時優先於主機負責人()
    {
        var day = Today.AddDays(1);
        var issue = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, issue);
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId } });

        var result = Create(Capability.Handle).Get(_host.HostId, day);

        Assert.Equal("XXX", result.HandlerName);
    }

    /// <summary>問題負責人多人時不猜，落回主機負責人恰一人的既有規則</summary>
    [Fact]
    public void 問題負責人多人時落回主機負責人()
    {
        var third = _users.Upsert(new WebUser { Account = "DOMAIN\\yyy", DisplayName = "YYY" });
        var day = Today.AddDays(1);
        var issue = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, issue);
        _issueOwners.Upsert(new IssueOwnerRule
        {
            SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId, third.UserId }
        });

        var result = Create(Capability.Handle).Get(_host.HostId, day);

        Assert.Equal("OOO", result.HandlerName);   // 落回主機負責人（唯一：_owner）
    }

    /// <summary>當天多個問題各自命中不同問題負責人時不猜——與主機負責人多人時同一套精神，
    /// 落回主機負責人恰一人的規則。</summary>
    [Fact]
    public void 當天多問題各自負責人不同時不猜_落回主機負責人()
    {
        var third = _users.Upsert(new WebUser { Account = "DOMAIN\\yyy", DisplayName = "YYY" });
        var day = Today.AddDays(1);
        var a = Issue("disk", 153);
        var b = Issue("network", 201);
        _repository.AddRecord(_host.HostName, day, a, b);
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId } });
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "network", EventId = 201, OwnerUserIds = new List<long> { third.UserId } });

        var result = Create(Capability.Handle).Get(_host.HostId, day);

        Assert.Equal("OOO", result.HandlerName);
    }

    /// <summary>當天多個問題命中的問題負責人恰好是同一人時，聯集去重後仍算「恰一人」</summary>
    [Fact]
    public void 當天多問題負責人聯集為同一人時採用()
    {
        var day = Today.AddDays(1);
        var a = Issue("disk", 153);
        var b = Issue("network", 201);
        _repository.AddRecord(_host.HostName, day, a, b);
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId } });
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "network", EventId = 201, OwnerUserIds = new List<long> { _other.UserId } });

        var result = Create(Capability.Handle).Get(_host.HostId, day);

        Assert.Equal("XXX", result.HandlerName);
    }

    /// <summary>停用的問題負責人不算——同主機負責人的既有語意</summary>
    [Fact]
    public void 問題負責人帳號停用時不採用_落回主機負責人()
    {
        _other.Active = false;
        _users.Upsert(_other);
        var day = Today.AddDays(1);
        var issue = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, issue);
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId } });

        var result = Create(Capability.Handle).Get(_host.HostId, day);

        Assert.Equal("OOO", result.HandlerName);
    }

    /// <summary>問題負責人的優先序在實際寫入（第一次 Update/Assign）時同樣生效，不只是顯示</summary>
    [Fact]
    public void 問題負責人優先序_實際寫入時同樣生效()
    {
        var day = Today.AddDays(1);
        var issue = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, issue);
        _issueOwners.Upsert(new IssueOwnerRule { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { _other.UserId } });

        Create(Capability.Handle).Update(_host.HostId, day, new UpdateHandlingRequest { Status = HandlingStatuses.InProgress });

        Assert.Equal(_other.UserId, _handlings.Get(_host.HostName, day)!.HandlerId);
    }

    // ── 狀態維護 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 更新狀態_寫入快照與歷程()
    {
        var service = Create(Capability.Handle);

        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        {
            Status = HandlingStatuses.InProgress,
            Note = "已聯絡機房安排停機"
        });

        var handling = _handlings.Get(_host.HostName, Today)!;
        Assert.Equal(HandlingStatuses.InProgress, handling.Status);
        Assert.Equal("已聯絡機房安排停機", handling.Note);

        var logs = _handlings.GetLogs(_host.HostName, Today);
        Assert.Contains(logs, l => l.Note == "已聯絡機房安排停機");
    }

    /// <summary>docs/archive/FEEDBACK-8-PLAN.md #6：操作歷程補上操作者的顯示名稱素材，
    /// 讓前端能統一顯示「顯示名稱(帳號)」而不只是帳號</summary>
    [Fact]
    public void 更新處理狀態_歷程DTO帶操作者顯示名稱()
    {
        // FakeCurrentUser 固定回報 Account="test-user"（與傳入的 userId 無關），這裡註冊
        // 同帳號的使用者以驗證 ActorDisplayName 解析路徑
        _users.Upsert(new WebUser { Account = "test-user", DisplayName = "測試操作者" });
        var service = Create(Capability.Handle);

        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        { Status = HandlingStatuses.InProgress, Note = "已聯絡機房" });

        var log = Assert.Single(service.GetLogs(_host.HostId, Today));
        Assert.Equal("test-user", log.ActorAccount);
        Assert.Equal("測試操作者", log.ActorDisplayName);
    }

    /// <summary>歷程是 append-only：後續更新不會蓋掉先前的說明，完整敘事才留得下來</summary>
    [Fact]
    public void 多次更新_歷程保留完整敘事()
    {
        var service = Create(Capability.Handle);

        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        { Status = HandlingStatuses.InProgress, Note = "已聯絡機房" });
        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        { Status = HandlingStatuses.InProgress, Note = "已更換硬碟" });
        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        { Status = HandlingStatuses.Resolved, Note = "觀察一週無異常，結案" });

        var logs = service.GetLogs(_host.HostId, Today);

        Assert.Equal(3, logs.Count);
        Assert.Equal("已聯絡機房", logs[0].Note);
        Assert.Equal("已更換硬碟", logs[1].Note);
        Assert.Equal("觀察一週無異常，結案", logs[2].Note);
    }

    [Fact]
    public void 未知狀態_被拒()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).Update(_host.HostId, Today,
                new UpdateHandlingRequest { Status = "not_a_status" }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void 預計完成日早於今天_被拒()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).Update(_host.HostId, Today, new UpdateHandlingRequest
            {
                Status = HandlingStatuses.InProgress,
                DueDate = DateTime.Today.AddDays(-1)
            }));

        Assert.Contains("不可早於今天", ex.Message);
    }

    /// <summary>不允許對不存在的分析紀錄建立處理狀態——那會產生指向空白的待辦事項</summary>
    [Fact]
    public void 分析紀錄不存在_被拒()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).Update(_host.HostId, Today.AddDays(-99),
                new UpdateHandlingRequest { Status = HandlingStatuses.InProgress }));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    // ── 能力邊界 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// user 沒有 Assign 能力：畫面上處理人是唯讀文字而不是下拉。
    /// （API 層的擋下由 PermissionFilter 負責，這裡驗證的是 DTO 旗標正確。）
    /// </summary>
    [Fact]
    public void 無Assign能力_DTO標示不可指派()
    {
        var result = Create(Capability.Handle).Get(_host.HostId, Today);

        Assert.False(result.CanAssign);
        Assert.True(result.CanHandle);
    }

    [Fact]
    public void 具Assign能力_DTO標示可指派()
    {
        var result = Create(Capability.Handle, Capability.Assign).Get(_host.HostId, Today);

        Assert.True(result.CanAssign);
    }

    // ── 待辦統計 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 待辦統計_計入未處理與逾期()
    {
        var service = Create(Capability.Handle);
        service.Update(_host.HostId, Today, new UpdateHandlingRequest
        {
            Status = HandlingStatuses.InProgress,
            DueDate = DateTime.Today.AddDays(3)
        });

        // 直接寫入一筆已逾期的資料（Update 會擋下過去的日期）
        _repository.AddRecord(_host.HostName, Today.AddDays(-1));
        _handlings.Save(new RecordHandling
        {
            HostName = _host.HostName,
            Date = Today.AddDays(-1),
            Status = HandlingStatuses.Open,
            DueDate = DateTime.Today.AddDays(-2)
        });

        var todo = service.GetTodo(_repository.Query(new RecordQueryFilter()));

        Assert.Equal(1, todo.InProgressCount);
        Assert.Equal(1, todo.OpenCount);
        Assert.Equal(1, todo.OverdueCount);
    }

    /// <summary>
    /// 釘住儀表板「清單全是未處理、待辦卻顯示 0」的修正：
    /// 沒有 handling 列的風險日**就是**未處理，不是「不存在的待辦」。
    /// </summary>
    [Fact]
    public void 待辦統計_從未處理過的風險日視為未處理()
    {
        var todo = Create(Capability.Handle).GetTodo(_repository.Query(new RecordQueryFilter()));

        Assert.Equal(1, todo.OpenCount);
        Assert.Equal(0, todo.InProgressCount);
        Assert.Equal(0, todo.OverdueCount);
    }

    /// <summary>
    /// 待辦母體＝高＋中風險日（docs/archive/HISTORY.md S3）：低風險日即使從未處理過，
    /// 也不進待辦——這條規則由 GetTodo 內部強制套用，呼叫端不必自己先過濾。
    /// </summary>
    [Fact]
    public void 待辦統計_低風險日不計入母體()
    {
        _repository.AddRecord("SRV-LOW", Today, "低");

        var todo = Create(Capability.Handle).GetTodo(_repository.Query(new RecordQueryFilter()));

        // 建構式已預先加入一筆 _host 的高風險日；這裡只驗證新增的低風險日沒有讓計數增加
        Assert.Equal(1, todo.OpenCount);
    }

    // ── 問題層級處理狀態（方案 B）─────────────────────────────────────────────

    // 預設 Severity=High：落在 SystemSettings.UnhandledSeverities 的預設值內，
    // 讓這裡的測試專注在結案狀態組合的計算邏輯，不受未處理等級篩選影響
    // （等級篩選本身的行為由 DayHandlingDerivationTests 專門釘住）
    // Issue(...) 已搬到 TestDoubles\TestData.cs（與 DayHandlingDerivationTests 原本逐字相同，已合併）。

    [Fact]
    public void 標記部分問題_日進度反映已結案數與處理中()
    {
        var day = Today.AddDays(-3);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);

        var result = Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });

        Assert.Equal(2, result.TotalIssues);
        Assert.Equal(1, result.ClosedIssues);
        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
    }

    [Fact]
    public void 標記全部問題_日狀態推導為已處理()
    {
        var day = Today.AddDays(-4);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });
        var result = service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(b),
            Status = IssueHandlingStatuses.KnownNoise
        });

        Assert.Equal(2, result.ClosedIssues);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
    }

    [Fact]
    public void 清除問題標記_回到未處理()
    {
        var day = Today.AddDays(-5);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });
        var cleared = service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = ""
        });

        Assert.Equal(0, cleared.ClosedIssues);
        Assert.Equal(string.Empty, cleared.Status);
        Assert.Equal(HandlingStatuses.Open, cleared.DayStatus);
    }

    /// <summary>
    /// Phase 2 釘樁：問題全部結案的風險日，即使日層級從沒被人動過，也不再算未處理。
    /// 這是「日狀態由問題層推導」在待辦統計上的兌現。
    /// </summary>
    [Fact]
    public void 待辦統計_問題全部結案的風險日不算未處理()
    {
        var day = Today.AddDays(-7);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        var record = _repository.AddRecord(_host.HostName, day, a, b);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });
        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(b),
            Status = IssueHandlingStatuses.WontFix
        });

        var todo = service.GetTodo(new[] { record });

        Assert.Equal(0, todo.OpenCount);
        Assert.Equal(0, todo.InProgressCount);
    }

    /// <summary>
    /// docs/archive/HISTORY.md #12：日層級 fallback 為 wont_fix（使用者把整天標成這個狀態、
    /// 當天沒有問題層級標記）時，對外三態要算作「已處理」。這是原始 bug 的釘樁——舊版
    /// GetTodo 的三個桶（Open/InProgress/Resolved）都數不到 wont_fix，讓「未完成」
    /// （Total－Resolved）把已結案的日子誤算成未完成。
    /// </summary>
    [Fact]
    public void 待辦統計_日層級WontFix算入已處理()
    {
        var day = Today.AddDays(-18);
        var a = Issue("disk", 153);
        var record = _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        service.Update(_host.HostId, day, new UpdateHandlingRequest
        {
            Status = HandlingStatuses.WontFix,
            Note = "評估後決定不處理"
        });

        var todo = service.GetTodo(new[] { record });

        Assert.Equal(0, todo.OpenCount);
        Assert.Equal(0, todo.InProgressCount);
        Assert.Equal(1, todo.ResolvedCount);
        Assert.Equal(1, todo.TotalCount);
    }

    /// <summary>已知雜訊記憶（§5.1 D-1 #3）：標記後記憶留存，之後同主機同簽章可查得到</summary>
    [Fact]
    public void 標已知雜訊_寫入記憶()
    {
        var day = Today.AddDays(-8);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var key = IssueSignatureKey.For(a);

        Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = key,
            Status = IssueHandlingStatuses.KnownNoise,
            Note = "已知硬碟廠商韌體訊息"
        });

        var mark = _noiseMarks.Get(_host.HostName, key);
        Assert.NotNull(mark);
        Assert.Equal("已知硬碟廠商韌體訊息", mark!.Note);
    }

    /// <summary>調回未處理且選擇刪除記憶時，記憶應被清除；不選擇刪除時記憶留存</summary>
    [Fact]
    public void 調回未處理_選擇刪除記憶時記憶消失()
    {
        var day = Today.AddDays(-9);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var key = IssueSignatureKey.For(a);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest { IssueKey = key, Status = IssueHandlingStatuses.KnownNoise });
        Assert.NotNull(_noiseMarks.Get(_host.HostName, key));

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = key,
            Status = IssueHandlingStatuses.Open,
            ForgetNoise = true
        });

        Assert.Null(_noiseMarks.Get(_host.HostName, key));
    }

    [Fact]
    public void 調回未處理_不刪記憶時記憶留存()
    {
        var day = Today.AddDays(-10);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var key = IssueSignatureKey.For(a);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest { IssueKey = key, Status = IssueHandlingStatuses.KnownNoise });
        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = key,
            Status = IssueHandlingStatuses.Open,
            ForgetNoise = false
        });

        Assert.NotNull(_noiseMarks.Get(_host.HostName, key));
    }

    /// <summary>open 是合法的可持久化狀態（不是結案類，但通過驗證）</summary>
    [Fact]
    public void 設定Open狀態_通過驗證且不計入已結案()
    {
        var day = Today.AddDays(-11);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var result = Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Open
        });

        Assert.Equal(0, result.ClosedIssues);
        Assert.Equal(HandlingStatuses.Open, result.DayStatus);
    }

    [Fact]
    public void 標記不存在的問題_擲驗證例外()
    {
        var day = Today.AddDays(-6);
        _repository.AddRecord(_host.HostName, day, Issue("disk", 153));

        Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
            {
                IssueKey = "System|nonexistent|999|1",
                Status = IssueHandlingStatuses.Resolved
            }));
    }

    // ── 批次套用（風險日詳情：勾選多個問題後在右側處理狀態區塊一次套用）─────────

    [Fact]
    public void 批次套用_多個問題同時標記成功()
    {
        var day = Today.AddDays(-12);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        var c = Issue("net", 2000);
        _repository.AddRecord(_host.HostName, day, a, b, c);

        var result = Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.Resolved
        });

        Assert.Equal(2, result.UpdatedIssueKeys.Count);
        Assert.Equal(2, result.ClosedIssues);
        Assert.Equal(3, result.TotalIssues);
        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
    }

    /// <summary>
    /// docs/archive/HISTORY.md #6/D4：批次勾選 N 個問題套用，歷程要留下 N 筆逐一紀錄
    /// （不是一筆彙總）——每一筆都查得到「對哪個問題、標成什麼」，含反正規化的 IssueLabel。
    /// </summary>
    [Fact]
    public void 批次套用_歷程逐問題各留一筆記錄()
    {
        var day = Today.AddDays(-12);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);

        Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.Resolved
        });

        var logs = _handlings.GetLogs(_host.HostName, day);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(HandlingActions.IssueStatus, l.Action));
        Assert.All(logs, l => Assert.Equal(HandlingStatuses.Resolved, l.Status));
        Assert.Contains(logs, l => l.IssueKey == IssueSignatureKey.For(a) && l.IssueLabel == "disk 153");
        Assert.Contains(logs, l => l.IssueKey == IssueSignatureKey.For(b) && l.IssueLabel == "app 1000");
        // 同一次批次共用同一個時間戳（供前端 timeline 視覺分組），操作者一致
        Assert.All(logs, l => Assert.Equal(logs[0].CreatedAt, l.CreatedAt));
        Assert.All(logs, l => Assert.False(string.IsNullOrEmpty(l.ActorAccount)));
        Assert.All(logs, l => Assert.Equal(logs[0].ActorAccount, l.ActorAccount));
    }

    /// <summary>單筆標記（非批次）同樣要留下問題層級的歷程，含 IssueLabel</summary>
    [Fact]
    public void 單筆標記_歷程記錄問題鍵與顯示文字()
    {
        var day = Today.AddDays(-16);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.InProgress,
            Note = "查修中"
        });

        var log = Assert.Single(_handlings.GetLogs(_host.HostName, day));
        Assert.Equal(HandlingActions.IssueStatus, log.Action);
        Assert.Equal(IssueSignatureKey.For(a), log.IssueKey);
        Assert.Equal("disk 153", log.IssueLabel);
        Assert.Equal(HandlingStatuses.InProgress, log.Status);
        Assert.Equal("查修中", log.Note);
    }

    /// <summary>清除標記（調回未處理）也要留痕，不是靜默消失——歷程要看得出「誰把它清掉的」</summary>
    [Fact]
    public void 清除問題標記_歷程記錄清除動作()
    {
        var day = Today.AddDays(-17);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        { IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Resolved });
        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        { IssueKey = IssueSignatureKey.For(a), Status = "" });

        var logs = _handlings.GetLogs(_host.HostName, day);
        Assert.Equal(2, logs.Count);
        Assert.Equal(HandlingActions.IssueStatusCleared, logs[1].Action);
        Assert.Equal(IssueSignatureKey.For(a), logs[1].IssueKey);
    }

    [Fact]
    public void 批次套用_處理中狀態存下預計完成日()
    {
        var day = Today.AddDays(-13);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);
        var dueDate = Today.AddDays(7);

        Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.InProgress,
            DueDate = dueDate
        });

        var stored = _issueHandlings.GetForDay(_host.HostName, day);
        Assert.All(stored, h => Assert.Equal(dueDate.Date, h.DueDate));
    }

    [Fact]
    public void 批次套用_非處理中狀態不存預計完成日()
    {
        var day = Today.AddDays(-14);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a) },
            Status = IssueHandlingStatuses.Resolved,
            DueDate = Today.AddDays(7)   // 使用者填了但狀態不是處理中——不該被存下
        });

        var stored = _issueHandlings.GetForDay(_host.HostName, day).Single();
        Assert.Null(stored.DueDate);
    }

    [Fact]
    public void 批次套用_略過已不存在的問題鍵()
    {
        var day = Today.AddDays(-15);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var result = Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), "System|gone|999|1" },
            Status = IssueHandlingStatuses.Resolved
        });

        Assert.Single(result.UpdatedIssueKeys);
        Assert.Equal(IssueSignatureKey.For(a), result.UpdatedIssueKeys[0]);
    }

    [Fact]
    public void 批次套用_全部問題鍵都不存在時擲驗證例外()
    {
        var day = Today.AddDays(-16);
        _repository.AddRecord(_host.HostName, day, Issue("disk", 153));

        Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
            {
                IssueKeys = new List<string> { "System|gone|999|1" },
                Status = IssueHandlingStatuses.Resolved
            }));
    }

    [Fact]
    public void 批次套用_調回未處理且選擇刪除記憶時記憶消失()
    {
        var day = Today.AddDays(-17);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);
        var service = Create(Capability.Handle);

        service.SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.KnownNoise
        });
        Assert.NotNull(_noiseMarks.Get(_host.HostName, IssueSignatureKey.For(a)));
        Assert.NotNull(_noiseMarks.Get(_host.HostName, IssueSignatureKey.For(b)));

        service.SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.Open,
            ForgetNoise = true
        });

        Assert.Null(_noiseMarks.Get(_host.HostName, IssueSignatureKey.For(a)));
        Assert.Null(_noiseMarks.Get(_host.HostName, IssueSignatureKey.For(b)));
    }

    [Fact]
    public void 批次套用_處理中預計完成日早於今天_擲驗證例外()
    {
        var day = Today.AddDays(-18);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
            {
                IssueKeys = new List<string> { IssueSignatureKey.For(a) },
                Status = IssueHandlingStatuses.InProgress,
                DueDate = Today.AddDays(-1)
            }));
    }

    /// <summary>逾期兩層都看：問題層級「處理中」的預計完成日過期，即使日層級沒有 DueDate 也算逾期</summary>
    [Fact]
    public void 待辦統計_問題層級處理中逾期_計入逾期()
    {
        var day = Today.AddDays(-19);
        var a = Issue("disk", 153);
        var record = _repository.AddRecord(_host.HostName, day, a);

        // 寫入驗證擋下過去的預計完成日，這裡直接落一筆已逾期的資料模擬「當初填了、後來過期」
        _issueHandlings.Save(new IssueHandling
        {
            HostName = _host.HostName,
            Date = day,
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.InProgress,
            DueDate = Today.AddDays(-2),
            UpdatedAt = DateTime.Now
        });

        var todo = Create(Capability.Handle).GetTodo(new[] { record });

        Assert.Equal(1, todo.InProgressCount);   // in_progress 標記讓日狀態推導為處理中
        Assert.Equal(1, todo.OverdueCount);
    }

    /// <summary>問題層級「處理中」的預計完成日還沒到，不算逾期</summary>
    [Fact]
    public void 待辦統計_問題層級處理中未到期_不計入逾期()
    {
        var day = Today.AddDays(-20);
        var a = Issue("disk", 153);
        var record = _repository.AddRecord(_host.HostName, day, a);

        Create(Capability.Handle).SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a) },
            Status = IssueHandlingStatuses.InProgress,
            DueDate = Today.AddDays(3)
        });

        var todo = Create(Capability.Handle).GetTodo(new[] { record });

        Assert.Equal(1, todo.InProgressCount);
        Assert.Equal(0, todo.OverdueCount);
    }

    // ── 觀察中（docs/archive/FEEDBACK-8-PLAN.md #4）──────────────────────────────────────

    /// <summary>標為觀察中時必須指定觀察至日期，否則拒絕——沒有終點的「觀察」沒有意義</summary>
    [Fact]
    public void 標記觀察中_未指定觀察至日期時拒絕()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        var ex = Assert.Throws<DomainException>(() => service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Observing
        }));
        Assert.Contains("觀察至", ex.Message);
    }

    /// <summary>觀察至日期不可超過 90 天——伺服器端防禦性驗證，不只信前端的天數上限</summary>
    [Fact]
    public void 標記觀察中_觀察至日期超過90天時拒絕()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        var ex = Assert.Throws<DomainException>(() => service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Observing,
            DueDate = Today.AddDays(91)
        }));
        Assert.Contains("90 天", ex.Message);
    }

    /// <summary>剛好 90 天是合法邊界，不該被拒絕</summary>
    [Fact]
    public void 標記觀察中_觀察至日期恰為90天時允許()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        var result = service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Observing,
            DueDate = Today.AddDays(90)
        });

        Assert.Equal(IssueHandlingStatuses.Observing, result.Status);
    }

    /// <summary>
    /// 完整劇本（docs/archive/FEEDBACK-8-PLAN.md #4）：標觀察 → 儀表板不吵（不算 open，也不算逾期）
    /// → 模擬到期（直接落一筆已過期的觀察紀錄，同其餘逾期測試的既有手法）→ 逾期現身。
    /// </summary>
    [Fact]
    public void 標記觀察中_觀察期間不進待辦_到期後以逾期現身()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        var record = _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Observing,
            DueDate = Today.AddDays(7)
        });

        var duringObservation = service.GetTodo(new[] { record });
        Assert.Equal(0, duringObservation.OpenCount);         // 不再是「未處理」
        Assert.Equal(1, duringObservation.InProgressCount);   // 視同處理中
        Assert.Equal(0, duringObservation.OverdueCount);      // 觀察期間不逾期，不吵

        // 模擬到期：寫入驗證擋下過去日期，這裡直接落一筆已過期的觀察紀錄
        _issueHandlings.Save(new IssueHandling
        {
            HostName = _host.HostName,
            Date = day,
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Observing,
            DueDate = Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        var afterExpiry = service.GetTodo(new[] { record });
        Assert.Equal(0, afterExpiry.OpenCount);
        Assert.Equal(1, afterExpiry.InProgressCount);   // 仍是處理中，不倒退回未處理
        Assert.Equal(1, afterExpiry.OverdueCount);      // 但現在算逾期——這就是「問題仍在發生」的提示
    }

    // ── 問題案件（IssueCase，docs/archive/FEEDBACK-4-PLAN.md §2）────────────────────────

    /// <summary>指派處理人時，對當日未結案的問題自動建案（Q1）；低風險以下的問題不建案</summary>
    [Fact]
    public void 指派_對未結案問題自動建案_低風險預設不處理的問題不建案()
    {
        var day = Today.AddDays(-30);
        var a = Issue("disk", 153);                                  // High，建案
        var low = Issue("misc", 1, IssueSeverity.Low);                // Low，不在未處理計算等級，不建案
        _repository.AddRecord(_host.HostName, day, a, low);

        var result = Create(Capability.Assign, Capability.Handle).Assign(_host.HostId, day, _other.UserId);

        Assert.Equal(1, result.CasesCreated);
        Assert.Empty(result.CasesSkippedHandlerNames);
        var openCase = _cases.GetOpen(_host.HostName, IssueSignatureKey.For(a));
        Assert.NotNull(openCase);
        Assert.Equal(_other.UserId, openCase!.HandlerId);
        Assert.Null(_cases.GetOpen(_host.HostName, IssueSignatureKey.For(low)));
    }

    /// <summary>已結案的問題不建案——那已經有結論了，不是「要有人處理的事」</summary>
    [Fact]
    public void 指派_已結案的問題不建案()
    {
        var day = Today.AddDays(-31);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = _host.HostName, Date = day, IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

        var result = Create(Capability.Assign, Capability.Handle).Assign(_host.HostId, day, _other.UserId);

        Assert.Equal(0, result.CasesCreated);
        Assert.Null(_cases.GetOpen(_host.HostName, IssueSignatureKey.For(a)));
    }

    /// <summary>已有他人進行中案件的問題：指派不搶走，回報既有處理人姓名供前端提示（Q2）</summary>
    [Fact]
    public void 指派_已有進行中案件時保留原處理人並回報姓名()
    {
        var day = Today.AddDays(-32);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _owner.UserId);   // 先指派給 OOO，建案

        var second = service.Assign(_host.HostId, day, _other.UserId);   // 改指派給 XXX

        Assert.Equal(0, second.CasesCreated);
        // 顯示名稱(帳號)（docs/archive/FEEDBACK-10-PLAN.md §6）：略過清單會直接顯示給使用者看，
        // 走與全站一致的人名格式
        Assert.Equal(new[] { "OOO(DOMAIN\\ooo)" }, second.CasesSkippedHandlerNames);
        Assert.Equal(_owner.UserId, _cases.GetOpen(_host.HostName, IssueSignatureKey.For(a))!.HandlerId);
    }

    /// <summary>取消指派（handlerId=null）不建案——那不是「開始有人處理」的動作</summary>
    [Fact]
    public void 取消指派_不建案()
    {
        var day = Today.AddDays(-33);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var result = Create(Capability.Assign, Capability.Handle).Assign(_host.HostId, day, null);

        Assert.Equal(0, result.CasesCreated);
    }

    /// <summary>有進行中案件的問題再次標記狀態時，DTO 帶回案件同步涵蓋的天數；面板 OpenCaseCount 反映進行中案件數</summary>
    [Fact]
    public void 標記狀態_有進行中案件時DTO帶回同步天數與OpenCaseCount()
    {
        var day1 = Today.AddDays(-40);
        var day2 = Today.AddDays(-35);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day1, a);
        _repository.AddRecord(_host.HostName, day2, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day1, _other.UserId);   // 建案，回溯涵蓋 day1+day2

        var afterAssign = service.Get(_host.HostId, day2);
        Assert.Equal(1, afterAssign.OpenCaseCount);

        var result = service.SetIssueStatus(_host.HostId, day2, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });

        // 結案同步展開到 day1（觸發日 day2 本身不算在 SyncedDayCount 內）
        Assert.Equal(1, result.CaseSyncedDayCount);
        Assert.Equal(IssueHandlingStatuses.Resolved, _issueHandlings.GetForDay(_host.HostName, day1).Single().Status);
        Assert.Null(_cases.GetOpen(_host.HostName, IssueSignatureKey.For(a)));   // 案件已結案
    }

    /// <summary>沒有案件時，標記狀態的 DTO CaseSyncedDayCount 為 0（既有行為不受影響）</summary>
    [Fact]
    public void 標記狀態_無案件時CaseSyncedDayCount為零()
    {
        var day = Today.AddDays(-41);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var result = Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });

        Assert.Equal(0, result.CaseSyncedDayCount);
    }

    // ── 統一標記（docs/archive/FEEDBACK-11-PLAN.md §6）─────────────────────────────────────
    //
    // 註：期間（from/to）的過濾由 IRecordRepository.Query 負責，而 FakeRecordRepository
    // 刻意忽略 filter（見它的類別註解），因此這組測試釘的是「哪些主機／哪些天會被標」
    // 的規則本身；期間過濾另由 RecordRepository 的查詢測試涵蓋。

    /// <summary>無人接手的主機：期間內每一天都標成結論，逐日各留一列歷程</summary>
    [Fact]
    public void 統一標記_無案件的主機_逐日標記並寫歷程()
    {
        var a = Issue("disk", 153);
        var day1 = Today.AddDays(-3);
        var day2 = Today.AddDays(-2);
        _repository.AddRecord(_host.HostName, day1, a);
        _repository.AddRecord(_host.HostName, day2, a);

        var result = Create(Capability.Assign, Capability.Handle).BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.Resolved, Note = "週期性維護"
        });

        Assert.Equal(1, result.UpdatedHostCount);
        Assert.Equal(2, result.UpdatedDayCount);
        Assert.Empty(result.Skipped);

        foreach (var day in new[] { day1, day2 })
        {
            var handling = Assert.Single(_issueHandlings.GetForDay(_host.HostName, day));
            Assert.Equal(IssueHandlingStatuses.Resolved, handling.Status);
            Assert.Equal("週期性維護", handling.Note);
        }

        // 逐問題逐日各一列歷程（同批次套用的規則，不做彙總）
        foreach (var day in new[] { day1, day2 })
        {
            Assert.Single(_handlings.GetLogs(_host.HostName, day), l => l.Action == HandlingActions.IssueStatus);
        }
    }

    /// <summary>
    /// 已有進行中案件的主機整台略過（定案 6-1）——不論處理人是誰。要動別人的案件，
    /// 正規路徑是改派或由處理人自己回覆。
    /// </summary>
    [Fact]
    public void 統一標記_已有案件的主機_略過並回報處理人()
    {
        var a = Issue("disk", 153);
        var day = Today.AddDays(-4);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _owner.UserId);   // 建案，處理人＝OOO

        var ex = Assert.Throws<DomainException>(() => service.BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.Resolved, Note = "理由"
        }));

        // 全部主機都被略過時擋下整批（沒有可標的東西，不該回一個「成功 0 台」）
        Assert.Contains("已有人處理", ex.Message);

        var preview = service.PreviewBulkClose("disk", 153, null, null);
        var row = Assert.Single(preview.Hosts);
        Assert.Contains("OOO", row.SkipReason);
        Assert.Equal(0, row.DayCount);
    }

    /// <summary>
    /// 無案件但有人標了處理中／觀察中：一併覆蓋（定案 6-3，admin 的統一標記為主），
    /// 且預覽要先講明會覆蓋幾天——按下去之前看得到。
    /// </summary>
    [Fact]
    public void 統一標記_無案件的處理中標記_被覆蓋且預覽先告知()
    {
        var a = Issue("disk", 153);
        var day = Today.AddDays(-5);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.InProgress
        });

        var preview = service.PreviewBulkClose("disk", 153, null, null);
        Assert.Equal(1, Assert.Single(preview.Hosts).OverwriteDayCount);

        service.BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.WontFix, Note = "已知行為"
        });

        Assert.Equal(IssueHandlingStatuses.WontFix,
            Assert.Single(_issueHandlings.GetForDay(_host.HostName, day)).Status);
    }

    /// <summary>已有結論的日子不重寫——重寫只會多一列沒意義的歷程，還會蓋掉當初的判斷</summary>
    [Fact]
    public void 統一標記_已有結論的日子_不動也不重寫歷程()
    {
        var a = Issue("disk", 153);
        var closedDay = Today.AddDays(-7);
        var openDay = Today.AddDays(-6);
        _repository.AddRecord(_host.HostName, closedDay, a);
        _repository.AddRecord(_host.HostName, openDay, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.SetIssueStatus(_host.HostId, closedDay, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.FalsePositive,
            Note = "先前判定"
        });
        var closedDayLogsBefore = _handlings.GetLogs(_host.HostName, closedDay).Count;

        var result = service.BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.Resolved, Note = "統一標記"
        });

        Assert.Equal(1, result.UpdatedDayCount);   // 只有 openDay
        Assert.Equal(IssueHandlingStatuses.FalsePositive,
            Assert.Single(_issueHandlings.GetForDay(_host.HostName, closedDay)).Status);
        Assert.Equal(closedDayLogsBefore, _handlings.GetLogs(_host.HostName, closedDay).Count);
    }

    /// <summary>標「已知雜訊」要寫入雜訊記憶（與詳情頁一致）——這是四態中唯一有後續效果的</summary>
    [Fact]
    public void 統一標記_已知雜訊_寫入雜訊記憶()
    {
        var a = Issue("disk", 153);
        var day = Today.AddDays(-8);
        _repository.AddRecord(_host.HostName, day, a);

        Create(Capability.Assign, Capability.Handle).BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.KnownNoise, Note = "已知雜訊"
        });

        Assert.NotNull(_noiseMarks.Get(_host.HostName, IssueSignatureKey.For(a)));
    }

    [Theory]
    [InlineData(IssueHandlingStatuses.InProgress)]
    [InlineData(IssueHandlingStatuses.Observing)]
    [InlineData(IssueHandlingStatuses.Open)]
    public void 統一標記_非結案狀態_擋下(string status)
    {
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, Today.AddDays(-9), a);

        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Assign, Capability.Handle).BulkCloseIssue(new BulkCloseIssueRequest
            {
                Source = "disk", EventId = 153, Status = status, Note = "理由"
            }));

        Assert.Contains("四種結論", ex.Message);
    }

    /// <summary>原因必填：這是代全體下結論的操作，理由是紀錄的一部分</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 統一標記_原因未填_擋下(string note)
    {
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, Today.AddDays(-10), a);

        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Assign, Capability.Handle).BulkCloseIssue(new BulkCloseIssueRequest
            {
                Source = "disk", EventId = 153, Status = IssueHandlingStatuses.Resolved, Note = note
            }));

        Assert.Contains("原因", ex.Message);
    }

    [Fact]
    public void 統一標記_寫入專屬稽核動作()
    {
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, Today.AddDays(-11), a);

        Create(Capability.Assign, Capability.Handle).BulkCloseIssue(new BulkCloseIssueRequest
        {
            Source = "disk", EventId = 153, Status = IssueHandlingStatuses.Resolved, Note = "理由"
        });

        var entry = Assert.Single(_audit.Entries, e => e.Action == AuditActions.IssueBulkClose);
        Assert.Contains("統一標記", entry.Summary);
    }

    // ── 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）────────────────────────────

    [Fact]
    public void 工作頁_列出名下進行中案件與被指派的未結案風險日()
    {
        var day = Today.AddDays(-50);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);   // 建案＋日層級指派給 XXX

        var workload = service.GetHandlerWorkload(_other.UserId, includeResolvedDays: false);

        Assert.Equal("XXX", workload.DisplayName);
        Assert.True(workload.Active);
        Assert.Equal(1, workload.OpenCaseCount);
        var caseItem = Assert.Single(workload.Cases);
        Assert.Equal(_host.HostName, caseItem.HostName);
        Assert.Equal("disk 153", caseItem.IssueLabel);

        var dayItem = Assert.Single(workload.Days);
        Assert.Equal(day.ToString("yyyy-MM-dd"), dayItem.Date);
        Assert.Equal(HandlingStatuses.InProgress, dayItem.DerivedStatus);
    }

    /// <summary>
    /// 問題簽章鍵的反解（§7）。與 <c>For</c> 對稱，格式壞掉時回 null 而不是拋例外——
    /// 舊資料或人為改壞的 blob 不該讓整個工作頁 500。
    /// </summary>
    [Theory]
    [InlineData("Application|disk|153|1", "disk", 153)]
    [InlineData("System|Service Control Manager|7031|1", "Service Control Manager", 7031)]
    public void 問題簽章鍵_可反解出來源與EventId(string key, string expectedSource, int expectedEventId)
    {
        var parsed = IssueSignatureKey.TryParseSignature(key);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSource, parsed!.Value.Source);
        Assert.Equal(expectedEventId, parsed.Value.EventId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Application|disk|153")]
    [InlineData("Application|disk|not-a-number|1")]
    public void 問題簽章鍵_格式不符回null不拋例外(string? key)
    {
        Assert.Null(IssueSignatureKey.TryParseSignature(key));
    }

    /// <summary>
    /// 工作頁「依問題」視角（docs/archive/FEEDBACK-11-PLAN.md §7）在前端分組，靠的是這兩個欄位：
    /// 沒有它們就分不了組，「回覆處理狀態」也送不出參數（端點吃 source＋eventId）。
    /// </summary>
    [Fact]
    public void 工作頁_案件帶出問題簽章的來源與EventId()
    {
        var day = Today.AddDays(-51);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);

        var caseItem = Assert.Single(service.GetHandlerWorkload(_other.UserId, includeResolvedDays: false).Cases);

        Assert.Equal("disk", caseItem.Source);
        Assert.Equal(153, caseItem.EventId);
    }

    /// <summary>已結案的風險日預設不顯示；includeResolvedDays=true 時才納入</summary>
    [Fact]
    public void 工作頁_已結案風險日預設不顯示_切換後才出現()
    {
        var day = Today.AddDays(-5);   // 30 天內，切換後應該看得到
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);
        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });

        var withoutResolved = service.GetHandlerWorkload(_other.UserId, includeResolvedDays: false);
        Assert.Empty(withoutResolved.Days);
        Assert.Empty(withoutResolved.Cases);   // 案件已結案，不再是「進行中」

        var withResolved = service.GetHandlerWorkload(_other.UserId, includeResolvedDays: true);
        Assert.Single(withResolved.Days);
    }

    /// <summary>資料以檢視者的可見範圍過濾——被看者的交辦項目在檢視者看不到的主機上時不列出</summary>
    [Fact]
    public void 工作頁_資料以檢視者可見範圍過濾()
    {
        var day = Today.AddDays(-52);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        Create(Capability.Assign, Capability.Handle).Assign(_host.HostId, day, _other.UserId);

        // 檢視者只看得到別的主機（AlwaysVisibleService 全可見，這裡改用受限的可見範圍服務）
        var restrictedVisibility = new RestrictedVisibleService();
        var restrictedService = new HandlingServiceFacade(
            store: _handlings,
            issueStore: _issueHandlings,
            cases: _cases,
            caseCoordinator: _caseCoordinator,
            noiseMarks: _noiseMarks,
            repository: _repository,
            hosts: _hosts,
            users: _users,
            visibility: restrictedVisibility,
            currentUser: FakeCurrentUser.ForUser(_other.UserId, Capability.Assign, Capability.Handle),
            audit: _audit,
            settings: _settings);

        var workload = restrictedService.GetHandlerWorkload(_other.UserId, includeResolvedDays: false);

        Assert.Empty(workload.Cases);
        Assert.Empty(workload.Days);
    }

    [Fact]
    public void 工作頁_查無使用者時拋NotFound()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).GetHandlerWorkload(9999, includeResolvedDays: false));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }
    // ── 回饋第十輪 §8／§9／§11（docs/archive/FEEDBACK-10-PLAN.md）─────────────────────

    /// <summary>
    /// §8：別人的案件正在處理的問題不接受直接改狀態——要換人得走改派，
    /// 否則兩個人先後標記，後標的會靜默蓋掉先標的判斷。
    /// </summary>
    [Fact]
    public void 標記問題_他人案件處理中時拒絕()
    {
        var day = Today.AddDays(-40);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        // _owner 先取得案件（Create 的目前使用者是 _other）
        Create(Capability.Assign, Capability.Handle).Assign(_host.HostId, day, _owner.UserId);

        var ex = Assert.Throws<DomainException>(() =>
            Create(Capability.Handle).SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
            {
                IssueKey = IssueSignatureKey.For(a),
                Status = IssueHandlingStatuses.Resolved
            }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("案件處理中", ex.Message);
    }

    /// <summary>§8：自己的案件照常可以標記（限制的是「別人的」，不是「有案件的」）</summary>
    [Fact]
    public void 標記問題_自己的案件可以標()
    {
        var day = Today.AddDays(-41);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);   // 目前使用者就是 _other

        var result = service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Resolved
        });

        Assert.Equal(IssueHandlingStatuses.Resolved, result.Status);
    }

    /// <summary>
    /// §9：帶 reassign 時改派他人的進行中案件——案件**不結案重開**（同一個 CaseId 換人），
    /// 這樣「這個問題這一輪處理」的歷程才是連續的。
    /// </summary>
    [Fact]
    public void 指派_帶改派旗標時換掉他人案件的處理人()
    {
        var day = Today.AddDays(-42);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _owner.UserId);
        var caseIdBefore = _cases.GetOpen(_host.HostName, IssueSignatureKey.For(a))!.CaseId;

        var result = service.Assign(_host.HostId, day, _other.UserId, reassign: true);

        Assert.Equal(1, result.CasesReassigned);
        Assert.Empty(result.CasesSkippedHandlerNames);

        var after = _cases.GetOpen(_host.HostName, IssueSignatureKey.For(a))!;
        Assert.Equal(_other.UserId, after.HandlerId);
        Assert.Equal(caseIdBefore, after.CaseId);   // 同一個案件，不是重開的
        Assert.Null(after.ClosedAt);
    }

    /// <summary>§9：改派留下 case_reassign 歷程——事後查得到「誰把誰換掉」</summary>
    [Fact]
    public void 改派_留下改派歷程()
    {
        var day = Today.AddDays(-43);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _owner.UserId);
        service.Assign(_host.HostId, day, _other.UserId, reassign: true);

        Assert.Contains(_handlings.GetLogs(_host.HostName, day), l => l.Action == HandlingActions.CaseReassign);
    }

    /// <summary>§11：跨主機一次回覆——自己名下的全部進行中案件同步更新</summary>
    [Fact]
    public void 跨主機回覆_更新自己名下的全部案件()
    {
        var day = Today.AddDays(-44);
        var a = Issue("disk", 153);
        var second = _hosts.Upsert(new WebHost { HostName = "SRV-B" });
        _repository.AddRecord(_host.HostName, day, a);
        _repository.AddRecord(second.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);     // 目前使用者
        service.Assign(second.HostId, day, _other.UserId);

        var result = service.BulkSetIssueStatusByHandler(new BulkIssueStatusRequest
        {
            Source = "disk", EventId = 153,
            Status = IssueHandlingStatuses.Resolved,
            Note = "已更換硬碟"
        });

        Assert.Equal(2, result.UpdatedCaseCount);
        Assert.Equal(new[] { "SRV-A", "SRV-B" }, result.HostNames);
        // 結案類會讓案件本身結案（沿用 SyncStatus 既有語意）
        Assert.Null(_cases.GetOpen(_host.HostName, IssueSignatureKey.For(a)));
        Assert.Null(_cases.GetOpen(second.HostName, IssueSignatureKey.For(a)));
    }

    /// <summary>§11：別人名下的案件不受影響——這是「回覆自己手上的工作」，不是代人回覆</summary>
    [Fact]
    public void 跨主機回覆_不動別人名下的案件()
    {
        var day = Today.AddDays(-45);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var service = Create(Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _owner.UserId);   // 指派給別人

        var ex = Assert.Throws<DomainException>(() =>
            service.BulkSetIssueStatusByHandler(new BulkIssueStatusRequest
            {
                Source = "disk", EventId = 153,
                Status = IssueHandlingStatuses.Resolved
            }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(_owner.UserId, _cases.GetOpen(_host.HostName, IssueSignatureKey.For(a))!.HandlerId);
    }

    // ── 無法處理（escalated，回饋十八輪批次G）────────────────────────────────

    /// <summary>問題層級標無法處理必填原因——admin 要據此決定結案或改派，後端擋不只信前端</summary>
    [Fact]
    public void 標記無法處理_未填原因時拒絕()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);

        var ex = Assert.Throws<DomainException>(() => Create(Capability.Handle).SetIssueStatus(_host.HostId, day,
            new SetIssueStatusRequest
            {
                IssueKey = IssueSignatureKey.For(a),
                Status = IssueHandlingStatuses.Escalated
            }));

        Assert.Contains("原因", ex.Message);
    }

    /// <summary>無法處理是非結案：問題視同處理中（有人在管），不進未處理、也不因此結案</summary>
    [Fact]
    public void 標記無法處理_視同處理中不結案()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        var record = _repository.AddRecord(_host.HostName, day, a);
        var service = Create(Capability.Handle);

        var result = service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Escalated,
            Note = "換硬體超出我的權限"
        });

        Assert.Equal(IssueHandlingStatuses.Escalated, result.Status);
        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);

        var todo = service.GetTodo(new[] { record });
        Assert.Equal(0, todo.OpenCount);
        Assert.Equal(1, todo.InProgressCount);
    }

    /// <summary>日層級也擋：Update 標無法處理沒填原因時拒絕（與問題層級同一條規則）</summary>
    [Fact]
    public void 日層級標記無法處理_未填原因時拒絕()
    {
        var ex = Assert.Throws<DomainException>(() => Create(Capability.Handle).Update(_host.HostId, Today,
            new UpdateHandlingRequest { Status = HandlingStatuses.Escalated }));

        Assert.Contains("原因", ex.Message);
    }

    [Fact]
    public void 日層級標記無法處理_有原因時成功且列入未結案()
    {
        var result = Create(Capability.Handle).Update(_host.HostId, Today,
            new UpdateHandlingRequest { Status = HandlingStatuses.Escalated, Note = "需要外部廠商" });

        Assert.Equal(HandlingStatuses.Escalated, result.Status);
        Assert.Contains(HandlingStatuses.Escalated, HandlingStatuses.Unresolved);
    }

    // ── 上報通知防重寄（回饋十八輪體檢輪修正：previousStatus 已是 escalated 時不重寄）────

    /// <summary>單筆轉入無法處理寄一次；已是無法處理時改備註重存不再寄第二次。</summary>
    [Fact]
    public void SetIssueStatus_轉入無法處理通知一次_已上報再存不重寄()
    {
        var day = Today.AddDays(-1);
        var a = Issue("disk", 153);
        _repository.AddRecord(_host.HostName, day, a);
        var mail = CreateMail();
        var service = CreateWithMail(mail, Capability.Handle);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Escalated,
            Note = "換硬體超出我的權限"
        });
        Assert.Single(_mailSender.Sent);

        service.SetIssueStatus(_host.HostId, day, new SetIssueStatusRequest
        {
            IssueKey = IssueSignatureKey.For(a),
            Status = IssueHandlingStatuses.Escalated,
            Note = "追加說明，廠商已聯絡"
        });
        Assert.Single(_mailSender.Sent);
    }

    /// <summary>批次轉入無法處理彙整寄一封；整批已上報過的問題再送同一批不重寄。</summary>
    [Fact]
    public void SetIssueStatusBatch_批次轉入無法處理通知一次_已上報再送不重寄()
    {
        var day = Today.AddDays(-2);
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        _repository.AddRecord(_host.HostName, day, a, b);
        var mail = CreateMail();
        var service = CreateWithMail(mail, Capability.Handle);

        service.SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.Escalated,
            Note = "需要外部廠商"
        });
        Assert.Single(_mailSender.Sent);

        service.SetIssueStatusBatch(_host.HostId, day, new BatchSetIssueStatusRequest
        {
            IssueKeys = new List<string> { IssueSignatureKey.For(a), IssueSignatureKey.For(b) },
            Status = IssueHandlingStatuses.Escalated,
            Note = "追加說明"
        });
        Assert.Single(_mailSender.Sent);
    }

    /// <summary>跨主機一次回覆轉入無法處理寄一封；同一批案件已是無法處理時再回覆不重寄。</summary>
    [Fact]
    public void BulkSetIssueStatusByHandler_轉入無法處理通知一次_已上報再回覆不重寄()
    {
        var day = Today.AddDays(-3);
        var a = Issue("disk", 153);
        var second = _hosts.Upsert(new WebHost { HostName = "SRV-B" });
        _repository.AddRecord(_host.HostName, day, a);
        _repository.AddRecord(second.HostName, day, a);
        var mail = CreateMail();
        var service = CreateWithMail(mail, Capability.Assign, Capability.Handle);
        service.Assign(_host.HostId, day, _other.UserId);
        service.Assign(second.HostId, day, _other.UserId);

        service.BulkSetIssueStatusByHandler(new BulkIssueStatusRequest
        {
            Source = "disk", EventId = 153,
            Status = IssueHandlingStatuses.Escalated,
            Note = "需要外部廠商"
        });
        Assert.Single(_mailSender.Sent);

        service.BulkSetIssueStatusByHandler(new BulkIssueStatusRequest
        {
            Source = "disk", EventId = 153,
            Status = IssueHandlingStatuses.Escalated,
            Note = "追加說明"
        });
        Assert.Single(_mailSender.Sent);
    }
}
