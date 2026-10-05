using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// PRTG 鏡像資料存取層（lf_prtg_* 五張表）：提供裝置與感測器結構鏡像 upsert、狀態變更寫入、
/// 每小時數值 upsert、按日主機映射寫入，以及依保留天數清理過期資料。
/// </summary>
public sealed class EfPrtgStore
{
    private const int CandidateSnapshotSensorTypeMaxChars = 256;
    private const int CandidateSnapshotUnitMaxChars = 128;
    private const int CandidateSnapshotNameMaxChars = 255;
    private const int CandidateSnapshotCategoryMaxChars = 64;
    private const int CandidateSnapshotSensorTypeFieldMaxChars = 128;
    private const int CandidateSnapshotUnitFieldMaxChars = 64;
    private const int CandidateSnapshotTextMaxBytes = 24 * 1024 * 1024;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;

    /// <summary>
    /// 慢操作監控（可選相依，與 <see cref="EfAnalysisRecordStore"/> 等 store 同一套）。
    /// PRTG 鏡像是最容易變慢的一區（數值表隨感測器數×小時數成長），
    /// 過去一個埋點都沒有，慢的時候健康頁上看不到任何線索（回饋四十五輪 B6）。
    /// </summary>
    private readonly SqlPerformanceMonitor? _performance;

    /// <summary>單次 SaveChanges 的批次上限</summary>
    private const int UpsertBatchSize = 500;
    public const string RecentStateChangeQueuePrefix = "prtg_recent_stateq_";
    public const int RecentStateChangeQueueCapacity = 15000;
    public const int RecentStateChangeQueueItemMaxBytes = 8 * 1024;
    public const string RecentStateChangeQueueStopSummaryKey = "prtg_recent_state_queue_stop_summary";

    public EfPrtgStore(Func<LfDbContext> contextFactory, SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _performance = performance;
    }

    /// <summary>
    /// 切批寫入的私有輔助方法（每 <see cref="UpsertBatchSize"/> 筆存一次檔，每批一個獨立 DbContext）。
    /// 任一批失敗時例外照樣往外傳，不吞、不跳過剩餘批次。
    /// </summary>
    private int BatchWrite<T>(IReadOnlyList<T> items, Func<LfDbContext, List<T>, int> writeBatch)
    {
        if (items == null || items.Count == 0) return 0;

        var list = items as List<T> ?? items.ToList();
        var total = 0;

        for (var offset = 0; offset < list.Count; offset += UpsertBatchSize)
        {
            var count = Math.Min(UpsertBatchSize, list.Count - offset);
            var batch = list.GetRange(offset, count);
            using var ctx = _contextFactory();
            total += writeBatch(ctx, batch);
        }

        return total;
    }

    /// <summary>
    /// 快照整點列必須整批提交。若前 500 列已寫而後一批失敗，呼叫端會保留整份清單重試；
    /// 分批獨立提交會把前一批的 sampled coverage 再合併一次。
    /// </summary>
    private int BatchWriteAtomic<T>(IReadOnlyList<T> items, Func<LfDbContext, List<T>, int> writeBatch,
        string? sampledBatchId = null, Action<LfDbContext>? beforeCommit = null)
    {
        if (items == null || items.Count == 0) return 0;

        var list = items as List<T> ?? items.ToList();
        using var probe = _contextFactory();
        var strategy = probe.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            using var ctx = _contextFactory();
            using var transaction = ctx.Database.BeginTransaction();
            if (sampledBatchId != null)
            {
                if (ctx.PrtgSampledBatches.Any(b => b.BatchId == sampledBatchId)) return 0;
                ctx.PrtgSampledBatches.Add(new PrtgSampledBatchRow
                {
                    BatchId = sampledBatchId,
                    CreatedAt = DateTime.Now
                });
            }
            var total = 0;
            for (var offset = 0; offset < list.Count; offset += UpsertBatchSize)
            {
                var count = Math.Min(UpsertBatchSize, list.Count - offset);
                total += writeBatch(ctx, list.GetRange(offset, count));
                // 每批已 SaveChanges，清掉追蹤列以免後續批次反覆掃描前批的 entity。
                ctx.ChangeTracker.Clear();
            }
            beforeCommit?.Invoke(ctx);
            transaction.Commit();
            return total;
        });
    }

    /// <summary>
    /// PRTG 裝置結構鏡像 upsert。自然鍵為 objid，已存在則就地更新描述性欄位，不存在則新增。
    /// 更新時 CreatedAt 保持原值不覆蓋（首次寫入時間）。
    /// </summary>
    public int UpsertDevices(IReadOnlyList<PrtgDeviceRow> devices, DateTime syncedAt)
    {
        return BatchWrite(devices, (ctx, batch) =>
        {
            var ids = batch.Select(d => d.Objid).ToList();
            var existing = ctx.PrtgDevices.Where(d => ids.Contains(d.Objid)).ToDictionary(d => d.Objid);

            foreach (var item in batch)
            {
                if (existing.TryGetValue(item.Objid, out var row))
                {
                    row.Name = item.Name;
                    row.GroupPath = item.GroupPath;
                    row.Ip = item.Ip;
                    row.Tags = item.Tags;
                    row.Status = item.Status;
                    row.DependencyObjid = item.DependencyObjid;
                    row.Paused = item.Paused;
                    row.SyncedAt = syncedAt;
                    // CreatedAt 保持原值不覆蓋（首次寫入時間）
                }
                else
                {
                    var newRow = new PrtgDeviceRow
                    {
                        Objid = item.Objid,
                        Name = item.Name,
                        GroupPath = item.GroupPath,
                        Ip = item.Ip,
                        Tags = item.Tags,
                        Status = item.Status,
                        DependencyObjid = item.DependencyObjid,
                        Paused = item.Paused,
                        SyncedAt = syncedAt,
                        CreatedAt = item.CreatedAt != default ? item.CreatedAt : syncedAt
                    };
                    ctx.PrtgDevices.Add(newRow);
                    existing[item.Objid] = newRow;
                }
            }

            IncrementPrtgCatalogueDataRevision(ctx);
            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// PRTG 感測器結構鏡像 upsert。自然鍵為 objid，已存在則就地更新描述性欄位，不存在則新增。
    /// 更新時 CreatedAt 保持原值不覆蓋。
    /// </summary>
    public int UpsertSensors(IReadOnlyList<PrtgSensorRow> sensors, DateTime syncedAt, bool requireAtomic = false)
        => UpsertSensorsCore(sensors, syncedAt, requireAtomic, null);

    public int UpsertSensorsAndEnqueueRecentStateChanges(IReadOnlyList<PrtgSensorRow> sensors, DateTime syncedAt,
        PrtgRecentStateChangeQueueItem queueItem)
        => UpsertSensorsCore(sensors, syncedAt, requireAtomic: true, queueItem);

    private int UpsertSensorsCore(IReadOnlyList<PrtgSensorRow> sensors, DateTime syncedAt, bool requireAtomic,
        PrtgRecentStateChangeQueueItem? queueItem)
    {
        int WriteBatch(LfDbContext ctx, List<PrtgSensorRow> batch)
        {
            var ids = batch.Select(s => s.Objid).ToList();
            var existing = ctx.PrtgSensors.Where(s => ids.Contains(s.Objid)).ToDictionary(s => s.Objid);

            foreach (var item in batch)
            {
                if (existing.TryGetValue(item.Objid, out var row))
                {
                    row.DeviceObjid = item.DeviceObjid;
                    row.Name = item.Name;
                    row.SensorType = item.SensorType;
                    row.Tags = item.Tags;
                    row.Unit = item.Unit;
                    row.Status = item.Status;
                    row.ThresholdsJson = item.ThresholdsJson;
                    row.DependencyObjid = item.DependencyObjid;
                    row.Paused = item.Paused;
                    row.SyncedAt = syncedAt;
                    // Category 與 CategorySource 絕對不可覆蓋：這兩欄是 sensor 語意分類結果，未來會有人工指定的值，每日結構同步不得把人工結果洗掉。
                    // CreatedAt 保持原值不覆蓋（首次寫入時間）。
                }
                else
                {
                    var newRow = new PrtgSensorRow
                    {
                        Objid = item.Objid,
                        DeviceObjid = item.DeviceObjid,
                        Name = item.Name,
                        SensorType = item.SensorType,
                        Tags = item.Tags,
                        Unit = item.Unit,
                        Status = item.Status,
                        ThresholdsJson = item.ThresholdsJson,
                        DependencyObjid = item.DependencyObjid,
                        Paused = item.Paused,
                        Category = item.Category,
                        CategorySource = item.CategorySource,
                        SyncedAt = syncedAt,
                        CreatedAt = item.CreatedAt != default ? item.CreatedAt : syncedAt
                    };
                    ctx.PrtgSensors.Add(newRow);
                    existing[item.Objid] = newRow;
                }
            }

            IncrementPrtgCatalogueDataRevision(ctx);
            ctx.SaveChanges();
            return batch.Count;
        }

        if (queueItem != null)
            return BatchWriteAtomic(sensors, (ctx, batch) => WriteBatch(ctx, batch),
                beforeCommit: ctx => EnqueueRecentStateChangeInTransaction(ctx, queueItem));
        return requireAtomic ? BatchWriteAtomic(sensors, WriteBatch) : BatchWrite(sensors, WriteBatch);
    }

    public IReadOnlyList<PrtgRecentStateChangeQueueItem> ReadRecentStateChangeQueue()
    {
        using var ctx = _contextFactory();
        return ReadRecentStateChangeQueueRows(ctx).Select(x => x.Item).ToArray();
    }

    public PrtgRecentStateChangeQueueStopSummary? ReadRecentStateChangeQueueStopSummary()
    {
        using var ctx = _contextFactory();
        var content = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == RecentStateChangeQueueStopSummaryKey)
            .Select(b => b.Content).FirstOrDefault();
        return content == null ? null : JsonSerializer.Deserialize<PrtgRecentStateChangeQueueStopSummary>(content);
    }

    public IReadOnlyList<PrtgRecentStateChangeLease> ClaimRecentStateChanges(string owner, DateTime nowUtc,
        TimeSpan leaseDuration, int take)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("佇列租用者不可空白。", nameof(owner));
        if (take is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(take));
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();
            var due = ReadRecentStateChangeQueueRows(ctx)
                .Where(x => x.Item.CompletedAtUtc == null && x.Item.NextAttemptAtUtc <= nowUtc &&
                    (!x.Item.LeaseExpiresAtUtc.HasValue || x.Item.LeaseExpiresAtUtc.Value <= nowUtc))
                .OrderBy(x => x.Item.NextAttemptAtUtc)
                .ThenBy(x => x.Item.FirstEnqueuedAtUtc)
                .ThenBy(x => x.Row.BlobKey, StringComparer.Ordinal)
                .Take(take)
                .ToList();
            var leases = new List<PrtgRecentStateChangeLease>(due.Count);
            foreach (var candidate in due)
            {
                var leasedItem = candidate.Item with { LeaseOwner = owner, LeaseExpiresAtUtc = nowUtc + leaseDuration };
                var content = SerializeQueueItem(leasedItem);
                if (!FitsQueueContent(content)) continue;
                var changed = ctx.Blobs.Where(b => b.BlobKey == candidate.Row.BlobKey && b.Version == candidate.Row.Version)
                    .ExecuteUpdate(setters => setters.SetProperty(b => b.Content, content)
                        .SetProperty(b => b.UpdatedAt, nowUtc).SetProperty(b => b.Version, b => b.Version + 1));
                if (changed == 1) leases.Add(new PrtgRecentStateChangeLease(leasedItem, owner, candidate.Row.Version + 1));
            }
            tx.Commit();
            return (IReadOnlyList<PrtgRecentStateChangeLease>)leases;
        });
    }

    public bool AcknowledgeRecentStateChange(PrtgRecentStateChangeLease lease)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();
            var row = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version).Select(b => b.Content).FirstOrDefault();
            if (row == null) { tx.Commit(); return false; }
            var current = DeserializeQueueItem(row);
            if (current.LeaseOwner != lease.Owner || current.CompletedAtUtc != null) { tx.Commit(); return false; }
            var content = SerializeQueueItem(current with { LeaseOwner = null, LeaseExpiresAtUtc = null, CompletedAtUtc = DateTime.UtcNow });
            var updated = ctx.Blobs.Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version)
                .ExecuteUpdate(setters => setters.SetProperty(b => b.Content, content)
                    .SetProperty(b => b.UpdatedAt, DateTime.UtcNow).SetProperty(b => b.Version, b => b.Version + 1));
            tx.Commit();
            return updated == 1;
        });
    }

    public int ReconcileRecentStateChanges(IReadOnlySet<long> businessDeviceObjids, string sourceIdentityHash,
        string businessScopeVersion, DateTime fromLocalDate, DateTime toLocalDate, DateTime nowUtc)
    {
        var mirroredBusinessDevices = businessDeviceObjids.Count == 0 || businessDeviceObjids.Count > RecentStateChangeQueueCapacity
            ? Array.Empty<long>()
            : PrtgSqlServerIdScope.Execute(_contextFactory, businessDeviceObjids, null,
                (ctx, hosts, _, hostScope, _) =>
                {
                    var hostIds = hostScope ?? hosts.AsQueryable();
                    return ctx.PrtgSensors.AsNoTracking().Where(s => hostIds.Contains(s.DeviceObjid))
                        .Select(s => s.DeviceObjid).Distinct().ToArray();
                });
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();
            var rows = ReadRecentStateChangeQueueRows(ctx);
            var deletedKeys = new HashSet<string>(StringComparer.Ordinal);
            var changedRows = 0;
            var stopped = rows.Where(x => !businessDeviceObjids.Contains(x.Item.DeviceObjid)).ToList();
            var stoppedPendingDeviceIds = new List<long>();
            foreach (var stale in stopped)
            {
                if (ctx.Blobs.Where(b => b.BlobKey == stale.Row.BlobKey && b.Version == stale.Row.Version).ExecuteDelete() == 1)
                {
                    deletedKeys.Add(stale.Row.BlobKey);
                    if (!stale.Item.CompletedAtUtc.HasValue) stoppedPendingDeviceIds.Add(stale.Item.DeviceObjid);
                }
            }
            var remaining = rows.Where(x => !deletedKeys.Contains(x.Row.BlobKey)).ToList();
            if (businessDeviceObjids.Count > RecentStateChangeQueueCapacity)
            {
                UpsertRecentStateChangeQueueStopSummary(ctx, new(nowUtc,
                    stoppedPendingDeviceIds.Count == 0 ? "business-scope-capacity-exceeded" : "business-scope-removed-and-capacity-exceeded",
                    businessScopeVersion, stoppedPendingDeviceIds.Count,
                    stoppedPendingDeviceIds.Order().Take(50).ToArray(),
                    businessDeviceObjids.Count,
                    businessDeviceObjids.Order().Take(50).ToArray()));
                ctx.SaveChanges();
                tx.Commit();
                throw new InvalidOperationException($"PRTG 近期狀態業務範圍有 {businessDeviceObjids.Count} 台裝置；持久化佇列最多支援 {RecentStateChangeQueueCapacity} 台。系統未靜默省略任何裝置。");
            }
            foreach (var candidate in remaining
                .Where(x => businessDeviceObjids.Contains(x.Item.DeviceObjid))
                .Where(x => x.Item.SourceIdentityHash != sourceIdentityHash || x.Item.BusinessScopeVersion != businessScopeVersion ||
                    (x.Item.CompletedAtUtc.HasValue &&
                     (x.Item.FromLocalDate.Date != fromLocalDate.Date || x.Item.ToLocalDate.Date != toLocalDate.Date)))
                .ToList())
            {
                var reset = candidate.Item with
                {
                    FromLocalDate = fromLocalDate.Date,
                    ToLocalDate = toLocalDate.Date,
                    SourceIdentityHash = sourceIdentityHash,
                    BusinessScopeVersion = businessScopeVersion,
                    FirstEnqueuedAtUtc = nowUtc,
                    NextAttemptAtUtc = nowUtc,
                    Attempts = 0,
                    LeaseOwner = null,
                    LeaseExpiresAtUtc = null,
                    CompletedAtUtc = null
                };
                var content = SerializeQueueItem(reset);
                if (!FitsQueueContent(content)) throw new InvalidOperationException("PRTG 近期狀態佇列項目超過 8 KiB。");
                changedRows += ctx.Blobs.Where(b => b.BlobKey == candidate.Row.BlobKey && b.Version == candidate.Row.Version)
                    .ExecuteUpdate(setters => setters.SetProperty(b => b.Content, content)
                        .SetProperty(b => b.UpdatedAt, nowUtc).SetProperty(b => b.Version, b => b.Version + 1));
            }

            var knownDevices = remaining.Select(x => x.Item.DeviceObjid).ToHashSet();
            var queueCount = rows.Count - deletedKeys.Count;
            var missingMirroredDevices = mirroredBusinessDevices.Where(id => !knownDevices.Contains(id)).OrderBy(id => id).ToArray();
            if (queueCount + missingMirroredDevices.Length > RecentStateChangeQueueCapacity)
            {
                UpsertRecentStateChangeQueueStopSummary(ctx, new(nowUtc,
                    stoppedPendingDeviceIds.Count == 0 ? "business-scope-capacity-exceeded" : "business-scope-removed-and-capacity-exceeded",
                    businessScopeVersion, stoppedPendingDeviceIds.Count,
                    stoppedPendingDeviceIds.Order().Take(50).ToArray(),
                    missingMirroredDevices.Length, missingMirroredDevices.Take(50).ToArray()));
                ctx.SaveChanges();
                tx.Commit();
                throw new InvalidOperationException($"PRTG 近期狀態佇列容量 {RecentStateChangeQueueCapacity} 將因 {missingMirroredDevices.Length} 台已鏡像的業務裝置而超限。系統未靜默省略任何裝置。");
            }
            foreach (var deviceObjid in missingMirroredDevices)
            {
                var item = new PrtgRecentStateChangeQueueItem(deviceObjid, fromLocalDate.Date, toLocalDate.Date,
                    sourceIdentityHash, businessScopeVersion, nowUtc, nowUtc, 0, null, null, null);
                var content = SerializeQueueItem(item);
                if (!FitsQueueContent(content)) throw new InvalidOperationException("PRTG 近期狀態佇列項目超過 8 KiB。");
                ctx.Blobs.Add(new BlobRow { BlobKey = RecentStateChangeQueueKey(deviceObjid), Content = content, UpdatedAt = nowUtc, Version = 1 });
                queueCount++;
                changedRows++;
            }
            if (stoppedPendingDeviceIds.Count > 0)
            {
                UpsertRecentStateChangeQueueStopSummary(ctx, new(nowUtc, "business-scope-removed", businessScopeVersion,
                    stoppedPendingDeviceIds.Count, stoppedPendingDeviceIds.Order().Take(50).ToArray(),
                    0, Array.Empty<long>()));
                changedRows++;
            }
            if (changedRows > 0) ctx.SaveChanges();
            tx.Commit();
            return changedRows + deletedKeys.Count;
        });
    }

    private static void UpsertRecentStateChangeQueueStopSummary(LfDbContext ctx, PrtgRecentStateChangeQueueStopSummary summary)
    {
        var content = JsonSerializer.Serialize(summary);
        if (Encoding.UTF8.GetByteCount(content) > RecentStateChangeQueueItemMaxBytes)
            throw new InvalidOperationException("PRTG 近期狀態佇列停止摘要超過 8 KiB。");
        var prior = ctx.Blobs.SingleOrDefault(b => b.BlobKey == RecentStateChangeQueueStopSummaryKey);
        if (prior == null)
            ctx.Blobs.Add(new BlobRow { BlobKey = RecentStateChangeQueueStopSummaryKey, Content = content, UpdatedAt = summary.AtUtc, Version = 1 });
        else
        {
            prior.Content = content;
            prior.UpdatedAt = summary.AtUtc;
            prior.Version++;
        }
    }

    public bool RetryRecentStateChange(PrtgRecentStateChangeLease lease, DateTime nowUtc, TimeSpan? delay = null)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();
            var row = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version).Select(b => new { b.Content, b.Version }).FirstOrDefault();
            if (row == null) { tx.Commit(); return false; }
            var current = DeserializeQueueItem(row.Content);
            if (current.LeaseOwner != lease.Owner) { tx.Commit(); return false; }
            var seconds = delay?.TotalSeconds ?? Math.Min(600, 15 * Math.Pow(2, Math.Min(current.Attempts, 5)));
            var retry = current with
            {
                Attempts = checked(current.Attempts + 1),
                NextAttemptAtUtc = nowUtc + TimeSpan.FromSeconds(seconds),
                LeaseOwner = null,
                LeaseExpiresAtUtc = null
            };
            var content = SerializeQueueItem(retry);
            if (!FitsQueueContent(content)) { tx.Commit(); return false; }
            var changed = ctx.Blobs.Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version)
                .ExecuteUpdate(setters => setters.SetProperty(b => b.Content, content)
                    .SetProperty(b => b.UpdatedAt, nowUtc).SetProperty(b => b.Version, b => b.Version + 1));
            tx.Commit();
            return changed == 1;
        });
    }

    public bool ReleaseRecentStateChangeLease(PrtgRecentStateChangeLease lease, DateTime nowUtc)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();
            var row = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version).Select(b => b.Content).FirstOrDefault();
            if (row == null) { tx.Commit(); return false; }
            var current = DeserializeQueueItem(row);
            if (current.LeaseOwner != lease.Owner || current.CompletedAtUtc != null) { tx.Commit(); return false; }
            var content = SerializeQueueItem(current with { LeaseOwner = null, LeaseExpiresAtUtc = null });
            var updated = ctx.Blobs.Where(b => b.BlobKey == RecentStateChangeQueueKey(lease.Item.DeviceObjid) &&
                    b.Version == lease.Version)
                .ExecuteUpdate(setters => setters.SetProperty(b => b.Content, content)
                    .SetProperty(b => b.UpdatedAt, nowUtc).SetProperty(b => b.Version, b => b.Version + 1));
            tx.Commit();
            return updated == 1;
        });
    }

    private static void EnqueueRecentStateChangeInTransaction(LfDbContext ctx, PrtgRecentStateChangeQueueItem item)
    {
        var key = RecentStateChangeQueueKey(item.DeviceObjid);
        var existing = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        if (existing == null)
        {
            var count = ctx.Blobs.Count(b => b.BlobKey.StartsWith(RecentStateChangeQueuePrefix));
            if (count >= RecentStateChangeQueueCapacity)
                throw new InvalidOperationException($"PRTG 近期狀態佇列已滿（{RecentStateChangeQueueCapacity} 筆）；感測器鏡像交易已回滾。");
            var content = SerializeQueueItem(item);
            if (!FitsQueueContent(content)) throw new InvalidOperationException("PRTG 近期狀態佇列項目超過 8 KiB。");
            ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, UpdatedAt = DateTime.UtcNow, Version = 1 });
            ctx.SaveChanges();
            return;
        }

        var prior = DeserializeQueueItem(existing.Content);
        if (prior.SourceIdentityHash == item.SourceIdentityHash && prior.BusinessScopeVersion == item.BusinessScopeVersion &&
            prior.FromLocalDate == item.FromLocalDate && prior.ToLocalDate == item.ToLocalDate)
            return;
        var updated = item with { FirstEnqueuedAtUtc = prior.FirstEnqueuedAtUtc, Attempts = 0,
            NextAttemptAtUtc = DateTime.UtcNow, LeaseOwner = null, LeaseExpiresAtUtc = null, CompletedAtUtc = null };
        var updatedContent = SerializeQueueItem(updated);
        if (!FitsQueueContent(updatedContent)) throw new InvalidOperationException("PRTG 近期狀態佇列項目超過 8 KiB。");
        existing.Content = updatedContent;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version++;
        ctx.SaveChanges();
    }

    private static List<(BlobRow Row, PrtgRecentStateChangeQueueItem Item)> ReadRecentStateChangeQueueRows(LfDbContext ctx)
    {
        var rows = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey.StartsWith(RecentStateChangeQueuePrefix))
            .OrderBy(b => b.BlobKey).Take(RecentStateChangeQueueCapacity + 1).ToList();
        if (rows.Count > RecentStateChangeQueueCapacity)
            throw new InvalidOperationException($"PRTG 近期狀態佇列超過 {RecentStateChangeQueueCapacity} 筆容量上限。");
        var result = new List<(BlobRow, PrtgRecentStateChangeQueueItem)>(rows.Count);
        foreach (var row in rows)
        {
            if (Encoding.UTF8.GetByteCount(row.Content) > RecentStateChangeQueueItemMaxBytes)
                throw new InvalidOperationException($"PRTG 近期狀態佇列資料列 {row.BlobKey} 超過 8 KiB。");
            result.Add((row, DeserializeQueueItem(row.Content)));
        }
        return result;
    }

    private static string RecentStateChangeQueueKey(long deviceObjid) =>
        RecentStateChangeQueuePrefix + deviceObjid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string SerializeQueueItem(PrtgRecentStateChangeQueueItem item) => JsonSerializer.Serialize(item);
    private static PrtgRecentStateChangeQueueItem DeserializeQueueItem(string content) =>
        JsonSerializer.Deserialize<PrtgRecentStateChangeQueueItem>(content) ??
        throw new InvalidOperationException("PRTG 近期狀態佇列資料列無效。");
    private static bool FitsQueueContent(string content) => Encoding.UTF8.GetByteCount(content) <= RecentStateChangeQueueItemMaxBytes;

    /// <summary>
    /// 依補充對照表＋內建對照表（<see cref="PrtgSensorTypeCategoryMap.Resolve"/>）重算自動分類。
    /// 候選列＝category 為 null 或 category_source 為 auto；目標分類與現值相同不寫，
    /// 不同則寫入並標 auto；auto 列的 type 已不在任何對照表時把分類與來源清回 null。
    /// 人工指定的分類（來源非 auto 且 category 非 null）永不動。回傳實際改動列數。
    /// </summary>
    public int ApplyAutoCategories(IReadOnlyDictionary<string, string> overrides)
    {
        List<(long Objid, string? Category)> toUpdate;
        using (var ctx = _contextFactory())
        {
            var candidates = ctx.PrtgSensors
                .AsNoTracking()
                .Where(s => s.Category == null || s.CategorySource == PrtgCategorySources.Auto)
                .Select(s => new { s.Objid, s.SensorType, s.Category, s.CategorySource })
                .ToList();

            toUpdate = new List<(long Objid, string? Category)>();
            foreach (var item in candidates)
            {
                var target = PrtgSensorTypeCategoryMap.Resolve(item.SensorType, overrides);
                if (NeedsCategoryChange(item.Category, item.CategorySource, target))
                    toUpdate.Add((item.Objid, target));
            }
        }

        if (toUpdate.Count == 0) return 0;

        return BatchWrite(toUpdate, (ctx, batch) =>
        {
            var map = batch.ToDictionary(x => x.Objid, x => x.Category);
            var ids = batch.Select(x => x.Objid).ToList();
            // 寫入前重取並再判一次：讀取後到寫入前若被人工改成非 auto，不可覆蓋
            var rows = ctx.PrtgSensors
                .Where(s => ids.Contains(s.Objid) &&
                            (s.Category == null || s.CategorySource == PrtgCategorySources.Auto))
                .ToList();

            var changed = 0;
            foreach (var row in rows)
            {
                var target = map[row.Objid];
                if (!NeedsCategoryChange(row.Category, row.CategorySource, target)) continue;

                row.Category = target;
                row.CategorySource = target == null ? null : PrtgCategorySources.Auto;
                changed++;
            }

            if (changed > 0) IncrementPrtgCatalogueDataRevision(ctx);
            ctx.SaveChanges();
            return changed;
        });
    }

    /// <summary>候選列（category null 或來源 auto）是否需要改寫成目標分類。</summary>
    private static bool NeedsCategoryChange(string? current, string? source, string? target)
    {
        if (target == null)
            return current != null; // 候選列中 category 非 null 者必為 auto 列（type 被移出對照表）
        return !string.Equals(current, target, StringComparison.Ordinal) ||
               source != PrtgCategorySources.Auto;
    }

    /// <summary>
    /// PRTG 狀態變更寫入。以 (sensor_objid, changed_at) 為去重依據，已存在者跳過不新增，回傳實際新增列數。
    /// 查詢既有列時分批查（每批取 sensor_objid 集合與時間範圍比對），避免全表載入記憶體。
    /// </summary>
    public int AppendStateChanges(IReadOnlyList<PrtgStateChangeRow> changes)
    {
        var now = DateTime.Now;
        return BatchWrite(changes, (ctx, batch) =>
        {
            var sensorIds = batch.Select(c => c.SensorObjid).Distinct().ToList();
            var minTime = batch.Min(c => c.ChangedAt);
            var maxTime = batch.Max(c => c.ChangedAt);

            var existingKeys = ctx.PrtgStateChanges
                .Where(c => sensorIds.Contains(c.SensorObjid) && c.ChangedAt >= minTime && c.ChangedAt <= maxTime)
                .Select(c => new { c.SensorObjid, c.ChangedAt })
                .ToList()
                .Select(c => (c.SensorObjid, c.ChangedAt))
                .ToHashSet();

            var addedCount = 0;
            foreach (var item in batch)
            {
                var key = (item.SensorObjid, item.ChangedAt);
                if (existingKeys.Contains(key)) continue;

                existingKeys.Add(key);
                ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
                {
                    SensorObjid = item.SensorObjid,
                    ChangedAt = item.ChangedAt,
                    Status = item.Status,
                    PrevStatus = item.PrevStatus,
                    Message = item.Message,
                    Quality = item.Quality,
                    CreatedAt = item.CreatedAt != default ? item.CreatedAt : now
                });
                addedCount++;
            }

            ctx.SaveChanges();
            return addedCount;
        });
    }

    /// <summary>
    /// PRTG 每小時聚合數值 upsert。自然鍵為 (sensor_objid, period_start)。已存在則更新量測欄位與 CreatedAt（供保留期清理），不存在則新增。
    /// </summary>
    public int UpsertValues(IReadOnlyList<PrtgValueRow> values)
    {
        var now = DateTime.Now;
        return BatchWrite(values, (ctx, batch) =>
        {
            var sensorIds = batch.Select(v => v.SensorObjid).Distinct().ToList();
            var minTime = batch.Min(v => v.PeriodStart);
            var maxTime = batch.Max(v => v.PeriodStart);

            var existing = ctx.PrtgValues
                .Where(v => sensorIds.Contains(v.SensorObjid) && v.PeriodStart >= minTime && v.PeriodStart <= maxTime)
                .ToList()
                .GroupBy(v => (v.SensorObjid, v.PeriodStart))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in batch)
            {
                var key = (item.SensorObjid, item.PeriodStart);
                var writeTime = item.CreatedAt != default ? item.CreatedAt : now;

                if (existing.TryGetValue(key, out var row))
                {
                    row.AvgValue = item.AvgValue;
                    row.MinValue = item.MinValue;
                    row.MaxValue = item.MaxValue;
                    row.Coverage = item.Coverage;
                    row.Quality = item.Quality;
                    row.CreatedAt = writeTime; // 每次覆蓋都更新，保留期依它清理
                }
                else
                {
                    var newRow = new PrtgValueRow
                    {
                        SensorObjid = item.SensorObjid,
                        PeriodStart = item.PeriodStart,
                        AvgValue = item.AvgValue,
                        MinValue = item.MinValue,
                        MaxValue = item.MaxValue,
                        Coverage = item.Coverage,
                        Quality = item.Quality,
                        CreatedAt = writeTime
                    };
                    ctx.PrtgValues.Add(newRow);
                    existing[key] = newRow;
                }
            }

            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// 合併快照計算出的每小時取樣數值（Quality == sampled）。
    /// 自然鍵為 (SensorObjid, PeriodStart)：
    /// 1. 無既有列 → 新增（原樣寫入，計入回傳數）。
    /// 2. 既有列 Quality == sampled → 依樣本數（Coverage 作為代理權重）加權平均合併，計入回傳數。
    /// 3. 既有列 Quality != sampled（如 ok 等精確值）→ 不動（精確值優先），該列不計入回傳數。
    /// 回傳實際寫入或合併的列數。
    /// </summary>
    public int MergeSampledValues(IReadOnlyList<PrtgValueRow> values, string? sampledBatchId = null)
    {
        if (sampledBatchId != null)
        {
            if (!Guid.TryParse(sampledBatchId, out var id))
                throw new ArgumentException("快照批次識別必須是 GUID。", nameof(sampledBatchId));
            sampledBatchId = id.ToString("N");
        }
        var now = DateTime.Now;
        return BatchWriteAtomic(values, (ctx, batch) =>
        {
            var sensorIds = batch.Select(v => v.SensorObjid).Distinct().ToList();
            var minTime = batch.Min(v => v.PeriodStart);
            var maxTime = batch.Max(v => v.PeriodStart);

            var existing = ctx.PrtgValues
                .Where(v => sensorIds.Contains(v.SensorObjid) && v.PeriodStart >= minTime && v.PeriodStart <= maxTime)
                .ToList()
                .GroupBy(v => (v.SensorObjid, v.PeriodStart))
                .ToDictionary(g => g.Key, g => g.First());

            var writtenOrMergedCount = 0;

            foreach (var item in batch)
            {
                var key = (item.SensorObjid, item.PeriodStart);
                var writeTime = item.CreatedAt != default ? item.CreatedAt : now;

                if (!existing.TryGetValue(key, out var row))
                {
                    var newRow = new PrtgValueRow
                    {
                        SensorObjid = item.SensorObjid,
                        PeriodStart = item.PeriodStart,
                        AvgValue = item.AvgValue,
                        MinValue = item.MinValue,
                        MaxValue = item.MaxValue,
                        Coverage = item.Coverage,
                        Quality = !string.IsNullOrEmpty(item.Quality) ? item.Quality : PrtgDataQuality.Sampled,
                        CreatedAt = writeTime
                    };
                    ctx.PrtgValues.Add(newRow);
                    existing[key] = newRow;
                    writtenOrMergedCount++;
                }
                else if (string.Equals(row.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase))
                {
                    var oldAvg = row.AvgValue;
                    var newAvg = item.AvgValue;
                    var oldCov = row.Coverage;
                    var newCov = item.Coverage;

                    // Coverage 相加後夾在 100 以內；任一為 null 時退化成有值的那個
                    double? mergedCov;
                    if (oldCov.HasValue && newCov.HasValue)
                    {
                        mergedCov = Math.Min(100.0, oldCov.Value + newCov.Value);
                    }
                    else
                    {
                        mergedCov = oldCov ?? newCov;
                    }

                    // 加權平均用 (oldAvg * oldCov + newAvg * newCov) / (oldCov + newCov)，任一為 null 時退化成有值的那個
                    double? mergedAvg;
                    if (oldAvg == null)
                    {
                        mergedAvg = newAvg;
                    }
                    else if (newAvg == null)
                    {
                        mergedAvg = oldAvg;
                    }
                    else if (oldCov.HasValue && newCov.HasValue && (oldCov.Value + newCov.Value > 0))
                    {
                        mergedAvg = (oldAvg.Value * oldCov.Value + newAvg.Value * newCov.Value) / (oldCov.Value + newCov.Value);
                    }
                    else if (oldCov.HasValue && !newCov.HasValue)
                    {
                        mergedAvg = oldAvg;
                    }
                    else if (!oldCov.HasValue && newCov.HasValue)
                    {
                        mergedAvg = newAvg;
                    }
                    else
                    {
                        mergedAvg = (oldAvg.Value + newAvg.Value) / 2.0;
                    }

                    // MinValue 取兩者最小、MaxValue 取兩者最大（null 視為缺席）
                    double? mergedMin = (row.MinValue.HasValue && item.MinValue.HasValue)
                        ? Math.Min(row.MinValue.Value, item.MinValue.Value)
                        : row.MinValue ?? item.MinValue;

                    double? mergedMax = (row.MaxValue.HasValue && item.MaxValue.HasValue)
                        ? Math.Max(row.MaxValue.Value, item.MaxValue.Value)
                        : row.MaxValue ?? item.MaxValue;

                    row.AvgValue = mergedAvg;
                    row.MinValue = mergedMin;
                    row.MaxValue = mergedMax;
                    row.Coverage = mergedCov;
                    row.CreatedAt = writeTime;

                    writtenOrMergedCount++;
                }
                // 若既有列 Quality != Sampled（即 ok 等精確值）：不動，該列不計入回傳數
            }

            ctx.SaveChanges();
            return writtenOrMergedCount;
        }, sampledBatchId);
    }

    /// <summary>
    /// PRTG 主機對應按日寫入。該日期的對應整份就地取代——先刪除該 map_date 的所有列，再寫入新的一批。
    /// 傳入空清單時仍然清空該日，回傳 0。
    /// </summary>
    public int ReplaceHostMapForDate(DateTime mapDate, IReadOnlyList<PrtgHostMapRow> rows)
    {
        var targetDate = mapDate.Date;
        var list = rows?.ToList() ?? new List<PrtgHostMapRow>();
        var now = DateTime.Now;

        // 刪除與全部寫入放在同一個交易：刪完、寫入前行程被回收或寫入中途失敗時整批回滾，
        // 不會留下「該日對應整批消失」的空日。SQL Server 開了連線重試，自開交易必須包在執行策略內。
        using var ctx = _contextFactory();
        var strategy = ctx.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            ctx.ChangeTracker.Clear();
            using var tx = ctx.Database.BeginTransaction();
            ctx.PrtgHostMaps.Where(m => m.MapDate == targetDate).ExecuteDelete();

            var written = 0;
            for (var offset = 0; offset < list.Count; offset += UpsertBatchSize)
            {
                var count = Math.Min(UpsertBatchSize, list.Count - offset);
                foreach (var item in list.GetRange(offset, count))
                {
                    ctx.PrtgHostMaps.Add(new PrtgHostMapRow
                    {
                        MapDate = targetDate,
                        DeviceObjid = item.DeviceObjid,
                        Ip = item.Ip,
                        HostId = item.HostId,
                        HostName = item.HostName,
                        MapStatus = item.MapStatus,
                        Note = item.Note,
                        CreatedAt = item.CreatedAt != default ? item.CreatedAt : now
                    });
                }

                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
                written += count;
            }

            IncrementHostMapDataRevision(ctx);
            tx.Commit();
            return written;
        });
    }

    /// <summary>
    /// 保留期清理：對象三張表（lf_prtg_values、lf_prtg_state_changes、lf_prtg_host_map），
    /// 皆依 created_at &lt; 今天減 retentionDays 刪除。
    /// lf_prtg_devices 與 lf_prtg_sensors 不依保留期清（結構鏡像是現況；過期列由結構同步自己清，見 DeleteDevicesNotSyncedSince／DeleteSensorsNotSyncedSince）。
    /// </summary>
    public int Prune(int retentionDays) => Prune(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var total = 0;

        // lf_prtg_devices 與 lf_prtg_sensors 不依保留期清（過期列由結構同步自己清）。
        if (total < maxRows)
        {
            total += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgValues
                    .Where(v => v.CreatedAt < cutoff)
                    .OrderBy(v => v.Id)
                    .Select(v => v.Id)
                    .Take(take)
                    .ToList(),
                (ctx, ids) => ctx.PrtgValues.Where(v => ids.Contains(v.Id)).ExecuteDelete(),
                ctx => ctx.PrtgValues.Count(v => v.CreatedAt < cutoff),
                "PRTG 數值", maxRows - total, batchSize);
        }

        if (total < maxRows)
        {
            total += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgStateChanges
                    .Where(c => c.CreatedAt < cutoff)
                    .OrderBy(c => c.Id)
                    .Select(c => c.Id)
                    .Take(take)
                    .ToList(),
                (ctx, ids) => ctx.PrtgStateChanges.Where(c => ids.Contains(c.Id)).ExecuteDelete(),
                ctx => ctx.PrtgStateChanges.Count(c => c.CreatedAt < cutoff),
                "PRTG 狀態變更", maxRows - total, batchSize);
        }

        if (total < maxRows)
        {
            total += BatchedPrune.Run<(DateTime MapDate, long DeviceObjid)>(
                _contextFactory,
                (ctx, take) => ctx.PrtgHostMaps
                    .Where(m => m.CreatedAt < cutoff)
                    .OrderBy(m => m.MapDate)
                    .ThenBy(m => m.DeviceObjid)
                    .Select(m => new { m.MapDate, m.DeviceObjid })
                    .Take(take)
                    .ToList()
                    .Select(m => (m.MapDate, m.DeviceObjid))
                    .ToList(),
                (ctx, keys) =>
                {
                    var strategy = ctx.Database.CreateExecutionStrategy();
                    return strategy.Execute(() =>
                    {
                        ctx.ChangeTracker.Clear();
                        using var tx = ctx.Database.BeginTransaction();
                        var deleted = 0;
                        foreach (var g in keys.GroupBy(k => k.MapDate))
                        {
                            var deviceIds = g.Select(k => k.DeviceObjid).ToList();
                            deleted += ctx.PrtgHostMaps
                                .Where(m => m.MapDate == g.Key && deviceIds.Contains(m.DeviceObjid))
                                .ExecuteDelete();
                        }
                        if (deleted > 0) IncrementHostMapDataRevision(ctx);
                        tx.Commit();
                        return deleted;
                    });
                },
                ctx => ctx.PrtgHostMaps.Count(m => m.CreatedAt < cutoff),
                "PRTG 主機對應", maxRows - total, batchSize);
        }

        if (total < maxRows)
        {
            // 本地復原佇列最多重播 30 日；即使使用者縮短原始資料保留期，批次鍵也必須留得更久。
            var batchCutoff = cutoff < DateTime.Today.AddDays(-32) ? cutoff : DateTime.Today.AddDays(-32);
            total += BatchedPrune.Run<string>(
                _contextFactory,
                (ctx, take) => ctx.PrtgSampledBatches
                    .Where(b => b.CreatedAt < batchCutoff)
                    .OrderBy(b => b.CreatedAt)
                    .Select(b => b.BatchId)
                    .Take(take)
                    .ToList(),
                (ctx, ids) => ctx.PrtgSampledBatches.Where(b => ids.Contains(b.BatchId)).ExecuteDelete(),
                ctx => ctx.PrtgSampledBatches.Count(b => b.CreatedAt < batchCutoff),
                "PRTG sampled 批次識別", maxRows - total, batchSize);
        }

        return total;
    }

    /// <summary>
    /// 清除本趟全站裝置同步沒刷新到的鏡像列（<c>SyncedAt &lt; syncStartedAt</c>），即 PRTG 端已不存在的裝置。
    /// 只動 <c>lf_prtg_devices</c>；人工對應、主機對應、感測器、狀態變更與數值表一律不碰。
    /// 呼叫端必須保證本趟裝置階段是完整收斂的全站同步，否則「沒刷新到」不等於「已被刪除」。
    /// </summary>
    public PrtgStaleDeleteResult DeleteDevicesNotSyncedSince(DateTime syncStartedAt)
    {
        using var __perf = _performance.Measure("prtg:DeleteStaleDevices");
        using var ctx = _contextFactory();
        var total = ctx.PrtgDevices.Count();
        var stale = ctx.PrtgDevices.Count(d => d.SyncedAt < syncStartedAt);
        if (stale == 0)
        {
            return new PrtgStaleDeleteResult(total, 0, 0, false);
        }

        // 安全保險（常數門檻，刻意不做成設定）：過期列超過鏡像一半就一列都不刪。
        // PRTG API 帳號權限被縮小、或查詢被代理截斷時，回傳會合法地少一大塊且分頁照樣收斂，
        // 那不代表裝置被刪了；此時照刪會把大半鏡像連同下游對應一起清掉。
        if (stale * 2 > total)
        {
            return new PrtgStaleDeleteResult(total, stale, 0, true);
        }

        using var probe = _contextFactory();
        var deleted = probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var write = _contextFactory();
            using var transaction = write.Database.BeginTransaction();
            var count = write.PrtgDevices.Where(d => d.SyncedAt < syncStartedAt).ExecuteDelete();
            if (count > 0)
            {
                IncrementPrtgCatalogueDataRevision(write);
                write.SaveChanges();
            }
            transaction.Commit();
            return count;
        });
        return new PrtgStaleDeleteResult(total, stale, deleted, false);
    }

    /// <summary>
    /// 清除本趟感測器同步沒刷新到的鏡像列（<c>SyncedAt &lt; syncStartedAt</c>），即取數範圍外或 PRTG 端已不存在的感測器，回傳刪除數。
    /// 只動 <c>lf_prtg_sensors</c>；狀態變更、數值與快照表一律不碰（交給保留期）。
    /// 本方法不複製範圍縮小／無基準門檻；呼叫端必須先以 <see cref="PrtgScopePurge.CheckScope"/>
    /// 與 <see cref="PrtgScopePurge.CheckShrink"/> 通過同一套清除保護，再呼叫本方法。
    /// 呼叫端也必須保證本趟感測器階段完整刷新了範圍內每一台裝置，否則「沒刷新到」不等於「不該留」。
    /// <para>
    /// <paramref name="graceDeviceObjids"/>＝本趟「查詢成功但回 0 顆」的裝置。PRTG 偶發回空陣列時，一次就把整台的感測器刪光
    /// 會讓當晚對那台主機的規則評估無聲失效；所以這些裝置底下 <c>SyncedAt &gt;= graceSince</c> 的列本趟先留著（回傳 GraceKept 供出聲）。
    /// 下一趟仍回 0 顆時，那些列的 SyncedAt 已早於 graceSince，照常刪除——PRTG 上真的移除了全部感測器的裝置最多多留一趟。
    /// </para>
    /// </summary>
    /// <param name="preserveDeviceObjids">
    /// 一律保留的裝置（<see cref="PrtgScopeResult.PreserveDeviceObjids"/>：守門未啟用時的守門與 corehealth 裝置）。
    /// 它們不在監看範圍、本趟不會被刷新，但守門自動偵測要靠它們的感測器鏡像。
    /// </param>
    public (int Deleted, int GraceKept) DeleteSensorsNotSyncedSince(
        DateTime syncStartedAt, IReadOnlyCollection<long> graceDeviceObjids, DateTime graceSince,
        IReadOnlyCollection<long> preserveDeviceObjids)
    {
        using var __perf = _performance.Measure("prtg:DeleteStaleSensors");
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var transaction = ctx.Database.BeginTransaction();
            var stale = ctx.PrtgSensors.Where(s => s.SyncedAt < syncStartedAt);
            // 守門裝置數量級很小（幾台），IN 清單不會撞參數上限。
            if (preserveDeviceObjids.Count > 0)
            {
                var preserve = preserveDeviceObjids.ToList();
                stale = stale.Where(s => !preserve.Contains(s.DeviceObjid));
            }
            int deleted;
            int kept;
            if (graceDeviceObjids.Count == 0)
            {
                deleted = stale.ExecuteDelete();
                kept = 0;
            }
            else
            {
                // 回 0 顆的裝置數量級很小；超出單批上限的照常刪。
                var grace = graceDeviceObjids.Take(DeviceQueryBatchSize).ToList();
                kept = stale.Count(s => grace.Contains(s.DeviceObjid) && s.SyncedAt >= graceSince);
                deleted = stale.Where(s => !(grace.Contains(s.DeviceObjid) && s.SyncedAt >= graceSince)).ExecuteDelete();
            }
            if (deleted > 0)
            {
                IncrementPrtgCatalogueDataRevision(ctx);
                ctx.SaveChanges();
            }
            transaction.Commit();
            return (deleted, kept);
        });
    }

    /// <summary>範圍外清除的基準（blob <see cref="PrtgScopeBaselineStore.BlobKey"/>），與鏡像共用同一個連線工廠</summary>
    public PrtgScopeBaselineStore ScopeBaseline() =>
        new(new EfJsonBlobStore(_contextFactory, PrtgScopeBaselineStore.BlobKey, _performance));

    /// <summary>
    /// 範圍外清除要刪的 sensor objid：數值與狀態變更表出現過、且對不到 <paramref name="keepDeviceObjids"/> 的 sensor。
    /// 兩表沒有裝置欄位，以感測器鏡像的 sensor→裝置 判斷；鏡像中已沒有的 sensor（先前已被清出感測器鏡像的範圍外裝置、
    /// 或 PRTG 端已刪除）視為範圍外。objid 本身就是保留裝置的列（裝置層級訊息）不刪。
    /// </summary>
    private static (List<long> Sensors, Dictionary<long, long> SensorDevice) OutOfScopeSensorObjids(
        LfDbContext ctx, IReadOnlySet<long> keepDeviceObjids)
    {
        if (keepDeviceObjids.Count == 0)
            throw new ArgumentException("保留裝置集合不得為空（等於清光全部數值與狀態變更）。", nameof(keepDeviceObjids));

        var sensorDevice = ctx.PrtgSensors.AsNoTracking()
            .Select(s => new { s.Objid, s.DeviceObjid })
            .ToList()
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().DeviceObjid);

        var candidates = ctx.PrtgValues.Select(v => v.SensorObjid).Distinct().ToList();
        candidates.AddRange(ctx.PrtgStateChanges.Select(c => c.SensorObjid).Distinct().ToList());

        var sensors = candidates
            .Distinct()
            .Where(id => !keepDeviceObjids.Contains(id)
                         && !(sensorDevice.TryGetValue(id, out var dev) && keepDeviceObjids.Contains(dev)))
            .OrderBy(id => id)
            .ToList();
        return (sensors, sensorDevice);
    }

    /// <summary>範圍外清除的預覽（只用 COUNT，不讀回資料列）</summary>
    public PrtgOutOfScopePreview PreviewOutOfScopeData(IReadOnlySet<long> keepDeviceObjids, int topDevices)
    {
        using var __perf = _performance.Measure("prtg:PreviewOutOfScope");
        using var ctx = _contextFactory();
        var (sensors, sensorDevice) = OutOfScopeSensorObjids(ctx, keepDeviceObjids);

        var values = 0;
        var stateChanges = 0;
        for (var offset = 0; offset < sensors.Count; offset += DeviceQueryBatchSize)
        {
            var batch = sensors.GetRange(offset, Math.Min(DeviceQueryBatchSize, sensors.Count - offset));
            values += ctx.PrtgValues.Count(v => batch.Contains(v.SensorObjid));
            stateChanges += ctx.PrtgStateChanges.Count(c => batch.Contains(c.SensorObjid));
        }

        var devices = sensors
            .Where(sensorDevice.ContainsKey)
            .Select(id => sensorDevice[id])
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var knownSensors = sensors.Count(sensorDevice.ContainsKey);

        var top = devices.Take(topDevices).ToList();
        var names = GetDeviceNamesByObjids(top);
        return new PrtgOutOfScopePreview(
            values, stateChanges, devices.Count,
            top.Select(id => names.TryGetValue(id, out var n) ? $"{n}（{id}）" : id.ToString()).ToList(),
            sensors.Count - knownSensors);
    }

    /// <summary>
    /// 刪除 <c>lf_prtg_values</c> 與 <c>lf_prtg_state_changes</c> 中裝置不在 <paramref name="keepDeviceObjids"/> 的列（判定見
    /// <see cref="OutOfScopeSensorObjids"/>），分批刪、不設單次上限，回傳各表筆數。
    /// 呼叫端必須先確認範圍可信（<see cref="PrtgScopePurge.CheckScope"/>）。
    /// </summary>
    public (int Values, int StateChanges) DeleteOutOfScopeData(IReadOnlySet<long> keepDeviceObjids)
    {
        using var __perf = _performance.Measure("prtg:DeleteOutOfScope");
        List<long> sensors;
        using (var ctx = _contextFactory())
        {
            sensors = OutOfScopeSensorObjids(ctx, keepDeviceObjids).Sensors;
        }

        var values = 0;
        var stateChanges = 0;
        for (var offset = 0; offset < sensors.Count; offset += DeviceQueryBatchSize)
        {
            var batch = sensors.GetRange(offset, Math.Min(DeviceQueryBatchSize, sensors.Count - offset));
            values += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgValues.Where(v => batch.Contains(v.SensorObjid))
                    .OrderBy(v => v.Id).Select(v => v.Id).Take(take).ToList(),
                (ctx, ids) => ctx.PrtgValues.Where(v => ids.Contains(v.Id)).ExecuteDelete(),
                _ => 0,
                "PRTG 監看範圍外數值", int.MaxValue);
            stateChanges += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgStateChanges.Where(c => batch.Contains(c.SensorObjid))
                    .OrderBy(c => c.Id).Select(c => c.Id).Take(take).ToList(),
                (ctx, ids) => ctx.PrtgStateChanges.Where(c => ids.Contains(c.Id)).ExecuteDelete(),
                _ => 0,
                "PRTG 監看範圍外狀態變更", int.MaxValue);
        }
        return (values, stateChanges);
    }

    /// <summary>
    /// 取得所有 PRTG 裝置鏡像清單（唯讀查詢）。
    /// </summary>
    public List<PrtgDeviceRow> GetAllDevices()
    {
        using var __perf = _performance.Measure("prtg:GetAllDevices");
        using var ctx = _contextFactory();
        return ctx.PrtgDevices.AsNoTracking().ToList();
    }

    /// <summary>
    /// 依 objid 集合取得 PRTG 裝置名稱（唯讀查詢，單次查詢；對不到的 objid 不在結果內）。
    /// </summary>
    public Dictionary<long, string> GetDeviceNamesByObjids(IReadOnlyCollection<long> objids)
    {
        using var __perf = _performance.Measure("prtg:GetDeviceNamesByObjids");
        if (objids.Count == 0) return new Dictionary<long, string>();
        using var ctx = _contextFactory();
        return ctx.PrtgDevices
            .AsNoTracking()
            .Where(d => objids.Contains(d.Objid))
            .Select(d => new { d.Objid, d.Name })
            .ToDictionary(d => d.Objid, d => d.Name);
    }

    /// <summary>
    /// 取得所有 PRTG 感測器鏡像清單（唯讀查詢）。
    /// </summary>
    public List<PrtgSensorRow> GetAllSensors()
    {
        using var __perf = _performance.Measure("prtg:GetAllSensors");
        using var ctx = _contextFactory();
        return ctx.PrtgSensors.AsNoTracking().ToList();
    }

    /// <summary>小範圍資料流驗證只讀候選裝置的鏡像，避免載入全站感測器。</summary>
    public List<PrtgSensorRow> GetSensorsForDevices(IReadOnlyCollection<long> deviceObjids)
    {
        if (deviceObjids.Count == 0) return new List<PrtgSensorRow>();
        var ids = deviceObjids.Take(20).ToArray();
        using var ctx = _contextFactory();
        return ctx.PrtgSensors.AsNoTracking()
            .Where(s => ids.Contains(s.DeviceObjid))
            .OrderBy(s => s.DeviceObjid).ThenBy(s => s.Objid)
            .ToList();
    }

    /// <summary>候選 sensor 僅限 as-of 當日成功映射至啟用主機的 device，並在 SQL 端計數與分頁。</summary>
    public (int Total, List<(long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)> Rows) GetCurrentReadinessSensors(
        IReadOnlyCollection<long> activeHostIds, DateTime mapDate, int offset, int take)
    {
        if (activeHostIds.Count == 0) return (0, new());
        using var ctx = _contextFactory();
        var query = from sensor in ctx.PrtgSensors.AsNoTracking()
                    join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                    join map in ctx.PrtgHostMaps.AsNoTracking() on device.Objid equals map.DeviceObjid
                    where sensor.Category == PrtgSensorCategories.Disk && map.MapDate == mapDate
                        && map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue
                        && activeHostIds.Contains(map.HostId.Value)
                    select new { Sensor = sensor, Device = device, HostId = map.HostId!.Value };
        var total = query.Select(x => x.Sensor.Objid).Distinct().Count();
        var rows = query.OrderBy(x => x.Sensor.Objid).Skip(offset).Take(take)
            .Select(x => new { x.Sensor.Objid, x.Sensor.DeviceObjid, x.HostId, x.Sensor.Name,
                x.Sensor.SensorType, x.Sensor.Category, x.Sensor.Unit, SensorPaused = x.Sensor.Paused,
                DevicePaused = x.Device.Paused })
            .ToList();
        return (total, rows.Select(x => (x.Objid, x.DeviceObjid, x.HostId, x.Name, x.SensorType,
            x.Category ?? "", x.Unit, x.SensorPaused, x.DevicePaused)).ToList());
    }

    /// <summary>SQL-filtered lookup for one current disk candidate; never materializes the full sensor set.</summary>
    public (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)? GetCurrentReadinessSensorById(
        long sensorObjid, IReadOnlyCollection<long> activeHostIds, DateTime throughDate, DateTime fromDate)
    {
        if (sensorObjid <= 0 || activeHostIds.Count == 0) return null;
        var row = PrtgSqlServerIdScope.Execute(_contextFactory, activeHostIds, new[] { sensorObjid },
            (ctx, hosts, sensors, hostScope, sensorScope) =>
            {
                var query = BuildLatestMappedReadinessSensorsQuery(ctx, hosts, throughDate, fromDate,
                    sensors, hostScope, sensorScope);
                return query.Where(x => x.Sensor.Objid == sensorObjid)
                    .Select(x => new { x.Sensor.Objid, x.Sensor.DeviceObjid, x.HostId, x.Sensor.Name,
                        x.Sensor.SensorType, x.Sensor.Category, x.Sensor.Unit,
                        SensorPaused = x.Sensor.Paused, DevicePaused = x.Device.Paused })
                    .FirstOrDefault();
            });
        return row is null ? null : (row.Objid, row.DeviceObjid, row.HostId, row.Name, row.SensorType,
            row.Category ?? "", row.Unit, row.SensorPaused, row.DevicePaused);
    }

    /// <summary>Compatibility overload; new callers should pass the explicit lookback start date.</summary>
    public (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)? GetCurrentReadinessSensorById(
        long sensorObjid, IReadOnlyCollection<long> activeHostIds, DateTime throughDate) =>
        GetCurrentReadinessSensorById(sensorObjid, activeHostIds, throughDate, throughDate.Date.AddDays(-28));

    /// <summary>Bounded disk candidates using each device's newest successful mapping in the lookback window.</summary>
    public (int Total, List<(long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)> Rows) GetLatestMappedReadinessSensors(
        IReadOnlyCollection<long> activeHostIds, DateTime throughDate, DateTime fromDate, int take, int offset = 0,
        IReadOnlyCollection<long>? selectedSensorObjids = null)
    {
        if (activeHostIds.Count == 0 || take <= 0) return (0, new());
        long[]? filterSensorIds = null;
        if (selectedSensorObjids is not null)
        {
            filterSensorIds = selectedSensorObjids.Where(id => id > 0).Distinct().ToArray();
            if (filterSensorIds.Length == 0) return (0, new());
        }

        return PrtgSqlServerIdScope.Execute(_contextFactory, activeHostIds, filterSensorIds,
            (ctx, hosts, sensors, hostScope, sensorScope) =>
            {
                var query = BuildLatestMappedReadinessSensorsQuery(ctx, hosts, throughDate, fromDate,
                    sensors, hostScope, sensorScope);
                var total = query.Select(x => x.Sensor.Objid).Distinct().Count();
                if (total == 0 || offset >= total) return (total, new());
                var rows = query.OrderBy(x => x.Sensor.Objid).Skip(Math.Max(0, offset)).Take(take)
                    .Select(x => new { x.Sensor.Objid, x.Sensor.DeviceObjid, x.HostId, x.Sensor.Name, x.Sensor.SensorType,
                        x.Sensor.Category, x.Sensor.Unit, SensorPaused = x.Sensor.Paused, DevicePaused = x.Device.Paused }).ToList();
                return (total, rows.Select(x => (x.Objid, x.DeviceObjid, x.HostId, x.Name, x.SensorType,
                    x.Category ?? "", x.Unit, x.SensorPaused, x.DevicePaused)).ToList());
            });
    }

    /// <summary>
    /// 為多頁評估擷取有界且不可變的候選集合。SQL 總數只計一次，完整排序列也受上限保護，避免大量清單被載入記憶體。
    /// 有序列查詢定義實際捕獲集合，COUNT 僅核對筆數；這不代表資料庫整體同一時間點快照。評估各頁沿用同一批不可變列，
    /// 並在使用歷史資料前逐頁核對捕獲的最新映射日期、狀態與主機。
    /// </summary>
    internal (int Total, List<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)> Rows)
        GetLatestMappedReadinessSensorSnapshot(IReadOnlyCollection<long> activeHostIds, DateTime throughDate, DateTime fromDate,
            int maximumCandidates, IReadOnlyCollection<long>? selectedSensorObjids = null)
    {
        if (maximumCandidates <= 0 || maximumCandidates > PrtgDiskAssessmentService.MaximumCandidateSnapshotSize)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates), "候選快照上限只能是 1 到 15,000。");
        if (activeHostIds.Count == 0) return (0, new());
        long[]? filterSensorIds = selectedSensorObjids?.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (selectedSensorObjids is not null && filterSensorIds!.Length == 0) return (0, new());

        return PrtgSqlServerIdScope.Execute(_contextFactory, activeHostIds, filterSensorIds,
            (ctx, hosts, sensors, hostScope, sensorScope) =>
            {
                var query = BuildLatestMappedReadinessSensorsQuery(ctx, hosts, throughDate, fromDate,
                    sensors, hostScope, sensorScope);
                var total = query.Select(x => x.Sensor.Objid).Distinct().LongCount();
                if (total > maximumCandidates)
                    throw new InvalidOperationException($"PRTG 磁碟候選數 {total} 超過單次評估上限 {maximumCandidates}，已拒絕建立候選快照。");
                if (total == 0) return (0, new());

                var rows = query.OrderBy(x => x.Sensor.Objid).Take(maximumCandidates + 1)
                    .Select(x => new
                    {
                        x.Sensor.Objid,
                        x.Sensor.DeviceObjid,
                        x.HostId,
                        x.MapDate,
                        SensorTypeLength = x.Sensor.SensorType == null ? 0 : x.Sensor.SensorType.Length,
                        SensorType = x.Sensor.SensorType == null ? "" : x.Sensor.SensorType.Length > CandidateSnapshotSensorTypeMaxChars
                            ? x.Sensor.SensorType.Substring(0, CandidateSnapshotSensorTypeMaxChars) : x.Sensor.SensorType,
                        UnitLength = x.Sensor.Unit == null ? 0 : x.Sensor.Unit.Length,
                        Unit = x.Sensor.Unit == null ? null : x.Sensor.Unit.Length > CandidateSnapshotUnitMaxChars
                            ? x.Sensor.Unit.Substring(0, CandidateSnapshotUnitMaxChars) : x.Sensor.Unit,
                        SensorPaused = x.Sensor.Paused,
                        DevicePaused = x.Device.Paused
                    }).ToList();
                if (rows.Count > maximumCandidates || rows.Count != total || rows.Select(x => x.Objid).Distinct().Count() != rows.Count)
                    throw new InvalidOperationException("建立 PRTG 磁碟候選快照時集合筆數或唯一鍵改變；請重新開始評估。");
                if (rows.Any(x => x.SensorTypeLength > CandidateSnapshotSensorTypeMaxChars || x.UnitLength > CandidateSnapshotUnitMaxChars))
                    throw new InvalidOperationException("PRTG 磁碟候選 metadata 超過快照欄位上限；已拒絕建立快照。");
                var textBytes = rows.Sum(x => (long)Encoding.UTF8.GetByteCount(x.SensorType) +
                    (x.Unit is null ? 0 : Encoding.UTF8.GetByteCount(x.Unit)));
                if (textBytes > CandidateSnapshotTextMaxBytes)
                    throw new InvalidOperationException("PRTG 磁碟候選快照超過文字資料容量上限；已拒絕建立快照。");

                return ((int)total, rows.Select(x => (x.Objid, x.DeviceObjid, x.HostId, x.MapDate, Name: "", x.SensorType,
                    Category: PrtgSensorCategories.Disk, x.Unit, x.SensorPaused, x.DevicePaused)).ToList());
            });
    }

    /// <summary>
    /// Captures one range's candidate denominator with a single grouped sensor/map stream. A map row
    /// owns its day through the earlier of the next row or its inclusive 30-day lookback expiry.
    /// Rows with non-OK status or no host remain barriers; filtering them before interval construction
    /// would incorrectly resurrect older successful mappings.
    /// </summary>
    internal int[] GetReadinessRangeCandidateCounts(DateOnly fromDate, DateOnly throughDate,
        IReadOnlySet<long> activeHostIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeHostIds);
        var dayCount = throughDate.DayNumber - fromDate.DayNumber + 1;
        if (dayCount is < 1 or > 730)
            throw new ArgumentOutOfRangeException(nameof(throughDate), "候選日期範圍只能是 1 到 730 日。");

        var counts = new int[dayCount];

        var rangeStart = fromDate.ToDateTime(TimeOnly.MinValue);
        var rangeEnd = throughDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var mapStart = fromDate.AddDays(-PrtgDiskAssessmentService.CandidateMappingLookbackDays).ToDateTime(TimeOnly.MinValue);
        var differences = new long[dayCount + 1];
        var maximumRows = checked((long)PrtgDiskAssessmentService.MaximumCandidateSnapshotSize *
            (dayCount + PrtgDiskAssessmentService.CandidateMappingLookbackDays));
        long rowsRead = 0;
        long previousDeviceId = 0;
        RangeCandidateMapRow? previous = null;

        using var context = _contextFactory();
        var sensorCounts =
            from sensor in context.PrtgSensors.AsNoTracking()
            join device in context.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
            where sensor.Category == PrtgSensorCategories.Disk
            group sensor by sensor.DeviceObjid into groupRows
            select new { DeviceObjid = groupRows.Key, SensorCount = groupRows.LongCount() };

        var stream =
            from map in context.PrtgHostMaps.AsNoTracking()
            join sensorCount in sensorCounts on map.DeviceObjid equals sensorCount.DeviceObjid
            where map.MapDate >= mapStart && map.MapDate < rangeEnd
            orderby map.DeviceObjid, map.MapDate
            select new RangeCandidateMapRow(map.DeviceObjid, map.MapDate, map.MapStatus, map.HostId,
                sensorCount.SensorCount);

        foreach (var current in stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsRead = checked(rowsRead + 1);
            if (rowsRead > maximumRows)
                throw new InvalidOperationException("PRTG 磁碟範圍映射列超過有限掃描上限；已拒絕預覽。");
            if (current.MapDate.TimeOfDay != TimeSpan.Zero || current.MapDate < mapStart || current.MapDate >= rangeEnd)
                throw new InvalidOperationException("PRTG 磁碟範圍包含非午夜或超界日映射；已拒絕推定日期。");
            if (current.SensorCount <= 0)
                throw new InvalidOperationException("PRTG 磁碟範圍 sensor/device 分組筆數無效。");

            if (previous is not null)
            {
                if (current.DeviceObjid < previousDeviceId)
                    throw new InvalidOperationException("PRTG 磁碟範圍映射排序或唯一鍵無效。");
                if (current.DeviceObjid == previousDeviceId)
                {
                    if (current.MapDate <= previous.MapDate)
                        throw new InvalidOperationException("PRTG 磁碟範圍映射排序或唯一鍵無效。");
                    AddCandidateInterval(previous, current.MapDate);
                }
                else
                {
                    AddCandidateInterval(previous, rangeEnd);
                    previousDeviceId = current.DeviceObjid;
                }
            }
            else
            {
                previousDeviceId = current.DeviceObjid;
            }
            previous = current;
        }
        if (previous is not null) AddCandidateInterval(previous, rangeEnd);

        long running = 0;
        long total = 0;
        for (var index = 0; index < dayCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            running = checked(running + differences[index]);
            if (running < 0 || running > PrtgDiskAssessmentService.MaximumCandidateSnapshotSize)
                throw new InvalidOperationException($"PRTG 磁碟候選單日數量 {running} 超過上限 {PrtgDiskAssessmentService.MaximumCandidateSnapshotSize}。");
            counts[index] = checked((int)running);
            total = checked(total + running);
        }
        running = checked(running + differences[dayCount]);
        if (running != 0 || total > checked((long)PrtgDiskAssessmentService.MaximumCandidateSnapshotSize * 730))
            throw new InvalidOperationException("PRTG 磁碟範圍候選差分計數不一致。");
        return counts;

        void AddCandidateInterval(RangeCandidateMapRow row, DateTime nextMapDate)
        {
            if (!string.Equals(row.MapStatus, PrtgMapStatus.Ok, StringComparison.Ordinal) ||
                !row.HostId.HasValue || !activeHostIds.Contains(row.HostId.Value)) return;

            var start = row.MapDate < rangeStart ? rangeStart : row.MapDate;
            var expiry = row.MapDate.AddDays(PrtgDiskAssessmentService.CandidateMappingLookbackDays + 1);
            var end = nextMapDate < expiry ? nextMapDate : expiry;
            if (end > rangeEnd) end = rangeEnd;
            if (start >= end) return;
            var startIndex = DateOnly.FromDateTime(start).DayNumber - fromDate.DayNumber;
            var endIndex = DateOnly.FromDateTime(end).DayNumber - fromDate.DayNumber;
            differences[startIndex] = checked(differences[startIndex] + row.SensorCount);
            differences[endIndex] = checked(differences[endIndex] - row.SensorCount);
        }
    }

    /// <summary>Reads only the page's per-day slices in one candidate SELECT; counts are supplied by Core's private range operation.</summary>
    internal List<RangeReadinessCandidateRow> GetReadinessRangeCandidateRows(
        IReadOnlyCollection<long> activeHostIds, IReadOnlyList<RangeReadinessCandidateSlice> slices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeHostIds);
        ArgumentNullException.ThrowIfNull(slices);
        if (slices.Count > PrtgDiskAssessmentService.MaximumBatchSize || slices.Sum(x => x.Take) > PrtgDiskAssessmentService.MaximumBatchSize ||
            slices.Select(x => x.CompletedDay).Distinct().Count() != slices.Count ||
            slices.Any(x => x.Total is < 0 or > PrtgDiskAssessmentService.MaximumCandidateSnapshotSize || x.Offset < 0 || x.Take <= 0 || x.Take > PrtgDiskAssessmentService.MaximumBatchSize || (long)x.Offset + x.Take > x.Total))
            throw new ArgumentOutOfRangeException(nameof(slices), "範圍候選頁必須是總數不超過 100 的 Core 私有分頁切片。");
        if (activeHostIds.Count == 0 || slices.Count == 0) return new();

        return PrtgSqlServerIdScope.Execute(_contextFactory, activeHostIds, null,
            (context, hosts, _, hostIdScope, _) =>
            {
                IQueryable<RangeReadinessCandidateProjection>? combined = null;
                foreach (var slice in slices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var through = slice.CompletedDay.ToDateTime(TimeOnly.MinValue);
                    var from = through.AddDays(-PrtgDiskAssessmentService.CandidateMappingLookbackDays);
                    var branch = BuildLatestMappedReadinessSensorsQuery(context, hosts, through, from,
                            selectedSensorObjids: null, hostIdScopeQuery: hostIdScope)
                        .OrderBy(x => x.Sensor.Objid).Skip(slice.Offset).Take(slice.Take)
                        .Select(x => new RangeReadinessCandidateProjection
                        {
                            CompletedDay = through,
                            Objid = x.Sensor.Objid,
                            DeviceObjid = x.Sensor.DeviceObjid,
                            HostId = x.HostId,
                            MappingDate = x.MapDate,
                            NameLength = x.Sensor.Name == null ? 0 : x.Sensor.Name.Length,
                            Name = x.Sensor.Name == null ? "" : x.Sensor.Name.Substring(0, CandidateSnapshotNameMaxChars + 1),
                            SensorTypeLength = x.Sensor.SensorType == null ? 0 : x.Sensor.SensorType.Length,
                            SensorType = x.Sensor.SensorType == null ? "" : x.Sensor.SensorType.Substring(0, CandidateSnapshotSensorTypeFieldMaxChars + 1),
                            CategoryLength = x.Sensor.Category == null ? 0 : x.Sensor.Category.Length,
                            Category = x.Sensor.Category == null ? "" : x.Sensor.Category.Substring(0, CandidateSnapshotCategoryMaxChars + 1),
                            UnitLength = x.Sensor.Unit == null ? 0 : x.Sensor.Unit.Length,
                            Unit = x.Sensor.Unit == null ? null : x.Sensor.Unit.Substring(0, CandidateSnapshotUnitFieldMaxChars + 1),
                            Paused = x.Sensor.Paused,
                            DevicePaused = x.Device.Paused
                        });
                    combined = combined is null ? branch : combined.Concat(branch);
                }

                if (combined is null) return new List<RangeReadinessCandidateRow>();
                var projected = combined.OrderBy(x => x.CompletedDay).ThenBy(x => x.Objid).ToList();
                cancellationToken.ThrowIfCancellationRequested();
                var result = projected.Select(x =>
                {
                    if (x.NameLength > CandidateSnapshotNameMaxChars ||
                        x.Name.Length > CandidateSnapshotNameMaxChars ||
                        x.SensorTypeLength > CandidateSnapshotSensorTypeFieldMaxChars || x.SensorType.Length > CandidateSnapshotSensorTypeFieldMaxChars ||
                        x.CategoryLength > CandidateSnapshotCategoryMaxChars || x.Category.Length > CandidateSnapshotCategoryMaxChars ||
                        x.UnitLength > CandidateSnapshotUnitFieldMaxChars ||
                        x.Unit is { Length: > CandidateSnapshotUnitFieldMaxChars })
                        throw new InvalidOperationException("PRTG 磁碟範圍候選 metadata 超過正式欄位上限；已拒絕建立頁面。");
                    return new RangeReadinessCandidateRow(DateOnly.FromDateTime(x.CompletedDay), x.Objid,
                        x.DeviceObjid, x.HostId, x.MappingDate, x.Name, x.SensorType, x.Category,
                        x.Unit, x.Paused, x.DevicePaused);
                }).ToList();
                foreach (var slice in slices)
                {
                    var sliceRows = result.Where(row => row.CompletedDay == slice.CompletedDay).ToArray();
                    if (sliceRows.Length != slice.Take || sliceRows.Select(row => row.Objid).Distinct().Count() != sliceRows.Length ||
                        !sliceRows.Select(row => row.Objid).SequenceEqual(sliceRows.Select(row => row.Objid).OrderBy(id => id)))
                        throw new InvalidOperationException("PRTG 磁碟範圍候選頁筆數或排序於擷取期間改變；已拒絕整頁。");
                }
                var textBytes = result.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Name) +
                    Encoding.UTF8.GetByteCount(row.SensorType) + Encoding.UTF8.GetByteCount(row.Category) +
                    (row.Unit is null ? 0 : Encoding.UTF8.GetByteCount(row.Unit)));
                if (textBytes > CandidateSnapshotTextMaxBytes)
                    throw new InvalidOperationException("PRTG 磁碟範圍候選頁文字資料超過容量上限；已拒絕整頁。");
                return result;
            });
    }

    private sealed record RangeCandidateMapRow(long DeviceObjid, DateTime MapDate, string MapStatus,
        long? HostId, long SensorCount);

    private sealed class RangeReadinessCandidateProjection
    {
        public DateTime CompletedDay { get; init; }
        public long Objid { get; init; }
        public long DeviceObjid { get; init; }
        public long HostId { get; init; }
        public DateTime MappingDate { get; init; }
        public int NameLength { get; init; }
        public string Name { get; init; } = "";
        public int SensorTypeLength { get; init; }
        public string SensorType { get; init; } = "";
        public int CategoryLength { get; init; }
        public string Category { get; init; } = "";
        public int UnitLength { get; init; }
        public string? Unit { get; init; }
        public bool Paused { get; init; }
        public bool DevicePaused { get; init; }
    }

    internal sealed record RangeReadinessCandidateRow(DateOnly CompletedDay, long Objid, long DeviceObjid,
        long HostId, DateTime MappingDate, string Name, string SensorType, string Category, string? Unit,
        bool Paused, bool DevicePaused);

    internal sealed record RangeReadinessCandidateSlice(DateOnly CompletedDay, int Total, int Offset, int Take);

    internal static IQueryable<PrtgReadinessCandidateQueryRow> BuildLatestMappedReadinessSensorsQuery(
        LfDbContext ctx, IReadOnlyCollection<long> activeHostIds, DateTime throughDate, DateTime fromDate,
        IReadOnlyCollection<long>? selectedSensorObjids = null,
        IQueryable<long>? hostIdScopeQuery = null, IQueryable<long>? sensorIdScopeQuery = null)
    {
        var hostIds = activeHostIds as long[] ?? activeHostIds.ToArray();
        var filterSensorIds = selectedSensorObjids == null
            ? null
            : (selectedSensorObjids as long[] ?? selectedSensorObjids.ToArray());

        var sensorQuery = ctx.PrtgSensors.AsNoTracking().Where(s => s.Category == PrtgSensorCategories.Disk);
        if (filterSensorIds != null)
        {
            sensorQuery = sensorIdScopeQuery is null
                ? sensorQuery.Where(s => filterSensorIds.Contains(s.Objid))
                : sensorQuery.Where(s => sensorIdScopeQuery.Contains(s.Objid));
        }

        var latest = ctx.PrtgHostMaps.AsNoTracking()
            .Where(m => m.MapDate >= fromDate && m.MapDate <= throughDate)
            .GroupBy(m => m.DeviceObjid)
            .Select(g => new { DeviceObjid = g.Key, MapDate = g.Max(m => m.MapDate) });

        var mappedQuery = from sensor in sensorQuery
                          join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                          join last in latest on device.Objid equals last.DeviceObjid
                          join map in ctx.PrtgHostMaps.AsNoTracking() on new { last.DeviceObjid, last.MapDate } equals new { map.DeviceObjid, map.MapDate }
                          where map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue
                          select new PrtgReadinessCandidateQueryRow
                          {
                              Sensor = sensor,
                              Device = device,
                              HostId = map.HostId!.Value,
                              MapDate = last.MapDate
                          };
        return hostIdScopeQuery is null
            ? mappedQuery.Where(x => hostIds.Contains(x.HostId))
            : mappedQuery.Where(x => hostIdScopeQuery.Contains(x.HostId));
    }

    internal sealed class PrtgReadinessCandidateQueryRow
    {
        public PrtgSensorRow Sensor { get; init; } = null!;
        public PrtgDeviceRow Device { get; init; } = null!;
        public long HostId { get; init; }
        public DateTime MapDate { get; init; }
    }

    /// <summary>
    /// Global mirror counts computed by SQL COUNT aggregates; no sensor inventory is materialized.
    /// The 30-day map range uses lf_prtg_host_map's (map_date, device_objid) primary-key index;
    /// per-sensor hourly reads below use lf_prtg_values' unique (sensor_objid, period_start) index.
    /// </summary>
    public PrtgReadinessInventoryCounts GetReadinessInventoryCounts(IReadOnlyCollection<long> activeHostIds,
        IReadOnlyCollection<string> whitelistedTypes, DateTime throughDate, DateTime fromDate)
    {
        return PrtgSqlServerIdScope.Execute(_contextFactory, activeHostIds, null,
            (ctx, hosts, _, hostIdScopeQuery, _) =>
            {
                var candidates = ctx.PrtgSensors.AsNoTracking().Where(s => s.Category == PrtgSensorCategories.Disk);
                var inventory = BuildReadinessInventoryQuery(ctx, hosts, whitelistedTypes, throughDate, fromDate,
                    hostIdScopeQuery);
                var candidatesCount = candidates.Count();
                var mappedCount = inventory.Count(x => x.ActiveMapped);
                var whitelistCount = inventory.Count(x => x.Whitelisted);
                var pausedCount = inventory.Count(x => x.Paused);
                var conflictCount = inventory.Count(x => x.Conflict);
                var unmappedCount = inventory.Count(x => x.Unmapped);
                var disabledHostCount = inventory.Count(x => x.DisabledHost);
                return new PrtgReadinessInventoryCounts(candidatesCount, mappedCount, whitelistCount, pausedCount,
                    conflictCount, unmappedCount, disabledHostCount);
            });
    }

    internal static IQueryable<PrtgReadinessInventoryRow> BuildReadinessInventoryQuery(LfDbContext ctx,
        IReadOnlyCollection<long> activeHostIds, IReadOnlyCollection<string> whitelistedTypes,
        DateTime throughDate, DateTime fromDate, IQueryable<long>? activeHostIdScopeQuery = null)
    {
        var whitelistIsUnrestricted = whitelistedTypes.Count == 0;
        var normalizedTypes = whitelistedTypes.Select(t => t.ToUpperInvariant()).ToArray();
        var candidates = ctx.PrtgSensors.AsNoTracking().Where(s => s.Category == PrtgSensorCategories.Disk);
        var latest = ctx.PrtgHostMaps.AsNoTracking().Where(m => m.MapDate >= fromDate && m.MapDate <= throughDate)
            .GroupBy(m => m.DeviceObjid).Select(g => new { DeviceObjid = g.Key, MapDate = g.Max(m => m.MapDate) });
        var source = from sensor in candidates
                     join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                     join last in latest on device.Objid equals last.DeviceObjid into lastRows
                     from last in lastRows.DefaultIfEmpty()
                     join map in ctx.PrtgHostMaps.AsNoTracking() on new { DeviceObjid = device.Objid, MapDate = last == null ? (DateTime?)null : last.MapDate }
                         equals new { map.DeviceObjid, MapDate = (DateTime?)map.MapDate } into mapRows
                     from map in mapRows.DefaultIfEmpty()
                     select new
                     {
                         sensor.Objid,
                         sensor.SensorType,
                         SensorPaused = sensor.Paused,
                         DevicePaused = device.Paused,
                         MapStatus = map == null ? (string?)null : map.MapStatus,
                         HostId = map == null ? (long?)null : map.HostId
                     };

        if (activeHostIdScopeQuery is null)
        {
            return source.Select(x => new PrtgReadinessInventoryRow
            {
                Objid = x.Objid,
                ActiveMapped = x.MapStatus == PrtgMapStatus.Ok && x.HostId.HasValue && activeHostIds.Contains(x.HostId.Value),
                Whitelisted = whitelistIsUnrestricted || normalizedTypes.Contains((x.SensorType ?? "").ToUpper()),
                Paused = x.SensorPaused || x.DevicePaused,
                Conflict = x.MapStatus == PrtgMapStatus.Conflict,
                Unmapped = x.MapStatus == null || (x.MapStatus != PrtgMapStatus.Conflict &&
                    (x.MapStatus != PrtgMapStatus.Ok || !x.HostId.HasValue)),
                DisabledHost = x.MapStatus == PrtgMapStatus.Ok && x.HostId.HasValue &&
                    !activeHostIds.Contains(x.HostId.Value)
            });
        }

        return source.Select(x => new PrtgReadinessInventoryRow
        {
            Objid = x.Objid,
            ActiveMapped = x.MapStatus == PrtgMapStatus.Ok && x.HostId.HasValue && activeHostIdScopeQuery.Contains(x.HostId.Value),
            Whitelisted = whitelistIsUnrestricted || normalizedTypes.Contains((x.SensorType ?? "").ToUpper()),
            Paused = x.SensorPaused || x.DevicePaused,
            Conflict = x.MapStatus == PrtgMapStatus.Conflict,
            Unmapped = x.MapStatus == null || (x.MapStatus != PrtgMapStatus.Conflict &&
                (x.MapStatus != PrtgMapStatus.Ok || !x.HostId.HasValue)),
            DisabledHost = x.MapStatus == PrtgMapStatus.Ok && x.HostId.HasValue &&
                !activeHostIdScopeQuery.Contains(x.HostId.Value)
        });
    }

    public sealed record PrtgReadinessInventoryCounts(int CandidateSensors, int MappedActiveSensors,
        int WhitelistedSensors, int PausedSensors, int ConflictSensors, int UnmappedSensors, int DisabledHostSensors);

    internal sealed class PrtgReadinessInventoryRow
    {
        public long Objid { get; init; }
        public bool ActiveMapped { get; init; }
        public bool Whitelisted { get; init; }
        public bool Paused { get; init; }
        public bool Conflict { get; init; }
        public bool Unmapped { get; init; }
        public bool DisabledHost { get; init; }
    }

    /// <summary>只讀取指定 sensor 頁面的 28 日 hourly 列。</summary>
    public List<PrtgValueRow> GetReadinessValues(IReadOnlyCollection<long> sensorObjids, DateTime from, DateTime to)
    {
        if (sensorObjids.Count == 0) return new();
        using var ctx = _contextFactory();
        return ctx.PrtgValues.AsNoTracking().Where(v => sensorObjids.Contains(v.SensorObjid)
            && v.PeriodStart >= from && v.PeriodStart < to).OrderBy(v => v.SensorObjid).ThenBy(v => v.PeriodStart).ToList();
    }

    /// <summary>讀取指定 sensor 頁面所在 device 的歷史每日 mapping，沒有日期列時維持缺席。</summary>
    public List<PrtgHostMapRow> GetReadinessMaps(IReadOnlyCollection<long> deviceObjids, DateTime from, DateTime to)
    {
        if (deviceObjids.Count == 0) return new();
        using var ctx = _contextFactory();
        return ctx.PrtgHostMaps.AsNoTracking().Where(m => deviceObjids.Contains(m.DeviceObjid)
            && m.MapDate >= from && m.MapDate < to).ToList();
    }

    /// <summary>單次 IN 查詢的 device objid 上限（SQL Server 參數上限 2100，留足餘裕）</summary>
    private const int DeviceQueryBatchSize = 500;

    /// <summary>
    /// 取得指定 device 底下**未暫停** sensor 的現況狀態與分類（唯讀查詢，未回報主機的 PRTG 提示用）。
    /// device 清單每 <see cref="DeviceQueryBatchSize"/> 個分一批查，避免撞 SQL Server 參數上限。
    /// </summary>
    public List<(long DeviceObjid, string? Status, string? Category)> GetSensorStatesForDevices(
        IReadOnlyCollection<long> deviceObjids)
    {
        using var __perf = _performance.Measure("prtg:GetSensorStatesForDevices");
        var result = new List<(long DeviceObjid, string? Status, string? Category)>();
        var ids = deviceObjids.Distinct().ToList();

        for (var offset = 0; offset < ids.Count; offset += DeviceQueryBatchSize)
        {
            var batch = ids.GetRange(offset, Math.Min(DeviceQueryBatchSize, ids.Count - offset));
            using var ctx = _contextFactory();
            var rows = ctx.PrtgSensors
                .AsNoTracking()
                .Where(s => batch.Contains(s.DeviceObjid) && !s.Paused)
                .Select(s => new { s.DeviceObjid, s.Status, s.Category })
                .ToList();
            result.AddRange(rows.Select(r => (r.DeviceObjid, r.Status, r.Category)));
        }

        return result;
    }

    /// <summary>
    /// 結構鏡像最近一次完整同步的時間（lf_prtg_devices.synced_at 最大值）；表為空時回 null。
    /// 取裝置表而不是感測器表：裝置只有結構同步會寫，感測器還會被快照服務的範圍補抓零星寫入當下時間——
    /// 拿感測器的最大值，同步連壞幾天時只要補抓過一台就會被當成「剛同步過」。
    /// </summary>
    public DateTime? GetLatestStructureSyncedAt()
    {
        using var __perf = _performance.Measure("prtg:GetLatestStructureSyncedAt");
        using var ctx = _contextFactory();
        return ctx.PrtgDevices.Max(d => (DateTime?)d.SyncedAt);
    }

    /// <summary>取得指定期間的 hourly 數值（依 sensor 與時間排序，匯出用）。</summary>
    public List<PrtgValueRow> GetValues(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetValues");
        using var ctx = _contextFactory();
        return ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .OrderBy(v => v.SensorObjid)
            .ThenBy(v => v.PeriodStart)
            .ToList();
    }

    /// <summary>讀回單一感測器一天的數值，避免驗證時掃描其他主機。</summary>
    public List<PrtgValueRow> GetValuesForSensor(long sensorObjid, DateTime fromInclusive, DateTime toExclusive)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgValues.AsNoTracking()
            .Where(v => v.SensorObjid == sensorObjid && v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .OrderBy(v => v.PeriodStart)
            .ToList();
    }

    /// <summary>
    /// 取得 sensor 的 objid 與暫停狀態（唯讀，只取這兩欄）。
    /// 供歷史回填使用：回填不重跑結構同步，sensor 清單改從鏡像讀。
    /// </summary>
    public List<(long Objid, bool Paused)> GetSensorTargets()
    {
        using var __perf = _performance.Measure("prtg:GetSensorTargets");
        using var ctx = _contextFactory();
        return ctx.PrtgSensors.AsNoTracking()
            .Select(s => new { s.Objid, s.Paused })
            .ToList()
            .Select(s => (s.Objid, s.Paused))
            .ToList();
    }

    /// <summary>
    /// 取得指定期間的狀態變更（依 sensor 與時間排序）。判定「跨午夜持續 Down」時，
    /// 呼叫端可把起點往前一天以取得前導狀態。
    /// </summary>
    public List<PrtgStateChangeRow> GetStateChanges(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetStateChanges");
        using var ctx = _contextFactory();
        return ctx.PrtgStateChanges
            .AsNoTracking()
            .Where(r => r.ChangedAt >= fromInclusive && r.ChangedAt < toExclusive)
            .OrderBy(r => r.SensorObjid)
            .ThenBy(r => r.ChangedAt)
            .ToList();
    }


    /// <summary>取得未暫停 sensor 的現況狀態（規則評估用）：objid、device、status、type、name、category。</summary>
    public List<(long Objid, long DeviceObjid, string? Status, string SensorType, string SensorName, string? Category)> GetSensorStatuses()
    {
        using var __perf = _performance.Measure("prtg:GetSensorStatuses");
        using var ctx = _contextFactory();
        return ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused)
            .Select(s => new { s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Name, s.Category })
            .ToList()
            .Select(s => (s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Name, s.Category))
            .ToList();
    }

    /// <summary>依所選主機及同一個最新全域對應日期，在 SQL 端計數並回傳一頁感測器。</summary>
    public PrtgMonitoringSensorPage GetMonitoringSensorPage(
        DateTime mapDate, IReadOnlyCollection<long> hostIds, string? search, int offset, int take, int? knownTotal = null)
    {
        if (hostIds.Count > 3000 || offset < 0 || take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take), "範圍清單或頁面大小超出上限。");
        if (hostIds.Count == 0) return new PrtgMonitoringSensorPage(0, []);
        var hosts = hostIds.Distinct().ToArray();
        var needle = search?.Trim();
        using var ctx = _contextFactory();
        var query = from sensor in ctx.PrtgSensors.AsNoTracking()
                    join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                    join map in ctx.PrtgHostMaps.AsNoTracking() on device.Objid equals map.DeviceObjid
                    where !sensor.Paused && !device.Paused && map.MapDate == mapDate && map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue
                        && hosts.Contains(map.HostId.Value)
                    select new { Sensor = sensor, Device = device, Map = map };
        if (!string.IsNullOrEmpty(needle))
        {
            if (long.TryParse(needle, out var sensorId))
                query = query.Where(x => x.Sensor.Name.Contains(needle) || x.Sensor.Objid == sensorId ||
                    x.Device.Name.Contains(needle) || (x.Map.HostName ?? "").Contains(needle));
            else
                query = query.Where(x => x.Sensor.Name.Contains(needle) || x.Device.Name.Contains(needle) ||
                    (x.Map.HostName ?? "").Contains(needle));
        }
        var total = knownTotal ?? query.Select(x => x.Sensor.Objid).Distinct().Count();
        var rows = query.OrderBy(x => x.Sensor.Objid).Skip(offset).Take(take)
            .Select(x => new PrtgMonitoringSensorRow(x.Sensor.Objid, x.Map.HostId!.Value,
                x.Sensor.Name, x.Sensor.SensorType, x.Sensor.Category))
            .ToList();
        return new PrtgMonitoringSensorPage(total, rows);
    }

    /// <summary>以最新全域映射日驗證指定的 sensor 全部屬於所選 host 集合。</summary>
    public int CountValidMonitoringSensors(DateTime mapDate, IReadOnlyCollection<long> hostIds,
        IReadOnlyCollection<long> sensorIds)
    {
        if (hostIds.Count > 3000 || sensorIds.Count > 15000)
            throw new ArgumentOutOfRangeException(nameof(sensorIds), "範圍清單超出上限。");
        if (hostIds.Count == 0 || sensorIds.Count == 0) return 0;
        return PrtgSqlServerIdScope.Execute(_contextFactory, hostIds, sensorIds,
            (ctx, hosts, sensors, hostIdScopeQuery, sensorIdScopeQuery) =>
            {
                var query = from sensor in ctx.PrtgSensors.AsNoTracking()
                            join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                            join map in ctx.PrtgHostMaps.AsNoTracking() on device.Objid equals map.DeviceObjid
                            where !sensor.Paused && !device.Paused && map.MapDate == mapDate && map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue
                            select new { sensor.Objid, map.HostId };
                query = hostIdScopeQuery is null
                    ? query.Where(x => hosts.Contains(x.HostId!.Value))
                    : query.Where(x => hostIdScopeQuery.Contains(x.HostId!.Value));
                query = sensorIdScopeQuery is null
                    ? query.Where(x => sensors!.Contains(x.Objid))
                    : query.Where(x => sensorIdScopeQuery.Contains(x.Objid));
                return query.Select(x => x.Objid).Distinct().Count();
            });
    }

    /// <summary>依明確 sensor ID 有界擷取評估 metadata；不計數、不依映射／候選資格定位。</summary>
    public IReadOnlyList<PrtgReadinessSensorDetail> GetReadinessSensorDetailsByIds(
        IReadOnlyCollection<long> sensorObjids)
    {
        ArgumentNullException.ThrowIfNull(sensorObjids);
        if (sensorObjids.Count > 100 || sensorObjids.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(sensorObjids), "最多接受 100 個正 sensor ID。");
        var ids = sensorObjids.Distinct().ToArray();
        if (ids.Length == 0) return [];

        using var ctx = _contextFactory();
        var rows = (from sensor in ctx.PrtgSensors.AsNoTracking()
                    join device in ctx.PrtgDevices.AsNoTracking() on sensor.DeviceObjid equals device.Objid
                    where ids.Contains(sensor.Objid)
                    select new
                    {
                        sensor.Objid,
                        sensor.DeviceObjid,
                        Name = sensor.Name == null ? "" : sensor.Name.Substring(0, 256),
                        SensorType = sensor.SensorType == null ? "" : sensor.SensorType.Substring(0, 129),
                        Category = sensor.Category == null ? null : sensor.Category.Substring(0, 65),
                        Unit = sensor.Unit == null ? null : sensor.Unit.Substring(0, 65),
                        sensor.Paused,
                        DevicePaused = device.Paused
                    }).OrderBy(x => x.Objid).ToList();

        if (rows.Count != ids.Length || rows.Select(x => x.Objid).Distinct().Count() != ids.Length)
            throw new InvalidOperationException("部分 sensor ID 不存在、缺少裝置資料或結果不唯一；已拒絕不完整 readiness metadata。");
        if (rows.Any(x => x.Name.Length > 255 || x.SensorType.Length > 128 ||
            x.Category is { Length: > 64 } || x.Unit is { Length: > 64 }))
            throw new InvalidOperationException("sensor readiness metadata 超過正式欄位上限；已拒絕讀取完整文字欄位。");

        return rows.Select(x => new PrtgReadinessSensorDetail(x.Objid, x.DeviceObjid, x.Name,
            x.SensorType, x.Category, x.Unit, x.Paused, x.DevicePaused)).ToArray();
    }

    /// <summary>依最新全域映射日，在 SQL 端先計數、再分頁讀取評估預覽候選。</summary>
    public PrtgMonitoringEvaluationPage GetMonitoringEvaluationPage(
        DateTime? mapDate, IReadOnlyCollection<long> visibleHostIds, bool fullScope, int offset, int take)
    {
        if (offset < 0 || offset > 30000 || take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take), "預覽頁面超出上限。");
        if (visibleHostIds.Count == 0 && !fullScope) return new PrtgMonitoringEvaluationPage(0, []);
        using var ctx = _contextFactory();
        // 缺裝置鏡像的感測器仍供全站診斷顯示未對應；已暫停的資源不參與評估。
        var sensors = ctx.PrtgSensors.AsNoTracking().Where(sensor => !sensor.Paused &&
            !ctx.PrtgDevices.Any(device => device.Objid == sensor.DeviceObjid && device.Paused));
        if (!mapDate.HasValue)
        {
            if (!fullScope) return new PrtgMonitoringEvaluationPage(0, []);
            var totalAll = sensors.Count();
            var pageAll = sensors.OrderBy(s => s.Objid).Skip(offset).Take(take)
                .Select(s => new { SensorId = s.Objid, s.DeviceObjid, s.Name, s.SensorType, s.Category })
                .ToList();
            var rowsAll = pageAll.Select(s => new PrtgMonitoringEvaluationRow(s.SensorId, s.DeviceObjid,
                s.Name, s.SensorType, s.Category, null, null, null)).ToList();
            return new PrtgMonitoringEvaluationPage(totalAll, rowsAll);
        }
        var candidates = from sensor in sensors
                         join map in ctx.PrtgHostMaps.AsNoTracking().Where(m => m.MapDate == mapDate.Value)
                             on sensor.DeviceObjid equals map.DeviceObjid into matches
                         from map in matches.DefaultIfEmpty()
                         where fullScope ||
                             (map != null && map.HostId.HasValue && visibleHostIds.Contains(map.HostId.Value))
                         select new
                         {
                             SensorId = sensor.Objid,
                             sensor.DeviceObjid,
                             sensor.Name,
                             sensor.SensorType,
                             sensor.Category,
                             HostId = map == null ? (long?)null : map.HostId,
                             HostName = map == null ? null : map.HostName,
                             MapStatus = map == null ? null : map.MapStatus
                         };
        var total = candidates.Select(row => row.SensorId).Distinct().Count();
        // SQL 先投影純量並限制頁面，再於記憶體建 DTO，避免左連接的可空實體無法翻譯。
        var page = candidates.OrderBy(row => row.SensorId).Skip(offset).Take(take)
            .Select(row => new
            {
                row.SensorId,
                row.DeviceObjid,
                row.Name,
                row.SensorType,
                row.Category,
                row.HostId,
                row.HostName,
                row.MapStatus
            })
            .ToList();
        var rows = page.Select(row => new PrtgMonitoringEvaluationRow(row.SensorId, row.DeviceObjid,
            row.Name, row.SensorType, row.Category, row.HostId, row.HostName, row.MapStatus)).ToList();
        return new PrtgMonitoringEvaluationPage(total, rows);
    }

    /// <summary>在最新全域映射日內，以 SQL 計數並分頁列出唯一 NetIQ host id。</summary>
    public PrtgMonitoringHostPage GetMonitoringHostPage(
        IReadOnlyCollection<long> eligibleHostIds, string? search, int offset, int take, int? knownTotal = null)
    {
        if (eligibleHostIds.Count > 3000 || offset < 0 || take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take), "範圍清單或頁面大小超出上限。");
        if (eligibleHostIds.Count == 0) return new PrtgMonitoringHostPage(null, 0, []);
        var ids = eligibleHostIds.Distinct().ToArray();
        using var ctx = _contextFactory();
        var mapDate = FindLatestHostMapDate(ctx, 30, null);
        if (!mapDate.HasValue) return new PrtgMonitoringHostPage(null, 0, []);
        var query = ctx.PrtgHostMaps.AsNoTracking()
            .Where(m => m.MapDate == mapDate.Value && m.MapStatus == PrtgMapStatus.Ok &&
                m.HostId.HasValue && ids.Contains(m.HostId.Value));
        var needle = search?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            if (long.TryParse(needle, out var hostId)) query = query.Where(m => (m.HostName ?? "").Contains(needle) || m.HostId == hostId);
            else query = query.Where(m => (m.HostName ?? "").Contains(needle));
        }
        var total = knownTotal ?? query.Select(m => m.HostId!.Value).Distinct().Count();
        var rows = query.GroupBy(m => m.HostId!.Value)
            .Select(g => new { HostId = g.Key, HostName = g.Min(m => m.HostName) })
            .OrderBy(x => x.HostId).Skip(offset).Take(take)
            .Select(x => new PrtgMonitoringHostRow(x.HostId, x.HostName ?? ""))
            .ToList();
        return new PrtgMonitoringHostPage(mapDate, total, rows);
    }

    /// <summary>
    /// 取得指定日期的 PRTG 主機對應清單（唯讀查詢）。
    /// </summary>
    public List<PrtgHostMapRow> GetHostMapForDate(DateTime mapDate)
    {
        using var __perf = _performance.Measure("prtg:GetHostMapForDate");
        using var ctx = _contextFactory();
        var targetDate = mapDate.Date;
        return ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == targetDate)
            .ToList();
    }

    /// <summary>取得指定 device 上的全部 sensor（主機明細顯示用）。</summary>
    public List<PrtgSensorRow> GetSensorsByDevice(long deviceObjid)
    {
        using var __perf = _performance.Measure("prtg:GetSensorsByDevice");
        using var ctx = _contextFactory();
        return ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => s.DeviceObjid == deviceObjid)
            .OrderBy(s => s.Name)
            .ToList();
    }

    /// <summary>
    /// 取得回看窗內最近一個有對應資料的日期，以及該日的全部對應列（唯讀查詢）。
    /// 最多兩次資料庫往返（聚合 Max 日期 + 取該日列），窗內無資料時回傳 (null, 空清單)。
    /// </summary>
    /// <param name="maxLookbackDays">回看幾天（含基準日當天）</param>
    /// <param name="anchor">回看的基準日（含），null＝今天。歷史回填逐日往回補時，
    /// 基準日是「正在回填的那一天」而不是今天——拿今天當基準會取到回填當下的對應，
    /// 與該日的實際對應不符。</param>
    public (DateTime? MapDate, List<PrtgHostMapRow> Rows) GetLatestHostMapWithDate(
        int maxLookbackDays = 30, DateTime? anchor = null)
    {
        using var __perf = _performance.Measure("prtg:GetLatestHostMapWithDate");
        using var ctx = _contextFactory();
        var latestDate = FindLatestHostMapDate(ctx, maxLookbackDays, anchor);
        if (latestDate == null)
        {
            return (null, new List<PrtgHostMapRow>());
        }

        var rows = ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == latestDate.Value)
            .ToList();

        return (latestDate.Value, rows);
    }

    /// <summary>只讀取最新全域映射日期，不載入該日期的映射列。</summary>
    public DateTime? GetLatestHostMapDate(int maxLookbackDays = 30)
    {
        using var ctx = _contextFactory();
        return FindLatestHostMapDate(ctx, maxLookbackDays, null);
    }

    /// <summary>
    /// 取得最近 30 天內最近一個有對應資料日期的 PRTG 主機對應清單（唯讀查詢）。
    /// </summary>
    public List<PrtgHostMapRow> GetLatestHostMap(int maxLookbackDays = 30) =>
        GetLatestHostMapWithDate(maxLookbackDays).Rows;

    /// <summary>
    /// 取得回看窗內最近一個有對應資料的日期，以及該日**指定主機**的對應列（唯讀查詢）。
    /// 語意與 <see cref="GetLatestHostMapWithDate"/> 完全相同（同一個「最近一次對應」的日期），
    /// 差別只在第二趟查詢多了 host_id 條件——主機詳情的 PRTG 頁籤只需要一台主機的那幾列，
    /// 沒有理由把整日的對應表（數千列起跳）全部讀回記憶體再過濾（回饋四十五輪 B5）。
    ///
    /// **該日存在但這台主機沒有對應列時，MapDate 照樣回傳那個日期**（與整表版本的行為一致：
    /// 畫面上顯示的是「對應資料的日期」，不是「這台主機有對應的日期」）；
    /// 窗內完全沒有資料時才回傳 (null, 空清單)。
    /// </summary>
    public (DateTime? MapDate, List<PrtgHostMapRow> Rows) GetLatestHostMapForHost(
        long hostId, int maxLookbackDays = 30, DateTime? anchor = null)
    {
        using var __perf = _performance.Measure("prtg:GetLatestHostMapForHost");
        using var ctx = _contextFactory();
        var latestDate = FindLatestHostMapDate(ctx, maxLookbackDays, anchor);
        if (latestDate == null)
        {
            return (null, new List<PrtgHostMapRow>());
        }

        var rows = ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == latestDate.Value && m.HostId == hostId)
            .ToList();

        return (latestDate.Value, rows);
    }

    /// <summary>
    /// 多台版的 <see cref="GetLatestHostMapForHost"/>：同一個「最近一次對應」的日期，
    /// 第二趟只取 <paramref name="hostIds"/> 內主機的列（host_id IN (...)）。
    /// 主機清單每頁只需要本頁未回報主機的對應，不必讀回整日對應表。
    /// hostIds 為空時直接回空清單，不查資料庫。
    /// </summary>
    public List<PrtgHostMapRow> GetLatestHostMapForHosts(
        IReadOnlyCollection<long> hostIds, int maxLookbackDays = 30)
    {
        using var __perf = _performance.Measure("prtg:GetLatestHostMapForHosts");
        if (hostIds.Count == 0)
        {
            return new List<PrtgHostMapRow>();
        }

        using var ctx = _contextFactory();
        var latestDate = FindLatestHostMapDate(ctx, maxLookbackDays, anchor: null);
        if (latestDate == null)
        {
            return new List<PrtgHostMapRow>();
        }

        var ids = hostIds.Distinct().ToList();
        return ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == latestDate.Value && m.HostId.HasValue && ids.Contains(m.HostId.Value))
            .ToList();
    }

    /// <summary>單台／多台版共用：回看窗內最近一個有對應資料的日期，無資料或窗長不合法回 null。</summary>
    private static DateTime? FindLatestHostMapDate(LfDbContext ctx, int maxLookbackDays, DateTime? anchor)
    {
        if (maxLookbackDays <= 0)
        {
            return null;
        }

        var anchorDate = (anchor ?? DateTime.Today).Date;
        var cutoff = anchorDate.AddDays(-(maxLookbackDays - 1));
        return ctx.PrtgHostMaps
            .Where(m => m.MapDate >= cutoff && m.MapDate <= anchorDate)
            .Max(m => (DateTime?)m.MapDate);
    }

    public const string ScopeRevisionBlobKey = "prtg_scope_revision";
    public const string HostMapDataRevisionBlobKey = "prtg_host_map_data_revision";
    public const string CatalogueDataRevisionBlobKey = "prtg_catalogue_data_revision";

    /// <summary>讀取與裝置、感測器、分類正式寫入同交易遞增的版本。</summary>
    public long ReadCatalogueDataRevision()
    {
        using var ctx = _contextFactory();
        return ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == CatalogueDataRevisionBlobKey)
            .Select(b => b.Version).FirstOrDefault();
    }

    private static void IncrementPrtgCatalogueDataRevision(LfDbContext ctx)
    {
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == CatalogueDataRevisionBlobKey);
        if (row is null)
            ctx.Blobs.Add(new BlobRow { BlobKey = CatalogueDataRevisionBlobKey, Content = "{}", Version = 1, UpdatedAt = DateTime.Now });
        else
        {
            row.Version++;
            row.UpdatedAt = DateTime.Now;
        }
    }

    /// <summary>讀取與正式日映射寫入同交易遞增的版本。</summary>
    public long ReadHostMapDataRevision()
    {
        using var ctx = _contextFactory();
        return ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == HostMapDataRevisionBlobKey)
            .Select(b => b.Version).FirstOrDefault();
    }

    private static void IncrementHostMapDataRevision(LfDbContext ctx)
    {
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == HostMapDataRevisionBlobKey);
        if (row is null)
        {
            ctx.Blobs.Add(new BlobRow
            {
                BlobKey = HostMapDataRevisionBlobKey,
                Content = "{}",
                Version = 1,
                UpdatedAt = DateTime.Now
            });
            ctx.SaveChanges();
            return;
        }

        row.Version++;
        row.UpdatedAt = DateTime.Now;
        ctx.SaveChanges();
    }

    // 範圍修改及其版本在同一交易提交；其他程序不會看見新對應卻仍讀到舊版本。
    private T WriteScopeChange<T>(Func<LfDbContext, T> change, long? expectedRevision = null)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var transaction = ctx.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var stamp = ctx.Blobs.SingleOrDefault(b => b.BlobKey == ScopeRevisionBlobKey);
            if (expectedRevision.HasValue && (stamp?.Version ?? 0) != expectedRevision.Value)
                throw new PrtgScopeConflictException();
            var result = change(ctx);
            if (stamp == null)
                ctx.Blobs.Add(new BlobRow { BlobKey = ScopeRevisionBlobKey, Content = "{}", Version = 1, UpdatedAt = DateTime.Now });
            else
            {
                stamp.Version++;
                stamp.UpdatedAt = DateTime.Now;
            }
            ctx.SaveChanges();
            transaction.Commit();
            return result;
        });
    }

    /// <summary>讀取全部人工對應（device_objid → 列）</summary>
    public List<PrtgManualMapRow> GetManualMaps()
    {
        using var __perf = _performance.Measure("prtg:GetManualMaps");
        using var ctx = _contextFactory();
        return ctx.PrtgManualMaps.AsNoTracking().ToList();
    }

    /// <summary>新增或更新一筆人工對應。CreatedAt 首次建立時寫入，後續更新不覆蓋。</summary>
    public void UpsertManualMap(PrtgManualMapRow row, long? expectedRevision = null)
    {
        if (row == null) return;
        WriteScopeChange(ctx =>
        {
            var existing = ctx.PrtgManualMaps.FirstOrDefault(m => m.DeviceObjid == row.DeviceObjid);
            if (existing != null)
            {
                existing.HostId = row.HostId;
                existing.CreatedBy = row.CreatedBy;
                existing.Note = row.Note;
                // CreatedAt 保持原值
            }
            else
            {
                var newRow = new PrtgManualMapRow
                {
                    DeviceObjid = row.DeviceObjid,
                    HostId = row.HostId,
                    CreatedBy = row.CreatedBy,
                    Note = row.Note,
                    CreatedAt = row.CreatedAt != default ? row.CreatedAt : DateTime.Now
                };
                ctx.PrtgManualMaps.Add(newRow);
            }
            return 0;
        }, expectedRevision);
    }

    /// <summary>刪除一筆人工對應，回傳刪除筆數。</summary>
    public int DeleteManualMap(long deviceObjid, long? expectedRevision = null) =>
        WriteScopeChange(ctx => ctx.PrtgManualMaps.Where(m => m.DeviceObjid == deviceObjid).ExecuteDelete(), expectedRevision);

    /// <summary>讀取全部 IP 排除清單</summary>
    public List<PrtgIpExcludeRow> GetIpExcludes()
    {
        using var __perf = _performance.Measure("prtg:GetIpExcludes");
        using var ctx = _contextFactory();
        return ctx.PrtgIpExcludes.AsNoTracking().ToList();
    }

    /// <summary>新增或更新一筆 IP 排除。CreatedAt 首次建立時寫入，後續更新不覆蓋。</summary>
    public void UpsertIpExclude(PrtgIpExcludeRow row, long? expectedRevision = null)
    {
        if (row == null) return;
        var normIp = PrtgHostMapper.NormalizeIp(row.Ip);
        if (normIp == null) return;
        normIp = Truncate(normIp, 64)!;
        var note = Truncate(row.Note, 512);
        var createdBy = Truncate(row.CreatedBy, 64);

        WriteScopeChange(ctx =>
        {
            var existing = ctx.PrtgIpExcludes.FirstOrDefault(e => e.Ip == normIp);
            if (existing != null)
            {
                existing.CreatedBy = createdBy;
                existing.Note = note;
                // CreatedAt 保持原值
            }
            else
            {
                var newRow = new PrtgIpExcludeRow
                {
                    Ip = normIp,
                    CreatedBy = createdBy,
                    Note = note,
                    CreatedAt = row.CreatedAt != default ? row.CreatedAt : DateTime.Now
                };
                ctx.PrtgIpExcludes.Add(newRow);
            }
            return 0;
        }, expectedRevision);
    }

    /// <summary>
    /// 刪除一筆 IP 排除，回傳刪除筆數。
    /// 同時以原始 trim 值與正規化值各嘗試一次，以相容舊的非 IP 排除列。
    /// </summary>
    public int DeleteIpExclude(string ip, long? expectedRevision = null)
    {
        var trimmed = ip?.Trim();
        var normalized = PrtgHostMapper.NormalizeIp(ip);
        if (string.IsNullOrEmpty(trimmed) && normalized == null) return 0;
        return WriteScopeChange(ctx => ctx.PrtgIpExcludes
            .Where(e => e.Ip == trimmed || (normalized != null && e.Ip == normalized)).ExecuteDelete(), expectedRevision);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>
    /// 取得 PRTG 鏡像五表的概要統計（計數與最近時間點）。
    /// 透過 SQL 聚合查詢，不將整張表載入記憶體。
    /// </summary>
    public PrtgMirrorSummary GetMirrorSummary()
    {
        using var __perf = _performance.Measure("prtg:GetMirrorSummary");
        using var ctx = _contextFactory();

        var deviceCount = ctx.PrtgDevices.Count();
        var sensorCount = ctx.PrtgSensors.Count();

        var lastDeviceSync = ctx.PrtgDevices.Max(d => (DateTime?)d.SyncedAt);
        var lastSensorSync = ctx.PrtgSensors.Max(s => (DateTime?)s.SyncedAt);
        var lastValueAt = ctx.PrtgValues.Max(v => (DateTime?)v.PeriodStart);
        var lastStateChangeAt = ctx.PrtgStateChanges.Max(c => (DateTime?)c.ChangedAt);

        return new PrtgMirrorSummary(
            deviceCount,
            sensorCount,
            lastDeviceSync,
            lastSensorSync,
            lastValueAt,
            lastStateChangeAt);
    }

    /// <summary>
    /// 白名單覆蓋量級：命中白名單的未暫停 sensor 數，以及其中位於
    /// 當日已成功對應（ok）device 上的數量。白名單為空時兩個數字都回 0。
    /// </summary>
    public PrtgWhitelistCoverage GetWhitelistCoverage(IReadOnlyCollection<string>? whitelist, DateTime? mapDate)
    {
        using var __perf = _performance.Measure("prtg:GetWhitelistCoverage");
        if (whitelist == null || whitelist.Count == 0)
        {
            return new PrtgWhitelistCoverage(0, 0);
        }

        var whitelistSet = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);

        using var ctx = _contextFactory();

        var activeSensors = ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused)
            .Select(s => new { s.Objid, s.DeviceObjid, s.SensorType })
            .ToList();

        var matchedSensors = activeSensors
            .Where(s => whitelistSet.Contains(s.SensorType))
            .ToList();

        var whitelistSensorCount = matchedSensors.Count;
        if (whitelistSensorCount == 0 || !mapDate.HasValue)
        {
            return new PrtgWhitelistCoverage(whitelistSensorCount, 0);
        }

        var targetDate = mapDate.Value.Date;
        var okDeviceIds = ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == targetDate && m.MapStatus == PrtgMapStatus.Ok)
            .Select(m => m.DeviceObjid)
            .ToHashSet();

        var onMappedDeviceCount = matchedSensors.Count(s => okDeviceIds.Contains(s.DeviceObjid));

        return new PrtgWhitelistCoverage(whitelistSensorCount, onMappedDeviceCount);
    }

    /// <summary>
    /// 選出要擷取數值的 sensor：位於指定 device 上、未暫停、且 type 命中白名單者。
    /// 白名單為 null 或空代表不限制 type（沿用設定語意）。device 集合為空時回空清單。
    /// </summary>
    public List<long> GetValueFetchTargets(IReadOnlyCollection<string>? whitelist, IReadOnlyCollection<long> deviceObjids)
    {
        using var __perf = _performance.Measure("prtg:GetValueFetchTargets");
        if (deviceObjids == null || deviceObjids.Count == 0)
        {
            return new List<long>();
        }

        using var ctx = _contextFactory();

        var query = ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused && deviceObjids.Contains(s.DeviceObjid))
            .Select(s => new { s.Objid, s.SensorType })
            .ToList();

        if (whitelist == null || whitelist.Count == 0)
        {
            return query.Select(s => s.Objid).ToList();
        }

        var whitelistSet = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);

        return query
            .Where(s => whitelistSet.Contains(s.SensorType))
            .Select(s => s.Objid)
            .ToList();
    }

    /// <summary>
    /// 取得指定期間內每個 sensor 的數值涵蓋摘要（依 sensor_objid 排序）。
    /// 全程在 SQL 端以 GroupBy 聚合，統計有效小時數（ok）、可用天數（usable 相異日）、
    /// 最早／最晚可用起點，以及各品質與可用筆數。
    /// </summary>
    /// <param name="fromInclusive">起始時間（含）</param>
    /// <param name="toExclusive">結束時間（不含）</param>
    public List<PrtgSensorValueCoverage> GetValueCoverageSummary(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetValueCoverageSummary");
        using var ctx = _contextFactory();
        return ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .Select(v => new
            {
                v.SensorObjid,
                UsablePeriod = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                    ? (DateTime?)v.PeriodStart : null,
                UsableDate = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                    ? (DateTime?)v.PeriodStart.Date : null,
                OkCount = v.Quality == PrtgDataQuality.Ok ? 1 : 0,
                SampledCount = (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage) ? 1 : 0,
                UsableCount = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) ? 1 : 0,
                UnknownCount = v.Quality == PrtgDataQuality.Unknown ? 1 : 0,
                NodataCount = v.Quality == PrtgDataQuality.NoData ? 1 : 0,
                OtherCount = (!(v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                    && v.Quality != PrtgDataQuality.Unknown
                    && v.Quality != PrtgDataQuality.NoData) ? 1 : 0,
            })
            .GroupBy(x => x.SensorObjid)
            .Select(g => new
            {
                SensorObjid = g.Key,
                OkCount = g.Sum(x => x.OkCount),
                UsableDays = g.Select(x => x.UsableDate).Distinct().Count(),
                EarliestUsablePeriod = g.Min(x => x.UsablePeriod),
                LatestUsablePeriod = g.Max(x => x.UsablePeriod),
                UnknownCount = g.Sum(x => x.UnknownCount),
                NodataCount = g.Sum(x => x.NodataCount),
                OtherCount = g.Sum(x => x.OtherCount),
                TotalCount = g.Count(),
                SampledCount = g.Sum(x => x.SampledCount),
                UsableCount = g.Sum(x => x.UsableCount)
            })
            .OrderBy(r => r.SensorObjid)
            .AsEnumerable()
            .Select(r => new PrtgSensorValueCoverage(
                r.SensorObjid,
                r.OkCount,
                r.UsableDays,
                r.EarliestUsablePeriod,
                r.LatestUsablePeriod,
                r.UnknownCount,
                r.NodataCount,
                r.OtherCount,
                r.TotalCount,
                r.SampledCount,
                r.UsableCount))
            .ToList();
    }

    /// <summary>
    /// 取得指定期間內每個 (sensor_objid, 日期) 的每日數值聚合（匯出用，依 sensor_objid 與日期排序）。
    /// 數值統計（平均、最小、最大）僅納入可用列且 AvgValue != null 的列，null 絕對不當成 0 計算；
    /// 觀測極值（MinObserved、MaxObserved）取可用列的 MinValue 與 MaxValue 極值；
    /// 當日完全無可用列時仍回傳該 (sensor, 日) 一列（數值統計為 null，可用列數為 0）。
    /// </summary>
    /// <param name="fromInclusive">起始時間（含）</param>
    /// <param name="toExclusive">結束時間（不含）</param>
    public List<PrtgDailyValueAggregation> GetDailyValueAggregations(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetDailyValueAggregations");
        using var ctx = _contextFactory();
        return ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .Select(v => new
            {
                v.SensorObjid,
                Date = v.PeriodStart.Date,
                UsableAvgValue = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) && v.AvgValue != null ? v.AvgValue : null,
                UsableMinValue = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) ? v.MinValue : null,
                UsableMaxValue = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) ? v.MaxValue : null,
                OkCount = v.Quality == PrtgDataQuality.Ok ? 1 : 0,
                SampledCount = (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage) ? 1 : 0,
                UsableCount = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) ? 1 : 0,
                UnknownCount = v.Quality == PrtgDataQuality.Unknown ? 1 : 0,
                NodataCount = v.Quality == PrtgDataQuality.NoData ? 1 : 0,
                OtherCount = (!(v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                    && v.Quality != PrtgDataQuality.Unknown
                    && v.Quality != PrtgDataQuality.NoData) ? 1 : 0,
            })
            .GroupBy(x => new { x.SensorObjid, x.Date })
            .Select(g => new
            {
                g.Key.SensorObjid,
                g.Key.Date,
                AvgValue = g.Average(x => x.UsableAvgValue),
                MinValue = g.Min(x => x.UsableAvgValue),
                MaxValue = g.Max(x => x.UsableAvgValue),
                OkCount = g.Sum(x => x.OkCount),
                UnknownCount = g.Sum(x => x.UnknownCount),
                NodataCount = g.Sum(x => x.NodataCount),
                OtherCount = g.Sum(x => x.OtherCount),
                TotalCount = g.Count(),
                SampledCount = g.Sum(x => x.SampledCount),
                UsableCount = g.Sum(x => x.UsableCount),
                MinObserved = g.Min(x => x.UsableMinValue),
                MaxObserved = g.Max(x => x.UsableMaxValue)
            })
            .OrderBy(r => r.SensorObjid)
            .ThenBy(r => r.Date)
            .AsEnumerable()
            .Select(r => new PrtgDailyValueAggregation(
                r.SensorObjid,
                r.Date,
                r.AvgValue,
                r.MinValue,
                r.MaxValue,
                r.OkCount,
                r.UnknownCount,
                r.NodataCount,
                r.OtherCount,
                r.TotalCount,
                r.SampledCount,
                r.UsableCount,
                r.MinObserved,
                r.MaxObserved))
            .ToList();
    }

    /// <summary>
    /// 取得指定期間內每日數值量級統計（依日期排序）。
    /// 全程在 SQL 端以 GroupBy 聚合，統計每日相異 sensor 數、總列數與各品質列數。
    /// </summary>
    /// <param name="fromInclusive">起始時間（含）</param>
    /// <param name="toExclusive">結束時間（不含）</param>
    public List<PrtgDailyValueMagnitude> GetDailyValueMagnitudes(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetDailyValueMagnitudes");
        using var ctx = _contextFactory();
        return ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .Select(v => new
            {
                Date = v.PeriodStart.Date,
                v.SensorObjid,
                OkCount = v.Quality == PrtgDataQuality.Ok ? 1 : 0,
                SampledCount = (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage) ? 1 : 0,
                UsableCount = (v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage)) ? 1 : 0,
                UnknownCount = v.Quality == PrtgDataQuality.Unknown ? 1 : 0,
                NodataCount = v.Quality == PrtgDataQuality.NoData ? 1 : 0,
                OtherCount = (!(v.Quality == PrtgDataQuality.Ok ||
                    (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                    && v.Quality != PrtgDataQuality.Unknown
                    && v.Quality != PrtgDataQuality.NoData) ? 1 : 0,
            })
            .GroupBy(x => x.Date)
            .Select(g => new
            {
                Date = g.Key,
                SensorCount = g.Select(x => x.SensorObjid).Distinct().Count(),
                TotalCount = g.Count(),
                OkCount = g.Sum(x => x.OkCount),
                UnknownCount = g.Sum(x => x.UnknownCount),
                NodataCount = g.Sum(x => x.NodataCount),
                OtherCount = g.Sum(x => x.OtherCount),
                SampledCount = g.Sum(x => x.SampledCount),
                UsableCount = g.Sum(x => x.UsableCount)
            })
            .OrderBy(r => r.Date)
            .AsEnumerable()
            .Select(r => new PrtgDailyValueMagnitude(
                r.Date,
                r.SensorCount,
                r.TotalCount,
                r.OkCount,
                r.UnknownCount,
                r.NodataCount,
                r.OtherCount,
                r.SampledCount,
                r.UsableCount))
            .ToList();
    }


    /// <summary>
    /// 取得指定期間內狀態變更的涵蓋摘要。
    /// 全程在 SQL 端聚合，統計相異日期數、相異 sensor 數、總筆數與最早／最晚變更時間。
    /// 無資料時回傳計數皆為 0、時間為 null 的摘要物件。
    /// 只計 sensor 仍在感測器鏡像中的列：鏡像已縮到取數範圍內，範圍外的舊狀態變更要到保留期才消失，
    /// 不排除的話新舊口徑會混在同一個數字裡。
    /// </summary>
    /// <param name="fromInclusive">起始時間（含）</param>
    /// <param name="toExclusive">結束時間（不含）</param>
    public PrtgStateChangeCoverageSummary GetStateChangeCoverageSummary(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetStateChangeCoverageSummary");
        using var ctx = _contextFactory();
        var summary = ctx.PrtgStateChanges
            .AsNoTracking()
            .Where(r => r.ChangedAt >= fromInclusive && r.ChangedAt < toExclusive
                && ctx.PrtgSensors.Any(s => s.Objid == r.SensorObjid))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                DistinctDates = g.Select(r => r.ChangedAt.Date).Distinct().Count(),
                DistinctSensors = g.Select(r => r.SensorObjid).Distinct().Count(),
                TotalCount = g.Count(),
                EarliestChangedAt = g.Min(r => (DateTime?)r.ChangedAt),
                LatestChangedAt = g.Max(r => (DateTime?)r.ChangedAt)
            })
            .FirstOrDefault();

        return summary != null
            ? new PrtgStateChangeCoverageSummary(
                summary.DistinctDates,
                summary.DistinctSensors,
                summary.TotalCount,
                summary.EarliestChangedAt,
                summary.LatestChangedAt)
            : new PrtgStateChangeCoverageSummary(0, 0, 0, null, null);
    }

    /// <summary>
    /// 依感測器類型與小時分組，計算可用列的小時數值曲線（SQL 聚合）。
    /// </summary>
    public List<PrtgTypeHourlyProfile> GetUsableHourlyProfileByType(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetUsableHourlyProfileByType");
        using var ctx = _contextFactory();
        return BuildUsableHourlyProfileQuery(
            ctx.PrtgValues.AsNoTracking(),
            ctx.PrtgSensors.AsNoTracking(),
            fromInclusive,
            toExclusive)
            .ToList();
    }

    internal static IQueryable<PrtgTypeHourlyProfile> BuildUsableHourlyProfileQuery(
        IQueryable<PrtgValueRow> values,
        IQueryable<PrtgSensorRow> sensors,
        DateTime fromInclusive,
        DateTime toExclusive)
    {
        return from v in values
               where v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive
                     && (v.Quality == PrtgDataQuality.Ok ||
                         (v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
                     && v.AvgValue != null
               join s in sensors on v.SensorObjid equals s.Objid
               group v by new { s.SensorType, v.PeriodStart.Hour } into g
               select new PrtgTypeHourlyProfile(
                   g.Key.SensorType,
                   g.Key.Hour,
                   g.Average(x => x.AvgValue),
                   g.Count());
    }

    /// <summary>
    /// 取得指定時間之後的快照取樣涵蓋（包含 coverage 不足 75 的取樣列）。
    /// </summary>
    public PrtgSampledCoverage GetSampledCoverageSince(DateTime fromInclusive)
    {
        using var __perf = _performance.Measure("prtg:GetSampledCoverageSince");
        using var ctx = _contextFactory();
        var result = ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.Quality == PrtgDataQuality.Sampled && v.PeriodStart >= fromInclusive)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                SensorCount = g.Select(v => v.SensorObjid).Distinct().Count(),
                AverageCoverage = g.Average(v => v.Coverage)
            })
            .FirstOrDefault();

        return result != null
            ? new PrtgSampledCoverage(result.SensorCount, result.AverageCoverage)
            : new PrtgSampledCoverage(0, null);
    }

    /// <summary>在有限 PeriodStart 範圍內按小時彙總快照列品質。</summary>
    public List<PrtgSnapshotValueCoverage> GetSnapshotValueCoverage(DateTime fromInclusive, DateTime toExclusive)
    {
        using var __perf = _performance.Measure("prtg:GetSnapshotValueCoverage");
        using var ctx = _contextFactory();
        return BuildSnapshotValueCoverageQuery(ctx.PrtgValues.AsNoTracking(), fromInclusive, toExclusive)
            .AsEnumerable()
            .Select(x => new PrtgSnapshotValueCoverage(x.Hour, x.SampledUsable, x.SampledLowCoverage, x.Ok, x.Usable))
            .ToList();
    }

    internal static IQueryable<PrtgSnapshotValueCoverageProjection> BuildSnapshotValueCoverageQuery(
        IQueryable<PrtgValueRow> values, DateTime fromInclusive, DateTime toExclusive) =>
        values.Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .GroupBy(v => v.PeriodStart)
            .Select(g => new PrtgSnapshotValueCoverageProjection(
                g.Key,
                g.Sum(v => v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage ? 1 : 0),
                g.Sum(v => v.Quality == PrtgDataQuality.Sampled && v.Coverage < PrtgValueUsability.SampledMinCoverage ? 1 : 0),
                g.Sum(v => v.Quality == PrtgDataQuality.Ok ? 1 : 0),
                g.Sum(v => v.Quality == PrtgDataQuality.Ok ||
                    v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage ? 1 : 0)));
}

/// <summary>
/// PRTG 鏡像表統計摘要
/// </summary>
public sealed record PrtgMirrorSummary(
    int DeviceCount,
    int SensorCount,
    DateTime? LastDeviceSync,
    DateTime? LastSensorSync,
    DateTime? LastValueAt,
    DateTime? LastStateChangeAt);

/// <summary>
/// PRTG 白名單覆蓋量級統計
/// </summary>
public sealed record PrtgWhitelistCoverage(int WhitelistSensorCount, int OnMappedDeviceCount);

/// <summary>
/// PRTG sensor 數值涵蓋摘要
/// </summary>
public sealed record PrtgSensorValueCoverage(
    long SensorObjid,
    int OkCount,
    int UsableDays,
    DateTime? EarliestUsablePeriod,
    DateTime? LatestUsablePeriod,
    int UnknownCount,
    int NodataCount,
    int OtherCount,
    int TotalCount,
    int SampledCount,
    int UsableCount);

/// <summary>
/// PRTG sensor 每日數值聚合統計（匯出用）
/// </summary>
public sealed record PrtgDailyValueAggregation(
    long SensorObjid,
    DateTime Date,
    double? AvgValue,
    double? MinValue,
    double? MaxValue,
    int OkCount,
    int UnknownCount,
    int NodataCount,
    int OtherCount,
    int TotalCount,
    int SampledCount,
    int UsableCount,
    double? MinObserved,
    double? MaxObserved);

/// <summary>
/// PRTG 每日數值量級統計
/// </summary>
public sealed record PrtgDailyValueMagnitude(
    DateTime Date,
    int SensorCount,
    int TotalCount,
    int OkCount,
    int UnknownCount,
    int NodataCount,
    int OtherCount,
    int SampledCount,
    int UsableCount);

/// <summary>
/// PRTG 狀態變更涵蓋摘要
/// </summary>
public sealed record PrtgStateChangeCoverageSummary(
    int DistinctDates,
    int DistinctSensors,
    int TotalCount,
    DateTime? EarliestChangedAt,
    DateTime? LatestChangedAt);

/// <summary>
/// PRTG 每種 type 的每小時數值曲線分組
/// </summary>
public sealed record PrtgTypeHourlyProfile(string SensorType, int Hour, double? AvgValue, int UsableCount);

/// <summary>
/// PRTG 快照取樣涵蓋指標
/// </summary>
public sealed record PrtgSampledCoverage(int SensorCount, double? AverageCoverage);
public sealed record PrtgSnapshotValueCoverage(DateTime Hour, int SampledUsable, int SampledLowCoverage, int Ok, int Usable);
internal sealed record PrtgSnapshotValueCoverageProjection(DateTime Hour, int SampledUsable, int SampledLowCoverage, int Ok, int Usable);

/// <summary>
/// 過期裝置清除結果。Total＝清除前鏡像裝置總數；Stale＝本趟沒刷新到的列數；
/// SkippedBySafety＝過期列超過一半而觸發安全保險、一列都沒刪。
/// </summary>
public sealed record PrtgStaleDeleteResult(int Total, int Stale, int Deleted, bool SkippedBySafety);
public sealed record PrtgMonitoringSensorPage(int Total, List<PrtgMonitoringSensorRow> Rows);
public sealed record PrtgMonitoringSensorRow(long SensorId, long HostId, string Name, string SensorType, string? Category);
public sealed record PrtgReadinessSensorDetail(long Objid, long DeviceObjid, string Name, string SensorType,
    string? Category, string? Unit, bool Paused, bool DevicePaused);
public sealed record PrtgMonitoringHostPage(DateTime? MapDate, int Total, List<PrtgMonitoringHostRow> Rows);
public sealed record PrtgMonitoringHostRow(long HostId, string HostName);
public sealed record PrtgMonitoringEvaluationPage(int Total, List<PrtgMonitoringEvaluationRow> Rows);
public sealed record PrtgMonitoringEvaluationRow(long SensorId, long DeviceObjid, string Name, string SensorType,
    string? Category, long? HostId, string? HostName, string? MapStatus);

/// <summary>
/// 範圍外清除預覽：將刪除的數值列數、狀態變更列數、受影響裝置數（感測器鏡像對得到的）與前幾台裝置名稱；
/// <paramref name="UnknownSensors"/>＝感測器鏡像已沒有、對不到裝置的 sensor 數（它們的列同樣會被刪）。
/// </summary>
public sealed record PrtgOutOfScopePreview(
    int Values, int StateChanges, int AffectedDevices, IReadOnlyList<string> TopDeviceNames, int UnknownSensors);


public sealed class PrtgScopeConflictException : Exception
{
    public PrtgScopeConflictException() : base("PRTG 對應／排除範圍已被修改，請重新載入並核對後儲存。") { }
}
