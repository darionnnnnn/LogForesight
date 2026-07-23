using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 風險日的處理狀態與指派（docs/WEB-SPEC.md §9.3 處理面板）。
///
/// 兩條寫入路徑的權限刻意不同：
///   - 狀態/說明/預計完成日：<c>Handle</c> 能力，限授權範圍內的主機（user 與 admin）
///   - **指派/改派處理人：<c>Assign</c> 能力，只有 admin**
///
/// 這對應到使用情境：主機 A 的負責人是 OOO，但主管認為這個問題緊急、
/// 先交給 XXX 處理——此時**負責人不變**（那是主機的長期屬性），
/// 變的是這個風險日的處理人。
/// </summary>
public interface IHandlingService
{
    HandlingDto Get(long hostId, DateTime date);

    /// <summary>更新狀態/說明/預計完成日（不含指派）</summary>
    HandlingDto Update(long hostId, DateTime date, UpdateHandlingRequest request);

    /// <summary>設定單一問題的處理狀態（方案 B）；回傳更新後的當日進度</summary>
    IssueStatusResultDto SetIssueStatus(long hostId, DateTime date, SetIssueStatusRequest request);

    /// <summary>指派/改派處理人（僅 admin）</summary>
    HandlingDto Assign(long hostId, DateTime date, long? handlerId);

    List<HandlingLogDto> GetLogs(long hostId, DateTime date);

    /// <summary>
    /// 儀表板待辦：未處理與逾期。母體是傳入的風險日紀錄——
    /// **沒有 handling 列的風險日也要算未處理**（與問題查詢清單同一套語意），
    /// 只數 handling.json 既有列會漏掉所有「從未被動過」的新問題。
    /// </summary>
    HandlingTodoDto GetTodo(IReadOnlyCollection<DailyAnalysisRecord> records);
}

public class HandlingService : IHandlingService
{
    private readonly IRecordHandlingStore _store;
    private readonly IIssueHandlingStore _issueStore;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public HandlingService(
        IRecordHandlingStore store,
        IIssueHandlingStore issueStore,
        INoiseMarkStore noiseMarks,
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _store = store;
        _issueStore = issueStore;
        _noiseMarks = noiseMarks;
        _repository = repository;
        _hosts = hosts;
        _users = users;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
    }

    public HandlingDto Get(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);
        var handling = _store.Get(host.HostName, date);

        return ToDto(host, date, handling);
    }

    public HandlingDto Update(long hostId, DateTime date, UpdateHandlingRequest request)
    {
        var host = RequireVisibleHost(hostId);
        RequireRecordExists(hostId, date);

        if (!HandlingStatuses.IsValid(request.Status))
            throw DomainException.Validation($"未知的處理狀態「{request.Status}」。");

        if (request.DueDate.HasValue && request.DueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");

        var existing = _store.Get(host.HostName, date) ?? NewHandling(host.HostName, date);
        var previousStatus = existing.Status;
        var previousNote = existing.Note;

        existing.Status = request.Status;
        existing.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        existing.DueDate = request.DueDate;
        existing.UpdatedAt = DateTime.Now;

        _store.Save(existing);

        // 歷程與快照必須成對寫入，否則會出現「狀態變了但歷程沒記錄」的斷點
        var action = previousStatus != request.Status ? HandlingActions.StatusChange : HandlingActions.NoteUpdate;
        AppendLog(existing, action);

        _audit.Record(
            action: previousStatus != request.Status ? AuditActions.HandlingStatus : AuditActions.HandlingNote,
            summary: previousStatus != request.Status
                ? $"將 {host.HostName} {date:yyyy-MM-dd} 的處理狀態由「{StatusText(previousStatus)}」改為「{StatusText(request.Status)}」"
                : $"更新 {host.HostName} {date:yyyy-MM-dd} 的處理說明",
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new
            {
                Before = new { Status = previousStatus, Note = previousNote },
                After = new { existing.Status, existing.Note, existing.DueDate }
            });

        return ToDto(host, date, existing);
    }

    public IssueStatusResultDto SetIssueStatus(long hostId, DateTime date, SetIssueStatusRequest request)
    {
        var host = RequireVisibleHost(hostId);
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var clearing = string.IsNullOrWhiteSpace(request.Status);
        if (!clearing && !IssueHandlingStatuses.IsValid(request.Status))
            throw DomainException.Validation($"未知的問題處理狀態「{request.Status}」。");

        // 問題必須真的存在於當日紀錄——否則會存下指向不存在問題的狀態
        var issue = record.TopIssues.FirstOrDefault(i => IssueSignatureKey.For(i) == request.IssueKey)
                    ?? throw DomainException.Validation("找不到這個問題，可能紀錄已更新，請重新整理。");

        if (clearing)
        {
            _issueStore.Clear(host.HostName, date, request.IssueKey);
        }
        else
        {
            _issueStore.Save(new IssueHandling
            {
                HostName = host.HostName,
                Date = date.Date,
                IssueKey = request.IssueKey,
                Status = request.Status,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
                ActorAccount = _currentUser.Account,
                UpdatedAt = DateTime.Now
            });

            // 標「已知雜訊」＝寫入記憶，之後同主機同簽章的新問題自動判讀成雜訊（治標，供無規則命中的 Other 類別）
            if (request.Status == IssueHandlingStatuses.KnownNoise)
            {
                _noiseMarks.Save(new NoiseMark
                {
                    HostName = host.HostName,
                    IssueKey = request.IssueKey,
                    MarkedByAccount = _currentUser.Account,
                    MarkedAt = DateTime.Now,
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
                });
            }
            // 「調回未處理」且使用者選擇一併刪除記憶——之後同簽章不再自動判讀成雜訊
            else if (request.Status == IssueHandlingStatuses.Open && request.ForgetNoise)
            {
                _noiseMarks.Delete(host.HostName, request.IssueKey);
            }
        }

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: clearing
                ? $"清除 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】的處理標記"
                : $"將 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】標為「{IssueStatusText(request.Status)}」",
            targetKind: "issue_handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}/{request.IssueKey}",
            detail: new { request.IssueKey, request.Status, request.Note });

        // 回傳更新後的當日進度，讓前端就地更新「N/M 已處理」與日層級推導狀態
        var handlings = _issueStore.GetForDay(host.HostName, date);
        var dayLevel = _store.Get(host.HostName, date)?.Status;
        var progress = DayHandlingDerivation.Derive(record.TopIssues, handlings, dayLevel);

        return new IssueStatusResultDto
        {
            IssueKey = request.IssueKey,
            Status = clearing ? string.Empty : request.Status,
            StatusText = clearing ? "未處理" : IssueStatusText(request.Status),
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            DayStatus = progress.DayStatus,
            DayStatusText = StatusText(progress.DayStatus)
        };
    }

    public HandlingDto Assign(long hostId, DateTime date, long? handlerId)
    {
        var host = RequireVisibleHost(hostId);
        RequireRecordExists(hostId, date);

        WebUser? handler = null;
        if (handlerId.HasValue)
        {
            handler = _users.Get(handlerId.Value)
                      ?? throw DomainException.NotFound("找不到指定的處理人。");

            if (!handler.Active)
                throw DomainException.Validation($"{handler.DisplayName} 的帳號已停用，無法指派。");
        }

        var existing = _store.Get(host.HostName, date) ?? NewHandling(host.HostName, date);
        var previousHandler = existing.HandlerId.HasValue ? _users.Get(existing.HandlerId.Value) : null;

        existing.HandlerId = handlerId;
        existing.UpdatedAt = DateTime.Now;

        // 指派給人卻還是「未處理」語意上矛盾，自動推進到處理中；
        // 已結案的狀態不動（改派結案問題可能是為了補資料）
        if (handlerId.HasValue && existing.Status == HandlingStatuses.Open)
            existing.Status = HandlingStatuses.InProgress;

        _store.Save(existing);
        AppendLog(existing, HandlingActions.Assign);

        var beforeName = previousHandler?.DisplayName ?? "（未指派）";
        var afterName = handler?.DisplayName ?? "（未指派）";

        _audit.Record(
            action: AuditActions.HandlingAssign,
            // 摘要要說清楚「負責人不變」——這正是負責人與處理人分離的意義，
            // 事後查稽核的人必須看得出這一點
            summary: $"將 {host.HostName} {date:yyyy-MM-dd} 的處理人由「{beforeName}」改為「{afterName}」" +
                     $"（主機負責人不變：{OwnerNames(host)}）",
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new { Before = beforeName, After = afterName, Owners = OwnerNames(host) });

        return ToDto(host, date, existing);
    }

    public List<HandlingLogDto> GetLogs(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);

        return _store.GetLogs(host.HostName, date)
            .Select(log => new HandlingLogDto
            {
                Action = log.Action,
                ActionText = ActionText(log.Action),
                Status = log.Status,
                StatusText = StatusText(log.Status),
                HandlerName = log.HandlerId.HasValue
                    ? _users.Get(log.HandlerId.Value)?.DisplayName ?? "（已刪除）"
                    : null,
                Note = log.Note,
                ActorAccount = log.ActorAccount,
                CreatedAt = log.CreatedAt
            })
            .ToList();
    }

    public HandlingTodoDto GetTodo(IReadOnlyCollection<DailyAnalysisRecord> records)
    {
        if (records.Count == 0) return new HandlingTodoDto();

        // 紀錄 → 現行主機名稱：handling 以現行名稱為鍵（與 RecordQueryService 同一套規則）。
        // 可見範圍不在這裡過濾——傳入的紀錄已經過 RecordRepository 的可見範圍交集
        var lookup = new HostLookup(_hosts.GetAll());
        string NameOf(DailyAnalysisRecord record) => lookup.For(record)?.HostName ?? record.Host;

        var hostNames = records.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = records.Min(r => r.Date);
        var to = records.Max(r => r.Date);
        var handlings = _store.GetMany(hostNames, from, to);
        var issueHandlings = _issueStore.GetMany(hostNames, from, to);

        var todo = new HandlingTodoDto();
        foreach (var record in records)
        {
            var name = NameOf(record);
            var handling = handlings.FirstOrDefault(h =>
                string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                h.Date.Date == record.Date.Date);

            // 日狀態由問題層級推導（方案 B，與問題清單同一套規則）——
            // 全部問題結案的風險日不再算未處理，即使日層級從沒被人動過
            var forDay = issueHandlings
                .Where(h => string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                            h.Date.Date == record.Date.Date)
                .ToList();
            var progress = DayHandlingDerivation.Derive(record.TopIssues, forDay, handling?.Status);

            if (progress.DayStatus == HandlingStatuses.Open) todo.OpenCount++;
            else if (progress.DayStatus == HandlingStatuses.InProgress) todo.InProgressCount++;

            if (handling?.DueDate.HasValue == true &&
                handling.DueDate.Value.Date < DateTime.Today &&
                progress.IsUnresolved)
            {
                todo.OverdueCount++;
            }
        }

        return todo;
    }

    /// <summary>
    /// 風險日產生時的預設處理人：**負責人恰好一人時自動帶入，多人或無人則留空**。
    /// 多人時不猜——猜錯會讓真正該處理的人以為有別人在處理。
    /// 由查詢端於首次讀取時觸發（批次不知道 Web 的負責人設定）。
    /// </summary>
    private RecordHandling NewHandling(string hostName, DateTime date)
    {
        var handling = new RecordHandling
        {
            HostName = hostName,
            Date = date.Date,
            Status = HandlingStatuses.Open,
            UpdatedAt = DateTime.Now
        };

        var host = _hosts.FindByName(hostName);
        var defaultHandlerId = host == null ? null : DefaultHandlerId(host);

        if (defaultHandlerId.HasValue)
        {
            handling.HandlerId = defaultHandlerId;

            _audit.RecordSystem(
                action: AuditActions.HandlingAssign,
                summary: $"{hostName} {date:yyyy-MM-dd} 的處理人自動帶入唯一負責人 " +
                         $"{_users.Get(defaultHandlerId.Value)?.DisplayName}",
                targetKind: "handling",
                targetId: $"{hostName}/{date:yyyy-MM-dd}");
        }

        return handling;
    }

    /// <summary>
    /// 預設處理人：**負責人恰好一人且未停用時**才帶入。
    /// 多人時不猜——猜錯會讓真正該處理的人以為有別人在處理，那比留空更糟。
    /// </summary>
    private long? DefaultHandlerId(WebHost host)
    {
        if (host.OwnerUserIds.Count != 1) return null;

        var owner = _users.Get(host.OwnerUserIds[0]);
        return owner is { Active: true } ? owner.UserId : null;
    }

    private void AppendLog(RecordHandling handling, string action)
    {
        _store.AppendLog(new RecordHandlingLog
        {
            HostName = handling.HostName,
            Date = handling.Date,
            Status = handling.Status,
            HandlerId = handling.HandlerId,
            Note = handling.Note,
            ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ActorAccount = _currentUser.Account,
            Action = action,
            CreatedAt = DateTime.Now
        });
    }

    private WebHost RequireVisibleHost(long hostId)
    {
        _visibility.EnsureVisible(hostId);
        return _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");
    }

    /// <summary>不允許對不存在的分析紀錄建立處理狀態——那會產生指向空白的待辦事項</summary>
    private void RequireRecordExists(long hostId, DateTime date)
    {
        if (_repository.GetOne(hostId, date) == null)
            throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");
    }

    private HandlingDto ToDto(WebHost host, DateTime date, RecordHandling? handling)
    {
        // 尚未有任何處理紀錄時，以「唯一負責人」作為顯示上的預設處理人。
        // **這裡只計算不寫入**——讀取不該產生副作用，也不該每次瀏覽都留下一筆稽核；
        // 實際的持久化與稽核發生在第一次真正寫入時（見 NewHandling），兩邊共用 DefaultHandlerId。
        //
        // 一旦有處理紀錄，其 HandlerId 就是唯一權威（含 null）：
        // 「從未指派」與「admin 明確取消指派」都是 null 但意義相反，
        // 對已存在的紀錄再套預設值會讓「取消指派」看起來沒有生效。
        var handlerId = handling != null ? handling.HandlerId : DefaultHandlerId(host);
        var handler = handlerId.HasValue ? _users.Get(handlerId.Value) : null;

        return new HandlingDto
        {
            HostId = host.HostId,
            HostName = host.HostName,
            Date = date.ToString("yyyy-MM-dd"),
            Status = handling?.Status ?? HandlingStatuses.Open,
            StatusText = StatusText(handling?.Status ?? HandlingStatuses.Open),
            HandlerId = handlerId,
            HandlerName = handler?.DisplayName,
            DueDate = handling?.DueDate?.ToString("yyyy-MM-dd"),
            IsOverdue = handling?.DueDate.HasValue == true &&
                        handling.DueDate.Value.Date < DateTime.Today &&
                        HandlingStatuses.Unresolved.Contains(handling.Status),
            Note = handling?.Note,
            UpdatedAt = handling?.UpdatedAt,

            // 負責人是主機的長期屬性，處理面板上唯讀顯示——改派不會動到它
            OwnerNames = host.OwnerUserIds
                .Select(id => _users.Get(id)?.DisplayName ?? "（已刪除）")
                .ToList(),

            CanAssign = _currentUser.Has(Capability.Assign),
            CanHandle = _currentUser.Has(Capability.Handle)
        };
    }

    private string OwnerNames(WebHost host)
    {
        var names = host.OwnerUserIds
            .Select(id => _users.Get(id)?.DisplayName ?? "（已刪除）")
            .ToList();

        return names.Count == 0 ? "未指定" : string.Join("、", names);
    }

    private static string StatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        HandlingStatuses.WontFix => "不處理",
        HandlingStatuses.FalsePositive => "誤報",
        HandlingStatuses.KnownNoise => "已知雜訊",
        _ => status
    };

    /// <summary>問題層級狀態文字（未標記＝未處理由呼叫端處理）</summary>
    private static string IssueStatusText(string status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        IssueHandlingStatuses.Open => "未處理",
        _ => status
    };

    private static string ActionText(string action) => action switch
    {
        HandlingActions.Assign => "指派處理人",
        HandlingActions.AutoAssign => "自動帶入處理人",
        HandlingActions.StatusChange => "變更狀態",
        HandlingActions.NoteUpdate => "更新說明",
        _ => action
    };
}
