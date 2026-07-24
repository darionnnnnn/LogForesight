using NLog;

namespace LogForesight;

/// <summary>
/// Sentinel 被刪除時，停用其所屬的 NetIQ 主機（docs/SCALE-2000-PLAN.md §1.7；
/// docs/NETIQ-WEB-CONFIG-PLAN.md 定案 5：Sentinel 改 Web 維護後，判定依據從
/// 「appsettings.NetIq.Servers 名單」改為「Sentinel store 現存的 SentinelId 集合」）。
///
/// 批次啟動時、主機登記（Touch）之前跑一次。不停用的後果是這些主機永遠不會被任何一輪
/// 查詢帶到，變成「看起來在監控、實際沒人看」的靜默黑洞——停用讓狀態誠實，
/// 未回報卡與主機頁看得見（README「沒告警 ≠ 沒問題」在主機生命週期上的版本）。
///
/// 只碰 <see cref="WebHost.Source"/>='netiq'、使用中、且帶所屬 Sentinel 的主機：
/// local 來源不管、已停用不重複處理、待歸屬（SentinelId 為 null）不受任何 Sentinel 異動影響。
/// **人工停用**（Active=false 但 OrphanedFromSentinel 為 null）不碰——那是人已表態，不覆寫。
/// **Sentinel 停用（Active=false 但列仍在）不觸發本流程**——那是暫停輪巡的溫和選項
/// （<see cref="NetiqHostList.Pollable"/> 已處理），只有 Sentinel 整列被刪除才孤兒化。
/// </summary>
public static class NetiqOrphanSweeper
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public sealed class Result
    {
        public int OrphanedCount { get; init; }
        public List<string> OrphanedHosts { get; init; } = new();
        public bool SkippedForSafety { get; init; }
    }

    /// <summary>
    /// 掃描並停用孤兒主機。<paramref name="existingSentinelIds"/> 是 Sentinel store 目前
    /// 仍存在的 SentinelId 集合（不論 Active 與否——停用不算刪除，見類別註解）。
    /// </summary>
    public static Result Sweep(IHostStore hosts, IReadOnlyCollection<long> existingSentinelIds)
    {
        var existing = existingSentinelIds.ToHashSet();

        var netiqActive = hosts.GetAll()
            .Where(h => string.Equals(h.Source, "netiq", StringComparison.OrdinalIgnoreCase) &&
                        h.Active && h.MergedInto == null &&
                        h.SentinelId.HasValue)
            .ToList();

        var orphans = netiqActive
            .Where(h => !existing.Contains(h.SentinelId!.Value))
            .ToList();

        if (orphans.Count == 0)
            return new Result();

        // 安全欄杆：Sentinel store 整個為空但有使用中的 NetIQ 主機，疑似尚未完成種子匯入/
        // 資料損毀——不該演變成全站停用。跳過並記 ERROR，讓人去查設定。刪除單一 Sentinel
        // （store 非空）照常處理。
        if (existing.Count == 0)
        {
            Log.Error(
                "Sentinel 名單為空，但有 {Count} 台使用中的 NetIQ 主機——疑似尚未完成種子匯入或資料損毀，" +
                "未執行自動停用。若確實要移除所有 Sentinel，請先在 Web 手動停用這些主機。", orphans.Count);
            return new Result { SkippedForSafety = true };
        }

        var orphanedNames = new List<string>();
        foreach (var host in orphans)
        {
            host.Active = false;
            host.OrphanedFromSentinel = host.NetiqServer;
            hosts.Upsert(host);
            orphanedNames.Add(host.HostName);
        }

        Log.Warn("偵測到 Sentinel 已被刪除，停用所屬 NetIQ 主機 {Count} 台：{Hosts}",
            orphanedNames.Count, string.Join("、", orphanedNames));

        return new Result
        {
            OrphanedCount = orphanedNames.Count,
            OrphanedHosts = orphanedNames
        };
    }
}
