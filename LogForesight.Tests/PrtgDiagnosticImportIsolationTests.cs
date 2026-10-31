using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiagnosticImportIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-diag-import-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    public PrtgDiagnosticImportIsolationTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        _hosts = new(_backend.Blob("hosts"));
        _hosts.Upsert(new() { HostName = "one", Source = "netiq", Active = true });
        _hosts.Upsert(new() { HostName = "two", Source = "netiq", Active = true });
    }

    [Fact]
    public void LegacyPrtgImport_完整可見者回410_受限案件及空可見仍拒絕()
    {
        var full = Controller(new TestVisibility([1, 2]));
        var retired = Assert.IsType<ObjectResult>(full.LegacyPrtgImport());
        Assert.Equal(StatusCodes.Status410Gone, retired.StatusCode);
        Assert.Equal("legacy_import_retired", Assert.IsType<ApiResponse<object>>(retired.Value).Error?.Code);
        using (var db = _backend.CreateContext())
            Assert.Empty(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));

        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1])).LegacyPrtgImport());
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2], caseOnly: true)).LegacyPrtgImport());
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([])).LegacyPrtgImport());
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).LegacyPrtgImport());
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).LegacyPrtgImport());
        var noHosts = new HostStore(_backend.Blob("empty-hosts"));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([]), hosts: noHosts).LegacyPrtgImport());
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([]), hosts: noHosts).ExportPrtgData(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Throws<DomainException>(() => Controller(null, hosts: _hosts).GetPrtgManualMaps());
        Assert.Throws<DomainException>(() => Controller(new TestVisibility([1, 2]), omitHosts: true).GetPrtgManualMaps());
    }

    [Fact]
    public void SettingsControllerPermissionFilter_只允許Maintain能力()
    {
        var required = typeof(SettingsController).GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().Single().Arguments!.Cast<Capability[]>().Single();
        var audit = new RecordingAuditService();
        AuthorizationFilterContext Context() => new(new ActionContext(new DefaultHttpContext
        { Request = { Path = "/api/admin/settings/prtg-import" } }, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()), []);

        var maintainerContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Maintain), audit).OnAuthorization(maintainerContext);
        Assert.Null(maintainerContext.Result);
        var maintainedAction = Assert.IsType<ObjectResult>(Controller(new TestVisibility([1, 2]), audit).LegacyPrtgImport());
        Assert.Equal(StatusCodes.Status410Gone, maintainedAction.StatusCode);

        var restrictedContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Handle), audit).OnAuthorization(restrictedContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(restrictedContext.Result).StatusCode);
        var restrictedActionCalls = 0;
        if (restrictedContext.Result is null)
        {
            restrictedActionCalls++;
            Controller(new TestVisibility([1, 2]), audit).LegacyPrtgImport();
        }
        Assert.Equal(0, restrictedActionCalls);
        Assert.Contains(audit.Entries, entry => entry.Result == LogForesight.Core.Models.AuditResult.Denied);
    }

    // 「完整可見者」＝持有 ViewAll；由各案給的可見集合與主機表推得，保留各案原本想表達的角色。
    private SettingsController Controller(TestVisibility? visibility, RecordingAuditService? audit = null, HostStore? hosts = null, bool omitHosts = false)
    {
        var effectiveHosts = omitHosts ? null : hosts ?? _hosts;
        var all = effectiveHosts?.GetAll();
        var viewAll = visibility != null && all is { Count: > 0 } && all.All(h =>
            visibility.GetVisibleHostIds().Contains(h.HostId) && !visibility.IsCaseGrantOnly(h.HostId));
        return new(new FakeSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")), audit ?? new RecordingAuditService(), backend: _backend, hosts: effectiveHosts, visibility: visibility,
            currentUser: viewAll ? FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll) : FakeCurrentUser.WithCapabilities(Capability.Maintain));
    }
    private sealed class TestVisibility(long[] ids, bool caseOnly = false) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => caseOnly;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
