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
    public void LegacyPrtgImport_回410且不寫診斷暫存_診斷匯出入口皆要求ViewAll()
    {
        var controller = Controller();
        var retired = Assert.IsType<ObjectResult>(controller.LegacyPrtgImport());
        Assert.Equal(StatusCodes.Status410Gone, retired.StatusCode);
        Assert.Equal("legacy_import_retired", Assert.IsType<ApiResponse<object>>(retired.Value).Error?.Code);
        using (var db = _backend.CreateContext())
            Assert.Empty(db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_import_diagnostic_")));

        // 受限者（只有 Maintain、沒有 ViewAll）一律擋下；完整可見者放行
        var limited = FakeCurrentUser.WithCapabilities(Capability.Maintain);
        var full = FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll);
        foreach (var action in new[] { nameof(controller.LegacyPrtgImport), nameof(controller.ExportPrtgData) })
        {
            ViewAllGuard.AssertForbidden(controller, action, limited);
            ViewAllGuard.AssertAllowed(controller, action, full);
        }

        var mapping = PrtgControllerFactory.HostMapping(_backend, new RecordingAuditService(), _hosts);
        ViewAllGuard.AssertForbidden(mapping, nameof(mapping.GetPrtgManualMaps), limited);
        ViewAllGuard.AssertAllowed(mapping, nameof(mapping.GetPrtgManualMaps), full);
    }

    [Fact]
    public void PrtgTransferControllerPermissionFilter_只允許Maintain能力()
    {
        var required = typeof(PrtgTransferController).GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().Single().Arguments!.Cast<Capability[]>().Single();
        var audit = new RecordingAuditService();
        AuthorizationFilterContext Context() => new(new ActionContext(new DefaultHttpContext
        { Request = { Path = "/api/admin/settings/prtg-import" } }, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()), []);

        var maintainerContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Maintain), audit).OnAuthorization(maintainerContext);
        Assert.Null(maintainerContext.Result);
        var maintainedAction = Assert.IsType<ObjectResult>(Controller(audit).LegacyPrtgImport());
        Assert.Equal(StatusCodes.Status410Gone, maintainedAction.StatusCode);

        var restrictedContext = Context();
        new PermissionFilter(required, FakeCurrentUser.WithCapabilities(Capability.Handle), audit).OnAuthorization(restrictedContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(restrictedContext.Result).StatusCode);
        var restrictedActionCalls = 0;
        if (restrictedContext.Result is null)
        {
            restrictedActionCalls++;
            Controller(audit).LegacyPrtgImport();
        }
        Assert.Equal(0, restrictedActionCalls);
        Assert.Contains(audit.Entries, entry => entry.Result == LogForesight.Core.Models.AuditResult.Denied);
    }

    private PrtgTransferController Controller(RecordingAuditService? audit = null) =>
        new(_backend, new FixedCapacity(), _hosts, new TestVisibility([1, 2]),
            FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll),
            new SystemSettingsStore(_backend.Blob("system_settings")), audit ?? new RecordingAuditService(),
            new PrtgLegacyJsonTransferValidator());

    private sealed class FixedCapacity : IPrtgTransferCapacityProvider
    {
        public PrtgTransferCapacitySnapshot Capture() => new(true, 512L * 1024 * 1024, "test-fixed-capacity", null);
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
