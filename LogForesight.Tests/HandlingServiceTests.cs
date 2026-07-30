using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態與指派（docs/WEB-SPEC.md §9.3）。
///
/// 這組測試釘住的核心語意是 2026-07-21 定案的**負責人與處理人分離**：
/// 「A 主機預設管理者是 OOO，但管理者覺得這個問題緊急先給 XXX 處理，
/// 這時管理者不變，但問題的處理者會被指派到 XXX 身上」。
/// </summary>
public class HandlingServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeNoiseMarkStore _noiseMarks = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeRecordRepository _repository;
    private readonly IssueCaseCoordinator _caseCoordinator;

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

    private HandlingService Create(params Capability[] capabilities)
    {
        var currentUser = FakeCurrentUser.ForUser(_other.UserId, capabilities);
        return new HandlingService(
            _handlings, _issueHandlings, _cases, _caseCoordinator, _noiseMarks, _repository, _hosts, _users,
            new AlwaysVisibleService(_hosts), currentUser, _audit, _settings);
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
    /// 待辦母體＝高＋中風險日（docs/HISTORY.md S3）：低風險日即使從未處理過，
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
    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = System.Diagnostics.EventLogEntryType.Error,
        Severity = severity
    };

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
    /// docs/HISTORY.md #12：日層級 fallback 為 wont_fix（使用者把整天標成這個狀態、
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
    /// docs/HISTORY.md #6/D4：批次勾選 N 個問題套用，歷程要留下 N 筆逐一紀錄
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

    // ── 問題案件（IssueCase，docs/FEEDBACK-4-PLAN.md §2）────────────────────────

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
        Assert.Equal(new[] { "OOO" }, second.CasesSkippedHandlerNames);
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

    // ── 處理人員工作頁（docs/FEEDBACK-4-PLAN.md §6）────────────────────────────

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
        var restrictedService = new HandlingService(
            _handlings, _issueHandlings, _cases, _caseCoordinator, _noiseMarks, _repository, _hosts, _users,
            restrictedVisibility, FakeCurrentUser.ForUser(_other.UserId, Capability.Assign, Capability.Handle),
            _audit, _settings);

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
}

/// <summary>可見範圍固定為空——供工作頁的「檢視者可見範圍過濾」測試使用</summary>
internal class RestrictedVisibleService : IVisibilityService
{
    public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long>();
    public List<WebHost> GetVisibleHosts() => new();
    public void EnsureVisible(long hostId) => throw DomainException.NotFound("找不到這台主機。");
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────
// FakeHostStore／FakeUserStore／AlwaysVisibleService／RecordingAuditService 已搬到
// TestDoubles\ 底下（跨多個測試檔共用）；以下僅保留本檔專用的替身。

/// <summary>
/// 同時實作 <see cref="IAnalysisRecordQuery"/>（顯式介面實作）供 IssueCaseCoordinator 之類
/// 只用得到 Core 層查詢介面的呼叫端測試——**與 <see cref="IRecordRepository.Query"/> 刻意分開**：
/// 後者長期以來忽略 filter.Hosts（多個既有測試檔可能仰賴這個寬鬆行為），改動它有波及既有測試的風險；
/// 前者是全新介面、沒有既有行為要保留，直接做正確的主機過濾（HostMatcher），讓 Coordinator 的
/// 回溯關聯查詢在多主機測試情境下不會誤抓別台主機的紀錄。
/// </summary>
internal class FakeRecordRepository : IRecordRepository, IAnalysisRecordQuery
{
    private readonly FakeHostStore _hosts;
    private readonly List<DailyAnalysisRecord> _records = new();

    public FakeRecordRepository(FakeHostStore hosts) => _hosts = hosts;

    public DailyAnalysisRecord AddRecord(string hostName, DateTime date, params LogIssueSignature[] issues) =>
        AddRecord(hostName, date, "高", issues);

    public DailyAnalysisRecord AddRecord(string hostName, DateTime date, string risk, params LogIssueSignature[] issues)
    {
        var record = new DailyAnalysisRecord
        {
            Host = hostName,
            HostId = _hosts.FindByName(hostName)?.HostId ?? 0,
            Date = date,
            RiskLevel = risk,
            TopIssues = issues.ToList()
        };
        _records.Add(record);
        return record;
    }

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter, bool applyDayRiskVisibility = true) => _records.ToList();

    // ── IAnalysisRecordQuery（顯式實作，正確套用 filter.Hosts）─────────────────
    List<DailyAnalysisRecord> IAnalysisRecordQuery.Query(RecordQueryFilter filter)
    {
        if (filter.Hosts == null) return _records.ToList();
        var matcher = new HostMatcher(filter.Hosts);
        return _records.Where(matcher.Matches).ToList();
    }

    DailyAnalysisRecord? IAnalysisRecordQuery.GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date)
    {
        var matcher = new HostMatcher(hosts);
        return _records.FirstOrDefault(r => matcher.Matches(r) && r.Date.Date == date.Date);
    }

    PagedResult<DailyAnalysisRecord> IAnalysisRecordQuery.QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey, bool ascending) =>
        throw new NotImplementedException();

    HashSet<(long HostId, DateTime Date)> IAnalysisRecordQuery.ListHostDates(DateTime from, DateTime to) =>
        throw new NotImplementedException();

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false)
    {
        var ordered = _records
            .OrderByDescending(r => r.RiskLevel == "高" ? 3 : r.RiskLevel == "中" ? 2 : r.RiskLevel == "低" ? 1 : 0)
            .ThenByDescending(r => r.CorrelationAlerts.Count > 0)
            .ThenByDescending(r => r.Date)
            .ToList();

        return new PagedResult<DailyAnalysisRecord>
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = ordered.Count
        };
    }

    public DailyAnalysisRecord? GetOne(long hostId, DateTime date)
    {
        var host = _hosts.Get(hostId);
        return host == null
            ? null
            : _records.FirstOrDefault(r =>
                string.Equals(r.Host, host.HostName, StringComparison.OrdinalIgnoreCase) &&
                r.Date.Date == date.Date);
    }

    public List<HostKey> ResolveHostKeys(long hostId) =>
        HostIdentityResolver.Expand(_hosts.GetAll(), hostId);

    public WebHost? ResolveHost(string hostName) => _hosts.FindByName(hostName);
}

internal class FakeIssueHandlingStore : IIssueHandlingStore
{
    private readonly List<IssueHandling> _items = new();

    public List<IssueHandling> GetForDay(string hostName, DateTime date) =>
        _items.Where(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                          h.Date.Date == date.Date).ToList();

    public List<IssueHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _items.Where(h => names.Contains(h.HostName) &&
                                 h.Date.Date >= from.Date && h.Date.Date <= to.Date).ToList();
    }

    public List<IssueHandling> GetByCase(string caseId) =>
        _items.Where(h => h.CaseId == caseId).ToList();

    public void Save(IssueHandling handling) => SaveMany(new[] { handling });

    public void SaveMany(IEnumerable<IssueHandling> handlings)
    {
        foreach (var handling in handlings)
        {
            if (string.IsNullOrWhiteSpace(handling.Status))
            {
                Clear(handling.HostName, handling.Date, handling.IssueKey);
                continue;
            }

            var existing = _items.FirstOrDefault(h => Same(h, handling.HostName, handling.Date, handling.IssueKey));
            if (existing == null) { _items.Add(handling); continue; }
            existing.Status = handling.Status;
            existing.Note = handling.Note;
            existing.ActorId = handling.ActorId;
            existing.ActorAccount = handling.ActorAccount;
            existing.DueDate = handling.DueDate;
            existing.CaseId = handling.CaseId;
            existing.UpdatedAt = handling.UpdatedAt;
        }
    }

    public void Clear(string hostName, DateTime date, string issueKey) =>
        _items.RemoveAll(h => Same(h, hostName, date, issueKey));

    private static bool Same(IssueHandling h, string hostName, DateTime date, string issueKey) =>
        string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
        h.Date.Date == date.Date && h.IssueKey == issueKey;
}

internal class FakeIssueCaseStore : IIssueCaseStore
{
    private readonly List<IssueCase> _items = new();

    public IssueCase? GetOpen(string hostName, string issueKey) =>
        _items.FirstOrDefault(c =>
            string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
            c.IssueKey == issueKey && c.ClosedAt == null);

    public List<IssueCase> GetOpenForHost(string hostName) =>
        _items.Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) && c.ClosedAt == null).ToList();

    public List<IssueCase> GetMany(IEnumerable<string> hostNames)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _items.Where(c => names.Contains(c.HostName)).ToList();
    }

    public List<IssueCase> GetOpenByHandler(long userId) =>
        _items.Where(c => c.HandlerId == userId && c.ClosedAt == null).ToList();

    public IssueCase? Get(string caseId) => _items.FirstOrDefault(c => c.CaseId == caseId);

    public void Save(IssueCase issueCase)
    {
        var existing = _items.FirstOrDefault(c => c.CaseId == issueCase.CaseId);
        if (existing == null) { _items.Add(issueCase); return; }
        existing.Status = issueCase.Status;
        existing.HandlerId = issueCase.HandlerId;
        existing.Note = issueCase.Note;
        existing.DueDate = issueCase.DueDate;
        existing.FirstLinkedDate = issueCase.FirstLinkedDate;
        existing.LastLinkedDate = issueCase.LastLinkedDate;
        existing.ClosedAt = issueCase.ClosedAt;
        existing.UpdatedAt = issueCase.UpdatedAt;
    }
}

internal class FakeNoiseMarkStore : INoiseMarkStore
{
    private readonly List<NoiseMark> _items = new();

    public List<NoiseMark> GetForHost(string hostName) =>
        _items.Where(m => string.Equals(m.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();

    public NoiseMark? Get(string hostName, string issueKey) =>
        _items.FirstOrDefault(m => Same(m, hostName, issueKey));

    public void Save(NoiseMark mark)
    {
        var existing = _items.FirstOrDefault(m => Same(m, mark.HostName, mark.IssueKey));
        if (existing == null) { _items.Add(mark); return; }
        existing.MarkedByAccount = mark.MarkedByAccount;
        existing.MarkedAt = mark.MarkedAt;
        existing.Note = mark.Note;
    }

    public void Delete(string hostName, string issueKey) =>
        _items.RemoveAll(m => Same(m, hostName, issueKey));

    private static bool Same(NoiseMark m, string hostName, string issueKey) =>
        string.Equals(m.HostName, hostName, StringComparison.OrdinalIgnoreCase) && m.IssueKey == issueKey;
}

internal class FakeSystemSettingsStore : ISystemSettingsStore
{
    private SystemSettings _settings = new();

    public SystemSettings Get() => _settings;

    public SystemSettings Update(Action<SystemSettings> mutation)
    {
        mutation(_settings);
        return _settings;
    }
}

internal class FakeHandlingStore : IRecordHandlingStore
{
    private readonly List<RecordHandling> _handlings = new();
    private readonly List<RecordHandlingLog> _logs = new();
    private long _nextLogId = 1;

    public RecordHandling? Get(string hostName, DateTime date) =>
        _handlings.FirstOrDefault(h =>
            string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
            h.Date.Date == date.Date);

    public List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _handlings.Where(h => names.Contains(h.HostName)).ToList();
    }

    public List<RecordHandling> GetUnresolved() =>
        _handlings.Where(h => HandlingStatuses.Unresolved.Contains(h.Status)).ToList();

    public List<RecordHandling> GetByHandler(long userId) =>
        _handlings.Where(h => h.HandlerId == userId).ToList();

    public void Save(RecordHandling handling)
    {
        var existing = Get(handling.HostName, handling.Date);
        if (existing == null)
        {
            _handlings.Add(handling);
            return;
        }

        existing.Status = handling.Status;
        existing.HandlerId = handling.HandlerId;
        existing.DueDate = handling.DueDate;
        existing.Note = handling.Note;
        existing.UpdatedAt = handling.UpdatedAt;
    }

    public void AppendLog(RecordHandlingLog log)
    {
        log.LogId = _nextLogId++;
        if (log.CreatedAt == default) log.CreatedAt = DateTime.Now;
        _logs.Add(log);
    }

    public List<RecordHandlingLog> GetLogs(string hostName, DateTime date) =>
        _logs.Where(l =>
                string.Equals(l.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                l.Date.Date == date.Date)
            .OrderBy(l => l.LogId)
            .ToList();
}
