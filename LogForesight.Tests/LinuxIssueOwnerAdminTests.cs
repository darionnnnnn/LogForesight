using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Linux 問題負責人管理（EventId = 0）：驗證 EventId = 0 可正常建立／查詢／設定結論／刪除，
/// 負數 EventId 仍被拒絕，且 DisplayLabel 重用 SourceEventLabel 概念不出現「(0)」。
/// </summary>
public class LinuxIssueOwnerAdminTests
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
    public void Upsert_EventId為0時可以成功建立規則_支援Linux問題()
    {
        var user = AddUser("DOMAIN\\linuxadmin", "LinuxAdmin");

        var saved = Create().Upsert(new SaveIssueOwnerRequest
        {
            SourceName = "sshd",
            EventId = 0,
            OwnerUserIds = new List<long> { user.UserId },
            Note = "Linux SSH 服務負責人"
        });

        Assert.Equal("sshd", saved.SourceName);
        Assert.Equal(0, saved.EventId);
        Assert.Equal("sshd", saved.DisplayLabel);
        Assert.Equal(new List<string> { "LinuxAdmin(DOMAIN\\linuxadmin)" }, saved.OwnerNames);
        Assert.Equal("Linux SSH 服務負責人", saved.Note);
        Assert.NotNull(saved.UpdatedAt);

        var inStore = _issueOwners.Get("sshd", 0);
        Assert.NotNull(inStore);
        Assert.Equal("sshd", inStore.SourceName);
        Assert.Equal(0, inStore.EventId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Upsert_EventId為負數時被拒絕(int invalidEventId)
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().Upsert(new SaveIssueOwnerRequest { SourceName = "sshd", EventId = invalidEventId }));

        Assert.Contains("Event ID", ex.Message);
    }

    [Fact]
    public void RecentIssues_EventId為0時正確產生DisplayLabel且不含括號0()
    {
        _issueAggregates.Result = new List<IssueAggregate>
        {
            new()
            {
                Source = "sshd",
                EventId = 0,
                HostCount = 5,
                LastSeen = DateTime.Today,
                IssueKeys = new List<string> { "Linux/sshd|sshd|0|1|ssh-bruteforce" }
            },
            new()
            {
                Source = "cron",
                EventId = 0,
                HostCount = 3,
                LastSeen = DateTime.Today,
                IssueKeys = new List<string> { "Linux/cron|cron|0|1" }
            },
            new()
            {
                Source = "disk",
                EventId = 153,
                HostCount = 2,
                LastSeen = DateTime.Today,
                IssueKeys = new List<string> { "System|disk|153|1" }
            }
        };

        var options = Create().RecentIssues();

        var sshd = options.Single(o => o.SourceName == "sshd");
        Assert.Equal("sshd（ssh-bruteforce）", sshd.DisplayLabel);
        Assert.DoesNotContain("(0)", sshd.DisplayLabel);

        var cron = options.Single(o => o.SourceName == "cron");
        Assert.Equal("cron", cron.DisplayLabel);
        Assert.DoesNotContain("(0)", cron.DisplayLabel);

        var disk = options.Single(o => o.SourceName == "disk");
        Assert.Equal("disk (153)", disk.DisplayLabel);
    }

    [Fact]
    public void SetConclusion_EventId為0時可以成功設定機房結論()
    {
        var saved = Create("DOMAIN\\admin1").SetConclusion("sshd", 0,
            new SetIssueConclusionRequest { Status = IssueHandlingStatuses.KnownNoise, Note = "Linux SSH 已知雜訊", AutoApply = true });

        Assert.Equal("sshd", saved.SourceName);
        Assert.Equal(0, saved.EventId);
        Assert.Equal("sshd", saved.DisplayLabel);
        Assert.Equal(IssueHandlingStatuses.KnownNoise, saved.ConclusionStatus);
        Assert.Equal("已知雜訊", saved.ConclusionStatusText);
        Assert.Equal("Linux SSH 已知雜訊", saved.ConclusionNote);
        Assert.True(saved.AutoApply);

        var inStore = _issueOwners.Get("sshd", 0);
        Assert.NotNull(inStore);
        Assert.Equal(IssueHandlingStatuses.KnownNoise, inStore.ConclusionStatus);
    }

    [Fact]
    public void ClearConclusion_EventId為0時可以成功解除機房結論()
    {
        Create().SetConclusion("sshd", 0,
            new SetIssueConclusionRequest { Status = IssueHandlingStatuses.KnownNoise, Note = "Linux SSH 已知雜訊", AutoApply = true });

        var cleared = Create().ClearConclusion("sshd", 0);

        Assert.Null(cleared.ConclusionStatus);
        Assert.Null(cleared.ConclusionStatusText);
        Assert.False(cleared.AutoApply);
    }

    [Fact]
    public void Delete_EventId為0時可以成功刪除規則()
    {
        var user = AddUser("DOMAIN\\linuxadmin", "LinuxAdmin");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "sshd", EventId = 0, OwnerUserIds = new List<long> { user.UserId } });

        Create().Delete("sshd", 0);

        Assert.Null(_issueOwners.Get("sshd", 0));
    }

    [Fact]
    public void List_EventId為0時DisplayLabel正確()
    {
        var user = AddUser("DOMAIN\\linuxadmin", "LinuxAdmin");
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "sshd", EventId = 0, OwnerUserIds = new List<long> { user.UserId } });
        Create().Upsert(new SaveIssueOwnerRequest { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });

        var list = Create().List();

        var sshd = list.Single(r => r.SourceName == "sshd");
        Assert.Equal(0, sshd.EventId);
        Assert.Equal("sshd", sshd.DisplayLabel);

        var disk = list.Single(r => r.SourceName == "disk");
        Assert.Equal(153, disk.EventId);
        Assert.Equal("disk (153)", disk.DisplayLabel);
    }
}
