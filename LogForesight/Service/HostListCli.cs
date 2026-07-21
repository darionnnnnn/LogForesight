namespace LogForesight;

/// <summary>
/// 主機清單的維護指令（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 D 的交接 SOP）：
/// `--import-hosts` 把 txt 匯入主機清單，`--host-list` 列出目前設定下實際會查詢的主機。
/// </summary>
public static class HostListCli
{
    /// <summary>
    /// txt → 主機清單的一次性匯入。**Web 模式下拒絕執行**：清單已交接給 Web 之後再匯入 txt，
    /// 會把 Web 上新增的主機當成「已從清單移除」而停用——這正是「同一時間只有一個主人」
    /// 要防的事故，所以擋在這裡而不是靠人記得。
    /// </summary>
    public static int Import(IHostStore hosts, NetIqSettings settings, string baseDirectory)
    {
        if (settings.UsesWebHostList)
        {
            Console.WriteLine("目前 NetIq.HostListSource = Web，主機清單由 Web 主機頁維護。");
            Console.WriteLine("此時匯入 txt 會把 Web 上新增的主機當成「已移除」而停用，因此拒絕執行。");
            Console.WriteLine("若確定要改回 txt 主導，請先把設定改成 \"Txt\" 再執行一次。");
            return 1;
        }

        var directory = ResolveDirectory(settings, baseDirectory);
        Console.WriteLine($"自 {directory} 匯入主機清單...");

        var knownServers = KnownServerNames(settings);
        var result = NetiqTxtImporter.Sync(hosts, directory, knownServers);

        if (!result.DirectoryUsable)
        {
            PrintWarnings(result.Warnings);
            Console.WriteLine("沒有可匯入的清單，未變更任何資料。");
            return 1;
        }

        Console.WriteLine($"  新增 {result.Added} 台、更新 {result.Updated} 台、停用 {result.Deactivated} 台。");
        PrintWarnings(result.Warnings);

        Console.WriteLine();
        PrintHostList(new StoreHostListProvider(hosts));

        Console.WriteLine();
        Console.WriteLine("匯入完成。切換為 Web 維護的步驟：核對上方筆數與 Web 主機頁一致後，");
        Console.WriteLine("把 appsettings.json 的 NetIq.HostListSource 改成 \"Web\"，再移除 txt 清單。");
        return 0;
    }

    /// <summary>
    /// 列出目前設定下實際會被查詢的主機。交接前後各跑一次即可確認「換了主人、清單沒變」，
    /// 也是日常確認「某台主機為什麼沒被檢查」的入口。
    /// </summary>
    public static int List(IHostStore hosts, NetIqSettings settings, string baseDirectory)
    {
        var provider = Create(hosts, settings, baseDirectory);
        Console.WriteLine($"清單來源：{provider.Description}");
        Console.WriteLine();

        return PrintHostList(provider) ? 0 : 1;
    }

    /// <summary>依設定建立清單來源（Strategy）。機房 pipeline 屆時取用同一個工廠</summary>
    public static IHostListProvider Create(IHostStore hosts, NetIqSettings settings, string baseDirectory)
    {
        return settings.UsesWebHostList
            ? new StoreHostListProvider(hosts)
            : new TxtHostListProvider(hosts, ResolveDirectory(settings, baseDirectory), KnownServerNames(settings));
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
                Console.WriteLine($"  [{server}] {targets.Count} 台");
                foreach (var target in targets)
                {
                    var role = string.IsNullOrWhiteSpace(target.RoleDesc) ? "" : $"（{target.RoleDesc}）";
                    Console.WriteLine($"    #{target.HostId} {target.IpAddress}{role}");
                }
            }
        }

        PrintWarnings(list.Warnings);
        return true;
    }

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

    private static string ResolveDirectory(NetIqSettings settings, string baseDirectory)
    {
        var configured = string.IsNullOrWhiteSpace(settings.HostListDirectory)
            ? "hosts"
            : settings.HostListDirectory;

        return Path.IsPathRooted(configured) ? configured : Path.Combine(baseDirectory, configured);
    }

    private static List<string> KnownServerNames(NetIqSettings settings) =>
        settings.Servers
            .Select(s => s.Name?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .ToList();
}
