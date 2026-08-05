using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/HISTORY.md #7：一次新增多個帳號。UserAdminService 先前沒有專屬測試檔，
/// 這裡順帶補上既有 SaveUser／SetUserGroups 的基本案例，再測批次新增。
/// </summary>
public class UserAdminServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly RecordingAuditService _audit = new();

    /// <summary>
    /// 使用者詳細（§3）要回答「這個人看得到什麼」，因此刻意串**真實的**
    /// <see cref="VisibilityService"/>——用放行一切的替身會讓群組授權／負責人兩條路徑
    /// 的區分（ViaGroup／ViaOwner 徽章）測不出來。檢視者身分只需具備 Maintain。
    /// </summary>
    private UserAdminService Create() => new(
        _users, _groups, _hosts, _hostGroups, _cases,
        new VisibilityService(
            FakeCurrentUser.WithCapabilities(LogForesight.Web.Auth.Capability.Maintain),
            _users, _groups, _access, _hosts, _cases),
        _audit);

    private long AddGroup(string name) => _groups.Upsert(new UserGroup { GroupName = name, Role = UserRole.User, Active = true }).GroupId;

    [Fact]
    public void BatchCreateUsers_全新帳號_逐一建立並套用群組()
    {
        var groupId = AddGroup("部門A");
        var service = Create();

        var result = service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "DOMAIN\\a", "DOMAIN\\b" },
            GroupIds = new List<long> { groupId }
        });

        Assert.Equal(new[] { "DOMAIN\\a", "DOMAIN\\b" }, result.Created);
        Assert.Empty(result.Overwritten);
        Assert.Empty(result.Skipped);

        var created = _users.FindByAccount("DOMAIN\\a");
        Assert.NotNull(created);
        Assert.Equal("DOMAIN\\a", created!.DisplayName);   // 顯示名稱預設＝帳號
        Assert.Null(created.Email);
        Assert.Contains(groupId, created.GroupIds);
    }

    [Fact]
    public void BatchCreateUsers_帳號去除空白與重複()
    {
        var service = Create();

        var result = service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "  DOMAIN\\a  ", "", "  ", "DOMAIN\\a", "DOMAIN\\A" }
        });

        Assert.Single(result.Created);   // 不分大小寫去重，三種寫法只算一筆
        Assert.Equal("DOMAIN\\a", result.Created[0]);
    }

    [Fact]
    public void BatchCreateUsers_已存在帳號_預設跳過不動()
    {
        var groupA = AddGroup("A");
        var groupB = AddGroup("B");
        var service = Create();
        service.SaveUser(new SaveUserRequest { Account = "DOMAIN\\existing", DisplayName = "原本的名字" });
        service.SetUserGroups(_users.FindByAccount("DOMAIN\\existing")!.UserId, new[] { groupA });

        var result = service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "DOMAIN\\existing" },
            GroupIds = new List<long> { groupB },
            OverwriteExisting = false
        });

        Assert.Empty(result.Created);
        Assert.Empty(result.Overwritten);
        Assert.Equal(new[] { "DOMAIN\\existing" }, result.Skipped);

        var user = _users.FindByAccount("DOMAIN\\existing")!;
        Assert.Equal("原本的名字", user.DisplayName);   // 完全沒被動過
        Assert.Contains(groupA, user.GroupIds);
        Assert.DoesNotContain(groupB, user.GroupIds);
    }

    [Fact]
    public void BatchCreateUsers_已存在帳號_OverwriteExisting時整組取代群組()
    {
        var groupA = AddGroup("A");
        var groupB = AddGroup("B");
        var service = Create();
        service.SaveUser(new SaveUserRequest { Account = "DOMAIN\\existing", DisplayName = "原本的名字" });
        service.SetUserGroups(_users.FindByAccount("DOMAIN\\existing")!.UserId, new[] { groupA });

        var result = service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "DOMAIN\\existing" },
            GroupIds = new List<long> { groupB },
            OverwriteExisting = true
        });

        Assert.Equal(new[] { "DOMAIN\\existing" }, result.Overwritten);

        var user = _users.FindByAccount("DOMAIN\\existing")!;
        Assert.Equal("原本的名字", user.DisplayName);   // 覆蓋只動群組，顯示名稱／Email 不動
        Assert.DoesNotContain(groupA, user.GroupIds);
        Assert.Contains(groupB, user.GroupIds);
    }

    [Fact]
    public void BatchCreateUsers_指定不存在的群組_丟例外()
    {
        var service = Create();

        Assert.Throws<DomainException>(() => service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "DOMAIN\\a" },
            GroupIds = new List<long> { 9999 }
        }));
    }

    [Fact]
    public void BatchCreateUsers_全空白帳號_丟例外()
    {
        var service = Create();

        Assert.Throws<DomainException>(() => service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "", "   " }
        }));
    }

    [Fact]
    public void BatchCreateUsers_超過上限_丟例外()
    {
        var service = Create();
        var accounts = Enumerable.Range(1, UserAdminService.BatchCreateMaxAccounts + 1)
            .Select(i => $"DOMAIN\\user{i}").ToList();

        Assert.Throws<DomainException>(() => service.BatchCreateUsers(new BatchCreateUsersRequest { Accounts = accounts }));
    }

    [Fact]
    public void BatchCreateUsers_每個新建帳號各一筆UserCreate稽核外加一筆批次摘要()
    {
        var service = Create();

        service.BatchCreateUsers(new BatchCreateUsersRequest
        {
            Accounts = new List<string> { "DOMAIN\\a", "DOMAIN\\b" }
        });

        var userCreateEntries = _audit.Entries.Count(e => e.Action == AuditActions.UserCreate && e.TargetKind == "user");
        var batchSummaryEntries = _audit.Entries.Count(e => e.TargetKind == "user_batch");

        Assert.Equal(2, userCreateEntries);
        Assert.Equal(1, batchSummaryEntries);
    }

    // ── 使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）──────────────────────────────

    /// <summary>建立「群組授權看得到 A、負責人看得到 B、兩者皆非的 C」三台主機</summary>
    private WebUser SetupVisibilityFixture()
    {
        var deptGroup = AddGroup("OO部門");
        var user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\wang", DisplayName = "王小明", GroupIds = new List<long> { deptGroup }
        });

        var hostGroup = _hostGroups.Upsert(new HostGroup { GroupName = "OO部門主機" });
        _hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { hostGroup.GroupId } });
        _hosts.Upsert(new WebHost { HostName = "SRV-B", OwnerUserIds = new List<long> { user.UserId } });
        _hosts.Upsert(new WebHost { HostName = "SRV-C" });

        _access.ReplaceAll(new[] { new GroupAccess { UserGroupId = deptGroup, HostGroupId = hostGroup.GroupId } });
        return user;
    }

    [Fact]
    public void GetUserDetail_可見主機含兩條路徑且標示來源()
    {
        var user = SetupVisibilityFixture();

        var detail = Create().GetUserDetail(user.UserId);

        Assert.Equal(new[] { "SRV-A", "SRV-B" }, detail.VisibleHosts.Select(h => h.HostName));

        var viaGroup = detail.VisibleHosts.Single(h => h.HostName == "SRV-A");
        Assert.True(viaGroup.ViaGroup);
        Assert.False(viaGroup.ViaOwner);

        var viaOwner = detail.VisibleHosts.Single(h => h.HostName == "SRV-B");
        Assert.True(viaOwner.ViaOwner);
        Assert.False(viaOwner.ViaGroup);
    }

    /// <summary>兩條路徑可同時成立時兩顆徽章都要亮——拿掉負責人身分是否仍看得到，靠這個判斷</summary>
    [Fact]
    public void GetUserDetail_同時是負責人與群組授權_兩個來源都標示()
    {
        var user = SetupVisibilityFixture();
        var srvA = _hosts.FindByName("SRV-A")!;
        srvA.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(srvA);

        var host = Create().GetUserDetail(user.UserId).VisibleHosts.Single(h => h.HostName == "SRV-A");

        Assert.True(host.ViaGroup);
        Assert.True(host.ViaOwner);
    }

    /// <summary>能力要含負責人隱含的 Handle（§2b），否則畫面會說「這人沒有任何能力」卻能標處理狀態</summary>
    [Fact]
    public void GetUserDetail_負責人的能力含Handle()
    {
        var user = SetupVisibilityFixture();

        var detail = Create().GetUserDetail(user.UserId);

        Assert.Contains(nameof(LogForesight.Web.Auth.Capability.Handle), detail.Capabilities);
    }

    [Fact]
    public void GetUserDetail_被指派歷程以案件為來源_含已結案()
    {
        var user = SetupVisibilityFixture();
        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = "SRV-B", IssueKey = "App|disk|153|1", IssueLabel = "disk 153",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.InProgress,
            CreatedAt = new DateTime(2026, 8, 1), CreatedByAccount = "DOMAIN\\admin",
            FirstLinkedDate = new DateTime(2026, 7, 28), LastLinkedDate = new DateTime(2026, 8, 1)
        });
        _cases.Save(new IssueCase
        {
            CaseId = "c2", HostName = "SRV-B", IssueKey = "App|disk|7|1", IssueLabel = "disk 7",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.Resolved,
            CreatedAt = new DateTime(2026, 7, 20), CreatedByAccount = "DOMAIN\\admin",
            ClosedAt = new DateTime(2026, 7, 25),
            FirstLinkedDate = new DateTime(2026, 7, 18), LastLinkedDate = new DateTime(2026, 7, 24)
        });

        var history = Create().GetUserDetail(user.UserId).AssignmentHistory;

        Assert.Equal(new[] { "c1", "c2" }, history.Select(h => h.CaseId));   // 新到舊
        Assert.False(history[0].Closed);
        Assert.True(history[1].Closed);
        Assert.Equal("DOMAIN\\admin", history[0].CreatedByAccount);
        // 主機名稱要能解析回 hostId，前端才連得到風險日詳情
        Assert.Equal(_hosts.FindByName("SRV-B")!.HostId, history[0].HostId);
    }

    [Fact]
    public void GetUserDetail_查無此人_回404()
    {
        var ex = Assert.Throws<DomainException>(() => Create().GetUserDetail(9999));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    /// <summary>停用帳號：可見範圍為空（停用優先於一切授權路徑），但歷程仍在（歷史事實）</summary>
    [Fact]
    public void GetUserDetail_已停用的使用者_可見主機為空但歷程保留()
    {
        var user = SetupVisibilityFixture();
        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = "SRV-B", IssueKey = "App|disk|153|1", IssueLabel = "disk 153",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.InProgress,
            CreatedAt = DateTime.Now, CreatedByAccount = "DOMAIN\\admin"
        });
        user.Active = false;
        _users.Upsert(user);

        var detail = Create().GetUserDetail(user.UserId);

        Assert.Empty(detail.VisibleHosts);
        Assert.Empty(detail.Capabilities);
        Assert.Single(detail.AssignmentHistory);
    }

    [Fact]
    public void GetUsers_帶出上次登入時間()
    {
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang" });
        Assert.Null(Create().GetUsers().Single().LastLoginAt);

        _users.TouchLogin(user.UserId, new DateTime(2026, 8, 5, 9, 30, 0));

        Assert.Equal(new DateTime(2026, 8, 5, 9, 30, 0), Create().GetUsers().Single().LastLoginAt);
    }

    /// <summary>
    /// 編輯使用者不可清掉上次登入時間——SaveUser 建構全新的 WebUser 交給 Upsert，
    /// 若 LastLoginAt 也走 Upsert 的逐欄覆寫，每次改個顯示名稱就會把它清成 null。
    /// </summary>
    [Fact]
    public void SaveUser_不覆寫上次登入時間()
    {
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "舊名" });
        _users.TouchLogin(user.UserId, new DateTime(2026, 8, 5, 9, 30, 0));

        Create().SaveUser(new SaveUserRequest { Account = "DOMAIN\\wang", DisplayName = "新名", Active = true });

        Assert.Equal(new DateTime(2026, 8, 5, 9, 30, 0), _users.Get(user.UserId)!.LastLoginAt);
    }
}
