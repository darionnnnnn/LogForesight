using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// Fair, restartable native status capture. It reads one complete device at a time and relies on the
/// shared PrtgRequestBudget; the daily pipeline only consumes snapshots already captured here.
/// </summary>
public sealed class PrtgSilentPresenceHostedService(StorageBackend backend) : BackgroundService
{
    private const string CursorKey = "prtg_silent_presence_cursor";
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mirror = backend.PrtgStore();
        var capture = new PrtgSilentPresenceCapture(backend);
        var completeToday = new HashSet<long>();
        string? capturedSetKey = null;
        DateTime? lastPrunedSourceDay = null;
        var lastPrunedRetentionDays = 0;
        PrtgClient? client = null;
        var clientSettingsRevision = string.Empty;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                if (!settings.PrtgEnabled || !PrtgClientFactory.HasUsableCredentials(settings) ||
                    policy.SourceAuthorityFingerprint(settings.PrtgUrl).Length == 0)
                {
                    client?.Dispose(); client = null; clientSettingsRevision = string.Empty;
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }
                if (client is null || clientSettingsRevision != settings.Revision)
                {
                    client?.Dispose();
                    client = PrtgClientFactory.Create(settings);
                    clientSettingsRevision = settings.Revision;
                }

                // The local clock selects the work window only. A proof's source day/as-of is always
                // assigned from the native HTTP Date header by CaptureDeviceAsync.
                var expectedSourceDay = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId)).Date;
                var eligibleHosts = new HostStore(backend.Blob("hosts")).GetAll()
                    .Where(host => PrtgFormalEligibility.HostAllowed(host, settings, policy))
                    .Select(host => host.HostId).ToHashSet();
                var maps = mirror.GetHostMapForDate(expectedSourceDay)
                    .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue && eligibleHosts.Contains(row.HostId.Value))
                    .GroupBy(row => row.DeviceObjid)
                    .Where(group => group.Select(row => row.HostId!.Value).Distinct().Count() == 1)
                    .Select(group => (DeviceObjid: group.Key, HostId: group.First().HostId!.Value))
                    .OrderBy(row => row.DeviceObjid).ToArray();
                if (maps.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var mapFingerprint = PrtgSilentPresenceMappingFingerprint.Compute(maps
                    .Select(row => new KeyValuePair<long, long>(row.DeviceObjid, row.HostId)));
                var retentionDays = PrtgSilentPresenceSnapshotStore.EffectiveRetentionDays(settings.RetentionDays);
                if (lastPrunedSourceDay != expectedSourceDay || lastPrunedRetentionDays != retentionDays)
                {
                    PrtgSilentPresenceSnapshotStore.PruneOlderThan(backend.CreateContext,
                        PrtgSilentPresenceSnapshotStore.OldestSourceDayToKeep(expectedSourceDay, retentionDays));
                    lastPrunedSourceDay = expectedSourceDay;
                    lastPrunedRetentionDays = retentionDays;
                }
                var sourceAuthorityFingerprint = policy.SourceAuthorityFingerprint(settings.PrtgUrl);
                var sourceSetKey = $"{expectedSourceDay:yyyyMMdd}|{sourceAuthorityFingerprint}|{mapFingerprint}";
                if (!string.Equals(capturedSetKey, sourceSetKey, StringComparison.Ordinal))
                {
                    completeToday.Clear();
                    foreach (var page in maps.Chunk(50))
                    {
                        var snapshots = PrtgSilentPresenceSnapshotStore.ReadMany(backend.CreateContext,
                            page.Select(row => row.DeviceObjid), expectedSourceDay);
                        foreach (var (deviceId, snapshot) in snapshots)
                        {
                            var expectedHost = page.FirstOrDefault(row => row.DeviceObjid == deviceId).HostId;
                            if (snapshot.ReadQuality == PrtgPresenceReadQuality.Complete &&
                                snapshot.SourceAuthorityFingerprint == sourceAuthorityFingerprint && snapshot.SourceGeneration == policy.SourceGeneration &&
                                snapshot.MappingFingerprint == PrtgSilentPresenceMappingFingerprint.Compute(deviceId, expectedHost) &&
                                snapshot.HostId == expectedHost)
                                completeToday.Add(deviceId);
                        }
                    }
                    capturedSetKey = sourceSetKey;
                }
                if (completeToday.Count == maps.Length)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                    continue;
                }

                var cursorStore = backend.Blob(CursorKey);
                var lastId = ReadCursor(cursorStore.Read());
                var candidates = maps.Where(row => !completeToday.Contains(row.DeviceObjid)).ToArray();
                var target = candidates.FirstOrDefault(row => row.DeviceObjid > lastId);
                if (target.DeviceObjid == 0) target = candidates[0];
                var cursorJson = JsonSerializer.Serialize(target.DeviceObjid);
                cursorStore.Mutate(_ => (cursorJson, true));

                var categoryOverrides = PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map;
                var deviceStore = new PrtgSilentPresenceSnapshotStore(
                    backend.Blob(PrtgSilentPresenceSnapshotStore.BlobKey(target.DeviceObjid, expectedSourceDay)));
                var result = await capture.CaptureDeviceAsync(client, deviceStore, policy, settings.PrtgUrl,
                    target.DeviceObjid, target.HostId, expectedSourceDay,
                    PrtgSilentPresenceMappingFingerprint.Compute(target.DeviceObjid, target.HostId), categoryOverrides, stoppingToken);
                if (result.ReadQuality == PrtgPresenceReadQuality.Complete) completeToday.Add(target.DeviceObjid);
                if (result.ReadQuality != PrtgPresenceReadQuality.Complete)
                    Log.Debug("PRTG silent presence remains waiting for device {DeviceId}: {Reason}", target.DeviceObjid, result.ReadReason);
                // Two native table responses form one conservatively time-bounded source proof.
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn(ex, "PRTG silent presence source refresh failed; persisted proof stays unusable until a complete replacement arrives");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        client?.Dispose();
    }

    private static long ReadCursor(string? json)
    {
        try
        {
            var value = json is null ? 0 : JsonSerializer.Deserialize<long>(json);
            return value > 0 ? value : 0;
        }
        catch (JsonException) { return 0; }
    }
}
