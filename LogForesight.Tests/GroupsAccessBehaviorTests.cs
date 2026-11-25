using Xunit;

namespace LogForesight.Tests;

/// <summary>授權矩陣前端（groups.js）：每格一個增減請求、以回應重繪、失敗重新載入；彈窗只送差異。</summary>
public class GroupsAccessBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("rapidTogglesSendOnePerCellAndRedrawFromResponse")]
    [InlineData("responseListRedrawsWholeRow")]
    [InlineData("failureReloadsMatrixAndWarns")]
    [InlineData("columnHeaderShowsHostCountAndTitle")]
    [InlineData("modalSavesOnlyTheDifference")]
    [InlineData("modalWithoutDifferenceSendsNoAccessRequest")]
    [InlineData("modalUncheckAllRevokesWhatWasThere")]
    [InlineData("outOfOrderResponseDoesNotOverwriteNewerResult")]
    [InlineData("staleFailureDoesNotReloadOverNewerResult")]
    [InlineData("accessChangeRefreshesHostGroupTable")]
    [InlineData("dispatchPoolToggleRefreshesHostGroupTable")]
    public void 授權矩陣行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "groups-access.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    /// <summary>授權寫入只有增減一種形式：前端與 tools 不得再出現送整份 hostGroupIds 給 access 端點的寫法。</summary>
    [Fact]
    public void 前端與工具不再送整份hostGroupIds給授權端點()
    {
        var root = FindRepoRoot();
        var files = new[] { Path.Combine(root, "LogForesight.Web", "wwwroot", "js") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.js", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "LogForesight.Tests", "tools"), "*.mjs", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var idx = 0;
            while ((idx = text.IndexOf("/api/admin/access/", idx, StringComparison.Ordinal)) >= 0)
            {
                var window = text.Substring(idx, Math.Min(260, text.Length - idx));
                Assert.False(window.Contains("hostGroupIds", StringComparison.Ordinal),
                    $"{Path.GetRelativePath(root, file)} 仍以整份 hostGroupIds 呼叫授權端點：{window}");
                idx += 10;
            }
        }
    }
}
