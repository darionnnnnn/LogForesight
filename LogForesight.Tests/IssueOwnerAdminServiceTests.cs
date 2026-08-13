using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題負責人管理（回饋十八輪批次F，「問題負責人」頁）：輸入驗證、逐欄複製、
/// 近期出現統計的合併，以及稽核摘要。
/// </summary>
public class IssueOwnerAdminServiceTests
{
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly FakeUserStore _users = new();
    private readonly RecordingAuditService _audit = new();

    private IssueOwnerAdminService Create(string account = "DOMAIN\\admin") =>
        new(_issueOwners, _issueAggregates, _users, _audit, FakeCurrentUser.ForAccount(account));

    private WebUser AddUser(string account, string displayName) =>
        _users.Upsert(new WebUser { Account = account, DisplayName = displayName, Active = true });

    [Fact]
    public void Upsert_來源留空時擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().Upsert(new SaveIssueOwnerRequest { SourceName = "  ", EventId = 153 }));

        Assert.Contains("來源", ex.Message);
    }

    [Fact]
    public void Upsert_EventId不合法時擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 0 }));

        Assert.Contains("Event ID", ex.Message);
    }

    [Fact]
    public void Upsert_負責人不存在時擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { 999 } }));

        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Upsert_新增後可查得到且OwnerNames正確組成()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");

        var saved = Create().Upsert(new SaveIssueOwnerRequest
        {
            SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId }, Note = "機房硬體"
        });

        Assert.Equal("disk", saved.SourceName);
        Assert.Equal(153, saved.EventId);
        Assert.Equal(new List<string> { "OOO(DOMAIN\\owner)" }, saved.OwnerNames);
        Assert.Equal("機房硬體", saved.Note);
        Assert.NotNull(saved.UpdatedAt);
    }

    [Fact]
    public void Upsert_記錄操作者帳號()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");

        var saved = Create("DOMAIN\\admin1").Upsert(new SaveIssueOwnerRequest
        {
            SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId }
        });

        Assert.Equal("DOMAIN\\admin1", saved.UpdatedByAccount);
    }

    /// <summary>更新走逐欄複製，同 SentinelStore／HostAdminService 的既有慣例——
    /// 漏抄一欄的症狀是「新增存得進去、編輯被靜默還原」。</summary>
    [Fact]
    public void Upsert_更新既有規則_全部欄位跟著改()
    {
        var a = AddUser("DOMAIN\\a", "AAA");
        var b = AddUser("DOMAIN\\b", "BBB");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { a.UserId }, Note = "舊備註" });

        var updated = Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { b.UserId }, Note = "新備註" });

        Assert.Equal(new List<long> { b.UserId }, updated.OwnerUserIds);
        Assert.Equal("新備註", updated.Note);
        Assert.Single(_issueOwners.GetAll());   // 視為同一筆更新，不是新增
    }

    [Fact]
    public void Upsert_Source不分大小寫視為同一筆()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "Disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });

        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "DISK", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });

        Assert.Single(_issueOwners.GetAll());
    }

    [Fact]
    public void List_附上近期出現統計()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.Result = new List<IssueAggregate>
        {
            new() { Source = "disk", EventId = 153, HostCount = 7, LastSeen = new DateTime(2026, 8, 1) }
        };

        var list = Create().List();

        var dto = Assert.Single(list);
        Assert.Equal(7, dto.RecentHostCount);
        Assert.Equal(new DateTime(2026, 8, 1), dto.RecentLastSeen);
    }

    /// <summary>沒有近期出現紀錄的規則（例如手動預先指派尚未出現過的問題）回 null，
    /// 不是 0——0 代表「查得到但沒出現」，null 代表「查無資料」，語意不同。</summary>
    [Fact]
    public void List_沒有近期出現紀錄時回null()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.Result = new List<IssueAggregate>();

        var dto = Assert.Single(Create().List());

        Assert.Null(dto.RecentHostCount);
        Assert.Null(dto.RecentLastSeen);
    }

    [Fact]
    public void RecentIssues_已指派的問題標示HasOwner()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.Result = new List<IssueAggregate>
        {
            new() { Source = "disk", EventId = 153, HostCount = 3, LastSeen = DateTime.Today },
            new() { Source = "network", EventId = 999, HostCount = 1, LastSeen = DateTime.Today }
        };

        var options = Create().RecentIssues();

        Assert.True(options.Single(o => o.EventId == 153).HasOwner);
        Assert.False(options.Single(o => o.EventId == 999).HasOwner);
    }

    [Fact]
    public void RecentIssues_依主機數由多到少排序()
    {
        _issueAggregates.Result = new List<IssueAggregate>
        {
            new() { Source = "small", EventId = 1, HostCount = 1, LastSeen = DateTime.Today },
            new() { Source = "big", EventId = 2, HostCount = 9, LastSeen = DateTime.Today }
        };

        var options = Create().RecentIssues();

        Assert.Equal("big", options[0].SourceName);
        Assert.Equal("small", options[1].SourceName);
    }

    [Fact]
    public void Delete_移除規則()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });

        Create().Delete("DISK", 153);   // 不分大小寫

        Assert.Empty(_issueOwners.GetAll());
    }

    [Fact]
    public void Delete_規則不存在時擋下()
    {
        Assert.Throws<DomainException>(() => Create().Delete("disk", 153));
    }

    [Fact]
    public void Upsert與Delete_皆寫入稽核()
    {
        var user = AddUser("DOMAIN\\owner", "OOO");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        Create().Delete("disk", 153);

        Assert.Contains(_audit.Entries, r => r.Action == AuditActions.IssueOwnerUpdate);
        Assert.Contains(_audit.Entries, r => r.Action == AuditActions.IssueOwnerDelete);
    }
}
