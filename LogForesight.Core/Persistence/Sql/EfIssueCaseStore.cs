using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IIssueCaseStore"/> 的真表實作（↔ lf_issue_cases，
/// docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3／根因 B）。
///
/// **為什麼案件也要一起搬**（它的列數比 issue_handling 少一個量級）：
///   1. <c>GetOpen</c> 是**每次標記都會走**的路徑（IssueCaseCoordinator.SyncStatus 開頭），
///      整份 blob 時每次標記都先反序列化全部案件。
///   2. 夜間掛接（AttachNewDay）過去在迴圈內逐案 <c>Save</c>，每次都是一次整份讀改寫
///      ——6000 台 × 平均 2 個命中案件＝每晚上萬次整份重寫（體檢 S4）。
///   3. 依問題視角逐群組呼叫 <c>GetMany</c>（規劃 N3），1000 種問題就是 1000 次整份讀。
///
/// 同一 (主機, 問題簽章) 同時至多一個進行中案件仍由呼叫端（IssueCaseCoordinator）保證，
/// 與原實作的職責分工一致——store 只管持久化。
/// </summary>
public sealed class EfIssueCaseStore : IIssueCaseStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly Func<LfDbContext> _contextFactory;
    private readonly TimeProvider _clock;

    public EfIssueCaseStore(Func<LfDbContext> contextFactory, TimeProvider? clock = null)
    {
        _contextFactory = contextFactory;
        _clock = clock ?? TimeProvider.System;
    }

    public IssueCase? GetOpen(string hostName, string issueKey)
    {
        var key = HostNameKey.Of(hostName);

        using var ctx = _contextFactory();
        // 先以主機＋進行中狀態縮小結果，再在 C# 比較完整 IssueKey；不能用 UPPER()，
        // 否則 SQLite／SQL Server 的 Unicode 大小寫規則會不一致。成本受該主機進行中案件數限制。
        var row = ctx.IssueCases.AsNoTracking()
            .Where(c => c.HostNameKey == key && c.ClosedAt == null)
            .ToList()
            .Where(c => IssueSignatureKeyComparer.Instance.Equals(c.IssueKey, issueKey))
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.IssueKey, StringComparer.Ordinal)
            .ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .FirstOrDefault();
        return row == null ? null : ToModel(row);
    }

    public List<IssueCase> GetOpenForHost(string hostName)
    {
        var key = HostNameKey.Of(hostName);

        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HostNameKey == key && c.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetMany(IEnumerable<string> hostNames) => ForHosts(hostNames, openOnly: false);

    public List<IssueCase> GetOpenMany(IEnumerable<string> hostNames) => ForHosts(hostNames, openOnly: true);

    private List<IssueCase> ForHosts(IEnumerable<string> hostNames, bool openOnly)
    {
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        if (keys.Count == 0) return new List<IssueCase>();

        using var ctx = _contextFactory();
        var query = ctx.IssueCases.AsNoTracking().Where(c => keys.Contains(c.HostNameKey));
        if (openOnly) query = query.Where(c => c.ClosedAt == null);
        return query.ToList().Select(ToModel).ToList();
    }

    public List<IssueCase> GetOpenByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == userId && c.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == userId)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public bool HasCaseOnHost(long handlerId, string hostName)
    {
        var key = HostNameKey.Of(hostName);
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Any(c => c.HandlerId == handlerId && c.HostNameKey == key && !c.Cancelled);
    }

    public HashSet<string> IssueKeysOnHost(long handlerId, string hostName)
    {
        var key = HostNameKey.Of(hostName);
        using var ctx = _contextFactory();
        var keys = ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == handlerId && c.HostNameKey == key && !c.Cancelled)
            .Select(c => c.IssueKey)
            .ToList();
        return new HashSet<string>(keys, IssueSignatureKeyComparer.Instance);
    }

    public List<string> HostNamesWithCases(long handlerId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == handlerId && !c.Cancelled)
            .Select(c => c.HostName)
            .Distinct()
            .OrderBy(h => h)
            .ToList();
    }

    public List<IssueCase> GetResolvedSince(DateTime since)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.ClosedAt != null && c.ClosedAt >= since && c.Status == IssueHandlingStatuses.Resolved)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public IssueCase? Get(string caseId)
    {
        using var ctx = _contextFactory();
        var row = ctx.IssueCases.AsNoTracking().FirstOrDefault(c => c.CaseId == caseId);
        return row == null ? null : ToModel(row);
    }

    /// <summary>issue_key 超長不寫（截斷會讓鍵對不上或撞鍵）；回傳 true 表示已警告、呼叫端應略過該列。</summary>
    private static bool IssueKeyTooLong(IssueCase issueCase)
    {
        var max = ColumnClamp.For<IssueCaseRow>(nameof(IssueCaseRow.IssueKey));
        if (max is not { } m || issueCase.IssueKey.Length <= m) return false;
        Log.Warn("[SQL] IssueCase 的 issue_key 超過 {Max} 字（{Len}），略過不寫：{Host} {Date:yyyy-MM-dd} {Key}",
            m, issueCase.IssueKey.Length, issueCase.HostName, issueCase.CreatedAt, issueCase.IssueKey[..80]);
        return true;
    }

    public void Save(IssueCase issueCase)
    {
        using var ctx = _contextFactory();
        var row = ctx.IssueCases.FirstOrDefault(c => c.CaseId == issueCase.CaseId);

        if (row == null)
        {
            if (IssueKeyTooLong(issueCase)) return;
            row = new IssueCaseRow
            {
                CaseId = issueCase.CaseId,
                HostName = issueCase.HostName,
                HostNameKey = HostNameKey.Of(issueCase.HostName),
                IssueKey = issueCase.IssueKey,
                IssueLabel = issueCase.IssueLabel,
                CreatedAt = issueCase.CreatedAt,
                CreatedByAccount = issueCase.CreatedByAccount
            };
            ctx.IssueCases.Add(row);
        }

        // 與原 blob 實作逐欄對齊：CaseId／HostName／IssueKey／IssueLabel／CreatedAt／
        // CreatedByAccount 是建案當下的事實，更新時不動
        if (issueCase.PrtgEvidence != null) row.PrtgEvidenceJson ??= JsonSerializer.Serialize(issueCase.PrtgEvidence);
        row.Status = issueCase.Status;
        row.HandlerId = issueCase.HandlerId;
        row.Note = issueCase.Note;
        row.DueDate = issueCase.DueDate;
        row.FirstLinkedDate = issueCase.FirstLinkedDate;
        row.LastLinkedDate = issueCase.LastLinkedDate;
        row.ClosedAt = issueCase.ClosedAt;
        row.UpdatedAt = issueCase.UpdatedAt;
        ApplyMemberColumns(issueCase, row);

        SaveWithConflictTranslation(ctx);
    }

    /// <summary>
    /// 批次寫入（P3 新增，供 <c>IssueCaseCoordinator.AttachNewDay</c> 用）：體檢 S4 指出
    /// 夜間掛接在迴圈內逐案 Save，2000 台每晚約 4000 次整份 blob 讀改寫。改真表之後
    /// 逐案 Save 已經便宜很多，但每次仍是一趟 DB 往返——批次入口讓一晚的推進併成一次交易。
    /// </summary>
    public void SaveMany(IEnumerable<IssueCase> cases)
    {
        var list = cases.ToList();
        if (list.Count == 0) return;

        using var ctx = _contextFactory();
        var ids = list.Select(c => c.CaseId).ToList();
        var existing = ctx.IssueCases.Where(c => ids.Contains(c.CaseId)).ToDictionary(c => c.CaseId);

        foreach (var issueCase in list)
        {
            if (!existing.TryGetValue(issueCase.CaseId, out var row))
            {
                if (IssueKeyTooLong(issueCase)) continue;
                row = new IssueCaseRow
                {
                    CaseId = issueCase.CaseId,
                    HostName = issueCase.HostName,
                    HostNameKey = HostNameKey.Of(issueCase.HostName),
                    IssueKey = issueCase.IssueKey,
                    IssueLabel = issueCase.IssueLabel,
                    CreatedAt = issueCase.CreatedAt,
                    CreatedByAccount = issueCase.CreatedByAccount
                };
                ctx.IssueCases.Add(row);
                existing[issueCase.CaseId] = row;
            }

            if (issueCase.PrtgEvidence != null) row.PrtgEvidenceJson ??= JsonSerializer.Serialize(issueCase.PrtgEvidence);
            row.Status = issueCase.Status;
            row.HandlerId = issueCase.HandlerId;
            row.Note = issueCase.Note;
            row.DueDate = issueCase.DueDate;
            row.FirstLinkedDate = issueCase.FirstLinkedDate;
            row.LastLinkedDate = issueCase.LastLinkedDate;
            row.ClosedAt = issueCase.ClosedAt;
            row.UpdatedAt = issueCase.UpdatedAt;
            ApplyMemberColumns(issueCase, row);
        }

        SaveWithConflictTranslation(ctx);
    }

    /// <summary>
    /// 交辦單相關欄位的寫入。source_name／source_key／event_id 一律由 issue_key 算出，呼叫端不填：
    /// issue_key 建案後不變，所以只在尚未解析（source_key 為 null）時算；解析失敗三欄維持 null，
    /// 交給背景整併標成 ''（見 <see cref="WorkOrderBackfiller"/>）。
    /// </summary>
    private static void ApplyMemberColumns(IssueCase issueCase, IssueCaseRow row)
    {
        // 連結後只會換成另一張單、永遠不會變回 null：背景整併以 ExecuteUpdate 寫入連結且刻意不動 updated_at，
        // 呼叫端手上若是整併前讀到的舊模型（WorkOrderId=null），整欄覆寫會把連結靜默抹掉
        if (issueCase.WorkOrderId != null) row.WorkOrderId = issueCase.WorkOrderId;
        row.DaySyncPending = issueCase.DaySyncPending;
        row.DaySyncIntent = issueCase.DaySyncIntent == null ? null : SerializeIntent(issueCase.DaySyncIntent);
        row.Cancelled = issueCase.Cancelled;
        row.BlocksRedispatch = issueCase.BlocksRedispatch;
        row.EscalatedAt = issueCase.EscalatedAt;

        if (row.SourceKey != null) return;
        var parsed = EfWorkOrderStore.ParseIssueColumns(row.IssueKey);
        if (parsed == null) return;

        row.SourceName = parsed.Value.SourceName;
        row.SourceKey = parsed.Value.SourceKey;
        row.EventId = parsed.Value.EventId;
    }

    /// <summary>主機清單分批查詢的批次大小：避免 IN 清單過長（SQL Server 參數上限 2100）</summary>
    private const int HostBatchSize = 500;

    public List<(string HostNameKey, string IssueKey)> GetOpenKeys()
    {
        using var ctx = _contextFactory();

        // 單句、只選兩欄：待派清單要扣掉「已有進行中案件」的全部缺口，不把整列案件拉回來
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.ClosedAt == null)
            .Select(c => new { c.HostNameKey, c.IssueKey })
            .AsEnumerable()
            .Select(x => (x.HostNameKey, x.IssueKey))
            .ToList();
    }

    public List<IssueCase> GetOpenByIssue(string source, int eventId)
    {
        var key = EfWorkOrderStore.SourceKeyOf(source);

        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.SourceKey == key && c.EventId == eventId && c.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetOpenMany(IEnumerable<string> hostNames, string source, int eventId)
    {
        var sourceKey = EfWorkOrderStore.SourceKeyOf(source);
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        var result = new List<IssueCase>();
        if (keys.Count == 0) return result;

        using var ctx = _contextFactory();
        foreach (var batch in keys.Chunk(HostBatchSize))
        {
            result.AddRange(ctx.IssueCases.AsNoTracking()
                .Where(c => batch.Contains(c.HostNameKey) && c.SourceKey == sourceKey && c.EventId == eventId && c.ClosedAt == null)
                .ToList()
                .Select(ToModel));
        }
        return result;
    }

    public List<IssueCase> GetByWorkOrder(long workOrderId, int skip, int take)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.WorkOrderId == workOrderId)
            .OrderBy(c => c.HostNameKey)
            .ThenBy(c => c.CaseId)
            .Skip(skip)
            .Take(take)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public int CountByWorkOrder(long workOrderId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.Count(c => c.WorkOrderId == workOrderId);
    }

    /// <summary>進行中成員精簡查詢的 workOrderIds 分批大小（避免 IN 清單過長）</summary>
    private const int WorkOrderBatchSize = 500;

    /// <summary>歷史查詢的 EventId 預篩上限：超過時不在 SQL 端篩（參數太多），改靠主機批次與記憶體端的精確比對</summary>
    private const int HistoryEventIdPrefilterLimit = 100;

    /// <summary>進行中成員（精簡欄位）：null＝全部交辦單；指定單號時依 500 一批，查詢次數隨單數而非成員數增長</summary>
    public List<IssueCase> GetActiveMemberSummaries(IReadOnlyCollection<long>? workOrderIds)
    {
        var result = new List<IssueCase>();
        using var ctx = _contextFactory();
        if (workOrderIds == null)
        {
            result.AddRange(BuildActiveMemberSummaryQuery(ctx, null).ToList().Select(ToSlimModel));
            return result;
        }

        foreach (var batch in workOrderIds.Distinct().Chunk(WorkOrderBatchSize))
            result.AddRange(BuildActiveMemberSummaryQuery(ctx, batch).ToList().Select(ToSlimModel));
        return result;
    }

    /// <summary>指定交辦單的全部成員（含已結案，精簡欄位）：依 500 張一批，查詢次數隨單數而非成員數增長</summary>
    public List<IssueCase> GetMemberSummaries(IReadOnlyCollection<long> workOrderIds)
    {
        var result = new List<IssueCase>();
        if (workOrderIds.Count == 0) return result;

        using var ctx = _contextFactory();
        foreach (var batch in workOrderIds.Distinct().Chunk(WorkOrderBatchSize))
            result.AddRange(BuildMemberSummaryQuery(ctx, batch).ToList().Select(ToSlimModel));
        return result;
    }

    /// <summary>全部成員精簡查詢本體（抽出供 SQL 翻譯測試）：投影同 <see cref="BuildActiveMemberSummaryQuery"/>，不限進行中</summary>
    internal static IQueryable<IssueCaseSlimRow> BuildMemberSummaryQuery(LfDbContext ctx, long[] workOrderIds) =>
        SlimProjection(ctx.IssueCases.AsNoTracking().Where(c => c.WorkOrderId != null && workOrderIds.Contains(c.WorkOrderId!.Value)));

    /// <summary>進行中成員精簡查詢本體（抽出供 SQL 翻譯測試）；只投影統計與復發判定要的欄位，不讀 PRTG 證據與逐日意圖</summary>
    internal static IQueryable<IssueCaseSlimRow> BuildActiveMemberSummaryQuery(LfDbContext ctx, long[]? workOrderIds)
    {
        var query = ctx.IssueCases.AsNoTracking().Where(c => c.ClosedAt == null && c.WorkOrderId != null);
        if (workOrderIds != null) query = query.Where(c => workOrderIds.Contains(c.WorkOrderId!.Value));
        return SlimProjection(query);
    }

    private static IQueryable<IssueCaseSlimRow> SlimProjection(IQueryable<IssueCaseRow> query) =>
        query.Select(c => new IssueCaseSlimRow
        {
            CaseId = c.CaseId, HostName = c.HostName, IssueKey = c.IssueKey, IssueLabel = c.IssueLabel, Status = c.Status,
            HandlerId = c.HandlerId, Note = c.Note, DueDate = c.DueDate, CreatedAt = c.CreatedAt, ClosedAt = c.ClosedAt,
            UpdatedAt = c.UpdatedAt, WorkOrderId = c.WorkOrderId, SourceName = c.SourceName, EventId = c.EventId,
            Cancelled = c.Cancelled, EscalatedAt = c.EscalatedAt, BlocksRedispatch = c.BlocksRedispatch
        });

    /// <summary>
    /// 復發判定的前案候選：已結案、未取消、指定主機與問題的案件。主機依 500 一批（host_name_key IN）；
    /// EventId 在 SQL 端預篩（不超過 <see cref="HistoryEventIdPrefilterLimit"/> 個時），來源以 source_key 在記憶體端精確比對
    /// （不對欄位套函式，兩個 provider 行為一致）。
    /// </summary>
    public List<IssueCase> GetClosedHistory(IEnumerable<string> hostNames, IReadOnlyCollection<(string Source, int EventId)> issues, DateTime? closedSince)
    {
        var result = new List<IssueCase>();
        var keys = hostNames.Select(HostNameKey.Of).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0 || issues.Count == 0) return result;

        var wanted = issues.Select(i => (SourceKey: WorkOrderIssueKey.SourceKeyOf(i.Source), i.EventId)).ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).Distinct().ToList();
        var prefilter = eventIds.Count <= HistoryEventIdPrefilterLimit ? eventIds.ToArray() : null;

        using var ctx = _contextFactory();
        foreach (var batch in keys.Chunk(HostBatchSize))
        {
            result.AddRange(BuildClosedHistoryQuery(ctx, batch, prefilter, closedSince).ToList()
                .Where(r => r.SourceName != null && r.EventId != null
                            && wanted.Contains((WorkOrderIssueKey.SourceKeyOf(r.SourceName), r.EventId.Value)))
                .Select(ToSlimModel));
        }
        return result;
    }

    /// <summary>前案候選查詢本體（抽出供 SQL 翻譯測試）</summary>
    internal static IQueryable<IssueCaseSlimRow> BuildClosedHistoryQuery(LfDbContext ctx, string[] hostNameKeys, int[]? eventIds, DateTime? closedSince)
    {
        var query = ctx.IssueCases.AsNoTracking()
            .Where(c => hostNameKeys.Contains(c.HostNameKey) && c.ClosedAt != null && !c.Cancelled);
        if (eventIds != null) query = query.Where(c => c.EventId != null && eventIds.Contains(c.EventId.Value));
        if (closedSince != null) query = query.Where(c => c.ClosedAt >= closedSince);
        return query.Select(c => new IssueCaseSlimRow
        {
            CaseId = c.CaseId, HostName = c.HostName, IssueKey = c.IssueKey, IssueLabel = c.IssueLabel, Status = c.Status,
            HandlerId = c.HandlerId, Note = c.Note, DueDate = c.DueDate, CreatedAt = c.CreatedAt, ClosedAt = c.ClosedAt,
            UpdatedAt = c.UpdatedAt, WorkOrderId = c.WorkOrderId, SourceName = c.SourceName, EventId = c.EventId,
            Cancelled = c.Cancelled, EscalatedAt = c.EscalatedAt, BlocksRedispatch = c.BlocksRedispatch
        });
    }

    /// <summary>每張單不重複的主機數：單一 GROUP BY＋COUNT(DISTINCT host_name_key)，依單號 500 一批</summary>
    public Dictionary<long, int> CountDistinctHosts(IReadOnlyCollection<long> workOrderIds)
    {
        var result = new Dictionary<long, int>();
        var ids = workOrderIds.Distinct().ToList();
        if (ids.Count == 0) return result;

        using var ctx = _contextFactory();
        foreach (var batch in ids.Chunk(WorkOrderBatchSize))
            foreach (var row in BuildDistinctHostsQuery(ctx, batch).ToList())
                result[row.WorkOrderId] = row.Hosts;
        return result;
    }

    /// <summary>不重複主機數的查詢本體（抽出供 SQL 翻譯測試）</summary>
    internal static IQueryable<DistinctHostRow> BuildDistinctHostsQuery(LfDbContext ctx, long[] workOrderIds) =>
        ctx.IssueCases.AsNoTracking()
            .Where(c => c.WorkOrderId != null && workOrderIds.Contains(c.WorkOrderId.Value))
            .GroupBy(c => c.WorkOrderId!.Value)
            .Select(g => new DistinctHostRow { WorkOrderId = g.Key, Hosts = g.Select(c => c.HostNameKey).Distinct().Count() });

    internal sealed class DistinctHostRow
    {
        public long WorkOrderId { get; init; }
        public int Hosts { get; init; }
    }

    /// <summary>精簡投影的列（<see cref="GetActiveMemberSummaries"/>、<see cref="GetClosedHistory"/> 共用）</summary>
    internal sealed class IssueCaseSlimRow
    {
        public string CaseId { get; init; } = string.Empty;
        public string HostName { get; init; } = string.Empty;
        public string IssueKey { get; init; } = string.Empty;
        public string IssueLabel { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public long? HandlerId { get; init; }
        public string? Note { get; init; }
        public DateTime? DueDate { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ClosedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public long? WorkOrderId { get; init; }
        public string? SourceName { get; init; }
        public int? EventId { get; init; }
        public bool Cancelled { get; init; }
        public DateTime? EscalatedAt { get; init; }
        public bool? BlocksRedispatch { get; init; }
    }

    private static IssueCase ToSlimModel(IssueCaseSlimRow row) => new()
    {
        CaseId = row.CaseId, HostName = row.HostName, IssueKey = row.IssueKey, IssueLabel = row.IssueLabel, Status = row.Status,
        HandlerId = row.HandlerId, Note = row.Note, DueDate = row.DueDate, CreatedAt = row.CreatedAt, ClosedAt = row.ClosedAt,
        UpdatedAt = row.UpdatedAt, WorkOrderId = row.WorkOrderId, SourceName = row.SourceName, EventId = row.EventId,
        Cancelled = row.Cancelled, EscalatedAt = row.EscalatedAt, BlocksRedispatch = row.BlocksRedispatch
    };

    /// <summary>
    /// 成員分頁查詢。不限主機時總數與本頁各一句 SQL；限定主機時主機名清單可能超過一批（500），
    /// 分批 IN 無法在 SQL 端跨批分頁，所以各批只取（host_name_key, case_id）兩欄、在記憶體合併排序後，
    /// 再以本頁 case_id（≤200）取整列——查詢次數＝批數＋1，隨主機名數而非成員數增長。
    /// </summary>
    public (List<IssueCase> Items, int Total) QueryMembers(WorkOrderMemberQuery q)
    {
        WorkOrderQueries.Validate(q);
        var skip = (q.Page - 1) * q.PageSize;

        using var ctx = _contextFactory();

        if (q.HostNameKeys == null)
        {
            var all = BuildMemberFilterQuery(ctx, q.WorkOrderId, q.Status, _clock.LocalToday());
            var total = all.Count();
            // 「全部」同主機的新舊案件相鄰、新的在前（看得出同一台反覆出現）；其餘篩選維持主機、案件編號
            var ordered = q.Status == WorkOrderQueries.StatusAll
                ? all.OrderBy(c => c.HostNameKey).ThenByDescending(c => c.CreatedAt).ThenBy(c => c.CaseId)
                : all.OrderBy(c => c.HostNameKey).ThenBy(c => c.CaseId);
            var rows = ordered.Skip(skip).Take(q.PageSize).ToList();
            return (rows.Select(ToModel).ToList(), total);
        }

        var keys = q.HostNameKeys.Select(HostNameKey.Of).Distinct().ToList();
        var matched = new List<(string HostNameKey, string CaseId, DateTime CreatedAt)>();
        foreach (var batch in keys.Chunk(HostBatchSize))
        {
            matched.AddRange(BuildMemberHostQuery(ctx, q.WorkOrderId, q.Status, _clock.LocalToday(), batch)
                .Select(c => new { c.HostNameKey, c.CaseId, c.CreatedAt })
                .ToList()
                .Select(r => (r.HostNameKey, r.CaseId, r.CreatedAt)));
        }

        // 排序規則同上一支分支：「全部」＝主機、建立日新到舊、案件編號；其餘＝主機、案件編號
        var sortedMatches = q.Status == WorkOrderQueries.StatusAll
            ? matched.OrderBy(m => m.HostNameKey, StringComparer.Ordinal)
                .ThenByDescending(m => m.CreatedAt).ThenBy(m => m.CaseId, StringComparer.Ordinal)
            : matched.OrderBy(m => m.HostNameKey, StringComparer.Ordinal)
                .ThenBy(m => m.CaseId, StringComparer.Ordinal);
        var pageIds = sortedMatches
            .Skip(skip)
            .Take(q.PageSize)
            .Select(m => m.CaseId)
            .ToList();
        if (pageIds.Count == 0) return (new List<IssueCase>(), matched.Count);

        var byId = ctx.IssueCases.AsNoTracking()
            .Where(c => pageIds.Contains(c.CaseId))
            .ToList()
            .ToDictionary(c => c.CaseId);
        return (pageIds.Where(byId.ContainsKey).Select(id => ToModel(byId[id])).ToList(), matched.Count);
    }

    /// <summary>成員狀態篩選加主機名鍵（host_name_key）集合：貼上清單與搜尋的主機名篩選；SQL Server 翻譯成 IN 清單</summary>
    internal static IQueryable<IssueCaseRow> BuildMemberHostQuery(LfDbContext ctx, long workOrderId, string status, DateTime today, string[] hostNameKeys) =>
        BuildMemberFilterQuery(ctx, workOrderId, status, today).Where(c => hostNameKeys.Contains(c.HostNameKey));

    /// <summary>成員狀態篩選本體；逾期判準與 <see cref="WorkOrderQueries.IsOverdue"/> 同義</summary>
    internal static IQueryable<IssueCaseRow> BuildMemberFilterQuery(LfDbContext ctx, long workOrderId, string status, DateTime today)
    {
        var query = ctx.IssueCases.AsNoTracking().Where(c => c.WorkOrderId == workOrderId);
        return status switch
        {
            WorkOrderQueries.StatusActive => query.Where(c => c.ClosedAt == null),
            WorkOrderQueries.StatusClosed => query.Where(c => c.ClosedAt != null),
            WorkOrderQueries.StatusEscalated => query.Where(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Escalated),
            WorkOrderQueries.StatusOverdue => query.Where(c => c.ClosedAt == null && c.DueDate != null && c.DueDate < today
                && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing)),
            _ => query
        };
    }

    public List<IssueCase> GetDaySyncPending(int take)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.DaySyncPending)
            .OrderBy(c => c.UpdatedAt)
            .ThenBy(c => c.CaseId)
            .Take(take)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public int CountDaySyncPending()
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.Count(c => c.DaySyncPending);
    }

    /// <summary>
    /// 單句條件式更新：WHERE case_id 與 day_sync_intent 都相符才清，讀與寫之間沒有空窗——
    /// 先讀再比再寫的話，使用者在中間送出的新意圖會被這一趟清掉。刻意不動 updated_at（樂觀鎖欄位）：
    /// 這是背景維護，不該讓使用者手上的案件因此撞 409。
    /// </summary>
    public bool ClearDaySyncPendingIfUnchanged(string caseId, CaseDayIntent intent)
    {
        var json = SerializeIntent(intent);
        using var ctx = _contextFactory();
        return ctx.IssueCases
            .Where(c => c.CaseId == caseId && c.DaySyncIntent == json)
            .ExecuteUpdate(s => s
                .SetProperty(c => c.DaySyncPending, false)
                .SetProperty(c => c.DaySyncIntent, (string?)null)) == 1;
    }

    /// <summary>樂觀鎖衝突轉語意例外（D3）：訊息帶「哪一件案件」，讓 Web 層對應成 409</summary>
    private static void SaveWithConflictTranslation(LfDbContext ctx)
    {
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var what = ex.Entries.FirstOrDefault()?.Entity is IssueCaseRow row
                ? $"「{row.IssueLabel}」（{row.HostName}）的案件"
                : "這件案件";
            throw new ConcurrentUpdateException(what, ex);
        }
    }

    /// <summary>
    /// 清除超過保留天數的案件（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4）。
    ///
    /// **只刪已結案、且結案時間早於 cutoff 的**——這裡刻意不用 <c>LastLinkedDate</c>
    /// 之類的「事件日期」當條件：進行中案件不論多舊都要留著，因為它代表
    /// **還沒處理完**。一個掛了兩年沒人動的案件正是最該被看見的那種，
    /// 用日期把它清掉等於幫忙把爛帳藏起來。
    ///
    /// 結案時間才是案件自己的生命週期終點，也才是「可以忘記了」的正確判準。
    /// 例外：處理人判定的不處理類且仍是該鍵最新案件者不過期（見 <see cref="Expired"/>）。
    /// </summary>
    /// <param name="rules">問題目前規則的嚴重度查詢（由呼叫端傳入；30 天解除判定要用，Core 不複製規則）</param>
    public int Prune(int retentionDays, IssueRuleLookup rules) =>
        Prune(retentionDays, rules, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, IssueRuleLookup rules, int maxRows, int batchSize)
    {
        var cutoff = _clock.LocalToday().AddDays(-retentionDays);
        var released = ReleasedCaseIds(cutoff, _clock.LocalToday(), rules);

        return BatchedPrune.Run<string>(_contextFactory,
            (ctx, take) => Expired(ctx, cutoff, released)
                .OrderBy(c => c.ClosedAt)
                .Select(c => c.CaseId)
                .Take(take)
                .ToList(),
            (ctx, ids) => ctx.IssueCases.Where(c => ids.Contains(c.CaseId)).ExecuteDelete(),
            ctx => Expired(ctx, cutoff, released).Count(),
            "已結案的問題案件", maxRows, batchSize);
    }

    /// <summary>
    /// 已過期、且依 <see cref="DismissalRelease.IsReleased"/>（與派工閘門同一個函式）已解除不再擋的不處理類案件編號。
    /// 先以 SQL 只取「結案早於 cutoff、不處理類、未取消、BlocksRedispatch 為空、結案日不早於生效日」的案件縮小範圍（只是預篩），
    /// 資格與天數的判定一律交給 <see cref="DismissalRelease.IsReleased"/>。
    /// </summary>
    internal List<string> ReleasedCaseIds(DateTime cutoff, DateTime today, IssueRuleLookup rules)
    {
        using var ctx = _contextFactory();
        return ReleaseCandidates(ctx, cutoff)
            .ToList()
            .Select(ToModel)
            .Where(c => DismissalRelease.IsReleased(c, today, rules))
            .Select(c => c.CaseId)
            .ToList();
    }

    /// <summary>解除判定的預篩查詢本體（抽出供 SQL 翻譯測試）</summary>
    internal static IQueryable<IssueCaseRow> ReleaseCandidates(LfDbContext ctx, DateTime cutoff)
    {
        var dismissals = IssueHandlingStatuses.Dismissals;
        var effectiveFrom = DismissalRelease.EffectiveFrom;
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.ClosedAt != null && c.ClosedAt < cutoff && c.ClosedAt >= effectiveFrom
                        && !c.Cancelled && c.BlocksRedispatch == null && dismissals.Contains(c.Status));
    }

    /// <summary>
    /// 可清除的已結案案件：結案早於 cutoff，**但**保留「處理人判定的不處理類且仍是該（主機, 問題）最新案件」——
    /// 這種案件是「不再打擾」閘門與待派頁恢復清單的依據（判定見 DispatchContext.DismissedCases：不處理類狀態、BlocksRedispatch 不是 false、
    /// 該鍵最新的未取消案件），刪掉它問題會重新派工、也從恢復清單消失。以 NOT EXISTS 較新的同鍵未取消且已結案案件表達（較新案件仍進行中不算取代：它之後若被取消，閘門就要靠這筆），兩個後端都能翻譯。
    /// <paramref name="releasedCaseIds"/> 是已滿 30 天解除的案件（<see cref="ReleasedCaseIds"/>）：閘門不再擋它，不再因「仍有效」而保留。
    /// </summary>
    internal static IQueryable<IssueCaseRow> Expired(LfDbContext ctx, DateTime cutoff, IReadOnlyCollection<string> releasedCaseIds)
    {
        var dismissals = IssueHandlingStatuses.Dismissals;
        var released = releasedCaseIds.ToList();
        return ctx.IssueCases.Where(c => c.ClosedAt != null && c.ClosedAt < cutoff
            && !(dismissals.Contains(c.Status) && !c.Cancelled && c.BlocksRedispatch != false
                 && !released.Contains(c.CaseId)
                 && !ctx.IssueCases.Any(n => n.HostNameKey == c.HostNameKey && n.IssueKey == c.IssueKey
                                             && !n.Cancelled && n.ClosedAt != null && n.CreatedAt > c.CreatedAt)));
    }

    private static IssueCase ToModel(IssueCaseRow row) => new()
    {
        CaseId = row.CaseId,
        HostName = row.HostName,
        IssueKey = row.IssueKey,
        PrtgEvidence = row.PrtgEvidenceJson == null ? null : JsonSerializer.Deserialize<PrtgCaseEvidence>(row.PrtgEvidenceJson),
        IssueLabel = row.IssueLabel,
        Status = row.Status,
        HandlerId = row.HandlerId,
        Note = row.Note,
        DueDate = row.DueDate,
        FirstLinkedDate = row.FirstLinkedDate,
        LastLinkedDate = row.LastLinkedDate,
        ClosedAt = row.ClosedAt,
        CreatedAt = row.CreatedAt,
        CreatedByAccount = row.CreatedByAccount,
        UpdatedAt = row.UpdatedAt,
        WorkOrderId = row.WorkOrderId,
        SourceName = row.SourceName,
        EventId = row.EventId,
        DaySyncPending = row.DaySyncPending,
        DaySyncIntent = row.DaySyncIntent == null
            ? null
            : JsonSerializer.Deserialize<CaseDayIntent>(row.DaySyncIntent, LfJsonOptions.Compact),
        Cancelled = row.Cancelled,
        BlocksRedispatch = row.BlocksRedispatch,
        EscalatedAt = row.EscalatedAt
    };

    /// <summary>意圖的序列化單點：寫入與「未變更才清」的比對必須用同一份選項，否則字串永遠不相等</summary>
    internal static string SerializeIntent(CaseDayIntent intent) => JsonSerializer.Serialize(intent, LfJsonOptions.Compact);
}
