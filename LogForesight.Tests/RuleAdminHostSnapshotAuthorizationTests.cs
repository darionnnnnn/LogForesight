using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class RuleAdminHostSnapshotAuthorizationTests
{
    [Fact]
    public void RealHostStoreAndVisibility_ViewAllCanPreviewWithInactiveMergedTombstone()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new HostStore(fx.Blob("hosts"));
        var active = hosts.Upsert(new WebHost { HostName = "preview-active", Active = true });
        var tombstone = hosts.Upsert(new WebHost
        {
            HostName = "preview-merged", Active = false, MergedInto = active.HostId
        });
        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var visibility = CreateVisibility(currentUser, hosts);
        var service = CreateService(fx, hosts, visibility, currentUser);

        var preview = service.PreviewDiskTrend(Request());

        Assert.Contains(tombstone.HostId, hosts.CapturePrtgSnapshot().Hosts.Select(host => host.HostId));
        Assert.Equal(0, preview.DateSensorAssessmentRowCount);
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public void RealHostStoreAndVisibility_ServerAdminWithoutViewAllRemainsDenied()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new HostStore(fx.Blob("hosts"));
        hosts.Upsert(new WebHost { HostName = "preview-active", Active = true });
        hosts.Upsert(new WebHost { HostName = "preview-disabled", Active = false });
        var currentUser = FakeCurrentUser.ServerAdmin();
        Assert.True(currentUser.Has(Capability.Maintain));
        Assert.False(currentUser.Has(Capability.ViewAll));
        var service = CreateService(fx, hosts, CreateVisibility(currentUser, hosts), currentUser);

        var error = Assert.Throws<DomainException>(() => service.PreviewDiskTrend(Request()));

        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
    }

    [Fact]
    public void RealHostStoreAndVisibilitySubsetCannotPreviewWholeInventory()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new HostStore(fx.Blob("hosts"));
        hosts.Upsert(new WebHost { HostName = "group-visible", Active = true, GroupIds = new List<long> { 45 } });
        hosts.Upsert(new WebHost { HostName = "outside-group", Active = true });
        hosts.Upsert(new WebHost { HostName = "merged-tombstone", Active = false, MergedInto = 1 });
        var users = new FakeUserStore();
        var group = new FakeUserGroupStore().Upsert(new UserGroup { GroupName = "limited-users", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "limited", Active = true, GroupIds = new List<long> { group.GroupId } });
        var groups = new FakeUserGroupStore();
        groups.Upsert(group);
        var access = new FakeGroupAccessStore();
        access.SetForUserGroup(group.GroupId, new[] { 45L });
        var currentUser = FakeCurrentUser.ForUser(user.UserId);
        var visibility = new VisibilityService(currentUser, users, groups, access, hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        Assert.Single(visibility.GetVisibleHostIds());
        var service = CreateService(fx, hosts, visibility, currentUser);

        var error = Assert.Throws<DomainException>(() => service.PreviewDiskTrend(Request()));

        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
    }

    [Fact]
    public void PreviewRejectsHostVersionChangeAfterSnapshotCapture()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new HostStore(fx.Blob("hosts"));
        var active = hosts.Upsert(new WebHost { HostName = "preview-active", Active = true });
        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var visibility = new MutatingVisibilityService(hosts, new[] { active.HostId });
        var service = CreateService(fx, hosts, visibility, currentUser);

        var error = Assert.Throws<DomainException>(() => service.PreviewDiskTrend(Request()));

        Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
    }

    [Fact]
    public void PreviewRechecksGroupAuthorizationBeforeReturningReadOnlyResult()
    {
        using var fx = new EfSqliteFixture();
        var hosts = new HostStore(fx.Blob("hosts"));
        hosts.Upsert(new WebHost { HostName = "grant-visible-1", Active = true, GroupIds = new List<long> { 45 } });
        hosts.Upsert(new WebHost { HostName = "grant-visible-2", Active = true, GroupIds = new List<long> { 45 } });
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var group = groups.Upsert(new UserGroup { GroupName = "preview-limited", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "preview-limited", Active = true, GroupIds = new List<long> { group.GroupId } });
        var currentUser = FakeCurrentUser.ForUser(user.UserId);
        var storedAccess = new GroupAccessStore(fx.Blob("group_access"));
        storedAccess.SetForUserGroup(group.GroupId, new[] { 45L });
        var access = new RevokeAfterInitialReadGroupAccessStore(storedAccess, group.GroupId);
        var visibility = new VisibilityService(currentUser, users, groups, access, hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        var audit = new RecordingAuditService();
        var service = CreateService(fx, hosts, visibility, currentUser, audit);

        var error = Assert.Throws<DomainException>(() => service.PreviewDiskTrend(Request()));

        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
        Assert.Equal(2, access.ReadCount);
        Assert.Empty(audit.Entries);
        using var db = fx.NewContext();
        Assert.Empty(db.PrtgValues);
        Assert.Empty(db.PrtgHostMaps);
    }

    private static VisibilityService CreateVisibility(ICurrentUser currentUser, IHostStore hosts) =>
        new(currentUser, new FakeUserStore(), new FakeUserGroupStore(), new FakeGroupAccessStore(), hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());

    private static RuleAdminService CreateService(EfSqliteFixture fx, IHostStore hosts,
        IVisibilityService visibility, ICurrentUser currentUser, RecordingAuditService? audit = null)
    {
        var rules = KnownIssueSeed.CreateRules();
        var ruleStore = new FakeRuleStore
        {
            Content = new RuleFileContent { SeedVersion = KnownIssueSeed.Version, Rules = rules }
        };
        var seedStore = new FakeRuleSeedStore();
        seedStore.Sync(rules, KnownIssueSeed.Version);
        var settings = new FakeSystemSettingsStore();
        var assessment = new PrtgDiskAssessmentService(new EfPrtgStore(fx.NewContext), hosts, settings,
            new PrtgDiskSemanticEvidenceStore(fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        return new RuleAdminService(ruleStore, seedStore, new FakeSuppressionStore(), new FakeUserStore(),
            currentUser, audit ?? new RecordingAuditService(), new FakeHostGroupStore(), hosts,
            new FakeIssueAggregateQuery(), assessment, settings, visibility);
    }

    private static DiskTrendRulePreviewRequest Request() => new()
    {
        FromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
        ThroughDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
        Rule = new SaveRuleRequest
        {
            Id = "draft-disk-preview", Enabled = false, Platform = "prtg", PrtgRuleCode = "disk_free_trend",
            PrtgSensorCategory = "disk", PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional,
            Category = "Storage", Severity = "High", Description = "preview only", CountThreshold = 1,
            PlainExplanation = "磁碟可用空間持續下降。", Impact = "可能耗盡。",
            LikelyCauses = new() { "容量不足" }, NextSteps = new() { "確認磁碟容量" }
        }
    };

    private sealed class MutatingVisibilityService(IHostStore hosts, IEnumerable<long> visibleHostIds) : IVisibilityService
    {
        private readonly HashSet<long> _visible = visibleHostIds.ToHashSet();
        private bool _mutated;

        public IReadOnlySet<long> GetVisibleHostIds()
        {
            if (!_mutated)
            {
                _mutated = true;
                hosts.Upsert(new WebHost { HostName = "added-after-snapshot", Active = true });
            }
            return _visible;
        }

        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
        public List<WebHost> GetVisibleHosts() => new();
        public void EnsureVisible(long hostId) { if (!_visible.Contains(hostId)) throw new InvalidOperationException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => Array.Empty<string>();
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    }

    private sealed class RevokeAfterInitialReadGroupAccessStore(IGroupAccessStore inner, long userGroupId) : IGroupAccessStore
    {
        private bool _revoked;
        public int ReadCount { get; private set; }

        public List<GroupAccess> GetAll()
        {
            ReadCount++;
            var accesses = inner.GetAll();
            if (!_revoked)
            {
                _revoked = true;
                inner.SetForUserGroup(userGroupId, Array.Empty<long>());
            }
            return accesses;
        }

        public void SetForUserGroup(long id, IEnumerable<long> hostGroupIds) => inner.SetForUserGroup(id, hostGroupIds);
        public void ReplaceAll(IEnumerable<GroupAccess> accesses) => inner.ReplaceAll(accesses);
    }
}
