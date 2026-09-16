using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// 一趟執行內共享的 PRTG finding 登錄簿（docs/PRTG-SPEC.md §9）。
///
/// PRTG 規則評估算出「哪台主機當天命中哪些 finding」之後，這些 finding 必須併進該主機當日的
/// <c>lf_top_issues</c>。追加時機有兩條路：
/// 1. **就緒之後**才落地的主機日——由寫入路徑（<see cref="HostDayPostProcessor.AttachPrtgFindings"/>）
///    在紀錄剛寫完、問題案件掛接之前當場併入，PRTG finding 因此能一併進案件與處理狀態鏈；
/// 2. **就緒之前**已落地的主機日——由 PRTG 路徑在發佈後掃一次補追加。
///
/// 登錄簿按日期保存各日的評估結果（同日重複發佈以後者為準，不覆蓋其他日期）。
/// 「就緒」是給 AI 分析排程看的旗標：AI 排程要等當日 PRTG finding 都到齊才能判讀，
/// 否則 AI 看到的是缺了 PRTG 訊號的半份資料。發佈之後 AI 排程即可處理當日待補，
/// 不必等整趟取數結束。
///
/// 執行緒安全：NetIQ 路徑是多 Sentinel 平行迴圈，<see cref="For"/> 會被多執行緒同時呼叫。
/// 各日發佈後內容獨立保存且不可變，讀取取的是快照。
/// </summary>
public sealed class PrtgFindingsRegistry
{
    /// <summary>
    /// 「已就緒且沒有任何 finding」的共用實例，語意等同 PRTG 停用。
    /// 供未提供登錄簿的呼叫端（單元測試、不經 PRTG 的路徑）使用，
    /// 讓消費端不必到處寫 null 判斷——那種分支在正式路徑永遠不會執行。
    /// </summary>
    public static PrtgFindingsRegistry Empty { get; } = CreateReady();

    private readonly object _lock = new();
    private readonly Dictionary<DateTime, IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>>> _byDate = new();
    private readonly Dictionary<DateTime, IReadOnlyDictionary<long, IReadOnlySet<string>>> _suppressedPatternIdsByDate = new();
    private static readonly IReadOnlySet<string> NoPatternIds = new HashSet<string>();

    /// <summary>PRTG finding 是否已發佈（至少一天已發佈即為 true，維持現有呼叫端與測試語意）。</summary>
    public bool IsReady
    {
        get { lock (_lock) { return _byDate.Count > 0; } }
    }

    /// <summary>指定日期的 PRTG finding 是否已發佈。</summary>
    public bool IsPublished(DateTime date)
    {
        lock (_lock)
        {
            return _byDate.ContainsKey(date.Date);
        }
    }

    /// <summary>
    /// 發佈特定日期的規則評估結果並按日保存。同日重複發佈以後者為準，不覆蓋其他已發佈日期的結果。
    /// PRTG 停用、結構同步失敗、規則庫尚無 PRTG 規則等情況一律發佈空集合——
    /// **「算不出東西」與「還沒算完」必須分得出來**，不發佈的話 AI 排程會一路等到整趟取數結束，
    /// 等於這個機制沒做。
    /// </summary>
    /// <param name="day">
    /// 這批 finding 所屬的日期。**追加時一定要比對它**：兩條寫入路徑都在逐日迴圈裡呼叫，
    /// 回補多天缺漏日時每一天都會經過同一個登錄簿，必須依日期各自比對。
    /// 少了這道比對，站台停機三天後開跑會把昨天的 down 掛到三天份的紀錄上，
    /// 且 EventKey（`prtg:{code}:{objid}`）不含日期、去重完全生效，重跑也不會自癒。
    /// </param>
    /// <param name="findingsByHost">該日各主機命中的 finding 清單。</param>
    /// <param name="suppressedPatternIdsByHost">
    /// 各主機生效中的關聯抑制模式 Id（<c>SuppressionFilter.ToCorrelationPatternIdSet</c>），
    /// 兩條追加路徑做跨來源佐證（<see cref="PrtgCorroboration"/>）時取用。沒有 finding 的發佈傳空字典。
    /// </param>
    public void Publish(DateTime day, IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>> findingsByHost,
        IReadOnlyDictionary<long, IReadOnlySet<string>> suppressedPatternIdsByHost)
    {
        lock (_lock)
        {
            _byDate[day.Date] = findingsByHost;
            _suppressedPatternIdsByDate[day.Date] = suppressedPatternIdsByHost;
        }
    }

    /// <summary>
    /// 取得某主機**該日**發佈時生效中的關聯抑制模式 Id。該日尚未發佈、或發佈時沒有這台主機時回空集合。
    /// </summary>
    public IReadOnlySet<string> SuppressedPatternIdsFor(long hostId, DateTime date)
    {
        lock (_lock)
        {
            if (!_suppressedPatternIdsByDate.TryGetValue(date.Date, out var hostMap)) return NoPatternIds;
            return hostMap.TryGetValue(hostId, out var ids) ? ids : NoPatternIds;
        }
    }

    /// <summary>
    /// 取得某主機**該日**的 finding。該日尚未發佈、或該主機該日沒有 finding 時一律回空清單
    /// （呼叫端不必分辨：都是「現在沒有東西要追加」）。
    /// </summary>
    public IReadOnlyList<LogIssueSignature> For(long hostId, DateTime date)
    {
        lock (_lock)
        {
            if (!_byDate.TryGetValue(date.Date, out var hostMap)) return Array.Empty<LogIssueSignature>();
            return hostMap.TryGetValue(hostId, out var findings)
                ? findings
                : Array.Empty<LogIssueSignature>();
        }
    }

    /// <summary>
    /// 對同一個主機日的追加**行程內序列化**，並記住哪些主機日已經追加成功。
    /// 兩條追加路徑（PRTG 路徑的補追加、分析寫入路徑的就地追加）與本機／NetIQ 分析並行，
    /// 對同一個主機日可能同時進到「讀列 → 合併 → 寫回」；不序列化的話兩個交易各自讀到相同的
    /// 既有鍵、各插一列，`lf_top_issues` 長出重複、`ContentJson` 後寫的蓋掉先寫的。
    /// 同一趟執行只有一個行程，行程內鎖就夠——跨行程沒有第二個寫入者（具名 Mutex 已擋）。
    /// </summary>
    /// <returns>attach 的回傳值（true＝這次真的寫進資料庫）。</returns>
    public bool AttachExclusive(long hostId, DateTime date, Func<bool> attach)
    {
        var gate = _attachGates.GetOrAdd((hostId, date.Date), _ => new object());
        lock (gate)
        {
            var attached = attach();
            if (attached) _attached.TryAdd((hostId, date.Date), true);
            return attached;
        }
    }

    /// <summary>這個主機日是否已由任一條路徑追加成功（供另一條路徑判斷「資料庫已有，是我之前來過」）。</summary>
    public bool WasAttached(long hostId, DateTime date) => _attached.ContainsKey((hostId, date.Date));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long, DateTime), object> _attachGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long, DateTime), bool> _attached = new();

    private static PrtgFindingsRegistry CreateReady()
    {
        var registry = new PrtgFindingsRegistry();
        // 空集合對任何日期都回空清單，所以這裡的日期不影響行為
        registry.Publish(DateTime.Today, new Dictionary<long, IReadOnlyList<LogIssueSignature>>(),
            new Dictionary<long, IReadOnlySet<string>>());
        return registry;
    }
}
