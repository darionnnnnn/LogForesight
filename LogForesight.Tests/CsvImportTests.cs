using System.Text;
using LogForesight.Web.Models;
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

/// <summary>
/// 匯入類型退役後的行為（§2a，回饋第十一輪）：使用者／主機／群組授權三種 Importer
/// 已整組移除，只剩負責人。舊網址（含使用者收藏的範本連結）打進來必須是可讀的
/// 400 驗證錯誤，不是 500 或空白畫面。
/// </summary>
public class RetiredImportKindTests
{
    private ImportService Create() =>
        new(new ICsvImporter[] { new OwnerCsvImporter(new FakeHostStore(), new FakeUserStore()) },
            new RecordingAuditService(),
            new FakeImportLogStore(),
            FakeCurrentUser.WithCapabilities(),
            new FakeSystemSettingsStore());

    [Theory]
    [InlineData(ImportKind.Users)]
    [InlineData(ImportKind.Hosts)]
    [InlineData(ImportKind.GroupAccess)]
    public void 退役類型_下載範本被拒(ImportKind kind)
    {
        var ex = Assert.Throws<DomainException>(() => Create().GetTemplate(kind));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("不支援", ex.Message);
    }

    [Theory]
    [InlineData(ImportKind.Users)]
    [InlineData(ImportKind.Hosts)]
    [InlineData(ImportKind.GroupAccess)]
    public void 退役類型_上傳預覽被拒(ImportKind kind)
    {
        var content = new MemoryStream(Encoding.UTF8.GetBytes("account\r\nDOMAIN\\wang\r\n"));

        var ex = Assert.Throws<DomainException>(() => Create().Preview(kind, content, "x.csv"));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void 負責人類型_仍可正常取得範本()
    {
        var template = Encoding.UTF8.GetString(Create().GetTemplate(ImportKind.Owners));

        Assert.Contains("owner_account", template);
    }
}
