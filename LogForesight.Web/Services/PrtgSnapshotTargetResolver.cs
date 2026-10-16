using System.Globalization;
using System.Text;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>快照請求的形狀與目標挑選（docs/PRTG-SPEC.md §3b）：每批最多 100 顆、依 objid 排序的 filter_objid。</summary>
public static class PrtgSnapshotTargetResolver
{
    /// <summary>每個 filter_objid 快照請求最多帶的 objid 數。</summary>
    public const int BatchSize = 100;
    public const string SnapshotColumns = "objid,lastvalue,interval,lastcheck,status";
    public const int SnapshotCountSentinel = 1;
    public const int MaximumRelativeUrlBytes = 4096;

    public static string BuildSnapshotRelativeUrl(IReadOnlyList<long> sensorObjids)
    {
        if (sensorObjids.Count is < 1 or > BatchSize ||
            sensorObjids.Any(id => id <= 0) ||
            sensorObjids.Distinct().Count() != sensorObjids.Count ||
            !sensorObjids.SequenceEqual(sensorObjids.Order()))
            throw new ArgumentException("Snapshot request must use a non-empty, sorted, distinct batch of at most 100 IDs.", nameof(sensorObjids));

        var url = "api/table.json?content=sensors&columns=" + SnapshotColumns +
            PrtgResourceGuardProbe.BuildObjidFilter(sensorObjids) +
            "&count=" + (sensorObjids.Count + SnapshotCountSentinel).ToString(CultureInfo.InvariantCulture);
        if (Encoding.UTF8.GetByteCount(url) > MaximumRelativeUrlBytes)
            throw new InvalidDataException("Snapshot relative URL exceeds the 4096-byte bound.");
        return url;
    }

    /// <summary>白名單目標（監看政策的 sensor 清單非空時再縮圈）∪ 資源守門目標，依 objid 排序。</summary>
    public static long[] SelectEffectiveTargets(IEnumerable<long> mappedTargets, PrtgMonitoringPolicy policy,
        IEnumerable<long> guardTargets)
    {
        var selected = mappedTargets.Distinct();
        if (policy.SensorIds.Count > 0)
            selected = selected.Where(policy.SensorIds.Contains);
        return selected.Union(guardTargets).Distinct().Order().ToArray();
    }
}
