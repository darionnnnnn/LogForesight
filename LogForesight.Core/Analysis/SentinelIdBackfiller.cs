namespace LogForesight;

/// <summary>
/// 一次性遷移：把既有主機的 <see cref="WebHost.NetiqServer"/> 名稱字串回填成
/// <see cref="WebHost.SentinelId"/>（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 4）。
///
/// **冪等**：只處理 SentinelId 仍是 null 但 NetiqServer 有值的主機，回填完成後自然變成
/// no-op（一次查詢，無異動），可以放心每次啟動都跑，不需要額外的「是否已執行過」旗標。
/// 名稱在 Sentinel store 對不到時維持 null（＝待歸屬），落入既有的
/// <see cref="NetiqHostList.PendingAssignment"/> 佇列讓人工處理，不是錯誤。
/// </summary>
public static class SentinelIdBackfiller
{
    public sealed class Result
    {
        public int BackfilledCount { get; init; }
        public int UnresolvedCount { get; init; }
    }

    public static Result Run(IHostStore hosts, ISentinelStore sentinels)
    {
        var byName = sentinels.GetAll().ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var candidates = hosts.GetAll()
            .Where(h => h.SentinelId == null && !string.IsNullOrWhiteSpace(h.NetiqServer))
            .ToList();

        int backfilled = 0, unresolved = 0;
        foreach (var host in candidates)
        {
            if (byName.TryGetValue(host.NetiqServer!.Trim(), out var sentinel))
            {
                host.SentinelId = sentinel.SentinelId;
                host.NetiqServer = sentinel.Name;   // 正規化大小寫，與 Sentinel 現存名稱一致
                hosts.Upsert(host);
                backfilled++;
            }
            else
            {
                unresolved++;
            }
        }

        return new Result { BackfilledCount = backfilled, UnresolvedCount = unresolved };
    }
}
