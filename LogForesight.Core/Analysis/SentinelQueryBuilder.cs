namespace LogForesight;

/// <summary>
/// 規則表→Lucene filter 的純函數產生器（docs/NETIQ-API-PLAN.md §4「watchlist→Lucene 產生器」）。
///
/// 只有 Windows 分支：Linux 那台 Sentinel 尚未接入 LogForesight，沒有任何真實 probe 樣本可依據
/// （docs/LINUX-RULES-PLAN.md P3 閘門）。強行寫一份沒有實據的 Linux 產生器只是把猜測換個地方藏，
/// 等 Linux Sentinel 接入、對它跑過 probe 後再補這個分支，屆時介面照這份的形狀加，不是新設計。
/// </summary>
public static class SentinelQueryBuilder
{
    /// <summary>
    /// generic 錯誤等級收集的 sev 下限（候選值，見 <see cref="SentinelFieldMap.MapEntryType"/> 同一份
    /// 未實證說明）。第三輪 probe 量級實測：單主機 sev&gt;=3 全頻道 3872/日、鎖 System+Application 後
    /// 155/日，量級可行；sev&gt;=2 的量尚未實測，先取 2（覆蓋面較保守的一側，寧可稍多不要漏），
    /// 試點時依實測耗時與 Warning/Error 門檻確認一併調整。
    /// </summary>
    public const int GenericErrorSeverityMin = 2;

    /// <summary>
    /// 建立 Q1（主聚合查詢）的 filter：`(IP 批次) AND (規則事件ID聯集 OR generic 錯誤收集)`。
    /// </summary>
    /// <param name="hostIps">本批要查詢的主機 IP（<see cref="SentinelFieldMap.HostIp"/> 篩選對象），
    /// 批次大小上限由呼叫端控制（第一、二輪 probe 實證 IP 篩選 10/50/100 個子句皆被接受）。</param>
    /// <param name="rules">目前啟用的規則表（呼叫端傳入 <c>KnownIssueCatalog.Rules</c> 或測試用清單，
    /// 本函數不讀任何全域可變狀態，維持純函數）。</param>
    /// <exception cref="ArgumentException"><paramref name="hostIps"/> 為空——沒有主機就不該建查詢，
    /// 呼叫端應該在更早的地方擋下（清單空＝零副作用，見 pipeline 既定原則），這裡用例外守住
    /// 「不小心傳空清單會建出一個只有 AND 右半邊、等於全站掃描的危險 filter」的風險。</exception>
    public static string BuildWindowsFilter(IReadOnlyList<string> hostIps, IEnumerable<KnownIssueRule> rules)
    {
        if (hostIps.Count == 0)
        {
            throw new ArgumentException("主機 IP 清單不可為空——會產生出等同全站掃描的 filter。", nameof(hostIps));
        }

        var ipClause = BuildIpClause(hostIps);
        var contentClause = BuildContentClause(rules);
        return $"{ipClause} AND {contentClause}";
    }

    /// <summary>單獨曝露 IP 批次子句，供 Q3（風險主機原始 log，單台小查詢）等其他查詢重用同一組寫法。</summary>
    public static string BuildIpClause(IReadOnlyList<string> hostIps) =>
        $"({string.Join(" OR ", hostIps.Select(ip => $"{SentinelFieldMap.HostIp}:{ip}"))})";

    /// <summary>
    /// 事件內容子句：規則事件 ID 聯集 OR generic 錯誤收集。兩者是 OR，任一命中即收——
    /// 規則命中的事件走 <see cref="KnownIssueCatalog.Classify"/> 精準分類，generic 分支撈的是
    /// 規則表還沒覆蓋到的 System/Application 錯誤/警告事件（對齊本機 <c>ErrorWarningOnly</c>
    /// 頻道的既有偵測面，也是 <see cref="KnownIssueRule.MatchAllEventIds"/> 類規則
    /// 【WHEA-Logger／Resource-Exhaustion／VSS，皆為 System 來源】事件的收集路徑——
    /// 這類規則沒有具體 EventId 可下推 Lucene，改由 generic 分支撈進來後本地 Classify 精準比對）。
    /// </summary>
    private static string BuildContentClause(IEnumerable<KnownIssueRule> rules)
    {
        var eventIds = WindowsRuleEventIds(rules);
        var genericClause =
            $"(({SentinelFieldMap.LogName}:System OR {SentinelFieldMap.LogName}:Application) " +
            $"AND {SentinelFieldMap.Severity}:[{GenericErrorSeverityMin} TO 5])";

        if (eventIds.Count == 0)
        {
            return genericClause;
        }

        var idClause = $"{SentinelFieldMap.EventId}:({string.Join(" OR ", eventIds)})";
        return $"({idClause} OR {genericClause})";
    }

    /// <summary>
    /// 啟用中的 Windows 規則、且非 <see cref="KnownIssueRule.MatchAllEventIds"/> 的 EventIds 聯集，
    /// 升冪排序去重——與 <see cref="KnownIssueCatalog"/> 既有的 watchlist 推導同一套篩選條件
    /// （<c>Platform=="windows"</c>、非 MatchAllEventIds），只是這裡聯集的是「全部規則」而非
    /// 單一頻道，因為 Sentinel Q1 是一次查詢涵蓋所有頻道，不像本機逐頻道分開讀取。
    /// </summary>
    internal static List<int> WindowsRuleEventIds(IEnumerable<KnownIssueRule> rules) =>
        rules
            .Where(r => r.Enabled && r.Platform == "windows" && !r.MatchAllEventIds)
            .SelectMany(r => r.EventIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
}
