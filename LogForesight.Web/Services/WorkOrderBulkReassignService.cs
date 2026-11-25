using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Services;

/// <summary>
/// 批次改派（/api/work-orders/bulk-reassign）：把勾選的進行中交辦單整張改派給同一位處理人。
///
/// **預覽與套用共用同一份分類（<see cref="Classify"/>）**：每張是「新單（原單換人）」「併入哪張」或「略過」，
/// 以及以授與補上幾台——畫面預覽說的數字，套用時照同一份分類逐張執行，結果就是預覽（測試以逐列比對守住）。
///
/// **逐張沿用 <see cref="WorkOrderCommandService.Reassign"/>（內含協調器 <c>WorkOrderCoordinator.Reassign</c>、稽核、通知）**，
/// 這裡不複製任何改派邏輯；單張失敗不影響其他張，逐張回報原因（例外訊息走 <see cref="UserFacingMessage"/>）。
///
/// 分類要模擬「依單號由小到大逐張套用」的狀態：同一批裡同問題有兩張單改派給同一人時，第一張成為他的單、
/// 第二張併入第一張——預覽若各自只看目標處理人「現有的單」就會把第二張誤報成新單。
///
/// 查詢次數不隨張數增長：單一次取多張單、目標處理人的進行中單、進行中成員、可見主機各一次。
/// </summary>
public class WorkOrderBulkReassignService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>一次最多改派幾張單（去重後超過整批被拒）</summary>
    public const int MaxOrders = 100;

    private const string ConcurrentFallback = "這張單剛被其他人更新（例如處理人正在回覆），請重新載入後再改派。";
    private const string FailedFallback = "改派失敗，請稍後再試；原因已寫入記錄檔。";

    private readonly WorkOrderCommandService _commands;
    private readonly WorkOrderCandidateService _candidates;
    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IKnownIssueRuleStore _rules;
    private readonly UserCapabilityResolver _capabilities;
    private readonly IAuditService _audit;
    private readonly IUserDisplayNameService _displayNames;

    public WorkOrderBulkReassignService(
        WorkOrderCommandService commands, WorkOrderCandidateService candidates, IWorkOrderStore orders,
        IIssueCaseStore cases, IHostStore hosts, IUserStore users, IKnownIssueRuleStore rules,
        UserCapabilityResolver capabilities, IAuditService audit, IUserDisplayNameService displayNames)
    {
        _commands = commands;
        _candidates = candidates;
        _orders = orders;
        _cases = cases;
        _hosts = hosts;
        _users = users;
        _rules = rules;
        _capabilities = capabilities;
        _audit = audit;
        _displayNames = displayNames;
    }

    public BulkReassignPreviewDto Preview(BulkReassignRequest request)
    {
        var plan = Classify(request);
        var rows = plan.Rows;
        return new BulkReassignPreviewDto
        {
            HandlerId = plan.Handler.UserId,
            HandlerName = NameOf(plan.Handler),
            HandlerPaused = plan.Handler.DispatchPaused,
            HandlerHasNoEmail = string.IsNullOrWhiteSpace(plan.Handler.Email),
            HandlerCannotHandle = plan.CannotHandle,
            TotalOrders = rows.Count,
            NewCount = rows.Count(r => r.Outcome == BulkReassignOutcomes.New),
            MergeCount = rows.Count(r => r.Outcome == BulkReassignOutcomes.Merge),
            SkipCount = rows.Count(r => r.Outcome == BulkReassignOutcomes.Skip),
            MovedMembers = rows.Where(r => r.Outcome != BulkReassignOutcomes.Skip).Sum(r => r.MemberCount),
            GrantHostCount = plan.GrantHostIds.Count,
            Rows = rows
        };
    }

    public BulkReassignResultDto Apply(BulkReassignRequest request)
    {
        var plan = Classify(request);
        var result = new BulkReassignResultDto
        {
            HandlerId = plan.Handler.UserId,
            HandlerName = NameOf(plan.Handler),
            HandlerHasNoEmail = string.IsNullOrWhiteSpace(plan.Handler.Email),
            HandlerPaused = plan.Handler.DispatchPaused,
            HandlerCannotHandle = plan.CannotHandle
        };

        foreach (var row in plan.Rows)
        {
            var item = new BulkReassignResultRowDto { WorkOrderId = row.WorkOrderId, IssueLabel = row.IssueLabel, Outcome = row.Outcome };
            result.Rows.Add(item);

            if (row.Outcome == BulkReassignOutcomes.Skip)
            {
                item.Status = BulkReassignStatuses.Skipped;
                item.Reason = row.Reason;
                result.SkippedCount++;
                continue;
            }

            try
            {
                var moved = _commands.Reassign(row.WorkOrderId,
                    new ReassignWorkOrderRequest { HandlerId = plan.Handler.UserId, NotifyNow = request.NotifyNow });
                item.Status = BulkReassignStatuses.Ok;
                item.TargetWorkOrderId = moved.TargetWorkOrderId;
                item.MovedCases = moved.MovedCases;
                result.SucceededCount++;
                result.MovedMembers += moved.MovedCases;
                if (moved.MergedIntoExisting) result.MergeCount++;
                else result.NewCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 單張失敗不影響其他張：記完整例外、回中文說明（併發衝突＝處理人正在回覆等）
                Log.Warn(ex, "批次改派：交辦單 {0} 改派失敗", row.WorkOrderId);
                item.Status = BulkReassignStatuses.Failed;
                item.Reason = ex is DbUpdateConcurrencyException
                    ? ConcurrentFallback
                    : UserFacingMessage.For(ex, FailedFallback);
                result.FailedCount++;
            }
        }

        _audit.Record(
            action: AuditActions.WorkOrderReassign,
            summary: $"批次改派 {plan.Rows.Count} 張交辦單給 {result.HandlerName}：成功 {result.SucceededCount} 張" +
                     $"（原單換人 {result.NewCount}、併入既有單 {result.MergeCount}，共 {result.MovedMembers} 台），" +
                     $"略過 {result.SkippedCount} 張、失敗 {result.FailedCount} 張",
            targetKind: "work_order",
            targetId: string.Join(",", plan.Rows.Take(20).Select(r => r.WorkOrderId)),
            detail: new
            {
                NewHandler = plan.Handler.Account,
                Succeeded = result.Rows.Where(r => r.Status == BulkReassignStatuses.Ok).Select(r => r.WorkOrderId).ToList(),
                Skipped = result.Rows.Where(r => r.Status == BulkReassignStatuses.Skipped).Select(r => r.WorkOrderId).ToList(),
                Failed = result.Rows.Where(r => r.Status == BulkReassignStatuses.Failed).Select(r => new { r.WorkOrderId, r.Reason }).ToList()
            },
            result: result.FailedCount > 0 && result.SucceededCount == 0 ? AuditResult.Failed : AuditResult.Ok);

        return result;
    }

    // ── 分類（預覽與套用的唯一一份）─────────────────────────────────────────

    private sealed record Plan(WebUser Handler, bool CannotHandle, List<BulkReassignRowDto> Rows, HashSet<long> GrantHostIds);

    private Plan Classify(BulkReassignRequest request)
    {
        var ids = (request.WorkOrderIds ?? new List<long>()).Distinct().OrderBy(id => id).ToList();
        if (ids.Count == 0)
            throw DomainException.Validation("請選擇要改派的交辦單。");
        if (ids.Count > MaxOrders)
            throw DomainException.Validation($"一次最多改派 {MaxOrders} 張交辦單，目前選了 {ids.Count} 張，請分次處理。");

        var handler = _users.Get(request.HandlerId) ?? throw DomainException.NotFound("找不到指定的處理人。");
        if (!handler.Active)
            throw DomainException.Validation($"{NameOf(handler)} 的帳號已停用，無法指派。");

        var found = _orders.GetMany(ids).ToDictionary(o => o.WorkOrderId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var ruleList = KnownIssueCatalog.ResolveRules(_rules);

        // 目標處理人現有的進行中單（依問題鍵）：模擬逐張套用時，每張「新單」會佔住自己的問題鍵讓後面同問題的單併入它
        var claimed = new Dictionary<(string Source, int EventId), long>();
        foreach (var existing in _orders.GetActiveByHandler(handler.UserId))
        {
            if (existing.SourceName == null || existing.EventId == null) continue;
            claimed.TryAdd((WorkOrderIssueKey.SourceKeyOf(existing.SourceName), existing.EventId.Value), existing.WorkOrderId);
        }

        var activeIds = found.Values.Where(o => o.ClosedAt == null && o.HandlerId != handler.UserId).Select(o => o.WorkOrderId).ToList();
        var membersByOrder = activeIds.Count == 0
            ? new Dictionary<long, List<IssueCase>>()
            : _cases.GetActiveMemberSummaries(activeIds)
                .Where(c => c.WorkOrderId.HasValue)
                .GroupBy(c => c.WorkOrderId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

        var hostIdByKey = _hosts.GetAll()
            .GroupBy(h => HostNameKey.Of(h.HostName))
            .ToDictionary(g => g.Key, g => g.First().HostId);
        var visible = activeIds.Count == 0 ? new HashSet<long>() : _candidates.VisibleHostIdsOf(handler);

        var rows = new List<BulkReassignRowDto>();
        var grantAll = new HashSet<long>();
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var order))
            {
                rows.Add(new BulkReassignRowDto { WorkOrderId = id, Reason = "找不到這張交辦單" });
                continue;
            }

            var row = new BulkReassignRowDto
            {
                WorkOrderId = id,
                IssueLabel = order.DisplayLabel(ruleList),
                CurrentHandlerId = order.HandlerId,
                CurrentHandlerName = users.TryGetValue(order.HandlerId, out var current) ? NameOf(current) : NameFormat.Deleted
            };
            rows.Add(row);

            if (order.ClosedAt != null) { row.Reason = "這張單已結案"; continue; }
            if (order.HandlerId == handler.UserId) { row.Reason = $"已經是 {NameOf(handler)} 的單"; continue; }

            var members = membersByOrder.TryGetValue(id, out var list) ? list : new List<IssueCase>();
            row.MemberCount = members.Count;

            var grantHosts = new HashSet<long>();
            foreach (var member in members)
            {
                if (!hostIdByKey.TryGetValue(HostNameKey.Of(member.HostName), out var hostId)) continue;
                if (!visible.Contains(hostId)) grantHosts.Add(hostId);
            }
            row.GrantHostCount = grantHosts.Count;
            grantAll.UnionWith(grantHosts);

            if (order.SourceName != null && order.EventId != null)
            {
                var key = (WorkOrderIssueKey.SourceKeyOf(order.SourceName), order.EventId.Value);
                if (claimed.TryGetValue(key, out var targetId) && targetId != id)
                {
                    row.Outcome = BulkReassignOutcomes.Merge;
                    row.TargetWorkOrderId = targetId;
                    continue;
                }
                claimed[key] = id;
            }
            row.Outcome = BulkReassignOutcomes.New;
        }

        return new Plan(handler, !_capabilities.Resolve(handler).Contains(Capability.Handle), rows, grantAll);
    }

    private string NameOf(WebUser user) => _displayNames.WithAccount(user.DisplayName, user.Account);
}
