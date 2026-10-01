using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>只查明列 sensor。完整讀到結尾才提交涵蓋；截斷／格式錯誤不延續舊狀態。</summary>
public sealed class PrtgSensorTimelineCollector(StorageBackend backend, PrtgClient client)
{
    public async Task<PrtgSensorTimelineEvidence> CollectAsync(long sensorId, long hostId,
        PrtgMonitoringPolicy policy, CancellationToken ct)
    {
        var store = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensorId));
        var attemptAt = DateTimeOffset.Now;
        var mappingRevision = backend.Blob(LogForesight.Core.Persistence.Sql.EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        // 人工對應改變後，查詢失敗也不能繼續消費舊資源證據。
        store.Update(e =>
        {
            if (e.MappingRevision != mappingRevision || e.HostId != hostId || e.SourceGeneration != policy.SourceGeneration)
            {
                e.Coverage.Clear(); e.States.Clear(); e.IdentityFingerprint = "";
                e.ValidFrom = attemptAt; e.QualityReason = "identity-warmup";
                e.MappingRevision = mappingRevision;
            }
        });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!policy.SensorIds.Contains(sensorId) || !policy.HostIds.Contains(hostId))
                throw new InvalidOperationException("outside-pilot");
            TimeZoneInfo sourceZone;
            CultureInfo sourceCulture;
            try { sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId); sourceCulture = CultureInfo.GetCultureInfo(policy.SourceCultureName); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw new InvalidOperationException("source-time-zone-or-culture-unverified"); }
            using var sensorDoc = JsonDocument.Parse(await client.GetJsonAsync(
                $"api/table.json?content=sensors&id={sensorId}&columns=objid,parentid,type,status,cumsince&count=2", budget.Token));
            attemptAt = DateTimeOffset.Now;
            if (!sensorDoc.RootElement.TryGetProperty("sensors", out var sensors) || sensors.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("sensor-identity-unavailable");
            var exact = sensors.EnumerateArray().Where(s => Read(s, "objid") == sensorId.ToString(CultureInfo.InvariantCulture)).ToArray();
            if (exact.Length != 1) throw new InvalidOperationException("sensor-identity-not-unique");
            var sensor = exact[0];
            var parent = Read(sensor, "parentid"); var type = Read(sensor, "type");
            var since = Read(sensor, "cumsince_raw");
            if (string.IsNullOrWhiteSpace(since)) since = Read(sensor, "cumsince");
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(since))
                throw new InvalidOperationException("resource-generation-unverified");
            var fingerprint = $"{parent}|{type}|{since}|map:{backend.Blob(LogForesight.Core.Persistence.Sql.EfPrtgStore.ScopeRevisionBlobKey).ReadVersion()}";
            var previousGeneration = store.Get().ResourceGeneration;
            var evidence = store.Update(e => e.Bind(sensorId, hostId, policy.SourceGeneration, fingerprint, attemptAt));
            if (previousGeneration.Length > 0 && previousGeneration != evidence.ResourceGeneration)
                backend.Blob("prtg_resource_generation_revision").Mutate(_ => (System.Text.Json.JsonSerializer.Serialize(Guid.NewGuid().ToString("N")), true));
            // 每次最多 31 天；新身分只能從確認當下開始。當前狀態僅是取得時間的檢查點。
            var from = evidence.ValidFrom > attemptAt.AddDays(-31) ? evidence.ValidFrom : attemptAt.AddDays(-31);
            if (policy.ValidFrom > from) from = policy.ValidFrom;
            var states = new List<PrtgTimedState>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ended = false;
            DateTimeOffset? previous = null;
            for (var page = 0; page < 20; page++)
            {
                using var doc = JsonDocument.Parse(await client.GetJsonAsync(
                    $"api/table.json?content=messages&id={sensorId}&columns=objid,datetime,status,message&filter_drel=12months&sortby=-datetime&start={page * 1000}&count=1000", budget.Token));
                if (!doc.RootElement.TryGetProperty("messages", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("messages-array-missing");
                var count = rows.GetArrayLength();
                if (count == 0) { ended = true; break; }
                var older = false;
                foreach (var row in rows.EnumerateArray())
                {
                    if (Read(row, "objid") != sensorId.ToString(CultureInfo.InvariantCulture) ||
                        !DateTime.TryParse(Read(row, "datetime"), sourceCulture, DateTimeStyles.None, out var local))
                        throw new InvalidOperationException("sensor-or-time-unverified");
                    var at = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), sourceZone.GetUtcOffset(local));
                    if (sourceZone.IsAmbiguousTime(local) || sourceZone.IsInvalidTime(local))
                        throw new InvalidOperationException("source-time-zone-ambiguous");
                    if (at > attemptAt.AddMinutes(2) || previous.HasValue && at > previous.Value)
                        throw new InvalidOperationException("source-clock-or-order-unverified");
                    previous = at;
                    var status = ReadStatus(row);
                    if (string.IsNullOrWhiteSpace(status)) throw new InvalidOperationException("state-unavailable");
                    if (!seen.Add($"{at:O}|{status}|{Read(row, "message")}"))
                        throw new InvalidOperationException("messages-repeated-page-or-ambiguous-event");
                    if (at < from) { older = true; continue; }
                    if (at <= attemptAt) states.Add(new(sensorId, at, status, policy.SourceGeneration, evidence.ResourceGeneration));
                }
                if (older || count < 1000) { ended = true; break; }
            }
            if (!ended) throw new InvalidOperationException("messages-truncated");
            var current = ReadStatus(sensor);
            if (!string.IsNullOrWhiteSpace(current))
                states.Add(new(sensorId, attemptAt, current, policy.SourceGeneration, evidence.ResourceGeneration));
            return store.Update(e =>
            {
                if (e.ResourceGeneration != evidence.ResourceGeneration) throw new InvalidOperationException("identity-changed");
                if (attemptAt > from) e.Accept(from, attemptAt, states);
                else { e.States = states; e.QualityReason = "identity-warmup"; e.LastAttemptAt = attemptAt; }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return store.Update(e => { e.LastAttemptAt = attemptAt; e.QualityReason = "query-time-budget-exceeded"; }); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or PrtgClientException)
        { return store.Update(e => { e.LastAttemptAt = attemptAt; e.QualityReason = ex is InvalidOperationException ? ex.Message : "source-query-failed"; }); }
    }

    private static string Read(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static string ReadStatus(JsonElement row)
    {
        var raw = Read(row, "status_raw");
        var text = Read(row, "status");
        if (!int.TryParse(raw.Length > 0 ? raw : text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return text;
        return code switch
        { 3 => "Up", 4 => "Warning", 5 => "Down", 13 => "Down (Acknowledged)", 14 => "Down (Partial)",
            7 or 8 or 9 or 12 => "Paused", _ => "Unknown" };
    }
}
