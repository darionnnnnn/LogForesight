namespace LogForesight.Core;

/// <summary>
/// 套用一批 NetIQ 掃描勾選結果：落盤成主機異動（新增/更新/孤兒復活三態）。
/// 掃描精靈勾選送出後直接呼叫（docs/archive/HISTORY.md 定案 7:排入佇列已退役,
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
    /// <param name="os">
    /// 本次新增主機的作業系統（docs/LINUX-RULES.md §3）。與 <paramref name="groupByIp"/> 同一原則：
    /// **只套用在全新主機**，既有主機（含復活的孤兒）的 OS 一律不動——匯入不是隱性改設定，
    /// 而改 OS 等於改這台套哪個平台的規則面，靜默改掉會讓既有主機的偵測面整個換掉。
    /// 省略＝windows（既有行為）。
    /// </param>
    /// <param name="displayNameByIp">
    /// 探索掃描時已知的真實機器名（docs/NETIQ-API-REFERENCE.md §3.4：網段範圍掃描投影 sn 欄位，
    /// 匯入當下就有名字，不用等夜間批次的 TouchNetiq 才回填）。**只套用在全新主機**，
    /// 同 <paramref name="groupByIp"/>／<paramref name="os"/> 的一致原則：匯入不隱性改既有主機的
    /// 任何欄位。省略或某 IP 沒有對應名稱＝該欄位維持既有行為（新主機 DisplayName 為 null，
    /// 等夜間批次回填）。
    /// </param>
    /// <param name="tier">
    /// 本次新增主機的分級（回饋十九輪批次G，選填）。與 <paramref name="os"/> 同一原則：
    /// **只套用在全新主機**，既有主機（含復活的孤兒）的分級一律不動。省略＝standard（一般，既有行為）。
    /// </param>
    public static ApplyOutcome Apply(
        string serverName,
        IEnumerable<string> selectedIps,
        IHostStore hosts,
        ISentinelStore sentinels,
        IReadOnlyDictionary<string, long?>? groupByIp = null,
        string? os = null,
        IReadOnlyDictionary<string, string>? displayNameByIp = null,
        string? tier = null)
    {
        var newHostOs = WebHost.NormalizeOs(os) ?? WebHost.OsWindows;
        var newHostTier = WebHost.NormalizeTier(tier) ?? WebHost.TierStandard;
        var sentinel = sentinels.FindByName(serverName);
        var ips = selectedIps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 一次 MutateBatch 完成整批（回饋十七輪批次D）：原本逐台 FindByName+Upsert，
        // 各自都是一次整份 blob 讀改寫（見 JsonBlobCollection.Mutate）——勾 500 台就是上千次
        // 序列化往返，這是掃描精靈匯入慢的主因（掃描本身的網路耗時另計）。
        return hosts.MutateBatch(list =>
            ApplyToList(list, serverName, ips, sentinel?.SentinelId, newHostOs, groupByIp, displayNameByIp, newHostTier));
    }

    /// <summary>
    /// 純函式核心：對記憶體中的主機清單就地套用三態異動（新增／更新／孤兒復活）。
    /// 自 <see cref="Apply"/> 抽出以便一次 <see cref="IHostStore.MutateBatch{TResult}"/> 完成整批。
    ///
    /// **真 store 與測試替身共用同一份邏輯**：<c>FakeHostStore.MutateBatch</c> 直接對內部 list
    /// 呼叫這支方法，不是各自重寫一份三態判斷——這個專案已經踩過幾次「測試替身的邏輯與正式
    /// 實作漂移、測試綠燈卻蓋掉正式環境 bug」的教訓（如 Sentinel Upsert 曾漏欄位），
    /// 單點化直接堵住這個形狀。
    /// </summary>
    internal static ApplyOutcome ApplyToList(
        List<WebHost> hosts, string serverName, List<string> ips, long? sentinelId, string newHostOs,
        IReadOnlyDictionary<string, long?>? groupByIp, IReadOnlyDictionary<string, string>? displayNameByIp,
        string newHostTier = WebHost.TierStandard)
    {
        int added = 0, updated = 0, revived = 0;
        var nextId = hosts.Count == 0 ? 1 : hosts.Max(h => h.HostId) + 1;

        foreach (var ip in ips)
        {
            var existing = hosts.FirstOrDefault(h => string.Equals(h.HostName, ip, StringComparison.OrdinalIgnoreCase));

            if (existing?.OrphanedFromSentinel != null)
            {
                // 重疊復活：同 HostId 復活，歷史/群組/負責人零斷裂。
                // 群組不動——這仍是「既有主機」，只是查詢重疊觸發復活，不是新登錄
                existing.Active = true;
                existing.SentinelId = sentinelId;
                existing.NetiqServer = serverName;
                existing.OrphanedFromSentinel = null;
                revived++;
            }
            else if (existing != null)
            {
                // 既有使用中主機：群組不動
                existing.SentinelId = sentinelId;
                existing.NetiqServer = serverName;
                existing.Active = true;
                updated++;
            }
            else
            {
                var groupId = groupByIp != null && groupByIp.TryGetValue(ip, out var g) ? g : null;
                var displayName = displayNameByIp != null && displayNameByIp.TryGetValue(ip, out var name) ? name : null;
                hosts.Add(new WebHost
                {
                    HostId = nextId++,
                    HostName = ip,
                    IpAddress = ip,
                    IpUpdatedAt = DateTime.Now,
                    SentinelId = sentinelId,
                    NetiqServer = serverName,
                    Source = "netiq",
                    Os = newHostOs,
                    Tier = newHostTier,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
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
