using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// Opaque request-scoped state for a flattened completed-day preview. Candidate denominators are
/// captured once for the whole date range; only candidate rows intersecting the current page are
/// loaded later. This object is neither serializable nor valid across HTTP requests.
/// </summary>
public sealed class PrtgDiskAssessmentRangeOperation
{
    private readonly long[] _prefixCounts;
    private readonly int[] _dailyCounts;
    private int _completed;

    internal PrtgDiskAssessmentRangeOperation(object owner, DateOnly fromDate, DateOnly throughDate,
        KnownIssueRule? rule, PrtgDiskDecisionMode mode, PrtgHostSnapshot hostSnapshot, long[] activeHostIds,
        string settingsRevision, string settingsFingerprint, string[] capturedWhitelist, string ruleFingerprint,
        int[] dailyCounts, long hostMapDataRevision, long catalogueDataRevision,
        PrtgDiskMetadataSnapshot metadataSnapshot)
    {
        Owner = owner;
        FromDate = fromDate;
        ThroughDate = throughDate;
        var ruleJson = rule is null ? null : System.Text.Json.JsonSerializer.Serialize(rule);
        Rule = ruleJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(ruleJson);
        Mode = mode;
        HostSnapshot = hostSnapshot;
        ActiveHostIds = activeHostIds.ToArray();
        HostVersion = hostSnapshot.Version;
        SettingsRevision = settingsRevision;
        SettingsFingerprint = settingsFingerprint;
        CapturedWhitelist = capturedWhitelist.ToArray();
        RuleFingerprint = ruleFingerprint;
        _dailyCounts = dailyCounts.ToArray();
        if (_dailyCounts.Length != throughDate.DayNumber - fromDate.DayNumber + 1 ||
            _dailyCounts.Length is < 1 or > 730 ||
            _dailyCounts.Any(count => count is < 0 or > PrtgDiskAssessmentService.MaximumCandidateSnapshotSize))
            throw new InvalidOperationException("PRTG 磁碟範圍候選日計數不符合界線。");
        _prefixCounts = new long[_dailyCounts.Length + 1];
        for (var index = 0; index < _dailyCounts.Length; index++)
            _prefixCounts[index + 1] = checked(_prefixCounts[index] + _dailyCounts[index]);
        var maximumTotal = checked((long)PrtgDiskAssessmentService.MaximumCandidateSnapshotSize * 730);
        if (_prefixCounts[^1] > maximumTotal || _prefixCounts[^1] > int.MaxValue)
            throw new InvalidOperationException("PRTG 磁碟範圍候選總數超過可表示上限。");
        HostMapDataRevision = hostMapDataRevision;
        CatalogueDataRevision = catalogueDataRevision;
        MetadataSnapshot = metadataSnapshot;
    }

    /// <summary>Flattened date × candidate count; each date is capped at 15,000.</summary>
    public int CandidateCount => checked((int)_prefixCounts[^1]);

    internal object Owner { get; }
    internal DateOnly FromDate { get; }
    internal DateOnly ThroughDate { get; }
    internal KnownIssueRule? Rule { get; }
    internal PrtgDiskDecisionMode Mode { get; }
    internal PrtgHostSnapshot HostSnapshot { get; }
    internal long[] ActiveHostIds { get; }
    internal long HostVersion { get; }
    internal string SettingsRevision { get; }
    internal string SettingsFingerprint { get; }
    internal string[] CapturedWhitelist { get; }
    internal string RuleFingerprint { get; }
    internal long HostMapDataRevision { get; }
    internal long CatalogueDataRevision { get; }
    internal PrtgDiskMetadataSnapshot MetadataSnapshot { get; }
    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal int GetDailyCount(int dayIndex) => _dailyCounts[dayIndex];

    internal IReadOnlyList<EfPrtgStore.RangeReadinessCandidateSlice> CreateSlices(long offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > PrtgDiskAssessmentService.MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(offset), "PRTG 磁碟範圍頁面位置或長度無效。");
        if (offset >= _prefixCounts[^1]) return Array.Empty<EfPrtgStore.RangeReadinessCandidateSlice>();

        var remaining = Math.Min((long)limit, _prefixCounts[^1] - offset);
        var dayIndex = FindDayIndex(offset);
        var withinDayOffset = checked((int)(offset - _prefixCounts[dayIndex]));
        var slices = new List<EfPrtgStore.RangeReadinessCandidateSlice>();
        while (remaining > 0 && dayIndex < _dailyCounts.Length)
        {
            var available = _dailyCounts[dayIndex] - withinDayOffset;
            if (available > 0)
            {
                var take = checked((int)Math.Min(remaining, available));
                slices.Add(new EfPrtgStore.RangeReadinessCandidateSlice(
                    FromDate.AddDays(dayIndex), _dailyCounts[dayIndex], withinDayOffset, take));
                remaining -= take;
            }
            dayIndex++;
            withinDayOffset = 0;
        }
        if (remaining != 0 || slices.Sum(slice => slice.Take) != Math.Min((long)limit, _prefixCounts[^1] - offset))
            throw new InvalidOperationException("PRTG 磁碟範圍頁面切片與私有前綴計數不一致。");
        return slices.AsReadOnly();
    }

    internal void MarkCompleted()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            throw new InvalidOperationException("PRTG 磁碟範圍評估作業已完成。");
    }

    private int FindDayIndex(long offset)
    {
        var low = 0;
        var high = _dailyCounts.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_prefixCounts[middle + 1] <= offset) low = middle + 1;
            else high = middle;
        }
        if (low >= _dailyCounts.Length || _dailyCounts[low] == 0)
            throw new InvalidOperationException("PRTG 磁碟範圍頁面前綴未定位到候選日期。");
        return low;
    }
}

/// <summary>Core-only result for one flattened page; Web maps it only after the final operation fence.</summary>
public sealed record PrtgDiskAssessmentRangePage(int CandidateCount, long Offset,
    IReadOnlyList<PrtgDiskAssessmentRangeRow> Rows);

public sealed record PrtgDiskAssessmentRangeRow(DateOnly CompletedDate, PrtgDiskAssessmentRow Assessment);
