using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotDiagnosticsApiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

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
    public void 回報寫入量達標狀態有明確標籤與重試提示且不標成健康()
    {
        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        const string key = "'reported-write-count-met-target'";

        Assert.Contains($"{key}: '回報寫入量達目標，逐顆覆蓋未證明'", js);
        Assert.Contains($"{key}: '回報寫入量達目標但未逐顆確認；重試或覆寫可能造成重複計數。'", js);
        Assert.DoesNotContain($"{key}: '健康'", js);
    }
}
