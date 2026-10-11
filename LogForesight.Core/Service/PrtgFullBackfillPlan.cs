using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>One day's exact historical targets, resolved with the same triggered scope as full backfill.</summary>
public sealed record PrtgFullBackfillDayTargets(
    DateTime Day,
    int TriggeredHosts,
    int MappedHosts,
    int SelectedHosts,
    IReadOnlyList<long> DeviceObjids,
    IReadOnlyList<long> SensorObjids,
    string Fingerprint);

/// <summary>Bounded summary for a stored full-backfill range. Only per-day counts and hashes are retained.</summary>
public sealed record PrtgFullBackfillPlan(
    DateTime FromDate,
    DateTime ToDate,
    int DayCount,
    long HistoricRequests,
    int DaysWithTargets,
    int StateChangeObjects,
    string TargetFingerprint,
    IReadOnlyList<PrtgFullBackfillDaySummary> Days)
{
    public long HistoricQuotaLowerBoundSeconds => QuotaLowerBoundSeconds(HistoricRequests);

    public static long QuotaLowerBoundSeconds(long requests) => requests <= 1 ? 0 : ((requests - 1) / 5) * 60;
}

public sealed record PrtgFullBackfillDaySummary(DateTime Day, int TriggeredHosts, int MappedHosts, int SelectedHosts,
    int TargetSensors, string TargetFingerprint);

/// <summary>Shared resolver used by preview and execution; mirrors are read only and this resolver makes no PRTG requests.</summary>
public static class PrtgFullBackfillPlanBuilder
{
    public static PrtgFullBackfillDayTargets ResolveDay(
        EfPrtgStore store,
        IAnalysisRecordQuery records,
        DateTime day,
        IReadOnlyCollection<long> scopeDeviceObjids,
        IReadOnlyCollection<string>? whitelist,
        string? scope,
        IReadOnlyCollection<long>? extraScopeHosts)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(scopeDeviceObjids);

        var date = day.Date;
        var hostMapRows = store.GetLatestHostMapWithDate(PrtgTriggeredValueFetcher.HostMapLookbackDays, date).Rows;
        var mappedHostIds = hostMapRows
            .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue)
            .Select(row => row.HostId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var riskyRecords = records.QueryLightweight(new RecordQueryFilter
        {
            From = date,
            To = date,
            RiskLevels = new[] { "高", "中" },
            Hosts = null
        });
        var triggeredHostIds = riskyRecords.Select(row => row.HostId).Distinct().OrderBy(id => id).ToArray();
        var selectedHostIds = PrtgValueFetchScope.SelectHosts(
            scope, triggeredHostIds, mappedHostIds, extraScopeHosts ?? Array.Empty<long>(),
            whitelist == null || whitelist.Count == 0).Distinct().OrderBy(id => id).ToArray();
        var selected = selectedHostIds.ToHashSet();
        var deviceObjids = hostMapRows
            .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue && selected.Contains(row.HostId.Value))
            .Select(row => row.DeviceObjid)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var sensorObjids = deviceObjids.Length == 0
            ? Array.Empty<long>()
            : store.GetValueFetchTargets(whitelist, deviceObjids).Distinct().OrderBy(id => id).ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, "maps");
        foreach (var row in hostMapRows.OrderBy(row => row.DeviceObjid).ThenBy(row => row.HostId).ThenBy(row => row.MapStatus, StringComparer.Ordinal))
            Add(hash, $"{row.MapDate:yyyy-MM-dd}|{row.DeviceObjid}|{row.HostId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}|{row.MapStatus}|{row.Ip}");
        Add(hash, "records");
        foreach (var row in riskyRecords.OrderBy(row => row.RecordId).ThenBy(row => row.HostId))
            Add(hash, $"{row.RecordId}|{row.HostId}|{row.RiskLevel}");
        Add(hash, "scope");
        foreach (var id in scopeDeviceObjids.OrderBy(id => id)) Add(hash, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, "selected-hosts");
        foreach (var id in selectedHostIds) Add(hash, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, "devices");
        foreach (var id in deviceObjids) Add(hash, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, "sensors");
        foreach (var id in sensorObjids) Add(hash, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new PrtgFullBackfillDayTargets(date, triggeredHostIds.Length, mappedHostIds.Length,
            selectedHostIds.Length, deviceObjids, sensorObjids, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static PrtgFullBackfillPlan Build(
        EfPrtgStore store,
        IAnalysisRecordQuery records,
        DateTime anchorDate,
        int days,
        IReadOnlyCollection<long> scopeDeviceObjids,
        IReadOnlyCollection<string>? whitelist,
        string? scope,
        IReadOnlyCollection<long>? extraScopeHosts)
    {
        if (days is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(days), "Backfill days must be between 1 and 365.");
        var anchor = anchorDate.Date;
        var from = anchor.AddDays(-days);
        var to = anchor.AddDays(-1);
        var summaries = new List<PrtgFullBackfillDaySummary>(days);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long requests = 0;
        var daysWithTargets = 0;
        for (var day = to; day >= from; day = day.AddDays(-1))
        {
            var targets = ResolveDay(store, records, day, scopeDeviceObjids, whitelist, scope, extraScopeHosts);
            Add(hash, $"{day:yyyy-MM-dd}|{targets.Fingerprint}");
            requests = checked(requests + targets.SensorObjids.Count);
            if (targets.SensorObjids.Count > 0) daysWithTargets++;
            summaries.Add(new PrtgFullBackfillDaySummary(day, targets.TriggeredHosts, targets.MappedHosts,
                targets.SelectedHosts, targets.SensorObjids.Count, targets.Fingerprint));
        }

        summaries.Reverse();
        return new PrtgFullBackfillPlan(from, to, days, requests, daysWithTargets, scopeDeviceObjids.Count,
            Convert.ToHexString(hash.GetHashAndReset()), summaries);
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData(new byte[] { 0 });
    }
}
