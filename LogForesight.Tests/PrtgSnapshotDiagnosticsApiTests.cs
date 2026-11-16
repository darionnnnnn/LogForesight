using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotDiagnosticsApiTests
{

    [Fact]
    public void 診斷API只允許Maintain()
    {
        var permission = Assert.Single(typeof(PrtgSnapshotDiagnosticsController)
            .GetCustomAttributes(typeof(PermissionAttribute), inherit: false)
            .Cast<PermissionAttribute>());
        Assert.Equal(new[] { Capability.Maintain }, (Capability[])permission.Arguments![0]!);
    }

    [Theory]
    [InlineData(PrtgSnapshotHourState.Healthy, "coverage-evidence")]
    [InlineData(PrtgSnapshotHourState.OkCovered, "ok-covered")]
    [InlineData(PrtgSnapshotHourState.ReportedWriteCountMetTarget, "reported-write-count-met-target")]
    [InlineData(PrtgSnapshotHourState.NoTargets, "no-targets")]
    [InlineData(PrtgSnapshotHourState.Unknown, "unknown")]
    public void API不會把可用資料或無目標包裝成快照健康(PrtgSnapshotHourState state, string expected)
    {
        Assert.Equal(expected, PrtgSnapshotDiagnosticsController.StateName(state));
    }

    [Fact]
    public void 封存端點_要求Maintain與ViewAll且是POST()
    {
        var method = typeof(PrtgSnapshotDiagnosticsController).GetMethod(nameof(PrtgSnapshotDiagnosticsController.ArchiveJournal))!;
        Assert.NotNull(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).SingleOrDefault());
        Assert.Equal("archive-journal", ((Microsoft.AspNetCore.Mvc.HttpPostAttribute)method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false)[0]).Template);
        // Maintain 由類別層級的 [Permission] 提供（診斷 API 只允許 Maintain），端點另加 ViewAll
        var permission = Assert.Single(typeof(PrtgSnapshotDiagnosticsController).GetCustomAttributes(typeof(PermissionAttribute), false).Cast<PermissionAttribute>());
        Assert.Equal(new[] { Capability.Maintain }, (Capability[])permission.Arguments![0]!);
        Assert.Single(method.GetCustomAttributes(typeof(RequireViewAllAttribute), false));
    }

    [Fact]
    public void 診斷API對外狀態字串包含stopped()
    {
        Assert.Equal("stopped", PrtgSnapshotDiagnosticsController.StateName(PrtgSnapshotHourState.Stopped));
    }
}
