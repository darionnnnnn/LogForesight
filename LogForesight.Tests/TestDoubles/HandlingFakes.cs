using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：HandlingService 相關 ───────────────────────────────────────────
// 原本都定義在 HandlingServiceTests.cs 檔尾，搬到這裡是因為之後 HandlingService
// 建構式改動（Phase 6-7 拆分 god class）時，改動範圍要集中在這一個檔案。

/// <summary>
/// 測試專用組裝門面：HandlingService 已依關注點拆成 DayHandlingCommandService／
/// IssueHandlingCommandService／HandlingHistoryQueryService＋共用的
/// HandlingProgressCalculator。既有測試大量呼叫「單一物件橫跨日/問題/查詢三種方法」
/// （例如同一個 fixture 先 Assign 再 GetLogs），這裡組裝成單一門面、保留原本
/// HandlingService 的建構參數與方法簽章，讓既有測試呼叫端零改動。
/// **僅供測試使用**，production 端一律直接注入拆分後的三個服務。
/// </summary>
internal class HandlingServiceFacade
{
    private readonly DayHandlingCommandService _day;
    private readonly IssueHandlingCommandService _issue;
    private readonly HandlingHistoryQueryService _history;

    public HandlingServiceFacade(
        IRecordHandlingStore store,
        IIssueHandlingStore issueStore,
        IIssueCaseStore cases,
        IssueCaseCoordinator caseCoordinator,
        INoiseMarkStore noiseMarks,
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit,
        ISystemSettingsStore settings)
    {
        var progress = new HandlingProgressCalculator(issueStore, store, cases, settings);
        _day = new DayHandlingCommandService(
            store, issueStore, caseCoordinator, repository, hosts, users, visibility, currentUser, audit, settings, progress);
        _issue = new IssueHandlingCommandService(
            store, issueStore, cases, caseCoordinator, noiseMarks, repository, hosts, users, visibility, currentUser, audit, progress);
        _history = new HandlingHistoryQueryService(
            store, issueStore, cases, hosts, users, visibility, settings, repository, progress);
    }

    public HandlingDto Get(long hostId, DateTime date) => _day.Get(hostId, date);
    public HandlingDto Update(long hostId, DateTime date, UpdateHandlingRequest request) => _day.Update(hostId, date, request);
    public HandlingDto Assign(long hostId, DateTime date, long? handlerId, bool reassign = false) => _day.Assign(hostId, date, handlerId, reassign);
    public IssueStatusResultDto SetIssueStatus(long hostId, DateTime date, SetIssueStatusRequest request) => _issue.SetIssueStatus(hostId, date, request);
    public BatchIssueStatusResultDto SetIssueStatusBatch(long hostId, DateTime date, BatchSetIssueStatusRequest request) => _issue.SetIssueStatusBatch(hostId, date, request);
    public List<IssueCasePreviewHostDto> PreviewIssueCaseAssign(string source, int eventId, DateTime? from, DateTime? to) => _issue.PreviewIssueCaseAssign(source, eventId, from, to);
    public BulkAssignIssueCaseResultDto BulkAssignIssueCase(BulkAssignIssueCaseRequest request) => _issue.BulkAssignIssueCase(request);
    public BulkIssueStatusResultDto BulkSetIssueStatusByHandler(BulkIssueStatusRequest request) => _issue.BulkSetIssueStatusByHandler(request);
    public List<HandlerCandidateDto> GetHandlerCandidates(long userGroupId) => _issue.GetHandlerCandidates(userGroupId);
    public List<HandlingLogDto> GetLogs(long hostId, DateTime date) => _history.GetLogs(hostId, date);
    public HandlingTodoDto GetTodo(IReadOnlyCollection<DailyAnalysisRecord> records) => _history.GetTodo(records);
    public HandlerWorkloadDto GetHandlerWorkload(long userId, bool includeResolvedDays) => _history.GetHandlerWorkload(userId, includeResolvedDays);
}

/// <summary>可見範圍固定為空——供工作頁的「檢視者可見範圍過濾」測試使用</summary>
internal class RestrictedVisibleService : IVisibilityService
{
    public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long>();
    public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => new HashSet<long>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> GetCaseGrants() => new Dictionary<string, IReadOnlySet<string>>();
    public bool IsCaseGrantOnly(long hostId) => false;
    public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    public List<WebHost> GetVisibleHosts() => new();
    public void EnsureVisible(long hostId) => throw DomainException.NotFound("找不到這台主機。");
}

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

    public List<IssueCase> GetByHandler(long userId) => _items.Where(c => c.HandlerId == userId).ToList();

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
