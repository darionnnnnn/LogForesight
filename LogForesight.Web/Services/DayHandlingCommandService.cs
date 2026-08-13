using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 風險日層級的處理狀態與指派（docs/WEB-SPEC.md §9.3 處理面板）。
///
/// 兩條寫入路徑的權限刻意不同：
///   - 狀態/說明/預計完成日：<c>Handle</c> 能力，限授權範圍內的主機（user 與 admin）
///   - **指派/改派處理人：<c>Assign</c> 能力，只有 admin**
///
/// 這對應到使用情境：主機 A 的負責人是 OOO，但主管認為這個問題緊急、
/// 先交給 XXX 處理——此時**負責人不變**（那是主機的長期屬性），
/// 變的是這個風險日的處理人。
///
/// 問題層級操作見 <see cref="IssueHandlingCommandService"/>；查詢/待辦/工作頁見
/// <see cref="HandlingHistoryQueryService"/>。
/// </summary>
public class DayHandlingCommandService
{
    private readonly IRecordHandlingStore _store;
    private readonly IIssueHandlingStore _issueStore;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly ISystemSettingsStore _settings;
    private readonly HandlingProgressCalculator _progress;
    private readonly UserCapabilityResolver _capabilities;

    /// <summary>問題上報通知（回饋十八輪批次G）：日層級狀態轉為「無法處理」時同樣通知 admin。
    /// **可為 null**（同 IssueHandlingCommandService 的說明）：測試組裝不注入，通知靜默跳過。</summary>
    private readonly MailNotificationService? _mail;

    public DayHandlingCommandService(
        IRecordHandlingStore store,
        IIssueHandlingStore issueStore,
        IssueCaseCoordinator caseCoordinator,
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit,
        ISystemSettingsStore settings,
        HandlingProgressCalculator progress,
        UserCapabilityResolver capabilities,
        MailNotificationService? mail = null)
    {
        _mail = mail;
        _store = store;
        _issueStore = issueStore;
        _caseCoordinator = caseCoordinator;
        _repository = repository;
        _hosts = hosts;
        _users = users;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
        _settings = settings;
        _progress = progress;
        _capabilities = capabilities;
    }

    public HandlingDto Get(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);
        var handling = _store.Get(host.HostName, date);
        // 補撈 record 算推導狀態（docs/archive/HISTORY.md #6）：處理面板要顯示的是
        // 「由問題標記推導出的日狀態」，不是存的日層級快照——兩者在批次套用後會不同步
        var record = _repository.GetOne(hostId, date);

        var dto = ToDto(host, date, handling, _progress.TryComputeProgress(host, date, record));
        dto.OpenCaseCount = _progress.OpenCaseCount(host, record);
        return dto;
    }

    public HandlingDto Update(long hostId, DateTime date, UpdateHandlingRequest request)
    {
        var host = RequireVisibleHost(hostId);
        RequireNotCaseGrantOnly(hostId);
        RequireRecordExists(hostId, date);

        if (!HandlingStatuses.IsValid(request.Status))
            throw DomainException.Validation($"未知的處理狀態「{request.Status}」。");

        if (request.DueDate.HasValue && request.DueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");

        // 無法處理必填原因（回饋十八輪批次G，與問題層級 ValidateIssueStatus 同一條規則）
        if (request.Status == HandlingStatuses.Escalated && string.IsNullOrWhiteSpace(request.Note))
            throw DomainException.Validation("標記為無法處理時必須填寫原因——管理者要據此決定結案或重新指派。");

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
                ? $"將 {host.HostName} {date:yyyy-MM-dd} 的處理狀態由「{HandlingTextHelpers.StatusText(previousStatus)}」改為「{HandlingTextHelpers.StatusText(request.Status)}」"
                : $"更新 {host.HostName} {date:yyyy-MM-dd} 的處理說明",
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new
            {
                Before = new { Status = previousStatus, Note = previousNote },
                After = new { existing.Status, existing.Note, existing.DueDate }
            });

        // 上報通知（回饋十八輪批次G）：只在「轉入」escalated 時通知（既有 escalated 只改說明不重寄）；
        // fire-and-forget，NotifyEscalationAsync 內部 try/catch 到底、永不拋出
        if (_mail != null && request.Status == HandlingStatuses.Escalated && previousStatus != HandlingStatuses.Escalated)
        {
            _ = _mail.NotifyEscalationAsync(new EscalationNotice(
                $"{date:yyyy-MM-dd} 風險日", host.HostName, _currentUser.Account, existing.Note));
        }

        return ToDto(host, date, existing);
    }

    /// <summary>
    /// 指派／改派這一天的處理人。<paramref name="reassign"/> 見
    /// <see cref="AssignHandlerRequest.Reassign"/>（docs/archive/FEEDBACK-10-PLAN.md §9）。
    /// </summary>
    public HandlingDto Assign(long hostId, DateTime date, long? handlerId, bool reassign = false)
    {
        var host = RequireVisibleHost(hostId);
        RequireNotCaseGrantOnly(hostId);
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

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

        // 建案（docs/archive/FEEDBACK-4-PLAN.md §2/Q1）：指派給人時，對當日「未處理計算」等級內、
        // 未結案、無進行中案件的每個問題建案並回溯關聯歷史——落實 2.1「同主機同問題只由
        // 一個人處理」。取消指派（handlerId=null）不建案，那不是「開始有人處理」的動作。
        var (casesCreated, skippedHandlerNames, reassignedCount) = handlerId.HasValue
            ? BuildCasesForDay(host, date, record, handlerId.Value, reassign)
            : (0, new List<string>(), 0);

        _audit.Record(
            action: AuditActions.HandlingAssign,
            // 摘要要說清楚「負責人不變」——這正是負責人與處理人分離的意義，
            // 事後查稽核的人必須看得出這一點
            summary: $"將 {host.HostName} {date:yyyy-MM-dd} 的處理人由「{beforeName}」改為「{afterName}」" +
                     $"（主機負責人不變：{OwnerNames(host)}）" +
                     (casesCreated > 0 ? $"；建立 {casesCreated} 個問題案件並回溯關聯歷史" : "") +
                     (reassignedCount > 0 ? $"；改派 {reassignedCount} 個他人進行中的問題案件" : ""),
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new
            {
                Before = beforeName, After = afterName, Owners = OwnerNames(host),
                CasesCreated = casesCreated, CasesSkipped = skippedHandlerNames, CasesReassigned = reassignedCount
            });

        var dto = ToDto(host, date, existing);
        dto.CasesCreated = casesCreated;
        dto.CasesSkippedHandlerNames = skippedHandlerNames;
        dto.CasesReassigned = reassignedCount;
        dto.OpenCaseCount = _progress.OpenCaseCount(host, record);

        // 被指派者看不到這台主機時告知執行指派的人（docs/archive/FEEDBACK-10-PLAN.md §7）：
        // 指派仍然成立——對方會以案件處理人的身分取得「這台主機的這些問題」的檢視權，
        // 但看不到這台主機的其他任何東西，交辦時講清楚比事後被問「我點進去是空的」好
        if (handler != null && !_visibility.GetVisibleHostIdsFor(handler.UserId).Contains(host.HostId))
            dto.AssigneeHasNoHostAccess = true;

        // 被指派者動得了嗎（回饋十三輪，體檢 H1 殘餘）：IssueHandlingCommandService 的批次指派
        // 已有這道提示，日層級指派原本沒有——同一套「不擋、只提示」決策，同一個
        // UserCapabilityResolver 事實來源，另寫一份判斷就會變成第二份複本
        if (handler != null && !_capabilities.Resolve(handler).Contains(Capability.Handle))
            dto.AssigneeCannotHandle = true;

        return dto;
    }

    /// <summary>
    /// 建案迴圈（Q1）：只對「未處理計算」等級內、未結案的問題建案——低風險預設不處理的問題
    /// 不該自動變成有人要處理的案件，已結案的問題也不建案。已有進行中案件的問題保留原處理人
    /// （Q2），回傳略過清單（去重的處理人姓名）供呼叫端提示「已由 ○○○ 的案件涵蓋」。
    /// </summary>
    private (int Created, List<string> SkippedHandlerNames, int Reassigned) BuildCasesForDay(
        WebHost host, DateTime date, DailyAnalysisRecord record, long handlerId, bool reassign)
    {
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();
        var dayIssueHandlings = _issueStore.GetForDay(host.HostName, date)
            .ToDictionary(h => h.IssueKey, StringComparer.Ordinal);
        var occurredAt = DateTime.Now;
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;

        var created = 0;
        var reassigned = 0;
        var skippedHandlerIds = new HashSet<long>();

        foreach (var issue in record.TopIssues)
        {
            if (!unhandledSeverities.Contains(issue.Severity)) continue;

            var key = IssueSignatureKey.For(issue);
            if (dayIssueHandlings.TryGetValue(key, out var existingHandling) &&
                IssueHandlingStatuses.IsClosed(existingHandling.Status))
                continue;

            var result = _caseCoordinator.BuildCase(
                host.HostName, key, HandlingTextHelpers.IssueLabel(issue), date,
                handlerId, note: null, dueDate: null,
                actorId, _currentUser.Account, occurredAt);

            if (result.Created)
            {
                created++;
            }
            else if (result.ExistingHandlerId is { } existingHandlerId && existingHandlerId != handlerId)
            {
                // 已由他人的進行中案件涵蓋：預設保留原處理人並回報（既有語意，不搶走）；
                // 使用者在前端確認過「要改派」才換人（§9），改派留下 case_reassign 歷程
                if (reassign)
                {
                    _caseCoordinator.ReassignCase(host.HostName, key, handlerId, actorId, _currentUser.Account, occurredAt);
                    reassigned++;
                }
                else
                {
                    skippedHandlerIds.Add(existingHandlerId);
                }
            }
        }

        var skippedNames = skippedHandlerIds
            .Select(id =>
            {
                var user = _users.Get(id);
                return user == null ? "（已刪除）" : NameFormat.WithAccount(user.DisplayName, user.Account);
            })
            .ToList();

        return (created, skippedNames, reassigned);
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

    /// <summary>
    /// 日層級的狀態與指派**不開放給案件授與者**（docs/archive/FEEDBACK-10-PLAN.md §7）：
    /// 他被交辦的是「這台主機的這個問題」，不是這一天。日層級狀態是整天的結論
    /// （由當天全部問題推導），改它等於代替有完整權限的人對整天下判斷。
    /// 前端已隱藏這一區（`RecordDetailDto.CaseGrantOnly`），這裡是防直接打端點。
    /// </summary>
    private void RequireNotCaseGrantOnly(long hostId)
    {
        if (_visibility.IsCaseGrantOnly(hostId))
            throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");
    }

    /// <summary>不允許對不存在的分析紀錄建立處理狀態——那會產生指向空白的待辦事項</summary>
    private void RequireRecordExists(long hostId, DateTime date)
    {
        if (_repository.GetOne(hostId, date) == null)
            throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");
    }

    private HandlingDto ToDto(WebHost host, DateTime date, RecordHandling? handling, DayHandlingDerivation.DayProgress? progress = null)
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
            StatusText = HandlingTextHelpers.StatusText(handling?.Status ?? HandlingStatuses.Open),
            // 由問題標記推導出的日狀態（#6）：處理面板應顯示這個，不是上面存的日層級快照——
            // 指派時日層級會被自動推進成 in_progress（見上方 Assign），之後問題全結案也不會
            // 回頭改寫那個存值，只有推導值能反映「現在真正的狀態」。progress 為 null（Update/
            // Assign 呼叫端不補算）時前端 fallback 用 Status/StatusText，行為與改版前一致。
            DerivedStatus = progress?.DayStatus,
            DerivedStatusText = progress != null ? HandlingTextHelpers.StatusText(progress.Value.DayStatus) : null,
            TotalIssues = progress?.Total ?? 0,
            ClosedIssues = progress?.Closed ?? 0,
            HandlerId = handlerId,
            HandlerName = handler?.DisplayName,
            HandlerAccount = handler?.Account,
            DueDate = handling?.DueDate?.ToString("yyyy-MM-dd"),
            IsOverdue = handling?.DueDate.HasValue == true &&
                        handling.DueDate.Value.Date < DateTime.Today &&
                        HandlingStatuses.Unresolved.Contains(handling.Status),
            Note = handling?.Note,
            UpdatedAt = handling?.UpdatedAt,

            // 負責人是主機的長期屬性，處理面板上唯讀顯示——改派不會動到它。
            // **這裡刻意不加帳號**，是全站「顯示名稱(帳號)」規範的已知例外
            // （docs/archive/FEEDBACK-10-PLAN.md §6 體檢確認）：這份清單同時是指派下拉的
            // 「置頂＋標（負責人）」比對鍵，而比對是拿 user.displayName 逐字比對
            // （ui.js searchableUserSelect），加上帳號會讓比對永遠不成立、置頂靜默失效。
            // 帶帳號的版本在主機清單另有一份（HostDtoMapper.OwnerNames）。
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
}
