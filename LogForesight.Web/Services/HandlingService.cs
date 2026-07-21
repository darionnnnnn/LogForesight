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

    /// <summary>指派/改派處理人（僅 admin）</summary>
    HandlingDto Assign(long hostId, DateTime date, long? handlerId);

    List<HandlingLogDto> GetLogs(long hostId, DateTime date);

    /// <summary>儀表板待辦：未處理與逾期</summary>
    HandlingTodoDto GetTodo();
}

public class HandlingService : IHandlingService
{
    private readonly IRecordHandlingStore _store;
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public HandlingService(
        IRecordHandlingStore store,
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _store = store;
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

    public HandlingTodoDto GetTodo()
    {
        var visibleHostNames = _visibility.GetVisibleHosts()
            .Select(h => h.HostName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unresolved = _store.GetUnresolved()
            .Where(h => visibleHostNames.Contains(h.HostName))
            .ToList();

        return new HandlingTodoDto
        {
            OpenCount = unresolved.Count(h => h.Status == HandlingStatuses.Open),
            InProgressCount = unresolved.Count(h => h.Status == HandlingStatuses.InProgress),
            OverdueCount = unresolved.Count(h => h.DueDate.HasValue && h.DueDate.Value.Date < DateTime.Today)
        };
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

    private static string ActionText(string action) => action switch
    {
        HandlingActions.Assign => "指派處理人",
        HandlingActions.AutoAssign => "自動帶入處理人",
        HandlingActions.StatusChange => "變更狀態",
        HandlingActions.NoteUpdate => "更新說明",
        _ => action
    };
}
