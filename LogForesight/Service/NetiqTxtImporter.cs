namespace LogForesight;

/// <summary>
/// txt 主機清單 → 主機清單資料（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 D）。
///
/// Txt 模式下，txt 檔是**主人**、主機清單資料是它的投影：每次同步都以 txt 的內容為準，
/// 包括「清單中已移除的主機要停止分析」。這是 txt 與 Web「同一時間只有一個主人」的
/// 具體落實——只做單向覆寫，不做雙向合併，才不會出現「兩邊都改過、誰贏不確定」。
///
/// 之所以每次執行都同步而不是只在 `--import-hosts` 時同步：分析紀錄以主機 PK 關聯，
/// 而 PK 只存在於主機清單資料裡；txt 新增一台卻沒同步，那台就沒有 PK 可寫。
/// </summary>
public static class NetiqTxtImporter
{
    public static TxtImportResult Sync(
        IHostStore hosts, string directory, IReadOnlyCollection<string> knownServers)
    {
        var result = new TxtImportResult { Directory = directory };

        if (!Directory.Exists(directory))
        {
            result.DirectoryUsable = false;
            result.Warnings.Add($"主機清單目錄不存在：{directory}");
            return result;
        }

        var files = Directory.GetFiles(directory, "*.txt");
        if (files.Length == 0)
        {
            result.DirectoryUsable = false;
            result.Warnings.Add($"主機清單目錄沒有任何 .txt 檔：{directory}");
            return result;
        }

        // 只對「本次真的讀到檔案」的 Sentinel 做移除判定——某台的 txt 暫時不見（誤刪、
        // 檔案伺服器沒掛上）時，不該把它轄下的主機整批停掉
        var syncedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var serverName = Path.GetFileNameWithoutExtension(file);

            // 名單為空＝設定尚未填 Servers，此時不擋（否則初次設定會卡死）；
            // 有名單就必須對得上，檔名打錯的後果是整檔主機靜默地不會被查
            if (knownServers.Count > 0 &&
                !knownServers.Contains(serverName, StringComparer.OrdinalIgnoreCase))
            {
                result.Warnings.Add(
                    $"{Path.GetFileName(file)}：檔名不對應任何已設定的 Sentinel，整檔略過" +
                    $"（可選：{string.Join("、", knownServers)}）");
                continue;
            }

            syncedServers.Add(serverName);
            ImportFile(hosts, file, serverName, seenIps, result);
        }

        DeactivateRemoved(hosts, syncedServers, seenIps, result);
        return result;
    }

    private static void ImportFile(
        IHostStore hosts, string file, string serverName,
        HashSet<string> seenIps, TxtImportResult result)
    {
        string[] lines;
        try
        {
            // ReadAllLines 會自動偵測並剝除 BOM（清單多半以記事本編輯，UTF-8 BOM 很常見）
            lines = File.ReadAllLines(file);
        }
        catch (IOException ex)
        {
            result.Warnings.Add($"{Path.GetFileName(file)}：讀取失敗（{ex.Message}），整檔略過");
            return;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var parsed = NetiqHostList.ParseLine(lines[i], i + 1);
            if (parsed == null) continue;

            if (parsed.Error != null)
            {
                result.Warnings.Add($"{Path.GetFileName(file)} 第 {parsed.LineNumber} 行：{parsed.Error}，略過");
                continue;
            }

            if (!seenIps.Add(parsed.IpAddress))
            {
                // 同一個 IP 出現在兩個 Sentinel 的清單裡＝設定錯誤（IP 全域唯一是識別前提）
                result.Warnings.Add(
                    $"{Path.GetFileName(file)} 第 {parsed.LineNumber} 行：IP {parsed.IpAddress} 已在其他清單中出現，略過");
                continue;
            }

            var existing = hosts.FindByName(parsed.IpAddress);

            hosts.Upsert(new WebHost
            {
                HostName = parsed.IpAddress,
                IpAddress = parsed.IpAddress,
                IpUpdatedAt = existing?.IpAddress == parsed.IpAddress ? existing.IpUpdatedAt : DateTime.Now,
                NetiqServer = serverName,
                // 角色描述留空時保留既有值：txt 沒寫不代表要清掉 Web 上填過的描述
                RoleDesc = parsed.RoleDesc.Length > 0 ? parsed.RoleDesc : existing?.RoleDesc ?? "",
                Source = NetiqHostList.NetiqSource,
                Active = true,
                // 群組與負責人是 Web 維護的欄位，txt 不帶這些資訊，原樣保留
                GroupIds = existing?.GroupIds ?? new List<long>(),
                OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
            });

            if (existing == null) result.Added++;
            else result.Updated++;
        }
    }

    /// <summary>
    /// 清單中已移除的主機：停用而不刪除——歷史紀錄與報告都要留著追溯，
    /// 只是不再納入每日分析（PLAN.md「移除 IP → 停止分析，既有 history 保留」）。
    /// </summary>
    private static void DeactivateRemoved(
        IHostStore hosts, HashSet<string> syncedServers,
        HashSet<string> seenIps, TxtImportResult result)
    {
        foreach (var host in NetiqHostList.Listed(hosts.GetAll()).ToList())
        {
            if (host.NetiqServer == null || !syncedServers.Contains(host.NetiqServer)) continue;
            if (host.IpAddress != null && seenIps.Contains(host.IpAddress)) continue;

            hosts.Upsert(new WebHost
            {
                HostName = host.HostName,
                IpAddress = host.IpAddress,
                IpUpdatedAt = host.IpUpdatedAt,
                NetiqServer = host.NetiqServer,
                RoleDesc = host.RoleDesc,
                Source = host.Source,
                Active = false,
                GroupIds = host.GroupIds,
                OwnerUserIds = host.OwnerUserIds
            });

            result.Deactivated++;
            result.Warnings.Add(
                $"{host.HostName}：已不在 {host.NetiqServer} 的清單中，停止分析（既有歷史保留）");
        }
    }
}

public class TxtImportResult
{
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// false = 目錄不存在或沒有任何 txt。**不是錯誤**（尚未導入 NetIQ 的環境就是這樣），
    /// 但機房分析本次必須跳過並明確提示——不能靜默當成「今天沒有主機要查」。
    /// </summary>
    public bool DirectoryUsable { get; set; } = true;

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }

    public List<string> Warnings { get; } = new();
}
