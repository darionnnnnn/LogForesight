using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 權限異動待辦的真表儲存（↔ lf_permission_changes，含確認狀態）。
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

    /// <summary>依主機與確認狀態查詢；hostNames 為空集合時回空結果（授權範圍為空）</summary>
    public List<PermissionChangeRecord> Query(IReadOnlyCollection<string>? hostNames, string? status, int maxCount)
    {
        if (hostNames != null && hostNames.Count == 0)
            return new List<PermissionChangeRecord>();

        using var ctx = _contextFactory();
        IQueryable<PermissionChangeRow> query = ctx.PermissionChanges.AsNoTracking();

        if (hostNames != null)
        {
            var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
            query = query.Where(r => keys.Contains(r.HostNameKey));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        var rows = query
            .OrderByDescending(r => r.DetectedAt)
            .Take(maxCount)
            .ToList();

        return rows.Select(ToModel).ToList();
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
    public HashSet<string> GetDedupeKeys(DateTime? appendedSince = null)
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

        using var ctx = _contextFactory();
        var updated = ctx.PermissionChanges
            .Where(r => r.ChangeId == confirmation.ChangeId && r.Status == PermissionConfirmStatuses.Pending)
            .ExecuteUpdate(s => s
                .SetProperty(r => r.Status, confirmation.Status)
                .SetProperty(r => r.ConfirmedBy, confirmation.ConfirmedBy)
                .SetProperty(r => r.ConfirmedByAccount, confirmation.ConfirmedByAccount)
                .SetProperty(r => r.ConfirmedAt, confirmation.ConfirmedAt)
                .SetProperty(r => r.ConfirmNote, confirmation.Note));

        return updated > 0;
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

    /// <summary>依寫入時間（created_at）清除超過保留天數的異動列</summary>
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
        TargetAccount = row.TargetAccount
    };
}
