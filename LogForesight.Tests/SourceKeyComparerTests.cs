using System.Diagnostics;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class SourceKeyComparerTests
{
    [Fact]
    public void Unicode來源_Equals與GetHashCode使用同一個正規化鍵()
    {
        const string longSource = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var truncatedVariant = longSource + "x";

        Assert.True(SourceKeyComparer.Instance.Equals("ſ", "S"));
        Assert.Equal(
            SourceKeyComparer.Instance.GetHashCode("ſ"),
            SourceKeyComparer.Instance.GetHashCode("S"));
        Assert.True(SourceKeyComparer.Instance.Equals(longSource, truncatedVariant));

        var keys = new HashSet<string>(SourceKeyComparer.Instance) { "ſ", "S", longSource, truncatedVariant };
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void 問題負責與靜音_同一來源正規化後命中()
    {
        var today = DateTime.Today;
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(new IssueProfile
        {
            SourceName = "ſ",
            EventId = 153,
            OwnerUserIds = new List<long> { 7 },
            Mutes = new List<MuteInterval> { new() { From = today, To = today } }
        });

        Assert.NotNull(owners.Get("S", 153));

        var exclusion = IssueExclusion.From(owners.GetAll(), today);
        Assert.True(exclusion.IsCurrentlyMuted("S", 153));
        Assert.True(exclusion.IsMuted("S", 153, today));

        var mute = new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.IssueMute,
            SourceName = "ſ",
            EventId = 153,
            MuteFrom = today,
            MuteTo = today
        };
        Assert.True(SuppressionFilter.MuteMatches(mute, "S", 153, today));
    }

    [Fact]
    public void 問題負責人授權_只傳入正規化來源且不擴大可見主機()
    {
        var users = new FakeUserStore();
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\owner", Active = true });
        var hosts = new FakeHostStore();
        var ownedHost = hosts.Upsert(new WebHost { HostName = "SRV-OWNED", Active = true });
        var unrelatedHost = hosts.Upsert(new WebHost { HostName = "SRV-UNRELATED", Active = true });
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(new IssueProfile { SourceName = "ſ", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        var aggregates = new FakeIssueAggregateQuery { HostIdsForResult = new HashSet<long> { ownedHost.HostId } };
        var service = new VisibilityService(
            FakeCurrentUser.ForUser(user.UserId), users, new FakeUserGroupStore(), new FakeGroupAccessStore(),
            hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore(),
            issueOwners: owners, issueAggregates: aggregates);

        var visible = service.GetVisibleHostIds();

        Assert.Equal(new[] { ("S", 153) }, aggregates.LastHostIdsForCall!.Value.Issues);
        Assert.Contains(ownedHost.HostId, visible);
        Assert.DoesNotContain(unrelatedHost.HostId, visible);
    }

    [Fact]
    public void 簽章字面不變且每日去重不吞掉不同完整簽章()
    {
        var hosts = new FakeHostStore();
        const string host = "SRV-A";
        hosts.Upsert(new WebHost { HostName = host, Active = true });
        var cases = new FakeIssueCaseStore();
        var handlings = new FakeIssueHandlingStore();
        var records = new FakeAnalysisRecordQuery();
        var logs = new FakeHandlingStore();
        var profiles = new FakeIssueOwnerStore();
        var coordinator = new IssueCaseCoordinator(cases, handlings, logs, records, hosts, profiles);
        var first = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error
        };
        var second = new LogIssueSignature
        {
            LogName = "Application", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error
        };

        var firstKey = IssueSignatureKey.For(first);
        Assert.Equal("System|disk|153|1", firstKey);

        coordinator.BuildCase(host, firstKey, "disk 153", DateTime.Today.AddDays(-1),
            handlerId: 7, note: null, dueDate: null, actorId: 7, actorAccount: "owner", occurredAt: DateTime.Now);
        var result = coordinator.AttachNewDay(host, DateTime.Today,
            new[] { first, first, second }, DateTime.Now);

        Assert.Equal(1, result.AttachedCount);
        var attached = handlings.GetForDay(host, DateTime.Today);
        Assert.Single(attached);
        Assert.Equal(firstKey, attached[0].IssueKey);
        Assert.Equal(new[] { IssueSignatureKey.For(second) }, result.Unassigned.Select(IssueSignatureKey.For));
    }
}
