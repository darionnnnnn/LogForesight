namespace LogForesight.Web.Services.Import;

/// <summary>
/// 主機匯入（hosts.csv）。自然鍵 = host_name。
///
/// 兩個與使用者匯入不同的地方：
/// - **owners 引用的帳號必須已存在**，不自動建立。負責人打錯字比群組打錯字嚴重
///   （會影響指派與未來的通知），寧可擋下來讓人修正 → 因此匯入順序是「先使用者、後主機」
/// - 批次分析也會建立主機（Touch），所以主機匯入實務上多半是「補 metadata 與群組」的更新
/// </summary>
public class HostCsvImporter : ICsvImporter
{
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IGroupAccessStore _access;

    public HostCsvImporter(
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IUserStore users,
        IUserGroupStore userGroups,
        IGroupAccessStore access)
    {
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _userGroups = userGroups;
        _access = access;
    }

    public ImportKind Kind => ImportKind.Hosts;

    public string[] RequiredHeaders => new[] { "host_name" };

    public string[] KnownHeaders =>
        new[] { "host_name", "ip_address", "netiq_server", "role_desc", "groups", "owners", "active" };

    public string BuildTemplate() =>
        "host_name,ip_address,netiq_server,role_desc,groups,owners,active\r\n" +
        "SRV-OO-WEB01,10.1.2.11,SENTINEL-A,OO部門網站主機,OO部門主機,DOMAIN\\wangxm;DOMAIN\\lidh,1\r\n" +
        "SRV-OO-DB01,10.1.2.12,SENTINEL-A,OO部門資料庫,OO部門主機;DB伺服器,DOMAIN\\lidh,1\r\n" +
        "SRV-XX-AP01,10.2.3.21,SENTINEL-B,XX部門AP,XX部門主機,DOMAIN\\chenyt,1\r\n";

    public ImportPlan BuildPlan(CsvTable table, string fileName)
    {
        var plan = new ImportPlan { Kind = Kind, FileName = fileName };

        var existingGroups = _hostGroups.GetAll();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingNewGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var hostName = row.Get("host_name");
            var rowPlan = new ImportRowPlan { LineNumber = row.LineNumber, Key = hostName };

            if (string.IsNullOrWhiteSpace(hostName))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "host_name 為必填。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            if (!seenHosts.Add(hostName))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "同一份檔案中出現重複的主機名稱。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            var active = row.GetBool("active");
            if (row.HasValue("active") && active == null)
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "active 只能填 1 或 0。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            // 負責人必須已存在——不自動建立（見類別註解）
            var ownerAccounts = row.GetMultiple("owners");
            var unknownOwners = ownerAccounts
                .Where(a => _users.FindByAccount(a) == null)
                .ToList();

            if (unknownOwners.Count > 0)
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = $"找不到負責人帳號：{string.Join("、", unknownOwners)}。請先匯入使用者，或確認帳號拼寫。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            var groupNames = row.GetMultiple("groups");
            foreach (var name in groupNames)
            {
                var exists = existingGroups.Any(g =>
                    string.Equals(g.GroupName, name, StringComparison.OrdinalIgnoreCase));
                if (!exists) pendingNewGroups.Add(name);
            }

            var existingHost = _hosts.FindByName(hostName);
            if (existingHost == null)
            {
                rowPlan.Action = ImportRowAction.Add;
                rowPlan.Description = $"新增主機「{row.Get("role_desc")}」" +
                                      (groupNames.Count > 0 ? $"，群組：{string.Join("、", groupNames)}" : "");
            }
            else
            {
                var changes = BuildChanges(existingHost, row, groupNames, ownerAccounts, existingGroups);
                if (changes.Count == 0)
                {
                    rowPlan.Action = ImportRowAction.Unchanged;
                    rowPlan.Description = "與現有資料相同";
                }
                else
                {
                    rowPlan.Action = ImportRowAction.Update;
                    rowPlan.Description = string.Join("；", changes.Select(c => $"{c.Field}：{c.Before} → {c.After}"));
                    rowPlan.Changes.AddRange(changes);
                }
            }

            plan.Rows.Add(rowPlan);
        }

        plan.NewGroups.AddRange(pendingNewGroups.OrderBy(n => n));
        AddOwnerVisibilityWarnings(plan, table, pendingNewGroups);
        return plan;
    }

    /// <summary>
    /// 負責人看不到自己負責的主機時提出警告，但**不擋**——
    /// 負責人與授權刻意是兩件事，中間狀態（部門群組還沒授權）是合理的，
    /// 但沉默地讓它發生就會變成「這台機器出事沒人看得到」。
    /// </summary>
    private void AddOwnerVisibilityWarnings(ImportPlan plan, CsvTable table, HashSet<string> pendingNewGroups)
    {
        var userGroups = _userGroups.GetAll();
        var accesses = _access.GetAll();
        var hostGroups = _hostGroups.GetAll();

        foreach (var row in table.Rows)
        {
            var ownerAccounts = row.GetMultiple("owners");
            if (ownerAccounts.Count == 0) continue;

            var hostGroupNames = row.GetMultiple("groups");

            // 這次才要建立的主機群組必然還沒有任何授權，一定看不到
            var hostGroupIds = hostGroupNames
                .Select(n => hostGroups.FirstOrDefault(g =>
                    string.Equals(g.GroupName, n, StringComparison.OrdinalIgnoreCase))?.GroupId)
                .Where(id => id != null)
                .Select(id => id!.Value)
                .ToHashSet();

            foreach (var account in ownerAccounts)
            {
                var user = _users.FindByAccount(account);
                if (user == null) continue;

                var userGroupIds = userGroups
                    .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
                    .Select(g => g.GroupId)
                    .ToHashSet();

                // ViewAll 角色（admin/manager/dev）本來就看得到全部，不需要警告
                var hasViewAll = userGroups.Any(g =>
                    userGroupIds.Contains(g.GroupId) &&
                    g.Role is UserRole.Admin or UserRole.Manager or UserRole.Dev);
                if (hasViewAll) continue;

                var visibleHostGroupIds = accesses
                    .Where(a => userGroupIds.Contains(a.UserGroupId))
                    .Select(a => a.HostGroupId)
                    .ToHashSet();

                if (!hostGroupIds.Any(visibleHostGroupIds.Contains))
                {
                    plan.Warnings.Add(
                        $"第 {row.LineNumber} 行：負責人 {account} 目前沒有 {row.Get("host_name")} 的檢視權限" +
                        "（負責人不會自動取得權限，請確認其部門群組已被授權該主機群組）。");
                }
            }
        }
    }

    public ImportResult Apply(ImportPlan plan, CsvTable table)
    {
        var result = new ImportResult();

        foreach (var name in plan.NewGroups)
        {
            if (_hostGroups.FindByName(name) != null) continue;

            _hostGroups.Upsert(new HostGroup { GroupName = name, Active = true });
            result.CreatedGroups.Add(name);
        }

        var groupsByName = _hostGroups.GetAll()
            .ToDictionary(g => g.GroupName, g => g.GroupId, StringComparer.OrdinalIgnoreCase);

        var plansByLine = plan.Rows.ToDictionary(r => r.LineNumber);

        foreach (var row in table.Rows)
        {
            if (!plansByLine.TryGetValue(row.LineNumber, out var rowPlan)) continue;
            if (rowPlan.Action is ImportRowAction.Unchanged or ImportRowAction.Error) continue;

            var hostName = row.Get("host_name");
            var existing = _hosts.FindByName(hostName);

            var groupIds = row.HasValue("groups")
                ? row.GetMultiple("groups").Select(n => groupsByName[n]).ToList()
                : existing?.GroupIds ?? new List<long>();

            var ownerIds = row.HasValue("owners")
                ? row.GetMultiple("owners").Select(a => _users.FindByAccount(a)!.UserId).ToList()
                : existing?.OwnerUserIds ?? new List<long>();

            var ip = row.HasValue("ip_address") ? row.Get("ip_address") : existing?.IpAddress;

            _hosts.Upsert(new WebHost
            {
                HostName = hostName,
                IpAddress = ip,
                IpUpdatedAt = ip != existing?.IpAddress ? DateTime.Now : existing?.IpUpdatedAt,
                NetiqServer = row.HasValue("netiq_server") ? row.Get("netiq_server") : existing?.NetiqServer,
                RoleDesc = row.HasValue("role_desc") ? row.Get("role_desc") : existing?.RoleDesc ?? "",
                Source = existing?.Source ?? "local",
                Active = row.GetBool("active") ?? existing?.Active ?? true,
                GroupIds = groupIds,
                OwnerUserIds = ownerIds
            });

            if (rowPlan.Action == ImportRowAction.Add) result.Added++;
            else result.Updated++;
        }

        return result;
    }

    private List<ImportFieldChange> BuildChanges(
        WebHost existing, CsvRow row, List<string> groupNames, List<string> ownerAccounts, List<HostGroup> allGroups)
    {
        var changes = new List<ImportFieldChange>();

        void CompareText(string header, string field, string? before)
        {
            if (!row.HasValue(header)) return;
            var after = row.Get(header);
            if (after == (before ?? "")) return;
            changes.Add(new ImportFieldChange { Field = field, Before = string.IsNullOrEmpty(before) ? "（無）" : before, After = after });
        }

        CompareText("ip_address", "IP", existing.IpAddress);
        CompareText("netiq_server", "Sentinel", existing.NetiqServer);
        CompareText("role_desc", "角色描述", existing.RoleDesc);

        var active = row.GetBool("active");
        if (active.HasValue && active.Value != existing.Active)
        {
            changes.Add(new ImportFieldChange
            {
                Field = "狀態",
                Before = existing.Active ? "啟用" : "停用",
                After = active.Value ? "啟用" : "停用"
            });
        }

        if (row.HasValue("groups"))
        {
            var before = existing.GroupIds
                .Select(id => allGroups.FirstOrDefault(g => g.GroupId == id)?.GroupName ?? $"(未知:{id})")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var after = groupNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ImportFieldChange
                {
                    Field = "主機群組",
                    Before = before.Count == 0 ? "（無）" : string.Join("、", before),
                    After = after.Count == 0 ? "（無）" : string.Join("、", after)
                });
            }
        }

        if (row.HasValue("owners"))
        {
            var before = existing.OwnerUserIds
                .Select(id => _users.Get(id)?.Account ?? $"(未知:{id})")
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            var after = ownerAccounts.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

            if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ImportFieldChange
                {
                    Field = "負責人",
                    Before = before.Count == 0 ? "（無）" : string.Join("、", before),
                    After = after.Count == 0 ? "（無）" : string.Join("、", after)
                });
            }
        }

        return changes;
    }
}
