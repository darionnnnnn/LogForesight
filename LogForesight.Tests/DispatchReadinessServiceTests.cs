using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 U：派工就緒檢查八項（F23-2）與主機群組「看得到的派工池群組」（F17-3）。
/// 用真的 <see cref="DispatchCandidateSource"/>（記憶體替身 store）：就緒檢查問的是真實的候選人快照與同一個資格函式。
/// </summary>
public sealed class DispatchReadinessServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly ScheduleOptionsStore _schedule;

    public DispatchReadinessServiceTests()
    {
        _schedule = new ScheduleOptionsStore(_fixture.Blob("schedule_options"));
    }

    public void Dispose() => _fixture.Dispose();

    private DispatchReadinessService Service() => new(
        _hosts, _hostGroups, _users, _groups,
        new DispatchCandidateSource(_users, _groups, _hosts, _access, new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(),
            _settings, new FakeWorkOrderStore(new FakeIssueCaseStore())),
        _settings, _schedule);

    private static DispatchReadinessItemDto Item(DispatchReadinessDto dto, string id) => Assert.Single(dto.Items, i => i.Id == id);

    private UserGroup AddPoolGroup(string name = "維運池", bool active = true) =>
        _groups.Upsert(new UserGroup { GroupName = name, Role = UserRole.User, Active = active, DispatchPool = true });

    private WebUser AddUser(string account, string? email, bool paused = false, bool active = true, params long[] groupIds) =>
        _users.Upsert(new WebUser { Account = account, Email = email, DispatchPaused = paused, Active = active, GroupIds = groupIds.ToList() });

    private HostGroup AddHostGroup(string name, bool active = true) => _hostGroups.Upsert(new HostGroup { GroupName = name, Active = active });

    private WebHost AddHost(string name, params long[] groupIds) =>
        _hosts.Upsert(new WebHost { HostName = name, GroupIds = groupIds.ToList() });

    /// <summary>一組全部就緒的環境：有池、池成員有 email、主機群組已授權、開關開、排程開、有 AI 窗口</summary>
    private (UserGroup Pool, HostGroup Room) ReadyEnvironment()
    {
        var pool = AddPoolGroup();
        var room = AddHostGroup("機房A");
        _access.SetForUserGroup(pool.GroupId, new[] { room.GroupId });
        AddUser("handler", "handler@example.test", groupIds: pool.GroupId);
        var admins = _groups.Upsert(new UserGroup { GroupName = "管理員", Role = UserRole.Admin, Active = true });
        AddUser("boss", "boss@example.test", groupIds: admins.GroupId);
        AddHost("HOST-1", room.GroupId);
        _settings.Update(s => { s.AutoDispatchEnabled = true; s.MailEnabled = true; });
        _schedule.Update(o => o.Enabled = true);
        return (pool, room);
    }

    // ── 全部通過 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 八項都通過_沒有不通過的項目()
    {
        ReadyEnvironment();

        var dto = Service().Evaluate();

        Assert.Equal(8, dto.Items.Count);
        Assert.True(dto.MailChecked);
        Assert.Equal(0, dto.FailedCount);
        Assert.All(dto.Items, i => Assert.True(i.Passed));
        Assert.DoesNotContain(dto.Items, i => i.Skipped);
    }

    // ── 1. 主機沒有群組 ─────────────────────────────────────────────────────

    [Fact]
    public void 主機沒有群組_不通過並列出台數與名稱_停用主機不算()
    {
        ReadyEnvironment();
        AddHost("NO-GROUP-A");
        AddHost("NO-GROUP-B");
        var stopped = AddHost("STOPPED");
        stopped.Active = false;

        var item = Item(Service().Evaluate(), "host-no-group");

        Assert.False(item.Passed);
        Assert.Equal(2, item.Affected);
        Assert.Equal(new[] { "NO-GROUP-A", "NO-GROUP-B" }, item.Examples);
    }

    [Fact]
    public void 主機都有群組_通過_一台都沒有也通過()
    {
        ReadyEnvironment();
        Assert.True(Item(Service().Evaluate(), "host-no-group").Passed);

        var empty = new DispatchReadinessServiceTests();
        try { Assert.True(Item(empty.Service().Evaluate(), "host-no-group").Passed); }
        finally { empty.Dispose(); }
    }

    // ── 2. 主機群組沒授權給任何派工池群組 ───────────────────────────────────

    [Fact]
    public void 主機群組沒授權給派工池_不通過_空群組與停用群組不算()
    {
        var (_, room) = ReadyEnvironment();
        var lonely = AddHostGroup("機房B");           // 有啟用主機、沒授權：算
        AddHost("HOST-B", lonely.GroupId);
        AddHostGroup("空群組");                         // 沒有主機：不算
        var stoppedGroup = AddHostGroup("停用群組", active: false);   // 停用：不算
        AddHost("HOST-C", stoppedGroup.GroupId);

        var item = Item(Service().Evaluate(), "host-group-no-pool");

        Assert.False(item.Passed);
        Assert.Equal(1, item.Affected);
        Assert.Equal(new[] { "機房B" }, item.Examples);
        Assert.NotEqual(room.GroupName, item.Examples.Single());
    }

    [Fact]
    public void 授權給派工池後_該項通過()
    {
        var (pool, _) = ReadyEnvironment();
        var other = AddHostGroup("機房B");
        AddHost("HOST-B", other.GroupId);
        Assert.False(Item(Service().Evaluate(), "host-group-no-pool").Passed);

        _access.SetForUserGroup(pool.GroupId, _hostGroups.GetAll().Select(g => g.GroupId));

        Assert.True(Item(Service().Evaluate(), "host-group-no-pool").Passed);
    }

    [Fact]
    public void 派工池沒有可接單成員時_主機群組那項無法判定_不算不通過()
    {
        AddPoolGroup();
        var room = AddHostGroup("機房A");
        AddHost("HOST-1", room.GroupId);

        var dto = Service().Evaluate();

        var item = Item(dto, "host-group-no-pool");
        Assert.True(item.Passed);
        Assert.True(item.Skipped);
        Assert.False(Item(dto, "no-pool").Passed);   // 真正的原因在派工池那一項
    }

    // ── 3. 派工池 ───────────────────────────────────────────────────────────

    [Fact]
    public void 沒有派工池群組_不通過_說明是沒有群組()
    {
        var item = Item(Service().Evaluate(), "no-pool");

        Assert.False(item.Passed);
        Assert.Contains("沒有任何啟用中的派工池群組", item.Detail);
    }

    [Fact]
    public void 派工池群組全員暫停接單_不通過_說明是沒有可接單的成員()
    {
        var pool = AddPoolGroup();
        AddUser("paused", "p@example.test", paused: true, groupIds: pool.GroupId);

        var item = Item(Service().Evaluate(), "no-pool");

        Assert.False(item.Passed);
        Assert.Contains("沒有可接單的成員", item.Detail);
    }

    [Fact]
    public void 派工池有一位可接單成員_通過()
    {
        var pool = AddPoolGroup();
        AddUser("ok", "ok@example.test", groupIds: pool.GroupId);

        Assert.True(Item(Service().Evaluate(), "no-pool").Passed);
    }

    // ── 4～6. 開關、AI 窗口、排程 ───────────────────────────────────────────

    [Fact]
    public void 自動派工開關_關不通過_開通過()
    {
        Assert.False(Item(Service().Evaluate(), "auto-dispatch-off").Passed);
        _settings.Update(s => s.AutoDispatchEnabled = true);
        Assert.True(Item(Service().Evaluate(), "auto-dispatch-off").Passed);
    }

    [Fact]
    public void AI窗口_沒有可解析的窗口不通過_有窗口通過()
    {
        _schedule.Update(o => o.AiWindows = new List<ScheduleWindow>());
        Assert.False(Item(Service().Evaluate(), "ai-window").Passed);

        _schedule.Update(o => o.AiWindows = new List<ScheduleWindow> { new() { Start = "25:99", End = "xx" } });
        Assert.False(Item(Service().Evaluate(), "ai-window").Passed);   // 無法解析的窗口等於沒有

        _schedule.Update(o => o.AiWindows = new List<ScheduleWindow> { new() { Start = "01:00", End = "05:00" } });
        Assert.True(Item(Service().Evaluate(), "ai-window").Passed);
    }

    [Fact]
    public void 排程_未啟用不通過_啟用通過()
    {
        Assert.False(Item(Service().Evaluate(), "schedule-off").Passed);
        _schedule.Update(o => o.Enabled = true);
        Assert.True(Item(Service().Evaluate(), "schedule-off").Passed);
    }

    // ── 7～8. email（只在郵件通知開啟時列入）─────────────────────────────────

    [Fact]
    public void 郵件關閉時兩個email項不列入_也不算不通過()
    {
        var pool = AddPoolGroup();
        AddUser("no-mail", null, groupIds: pool.GroupId);   // 派工池成員沒有 email、也沒有管理員

        var dto = Service().Evaluate();

        Assert.False(dto.MailChecked);
        Assert.Equal(6, dto.Items.Count);
        Assert.DoesNotContain(dto.Items, i => i.Id is "admin-no-email" or "pool-no-email");
    }

    [Fact]
    public void 管理員email_沒有管理員或都沒有email不通過_有一位有email通過()
    {
        _settings.Update(s => s.MailEnabled = true);
        Assert.False(Item(Service().Evaluate(), "admin-no-email").Passed);   // 沒有任何管理員

        var admins = _groups.Upsert(new UserGroup { GroupName = "管理員", Role = UserRole.Admin, Active = true });
        AddUser("a1", null, groupIds: admins.GroupId);
        AddUser("a2", "not-an-email", groupIds: admins.GroupId);   // 信箱不是單一位址：不算可用
        var none = Item(Service().Evaluate(), "admin-no-email");
        Assert.False(none.Passed);
        Assert.Equal(2, none.Affected);

        AddUser("a3", "a3@example.test", groupIds: admins.GroupId);
        Assert.True(Item(Service().Evaluate(), "admin-no-email").Passed);
    }

    [Fact]
    public void 派工池成員email_有人沒有email不通過_全有通過()
    {
        _settings.Update(s => s.MailEnabled = true);
        var pool = AddPoolGroup();
        AddUser("with", "w@example.test", groupIds: pool.GroupId);
        var without = AddUser("without", null, groupIds: pool.GroupId);

        var item = Item(Service().Evaluate(), "pool-no-email");
        Assert.False(item.Passed);
        Assert.Equal(1, item.Affected);
        Assert.Equal(new[] { "without" }, item.Examples);

        without.Email = "without@example.test";
        Assert.True(Item(Service().Evaluate(), "pool-no-email").Passed);
    }

    // ── F17-3：主機群組看得到的派工池群組 ────────────────────────────────────

    [Fact]
    public void 主機群組的派工池群組數_授權幾個池就是幾個_沒授權是0()
    {
        var poolA = AddPoolGroup("池甲");
        var poolB = AddPoolGroup("池乙");
        AddUser("a", "a@x.test", groupIds: poolA.GroupId);
        AddUser("b", "b@x.test", groupIds: poolB.GroupId);
        var both = AddHostGroup("兩池");
        var one = AddHostGroup("一池");
        var none = AddHostGroup("無池");
        AddHost("H1", both.GroupId); AddHost("H2", one.GroupId); AddHost("H3", none.GroupId);
        _access.SetForUserGroup(poolA.GroupId, new[] { both.GroupId, one.GroupId });
        _access.SetForUserGroup(poolB.GroupId, new[] { both.GroupId });

        var sights = Service().GetHostGroupSights().ToDictionary(s => s.GroupName);

        Assert.Equal(2, sights["兩池"].PoolGroups);
        Assert.Equal(1, sights["一池"].PoolGroups);
        Assert.Equal(0, sights["無池"].PoolGroups);
        Assert.All(sights.Values, s => Assert.True(s.Evaluated));
    }

    [Fact]
    public void 主機群組的派工池群組數_空群組與停用群組不評估_停用的派工池群組不算()
    {
        var pool = AddPoolGroup();
        var stoppedPool = AddPoolGroup("停用池", active: false);
        AddUser("a", "a@x.test", groupIds: pool.GroupId);
        AddUser("s", "s@x.test", groupIds: stoppedPool.GroupId);
        var empty = AddHostGroup("空");
        var stopped = AddHostGroup("停用", active: false);
        var room = AddHostGroup("機房");
        AddHost("H-STOP", stopped.GroupId);
        AddHost("H1", room.GroupId);
        _access.SetForUserGroup(stoppedPool.GroupId, new[] { room.GroupId });   // 只有停用的池授權

        var sights = Service().GetHostGroupSights().ToDictionary(s => s.GroupName);

        Assert.False(sights["空"].Evaluated);
        Assert.False(sights["停用"].Evaluated);
        Assert.True(sights["機房"].Evaluated);
        Assert.Equal(0, sights["機房"].PoolGroups);   // 停用的派工池群組不算
        _ = empty;
    }

    [Fact]
    public void 主機群組的派工池群組數_派工池沒有可接單成員時不評估()
    {
        AddPoolGroup();
        var room = AddHostGroup("機房");
        AddHost("H1", room.GroupId);

        Assert.All(Service().GetHostGroupSights(), s => Assert.False(s.Evaluated));
    }

    [Fact]
    public void 主機群組的派工池群組數_成員只看得到群組內部分主機不算授權()
    {
        var pool = AddPoolGroup();
        var owner = AddUser("owner", "o@x.test", groupIds: pool.GroupId);
        var room = AddHostGroup("機房");
        var mine = AddHost("MINE", room.GroupId);
        AddHost("OTHER", room.GroupId);
        mine.OwnerUserIds = new List<long> { owner.UserId };   // 只靠負責人看到其中一台

        var sight = Assert.Single(Service().GetHostGroupSights());

        Assert.True(sight.Evaluated);
        Assert.Equal(0, sight.PoolGroups);
    }

    // ── 群組清單與批次設定群組的提示 ─────────────────────────────────────────

    [Fact]
    public void 主機群組清單_帶出看得到的派工池群組數_不適用時為null()
    {
        var pool = AddPoolGroup();
        AddUser("a", "a@x.test", groupIds: pool.GroupId);
        var seen = AddHostGroup("有人看");
        var unseen = AddHostGroup("沒人看");
        AddHostGroup("空群組");
        AddHost("H1", seen.GroupId); AddHost("H2", unseen.GroupId);
        _access.SetForUserGroup(pool.GroupId, new[] { seen.GroupId });

        var service = new GroupAdminService(_groups, _hostGroups, _access, _users, _hosts, new RecordingAuditService(),
            new PermissionVersionStamp(_fixture.Blob(PermissionVersionStamp.BlobKey)), Service());
        var byName = service.GetHostGroups().ToDictionary(g => g.GroupName);

        Assert.Equal(1, byName["有人看"].VisiblePoolGroups);
        Assert.Equal(0, byName["沒人看"].VisiblePoolGroups);
        Assert.Null(byName["空群組"].VisiblePoolGroups);
    }
}
