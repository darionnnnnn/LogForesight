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

    // ── os 欄（docs/LINUX-RULES-PLAN.md §3：選填，缺值＝windows）────────────

    [Theory]
    [InlineData("linux", "linux")]
    [InlineData("Linux", "linux")]      // CSV 由人手編輯，大小寫不該影響結果
    [InlineData("  LINUX  ", "linux")]
    [InlineData("Windows", "windows")]
    public void os欄大小寫與空白不拘_儲存值一律正規化為小寫(string csvValue, string expected)
    {
        var table = Parse($"host_name,os\r\nSRV01,{csvValue}\r\n");
        var plan = Importer.BuildPlan(table, "hosts.csv");
        Assert.True(plan.CanApply);

        Importer.Apply(plan, table);

        Assert.Equal(expected, _hosts.FindByName("SRV01")!.Os);
    }

    [Fact]
    public void os欄缺值時預設windows()
    {
        var table = Parse("host_name,role_desc\r\nSRV01,網站主機\r\n");
        Importer.Apply(Importer.BuildPlan(table, "hosts.csv"), table);

        Assert.Equal("windows", _hosts.FindByName("SRV01")!.Os);
    }

    [Fact]
    public void os欄填不合法的值時標記錯誤()
    {
        var plan = Importer.BuildPlan(Parse("host_name,os\r\nSRV01,solaris\r\n"), "hosts.csv");

        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains("windows 或 linux", plan.Rows[0].Error);
    }

    /// <summary>
    /// 既有主機的 os 與 CSV 值只是大小寫不同時，不該被算成一筆變更——
    /// 預覽畫面多出假異動會讓人以為匯入真的改了東西。
    /// </summary>
    [Fact]
    public void os欄大小寫不同不算變更()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV01", Os = "linux" });

        var plan = Importer.BuildPlan(Parse("host_name,os\r\nSRV01,Linux\r\n"), "hosts.csv");

        Assert.Equal(ImportRowAction.Unchanged, plan.Rows[0].Action);
    }

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

/// <summary>
/// owners.csv 的職責只有「更新負責人清單」，不得動到監控歸屬與平台判定。
///
/// **為什麼這組跑在真實 <see cref="HostStore"/> 而不是測試替身上**：曾經的 bug 是 Apply 手刻
/// <c>new WebHost { ... }</c> 交給 <see cref="IHostStore.Upsert"/>，漏抄了 Upsert 既存分支
/// 實際會覆寫的 <c>SentinelId</c>／<c>Os</c>／<c>OrphanedFromSentinel</c>——最嚴重的是 SentinelId
/// 被清成 null 後主機落入「待歸屬」、從此不進日常輪巡。這個症狀源自真實 Upsert 的逐欄覆寫語意，
/// 而 <c>FakeHostStore.Upsert</c> 的欄位清單與真實實作並非逐位相同（例如它不覆寫 <c>Os</c>），
/// 用替身寫這組測試會在 bug 還在的情況下照樣綠燈——那正是它當初躲過測試的方式。
/// </summary>
public class OwnerCsvImporterHostFieldTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly IHostStore _hosts;
    private readonly FakeUserStore _users = new();

    public OwnerCsvImporterHostFieldTests() => _hosts = new HostStore(_fx.Blob("hosts"));

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private OwnerCsvImporter Importer => new(_hosts, _users);

    private static CsvTable Parse(string content) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 5000);

    /// <summary>
    /// 每個欄位都刻意填「非型別預設」的值，漏抄才看得出來（例如 Os 不填 linux 就與預設的
    /// windows 分不出差別）。情境取孤兒主機：被系統停用、留著標記等汰換 Sentinel 時復活。
    /// </summary>
    private WebHost AddHost() => _hosts.Upsert(new WebHost
    {
        HostName = "10.1.2.12",
        DisplayName = "SRV-DB-01",
        IpAddress = "10.1.2.12",
        IpUpdatedAt = new DateTime(2026, 7, 20, 3, 0, 0),
        SentinelId = 42,
        NetiqServer = "SENTINEL-A",
        RoleDesc = "OO部門資料庫",
        Source = "netiq",
        Os = WebHost.OsLinux,
        Active = false,
        GroupIds = new List<long> { 3, 7 },
        OwnerUserIds = new List<long> { 11 },
        OrphanedFromSentinel = "SENTINEL-OLD"
    });

    private void ImportOwner(string account)
    {
        var table = Parse($"host_name,owner_account\r\n10.1.2.12,{account}\r\n");
        var plan = Importer.BuildPlan(table, "owners.csv");
        Assert.True(plan.CanApply);
        Assert.Equal(1, Importer.Apply(plan, table).Updated);
    }

    /// <summary>
    /// 三個曾經被漏抄的欄位各自點名——反射版測試看得出「有欄位變了」，但看不出哪一個變了
    /// 會造成什麼後果，這裡把後果寫在斷言旁邊。
    /// </summary>
    [Fact]
    public void 匯入負責人_不動Sentinel歸屬與OS與孤兒標記()
    {
        var host = AddHost();
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        ImportOwner("DOMAIN\\a");

        var after = _hosts.Get(host.HostId)!;
        // 清成 null 會讓這台落入「待歸屬」、從此不進日常輪巡——看起來還在監控，實際沒人在看
        Assert.Equal(42, after.SentinelId);
        // 退回 windows 會讓 Linux 主機整個換成 Windows 規則面
        Assert.Equal(WebHost.OsLinux, after.Os);
        // 標記遺失後，汰換 Sentinel 時這台無法再用「重疊」分類復活
        Assert.Equal("SENTINEL-OLD", after.OrphanedFromSentinel);
    }

    /// <summary>
    /// 逐欄反射比對：除 OwnerUserIds 外每個欄位都必須與匯入前逐位相同。
    /// 與 <c>覆蓋builtin時除Enabled與修改追蹤外每一個欄位都取自種子</c> 同一個作法——
    /// 點名式斷言只釘得住今天已知的欄位，<see cref="WebHost"/> 日後新增欄位時要照樣紅燈，
    /// 得靠反射把「全部欄位」都納進來。
    /// </summary>
    [Fact]
    public void 匯入負責人_除負責人外每一個欄位都不變()
    {
        var host = AddHost();
        _users.Upsert(new WebUser { Account = "DOMAIN\\a" });

        // Get 每次都自 JSON 重新反序列化，這份快照與 store 內的實體互不相干
        var before = _hosts.Get(host.HostId)!;

        ImportOwner("DOMAIN\\a");

        var after = _hosts.Get(host.HostId)!;
        foreach (var property in typeof(WebHost).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            if (property.Name == nameof(WebHost.OwnerUserIds)) continue;

            var expected = property.GetValue(before);
            var actual = property.GetValue(after);

            if (expected is System.Collections.IEnumerable expectedItems and not string)
            {
                Assert.True(expectedItems.Cast<object>().SequenceEqual(((System.Collections.IEnumerable)actual!).Cast<object>()),
                    $"欄位 {property.Name} 被 owners.csv 匯入改動了——匯入負責人只該改 OwnerUserIds");
                continue;
            }

            Assert.True(Equals(expected, actual),
                $"欄位 {property.Name} 被 owners.csv 匯入改動了（匯入前={expected}、匯入後={actual}）——匯入負責人只該改 OwnerUserIds");
        }

        // 負責人本身確實有換掉，否則上面的「都沒變」是因為根本沒寫入
        Assert.Equal(new[] { _users.FindByAccount("DOMAIN\\a")!.UserId }, after.OwnerUserIds);
    }
}
