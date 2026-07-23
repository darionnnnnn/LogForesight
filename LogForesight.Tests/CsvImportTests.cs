using System.Text;
using LogForesight.Web.Services.Import;
using Xunit;

namespace LogForesight.Tests;

public class CsvParserTests
{
    private static CsvTable Parse(string content, bool withBom = false)
    {
        var bytes = withBom
            ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray()
            : Encoding.UTF8.GetBytes(content);

        return CsvParser.Parse(new MemoryStream(bytes), maxRows: 5000);
    }

    /// <summary>
    /// Excel 另存的 CSV 幾乎都帶 BOM。不處理的話第一個欄位名會多出一個看不見的字元，
    /// 症狀是「明明有 account 欄卻說找不到」——最難自己查出來的那種錯誤。
    /// </summary>
    [Fact]
    public void 帶BOM的檔案_標題列不含隱形字元()
    {
        var table = Parse("account,display_name\r\nDOMAIN\\wang,王小明\r\n", withBom: true);

        Assert.Equal(new[] { "account", "display_name" }, table.Headers);
        Assert.Equal("DOMAIN\\wang", table.Rows[0].Get("account"));
    }

    [Fact]
    public void 欄位名比對不分大小寫()
    {
        var table = Parse("Account,Display_Name\r\nDOMAIN\\wang,王小明\r\n");

        Assert.Equal("王小明", table.Rows[0].Get("display_name"));
    }

    [Fact]
    public void 雙引號欄位_可包含逗號()
    {
        var table = Parse("host_name,role_desc\r\nSRV01,\"資料庫,備援\"\r\n");

        Assert.Equal("資料庫,備援", table.Rows[0].Get("role_desc"));
    }

    [Fact]
    public void 雙引號跳脫_還原為單引號()
    {
        var table = Parse("host_name,role_desc\r\nSRV01,\"他說\"\"你好\"\"\"\r\n");

        Assert.Equal("他說\"你好\"", table.Rows[0].Get("role_desc"));
    }

    [Fact]
    public void 多值欄位_以分號分隔並去重()
    {
        var table = Parse("account,groups\r\nDOMAIN\\wang,OO部門;XX部門;OO部門\r\n");

        Assert.Equal(new[] { "OO部門", "XX部門" }, table.Rows[0].GetMultiple("groups"));
    }

    [Fact]
    public void 空白列_略過不計()
    {
        var table = Parse("account\r\nDOMAIN\\wang\r\n\r\nDOMAIN\\lee\r\n");

        Assert.Equal(2, table.Rows.Count);
    }

    /// <summary>錯誤訊息要指得出是哪一行，所以行號必須含標題列</summary>
    [Fact]
    public void 行號_對應原始檔案含標題列()
    {
        var table = Parse("account\r\nDOMAIN\\wang\r\nDOMAIN\\lee\r\n");

        Assert.Equal(2, table.Rows[0].LineNumber);
        Assert.Equal(3, table.Rows[1].LineNumber);
    }

    [Fact]
    public void 欄位數少於標題_缺的欄位視為空值()
    {
        var table = Parse("account,display_name,email\r\nDOMAIN\\wang,王小明\r\n");

        Assert.Equal("", table.Rows[0].Get("email"));
        Assert.False(table.Rows[0].HasValue("email"));
    }

    [Fact]
    public void 重複的標題欄位_直接拒絕()
    {
        var ex = Assert.Throws<CsvParseException>(() => Parse("account,account\r\nx,y\r\n"));
        Assert.Contains("重複", ex.Message);
    }

    [Fact]
    public void 空檔案_明確報錯()
    {
        Assert.Throws<CsvParseException>(() => Parse(""));
    }

    [Fact]
    public void 超過列數上限_拒絕並提示分批()
    {
        var content = new StringBuilder("account\r\n");
        for (var i = 0; i < 10; i++) content.Append($"user{i}\r\n");

        var bytes = Encoding.UTF8.GetBytes(content.ToString());
        var ex = Assert.Throws<CsvParseException>(() =>
            CsvParser.Parse(new MemoryStream(bytes), maxRows: 5));

        Assert.Contains("上限", ex.Message);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("", null)]
    [InlineData("maybe", null)]
    public void 布林欄位解析(string value, bool? expected)
    {
        var table = Parse($"account,active\r\nx,{value}\r\n");

        Assert.Equal(expected, table.Rows[0].GetBool("active"));
    }
}

public class UserCsvImporterTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();

    private UserCsvImporter Importer => new(_users, _groups);

    private static CsvTable Parse(string content) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 5000);

    [Fact]
    public void 新帳號_判定為新增並列出將建立的群組()
    {
        var table = Parse("account,display_name,groups\r\nDOMAIN\\wang,王小明,OO部門\r\n");

        var plan = Importer.BuildPlan(table, "users.csv");

        Assert.Equal(1, plan.AddCount);
        Assert.Contains("OO部門", plan.NewGroups);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void 套用後_使用者與群組皆已建立()
    {
        var table = Parse("account,display_name,groups\r\nDOMAIN\\wang,王小明,OO部門\r\n");
        var plan = Importer.BuildPlan(table, "users.csv");

        var result = Importer.Apply(plan, table);

        Assert.Equal(1, result.Added);
        var user = _users.FindByAccount("DOMAIN\\wang");
        Assert.NotNull(user);
        var group = _groups.FindByName("OO部門");
        Assert.NotNull(group);
        Assert.Contains(group!.GroupId, user!.GroupIds);
    }

    /// <summary>自動建立的群組一律是 User 角色——不允許一份試算表造出管理權限</summary>
    [Fact]
    public void 自動建立的群組_角色一律為User且非builtin()
    {
        var table = Parse("account,groups\r\nDOMAIN\\wang,某某部門\r\n");
        var plan = Importer.BuildPlan(table, "users.csv");
        Importer.Apply(plan, table);

        var group = _groups.FindByName("某某部門")!;
        Assert.Equal(UserRole.User, group.Role);
        Assert.False(group.Builtin);
    }

    /// <summary>groups 有值＝整組取代：調部門時最容易漏掉的就是移除舊部門</summary>
    [Fact]
    public void groups有值_整組取代既有群組()
    {
        var oo = _groups.Upsert(new UserGroup { GroupName = "OO部門" });
        var xx = _groups.Upsert(new UserGroup { GroupName = "XX部門" });
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", GroupIds = new List<long> { oo.GroupId } });

        var table = Parse("account,groups\r\nDOMAIN\\wang,XX部門\r\n");
        var plan = Importer.BuildPlan(table, "users.csv");
        Importer.Apply(plan, table);

        Assert.Equal(new[] { xx.GroupId }, _users.FindByAccount("DOMAIN\\wang")!.GroupIds);
    }

    /// <summary>groups 空白＝不變：只想改顯示名稱時不該把權限清掉</summary>
    [Fact]
    public void groups空白_保留既有群組()
    {
        var oo = _groups.Upsert(new UserGroup { GroupName = "OO部門" });
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "舊名", GroupIds = new List<long> { oo.GroupId } });

        var table = Parse("account,display_name,groups\r\nDOMAIN\\wang,新名,\r\n");
        var plan = Importer.BuildPlan(table, "users.csv");
        Importer.Apply(plan, table);

        var user = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Equal("新名", user.DisplayName);
        Assert.Equal(new[] { oo.GroupId }, user.GroupIds);
    }

    [Fact]
    public void 內容相同_判定為不變()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明", Active = true });

        var plan = Importer.BuildPlan(Parse("account,display_name\r\nDOMAIN\\wang,王小明\r\n"), "users.csv");

        Assert.Equal(1, plan.UnchangedCount);
    }

    [Fact]
    public void 更新列_附上欄位級前後對照()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "舊名" });

        var plan = Importer.BuildPlan(Parse("account,display_name\r\nDOMAIN\\wang,新名\r\n"), "users.csv");

        var change = Assert.Single(plan.Rows[0].Changes);
        Assert.Equal("顯示名稱", change.Field);
        Assert.Equal("舊名", change.Before);
        Assert.Equal("新名", change.After);
    }

    [Fact]
    public void 缺account_該列標記錯誤且整檔不可套用()
    {
        var plan = Importer.BuildPlan(Parse("account,display_name\r\n,王小明\r\n"), "users.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void 同檔案重複帳號_標記錯誤()
    {
        var plan = Importer.BuildPlan(
            Parse("account\r\nDOMAIN\\wang\r\nDOMAIN\\WANG\r\n"), "users.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("重複", plan.Rows[1].Error);
    }

    [Fact]
    public void active欄位值不合法_標記錯誤()
    {
        var plan = Importer.BuildPlan(Parse("account,active\r\nDOMAIN\\wang,maybe\r\n"), "users.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("1 或 0", plan.Rows[0].Error);
    }
}

public class HostCsvImporterTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();

    private HostCsvImporter Importer => new(_hosts, _hostGroups, _users, _userGroups, _access);

    private static CsvTable Parse(string content) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 5000);

    /// <summary>
    /// 負責人帳號不存在時擋下——負責人打錯字會影響指派與未來的通知，
    /// 自動建立一個空殼帳號反而讓錯誤更難發現。
    /// </summary>
    [Fact]
    public void 負責人帳號不存在_標記錯誤並提示先匯入使用者()
    {
        var plan = Importer.BuildPlan(
            Parse("host_name,owners\r\nSRV01,DOMAIN\\nobody\r\n"), "hosts.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("先匯入使用者", plan.Rows[0].Error);
    }

    [Fact]
    public void 負責人帳號存在_可正常匯入()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang" });

        var table = Parse("host_name,owners,groups\r\nSRV01,DOMAIN\\wang,OO部門主機\r\n");
        var plan = Importer.BuildPlan(table, "hosts.csv");
        Assert.True(plan.CanApply);

        Importer.Apply(plan, table);

        var host = _hosts.FindByName("SRV01")!;
        Assert.Single(host.OwnerUserIds);
        Assert.Single(host.GroupIds);
    }

    /// <summary>
    /// 負責人看不到自己負責的主機時要提醒，但不擋——
    /// 沉默地讓它發生就會變成「這台機器出事沒人看得到」。
    /// </summary>
    [Fact]
    public void 負責人無檢視權_產生警告但不阻擋()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang" });

        var plan = Importer.BuildPlan(
            Parse("host_name,owners,groups\r\nSRV01,DOMAIN\\wang,OO部門主機\r\n"), "hosts.csv");

        Assert.True(plan.CanApply);
        Assert.Contains(plan.Warnings, w => w.Contains("DOMAIN\\wang") && w.Contains("檢視權限"));
    }

    [Fact]
    public void 負責人具ViewAll角色_不產生警告()
    {
        var managerGroup = _userGroups.Upsert(new UserGroup { GroupName = "manager", Role = UserRole.Manager, Builtin = true });
        _users.Upsert(new WebUser { Account = "DOMAIN\\boss", GroupIds = new List<long> { managerGroup.GroupId } });

        var plan = Importer.BuildPlan(
            Parse("host_name,owners,groups\r\nSRV01,DOMAIN\\boss,OO部門主機\r\n"), "hosts.csv");

        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void 主機群組不存在_自動建立()
    {
        var table = Parse("host_name,groups\r\nSRV01,新群組\r\n");
        var plan = Importer.BuildPlan(table, "hosts.csv");

        Assert.Contains("新群組", plan.NewGroups);

        Importer.Apply(plan, table);
        Assert.NotNull(_hostGroups.FindByName("新群組"));
    }
}

public class GroupAccessCsvImporterTests
{
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeGroupAccessStore _access = new();

    private GroupAccessCsvImporter Importer => new(_userGroups, _hostGroups, _access);

    private static CsvTable Parse(string content) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 5000);

    [Fact]
    public void 群組不存在_標記錯誤不自動建立()
    {
        var plan = Importer.BuildPlan(Parse("user_group,host_group\r\nOO部門,OO部門主機\r\n"), "access.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Empty(_userGroups.GetAll());
    }

    /// <summary>
    /// 全量取代最危險的一點：漏列的授權會被靜默移除。
    /// 預覽必須逐筆列出將被移除的項目，否則使用者按下套用時不知道自己收回了什麼。
    /// </summary>
    [Fact]
    public void 未列於檔案的既有授權_預覽列出為移除並提出警告()
    {
        var oo = _userGroups.Upsert(new UserGroup { GroupName = "OO部門" });
        var xx = _userGroups.Upsert(new UserGroup { GroupName = "XX部門" });
        var ooHosts = _hostGroups.Upsert(new HostGroup { GroupName = "OO部門主機" });
        var xxHosts = _hostGroups.Upsert(new HostGroup { GroupName = "XX部門主機" });

        _access.ReplaceAll(new[]
        {
            new GroupAccess { UserGroupId = oo.GroupId, HostGroupId = ooHosts.GroupId },
            new GroupAccess { UserGroupId = xx.GroupId, HostGroupId = xxHosts.GroupId }
        });

        // 只列出 OO 部門的授權，XX 部門的授權將被移除
        var plan = Importer.BuildPlan(Parse("user_group,host_group\r\nOO部門,OO部門主機\r\n"), "access.csv");

        Assert.Equal(1, plan.RemoveCount);
        Assert.Contains(plan.Rows, r => r.Action == ImportRowAction.Remove && r.Key.Contains("XX部門"));
        Assert.Contains(plan.Warnings, w => w.Contains("全量取代"));
    }

    [Fact]
    public void 套用_整份取代授權()
    {
        var oo = _userGroups.Upsert(new UserGroup { GroupName = "OO部門" });
        var xx = _userGroups.Upsert(new UserGroup { GroupName = "XX部門" });
        var ooHosts = _hostGroups.Upsert(new HostGroup { GroupName = "OO部門主機" });
        var xxHosts = _hostGroups.Upsert(new HostGroup { GroupName = "XX部門主機" });

        _access.ReplaceAll(new[] { new GroupAccess { UserGroupId = xx.GroupId, HostGroupId = xxHosts.GroupId } });

        var table = Parse("user_group,host_group\r\nOO部門,OO部門主機\r\n");
        var plan = Importer.BuildPlan(table, "access.csv");
        Importer.Apply(plan, table);

        var remaining = _access.GetAll();
        Assert.Single(remaining);
        Assert.Equal(oo.GroupId, remaining[0].UserGroupId);
        Assert.Equal(ooHosts.GroupId, remaining[0].HostGroupId);
    }
}

public class OwnerCsvImporterTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();

    private OwnerCsvImporter Importer => new(_hosts, _users);

    private static CsvTable Parse(string content) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 5000);

    private WebHost AddHost(string name, string? ip = null) =>
        _hosts.Upsert(new WebHost { HostName = name, IpAddress = ip });

    [Fact]
    public void 同主機多列_彙總為多位負責人並取代()
    {
        AddHost("SRV01");
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });
        _users.Upsert(new WebUser { Account = "DOMAIN\\b" });

        var table = Parse("host_name,owner_account\r\nSRV01,DOMAIN\\a\r\nSRV01,DOMAIN\\b\r\n");
        var plan = Importer.BuildPlan(table, "owners.csv");

        // 一台主機 → 一列預覽（彙總），不是兩列
        Assert.Single(plan.Rows);
        Assert.Equal(ImportRowAction.Update, plan.Rows[0].Action);

        Importer.Apply(plan, table);
        Assert.Equal(2, _hosts.FindByName("SRV01")!.OwnerUserIds.Count);
    }

    [Fact]
    public void 帳號不存在_預覽標記將自動建立_套用時建立()
    {
        AddHost("SRV01");

        var table = Parse("host_name,owner_account\r\nSRV01,DOMAIN\\new\r\n");
        var plan = Importer.BuildPlan(table, "owners.csv");

        Assert.Contains("DOMAIN\\new", plan.NewUsers);
        Assert.True(plan.CanApply);

        var result = Importer.Apply(plan, table);
        Assert.Contains("DOMAIN\\new", result.CreatedUsers);
        Assert.NotNull(_users.FindByAccount("DOMAIN\\new"));
        // 自動建立的帳號是一般使用者、無群組
        Assert.Empty(_users.FindByAccount("DOMAIN\\new")!.GroupIds);
    }

    [Fact]
    public void host_name空白_以IP比對主機()
    {
        AddHost("SRV01", "10.2.3.21");
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        var table = Parse("host_name,ip_address,owner_account\r\n,10.2.3.21,DOMAIN\\a\r\n");
        var plan = Importer.BuildPlan(table, "owners.csv");
        Assert.True(plan.CanApply);

        Importer.Apply(plan, table);
        Assert.Single(_hosts.FindByName("SRV01")!.OwnerUserIds);
    }

    [Fact]
    public void 主機不存在_標記錯誤不自動建立主機()
    {
        var plan = Importer.BuildPlan(
            Parse("host_name,owner_account\r\nGHOST,DOMAIN\\a\r\n"), "owners.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("找不到主機", plan.Rows[0].Error);
        Assert.Empty(_hosts.GetAll());
    }

    [Fact]
    public void IP對應多台主機_擋下要求改用主機名()
    {
        AddHost("SRV01", "10.1.1.1");
        AddHost("SRV02", "10.1.1.1");
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        var plan = Importer.BuildPlan(
            Parse("host_name,ip_address,owner_account\r\n,10.1.1.1,DOMAIN\\a\r\n"), "owners.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("多台主機", plan.Rows[0].Error);
    }

    [Fact]
    public void host_name與ip指向不同主機_交叉驗證擋下()
    {
        AddHost("SRV01", "10.1.1.1");
        AddHost("SRV02", "10.2.2.2");
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        var plan = Importer.BuildPlan(
            Parse("host_name,ip_address,owner_account\r\nSRV01,10.2.2.2,DOMAIN\\a\r\n"), "owners.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("指向不同主機", plan.Rows[0].Error);
    }

    [Fact]
    public void 未出現在檔案的主機_負責人不受影響()
    {
        var srv1 = AddHost("SRV01");
        var other = _users.Upsert(new WebUser { Account = "DOMAIN\\keep" });
        var srv2 = _hosts.Upsert(new WebHost { HostName = "SRV02", OwnerUserIds = new List<long> { other.UserId } });
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        var table = Parse("host_name,owner_account\r\nSRV01,DOMAIN\\a\r\n");
        Importer.Apply(Importer.BuildPlan(table, "owners.csv"), table);

        // SRV02 不在檔案中 → 負責人不動
        Assert.Equal(new[] { other.UserId }, _hosts.FindByName("SRV02")!.OwnerUserIds);
    }
}
