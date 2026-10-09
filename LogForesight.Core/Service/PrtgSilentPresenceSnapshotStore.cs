using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Service;

/// <summary>One bounded SQL blob per device/source-day; snapshots are retained for the configured history window.</summary>
public sealed class PrtgSilentPresenceSnapshotStore(EfJsonBlobStore blob)
{
    public const string Prefix = "prtg_silent_presence_";
    public const int MaxSnapshotBytes = 64 * 1024;
    public const int MaxSupportedRetentionDays = 180;
    private const int MaxSnapshotCharacters = MaxSnapshotBytes;

    public static int EffectiveRetentionDays(int configuredRetentionDays) =>
        Math.Clamp(configuredRetentionDays, 1, MaxSupportedRetentionDays);

    public static DateTime OldestSourceDayToKeep(DateTime currentSourceDay, int configuredRetentionDays) =>
        currentSourceDay.Date.AddDays(-EffectiveRetentionDays(configuredRetentionDays));

    public static string BlobKey(long deviceObjid, DateTime sourceDay) =>
        Prefix + deviceObjid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + sourceDay.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    public void Save(PrtgSilentDeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot, LfJsonOptions.Pretty);
        if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes)
        {
            // Replace the old day proof with a bounded failed marker. Otherwise an oversize new
            // inventory could leave yesterday's same-key data looking current to a later reader.
            snapshot = snapshot with
            {
                SourceAsOf = null, DeviceStatusAsOf = null, CapturedAtUtc = null,
                ReportedSensorCount = 0, InventoryFingerprint = string.Empty, Sensors = [],
                ReadReason = "snapshot-exceeds-64KiB"
            };
            snapshot = snapshot with { ReadQuality = PrtgPresenceReadQuality.Failed };
            json = JsonSerializer.Serialize(snapshot, LfJsonOptions.Pretty);
        }
        blob.Mutate(_ => (json, true));
    }

    public PrtgSilentDeviceSnapshot? Get(long deviceObjid)
    {
        if (deviceObjid <= 0) return null;
        var (prefix, _, reportedLength) = blob.ReadBoundedWithVersion(MaxSnapshotCharacters);
        if (prefix is null) return null;
        if (reportedLength > MaxSnapshotCharacters || Encoding.UTF8.GetByteCount(prefix) > MaxSnapshotBytes)
            throw new InvalidDataException("靜默監測來源快照超過 64 KiB 上限；拒絕沿用舊證據。");
        PrtgSilentDeviceSnapshot? value;
        try { value = JsonSerializer.Deserialize<PrtgSilentDeviceSnapshot>(prefix, LfJsonOptions.Pretty); }
        catch (JsonException ex) { throw new InvalidDataException("靜默監測來源快照無效；拒絕沿用舊證據。", ex); }
        if (value is null || value.DeviceObjid != deviceObjid || value.Sensors is null || value.Sensors.Count > 512)
            throw new InvalidDataException("靜默監測來源快照欄位無效；拒絕沿用舊證據。");
        return value;
    }

    public static IReadOnlyDictionary<long, PrtgSilentDeviceSnapshot> ReadMany(
        Func<LfDbContext> contextFactory, IEnumerable<long> deviceObjids, DateTime sourceDay)
        => ReadManyForWindow(contextFactory, deviceObjids, [sourceDay.Date],
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    public static IReadOnlyDictionary<long, PrtgSilentDeviceSnapshot> ReadManyForWindow(
        Func<LfDbContext> contextFactory, IEnumerable<long> deviceObjids, IEnumerable<DateTime> sourceDays,
        DateTimeOffset requestedStartUtc, DateTimeOffset requestedEndUtc)
    {
        var ids = deviceObjids.Where(id => id > 0).Distinct().Order().ToArray();
        var days = sourceDays.Select(day => day.Date).Distinct().Order().ToArray();
        if (requestedEndUtc <= requestedStartUtc) throw new ArgumentException("Requested source window must be positive.");
        var result = new Dictionary<long, PrtgSilentDeviceSnapshot>();
        var pairs = ids.SelectMany(id => days.Select(day => (Id: id, Day: day))).ToArray();
        for (var offset = 0; offset < pairs.Length; offset += 50)
        {
            var page = pairs.Skip(offset).Take(50).ToArray();
            var keys = page.ToDictionary(pair => BlobKey(pair.Id, pair.Day), pair => pair,
                StringComparer.Ordinal);
            using var context = contextFactory();
            var rows = context.Blobs.AsNoTracking().Where(row => keys.Keys.Contains(row.BlobKey))
                .Select(row => new
                {
                    row.BlobKey,
                    ContentPrefix = row.Content.Substring(0, MaxSnapshotCharacters + 1),
                    CharacterLength = row.Content.Length
                }).ToList();
            foreach (var row in rows)
            {
                var pair = keys[row.BlobKey];
                if (row.CharacterLength > MaxSnapshotCharacters || Encoding.UTF8.GetByteCount(row.ContentPrefix) > MaxSnapshotBytes)
                    continue;
                try
                {
                    var snapshot = JsonSerializer.Deserialize<PrtgSilentDeviceSnapshot>(row.ContentPrefix, LfJsonOptions.Pretty);
                    if (snapshot is { Sensors.Count: <= 512 } && snapshot.DeviceObjid == pair.Id && snapshot.SourceDay == pair.Day &&
                        snapshot.SourceAsOf is { } asOf && asOf >= requestedStartUtc && asOf < requestedEndUtc &&
                        (!result.TryGetValue(pair.Id, out var previous) || !previous.SourceAsOf.HasValue || previous.SourceAsOf.Value < asOf))
                        result[pair.Id] = snapshot;
                }
                catch (JsonException) { }
            }
        }
        return result;
    }

    public static int PruneOlderThan(Func<LfDbContext> contextFactory, DateTime oldestSourceDayToKeep)
    {
        var cutoff = oldestSourceDayToKeep.Date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var deleted = 0;
        string? lastKey = null;
        while (true)
        {
            using var context = contextFactory();
            var pageQuery = context.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(Prefix));
            if (lastKey is not null) pageQuery = pageQuery.Where(row => string.Compare(row.BlobKey, lastKey) > 0);
            var keys = pageQuery.OrderBy(row => row.BlobKey).Select(row => row.BlobKey).Take(500).ToList();
            if (keys.Count == 0) break;
            lastKey = keys[^1];
            var expired = keys.Where(key =>
            {
                var suffix = key.LastIndexOf('_');
                return suffix >= 0 && key.Length - suffix - 1 == 8 &&
                    string.CompareOrdinal(key[(suffix + 1)..], cutoff) < 0;
            }).ToArray();
            if (expired.Length == 0) continue;
            var rows = context.Blobs.Where(row => expired.Contains(row.BlobKey)).ToList();
            context.Blobs.RemoveRange(rows);
            deleted += rows.Count;
            context.SaveChanges();
        }
        return deleted;
    }
}
