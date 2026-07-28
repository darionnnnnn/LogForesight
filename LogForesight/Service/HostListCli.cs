namespace LogForesight;

/// <summary>
/// 主機清單的查詢指令：`--host-list` 列出目前設定下實際會查詢的主機。
/// 主機清單的主人固定為 Web 主機頁維護（Txt 清單模式已退役，見 docs/NETIQ-WEB-CONFIG-PLAN.md 定案 12）。
/// </summary>
public static class HostListCli
{
    /// <summary>列出目前會被查詢的主機。日常確認「某台主機為什麼沒被檢查」的入口。</summary>
    public static int List(IHostStore hosts, ISentinelStore sentinels)
    {
        var provider = new StoreHostListProvider(hosts, sentinels);
        Console.WriteLine($"清單來源：{provider.Description}");
        Console.WriteLine();

        return PrintHostList(provider) ? 0 : 1;
    }

    private static bool PrintHostList(IHostListProvider provider)
    {
        var list = provider.GetHostList();

        if (!list.SourceUsable)
        {
            PrintWarnings(list.Warnings);
            Console.WriteLine("清單來源不可用，機房分析會跳過（本機分析不受影響）。");
            return false;
        }

        if (list.TotalHosts == 0)
        {
            Console.WriteLine("目前沒有任何主機會被查詢。");
        }
        else
        {
            Console.WriteLine($"共 {list.TotalHosts} 台主機會被查詢：");
            foreach (var (server, targets) in list.ByServer.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var osSummary = string.Join("、", targets
                    .GroupBy(t => t.Os, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{OsLabel(g.Key)} {g.Count()}"));
                Console.WriteLine($"  [{server}] {targets.Count} 台（{osSummary}）");
                foreach (var target in targets)
                {
                    var role = string.IsNullOrWhiteSpace(target.RoleDesc) ? "" : $"（{target.RoleDesc}）";
                    // OS 決定這台套哪個平台的規則面，選錯等於整台的偵測面配錯——列出來讓人核對
                    Console.WriteLine($"    #{target.HostId} [{OsLabel(target.Os)}] {target.IpAddress}{role}");
                }
            }
        }

        PrintWarnings(list.Warnings);
        return true;
    }

    private static string OsLabel(string os) =>
        string.Equals(os, WebHost.OsLinux, StringComparison.OrdinalIgnoreCase) ? "Linux" : "Windows";

    /// <summary>
    /// 警告以黃色顯示：每一條都代表某台主機不會被檢查，
    /// 和一般的進度輸出混在一起就等於沒有講。
    /// </summary>
    private static void PrintWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;

        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠ {warnings.Count} 項需要注意：");
        foreach (var warning in warnings) Console.WriteLine($"    - {warning}");
        Console.ForegroundColor = original;
    }
}
