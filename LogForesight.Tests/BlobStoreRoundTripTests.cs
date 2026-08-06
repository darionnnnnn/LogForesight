using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 全部 <c>JsonBlobCollection</c> store 的欄位往返（對**真實** store，不是替身）。
///
/// <para><b>為什麼整組存在</b>：這些 store 的 <c>Upsert</c> 更新既有資料時是**逐欄複製**，
/// 新增模型欄位卻忘了在那裡加一行，症狀是「新增存得進去、編輯被靜默還原」——
/// 建置過、單元測試也可能全綠，因為測試替身各自有一份同樣漏抄的複製邏輯。
/// 本專案已有三個前例：`RuleImporter` 漏抄 `ElevatesDayRisk`、
/// `OwnerCsvImporter` 手刻 WebHost 漏抄 SentinelId/Os/OrphanedFromSentinel、
/// 2026-08-06 的 `SentinelStore` 漏抄 `UseEsmDirectory`（見
/// docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §8.2／§8.4）。</para>
///
/// <para>因此這裡比對的是**整個物件**：從「全部欄位都不一樣」的既有資料更新過去，
/// 任一欄沒被複製都當場失敗，不必倚賴有沒有人記得補測試。
/// <b>刻意不複製的欄位逐案排除並註明理由</b>——那些排除本身就是設計決策，
/// 不是可以無腦全比對掉的東西。</para>
/// </summary>
public class BlobStoreRoundTripTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // ── HostStore ───────────────────────────────────────────────────────────

    private HostStore Hosts() => new(_fixture.Blob("hosts"));

    /// <summary>
    /// 全欄位填滿的主機。<c>DisplayName</c>／<c>LastReportAt</c>／<c>MergedInto</c>
    /// **刻意不驗證**——見 <see cref="主機_更新時全部可覆寫欄位都跟著改"/> 的說明。
    /// </summary>
    private static WebHost FullyPopulatedHost(string name = "SRV-A") => new()
    {
        HostName = name,
        IpAddress = "10.1.2.5",
        IpUpdatedAt = new DateTime(2026, 8, 1),
        SentinelId = 7,
        NetiqServer = "SENTINEL-A",
        RoleDesc = "網域控制站",
        Source = "netiq",
        Os = WebHost.OsLinux,
        Active = false,
        GroupIds = new List<long> { 1, 2 },
        OwnerUserIds = new List<long> { 3, 4 },
        OrphanedFromSentinel = "SENTINEL-OLD"
    };

    [Fact]
    public void 主機_新增時全部欄位都存得進去()
    {
        var saved = Hosts().Upsert(FullyPopulatedHost());

        var reread = Hosts().Get(saved.HostId)!;
        AssertHostFieldsMatch(FullyPopulatedHost(), reread);
    }

    /// <summary>
    /// 更新走逐欄複製的分支，漏抄一欄就會靜默還原成舊值。
    ///
    /// <para><b>三個刻意不覆寫的欄位</b>（HostStore.Upsert 的註解已說明，這裡對齊）：
    /// <c>LastReportAt</c>／<c>DisplayName</c> 是批次的職責（Web 不知道 Sentinel
    /// 回報了什麼名字，照抄會被 Web 端的 null 清掉）；<c>MergedInto</c> 只能經
    /// Merge/Unmerge 設定。<c>CreatedAt</c> 是建立當下的事實，同樣不該被覆寫。</para>
    /// </summary>
    [Fact]
    public void 主機_更新時全部可覆寫欄位都跟著改()
    {
        var original = Hosts().Upsert(new WebHost
        {
            HostName = "SRV-A",
            IpAddress = "10.9.9.9",
            IpUpdatedAt = new DateTime(2020, 1, 1),
            SentinelId = 99,
            NetiqServer = "OLD-SENTINEL",
            RoleDesc = "舊描述",
            Source = "local",
            Os = WebHost.OsWindows,
            Active = true,
            GroupIds = new List<long> { 99 },
            OwnerUserIds = new List<long> { 99 },
            OrphanedFromSentinel = null,
            // 下面三個是「不該被 Upsert 覆寫」的，先填值才驗得出來
            DisplayName = "批次回填的名字",
            LastReportAt = new DateTime(2026, 7, 1),
            MergedInto = 42
        });

        Hosts().Upsert(FullyPopulatedHost());

        var reread = Hosts().Get(original.HostId)!;
        AssertHostFieldsMatch(FullyPopulatedHost(), reread);

        // 刻意不覆寫的三欄要原封不動
        Assert.Equal("批次回填的名字", reread.DisplayName);
        Assert.Equal(new DateTime(2026, 7, 1), reread.LastReportAt);
        Assert.Equal(42, reread.MergedInto);
    }

    /// <summary>逐欄比對主機的**可覆寫**欄位。新增模型欄位時這裡也要加一行——
    /// 加了才逼得出 Upsert 的漏抄，這是刻意的重複，不是可以省的樣板。</summary>
    private static void AssertHostFieldsMatch(WebHost expected, WebHost actual)
    {
        Assert.Equal(expected.HostName, actual.HostName);
        Assert.Equal(expected.IpAddress, actual.IpAddress);
        Assert.Equal(expected.IpUpdatedAt, actual.IpUpdatedAt);
        Assert.Equal(expected.SentinelId, actual.SentinelId);
        Assert.Equal(expected.NetiqServer, actual.NetiqServer);
        Assert.Equal(expected.RoleDesc, actual.RoleDesc);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Os, actual.Os);
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.GroupIds, actual.GroupIds);
        Assert.Equal(expected.OwnerUserIds, actual.OwnerUserIds);
        Assert.Equal(expected.OrphanedFromSentinel, actual.OrphanedFromSentinel);
    }

    [Fact]
    public void 主機_更新不覆寫建立時間()
    {
        var original = Hosts().Upsert(FullyPopulatedHost());
        var createdAt = original.CreatedAt;

        Hosts().Upsert(FullyPopulatedHost());

        Assert.Equal(createdAt, Hosts().Get(original.HostId)!.CreatedAt);
    }

    // ── UserStore ───────────────────────────────────────────────────────────

    private UserStore Users() => new(_fixture.Blob("users"));

    private static WebUser FullyPopulatedUser(string account = "DOMAIN\\alice") => new()
    {
        Account = account,
        DisplayName = "愛麗絲",
        Email = "alice@example.com",
        Active = false,
        GroupIds = new List<long> { 5, 6 }
    };

    [Fact]
    public void 使用者_新增時全部欄位都存得進去()
    {
        var saved = Users().Upsert(FullyPopulatedUser());

        var reread = Users().Get(saved.UserId)!;
        AssertUserFieldsMatch(FullyPopulatedUser(), reread);
    }

    /// <summary>
    /// <c>LastLoginAt</c> **刻意不被 Upsert 覆寫**：唯一寫入點是 <c>TouchLogin</c>。
    /// Upsert 的呼叫端（admin 編輯、負責人匯入）建的是不帶登入時間的物件，
    /// 照抄會把真實登入時間清成 null，讓「最近登入」每編輯一次就歸零。
    /// </summary>
    [Fact]
    public void 使用者_更新時全部可覆寫欄位都跟著改_但不動最近登入時間()
    {
        var original = Users().Upsert(new WebUser
        {
            Account = "DOMAIN\\alice",
            DisplayName = "舊名字",
            Email = "old@example.com",
            Active = true,
            GroupIds = new List<long> { 99 }
        });
        Users().TouchLogin(original.UserId, new DateTime(2026, 8, 5, 9, 0, 0));

        Users().Upsert(FullyPopulatedUser());

        var reread = Users().Get(original.UserId)!;
        AssertUserFieldsMatch(FullyPopulatedUser(), reread);
        Assert.Equal(new DateTime(2026, 8, 5, 9, 0, 0), reread.LastLoginAt);
    }

    private static void AssertUserFieldsMatch(WebUser expected, WebUser actual)
    {
        Assert.Equal(expected.Account, actual.Account);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Email, actual.Email);
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.GroupIds, actual.GroupIds);
    }

    // ── UserGroupStore ──────────────────────────────────────────────────────

    private UserGroupStore UserGroups() => new(_fixture.Blob("user_groups"));

    private static UserGroup FullyPopulatedUserGroup() => new()
    {
        GroupName = "OO部門",
        Role = UserRole.Manager,
        Builtin = true,
        Active = false
    };

    [Fact]
    public void 使用者群組_新增與更新皆全欄位往返()
    {
        var original = UserGroups().Upsert(new UserGroup
        {
            GroupName = "舊名", Role = UserRole.User, Builtin = false, Active = true
        });

        var wanted = FullyPopulatedUserGroup();
        wanted.GroupId = original.GroupId;
        UserGroups().Upsert(wanted);

        var reread = UserGroups().Get(original.GroupId)!;
        Assert.Equal(FullyPopulatedUserGroup().GroupName, reread.GroupName);
        Assert.Equal(FullyPopulatedUserGroup().Role, reread.Role);
        Assert.Equal(FullyPopulatedUserGroup().Builtin, reread.Builtin);
        Assert.Equal(FullyPopulatedUserGroup().Active, reread.Active);
    }

    // ── HostGroupStore ──────────────────────────────────────────────────────

    private HostGroupStore HostGroups() => new(_fixture.Blob("host_groups"));

    [Fact]
    public void 主機群組_新增與更新皆全欄位往返()
    {
        var original = HostGroups().Upsert(new HostGroup { GroupName = "舊名", Active = true });

        HostGroups().Upsert(new HostGroup { GroupId = original.GroupId, GroupName = "機房A", Active = false });

        var reread = HostGroups().Get(original.GroupId)!;
        Assert.Equal("機房A", reread.GroupName);
        Assert.False(reread.Active);
    }

    // ── NoiseMarkStore ──────────────────────────────────────────────────────

    private NoiseMarkStore NoiseMarks() => new(_fixture.Blob("noise_marks"));

    /// <summary>HostName＋IssueKey 是比對鍵（不是可覆寫欄位），其餘三欄都要跟著改</summary>
    [Fact]
    public void 雜訊記憶_新增與更新皆全欄位往返()
    {
        NoiseMarks().Save(new NoiseMark
        {
            HostName = "SRV-A", IssueKey = "System|Svc|1|1",
            MarkedByAccount = "old", MarkedAt = new DateTime(2020, 1, 1), Note = "舊備註"
        });

        NoiseMarks().Save(new NoiseMark
        {
            HostName = "SRV-A", IssueKey = "System|Svc|1|1",
            MarkedByAccount = "admin", MarkedAt = new DateTime(2026, 8, 6), Note = "確認為背景雜訊"
        });

        var reread = NoiseMarks().Get("SRV-A", "System|Svc|1|1")!;
        Assert.Equal("admin", reread.MarkedByAccount);
        Assert.Equal(new DateTime(2026, 8, 6), reread.MarkedAt);
        Assert.Equal("確認為背景雜訊", reread.Note);
        Assert.Single(NoiseMarks().GetForHost("SRV-A"));   // 更新不是新增一列
    }
}
