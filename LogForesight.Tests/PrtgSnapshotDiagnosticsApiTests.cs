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

}
