namespace LogForesight.Web.Services.Import;

/// <summary>
/// 使用者匯入（users.csv）。自然鍵 = account。
///
/// groups 欄語意：**有值＝整組取代、空白＝不變**。
/// 取代而非追加是因為調部門時最容易出錯的是「忘了拿掉舊部門」——
/// 那會留下看不見的殘留權限。要單獨加一個群組請走使用者維護頁。
/// </summary>
public class UserCsvImporter : ICsvImporter
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;

    public UserCsvImporter(IUserStore users, IUserGroupStore groups)
    {
        _users = users;
        _groups = groups;
    }

    public ImportKind Kind => ImportKind.Users;

    public string[] RequiredHeaders => new[] { "account" };

    public string[] KnownHeaders => new[] { "account", "display_name", "email", "groups", "active" };

    public string BuildTemplate() =>
        "account,display_name,email,groups,active\r\n" +
        "DOMAIN\\wangxm,王小明,wang@corp.com,OO部門,1\r\n" +
        "DOMAIN\\lidh,李大華,li@corp.com,OO部門;XX部門,1\r\n" +
        "DOMAIN\\adminz,張管理,zhang@corp.com,admin,1\r\n";

    public ImportPlan BuildPlan(CsvTable table, string fileName)
    {
        var plan = new ImportPlan { Kind = Kind, FileName = fileName };

        var existingGroups = _groups.GetAll();
        var seenAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingNewGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var account = row.Get("account");
            var rowPlan = new ImportRowPlan { LineNumber = row.LineNumber, Key = account };

            if (string.IsNullOrWhiteSpace(account))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "account 為必填。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            if (!seenAccounts.Add(account))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "同一份檔案中出現重複的帳號。";
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

            // 群組不存在就自動建立（初次匯入要先手動建幾十個群組太折磨），
            // 且**一律建成 role=user**：admin/manager/dev 是系統種子群組，
            // 不允許由一份試算表無中生有地造出管理權限。
            // 指派到既有的 builtin 群組則是允許的——那是管理者刻意的操作，且全程稽核。
            var groupNames = row.GetMultiple("groups");
            foreach (var name in groupNames)
            {
                var exists = existingGroups.Any(g =>
                    string.Equals(g.GroupName, name, StringComparison.OrdinalIgnoreCase));

                if (!exists) pendingNewGroups.Add(name);
            }

            var existingUser = _users.FindByAccount(account);
            if (existingUser == null)
            {
                rowPlan.Action = ImportRowAction.Add;
                rowPlan.Description = $"新增使用者「{row.Get("display_name")}」" +
                                      (groupNames.Count > 0 ? $"，群組：{string.Join("、", groupNames)}" : "");
            }
            else
            {
                var changes = BuildChanges(existingUser, row, groupNames, existingGroups);
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
        return plan;
    }

    public ImportResult Apply(ImportPlan plan, CsvTable table)
    {
        var result = new ImportResult();

        // 先建立需要的群組，後面指派成員時才對得上
        foreach (var name in plan.NewGroups)
        {
            if (_groups.FindByName(name) != null) continue;

            _groups.Upsert(new UserGroup { GroupName = name, Role = UserRole.User, Builtin = false, Active = true });
            result.CreatedGroups.Add(name);
        }

        var groupsByName = _groups.GetAll()
            .ToDictionary(g => g.GroupName, g => g.GroupId, StringComparer.OrdinalIgnoreCase);

        var plansByLine = plan.Rows.ToDictionary(r => r.LineNumber);

        foreach (var row in table.Rows)
        {
            if (!plansByLine.TryGetValue(row.LineNumber, out var rowPlan)) continue;
            if (rowPlan.Action is ImportRowAction.Unchanged or ImportRowAction.Error) continue;

            var account = row.Get("account");
            var existing = _users.FindByAccount(account);

            // groups 欄空白 = 不變（沿用既有），有值 = 整組取代
            var groupIds = row.HasValue("groups")
                ? row.GetMultiple("groups").Select(n => groupsByName[n]).ToList()
                : existing?.GroupIds ?? new List<long>();

            _users.Upsert(new WebUser
            {
                Account = account,
                DisplayName = row.HasValue("display_name") ? row.Get("display_name")
                    : existing?.DisplayName ?? account,
                Email = row.HasValue("email") ? row.Get("email") : existing?.Email,
                Active = row.GetBool("active") ?? existing?.Active ?? true,
                GroupIds = groupIds
            });

            if (rowPlan.Action == ImportRowAction.Add) result.Added++;
            else result.Updated++;
        }

        return result;
    }

    private static List<ImportFieldChange> BuildChanges(
        WebUser existing, CsvRow row, List<string> groupNames, List<UserGroup> allGroups)
    {
        var changes = new List<ImportFieldChange>();

        if (row.HasValue("display_name") && row.Get("display_name") != existing.DisplayName)
        {
            changes.Add(new ImportFieldChange
            {
                Field = "顯示名稱", Before = existing.DisplayName, After = row.Get("display_name")
            });
        }

        if (row.HasValue("email") && row.Get("email") != (existing.Email ?? ""))
        {
            changes.Add(new ImportFieldChange
            {
                Field = "Email", Before = existing.Email ?? "（無）", After = row.Get("email")
            });
        }

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
            var beforeNames = existing.GroupIds
                .Select(id => allGroups.FirstOrDefault(g => g.GroupId == id)?.GroupName ?? $"(未知:{id})")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var afterNames = groupNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            if (!beforeNames.SequenceEqual(afterNames, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ImportFieldChange
                {
                    Field = "群組",
                    Before = beforeNames.Count == 0 ? "（無）" : string.Join("、", beforeNames),
                    After = afterNames.Count == 0 ? "（無）" : string.Join("、", afterNames)
                });
            }
        }

        return changes;
    }
}
