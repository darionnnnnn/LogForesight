using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 既有案件整併成交辦單的背景作業（公開面比照 <see cref="TopIssueBackfiller"/>）。
///
/// **階段一**：補 lf_issue_cases 的 source_name／source_key／event_id（全部案件，含已結案）。
/// 解析失敗的列寫 <c>source_key=''</c>（source_name／event_id 維持 null）——這是「已處理但無法依問題查」
/// 的標記，要與 null（尚未處理）區分，否則每次啟動都會重撈同一批壞鍵。
/// **所有依問題的查詢與分組都把 source_key='' 視同無法依問題查**。
///
/// **階段二**：進行中、有處理人、可依問題查、尚未連結的案件，依（處理人, source_key, event_id）分組，
/// 併入既有進行中交辦單或建一張 backfill 單。案件的 work_order_id 以集合式更新寫入，
/// 不逐筆追蹤實體、不動 updated_at（這是整併，不是使用者操作）。
///
/// 冪等：兩階段的撈取條件本身就排除已處理的列，重跑零新增、中斷後下次續跑。
/// 不掛在啟動路徑上（大 DB 會讓 Windows 服務啟動逾時），由 Web 的背景服務呼叫。
/// </summary>
public sealed class WorkOrderBackfiller
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>每批讀取／更新的案件數</summary>
    private const int BatchSize = 1000;

    /// <summary>掃處理歷程時每批讀的 log 列數</summary>
    private const int LogScanBatchSize = 5000;

    /// <summary>處理歷程在 lf_log_lines 的 log_key（同 StorageBackend.RecordHandlingStore 的 LogStore 鍵）</summary>
    private const string HandlingLogKey = "handling_log";

    private const string BackfillNote = "既有案件整併";

    private readonly Func<LfDbContext> _contextFactory;

    public WorkOrderBackfiller(EfWorkOrderStore store)
    {
        _contextFactory = store.ContextFactory;
    }

    public TopIssueBackfillProgress Progress { get; private set; } = new();

    /// <summary>待處理的案件數（階段一未解析＋階段二未連結；0＝已完成）</summary>
    public int CountPending()
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.Count(c => c.SourceKey == null) + Phase2Candidates(ctx).Count();
    }

    /// <summary>階段二的撈取條件。source_key='' 是解析失敗標記，視同無法依問題查</summary>
    private static IQueryable<IssueCaseRow> Phase2Candidates(LfDbContext ctx) =>
        ctx.IssueCases.Where(c =>
            c.WorkOrderId == null && c.ClosedAt == null && c.HandlerId != null &&
            c.SourceKey != null && c.SourceKey != "");

    public void Run(CancellationToken cancellationToken)
    {
        var pending = CountPending();
        if (pending == 0)
        {
            Progress = new TopIssueBackfillProgress { Completed = true };
            return;
        }

        Log.Info("[SQL] 交辦單整併開始：待處理 {Pending} 件", pending);
        Progress = new TopIssueBackfillProgress { Total = pending };

        var parsed = RunPhase1(cancellationToken, pending);
        var (created, merged, members) = cancellationToken.IsCancellationRequested
            ? (0, 0, 0)
            : RunPhase2(cancellationToken);

        int unlinked;
        using (var ctx = _contextFactory())
        {
            unlinked = ctx.IssueCases.Count(c => c.WorkOrderId == null && c.ClosedAt == null && c.HandlerId == null);
        }

        var remaining = CountPending();
        Progress = new TopIssueBackfillProgress { Total = pending, Done = Math.Max(0, pending - remaining), Completed = remaining == 0 };

        Log.Info("[SQL] 交辦單整併：解析 {Parsed} 件／建單 {Created} 張／併入 {Merged} 張／成員 {Members} 件／未連結（無處理人）{Unlinked} 件",
            parsed, created, merged, members, unlinked);
        if (remaining > 0)
            Log.Info("[SQL] 交辦單整併中止（站台關閉），剩餘 {Remaining} 件，下次啟動接續", remaining);
    }

    /// <summary>階段一：回傳本次解析（含解析失敗標記）的案件數</summary>
    private int RunPhase1(CancellationToken cancellationToken, int pending)
    {
        var done = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var ctx = _contextFactory();
            var batch = ctx.IssueCases.AsNoTracking()
                .Where(c => c.SourceKey == null)
                .OrderBy(c => c.CaseId)
                .Select(c => new { c.CaseId, c.IssueKey })
                .Take(BatchSize)
                .ToList();
            if (batch.Count == 0) break;

            foreach (var group in batch.GroupBy(c => EfWorkOrderStore.ParseIssueColumns(c.IssueKey)))
            {
                var ids = group.Select(c => c.CaseId).ToList();
                if (group.Key == null)
                {
                    ctx.IssueCases.Where(c => ids.Contains(c.CaseId))
                        .ExecuteUpdate(s => s.SetProperty(c => c.SourceKey, ""));
                    continue;
                }

                var (sourceName, sourceKey, eventId) = group.Key.Value;
                ctx.IssueCases.Where(c => ids.Contains(c.CaseId))
                    .ExecuteUpdate(s => s
                        .SetProperty(c => c.SourceName, sourceName)
                        .SetProperty(c => c.SourceKey, sourceKey)
                        .SetProperty(c => c.EventId, (int?)eventId));
            }

            done += batch.Count;
            Progress = new TopIssueBackfillProgress { Total = pending, Done = Math.Min(done, pending) };
        }
        return done;
    }

    /// <summary>
    /// 測試用失敗點：每組在「單列與案件連結已寫入、事件列寫入前」呼叫，用來驗證整組交易原子性。
    /// 正式路徑為 null。
    /// </summary>
    internal Action? BeforeEventWriteForTest;

    /// <summary>最近一次 Run 掃描處理歷程時實際讀取的 log 列數（測試斷言起點之前的列沒被讀）</summary>
    internal int ScannedLogLines { get; private set; }

    private (int Created, int Merged, int Members) RunPhase2(CancellationToken cancellationToken)
    {
        List<CandidateCase> candidates;
        using (var ctx = _contextFactory())
        {
            candidates = Phase2Candidates(ctx).AsNoTracking()
                .Select(c => new CandidateCase(c.CaseId, c.HandlerId!.Value, c.SourceKey!, c.SourceName, c.EventId,
                    c.IssueKey, c.IssueLabel, c.CreatedAt))
                .ToList();
        }
        if (candidates.Count == 0) return (0, 0, 0);

        var groups = candidates
            .Where(c => c.EventId != null && c.SourceName != null)
            .GroupBy(c => (c.HandlerId, c.SourceKey, EventId: c.EventId!.Value))
            .ToList();

        var lastReplies = ScanLastReplies(groups, cancellationToken);

        // SQL Server 後端啟用了連線重試（EnableRetryOnFailure），自開交易必須包在執行策略裡，
        // 否則 EF 直接擲 InvalidOperationException——SQLite 沒有重試策略，測試照樣全綠
        IExecutionStrategy strategy;
        using (var probe = _contextFactory()) strategy = probe.Database.CreateExecutionStrategy();

        int created = 0, merged = 0, members = 0;
        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var first = group.First();
            var caseIds = group.Select(c => c.CaseId).ToList();
            var (handlerId, sourceKey, eventId) = group.Key;
            var isMerge = false;

            // 每組一個交易：單列、案件連結、事件列一起提交——中斷不會留下零成員的單或缺 created 事件的單。
            // 重試時整段重來（新的 context），所以判斷與寫入都在委派內
            strategy.Execute(() =>
            {
                using var ctx = _contextFactory();
                using var tx = ctx.Database.BeginTransaction();

                var existingId = ctx.WorkOrders
                    .Where(w => w.HandlerId == handlerId && w.SourceKey == sourceKey && w.EventId == eventId && w.ClosedAt == null)
                    .Select(w => (long?)w.WorkOrderId)
                    .FirstOrDefault();

                long workOrderId;
                string action;
                if (existingId != null)
                {
                    workOrderId = existingId.Value;
                    action = WorkOrderEventActions.MergedIn;
                }
                else
                {
                    var row = new WorkOrderRow();
                    EfWorkOrderStore.CopyToRow(new WorkOrder
                    {
                        SourceName = first.SourceName,
                        EventId = first.EventId,
                        IssueLabel = first.IssueLabel,
                        HandlerId = handlerId,
                        Origin = WorkOrderOrigins.Backfill,
                        ScopeKind = WorkOrderScopes.Hosts,
                        ScopeGroupIds = new List<long>(),
                        AutoAttach = false,
                        CreatedById = null,
                        CreatedByAccount = AuditActions.SystemAccount,
                        CreatedAt = group.Min(c => c.CreatedAt),
                        LastReplyAt = lastReplies.TryGetValue((handlerId, sourceKey, eventId), out var at) ? at : null,
                        UpdatedAt = DateTime.Now
                    }, row);
                    ctx.WorkOrders.Add(row);
                    ctx.SaveChanges();
                    workOrderId = row.WorkOrderId;
                    action = WorkOrderEventActions.Created;
                }

                foreach (var chunk in caseIds.Chunk(BatchSize))
                {
                    // work_order_id IS NULL 條件：與協調層並行時不覆蓋已被連結的案件；updated_at 不動
                    ctx.IssueCases.Where(c => chunk.Contains(c.CaseId) && c.WorkOrderId == null)
                        .ExecuteUpdate(s => s.SetProperty(c => c.WorkOrderId, (long?)workOrderId));
                }

                BeforeEventWriteForTest?.Invoke();

                ctx.WorkOrderEvents.Add(new WorkOrderEventRow
                {
                    WorkOrderId = workOrderId,
                    Action = action,
                    ActorId = null,
                    ActorAccount = AuditActions.SystemAccount,
                    MemberDelta = caseIds.Count,
                    Note = BackfillNote,
                    CreatedAt = DateTime.Now
                });
                ctx.SaveChanges();
                tx.Commit();
                isMerge = existingId != null;
            });

            if (isMerge) merged++; else created++;
            members += caseIds.Count;
        }

        return (created, merged, members);
    }

    /// <summary>
    /// 每組（處理人, source_key, event_id）在**案件建立之後**，處理人本人對本組任一 issue_key 最近一次處理歷程時間
    /// （動作限 issue_status／issue_status_cleared／case_sync）。
    ///
    /// 處理歷程存在 lf_log_lines（log_key=handling_log）的 JSON 行裡、沒有可查的 actor_id 欄，只能在 C# 解析。
    /// 6000 台規模這是千萬列級，不能從頭掃：先用 (log_key, created_at) 索引找出 created_at ≥ 候選案件最早
    /// CreatedAt 的最小 seq 當起點（created_at 為 null 的舊列 seq 都比有值的小，可安全跳過），從起點往後分批掃。
    /// 查不到起點＝沒有夠新的歷程，不掃、全部 null。
    /// </summary>
    private Dictionary<(long HandlerId, string SourceKey, int EventId), DateTime> ScanLastReplies(
        List<IGrouping<(long HandlerId, string SourceKey, int EventId), CandidateCase>> groups,
        CancellationToken cancellationToken)
    {
        ScannedLogLines = 0;
        var result = new Dictionary<(long, string, int), DateTime>();
        if (groups.Count == 0) return result;

        // (處理人, issue_key) → (所屬組, 該組案件最早 CreatedAt)；同處理人的 issue_key 只會落在一組
        var wanted = new Dictionary<(long, string), ((long, string, int) Group, DateTime Since)>();
        foreach (var group in groups)
        {
            var since = group.Min(c => c.CreatedAt);
            foreach (var key in group.Select(c => c.IssueKey).Distinct())
                wanted[(group.Key.HandlerId, key)] = (group.Key, since);
        }
        var earliest = groups.Min(g => g.Min(c => c.CreatedAt));

        long? startSeq;
        using (var ctx = _contextFactory())
        {
            startSeq = ctx.LogLines
                .Where(l => l.LogKey == HandlingLogKey && l.CreatedAt != null && l.CreatedAt >= earliest)
                .Min(l => (long?)l.Seq);
        }
        if (startSeq == null) return result;

        var nextSeq = startSeq.Value;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var ctx = _contextFactory();
            var lines = ctx.LogLines.AsNoTracking()
                .Where(l => l.LogKey == HandlingLogKey && l.Seq >= nextSeq)
                .OrderBy(l => l.Seq)
                .Select(l => new { l.Seq, l.Line })
                .Take(LogScanBatchSize)
                .ToList();
            if (lines.Count == 0) break;
            nextSeq = lines[^1].Seq + 1;
            ScannedLogLines += lines.Count;

            foreach (var line in lines)
            {
                // 便宜的預篩：三種動作字串都不含就不必解析 JSON
                if (!line.Line.Contains(HandlingActions.IssueStatus) && !line.Line.Contains(HandlingActions.CaseSync)) continue;

                RecordHandlingLog? log;
                try
                {
                    log = JsonSerializer.Deserialize<RecordHandlingLog>(line.Line, LfJsonOptions.Compact);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (log?.ActorId == null || log.IssueKey == null) continue;
                if (log.Action is not (HandlingActions.IssueStatus or HandlingActions.IssueStatusCleared or HandlingActions.CaseSync)) continue;
                if (!wanted.TryGetValue((log.ActorId.Value, log.IssueKey), out var target)) continue;
                // 只計案件建立之後的回覆
                if (log.CreatedAt < target.Since) continue;

                if (!result.TryGetValue(target.Group, out var at) || log.CreatedAt > at) result[target.Group] = log.CreatedAt;
            }
        }

        return result;
    }

    private sealed record CandidateCase(
        string CaseId, long HandlerId, string SourceKey, string? SourceName, int? EventId,
        string IssueKey, string IssueLabel, DateTime CreatedAt);
}
