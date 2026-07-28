using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/WEB-FEEDBACK-PLAN.md #7：一次新增多個帳號。UserAdminService 先前沒有專屬測試檔，
/// 這裡順帶補上既有 SaveUser／SetUserGroups 的基本案例，再測批次新增。
/// </summary>
public class UserAdminServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeAuditService _audit = new();

    private UserAdminService Create() => new(_users, _groups, _audit);

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
}
