namespace LogForesight.Web.Services.Import;

/// <summary>
/// 負責人指派匯入（owners.csv）：host_name,ip_address,owner_account。
///
/// 與其他匯入不同的兩點：
/// - **一台主機多列＝多位負責人**（其他匯入是一列一實體）。因此預覽以「主機」為單位彙總，
///   而不是逐列——一列一列看「新增負責人 X」無法表達「這台的負責人整組換成 A、B」的取代語意。
/// - **帳號不存在時自動建立**（User 角色、無群組）。與 hosts.csv 的「擋下」刻意不同：
///   兩千台情境手動先建幾百個帳號不現實，且帳號真偽在 LDAP 登入時自然驗證
///   （見 docs/SCALE-2000-PLAN.md §2）。主機則**不**自動建立——主機的建立途徑是
///   批次 Touch／NetIQ 匯入／hosts.csv，負責人檔不該成為第四條。
/// </summary>
public class OwnerCsvImporter : ICsvImporter
{
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;

    public OwnerCsvImporter(IHostStore hosts, IUserStore users)
    {
        _hosts = hosts;
        _users = users;
    }

    public ImportKind Kind => ImportKind.Owners;

    public string[] RequiredHeaders => new[] { "owner_account" };

    public string[] KnownHeaders => new[] { "host_name", "ip_address", "owner_account" };

    public string BuildTemplate() =>
        "host_name,ip_address,owner_account\r\n" +
        "SRV-OO-WEB01,10.1.2.11,DOMAIN\\wangxm\r\n" +
        "SRV-OO-WEB01,10.1.2.11,DOMAIN\\lidh\r\n" +
        ",10.2.3.21,DOMAIN\\chenyt\r\n";

    public ImportPlan BuildPlan(CsvTable table, string fileName)
    {
        var plan = new ImportPlan { Kind = Kind, FileName = fileName };
        var allHosts = _hosts.GetAll();

        // 逐列解析成 (主機, 帳號)，解析階段的錯誤逐列回報（要指得出哪一行）
        var resolved = new List<(int Line, WebHost Host, string Account)>();
        foreach (var row in table.Rows)
        {
            var (host, account, error) = ResolveRow(row, allHosts);
            if (error != null)
            {
                plan.Rows.Add(new ImportRowPlan
                {
                    LineNumber = row.LineNumber,
                    Key = DescribeKey(row),
                    Action = ImportRowAction.Error,
                    Error = error
                });
                continue;
            }

            resolved.Add((row.LineNumber, host!, account!));
        }

        // 檔案中出現的帳號，不存在的標記為將自動建立
        var pendingNewUsers = resolved
            .Select(r => r.Account)
            .Where(a => _users.FindByAccount(a) == null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        plan.NewUsers.AddRange(pendingNewUsers);

        // 以主機為單位彙總：檔案中出現的主機，其負責人整組取代為檔案內容（取代語意）
        foreach (var group in resolved.GroupBy(r => r.Host.HostId))
        {
            var host = group.First().Host;
            var targetAccounts = group
                .Select(r => r.Account)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var beforeNames = host.OwnerUserIds
                .Select(id => _users.Get(id)?.Account ?? $"(已刪除:{id})")
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rowPlan = new ImportRowPlan
            {
                LineNumber = group.Min(r => r.Line),
                Key = host.HostName
            };

            if (beforeNames.SequenceEqual(targetAccounts, StringComparer.OrdinalIgnoreCase))
            {
                rowPlan.Action = ImportRowAction.Unchanged;
                rowPlan.Description = "負責人與現有相同";
            }
            else
            {
                rowPlan.Action = ImportRowAction.Update;
                rowPlan.Description = $"負責人：{Fmt(beforeNames)} → {Fmt(targetAccounts)}";
                rowPlan.Changes.Add(new ImportFieldChange
                {
                    Field = "負責人", Before = Fmt(beforeNames), After = Fmt(targetAccounts)
                });
            }

            plan.Rows.Add(rowPlan);
        }

        if (resolved.Count > 0)
        {
            // 帳號自動建立的明細走 NewUsers（預覽以資訊框呈現）；這裡只提權限的隱形陷阱
            plan.Warnings.Add(
                "負責人不會自動取得主機的檢視權限——請確認負責人所屬的部門群組已被授權對應的主機群組，" +
                "否則會出現「這台出事、負責人卻看不到」的狀況。");
        }

        return plan;
    }

    public ImportResult Apply(ImportPlan plan, CsvTable table)
    {
        var result = new ImportResult();
        var allHosts = _hosts.GetAll();

        // 先建立缺少的帳號，後面指派負責人時才對得上 UserId
        foreach (var account in plan.NewUsers)
        {
            if (_users.FindByAccount(account) != null) continue;
            _users.Upsert(new WebUser
            {
                Account = account,
                DisplayName = account,
                Active = true,
                GroupIds = new List<long>()
            });
            result.CreatedUsers.Add(account);
        }

        // 重新彙總（與 BuildPlan 同一套解析），逐台更新負責人清單
        var resolved = new List<(WebHost Host, string Account)>();
        foreach (var row in table.Rows)
        {
            var (host, account, error) = ResolveRow(row, allHosts);
            if (error == null) resolved.Add((host!, account!));
        }

        foreach (var group in resolved.GroupBy(r => r.Host.HostId))
        {
            var host = group.First().Host;
            var ownerIds = group
                .Select(r => _users.FindByAccount(r.Account)!.UserId)
                .Distinct()
                .ToList();

            if (host.OwnerUserIds.OrderBy(x => x).SequenceEqual(ownerIds.OrderBy(x => x)))
                continue;   // 未變更不寫入（與預覽的 Unchanged 對齊）

            _hosts.Upsert(new WebHost
            {
                HostName = host.HostName,
                DisplayName = host.DisplayName,
                IpAddress = host.IpAddress,
                IpUpdatedAt = host.IpUpdatedAt,
                NetiqServer = host.NetiqServer,
                RoleDesc = host.RoleDesc,
                Source = host.Source,
                Active = host.Active,
                MergedInto = host.MergedInto,
                LastReportAt = host.LastReportAt,
                GroupIds = host.GroupIds,
                OwnerUserIds = ownerIds
            });
            result.Updated++;
        }

        return result;
    }

    /// <summary>
    /// 解析單列成 (主機, 帳號)。回傳的 error 非 null 時即該列的錯誤原因（可直接顯示）。
    /// 比對規則：host_name 優先、ip_address fallback；兩者都給且指向不同主機視為衝突。
    /// </summary>
    private (WebHost? Host, string? Account, string? Error) ResolveRow(CsvRow row, List<WebHost> allHosts)
    {
        var account = row.Get("owner_account");
        if (string.IsNullOrWhiteSpace(account))
            return (null, null, "owner_account 為必填。");

        var hostName = row.Get("host_name");
        var ip = row.Get("ip_address");
        if (string.IsNullOrWhiteSpace(hostName) && string.IsNullOrWhiteSpace(ip))
            return (null, null, "host_name 與 ip_address 至少要填一個。");

        WebHost? byName = null;
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            byName = allHosts.FirstOrDefault(h =>
                h.MergedInto == null &&
                string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase));
            if (byName == null)
                return (null, null, $"找不到主機「{hostName}」（主機不會自動建立）。");
        }

        WebHost? byIp = null;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var matches = allHosts
                .Where(h => h.MergedInto == null &&
                            string.Equals(h.IpAddress, ip, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName == null)
            {
                if (matches.Count == 0)
                    return (null, null, $"找不到 IP 為「{ip}」的主機（主機不會自動建立）。");
                if (matches.Count > 1)
                    return (null, null, $"IP「{ip}」對應多台主機，請改用 host_name 指定。");
                byIp = matches[0];
            }
            else
            {
                byIp = matches.FirstOrDefault(h => h.HostId == byName.HostId);
            }
        }

        // 兩欄都給且指向不同主機：交叉驗證不一致，擋下
        if (byName != null && !string.IsNullOrWhiteSpace(ip) && byIp == null)
            return (null, null, $"host_name「{hostName}」與 ip_address「{ip}」指向不同主機。");

        return (byName ?? byIp, account, null);
    }

    private static string DescribeKey(CsvRow row)
    {
        var host = row.Get("host_name");
        if (!string.IsNullOrWhiteSpace(host)) return host;
        var ip = row.Get("ip_address");
        return string.IsNullOrWhiteSpace(ip) ? "(未指定主機)" : ip;
    }

    private static string Fmt(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);
}
