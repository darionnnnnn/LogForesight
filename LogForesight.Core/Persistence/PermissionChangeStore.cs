using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>權限異動查詢條件（多條件組合、分頁與排序下推 SQL）</summary>
public class PermissionChangeQueryFilter
{
    public IReadOnlyCollection<string>? HostNames { get; set; }
    public string? Keyword { get; set; }
    public List<string>? Categories { get; set; }
    public string? Status { get; set; }
    public string? Source { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Sort { get; set; }
    public bool Ascending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 權限異動檢核的真表儲存（↔ lf_permission_changes，含確認狀態）。
///
/// **單表整合異動與確認**：原本異動走 JSONL log、確認走 blob，現在合併為一列真表資料。
/// 狀態欄位可直接在 SQL 端篩選，並支援條件式原子更新（WHERE status = 'pending'），
/// 解決舊版整份讀改寫時多人同時確認會互相覆寫的問題。
/// </summary>
public class PermissionChangeStore
{
    private readonly Func<LfDbContext> _contextFactory;

    public PermissionChangeStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>批次寫入本次偵測到的異動</summary>
    public void AppendChanges(IEnumerable<PermissionChangeRecord> changes)
    {
        var list = changes.ToList();
        if (list.Count == 0) return;

        var now = DateTime.Now;
        using var ctx = _contextFactory();

        foreach (var change in list)
        {
            if (string.IsNullOrWhiteSpace(change.ChangeId))
                change.ChangeId = Guid.NewGuid().ToString("N");

            var row = new PermissionChangeRow
            {
                ChangeId = change.ChangeId,
                DedupeKey = change.DedupeKey(),
                HostName = change.HostName,
                HostNameKey = HostNameKey.Of(change.HostName),
                DetectedAt = change.DetectedAt,
                CreatedAt = now,
                Target = change.Target,
                ChangeType = change.ChangeType,
                Category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category,
                IsPrivilegedTarget = change.IsPrivilegedTarget,
                InitiatorAccount = change.InitiatorAccount,
                TargetAccount = change.TargetAccount,
                ObjectType = change.ObjectType,
                ProcessName = change.ProcessName,
                CoveredFrom = change.CoveredFrom,
                CoveredTo = change.CoveredTo,
                PairCount = change.PairCount,
                BeforeValue = change.Before ?? string.Empty,
                AfterValue = change.After ?? string.Empty,
                AlertText = change.AlertText ?? string.Empty,
                Source = string.IsNullOrWhiteSpace(change.Source) ? PermissionChangeSources.Local : change.Source,
                EventId = change.EventId,
                Status = PermissionConfirmStatuses.Pending
            };

            ctx.PermissionChanges.Add(row);
        }

        ctx.SaveChanges();
    }

    /// <summary>
    /// 依去重鍵寫入或更新一筆（供彙總列使用）。彙總列的內容會隨當日對數變動，
    /// 用 append 會每變一次就多一筆，所以這裡走「有就更新說明、沒有才新增」。
    /// 使用者已處理過的狀態（確認結果）不動——只更新描述性欄位。
    /// </summary>
    /// <param name="dedupeKey">**呼叫端指定的穩定鍵**：彙總列的說明文字含當日對數，用預設的
    /// 去重鍵（含 AlertText）會在對數一變就變成另一筆，所以由呼叫端給一個不含計數的鍵。</param>
    public void UpsertByDedupeKey(PermissionChangeRecord change, string dedupeKey)
    {
        using var ctx = _contextFactory();
        var existing = ctx.PermissionChanges.FirstOrDefault(r => r.DedupeKey == dedupeKey);

        if (existing != null)
        {
            existing.Target = change.Target;
            existing.AlertText = change.AlertText ?? string.Empty;
            // 涵蓋區間與對數會隨當日新事件變動，與說明文字同屬描述性欄位，一起更新
            existing.CoveredFrom = change.CoveredFrom;
            existing.CoveredTo = change.CoveredTo;
            existing.PairCount = change.PairCount;
            ctx.SaveChanges();
            return;
        }

        if (string.IsNullOrWhiteSpace(change.ChangeId))
            change.ChangeId = Guid.NewGuid().ToString("N");

        ctx.PermissionChanges.Add(new PermissionChangeRow
        {
            ChangeId = change.ChangeId,
            DedupeKey = dedupeKey,
            HostName = change.HostName,
            HostNameKey = HostNameKey.Of(change.HostName),
            DetectedAt = change.DetectedAt,
            CreatedAt = DateTime.Now,
            Target = change.Target,
            ChangeType = change.ChangeType,
            Category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category,
            IsPrivilegedTarget = change.IsPrivilegedTarget,
            InitiatorAccount = change.InitiatorAccount,
            TargetAccount = change.TargetAccount,
            ObjectType = change.ObjectType,
            ProcessName = change.ProcessName,
            CoveredFrom = change.CoveredFrom,
            CoveredTo = change.CoveredTo,
            PairCount = change.PairCount,
            BeforeValue = change.Before ?? string.Empty,
            AfterValue = change.After ?? string.Empty,
            AlertText = change.AlertText ?? string.Empty,
            Source = string.IsNullOrWhiteSpace(change.Source) ? PermissionChangeSources.Local : change.Source,
            EventId = change.EventId,
            Status = PermissionConfirmStatuses.Pending
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// 批次依去重鍵刪除，回傳刪除筆數（回饋二十七輪終檢）。
    ///
    /// 用途：某主機日的成對異動由「逐則列出」升級為「彙總列」時，先前已入庫的那些逐則列
    /// 必須撤掉——否則同一批異動同時以彙總與逐則兩種形式存在、被重複計算。
    /// 空集合直接回 0，不發查詢（呼叫端不必自己判斷）。
    /// </summary>
    public int DeleteByDedupeKeys(IEnumerable<string> dedupeKeys)
    {
        var keys = dedupeKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return 0;

        using var ctx = _contextFactory();
        return ctx.PermissionChanges
            .Where(r => keys.Contains(r.DedupeKey))
            .ExecuteDelete();
    }

    /// <summary>某去重鍵是否已有列（供彙總列判斷「這天先前已看過完整的例行同步」使用）。</summary>
    public bool ExistsByDedupeKey(string dedupeKey)
    {
        using var ctx = _contextFactory();
        return ctx.PermissionChanges.AsNoTracking().Any(r => r.DedupeKey == dedupeKey);
    }

    /// <summary>
    /// 依去重鍵刪除一筆。**不用於撤銷例行同步彙總列**——來源重跑只回子集時撤掉彙總，
    /// 等於用一次不完整的取數抹掉完整那次的結論，而被合併掉的成對明細從未入庫、補不回來
    /// （回饋二十七輪作業 A）。保留此方法供其他依鍵刪除的情境使用。
    /// </summary>
    public bool DeleteByDedupeKey(string dedupeKey)
    {
        using var ctx = _contextFactory();
        var existing = ctx.PermissionChanges.FirstOrDefault(r => r.DedupeKey == dedupeKey);
        if (existing == null) return false;

        ctx.PermissionChanges.Remove(existing);
        ctx.SaveChanges();
        return true;
    }

    /// <summary>
    /// 刪除舊版每主機日「權限異動（彙總）」列並回傳刪除筆數。
    ///
    /// 這批列由已移除的「每主機日筆數上限」機制產生（現行只產生「例行同步（彙總）」），
    /// 內容是「本日另有 N 則權限異動事件未逐則列出」——被它取代的那些逐則列並沒有寫進資料庫，
    /// 補不回來也查不了，留著只會讓待辦清單裡出現一筆點開沒有內容的項目。
    /// </summary>
    public int DeleteLegacySummaryRows()
    {
        using var ctx = _contextFactory();
        return ctx.PermissionChanges
            .Where(r => r.ChangeType == LegacySummaryChangeType)
            .ExecuteDelete();
    }

    /// <summary>不再產生的舊彙總列 change_type（見 <see cref="DeleteLegacySummaryRows"/>）</summary>
    public const string LegacySummaryChangeType = "權限異動（彙總）";

    /// <summary>多條件篩選、排序與分頁查詢（全部條件下推 SQL）</summary>
    public PagedResult<PermissionChangeRecord> Query(PermissionChangeQueryFilter filter)
    {
        if (filter.HostNames != null && filter.HostNames.Count == 0)
        {
            return new PagedResult<PermissionChangeRecord>
            {
                Items = new List<PermissionChangeRecord>(),
                Page = filter.Page,
                PageSize = filter.PageSize,
                Total = 0
            };
        }

        using var ctx = _contextFactory();
        var query = BuildQuery(ctx, filter);

        var total = query.Count();
        if (total == 0)
        {
            return new PagedResult<PermissionChangeRecord>
            {
                Items = new List<PermissionChangeRecord>(),
                Page = filter.Page,
                PageSize = filter.PageSize,
                Total = 0
            };
        }

        query = ApplyOrdering(query, filter.Sort, filter.Ascending);

        var skip = Math.Max(0, (filter.Page - 1) * filter.PageSize);
        var rows = query
            .Skip(skip)
            .Take(filter.PageSize)
            .ToList();

        return new PagedResult<PermissionChangeRecord>
        {
            Items = rows.Select(ToModel).ToList(),
            Page = filter.Page,
            PageSize = filter.PageSize,
            Total = total
        };
    }

    /// <summary>依篩選條件查詢符合的 ChangeId 清單（含總符合筆數與上限截斷）</summary>
    public (List<string> Ids, int Total) QueryIds(PermissionChangeQueryFilter filter, int maxCount)
    {
        if (filter.HostNames != null && filter.HostNames.Count == 0)
        {
            return (new List<string>(), 0);
        }

        using var ctx = _contextFactory();
        var query = BuildQuery(ctx, filter);

        var total = query.Count();
        if (total == 0)
        {
            return (new List<string>(), 0);
        }

        query = ApplyOrdering(query, filter.Sort, filter.Ascending);

        var ids = query
            .Take(maxCount)
            .Select(r => r.ChangeId)
            .ToList();

        return (ids, total);
    }

    private static IQueryable<PermissionChangeRow> BuildQuery(LfDbContext ctx, PermissionChangeQueryFilter filter)
    {
        IQueryable<PermissionChangeRow> query = ctx.PermissionChanges.AsNoTracking();

        if (filter.HostNames != null)
        {
            var keys = filter.HostNames.Select(HostNameKey.Of).Distinct().ToList();
            query = query.Where(r => keys.Contains(r.HostNameKey));
        }

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var q = filter.Keyword.Trim();
            query = query.Where(r =>
                r.HostName.Contains(q) ||
                (r.InitiatorAccount != null && r.InitiatorAccount.Contains(q)) ||
                (r.TargetAccount != null && r.TargetAccount.Contains(q)) ||
                r.Target.Contains(q) ||
                r.AlertText.Contains(q));
        }

        if (filter.Categories is { Count: > 0 })
        {
            query = query.Where(r => filter.Categories.Contains(r.Category));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(r => r.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            query = query.Where(r => r.Source == filter.Source);
        }

        if (filter.From.HasValue)
        {
            query = query.Where(r => r.DetectedAt >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(r => r.DetectedAt <= filter.To.Value);
        }

        return query;
    }

    /// <summary>簡化多載（轉發至多條件分頁查詢）。生產程式碼一律走 filter 版本，
    /// 這個只供測試以「主機＋狀態」的最小形狀建查詢，避免每支測試都要組整份 filter。</summary>
    public List<PermissionChangeRecord> Query(IReadOnlyCollection<string>? hostNames, string? status, int maxCount) =>
        Query(new PermissionChangeQueryFilter
        {
            HostNames = hostNames,
            Status = status,
            PageSize = maxCount
        }).Items;

    private static IQueryable<PermissionChangeRow> ApplyOrdering(
        IQueryable<PermissionChangeRow> query,
        string? sort,
        bool ascending)
    {
        // 先把 sortKey 正規化到白名單內，不認識的一律退回 detectedat。
        // 否則未支援的欄位名會直接落到最後那支 catch-all，連 ascending 一起吃掉——
        // 使用者按了升冪卻看到降冪，而且分頁時每頁的順序基準還不一致。
        var sortKey = sort?.Trim().ToLowerInvariant();
        if (sortKey is not ("hostname" or "category" or "status" or "detectedat"))
        {
            sortKey = "detectedat";
        }

        return (sortKey, ascending) switch
        {
            ("hostname", true) => query.OrderBy(r => r.HostName).ThenByDescending(r => r.DetectedAt).ThenBy(r => r.Id),
            ("hostname", false) => query.OrderByDescending(r => r.HostName).ThenByDescending(r => r.DetectedAt).ThenByDescending(r => r.Id),

            ("category", true) => query.OrderBy(r => r.Category).ThenByDescending(r => r.DetectedAt).ThenBy(r => r.Id),
            ("category", false) => query.OrderByDescending(r => r.Category).ThenByDescending(r => r.DetectedAt).ThenByDescending(r => r.Id),

            ("status", true) => query.OrderBy(r => r.Status).ThenByDescending(r => r.DetectedAt).ThenBy(r => r.Id),
            ("status", false) => query.OrderByDescending(r => r.Status).ThenByDescending(r => r.DetectedAt).ThenByDescending(r => r.Id),

            (_, true) => query.OrderBy(r => r.DetectedAt).ThenBy(r => r.Id),
            _ => query.OrderByDescending(r => r.DetectedAt).ThenByDescending(r => r.Id)
        };
    }

    /// <summary>單列查詢</summary>
    public PermissionChangeRecord? Get(string changeId)
    {
        if (string.IsNullOrWhiteSpace(changeId)) return null;

        using var ctx = _contextFactory();
        var row = ctx.PermissionChanges.AsNoTracking()
            .FirstOrDefault(r => r.ChangeId == changeId);

        return row != null ? ToModel(row) : null;
    }

    /// <summary>批次查詢多筆異動紀錄</summary>
    public List<PermissionChangeRecord> GetByChangeIds(IEnumerable<string> changeIds)
    {
        var ids = changeIds.Distinct().ToList();
        if (ids.Count == 0) return new List<PermissionChangeRecord>();

        using var ctx = _contextFactory();
        var rows = ctx.PermissionChanges.AsNoTracking()
            .Where(r => ids.Contains(r.ChangeId))
            .ToList();

        return rows.Select(ToModel).ToList();
    }

    /// <summary>取得指定異動的確認狀態清單</summary>
    public List<PermissionChangeConfirmation> GetConfirmations(IEnumerable<string> changeIds)
    {
        var ids = changeIds.Distinct().ToList();
        if (ids.Count == 0) return new List<PermissionChangeConfirmation>();

        using var ctx = _contextFactory();
        var rows = ctx.PermissionChanges.AsNoTracking()
            .Where(r => ids.Contains(r.ChangeId))
            .Select(r => new
            {
                r.ChangeId,
                r.Status,
                r.ConfirmedBy,
                r.ConfirmedByAccount,
                r.ConfirmedAt,
                r.ConfirmNote
            })
            .ToList();

        return rows.Select(r => new PermissionChangeConfirmation
        {
            ChangeId = r.ChangeId,
            Status = r.Status,
            ConfirmedBy = r.ConfirmedBy,
            ConfirmedByAccount = r.ConfirmedByAccount ?? string.Empty,
            ConfirmedAt = r.ConfirmedAt,
            Note = r.ConfirmNote
        }).ToList();
    }

    /// <summary>去重鍵快照（只投影 dedupe_key 欄位）</summary>
    public virtual HashSet<string> GetDedupeKeys(DateTime? appendedSince = null)
    {
        using var ctx = _contextFactory();
        IQueryable<PermissionChangeRow> query = ctx.PermissionChanges.AsNoTracking();

        if (appendedSince.HasValue)
        {
            query = query.Where(r => r.CreatedAt >= appendedSince.Value);
        }

        return query
            .Select(r => r.DedupeKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>條件式原子更新確認狀態：只有目前為 pending 時才寫入成功</summary>
    public bool SaveConfirmation(PermissionChangeConfirmation confirmation)
    {
        if (string.IsNullOrWhiteSpace(confirmation.ChangeId)) return false;

        return SaveConfirmationsInternal(
            new[] { confirmation.ChangeId },
            confirmation.Status,
            confirmation.ConfirmedBy,
            confirmation.ConfirmedByAccount,
            confirmation.ConfirmedAt,
            confirmation.Note) > 0;
    }

    /// <summary>批次條件式原子更新確認狀態：只有目前為 pending 的列才會被更新</summary>
    public int SaveConfirmations(
        IEnumerable<string> changeIds,
        string status,
        long? confirmedBy,
        string confirmedByAccount,
        DateTime? confirmedAt,
        string? note)
    {
        return SaveConfirmationsInternal(changeIds, status, confirmedBy, confirmedByAccount, confirmedAt, note);
    }

    private int SaveConfirmationsInternal(
        IEnumerable<string> changeIds,
        string status,
        long? confirmedBy,
        string confirmedByAccount,
        DateTime? confirmedAt,
        string? note)
    {
        var ids = changeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var ctx = _contextFactory();
        return ctx.PermissionChanges
            .Where(r => ids.Contains(r.ChangeId) && r.Status == PermissionConfirmStatuses.Pending)
            .ExecuteUpdate(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ConfirmedBy, confirmedBy)
                .SetProperty(r => r.ConfirmedByAccount, confirmedByAccount)
                .SetProperty(r => r.ConfirmedAt, confirmedAt)
                .SetProperty(r => r.ConfirmNote, note));
    }

    /// <summary>待確認筆數（SQL COUNT 下推）</summary>
    public int CountPending(IReadOnlyCollection<string>? hostNames)
    {
        if (hostNames != null && hostNames.Count == 0)
            return 0;

        using var ctx = _contextFactory();
        IQueryable<PermissionChangeRow> query = ctx.PermissionChanges.AsNoTracking()
            .Where(r => r.Status == PermissionConfirmStatuses.Pending);

        if (hostNames != null)
        {
            var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
            query = query.Where(r => keys.Contains(r.HostNameKey));
        }

        return query.Count();
    }

    /// <summary>
    /// 依寫入時間（created_at）清除超過保留天數的異動列。
    ///
    /// 判準刻意不用 detected_at：NetIQ 回補進來的舊事件 detected_at 可以遠早於保留期，
    /// 依它清理會讓剛匯入的資料一寫入就消失。重跑不會刷新 created_at（去重鍵讓明細列不重寫，
    /// 彙總列走 UpsertByDedupeKey 只更新描述性欄位），所以舊資料不會因重跑而續命。
    /// </summary>
    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        using var ctx = _contextFactory();
        return ctx.PermissionChanges
            .Where(r => r.CreatedAt < cutoff)
            .ExecuteDelete();
    }

    private static PermissionChangeRecord ToModel(PermissionChangeRow row) => new()
    {
        ChangeId = row.ChangeId,
        HostName = row.HostName,
        DetectedAt = row.DetectedAt,
        Target = row.Target,
        ChangeType = row.ChangeType,
        Before = row.BeforeValue,
        After = row.AfterValue,
        AlertText = row.AlertText,
        Source = row.Source,
        EventId = row.EventId,
        Category = row.Category,
        IsPrivilegedTarget = row.IsPrivilegedTarget,
        InitiatorAccount = row.InitiatorAccount,
        TargetAccount = row.TargetAccount,
        ObjectType = row.ObjectType,
        ProcessName = row.ProcessName,
        CoveredFrom = row.CoveredFrom,
        CoveredTo = row.CoveredTo,
        PairCount = row.PairCount
    };
}
