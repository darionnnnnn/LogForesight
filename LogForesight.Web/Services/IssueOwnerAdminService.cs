using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題負責人管理（回饋十八輪批次F，「問題負責人」頁）：以 (Source,EventId) 為鍵指派跨主機的
/// 負責人。與主機負責人（HostAdminService.SetHostOwners）是相同概念、相同管理模式，
/// 唯一差別是鍵從「主機」換成「問題」。
/// </summary>
public class IssueOwnerAdminService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    /// <summary>問題選擇器的近期出現窗口——與問題負責人的授權路徑窗口（RetentionDays）刻意不同：
    /// 那是「保留期內都算負責人可見」的長窗口，這是「新增規則時該挑哪個問題」的短窗口，
    /// 30 天內出現過的問題才有指派的實務意義，太久沒出現的問題挑進來也只是雜訊。</summary>
    private const int RecentIssueWindowDays = 30;

    private readonly IIssueOwnerStore _issueOwners;
    private readonly IIssueAggregateQuery _issueAggregates;
    private readonly IUserStore _users;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUserDisplayNameService _displayNameService;
    private readonly IWorkOrderStore _workOrders;
    private readonly WorkOrderCoordinator _workOrderCoordinator;
    private readonly PermissionVersionStamp _permissionVersion;

    /// <summary>靜音天數上限（含自訂截止日距今天的上限）</summary>
    public const int MaxMuteDays = 365;

    public const int MaxMuteReasonLength = 500;

    /// <summary>問題設定 DTO 附帶的靜音歷程筆數（新到舊）</summary>
    public const int MuteHistoryLimit = 10;

    public IssueOwnerAdminService(
        IIssueOwnerStore issueOwners, IIssueAggregateQuery issueAggregates, IUserStore users, IAuditService audit,
        ICurrentUser currentUser, IUserDisplayNameService displayNameService,
        IWorkOrderStore workOrders, WorkOrderCoordinator workOrderCoordinator,
        PermissionVersionStamp permissionVersion)
    {
        _permissionVersion = permissionVersion;
        _issueOwners = issueOwners;
        _issueAggregates = issueAggregates;
        _users = users;
        _audit = audit;
        _currentUser = currentUser;
        _displayNameService = displayNameService;
        _workOrders = workOrders;
        _workOrderCoordinator = workOrderCoordinator;
    }

    /// <summary>
    /// Maintain 能力的服務層檢查（唯一一份）：Controller 的 [Permission] 之外，服務層也擋——
    /// 統一標記勾「之後自動套用」會從別的入口呼叫 <see cref="SetConclusion"/>，只靠 Controller 擋不到。
    /// 需要在寫入前提早檢查的呼叫端（<see cref="IssueHandlingCommandService.BulkCloseIssue"/>）也呼叫這裡。
    /// </summary>
    public void EnsureMaintain()
    {
        if (!_currentUser.Capabilities.Contains(Capability.Maintain))
            throw DomainException.Forbidden("需要維護權限才能設定機房結論或靜音問題。");
    }

    public List<IssueOwnerDto> List()
    {
        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var recentByKey = RecentAggregatesByKey();

        return _issueOwners.GetAll()
            .OrderBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EventId)
            .Select(r => ToDto(r, usersById, recentByKey))
            .ToList();
    }

    /// <summary>問題選擇器候選項：近 <see cref="RecentIssueWindowDays"/> 天內出現過的問題，
    /// 依主機數由多到少排序（影響面大的問題排前面，符合「今天該先處理哪個」的既有排序哲學）。</summary>
    public List<RecentIssueOptionDto> RecentIssues()
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(RecentIssueWindowDays - 1));
        var owned = _issueOwners.GetAll()
            .Select(r => IssueProfile.KeyOf(r.SourceName, r.EventId))
            .ToHashSet();

        // 靜音不排除：靜音中的問題要選得到、看得到近況，才能解除
        return _issueAggregates.Aggregate(IssueExclusion.None, from, to, null)
            .OrderByDescending(a => a.HostCount)
            .ThenBy(a => a.Source, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                return new RecentIssueOptionDto
                {
                    SourceName = a.Source,
                    EventId = a.EventId,
                    DisplayLabel = FormatDisplayLabel(a.Source, a.EventId),
                    HostCount = a.HostCount,
                    LastSeen = a.LastSeen,
                    HasOwner = owned.Contains(IssueProfile.KeyOf(a.Source, a.EventId))
                };
            })
            .ToList();
    }

    public IssueOwnerDto Upsert(SaveIssueOwnerRequest request)
    {
        var sourceName = request.SourceName?.Trim() ?? "";
        if (sourceName.Length == 0)
            throw DomainException.Validation("請指定問題來源（Source）。");
        if (request.EventId < 0)
            throw DomainException.Validation("請指定合法的 Event ID。");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var requestedOwners = request.OwnerUserIds.Distinct().ToList();
        NameFormat.EnsureAllKnown(requestedOwners, usersById, "使用者");

        var before = _issueOwners.Get(sourceName, request.EventId);
        var beforeOwners = before?.OwnerUserIds.Select(id => usersById.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList()
                            ?? new List<string>();
        var afterOwners = requestedOwners.Select(id => usersById[id].Account).ToList();

        var saved = _issueOwners.Upsert(new IssueProfile
        {
            SourceName = sourceName,
            EventId = request.EventId,
            OwnerUserIds = requestedOwners,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            UpdatedByAccount = _currentUser.Account,
            // 這支方法只管負責人（表單看不到機房結論欄位）——保留既有結論原封不動，
            // 否則「編輯負責人」會把「設定機房結論」的結果悄悄清空（同 IssueOwnerStore.Upsert
            // 逐欄複製守則的反向情境：呼叫端沒帶到的欄位不該被覆寫成預設值）
            ConclusionStatus = before?.ConclusionStatus,
            ConclusionNote = before?.ConclusionNote ?? string.Empty,
            ConcludedById = before?.ConcludedById,
            ConcludedByAccount = before?.ConcludedByAccount,
            ConcludedAt = before?.ConcludedAt,
            AutoApply = before?.AutoApply ?? false,
            // 靜音區間同理：編輯負責人不可洗掉既有靜音
            Mutes = before?.Mutes ?? new List<MuteInterval>()
        });
        // 問題負責人隱含 User 角色能力：負責人清單一變就推進權限版本
        _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.IssueOwnerUpdate,
            summary: $"設定問題「{sourceName} {request.EventId}」的負責人：由「{NameFormat.Join(beforeOwners)}」改為「{NameFormat.Join(afterOwners)}」",
            targetKind: "issue_owner",
            targetId: $"{sourceName}/{request.EventId}",
            detail: new { SourceName = sourceName, request.EventId, Before = beforeOwners, After = afterOwners, request.Note });

        var recentByKey = RecentAggregatesByKey();
        return ToDto(saved, usersById, recentByKey);
    }

    /// <summary>
    /// 設定機房結論（回饋十九輪批次F，§2 決策一）：統一標記勾選「之後自動套用」
    /// （<see cref="IssueHandlingCommandService.BulkCloseIssue"/>）與問題負責與靜音頁的「設定機房結論」
    /// 都走這裡——只有一份「保留既有負責人／備註，只改結論欄」的合併邏輯，避免兩個入口
    /// 各自 Upsert 一次、其中一個沒注意到要保留對方負責的欄位。
    /// </summary>
    public IssueOwnerDto SetConclusion(string source, int eventId, SetIssueConclusionRequest request)
    {
        EnsureMaintain();
        var sourceName = source?.Trim() ?? "";
        if (sourceName.Length == 0) throw DomainException.Validation("請指定問題來源（Source）。");
        if (!IssueHandlingStatuses.IsClosed(request.Status))
            throw DomainException.Validation("機房結論只接受「已處理／不處理／誤報／已知雜訊」四種結論。");

        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note))
            throw DomainException.Validation("請填寫機房結論的原因——這是代全體下結論的操作，理由要留在紀錄裡。");

        var existing = _issueOwners.Get(sourceName, eventId) ?? new IssueProfile { SourceName = sourceName, EventId = eventId };
        var beforeStatus = existing.ConclusionStatus;

        existing.ConclusionStatus = request.Status;
        existing.ConclusionNote = note;
        existing.ConcludedById = _currentUser.UserId;
        existing.ConcludedByAccount = _currentUser.Account;
        existing.ConcludedAt = DateTime.Now;
        existing.AutoApply = request.AutoApply;
        existing.UpdatedByAccount = _currentUser.Account;

        var saved = _issueOwners.Upsert(existing);

        _audit.Record(
            action: AuditActions.IssueOwnerUpdate,
            summary: $"設定問題「{sourceName} {eventId}」的機房結論：{HandlingTextHelpers.IssueStatusText(request.Status)}" +
                     (beforeStatus != null ? $"（原結論：{HandlingTextHelpers.IssueStatusText(beforeStatus)}）" : "") +
                     (request.AutoApply ? "，之後新出現的主機日將自動套用" : ""),
            targetKind: "issue_owner",
            targetId: $"{sourceName}/{eventId}",
            detail: new { SourceName = sourceName, EventId = eventId, request.Status, Note = note, request.AutoApply });

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        return ToDto(saved, usersById, RecentAggregatesByKey());
    }

    /// <summary>
    /// 解除機房結論（§2 決策一）：只清結論欄位（AutoApply 隨之停止），已經套用到 IssueHandling
    /// 的歷史列**不回滾**——那是已經寫入的事實，誠實留痕；要翻案走既有逐日／統一標記改 open。
    /// </summary>
    public IssueOwnerDto ClearConclusion(string source, int eventId)
    {
        EnsureMaintain();
        var existing = _issueOwners.Get(source, eventId)
                        ?? throw DomainException.NotFound("找不到這筆問題設定。");

        existing.ConclusionStatus = null;
        existing.ConclusionNote = string.Empty;
        existing.ConcludedById = null;
        existing.ConcludedByAccount = null;
        existing.ConcludedAt = null;
        existing.AutoApply = false;
        existing.UpdatedByAccount = _currentUser.Account;

        var saved = _issueOwners.Upsert(existing);

        _audit.Record(
            action: AuditActions.IssueOwnerUpdate,
            summary: $"解除問題「{source} {eventId}」的機房結論",
            targetKind: "issue_owner",
            targetId: $"{source}/{eventId}");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        return ToDto(saved, usersById, RecentAggregatesByKey());
    }

    /// <summary>
    /// 靜音問題：<see cref="SetIssueMuteRequest.Days"/> 與 <see cref="SetIssueMuteRequest.Until"/> 恰給一個。
    /// 區間 From＝今天、To＝今天＋Days−1 或 Until；今天已在某區間內→延長該區間（不新增），原因與操作者更新為本次。
    /// 問題設定不存在時建立（沒有負責人、沒有結論）。ExistingOrders="close" 需同時具 Assign 與 Handle，
    /// 對進行中交辦單逐張以 wont_fix 代為結案；"pause"＝不動交辦單。
    /// </summary>
    public IssueOwnerDto SetMute(string source, int eventId, SetIssueMuteRequest request)
    {
        EnsureMaintain();

        var sourceName = source?.Trim() ?? "";
        if (sourceName.Length == 0) throw DomainException.Validation("請指定問題來源（Source）。");

        var today = DateTime.Today;
        if (request.Days.HasValue == request.Until.HasValue)
            throw DomainException.Validation("請在「靜音天數」與「靜音至（日期）」之間擇一指定。");

        DateTime to;
        if (request.Days.HasValue)
        {
            if (request.Days.Value < 1 || request.Days.Value > MaxMuteDays)
                throw DomainException.Validation($"靜音天數須介於 1～{MaxMuteDays} 天。");
            to = today.AddDays(request.Days.Value - 1);
        }
        else
        {
            to = request.Until!.Value.Date;
            if (to < today) throw DomainException.Validation("靜音截止日不可早於今天。");
            if (to > today.AddDays(MaxMuteDays))
                throw DomainException.Validation($"靜音截止日不可超過今天起 {MaxMuteDays} 天。");
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw DomainException.Validation("請填寫靜音原因——暫停告警的理由要留在紀錄裡。");
        if (reason.Length > MaxMuteReasonLength)
            throw DomainException.Validation($"靜音原因長度不可超過 {MaxMuteReasonLength} 字元。");

        bool closeOrders = request.ExistingOrders switch
        {
            "pause" => false,
            "close" => true,
            _ => throw DomainException.Validation("進行中交辦單的處置只接受「暫停」或「代為結案」。")
        };

        // 能力檢查一律在任何寫入之前：代為結案沒有權限時整筆不做
        if (closeOrders && !(_currentUser.Capabilities.Contains(Capability.Assign) && _currentUser.Capabilities.Contains(Capability.Handle)))
            throw DomainException.Forbidden("代為結案進行中的交辦單需要同時具備指派與處理權限。");

        var now = DateTime.Now;
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;
        var existing = _issueOwners.Get(sourceName, eventId) ?? new IssueProfile { SourceName = sourceName, EventId = eventId };

        var current = IssueProfile.CurrentMute(existing, today);
        var extended = current != null;
        var interval = new MuteInterval
        {
            From = current?.From.Date ?? today, To = to, Reason = reason,
            ById = actorId, ByAccount = _currentUser.Account, At = now
        };
        // 重建清單再存回（不就地改讀出的物件清單，store 的快照不可修改）
        var mutes = existing.Mutes.Where(m => !ReferenceEquals(m, current)).ToList();
        mutes.Add(interval);
        existing.Mutes = mutes;
        existing.UpdatedByAccount = _currentUser.Account;

        // 在靜音寫入前固定本次對象；權限與請求驗證失敗時仍維持零寫入。
        var ordersToClose = closeOrders ? _workOrders.GetActiveByIssue(sourceName, eventId).ToList() : new List<WorkOrder>();
        var saved = _issueOwners.Upsert(existing);

        IssueMuteCloseOutcomeDto? closeOutcome = null;
        if (closeOrders)
        {
            closeOutcome = new IssueMuteCloseOutcomeDto();
            var actor = new WorkOrderActor { ActorId = actorId, ActorAccount = _currentUser.Account, OccurredAt = now };
            foreach (var order in ordersToClose)
            {
                try
                {
                    _workOrderCoordinator.AdminClose(order.WorkOrderId, IssueHandlingStatuses.WontFix, "靜音：" + reason, actor);
                }
                catch (Exception ex)
                {
                    closeOutcome.FailedWorkOrderId = order.WorkOrderId;
                    closeOutcome.FailureMessage = ex is DomainException ? ex.Message : "代為結案時發生未預期的錯誤。";
                    closeOutcome.NotProcessed = ordersToClose.SkipWhile(o => o.WorkOrderId != order.WorkOrderId)
                        .Skip(1).Select(o => o.WorkOrderId).ToList();
                    Log.Error(ex, "靜音問題 {0}/{1} 代為結案停在交辦單 #{2}", sourceName, eventId, order.WorkOrderId);
                    break;
                }

                closeOutcome.Succeeded.Add(order.WorkOrderId);
                _audit.Record(
                    action: AuditActions.WorkOrderAdminClose,
                    summary: $"靜音問題「{sourceName} {eventId}」代為結案交辦單 #{order.WorkOrderId}",
                    targetKind: "work_order",
                    targetId: order.WorkOrderId.ToString(),
                    detail: new { SourceName = sourceName, EventId = eventId, Reason = reason });
            }
        }

        var closedCount = closeOutcome?.Succeeded.Count ?? 0;

        _audit.Record(
            action: AuditActions.IssueMute,
            summary: $"靜音問題「{sourceName} {eventId}」：{interval.From:yyyy-MM-dd}～{interval.To:yyyy-MM-dd}" +
                     (extended ? "（延長進行中的靜音）" : "") + $"，原因：{reason}" +
                     (closeOrders ? $"，代為結案進行中交辦單 {closedCount} 張" : "，進行中交辦單不代為結案（暫停）"),
            targetKind: "issue_owner",
            targetId: $"{sourceName}/{eventId}",
            detail: new
            {
                SourceName = sourceName, EventId = eventId, interval.From, interval.To, Reason = reason,
                Extended = extended, request.ExistingOrders, ClosedWorkOrders = closedCount,
                closeOutcome?.FailedWorkOrderId, closeOutcome?.NotProcessed
            });

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var dto = ToDto(saved, usersById, RecentAggregatesByKey());
        dto.MuteCloseOutcome = closeOutcome;
        return dto;
    }

    /// <summary>
    /// 解除靜音：今天所在區間若今天才開始→刪除該區間，否則迄日改為昨天（保留已靜音過的日子）。
    /// 沒有進行中的靜音→Validation。
    /// </summary>
    public IssueOwnerDto ClearMute(string source, int eventId)
    {
        EnsureMaintain();

        var today = DateTime.Today;
        var existing = _issueOwners.Get(source, eventId);
        var current = existing == null ? null : IssueProfile.CurrentMute(existing, today);
        if (existing == null || current == null)
            throw DomainException.Validation("這個問題目前沒有進行中的靜音。");

        var removed = current.From.Date == today;
        var mutes = existing.Mutes.Where(m => !ReferenceEquals(m, current)).ToList();
        if (!removed)
        {
            mutes.Add(new MuteInterval
            {
                From = current.From, To = today.AddDays(-1), Reason = current.Reason,
                ById = current.ById, ByAccount = current.ByAccount, At = current.At
            });
        }
        existing.Mutes = mutes;
        existing.UpdatedByAccount = _currentUser.Account;

        var saved = _issueOwners.Upsert(existing);

        _audit.Record(
            action: AuditActions.IssueUnmute,
            summary: $"解除問題「{existing.SourceName} {existing.EventId}」的靜音" +
                     (removed
                         ? $"（原區間 {current.From:yyyy-MM-dd}～{current.To:yyyy-MM-dd} 今天才開始，整段刪除）"
                         : $"（區間改為 {current.From:yyyy-MM-dd}～{today.AddDays(-1):yyyy-MM-dd}）"),
            targetKind: "issue_owner",
            targetId: $"{existing.SourceName}/{existing.EventId}");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        return ToDto(saved, usersById, RecentAggregatesByKey());
    }

    public void Delete(string source, int eventId)
    {
        var existing = _issueOwners.Get(source, eventId)
                        ?? throw DomainException.NotFound("找不到這筆問題負責人規則。");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var ownerNames = existing.OwnerUserIds.Select(id => usersById.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList();

        _issueOwners.Delete(source, eventId);
        _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.IssueOwnerDelete,
            summary: $"刪除問題「{existing.SourceName} {existing.EventId}」的負責人規則（原負責人：{NameFormat.Join(ownerNames)}）",
            targetKind: "issue_owner",
            targetId: $"{existing.SourceName}/{existing.EventId}");
    }

    private Dictionary<(string SourceUpper, int EventId), (int HostCount, DateTime LastSeen)> RecentAggregatesByKey()
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(RecentIssueWindowDays - 1));
        // 靜音不排除：靜音中的問題要選得到、看得到近況，才能解除
        return _issueAggregates.Aggregate(IssueExclusion.None, from, to, null)
            .GroupBy(a => IssueProfile.KeyOf(a.Source, a.EventId))
            .ToDictionary(
                g => g.Key,
                g => (HostCount: g.Max(x => x.HostCount), LastSeen: g.Max(x => x.LastSeen)));
    }

    private IssueOwnerDto ToDto(
        IssueProfile rule, Dictionary<long, WebUser> usersById,
        Dictionary<(string SourceUpper, int EventId), (int HostCount, DateTime LastSeen)> recentByKey)
    {
        var recent = recentByKey.TryGetValue(IssueProfile.KeyOf(rule.SourceName, rule.EventId), out var r) ? r : ((int, DateTime)?)null;

        return new IssueOwnerDto
        {
            SourceName = rule.SourceName,
            EventId = rule.EventId,
            DisplayLabel = FormatDisplayLabel(rule.SourceName, rule.EventId),
            OwnerUserIds = rule.OwnerUserIds,
            OwnerNames = rule.OwnerUserIds
                .Select(id => usersById.TryGetValue(id, out var u) ? _displayNameService.WithAccount(u.DisplayName, u.Account) : $"(已刪除:{id})")
                .ToList(),
            Note = rule.Note,
            UpdatedAt = rule.UpdatedAt,
            UpdatedByAccount = rule.UpdatedByAccount,
            UpdatedByDisplayName = string.IsNullOrEmpty(rule.UpdatedByAccount)
                ? null
                : _users.FindByAccount(rule.UpdatedByAccount)?.DisplayName,
            RecentHostCount = recent?.Item1,
            RecentLastSeen = recent?.Item2,
            ConclusionStatus = rule.ConclusionStatus,
            ConclusionStatusText = rule.ConclusionStatus == null ? null : HandlingTextHelpers.IssueStatusText(rule.ConclusionStatus),
            ConclusionNote = rule.ConclusionStatus == null ? null : rule.ConclusionNote,
            ConcludedAt = rule.ConcludedAt,
            ConcludedByAccount = rule.ConcludedByAccount,
            ConcludedByDisplayName = string.IsNullOrEmpty(rule.ConcludedByAccount)
                ? null
                : _users.FindByAccount(rule.ConcludedByAccount)?.DisplayName,
            AutoApply = rule.AutoApply,
            CurrentMute = IssueProfile.CurrentMute(rule, DateTime.Today) is { } current ? ToMuteDto(current) : null,
            MuteHistory = rule.Mutes
                .OrderByDescending(m => m.From).ThenByDescending(m => m.At)
                .Take(MuteHistoryLimit)
                .Select(ToMuteDto)
                .ToList()
        };
    }

    private static IssueMuteDto ToMuteDto(MuteInterval m) => new()
    {
        From = m.From, To = m.To, Reason = m.Reason, ByAccount = m.ByAccount, At = m.At
    };

    /// <summary>
    /// 顯示用的問題標籤：Windows 顯示「{Source} ({EventId})」；Linux（EventId 恆為 0）只顯示「{Source}」，
    /// 絕不顯示無意義的「(0)」。刻意不附規則 key——問題設定的鍵是 (Source, 0)，涵蓋該來源的
    /// 全部規則，任選其中一個 key 掛上去語意是錯的（且候選清單與已指派清單會長得不一樣）。
    /// </summary>
    public static string FormatDisplayLabel(string source, int eventId) =>
        eventId == 0 ? source : $"{source} ({eventId})";
}
