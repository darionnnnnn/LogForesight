namespace LogForesight;

/// <summary>
/// 套用一筆已排入的 NetIQ 匯入請求（§5.3 D-3）：把 <see cref="NetiqImportQueueEntry.SelectedIps"/>
/// 落盤成主機異動。邏輯與舊版 Web 端「立即套用」完全相同（新增/更新/孤兒復活三態），
/// 只是呼叫時機從「使用者按下套用當下」搬到「批次執行開頭」——搬地方不改邏輯，
/// 兩邊的行為才不會漂移。批次（Program.cs 的 --apply-netiq-imports／每次執行開頭自動處理）
/// 與（如果未來需要）Web 都呼叫同一份。
/// </summary>
public static class NetiqImportApplier
{
    public readonly record struct ApplyOutcome(int Added, int Updated, int Revived);

    public static ApplyOutcome Apply(NetiqImportQueueEntry entry, IHostStore hosts)
    {
        int added = 0, updated = 0, revived = 0;

        foreach (var ip in entry.SelectedIps.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = hosts.FindByName(ip);

            if (existing?.OrphanedFromSentinel != null)
            {
                // 重疊復活：同 HostId 復活，歷史/群組/負責人零斷裂
                existing.Active = true;
                existing.NetiqServer = entry.ServerName;
                existing.OrphanedFromSentinel = null;
                hosts.Upsert(existing);
                revived++;
            }
            else if (existing != null)
            {
                existing.NetiqServer = entry.ServerName;
                existing.Active = true;
                hosts.Upsert(existing);
                updated++;
            }
            else
            {
                hosts.Upsert(new WebHost
                {
                    HostName = ip,
                    IpAddress = ip,
                    IpUpdatedAt = DateTime.Now,
                    NetiqServer = entry.ServerName,
                    Source = "netiq",
                    Active = true,
                    GroupIds = new List<long>(),
                    OwnerUserIds = new List<long>()
                });
                added++;
            }
        }

        return new ApplyOutcome(added, updated, revived);
    }
}
