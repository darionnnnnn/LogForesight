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

        var spans = coverage
            .Where(c => c.SensorObjid == sensorObjid && c.SourceGeneration == sourceGeneration &&
                        c.ResourceGeneration == resourceGeneration && c.Through > c.From &&
                        c.Through > from && c.From < through)
            .Select(c => (c.From, Through: c.Through < through ? c.Through : through))
            .OrderBy(c => c.From).ThenBy(c => c.Through).ToList();
        if (spans.Count == 0) return [];

        var contiguous = new List<(DateTimeOffset From, DateTimeOffset Through)>();
        foreach (var span in spans)
        {
            if (contiguous.Count == 0 || span.From > contiguous[^1].Through)
                contiguous.Add(span);
            else if (span.Through > contiguous[^1].Through)
                contiguous[^1] = (contiguous[^1].From, span.Through);
        }

        var events = changes
            .Where(c => c.SensorObjid == sensorObjid && c.SourceGeneration == sourceGeneration &&
                        c.ResourceGeneration == resourceGeneration && c.At < through)
            .GroupBy(c => c.At).OrderBy(g => g.Key)
            .Select(g => new { At = g.Key, Status = g.Select(c => c.Status.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? g.First().Status.Trim() : null })
            .ToList();
        var periods = new List<PrtgCoveredStatePeriod>();
        foreach (var span in contiguous)
        {
            // 只認同一段可信涵蓋內發生的前導轉換；較早事件即使仍顯示 Down，
            // 中間沒有完整查詢證明時也不能延續到這段。
            var leading = events.LastOrDefault(e => e.At >= span.From && e.At <= from);
            var inSpan = events.Where(e => e.At >= span.From && e.At < span.Through).ToList();
            string? status = leading?.Status;
            var entered = leading?.At ?? span.From;
            var cursor = span.From;
            foreach (var change in inSpan)
            {
                if (change.At > cursor && status != null)
                    periods.Add(new PrtgCoveredStatePeriod(cursor, change.At, status, entered));
                cursor = change.At;
                status = change.Status;
                entered = change.At;
            }
            if (cursor < span.Through && status != null)
                periods.Add(new PrtgCoveredStatePeriod(cursor, span.Through, status, entered));
        }
        return periods.Where(p => p.Through > from && p.From < through)
            .Select(p => p with { From = p.From < from ? from : p.From,
                                  Through = p.Through > through ? through : p.Through })
            .ToArray();
    }
}
