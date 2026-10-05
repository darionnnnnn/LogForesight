namespace LogForesight.Core.Service;

/// <summary>來源已證明逐 sensor 查詢完整的時間區間；端點摘要與裝置查詢收斂均不可替代此證據。</summary>
public sealed record PrtgSensorCoverage(long SensorObjid, DateTimeOffset From, DateTimeOffset Through,
    string SourceGeneration, string ResourceGeneration);

/// <summary>帶來源與資源世代的原始狀態轉換；同時刻相矛盾的轉換視為資料不確定。</summary>
public sealed record PrtgTimedState(long SensorObjid, DateTimeOffset At, string Status,
    string SourceGeneration, string ResourceGeneration);

/// <summary>可支持的單一連續狀態區間。Through 是右開端；不能跨越證據缺口。</summary>
public sealed record PrtgCoveredStatePeriod(DateTimeOffset From, DateTimeOffset Through, string Status,
    DateTimeOffset EnteredAt);

/// <summary>
/// 把逐 sensor 完整查詢的覆蓋與原始轉換交集化。只產生具世代證據的區間；
/// 缺口、身分不符、事件相衝或沒有前導狀態都回空／切斷，不用目前鏡像補歷史。
/// </summary>
public static class PrtgCoveredStateTimeline
{
    public static IReadOnlyList<PrtgCoveredStatePeriod> Build(
        long sensorObjid, string? sourceGeneration, string? resourceGeneration,
        IReadOnlyList<PrtgSensorCoverage> coverage, IReadOnlyList<PrtgTimedState> changes,
        DateTimeOffset from, DateTimeOffset through)
    {
        if (through <= from) throw new ArgumentException("結束時間必須晚於開始時間。", nameof(through));
        if (sensorObjid <= 0 || string.IsNullOrWhiteSpace(sourceGeneration) ||
            string.IsNullOrWhiteSpace(resourceGeneration)) return [];

        var rawSpans = coverage
            .Where(c => c.SensorObjid == sensorObjid && c.SourceGeneration == sourceGeneration &&
                        c.ResourceGeneration == resourceGeneration && c.Through > c.From)
            .OrderBy(c => c.From).ThenBy(c => c.Through).ToList();
        if (rawSpans.Count == 0) return [];

        var contiguous = new List<(DateTimeOffset From, DateTimeOffset Through)>();
        foreach (var span in rawSpans)
        {
            if (contiguous.Count == 0 || span.From > contiguous[^1].Through)
                contiguous.Add((span.From, span.Through));
            else if (span.Through > contiguous[^1].Through)
                contiguous[^1] = (contiguous[^1].From, span.Through);
        }

        var activeChains = contiguous.Where(c => c.Through > from && c.From < through).ToList();
        if (activeChains.Count == 0) return [];

        var periods = new List<PrtgCoveredStatePeriod>();
        foreach (var chain in activeChains)
        {
            var spanEnd = chain.Through < through ? chain.Through : through;
            var events = changes
                .Where(c => c.SensorObjid == sensorObjid && c.SourceGeneration == sourceGeneration &&
                            c.ResourceGeneration == resourceGeneration && c.At >= chain.From && c.At < spanEnd)
                .GroupBy(c => c.At).OrderBy(g => g.Key)
                .Select(g => new { At = g.Key, Status = g.Select(c => c.Status.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? g.First().Status.Trim() : null })
                .ToList();

            string? status = null;
            DateTimeOffset entered = chain.From;
            DateTimeOffset cursor = chain.From;
            foreach (var change in events)
            {
                if (change.At > cursor && status != null)
                    periods.Add(new PrtgCoveredStatePeriod(cursor, change.At, status, entered));
                cursor = change.At;
                var sameFault = status != null && change.Status != null &&
                    (string.Equals(status, change.Status, StringComparison.OrdinalIgnoreCase) ||
                     PrtgSensorStatuses.IsDown(status) && PrtgSensorStatuses.IsDown(change.Status));
                status = change.Status;
                if (!sameFault) entered = change.At;
            }
            if (cursor < spanEnd && status != null)
                periods.Add(new PrtgCoveredStatePeriod(cursor, spanEnd, status, entered));
        }

        return periods.Where(p => p.Through > from && p.From < through)
            .Select(p => p with { From = p.From < from ? from : p.From,
                                  Through = p.Through > through ? through : p.Through })
            .ToArray();
    }
}
