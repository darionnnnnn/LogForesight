using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 處理人回覆交辦單（/api/work-orders/{id}/reply、/api/work-orders/reply-many）。
///
/// **只准該單處理人本人回覆，管理者也擋**：回覆是「處理人替自己手上的工作交代結果」，
/// 管理者要改變成員狀態有正規路徑——代為結案或改派（會留下各自的事件與稽核），
/// 不從這裡以處理人名義代答——全站同一條規則：回覆一律是處理人本人的名義。
///
/// 多單回覆**先全部檢查再寫**：任一張不存在、不是本人、已結案，整筆擋下、零寫入——
/// 檢查通過後逐張寫入（不做整批單一交易：協調層不持有連線）；寫入中途某張失敗時停在那張，
/// 已成功的單照樣逐張稽核、上報通知，回應列出成功／失敗／未處理，讓前端保留勾選重送。
///
/// 寫入一律走 <see cref="WorkOrderCoordinator.Reply"/>；這裡只做授權、狀態驗證、上報通知、稽核。
/// </summary>
public class WorkOrderReplyService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private const string TargetKind = "work_order";
    private const int MemberPageSize = 500;

    private readonly WorkOrderCoordinator _coordinator;
    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    /// <summary>
    /// 上報通知。既有 <see cref="IssueHandlingCommandService"/> 以可選參數注入（測試組裝可不給），
    /// 本服務依規格不新增可選相依，改為必要相依——production DI 本來就註冊為 Singleton。
    /// </summary>
    private readonly MailNotificationService _mail;

    public WorkOrderReplyService(
        WorkOrderCoordinator coordinator,
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        ICurrentUser currentUser,
        IAuditService audit,
        MailNotificationService mail)
    {
        _coordinator = coordinator;
        _orders = orders;
        _cases = cases;
        _currentUser = currentUser;
        _audit = audit;
        _mail = mail;
    }

    public WorkOrderReplyResultDto Reply(long id, WorkOrderReplyRequest req)
    {
        var order = RequireOwnActive(id);
        ValidateStatus(req.Status, req.DueDate, req.Note);

        var actor = NewActor();
        var newlyEscalated = NewlyEscalatedHosts(id, req.CaseIds, req.Status);
        var result = Guard(() => _coordinator.Reply(id, req.CaseIds, req.Status, req.Note, req.DueDate, actor));

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: $"回覆交辦單 #{id}「{order.IssueLabel}」：{result.Cases} 台標為「{HandlingTextHelpers.IssueStatusText(req.Status)}」",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, req.CaseIds, req.Status, req.Note, req.DueDate, result.Cases, result.WorkOrderClosed });

        NotifyEscalation(req.Status, order.IssueLabel, newlyEscalated, req.Note);

        return new WorkOrderReplyResultDto
        {
            WorkOrderId = id,
            Cases = result.Cases,
            WorkOrderClosed = result.WorkOrderClosed,
            DaySyncPendingCases = result.DaySync.PendingCases
        };
    }

    public WorkOrderReplyManyResultDto ReplyMany(WorkOrderReplyManyRequest req)
    {
        var ids = req.WorkOrderIds.Distinct().ToList();
        if (ids.Count == 0)
            throw DomainException.Validation("請至少選一張交辦單。");

        // 先全部檢查（存在、本人、進行中、狀態），任一不合整筆擋下——零寫入
        var orders = ids.Select(RequireOwnActive).ToList();
        ValidateStatus(req.Status, req.DueDate, req.Note);

        var actor = NewActor();
        var statusText = HandlingTextHelpers.IssueStatusText(req.Status);

        var succeeded = new List<WorkOrder>();
        var newlyEscalated = new List<string>();
        long? failedId = null;
        string? failureMessage = null;
        var cases = 0;
        var closed = 0;
        var pending = 0;
        foreach (var order in orders)
        {
            WorkOrderReplyResult result;
            List<string> escalatedHosts;
            try
            {
                escalatedHosts = NewlyEscalatedHosts(order.WorkOrderId, null, req.Status);
                result = Guard(() => _coordinator.Reply(order.WorkOrderId, null, req.Status, req.Note, req.DueDate, actor));
            }
            catch (Exception ex)
            {
                // 中途失敗：停在這張，已寫入的單照常有稽核與通知，回應交代成功／失敗／未處理
                failedId = order.WorkOrderId;
                failureMessage = ex is DomainException ? ex.Message : "回覆時發生未預期的錯誤。";
                if (ex is not DomainException)
                    Log.Error(ex, "多單回覆在交辦單 #{0} 發生未預期錯誤（前面的單已寫入）", order.WorkOrderId);
                break;
            }

            succeeded.Add(order);
            newlyEscalated.AddRange(escalatedHosts);
            cases += result.Cases;
            if (result.WorkOrderClosed) closed++;
            pending += result.DaySync.PendingCases;

            // 逐張稽核：寫入成功就立即留紀錄，後面的單失敗也不會讓這張沒有稽核
            _audit.Record(
                action: AuditActions.HandlingStatus,
                summary: $"回覆交辦單 #{order.WorkOrderId}：{result.Cases} 台標為「{statusText}」",
                targetKind: TargetKind,
                targetId: order.WorkOrderId.ToString(),
                detail: new { order.WorkOrderId, BatchWorkOrderIds = ids, req.Status, req.Note, req.DueDate, result.Cases, result.WorkOrderClosed });
        }

        // 整批一封（不逐張各寄一封轟炸 admin），只涵蓋已成功的單
        var issueLabel = succeeded.Count == 1 ? succeeded[0].IssueLabel : $"{succeeded.Count} 張交辦單";
        NotifyEscalation(req.Status, issueLabel, newlyEscalated, req.Note);

        return new WorkOrderReplyManyResultDto
        {
            WorkOrders = succeeded.Count,
            Cases = cases,
            ClosedWorkOrders = closed,
            DaySyncPendingCases = pending,
            Succeeded = succeeded.Select(o => o.WorkOrderId).ToList(),
            FailedWorkOrderId = failedId,
            FailureMessage = failureMessage,
            NotProcessed = failedId == null
                ? new List<long>()
                : orders.SkipWhile(o => o.WorkOrderId != failedId).Skip(1).Select(o => o.WorkOrderId).ToList()
        };
    }

    // ── 內部 ────────────────────────────────────────────────────────────────

    /// <summary>單不存在 404；不是本人 403（管理者也擋）；已結案 400</summary>
    private WorkOrder RequireOwnActive(long id)
    {
        var order = _orders.Get(id) ?? throw DomainException.NotFound($"找不到交辦單 #{id}。");
        if (order.HandlerId != _currentUser.UserId)
            throw DomainException.Forbidden($"交辦單 #{id} 不是指派給您的，只有處理人本人可以回覆。");
        if (order.ClosedAt != null)
            throw DomainException.Validation($"交辦單 #{id} 已結案，無法回覆。");
        return order;
    }

    /// <summary>狀態驗證走共用規則；open＝清除</summary>
    private static void ValidateStatus(string status, DateTime? dueDate, string? note) =>
        IssueStatusValidation.Validate(status, dueDate, clearing: status == IssueHandlingStatuses.Open, note);

    /// <summary>
    /// 寫入前讀成員舊狀態，只算「新」轉入 escalated 的主機（原本就是 escalated 的不重複通知）。
    /// 狀態不是 escalated 時不讀——省掉與通知無關的成員分頁查詢。
    /// </summary>
    private List<string> NewlyEscalatedHosts(long workOrderId, IReadOnlyCollection<string>? caseIds, string status)
    {
        if (status != IssueHandlingStatuses.Escalated) return new List<string>();

        var selected = caseIds == null || caseIds.Count == 0 ? null : caseIds.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        for (var skip = 0; ; skip += MemberPageSize)
        {
            var page = _cases.GetByWorkOrder(workOrderId, skip, MemberPageSize);
            result.AddRange(page
                .Where(c => c.ClosedAt == null && c.Status != IssueHandlingStatuses.Escalated
                            && (selected == null || selected.Contains(c.CaseId)))
                .Select(c => c.HostName));
            if (page.Count < MemberPageSize) break;
        }
        return result;
    }

    /// <summary>上報通知 fire-and-forget（NotifyEscalationAsync 內部 try/catch 到底、永不拋出）</summary>
    private void NotifyEscalation(string status, string issueLabel, List<string> newlyEscalatedHosts, string? note)
    {
        if (status != IssueHandlingStatuses.Escalated || newlyEscalatedHosts.Count == 0) return;
        _ = _mail.NotifyEscalationAsync(new EscalationNotice(
            issueLabel,
            newlyEscalatedHosts.Count == 1 ? newlyEscalatedHosts[0] : $"{newlyEscalatedHosts.Count} 台主機",
            _currentUser.Account, note));
    }

    private WorkOrderActor NewActor() => new()
    {
        ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
        ActorAccount = _currentUser.Account,
        OccurredAt = DateTime.Now
    };

    /// <summary>協調器的資料驗證例外（成員不屬於本單、已結案等）轉成 400</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
    }
}
