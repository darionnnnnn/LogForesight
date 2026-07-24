namespace LogForesight;

/// <summary>
/// 套用一批 NetIQ 掃描勾選結果：落盤成主機異動（新增/更新/孤兒復活三態）。
/// 掃描精靈勾選送出後直接呼叫（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 7:排入佇列已退役,
/// 改即時落盤——2000 台量級下這一步本身很輕量,真正重的規則檢查本來就要等下次批次)。
/// </summary>
public static class NetiqImportApplier
{
    public readonly record struct ApplyOutcome(int Added, int Updated, int Revived);

    /// <param name="serverName">要寫入的 Sentinel 名稱(顯示快照)。</param>
    /// <param name="selectedIps">使用者勾選的 IP(＝HostName)。</param>
    /// <param name="sentinels">
    /// 用來把 <paramref name="serverName"/> 解析成 <see cref="WebHost.SentinelId"/>——
    /// 識別鍵是 PK,字串只當顯示快照(定案 4)。名稱解析不到時(Sentinel 已被刪除)
    /// SentinelId 維持 null、NetiqServer 仍寫入原名稱,該主機會落在待歸屬佇列讓人工處理,
    /// 不阻斷整批匯入。
    /// </param>
    /// <param name="groupByIp">
    /// 新增 Sentinel 精靈的網段群組指派(定案 8)：IP → 要指派的主機群組 id(null＝跳過/未分組)。
    /// **只套用在全新主機**——復活的孤兒主機與既有使用中主機都是「既有主機」，
    /// 群組一律不動（決策原文：「既有主機的群組一律不動，匯入不是隱性改權限」）。
    /// 省略此參數＝維持 Phase 3 的行為(全部落在未分組安全預設)。
    /// </param>
    public static ApplyOutcome Apply(
        string serverName,
        IEnumerable<string> selectedIps,
        IHostStore hosts,
        ISentinelStore sentinels,
        IReadOnlyDictionary<string, long?>? groupByIp = null)
    {
        int added = 0, updated = 0, revived = 0;
        var sentinel = sentinels.FindByName(serverName);

        foreach (var ip in selectedIps.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = hosts.FindByName(ip);

            if (existing?.OrphanedFromSentinel != null)
            {
                // 重疊復活：同 HostId 復活，歷史/群組/負責人零斷裂。
                // 群組不動——這仍是「既有主機」，只是查詢重疊觸發復活，不是新登錄
                existing.Active = true;
                existing.SentinelId = sentinel?.SentinelId;
                existing.NetiqServer = serverName;
                existing.OrphanedFromSentinel = null;
                hosts.Upsert(existing);
                revived++;
            }
            else if (existing != null)
            {
                // 既有使用中主機：群組不動
                existing.SentinelId = sentinel?.SentinelId;
                existing.NetiqServer = serverName;
                existing.Active = true;
                hosts.Upsert(existing);
                updated++;
            }
            else
            {
                var groupId = groupByIp != null && groupByIp.TryGetValue(ip, out var g) ? g : null;
                hosts.Upsert(new WebHost
                {
                    HostName = ip,
                    IpAddress = ip,
                    IpUpdatedAt = DateTime.Now,
                    SentinelId = sentinel?.SentinelId,
                    NetiqServer = serverName,
                    Source = "netiq",
                    Active = true,
                    GroupIds = groupId.HasValue ? new List<long> { groupId.Value } : new List<long>(),
                    OwnerUserIds = new List<long>()
                });
                added++;
            }
        }

        return new ApplyOutcome(added, updated, revived);
    }
}
