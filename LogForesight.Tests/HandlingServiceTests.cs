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
    private readonly FakeAuditService _audit = new();
    private readonly FakeRecordRepository _repository;

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
    }

    private static DateTime Today => DateTime.Today;

    private HandlingService Create(params Capability[] capabilities)
    {
        var currentUser = FakeCurrentUser.ForUser(_other.UserId, capabilities);
        return new HandlingService(
            _handlings, _issueHandlings, _repository, _hosts, _users,
            new AlwaysVisibleService(_hosts), currentUser, _audit);
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

    // ── 問題層級處理狀態（方案 B）─────────────────────────────────────────────

    private static LogIssueSignature Issue(string source, int eventId) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = System.Diagnostics.EventLogEntryType.Error
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
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────

internal class AlwaysVisibleService : IVisibilityService
{
    private readonly FakeHostStore _hosts;

    public AlwaysVisibleService(FakeHostStore hosts) => _hosts = hosts;

    public IReadOnlySet<long> GetVisibleHostIds() => _hosts.GetAll().Select(h => h.HostId).ToHashSet();

    public List<WebHost> GetVisibleHosts() => _hosts.GetAll();

    public void EnsureVisible(long hostId) { }
}

internal class FakeRecordRepository : IRecordRepository
{
    private readonly FakeHostStore _hosts;
    private readonly List<DailyAnalysisRecord> _records = new();

    public FakeRecordRepository(FakeHostStore hosts) => _hosts = hosts;

    public DailyAnalysisRecord AddRecord(string hostName, DateTime date, params LogIssueSignature[] issues)
    {
        var record = new DailyAnalysisRecord
        {
            Host = hostName,
            Date = date,
            RiskLevel = "高",
            TopIssues = issues.ToList()
        };
        _records.Add(record);
        return record;
    }

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter) => _records.ToList();

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

    public void Save(IssueHandling handling)
    {
        if (string.IsNullOrWhiteSpace(handling.Status))
        {
            Clear(handling.HostName, handling.Date, handling.IssueKey);
            return;
        }

        var existing = _items.FirstOrDefault(h => Same(h, handling.HostName, handling.Date, handling.IssueKey));
        if (existing == null) { _items.Add(handling); return; }
        existing.Status = handling.Status;
        existing.Note = handling.Note;
        existing.ActorId = handling.ActorId;
        existing.ActorAccount = handling.ActorAccount;
        existing.UpdatedAt = handling.UpdatedAt;
    }

    public void Clear(string hostName, DateTime date, string issueKey) =>
        _items.RemoveAll(h => Same(h, hostName, date, issueKey));

    private static bool Same(IssueHandling h, string hostName, DateTime date, string issueKey) =>
        string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
        h.Date.Date == date.Date && h.IssueKey == issueKey;
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
