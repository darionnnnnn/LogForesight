using LogForesight.Core.Models;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using Microsoft.Data.Sqlite;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureUiTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ReplayStatusRejectsNonMaintainAndCaseGrantOnlyBeforeReadingHostState(bool maintain, bool caseGrantOnly)
    {
        var controller = new PrtgResourcePressureController(null!, null!, new VisibleScope(caseGrantOnly),
            FakeCurrentUser.WithCapabilities(maintain ? Capability.Maintain : Capability.ViewAll));
        Assert.IsType<ForbidResult>(controller.ReplayStatus(42, 100));
    }

    [Fact]
    public void ReplayStatusAllowsMaintainerWithHostScopeToReadPersistedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-replay-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "state.db")}"
            }, directory);
            var controller = new PrtgResourcePressureController(backend, null!, new VisibleScope(false),
                FakeCurrentUser.WithCapabilities(Capability.Maintain));
            Assert.IsType<BadRequestObjectResult>(controller.ReplayStatus(42, 0));
            var response = Assert.IsType<OkObjectResult>(controller.ReplayStatus(42));
            using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response.Value));
            var data = json.RootElement.GetProperty("Data");
            Assert.True(data.GetProperty("ModeSaved").GetBoolean());
            Assert.False(data.GetProperty("ReplayPending").GetBoolean());
            Assert.Equal(0, data.GetProperty("ReplayJobs").GetArrayLength());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ControllerRejectsNonMaintainAndCaseGrantOnlyForBothModeActions()
    {
        var ordinary = new PrtgResourcePressureController(null!, null!, new VisibleScope(false),
            FakeCurrentUser.WithCapabilities(Capability.ViewAll));
        Assert.IsType<ForbidResult>(ordinary.Trial(42, new PrtgResourceTrialRequest(100)));
        Assert.IsType<ForbidResult>(ordinary.SetMode(42, new PrtgResourceModeRequest(100, null, false)));

        var caseGrant = new PrtgResourcePressureController(null!, null!, new VisibleScope(true),
            FakeCurrentUser.WithCapabilities(Capability.Maintain));
        Assert.IsType<ForbidResult>(caseGrant.Trial(42, new PrtgResourceTrialRequest(100)));
        Assert.IsType<ForbidResult>(caseGrant.SetMode(42, new PrtgResourceModeRequest(100, null, false)));

        var maintainer = new PrtgResourcePressureController(null!, null!, new VisibleScope(false),
            FakeCurrentUser.WithCapabilities(Capability.Maintain));
        Assert.IsType<BadRequestObjectResult>(maintainer.Trial(42, new PrtgResourceTrialRequest(0)));
    }

    [Fact]
    public void HostDetailUiRequiresExplicitTrialThenEnableAndShowsServerWindowAndModeStaleness()
    {
        var root = FindRepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Controllers", "Api", "PrtgResourcePressureController.cs"));
        var detail = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Services", "RecordDetailQueryService.cs"));
        var dto = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Models", "Dto", "RecordDtos.cs"));
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "HostDetail.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "host-detail.js"));

        Assert.Contains("MinimumHourlyCoveragePercent", controller);
        Assert.Contains("StartUtc", controller);
        Assert.Contains("TrialResultId", controller);
        Assert.Contains("Enable", controller);
        Assert.Contains("Decision.Window", controller);
        Assert.Contains("Capability.Maintain", detail);
        Assert.Contains("IsCaseGrantOnly(hostId)", detail);
        Assert.Contains("IsCurrentFormalPressure", detail);
        Assert.DoesNotContain("consumer.EvaluateBatch", detail);
        Assert.Contains("CanManageResourcePressure", dto);
        Assert.Contains("StaleReason", dto);
        Assert.Contains("ResourcePressureAvailability = caseGrantOnly ? null", detail);
        Assert.Contains("PrtgResourceEvidencePresentation.From(issue)?.ReasonCodes.ToList()", detail);
        Assert.Contains("PrtgResourceVersionEvidence = BuildPrtgResourceVersionEvidence(issue)", detail);
        Assert.Contains("EvidenceVersionReference", detail);
        Assert.Contains("RuleAdmissionVersionReference", detail);
        Assert.Contains("aria-live=\"polite\"", view);
        var recordDetail = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js"));
        Assert.Contains("evidence.evidenceVersionReference", recordDetail);
        Assert.Contains("evidence.ruleAdmissionVersionReference", recordDetail);
        Assert.Contains("不是原生識別或目前授權證明", recordDetail);
        Assert.Contains("試算正式模式", js);
        Assert.Contains("啟用正式模式", js);
        Assert.Contains("關閉正式模式", js);
        Assert.Contains("AbortController", js);
        Assert.Contains("可信涵蓋率", js);
        Assert.Contains("不會自動建立案件或寄送郵件", js);
        foreach (var state in new[] { "disabled", "policy-not-ready", "current-proof-unavailable", "warming", "ready-no-hit" })
            Assert.Contains(state, js);
        Assert.Contains("if (!state) return null", js);
        Assert.Contains("目前資源證據狀態未知", js);

        var reportDetail = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Services", "RecordDetailQueryService.cs"));
        var reportView = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "report-view.js"));
        var reportService = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "RiskReportService.cs"));
        Assert.Contains("PrtgReportEvidenceFingerprint", reportDetail);
        Assert.Contains("HostDayWorkflowFingerprint.PrtgInputFingerprint(record)", reportDetail);
        Assert.Contains("PrtgResourceEvidencePresentation.From(i)", reportService);
        Assert.Contains("unknown: '此報告沒有可核對的已保存 PRTG 證據版本", reportView);
        Assert.Contains("stale: '此報告的已保存 PRTG 證據版本與目前主機日存檔不同", reportView);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LogForesight.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class VisibleScope(bool caseGrantOnly) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { 42 };
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => new HashSet<long> { 42 };
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long hostId) { }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long hostId) => caseGrantOnly;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => caseGrantOnly ? new HashSet<string>() : null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => new HashSet<long> { 42 };
    }
}
