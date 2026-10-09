using System.Reflection;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskVisibilityAuthorizationTests
{
    [Fact]
    public void ServerAdminMaintainCapabilityCannotReadAllHostDiskReadinessOrPreview()
    {
        using var fx = new EfSqliteFixture();
        var hosts = CreateHosts(
            new WebHost { HostId = 1, HostName = "visible-none-1", Active = true },
            new WebHost { HostId = 2, HostName = "visible-none-2", Active = true });
        var currentUser = FakeCurrentUser.ServerAdmin();
        var visibility = new VisibilityService(currentUser, new FakeUserStore(), new FakeUserGroupStore(),
            new FakeGroupAccessStore(), hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());

        Assert.True(currentUser.Has(Capability.Maintain));
        Assert.Empty(visibility.GetVisibleHostIds());
        AssertHasMaintainGate(typeof(PrtgDiskReadinessController));
        AssertHasMaintainGate(typeof(RulesController));
        AssertMaintainFilterPasses(typeof(PrtgDiskReadinessController), currentUser, "/api/prtg/disk-readiness");
        AssertMaintainFilterPasses(typeof(RulesController), currentUser, "/api/rules/disk-trend-preview");

        using var provider = CreateServiceProvider(fx, hosts, visibility, currentUser);
        var readinessController = provider.GetRequiredService<PrtgDiskReadinessController>();
        var readinessError = Assert.Throws<DomainException>(() => readinessController.Get());
        Assert.Equal(ApiErrorCodes.Forbidden, readinessError.Code);

        var rulesController = provider.GetRequiredService<RulesController>();
        rulesController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var previewError = Assert.Throws<DomainException>(() => rulesController.PreviewDiskTrend(new()));
        Assert.Equal(ApiErrorCodes.Forbidden, previewError.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void QueryAndPreviewRejectSubsetOrCaseGrantOnlyHost(bool includeCaseGrant, bool emptyVisible)
    {
        using var fx = new EfSqliteFixture();
        var hosts = CreateHosts(
            new WebHost { HostId = 1, HostName = "visible", Active = true },
            new WebHost { HostId = 2, HostName = "hidden", Active = true });
        var visibleIds = emptyVisible ? Array.Empty<long>() : includeCaseGrant ? new[] { 1L, 2L } : new[] { 1L };
        var caseOnly = includeCaseGrant ? new[] { 2L } : Array.Empty<long>();
        var visibility = new FixedVisibilityService(visibleIds, caseOnly);

        using var provider = CreateServiceProvider(fx, hosts, visibility,
            FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var readiness = provider.GetRequiredService<PrtgDiskReadinessController>();
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => readiness.Get()).Code);
        var rules = provider.GetRequiredService<RulesController>();
        rules.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => rules.PreviewDiskTrend(new())).Code);
    }

    [Fact]
    public void MissingVisibilityDependencyFailsClosedInControllerQueryAndRulePreview()
    {
        using var fx = new EfSqliteFixture();
        var hosts = CreateHosts(new WebHost { HostId = 1, HostName = "host", Active = true });
        using var provider = CreateServiceProvider(fx, hosts, visibility: null,
            currentUser: FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var controller = provider.GetRequiredService<PrtgDiskReadinessController>();
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => controller.Get()).Code);

        var rulesController = provider.GetRequiredService<RulesController>();
        rulesController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => rulesController.PreviewDiskTrend(new())).Code);
    }

    [Fact]
    public void MissingHostStoreDependencyFailsClosedBeforeReadinessOrTrendPreview()
    {
        using var fx = new EfSqliteFixture();
        var visibility = new FixedVisibilityService(new[] { 1L });
        var controller = new PrtgDiskReadinessController(new EfPrtgStore(fx.NewContext), null!,
            new SystemSettingsStore(fx.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(fx.Blob(PrtgDiskVerificationResultStore.BlobKey)), visibility);
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => controller.Get()).Code);

        var service = new RuleAdminService(new FakeRuleStore(), new FakeRuleSeedStore(), new FakeSuppressionStore(),
            new FakeUserStore(), FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(),
            new FakeHostGroupStore(), null!, new FakeIssueAggregateQuery(), visibility: visibility);
        var rulesController = new RulesController(service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => rulesController.PreviewDiskTrend(new())).Code);
    }

    [Fact]
    public void FullVisibleMaintainerCanReadEmptyReadinessAndServiceCheckIsBeforeAnyStoreRead()
    {
        using var fx = new EfSqliteFixture();
        var hosts = CreateHosts(new WebHost { HostId = 1, HostName = "all-visible", Active = true });
        var visibility = new FixedVisibilityService(new[] { 1L });
        using var provider = CreateServiceProvider(fx, hosts, visibility, FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var page = provider.GetRequiredService<PrtgDiskReadinessController>().Get().Data!;
        Assert.Equal(0, page.CandidateSensors);
        Assert.Equal(0, page.GlobalMirrorCandidates);

        var calls = 0;
        var settings = new SystemSettingsStore(fx.Blob("system_settings"));
        var emptyVisibility = new FixedVisibilityService(Array.Empty<long>());
        var query = new PrtgDiskReadinessQueryService(new EfPrtgStore(() =>
        {
            calls++;
            return fx.NewContext();
        }), hosts, settings, new PrtgDiskSemanticEvidenceStore(fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(fx.Blob(PrtgDiskVerificationResultStore.BlobKey)), emptyVisibility);
        Assert.Throws<DomainException>(() => query.Get(1, 10, new DateTime(2026, 9, 24)));
        Assert.Equal(0, calls);
    }

    private static ServiceProvider CreateServiceProvider(EfSqliteFixture fx, IHostStore hosts,
        IVisibilityService? visibility, ICurrentUser currentUser)
    {
        var services = new ServiceCollection();
        var store = new EfPrtgStore(fx.NewContext);
        var settings = new SystemSettingsStore(fx.Blob("system_settings"));
        services.AddSingleton(store);
        services.AddSingleton<IHostStore>(hosts);
        services.AddSingleton<ISystemSettingsStore>(settings);
        services.AddSingleton(new PrtgDiskSemanticEvidenceStore(fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)));
        services.AddSingleton(new PrtgDiskVerificationResultStore(fx.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        if (visibility is not null) services.AddSingleton<IVisibilityService>(visibility);
        services.AddSingleton<ICurrentUser>(currentUser);
        services.AddSingleton<IAuditService>(new RecordingAuditService());
        services.AddSingleton<PrtgDiskReadinessController>();
        services.AddSingleton<RulesController>();
        services.AddSingleton<RuleAdminService>(sp =>
        {
            var ruleSettings = new FakeSystemSettingsStore();
            var assessment = new PrtgDiskAssessmentService(store, hosts, ruleSettings,
                sp.GetRequiredService<PrtgDiskSemanticEvidenceStore>(), sp.GetRequiredService<PrtgDiskVerificationResultStore>());
            return new RuleAdminService(new FakeRuleStore(), new FakeRuleSeedStore(), new FakeSuppressionStore(),
                new FakeUserStore(), currentUser, sp.GetRequiredService<IAuditService>(), new FakeHostGroupStore(),
                hosts, new FakeIssueAggregateQuery(), assessment, ruleSettings, sp.GetService<IVisibilityService>());
        });
        return services.BuildServiceProvider();
    }

    private static FakeHostStore CreateHosts(params WebHost[] hosts)
    {
        var store = new FakeHostStore();
        foreach (var host in hosts) store.Upsert(host);
        return store;
    }

    private static void AssertHasMaintainGate(Type controllerType)
    {
        var attributes = controllerType.GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().ToArray();
        Assert.Contains(attributes, attribute => ((Capability[])attribute.Arguments![0]).Contains(Capability.Maintain));
    }

    private static void AssertMaintainFilterPasses(Type controllerType, ICurrentUser currentUser, string path)
    {
        var required = controllerType.GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().Single().Arguments!.Cast<Capability[]>().Single();
        var context = new AuthorizationFilterContext(new ActionContext(new DefaultHttpContext
        {
            Request = { Path = path }
        }, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()), []);
        new PermissionFilter(required, currentUser, new RecordingAuditService()).OnAuthorization(context);
        Assert.Null(context.Result);
    }
}

internal sealed class FixedVisibilityService(IEnumerable<long> visibleHostIds,
    IEnumerable<long>? caseGrantOnlyHostIds = null) : IVisibilityService
{
    private readonly HashSet<long> _visible = visibleHostIds.ToHashSet();
    private readonly HashSet<long> _caseOnly = caseGrantOnlyHostIds?.ToHashSet() ?? [];

    public IReadOnlySet<long> GetVisibleHostIds() => _visible;
    public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
    public List<WebHost> GetVisibleHosts() => [];
    public void EnsureVisible(long hostId) { if (!_visible.Contains(hostId)) throw new InvalidOperationException(); }
    public IReadOnlyList<string> GetCaseGrantHostNames() => [];
    public bool IsCaseGrantOnly(long hostId) => _caseOnly.Contains(hostId);
    public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
}
