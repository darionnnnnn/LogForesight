using System.Text.Json;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>單一 PRTG 資源的持久身分世代；每個 sensor 使用獨立 BlobRow，移除後仍保留墓碑。</summary>
public sealed class PrtgResourceIdentity
{
    public long SensorId { get; set; }
    public long Epoch { get; set; }
    public string Generation { get; set; } = "";
    public string SourceGeneration { get; set; } = "";
    public long DeviceId { get; set; }
    public long HostId { get; set; }
    public string ResourceFingerprint { get; set; } = "";
    public string InventoryFingerprint { get; set; } = "";
    public string ChannelFingerprint { get; set; } = "";
    public string ChannelGeneration { get; set; } = "";
    public bool Active { get; set; }
    public bool PendingReconciliation { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}

/// <summary>
/// 逐資源保存權威身分。鍵使用固定前綴與 sensor objid，避免將 15,000 顆資源及歷史墓碑
/// 聚合到單一無界 JSON blob。所有改版都保留遞增 Epoch 與新 Generation。
/// </summary>
public sealed class PrtgResourceIdentityStore(EfJsonBlobStore blob)
{
    public const string Prefix = "prtg_resource_identity_";
    public const int MaxLedgerBytes = 8 * 1024;
    public const string AuthorityRevisionPrefix = "prtg_resource_authority_host_";
    public const string ObservationRevisionPrefix = "prtg_resource_observation_host_";

    public PrtgResourceIdentity Get(long sensorId)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        var (json, _, length) = blob.ReadBoundedWithVersion(MaxLedgerBytes);
        if (length > MaxLedgerBytes || json is not null && Encoding.UTF8.GetByteCount(json) > MaxLedgerBytes)
            throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用舊涵蓋。");
        if (string.IsNullOrWhiteSpace(json)) return new() { SensorId = sensorId };
        var value = JsonSerializer.Deserialize<PrtgResourceIdentity>(json, LfJsonOptions.Pretty);
        if (value is null || value.SensorId != sensorId || value.Epoch < 0)
            throw new InvalidDataException("PRTG 資源身分紀錄無效；拒絕沿用舊涵蓋。");
        return value;
    }

    public bool IsCurrent(long sensorId, string sourceGeneration, string resourceGeneration,
        long deviceId, long hostId, string? channelGeneration = null)
    {
        if (string.IsNullOrWhiteSpace(sourceGeneration) || string.IsNullOrWhiteSpace(resourceGeneration)) return false;
        var current = Get(sensorId);
        return current.Epoch > 0 && current.Active && !current.PendingReconciliation && current.SourceGeneration == sourceGeneration &&
            current.Generation == resourceGeneration && current.DeviceId == deviceId && current.HostId == hostId &&
            (channelGeneration is null || current.ChannelGeneration == channelGeneration);
    }

    /// <summary>在呼叫端交易中更新單一資源；相同有效身分不旋轉，移除則留下墓碑。</summary>
    internal static PrtgResourceIdentity Set(LfDbContext ctx, long sensorId, string sourceGeneration,
        long deviceId, long hostId, string resourceFingerprint, string inventoryFingerprint, string channelFingerprint,
        bool active, DateTimeOffset nowUtc, bool reconciled = false)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        return SetCore(ctx, sensorId, sourceGeneration, deviceId, hostId, resourceFingerprint,
            inventoryFingerprint, channelFingerprint, active, nowUtc, row, reconciled, null);
    }

    /// <summary>Updates an identity using a row loaded by the caller's bounded batch query.</summary>
    internal static PrtgResourceIdentity SetLoaded(LfDbContext ctx, long sensorId, string sourceGeneration,
        long deviceId, long hostId, string resourceFingerprint, string inventoryFingerprint, string channelFingerprint,
        bool active, DateTimeOffset nowUtc, BlobRow row, bool reconciled = false,
        Dictionary<string, BlobRow>? authorityRevisionRows = null)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        if (row is null) throw new ArgumentNullException(nameof(row));
        return SetCore(ctx, sensorId, sourceGeneration, deviceId, hostId, resourceFingerprint,
            inventoryFingerprint, channelFingerprint, active, nowUtc, row, reconciled, authorityRevisionRows);
    }

    private static PrtgResourceIdentity SetCore(LfDbContext ctx, long sensorId, string sourceGeneration,
        long deviceId, long hostId, string resourceFingerprint, string inventoryFingerprint, string channelFingerprint,
        bool active, DateTimeOffset nowUtc, BlobRow? row, bool reconciled,
        Dictionary<string, BlobRow>? authorityRevisionRows)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (row is not null && !string.Equals(row.BlobKey, key, StringComparison.Ordinal))
            throw new InvalidDataException("PRTG 資源身分紀錄識別不一致；拒絕沿用舊涵蓋。");
        if (row is not null && !string.IsNullOrWhiteSpace(row.Content)) EnsureBounded(row.Content);
        var current = row is null || string.IsNullOrWhiteSpace(row.Content)
            ? new PrtgResourceIdentity { SensorId = sensorId }
            : JsonSerializer.Deserialize<PrtgResourceIdentity>(row.Content, LfJsonOptions.Pretty)
                ?? throw new InvalidDataException("PRTG 資源身分紀錄無效；拒絕沿用舊涵蓋。");
        if (current.SensorId != sensorId || current.Epoch < 0)
            throw new InvalidDataException("PRTG 資源身分紀錄識別不一致；拒絕沿用舊涵蓋。");

        var priorHostId = current.HostId;
        var wasPending = current.PendingReconciliation;
        var changed = current.Epoch == 0 || current.SourceGeneration != sourceGeneration ||
            current.DeviceId != deviceId || current.HostId != hostId ||
            current.ResourceFingerprint != resourceFingerprint || current.InventoryFingerprint != inventoryFingerprint ||
            current.Active != active;
        var channelChanged = current.ChannelFingerprint != channelFingerprint;
        var clearPending = current.PendingReconciliation && reconciled;
        if (changed || channelChanged || clearPending)
        {
            if (changed)
            {
                // Pending 狀態已在原始變更點推進世代；重新確認只安裝新觀察事實，不再旋轉第二次。
                if (!wasPending)
                {
                    current.Epoch++;
                    current.Generation = Guid.NewGuid().ToString("N");
                }
            }
            if (channelChanged) current.ChannelGeneration = Guid.NewGuid().ToString("N");
            current.SourceGeneration = sourceGeneration;
            current.DeviceId = deviceId;
            current.HostId = hostId;
            current.ResourceFingerprint = resourceFingerprint;
            current.InventoryFingerprint = inventoryFingerprint;
            current.ChannelFingerprint = channelFingerprint;
            current.Active = active;
            if (clearPending) current.PendingReconciliation = false;
            current.ChangedAtUtc = nowUtc;
        }
        var content = SerializeBounded(current);
        if (row is null) ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, Version = 1, UpdatedAt = nowUtc.LocalDateTime });
        else if (changed || channelChanged || clearPending) { row.Content = content; row.Version++; row.UpdatedAt = nowUtc.LocalDateTime; }
        if (changed || channelChanged || clearPending)
        {
            IncrementAuthorityRevision(ctx, priorHostId, nowUtc, authorityRevisionRows);
            if (priorHostId != hostId) IncrementAuthorityRevision(ctx, hostId, nowUtc, authorityRevisionRows);
        }
        return current;
    }

    internal static PrtgResourceIdentity Read(LfDbContext ctx, long sensorId)
    {
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == key)
            .Select(b => new { b.BlobKey, Content = b.Content.Substring(0, MaxLedgerBytes + 1), Length = b.Content.Length,
                b.Version, b.UpdatedAt })
            .SingleOrDefault();
        if (row is null) return new() { SensorId = sensorId };
        if (row.Length > MaxLedgerBytes) throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用舊涵蓋。");
        return ReadLoaded(sensorId, new BlobRow
        {
            BlobKey = row.BlobKey, Content = row.Content, Version = row.Version, UpdatedAt = row.UpdatedAt
        });
    }

    /// <summary>Reads an already bounded identity row without issuing a per-sensor query.</summary>
    internal static PrtgResourceIdentity ReadLoaded(long sensorId, BlobRow? row)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        if (row is null) return new() { SensorId = sensorId };
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(row.BlobKey, key, StringComparison.Ordinal))
            throw new InvalidDataException("PRTG 資源身分紀錄識別不一致；拒絕沿用舊涵蓋。");
        if (string.IsNullOrWhiteSpace(row.Content)) return new() { SensorId = sensorId };
        EnsureBounded(row.Content);
        var value = JsonSerializer.Deserialize<PrtgResourceIdentity>(row.Content, LfJsonOptions.Pretty);
        if (value is null || value.SensorId != sensorId || value.Epoch < 0)
            throw new InvalidDataException("PRTG 資源身分紀錄無效；拒絕沿用舊涵蓋。");
        return value;
    }

    /// <summary>在不改變目前對應值時也推進世代，供移出後再納入及明確來源重建使用。</summary>
    internal static PrtgResourceIdentity Advance(LfDbContext ctx, long sensorId, DateTimeOffset nowUtc,
        string? newSourceGeneration = null)
    {
        var current = Read(ctx, sensorId);
        current.Epoch++;
        current.Generation = Guid.NewGuid().ToString("N");
        if (newSourceGeneration is not null)
        {
            current.SourceGeneration = newSourceGeneration;
            current.PendingReconciliation = true;
        }
        current.ChangedAtUtc = nowUtc;
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        var content = SerializeBounded(current);
        if (row is null) ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, Version = 1, UpdatedAt = nowUtc.LocalDateTime });
        else { row.Content = content; row.Version++; row.UpdatedAt = nowUtc.LocalDateTime; }
        IncrementAuthorityRevision(ctx, current.HostId, nowUtc);
        return current;
    }

    /// <summary>Advances an identity already loaded in the caller's bounded batch transaction.</summary>
    internal static PrtgResourceIdentity AdvanceLoaded(LfDbContext ctx, PrtgResourceIdentity current,
        BlobRow? row, DateTimeOffset nowUtc, string? newSourceGeneration,
        Dictionary<string, BlobRow> authorityRevisionRows, out BlobRow persistedRow)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(authorityRevisionRows);
        var sensorId = current.SensorId;
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(current));
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (row is not null)
        {
            if (!string.Equals(row.BlobKey, key, StringComparison.Ordinal))
                throw new InvalidDataException("PRTG 資源身分紀錄識別不一致；拒絕沿用舊涵蓋。");
            EnsureBounded(row.Content);
        }

        current.Epoch++;
        current.Generation = Guid.NewGuid().ToString("N");
        if (newSourceGeneration is not null)
        {
            current.SourceGeneration = newSourceGeneration;
            current.PendingReconciliation = true;
        }
        current.ChangedAtUtc = nowUtc;
        var content = SerializeBounded(current);
        if (row is null)
        {
            row = new BlobRow { BlobKey = key, Content = content, Version = 1, UpdatedAt = nowUtc.LocalDateTime };
            ctx.Blobs.Add(row);
        }
        else
        {
            row.Content = content;
            row.Version++;
            row.UpdatedAt = nowUtc.LocalDateTime;
        }
        IncrementAuthorityRevision(ctx, current.HostId, nowUtc, authorityRevisionRows);
        persistedRow = row;
        return current;
    }

    internal static PrtgResourceIdentity MarkPending(LfDbContext ctx, long sensorId, DateTimeOffset nowUtc)
    {
        var current = Read(ctx, sensorId);
        current.Epoch++;
        current.Generation = Guid.NewGuid().ToString("N");
        current.PendingReconciliation = true;
        current.ChangedAtUtc = nowUtc;
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        var content = SerializeBounded(current);
        if (row is null) ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, Version = 1, UpdatedAt = nowUtc.LocalDateTime });
        else { row.Content = content; row.Version++; row.UpdatedAt = nowUtc.LocalDateTime; }
        IncrementAuthorityRevision(ctx, current.HostId, nowUtc);
        return current;
    }

    internal static PrtgResourceIdentity Deactivate(LfDbContext ctx, long sensorId, DateTimeOffset nowUtc)
    {
        var current = Read(ctx, sensorId);
        current.Epoch++;
        current.Generation = Guid.NewGuid().ToString("N");
        current.Active = false;
        current.PendingReconciliation = false;
        current.ChangedAtUtc = nowUtc;
        var key = Prefix + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        var content = SerializeBounded(current);
        if (row is null) ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, Version = 1, UpdatedAt = nowUtc.LocalDateTime });
        else { row.Content = content; row.Version++; row.UpdatedAt = nowUtc.LocalDateTime; }
        IncrementAuthorityRevision(ctx, current.HostId, nowUtc);
        return current;
    }

    internal static string AuthorityRevisionKey(long hostId) =>
        AuthorityRevisionPrefix + hostId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string ObservationRevisionKey(long hostId) =>
        ObservationRevisionPrefix + hostId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static void IncrementAuthorityRevision(LfDbContext ctx, long hostId, DateTimeOffset nowUtc,
        Dictionary<string, BlobRow>? loadedRows = null)
    {
        IncrementStableAuthorityRevision(ctx, hostId, nowUtc, loadedRows);
        IncrementObservationRevision(ctx, hostId, nowUtc, loadedRows);
    }

    internal static void IncrementStableAuthorityRevision(LfDbContext ctx, long hostId, DateTimeOffset nowUtc,
        Dictionary<string, BlobRow>? loadedRows = null)
    {
        if (hostId <= 0) return;
        IncrementRevisionRow(ctx, AuthorityRevisionKey(hostId), nowUtc, loadedRows);
    }

    internal static void IncrementObservationRevision(LfDbContext ctx, long hostId, DateTimeOffset nowUtc,
        Dictionary<string, BlobRow>? loadedRows = null)
    {
        if (hostId <= 0) return;
        IncrementRevisionRow(ctx, ObservationRevisionKey(hostId), nowUtc, loadedRows);
    }

    private static void IncrementRevisionRow(LfDbContext ctx, string key, DateTimeOffset nowUtc,
        Dictionary<string, BlobRow>? loadedRows)
    {
        BlobRow? row;
        if (loadedRows is not null && loadedRows.TryGetValue(key, out var cached))
        {
            row = cached;
        }
        else
        {
            row = ctx.Blobs.Local.FirstOrDefault(blob => blob.BlobKey == key) ??
                ctx.Blobs.SingleOrDefault(blob => blob.BlobKey == key);
        }
        if (row is null)
        {
            row = new BlobRow { BlobKey = key, Content = "{}", Version = 1, UpdatedAt = nowUtc.LocalDateTime };
            ctx.Blobs.Add(row);
        }
        else
        {
            row.Version = checked(row.Version + 1);
            row.UpdatedAt = nowUtc.LocalDateTime;
        }
        loadedRows?.TryAdd(key, row);
    }

    /// <summary>Captures tracked host revision rows once so bulk identity refreshes avoid repeated Local scans.</summary>
    internal static Dictionary<string, BlobRow> CaptureLoadedAuthorityRevisionRows(LfDbContext ctx)
        => ctx.Blobs.Local.Where(row => row.BlobKey.StartsWith(AuthorityRevisionPrefix, StringComparison.Ordinal) ||
                row.BlobKey.StartsWith(ObservationRevisionPrefix, StringComparison.Ordinal))
            .ToDictionary(row => row.BlobKey, StringComparer.Ordinal);

    /// <summary>Loads only the bounded revision markers needed by one identity batch into the caller's transaction.</summary>
    internal static void LoadAuthorityRevisionRows(LfDbContext ctx, IEnumerable<long> hostIds,
        Dictionary<string, BlobRow> loadedRows)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(hostIds);
        ArgumentNullException.ThrowIfNull(loadedRows);
        var keys = hostIds.Where(id => id > 0).Distinct()
            .SelectMany(id => new[] { AuthorityRevisionKey(id), ObservationRevisionKey(id) })
            .Where(key => !loadedRows.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var keyBatch in keys.Chunk(500))
        {
            var requested = keyBatch.ToHashSet(StringComparer.Ordinal);
            var stored = ctx.Blobs.AsNoTracking().Where(row => requested.Contains(row.BlobKey))
                .Select(row => new
                {
                    row.BlobKey,
                    Content = row.Content.Substring(0, 129),
                    Length = row.Content.Length,
                    row.Version,
                    row.UpdatedAt
                }).ToList();
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in stored)
            {
                if (item.Length > 128 || item.Content.Length > 128)
                    throw new InvalidDataException("PRTG 主機版本標記超過 128 字元；拒絕不完整更新。");
                var row = new BlobRow
                {
                    BlobKey = item.BlobKey,
                    Content = item.Content,
                    Version = item.Version,
                    UpdatedAt = item.UpdatedAt
                };
                ctx.Blobs.Attach(row);
                loadedRows.Add(row.BlobKey, row);
                found.Add(row.BlobKey);
            }
            foreach (var key in keyBatch)
            {
                if (found.Contains(key)) continue;
                var row = new BlobRow { BlobKey = key, Content = "{}", Version = 0, UpdatedAt = DateTime.Now };
                ctx.Blobs.Add(row);
                loadedRows.Add(key, row);
            }
        }
    }

    internal static void MarkPendingForHosts(LfDbContext ctx, IEnumerable<long> hostIds, DateTimeOffset nowUtc)
    {
        var hosts = hostIds.Where(id => id > 0).Distinct().ToArray();
        if (hosts.Length == 0) return;
        var latestDate = ctx.PrtgHostMaps.Select(m => (DateTime?)m.MapDate).Max();
        var devices = new HashSet<long>();
        for (var offset = 0; offset < hosts.Length; offset += 500)
        {
            var batch = hosts.Skip(offset).Take(500).ToArray();
            if (latestDate.HasValue)
            {
                devices.UnionWith(ctx.PrtgHostMaps.AsNoTracking().Where(m => m.MapDate == latestDate.Value &&
                    m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && batch.Contains(m.HostId.Value))
                    .Select(m => m.DeviceObjid).ToArray());
            }
            devices.UnionWith(ctx.PrtgManualMaps.AsNoTracking().Where(m => batch.Contains(m.HostId))
                .Select(m => m.DeviceObjid).ToArray());
        }
        foreach (var deviceBatch in devices.Order().Chunk(500))
        {
            var sensorIds = ctx.PrtgSensors.AsNoTracking().Where(s => deviceBatch.Contains(s.DeviceObjid))
                .Select(s => s.Objid).ToArray();
            foreach (var sensorId in sensorIds) MarkPending(ctx, sensorId, nowUtc);
        }
    }

    private static string SerializeBounded(PrtgResourceIdentity identity)
    {
        var content = JsonSerializer.Serialize(identity, LfJsonOptions.Pretty);
        EnsureBounded(content);
        return content;
    }

    private static void EnsureBounded(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxLedgerBytes)
            throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用或寫入舊涵蓋。");
    }
}
