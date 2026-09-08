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
/// 「就緒」是給 AI 分析排程看的旗標：AI 排程要等當日 PRTG finding 都到齊才能判讀，
/// 否則 AI 看到的是缺了 PRTG 訊號的半份資料。發佈之後 AI 排程即可處理當日待補，
/// 不必等整趟取數結束。
///
/// 執行緒安全：NetIQ 路徑是多 Sentinel 平行迴圈，<see cref="For"/> 會被多執行緒同時呼叫。
/// 發佈只有一次、發佈後內容不再變動，讀取取的是不可變快照。
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
    private IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>> _byHost =
        new Dictionary<long, IReadOnlyList<LogIssueSignature>>();
    private bool _ready;

    /// <summary>PRTG finding 是否已全部算完並發佈。</summary>
    public bool IsReady
    {
        get { lock (_lock) { return _ready; } }
    }

    /// <summary>已發佈的主機數（就緒前為 0）。供執行輸出與測試用。</summary>
    public int HostCount
    {
        get { lock (_lock) { return _byHost.Count; } }
    }

    /// <summary>
    /// 發佈規則評估結果並標記就緒。PRTG 停用、結構同步失敗、規則庫尚無 PRTG 規則等情況
    /// 一律發佈空集合——**「算不出東西」與「還沒算完」必須分得出來**，
    /// 不發佈的話 AI 排程會一路等到整趟取數結束，等於這個機制沒做。
    /// </summary>
    public void Publish(IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>> findingsByHost)
    {
        lock (_lock)
        {
            _byHost = findingsByHost;
            _ready = true;
        }
    }

    /// <summary>
    /// 取得某主機的 finding。未就緒或該主機沒有 finding 時回空清單
    /// （呼叫端不必分辨這兩者：兩種情況都是「現在沒有東西要追加」）。
    /// </summary>
    public IReadOnlyList<LogIssueSignature> For(long hostId)
    {
        lock (_lock)
        {
            if (!_ready) return Array.Empty<LogIssueSignature>();
            return _byHost.TryGetValue(hostId, out var findings)
                ? findings
                : Array.Empty<LogIssueSignature>();
        }
    }

    /// <summary>已發佈的全部主機 id（補追加階段用）。</summary>
    public IReadOnlyList<long> PublishedHostIds()
    {
        lock (_lock)
        {
            return _ready ? _byHost.Keys.ToList() : Array.Empty<long>();
        }
    }

    private static PrtgFindingsRegistry CreateReady()
    {
        var registry = new PrtgFindingsRegistry();
        registry.Publish(new Dictionary<long, IReadOnlyList<LogIssueSignature>>());
        return registry;
    }
}
