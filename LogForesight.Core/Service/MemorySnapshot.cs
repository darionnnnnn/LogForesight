using System.Diagnostics;
using System.Runtime;

namespace LogForesight.Core.Service;

/// <summary>
/// 單一快取的統計快照（健康頁記憶體詳情用）。
/// <c>Elements</c> 是各條目內含的元素總數（例如問題排行的 Items 筆數），用來看「條目數很少但每筆很大」的情形；
/// <c>OversizeRejected</c> 是因單筆過大而拒絕入快取的累計次數。
/// </summary>
public sealed record CacheStatisticsSnapshot(
    string Name, int Entries, long Elements, long Hits, long Misses, long Evictions, long OversizeRejected);

/// <summary>
/// 行程記憶體快照的**單一讀取點**：作業系統層（working set／private bytes）、.NET GC 層
/// （託管堆／committed／碎片／LOH）、GC 設定（Server GC、保守模式、堆上限）、SQLite 原生堆、
/// NLog 規則數與各快取統計。可空欄位表示「第一次 GC 前或讀不到」。
///
/// 絕不能讓健康頁因記憶體讀取失敗而失敗：外部委派（SQLite／NLog／快取）擲任何例外一律當 null／空。
/// </summary>
public sealed record MemorySnapshot
{
    public DateTime CapturedAt { get; init; }

    // ── 作業系統層（Process）──
    public long? WorkingSetBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? PeakPagedBytes { get; init; }

    // ── .NET GC 層 ──
    public long? ManagedHeapBytes { get; init; }
    public long? GcHeapSizeBytes { get; init; }
    public long? GcCommittedBytes { get; init; }
    public long? GcFragmentedBytes { get; init; }
    public long? LohSizeBytes { get; init; }
    public long? HardLimitBytes { get; init; }
    public int Gen2Collections { get; init; }

    // ── GC 設定 ──
    public bool IsServerGc { get; init; }
    public long? GcConserveMemory { get; init; }
    public long? GcHeapHardLimitPercent { get; init; }

    // ── 呼叫端提供 ──
    public long? SqliteHeapBytes { get; init; }
    public long? SqliteHeapHighwaterBytes { get; init; }
    public int? NlogRuleCount { get; init; }
    public IReadOnlyList<CacheStatisticsSnapshot> Caches { get; init; } = Array.Empty<CacheStatisticsSnapshot>();

    /// <summary>託管堆占可用記憶體上限的比例；上限未知（null 或 0）時為 null。</summary>
    public double? HardLimitUsageRatio =>
        ManagedHeapBytes is { } used && HardLimitBytes is { } limit && limit > 0
            ? (double)used / limit
            : null;

    /// <summary>擷取目前行程的記憶體快照。</summary>
    /// <param name="sqliteHeap">SQLite 原生堆（目前用量、歷史高水位）</param>
    /// <param name="nlogRuleCount">NLog 目前規則數（規則殘留累積的偵測點）</param>
    /// <param name="caches">各快取統計</param>
    public static MemorySnapshot Capture(
        Func<(long? Used, long? Highwater)>? sqliteHeap = null,
        Func<int?>? nlogRuleCount = null,
        Func<IEnumerable<CacheStatisticsSnapshot>>? caches = null)
    {
        long? workingSet = null, peakWorkingSet = null, privateBytes = null, peakPaged = null;
        try
        {
            using var proc = Process.GetCurrentProcess();
            workingSet = proc.WorkingSet64;
            peakWorkingSet = proc.PeakWorkingSet64;
            privateBytes = proc.PrivateMemorySize64;
            peakPaged = proc.PeakPagedMemorySize64;
        }
        catch { /* 部分平台讀不到；快照不得讓呼叫端失敗 */ }

        long? managed = null;
        try { managed = GC.GetTotalMemory(false); } catch { }

        long? heapSize = null, committed = null, fragmented = null, loh = null, hardLimit = null;
        try
        {
            var info = GC.GetGCMemoryInfo();
            heapSize = NullIfZero(info.HeapSizeBytes);
            committed = NullIfZero(info.TotalCommittedBytes);
            fragmented = NullIfZero(info.FragmentedBytes);
            hardLimit = NullIfZero(info.TotalAvailableMemoryBytes);
            // GenerationInfo：0～2 為各代，3 為 LOH（第一次 GC 前可能不足 4 筆）
            if (info.GenerationInfo.Length > 3) loh = NullIfZero(info.GenerationInfo[3].SizeAfterBytes);
        }
        catch { }

        long? conserve = null, hardPercent = null;
        try
        {
            var vars = GC.GetConfigurationVariables();
            conserve = ReadLong(vars, "GCConserveMem");
            hardPercent = ReadPositiveLong(vars, "GCHeapHardLimitPercent");
        }
        catch { }

        long? sqliteUsed = null, sqliteHigh = null;
        if (sqliteHeap != null)
        {
            try { (sqliteUsed, sqliteHigh) = sqliteHeap(); } catch { }
        }

        int? rules = null;
        if (nlogRuleCount != null)
        {
            try { rules = nlogRuleCount(); } catch { }
        }

        IReadOnlyList<CacheStatisticsSnapshot> cacheList = Array.Empty<CacheStatisticsSnapshot>();
        if (caches != null)
        {
            try { cacheList = caches().ToList(); } catch { }
        }

        return new MemorySnapshot
        {
            CapturedAt = DateTime.Now,
            WorkingSetBytes = workingSet,
            PeakWorkingSetBytes = peakWorkingSet,
            PrivateBytes = privateBytes,
            PeakPagedBytes = peakPaged,
            ManagedHeapBytes = managed,
            GcHeapSizeBytes = heapSize,
            GcCommittedBytes = committed,
            GcFragmentedBytes = fragmented,
            LohSizeBytes = loh,
            HardLimitBytes = hardLimit,
            Gen2Collections = GC.CollectionCount(2),
            IsServerGc = GCSettings.IsServerGC,
            GcConserveMemory = conserve,
            GcHeapHardLimitPercent = hardPercent,
            SqliteHeapBytes = sqliteUsed,
            SqliteHeapHighwaterBytes = sqliteHigh,
            NlogRuleCount = rules,
            Caches = cacheList
        };
    }

    private static long? NullIfZero(long v) => v == 0 ? null : v;

    /// <summary>
    /// 「沒設定就是 0」的 GC 設定：runtime 未設 HeapHardLimitPercent 時 <c>GC.GetConfigurationVariables()</c>
    /// 仍回這個鍵、值為 0（實測 .NET 8），不是缺鍵；0 要當 null，否則畫面會把實體記憶體標成「硬上限 0%」。
    /// </summary>
    internal static long? ReadPositiveLong(IReadOnlyDictionary<string, object> vars, string key) =>
        ReadLong(vars, key) is { } value && value > 0 ? value : null;

    internal static long? ReadLong(IReadOnlyDictionary<string, object> vars, string key)
    {
        if (!vars.TryGetValue(key, out var raw) || raw == null) return null;
        try { return Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}
