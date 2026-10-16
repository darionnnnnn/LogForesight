using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

public sealed record PrtgValueReadinessSummary(
    DateOnly CompletedDay,
    int Population,
    IReadOnlyDictionary<PrtgValueReason, int> Counts,     // 每個 PrtgValueReason 都有鍵（含 0）
    IReadOnlyList<PrtgValueAssessmentRow> Rows);           // 依 HostId、SensorObjid 排序

public static class PrtgValueReadiness
{
    public const int WindowDays = 28;
    public const int RequiredDays = 28;
    /// <summary>四類：cpu、memory、disk、availability（用 PrtgSensorCategories 常數）</summary>
    public static readonly IReadOnlySet<string> Categories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PrtgSensorCategories.Cpu,
            PrtgSensorCategories.Memory,
            PrtgSensorCategories.Disk,
            PrtgSensorCategories.Availability
        };

    public static PrtgValueReadinessSummary Compute(PrtgValueAssessment assessment,
        IReadOnlyDictionary<long, long> deviceToHost, IReadOnlySet<long> evaluationHostIds,
        IReadOnlySet<long> policySensorIds, DateOnly completedDay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var mirror = assessment.SensorMirror()
            .Where(s => s.Category != null && Categories.Contains(s.Category.Trim()));

        var candidates = PrtgValueAssessment.Candidates(
            mirror, deviceToHost, evaluationHostIds, policySensorIds, category: null);

        var allRows = new List<PrtgValueAssessmentRow>(candidates.Count);
        foreach (var page in candidates.Chunk(PrtgValueAssessment.PageSize))
        {
            ct.ThrowIfCancellationRequested();
            var evaluated = assessment.AssessPage(page, completedDay, WindowDays, RequiredDays);
            allRows.AddRange(evaluated);
        }

        var sortedRows = allRows
            .OrderBy(r => r.Sensor.HostId)
            .ThenBy(r => r.Sensor.SensorObjid)
            .ToList();

        var counts = Enum.GetValues<PrtgValueReason>().ToDictionary(r => r, _ => 0);
        foreach (var row in allRows)
        {
            counts[row.Reason]++;
        }

        return new PrtgValueReadinessSummary(completedDay, candidates.Count, counts, sortedRows);
    }

    /// <summary>執行輸出用的一行文字</summary>
    public static string FormatLine(PrtgValueReadinessSummary summary)
    {
        if (summary.Population == 0)
        {
            return $"數值資料就緒度（{summary.CompletedDay:yyyy-MM-dd}）：範圍內無可評估 sensor（請確認鏡像狀態頁籤的對應）。";
        }

        var parts = Enum.GetValues<PrtgValueReason>()
            .Where(reason => summary.Counts.TryGetValue(reason, out var count) && count > 0)
            .Select(reason => $"{PrtgValueReasonText.Label(reason)} {summary.Counts[reason]}");

        return $"數值資料就緒度（{summary.CompletedDay:yyyy-MM-dd}）：母體 {summary.Population} 顆；{string.Join("、", parts)}。";
    }
}
