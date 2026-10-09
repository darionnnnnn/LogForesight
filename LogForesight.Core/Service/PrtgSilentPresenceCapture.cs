using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Reads one native, unfiltered per-device source cross-section and durably replaces its proof.</summary>
public sealed class PrtgSilentPresenceCapture(StorageBackend backend)
{
    public const int MaxSensorsPerDevice = 512;
    private const int MaxSourceResponseBytes = 512 * 1024;

    public async Task<PrtgSilentDeviceSnapshot> CaptureDeviceAsync(PrtgClient client,
        PrtgSilentPresenceSnapshotStore store, PrtgMonitoringPolicy policy, string configuredUrl,
        long deviceObjid, long hostId, DateTime expectedSourceDay, string mappingFingerprint,
        IReadOnlyDictionary<string, string> categoryOverrides,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        var scopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var sourceAuthorityFingerprint = policy.SourceAuthorityFingerprint(configuredUrl);
        PrtgSilentDeviceSnapshot snapshot;
        try
        {
            if (sourceAuthorityFingerprint.Length == 0 || !policy.HostIds.Contains(hostId))
                throw new InvalidOperationException("policy-not-attested");
            if (!string.Equals(mappingFingerprint, PrtgSilentPresenceMappingFingerprint.Compute(deviceObjid, hostId), StringComparison.Ordinal))
                throw new InvalidOperationException("device-mapping-authority-invalid");
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
                _ = CultureInfo.GetCultureInfo(policy.SourceCultureName);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw new InvalidOperationException("policy-time-basis-unverified"); }

            var deviceResponse = await client.GetBoundedJsonResponseAsync(
                $"api/table.json?content=devices&id={deviceObjid}&columns=objid,paused&start=0&count=2&sortby=objid",
                MaxSourceResponseBytes, cancellationToken);
            var deviceDoc = ParseTable(deviceResponse.Content, "devices", expectedCount: 1, limit: 1);
            var deviceRow = deviceDoc.Rows[0];
            if (ReadLong(deviceRow, "objid") != deviceObjid)
                throw new InvalidOperationException("device-identity-mismatch");
            if (!TryReadPaused(deviceRow, out var devicePaused))
                throw new InvalidOperationException("device-pause-state-unavailable");

            var sensorResponse = await client.GetBoundedJsonResponseAsync(
                $"api/table.json?content=sensors&id={deviceObjid}&columns=objid,parentid,type,status,status_raw,paused&start=0&count={MaxSensorsPerDevice + 1}&sortby=objid",
                MaxSourceResponseBytes, cancellationToken);
            var sensorDoc = ParseTable(sensorResponse.Content, "sensors", expectedCount: null, limit: MaxSensorsPerDevice);
            if (sensorDoc.Rows.Count == 0 || sensorDoc.Rows.Count > MaxSensorsPerDevice ||
                sensorDoc.Rows.Any(row => ReadLong(row, "parentid") != deviceObjid))
                throw new InvalidOperationException("sensor-inventory-count-or-scope-unverified");
            if (deviceResponse.HttpDateUtc is null || sensorResponse.HttpDateUtc is null)
                throw new InvalidOperationException("source-response-date-missing");
            if (Math.Abs((deviceResponse.ReceivedAtUtc - deviceResponse.HttpDateUtc.Value).TotalMinutes) > 2 ||
                Math.Abs((sensorResponse.ReceivedAtUtc - sensorResponse.HttpDateUtc.Value).TotalMinutes) > 2)
                throw new InvalidOperationException("source-clock-attestation-failed");
            var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
            var deviceSourceDay = TimeZoneInfo.ConvertTime(deviceResponse.HttpDateUtc.Value, sourceZone).Date;
            var sensorSourceDay = TimeZoneInfo.ConvertTime(sensorResponse.HttpDateUtc.Value, sourceZone).Date;
            var responseInterval = sensorResponse.HttpDateUtc.Value - deviceResponse.HttpDateUtc.Value;
            if (deviceSourceDay != expectedSourceDay.Date || sensorSourceDay != expectedSourceDay.Date ||
                responseInterval < TimeSpan.Zero || responseInterval > TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("source-day-or-pause-status-interval-unverified");

            var rows = new List<PrtgSilentSensorSnapshot>(sensorDoc.Rows.Count);
            foreach (var row in sensorDoc.Rows)
            {
                var sensorId = ReadLong(row, "objid");
                var type = ReadString(row, "type");
                if (sensorId <= 0 || string.IsNullOrWhiteSpace(type) || !TryReadPaused(row, out var paused))
                    throw new InvalidOperationException("sensor-inventory-row-incomplete");
                rows.Add(new(sensorId, deviceObjid, type,
                    PrtgSensorTypeCategoryMap.Resolve(type, categoryOverrides), ReadStatus(row), paused));
            }
            if (rows.Select(s => s.SensorObjid).Distinct().Count() != rows.Count)
                throw new InvalidOperationException("sensor-inventory-duplicate-object");

            var digest = InventoryFingerprint(rows);
            snapshot = new PrtgSilentDeviceSnapshot(deviceObjid, hostId, policy.SourceGeneration,
                policy.Revision, scopeRevision, mappingFingerprint, expectedSourceDay.Date, sensorResponse.HttpDateUtc,
                deviceResponse.HttpDateUtc, sensorResponse.ReceivedAtUtc, devicePaused, rows.Count,
                digest, rows)
            { SourceAuthorityFingerprint = sourceAuthorityFingerprint };
            if (backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != scopeRevision)
                throw new InvalidOperationException("scope-changed-during-source-read");
            var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            if (!currentPolicy.HostIds.Contains(hostId) ||
                !string.Equals(currentPolicy.SourceAuthorityFingerprint(configuredUrl), sourceAuthorityFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("policy-changed-during-source-read");
        }
        catch (OperationCanceledException)
        {
            store.Save(new PrtgSilentDeviceSnapshot(deviceObjid, hostId, policy.SourceGeneration,
                policy.Revision, scopeRevision, mappingFingerprint, expectedSourceDay.Date, null, null, null, false, 0, string.Empty, [], "source-read-cancelled")
            { ReadQuality = PrtgPresenceReadQuality.Failed, SourceAuthorityFingerprint = sourceAuthorityFingerprint });
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or PrtgClientException or InvalidDataException)
        {
            snapshot = new PrtgSilentDeviceSnapshot(deviceObjid, hostId, policy.SourceGeneration,
                policy.Revision, scopeRevision, mappingFingerprint, expectedSourceDay.Date, null, null, null, false, 0, string.Empty, [], ex.Message)
            { ReadQuality = ex is PrtgClientException ? PrtgPresenceReadQuality.Failed : PrtgPresenceReadQuality.Partial,
                SourceAuthorityFingerprint = sourceAuthorityFingerprint };
        }
        store.Save(snapshot);
        return snapshot;
    }

    private static (List<JsonElement> Rows, int TreeSize) ParseTable(string json, string content, int? expectedCount, int limit)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var parsedTreeSize = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("treesize", out var treeSizeValue)
            ? (treeSizeValue.ValueKind == JsonValueKind.Number && treeSizeValue.TryGetInt32(out var numeric) ? numeric :
                treeSizeValue.ValueKind == JsonValueKind.String && int.TryParse(treeSizeValue.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var text) ? text : -1)
            : -1;
        if (root.ValueKind != JsonValueKind.Object || parsedTreeSize < 0 || parsedTreeSize > limit ||
            !root.TryGetProperty(content, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("source-table-completeness-metadata-missing");
        var treeSize = parsedTreeSize;
        var rows = array.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).Select(row => row.Clone()).ToList();
        if (rows.Count != array.GetArrayLength() || rows.Count != treeSize || expectedCount.HasValue && rows.Count != expectedCount.Value)
            throw new InvalidOperationException("source-table-count-mismatch-or-truncated");
        return (rows, treeSize);
    }

    private static long ReadLong(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)) return parsed;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    }

    private static string ReadString(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static bool TryReadPaused(JsonElement row, out bool paused)
    {
        paused = false;
        if (!row.TryGetProperty("paused", out var value)) return false;
        if (value.ValueKind == JsonValueKind.True) { paused = true; return true; }
        if (value.ValueKind == JsonValueKind.False) return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) { paused = number != 0; return true; }
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString()?.Trim();
        if (bool.TryParse(text, out paused)) return true;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) { paused = number != 0; return true; }
        return false;
    }

    private static string ReadStatus(JsonElement row)
    {
        var raw = ReadString(row, "status_raw");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return code switch { 3 => "Up", 4 => "Warning", 5 => "Down", 13 => "Down (Acknowledged)", 14 => "Down (Partial)", 7 or 8 or 9 or 12 => "Paused", _ => "Unknown" };
        return ReadString(row, "status");
    }

    private static string InventoryFingerprint(IEnumerable<PrtgSilentSensorSnapshot> rows)
    {
        var canonical = string.Join("\n", rows.OrderBy(x => x.SensorObjid)
            .Select(x => $"{x.SensorObjid}|{x.DeviceObjid}|{x.SensorType}|{x.Category}|{x.SourceStatus}|{x.Paused}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
