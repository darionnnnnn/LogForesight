namespace LogForesight.Web.Services.Import;

/// <summary>
/// 群組授權匯入（group_access.csv）。
///
/// 與另外兩種匯入的關鍵差異：**整檔全量取代**，不是逐列 upsert——
/// 檔案就是授權的完整清單，沒列出的既有授權會被移除。
/// 因此預覽**必須明列將被移除的項目**（ImportRowAction.Remove）：
/// 上傳一個漏列的檔案就會靜默收回權限，那是最難察覺的錯誤之一。
/// </summary>
public class GroupAccessCsvImporter : ICsvImporter
{
    private readonly IUserGroupStore _userGroups;
    private readonly IHostGroupStore _hostGroups;
    private readonly IGroupAccessStore _access;

    public GroupAccessCsvImporter(
        IUserGroupStore userGroups, IHostGroupStore hostGroups, IGroupAccessStore access)
    {
        _userGroups = userGroups;
        _hostGroups = hostGroups;
        _access = access;
    }

    public ImportKind Kind => ImportKind.GroupAccess;

    public string[] RequiredHeaders => new[] { "user_group", "host_group" };

    public string[] KnownHeaders => new[] { "user_group", "host_group" };

    public string BuildTemplate() =>
        "user_group,host_group\r\n" +
        "OO部門,OO部門主機\r\n" +
        "XX部門,XX部門主機\r\n";

    public ImportPlan BuildPlan(CsvTable table, string fileName)
    {
        var plan = new ImportPlan { Kind = Kind, FileName = fileName };

        var userGroups = _userGroups.GetAll();
        var hostGroups = _hostGroups.GetAll();
        var existing = _access.GetAll();

        var seen = new HashSet<(string, string)>();
        var inFile = new HashSet<(long, long)>();

        foreach (var row in table.Rows)
        {
            var userGroupName = row.Get("user_group");
            var hostGroupName = row.Get("host_group");
            var rowPlan = new ImportRowPlan
            {
                LineNumber = row.LineNumber,
                Key = $"{userGroupName} → {hostGroupName}"
            };

            if (string.IsNullOrWhiteSpace(userGroupName) || string.IsNullOrWhiteSpace(hostGroupName))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "user_group 與 host_group 皆為必填。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            if (!seen.Add((userGroupName.ToLowerInvariant(), hostGroupName.ToLowerInvariant())))
            {
                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = "同一份檔案中出現重複的授權對應。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            // 授權的兩端都**不自動建立**：授權檔引用到不存在的群組，
            // 幾乎都是拼錯字或匯入順序錯了，自動建立只會產生一個沒有任何成員的空群組，
            // 讓人以為授權設好了
            var userGroup = userGroups.FirstOrDefault(g =>
                string.Equals(g.GroupName, userGroupName, StringComparison.OrdinalIgnoreCase));
            var hostGroup = hostGroups.FirstOrDefault(g =>
                string.Equals(g.GroupName, hostGroupName, StringComparison.OrdinalIgnoreCase));

            if (userGroup == null || hostGroup == null)
            {
                var missing = new List<string>();
                if (userGroup == null) missing.Add($"使用者群組「{userGroupName}」");
                if (hostGroup == null) missing.Add($"主機群組「{hostGroupName}」");

                rowPlan.Action = ImportRowAction.Error;
                rowPlan.Error = $"找不到 {string.Join("、", missing)}。請先建立群組或匯入使用者/主機。";
                plan.Rows.Add(rowPlan);
                continue;
            }

            inFile.Add((userGroup.GroupId, hostGroup.GroupId));

            var alreadyGranted = existing.Any(a =>
                a.UserGroupId == userGroup.GroupId && a.HostGroupId == hostGroup.GroupId);

            rowPlan.Action = alreadyGranted ? ImportRowAction.Unchanged : ImportRowAction.Add;
            rowPlan.Description = alreadyGranted ? "授權已存在" : "新增授權";
            plan.Rows.Add(rowPlan);
        }

        // 全量取代的代價：檔案沒列到的既有授權會被移除。逐筆列出來讓人在套用前看見
        foreach (var access in existing)
        {
            if (inFile.Contains((access.UserGroupId, access.HostGroupId))) continue;

            var userGroupName = userGroups.FirstOrDefault(g => g.GroupId == access.UserGroupId)?.GroupName
                                ?? $"(已刪除:{access.UserGroupId})";
            var hostGroupName = hostGroups.FirstOrDefault(g => g.GroupId == access.HostGroupId)?.GroupName
                                ?? $"(已刪除:{access.HostGroupId})";

            plan.Rows.Add(new ImportRowPlan
            {
                LineNumber = 0,
                Action = ImportRowAction.Remove,
                Key = $"{userGroupName} → {hostGroupName}",
                Description = "此授權不在上傳的檔案中，套用後將被移除"
            });
        }

        if (plan.RemoveCount > 0)
        {
            plan.Warnings.Add(
                $"這份檔案為「全量取代」：套用後將移除 {plan.RemoveCount} 筆未列出的既有授權。" +
                "若只是要新增授權，請確認檔案包含所有現行的授權對應。");
        }

        return plan;
    }

    public ImportResult Apply(ImportPlan plan, CsvTable table)
    {
        var userGroups = _userGroups.GetAll();
        var hostGroups = _hostGroups.GetAll();

        var accesses = new List<GroupAccess>();
        foreach (var row in table.Rows)
        {
            var userGroup = userGroups.FirstOrDefault(g =>
                string.Equals(g.GroupName, row.Get("user_group"), StringComparison.OrdinalIgnoreCase));
            var hostGroup = hostGroups.FirstOrDefault(g =>
                string.Equals(g.GroupName, row.Get("host_group"), StringComparison.OrdinalIgnoreCase));

            if (userGroup == null || hostGroup == null) continue;

            accesses.Add(new GroupAccess
            {
                UserGroupId = userGroup.GroupId,
                HostGroupId = hostGroup.GroupId,
                GrantedAt = DateTime.Now
            });
        }

        _access.ReplaceAll(accesses);

        return new ImportResult
        {
            Added = plan.AddCount,
            Updated = 0,
            Removed = plan.RemoveCount
        };
    }
}
