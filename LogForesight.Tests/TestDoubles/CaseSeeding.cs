using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 測試前置用的「建案」入口。正式碼的建案路徑一律經 <see cref="WorkOrderCoordinator"/>
/// （CLAUDE.md 紅線），測試也走同一條，才不會在前置造出正式環境不可能出現的無單案件。
/// </summary>
internal sealed class WorkOrderCaseSeeder
{
    private readonly WorkOrderCoordinator _coordinator;

    public WorkOrderCaseSeeder(
        IIssueCaseStore cases, IIssueHandlingStore issueHandlings, IRecordHandlingStore handlingLog,
        IHostStore hosts, IssueCaseCoordinator caseCoordinator, IWorkOrderStore orders)
    {
        _coordinator = new WorkOrderCoordinator(orders, cases, issueHandlings, caseCoordinator, handlingLog, hosts);
    }

    /// <summary>
    /// 指派處理人（建單＋建案＋回溯關聯歷史風險日）。同處理人同問題已有進行中單時併入；
    /// 他人已有進行中案件且 <paramref name="reassignConflicts"/> 為 false 時略過並於 SkippedConflicts 回報。
    /// </summary>
    public WorkOrderMemberOutcome Assign(
        string hostName, string issueKey, string issueLabel, DateTime triggerDate,
        long handlerId, string? note, DateTime? dueDate,
        long? actorId, string actorAccount, DateTime occurredAt,
        string origin = WorkOrderOrigins.Manual, bool reassignConflicts = false)
    {
        var parsed = IssueSignatureKey.TryParseSignature(issueKey)
                     ?? throw new InvalidOperationException($"測試前置：無法解析問題簽章「{issueKey}」。");
        return _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = parsed.Source, EventId = parsed.EventId, IssueLabel = issueLabel,
            HandlerId = handlerId, Origin = origin,
            ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
            Note = note, DueDate = dueDate, ReassignConflicts = reassignConflicts,
            Members = new List<WorkOrderMember>
            {
                new() { HostName = hostName, IssueKey = issueKey, IssueLabel = issueLabel, TriggerDate = triggerDate }
            },
            Actor = new WorkOrderActor { ActorId = actorId, ActorAccount = actorAccount, OccurredAt = occurredAt }
        });
    }
}

/// <summary>
/// 模擬整併前遺留的「沒有交辦單的進行中案件」舊資料：直接寫入案件、觸發日逐日列與歷程，
/// 不經任何 Coordinator。只給「本意就是測舊資料防禦行為」的案例用（掛接須能掛入既有無單案件、
/// 不為它建單）；一般前置請用 <see cref="WorkOrderCaseSeeder"/>。只寫觸發日一天（不回溯歷史風險日）。
/// </summary>
internal static class LegacyCaseSeeder
{
    public static IssueCase Seed(
        IIssueCaseStore cases, IIssueHandlingStore issueHandlings, IRecordHandlingStore handlingLog,
        string hostName, string issueKey, string issueLabel, DateTime triggerDate,
        long handlerId, string? note, DateTime? dueDate,
        long? actorId, string actorAccount, DateTime occurredAt)
    {
        var caseId = Guid.NewGuid().ToString("n");
        issueHandlings.SaveMany(new[]
        {
            new IssueHandling
            {
                HostName = hostName, Date = triggerDate.Date, IssueKey = issueKey,
                Status = IssueHandlingStatuses.InProgress, Note = note, DueDate = dueDate, CaseId = caseId,
                ActorId = actorId, ActorAccount = actorAccount, UpdatedAt = occurredAt
            }
        });
        handlingLog.AppendLog(new RecordHandlingLog
        {
            HostName = hostName, Date = triggerDate.Date, Status = IssueHandlingStatuses.InProgress,
            IssueKey = issueKey, IssueLabel = issueLabel, Note = note,
            ActorId = actorId, ActorAccount = actorAccount,
            Action = HandlingActions.CaseAssign, CreatedAt = occurredAt
        });
        var legacy = new IssueCase
        {
            CaseId = caseId, HostName = hostName, IssueKey = issueKey, IssueLabel = issueLabel,
            Status = IssueHandlingStatuses.InProgress, HandlerId = handlerId, Note = note, DueDate = dueDate,
            FirstLinkedDate = triggerDate.Date, LastLinkedDate = triggerDate.Date,
            CreatedAt = occurredAt, CreatedByAccount = actorAccount, UpdatedAt = occurredAt
        };
        cases.Save(legacy);
        return legacy;
    }
}
