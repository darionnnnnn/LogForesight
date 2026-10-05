using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgHostVisibilitySnapshotTests
{
    [Fact]
    public void RealHostStoreViewAllSnapshotExcludesInactiveMergedTombstonesAndKeepsActiveHost()
    {
        using var fixture = new EfSqliteFixture();
        var hosts = new HostStore(fixture.Blob("viewall-host-snapshot"));
        var active = hosts.Upsert(new WebHost { HostName = "active-host" });
        var tombstone = hosts.Upsert(new WebHost { HostName = "old-host", Active = false, MergedInto = active.HostId });
        var snapshot = hosts.CapturePrtgSnapshot();
        var visibility = new VisibilityService(FakeCurrentUser.WithCapabilities(Capability.ViewAll),
            new FakeUserStore(), new FakeUserGroupStore(), new FakeGroupAccessStore(), hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());

        var visible = visibility.GetVisibleHostIds(snapshot);

        Assert.Contains(active.HostId, visible);
        Assert.DoesNotContain(tombstone.HostId, visible);
        Assert.Equal(2, snapshot.Hosts.Count);
        Assert.Contains(snapshot.Hosts, host => host.HostId == tombstone.HostId && !host.Active && host.MergedInto == active.HostId);
    }

    [Fact]
    public void OneCapturedSnapshotFeedsGroupOwnerIssueOwnerNamesAndCaseOnlyChecks()
    {
        var users = new FakeUserStore();
        var userGroups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var hosts = new FakeHostStore();
        var cases = new FakeIssueCaseStore();
        var issueOwners = new FakeIssueOwnerStore();
        var issueAggregates = new FakeIssueAggregateQuery();
        var settings = new FakeSystemSettingsStore();
        var userGroup = userGroups.Upsert(new UserGroup { GroupName = "department", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "user", GroupIds = new() { userGroup.GroupId } });
        var groupHost = hosts.Upsert(new WebHost { HostName = "group-host", GroupIds = new() { 10 } });
        var ownerHost = hosts.Upsert(new WebHost { HostName = "owner-host", OwnerUserIds = new() { user.UserId } });
        var issueHost = hosts.Upsert(new WebHost { HostName = "issue-host" });
        var inactive = hosts.Upsert(new WebHost { HostName = "inactive-host", Active = false, MergedInto = groupHost.HostId });
        var hidden = hosts.Upsert(new WebHost { HostName = "hidden-host" });
        var caseOnly = hosts.Upsert(new WebHost { HostName = "case-only-host" });
        access.ReplaceAll(new[] { new GroupAccess { UserGroupId = userGroup.GroupId, HostGroupId = 10 } });
        issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 42, OwnerUserIds = new() { user.UserId } });
        issueAggregates.HostIdsForResult = new HashSet<long> { issueHost.HostId, inactive.HostId };
        cases.Save(new IssueCase
        {
            CaseId = "case-only", HostName = caseOnly.HostName, IssueKey = "App|disk|42|1",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.InProgress
        });

        hosts.ResetCallCounts();
        var snapshot = ((IHostStore)hosts).CapturePrtgSnapshot();
        var service = new VisibilityService(FakeCurrentUser.ForUser(user.UserId), users, userGroups,
            access, hosts, cases, settings, issueOwners, issueAggregates);
        IVisibilityService visibilityContract = service;
        var visible = visibilityContract.GetVisibleHostIds(snapshot);

        Assert.Contains(groupHost.HostId, visible);
        Assert.Contains(ownerHost.HostId, visible);
        Assert.Contains(issueHost.HostId, visible);
        Assert.DoesNotContain(inactive.HostId, visible);
        Assert.DoesNotContain(hidden.HostId, visible);
        Assert.DoesNotContain(caseOnly.HostId, visible);
        service.EnsureVisible(caseOnly.HostId);
        Assert.True(service.IsCaseGrantOnly(caseOnly.HostId));
        Assert.Contains("App|disk|42|1", service.GetIssueKeyRestriction(caseOnly.HostId)!);
        Assert.Equal(new[] { "group-host", "issue-host", "owner-host" },
            service.GetVisibleHosts().Select(host => host.HostName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(1, hosts.GetAllCallCount); // one compatibility capture, no resolver/name re-read
        Assert.Equal(0, hosts.GetCallCount);     // IsCaseGrantOnly resolves its name from that same projection
    }

    [Fact]
    public void CallerSnapshotRecomputesInsteadOfReusingOlderVisibilitySet()
    {
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var hosts = new FakeHostStore();
        var userGroup = groups.Upsert(new UserGroup { GroupName = "department", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "user", GroupIds = new() { userGroup.GroupId } });
        var host = hosts.Upsert(new WebHost { HostName = "moving-host" });
        var service = new VisibilityService(FakeCurrentUser.ForUser(user.UserId), users, groups, access,
            hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        var first = ((IHostStore)hosts).CapturePrtgSnapshot();
        Assert.DoesNotContain(host.HostId, service.GetVisibleHostIds(first));

        access.ReplaceAll(new[] { new GroupAccess { UserGroupId = userGroup.GroupId, HostGroupId = 17 } });
        host.GroupIds = new() { 17 };
        hosts.Upsert(host);
        var second = ((IHostStore)hosts).CapturePrtgSnapshot();
        Assert.Contains(host.HostId, service.GetVisibleHostIds(second));
        Assert.Equal(new[] { "moving-host" }, service.GetVisibleHosts().Select(item => item.HostName));
    }

    [Fact]
    public void ViewAllServerAdminAnonymousAndInactiveUserKeepTheirExistingBoundaries()
    {
        var hosts = new FakeHostStore();
        var active = hosts.Upsert(new WebHost { HostName = "active" });
        var merged = hosts.Upsert(new WebHost { HostName = "merged", Active = false, MergedInto = active.HostId });
        var snapshot = ((IHostStore)hosts).CapturePrtgSnapshot();
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        VisibilityService Create(ICurrentUser current) => new(current, users, groups, access, hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());

        Assert.Equal(new[] { active.HostId }, Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds(snapshot));
        Assert.Empty(Create(FakeCurrentUser.ServerAdmin()).GetVisibleHostIds(snapshot));
        Assert.Empty(Create(FakeCurrentUser.Anonymous()).GetVisibleHostIds(snapshot));
        var inactiveUser = users.Upsert(new WebUser { Account = "inactive", Active = false });
        Assert.Empty(Create(FakeCurrentUser.ForUser(inactiveUser.UserId)).GetVisibleHostIds(snapshot));
        Assert.DoesNotContain(merged.HostId, Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds(snapshot));
    }
}
