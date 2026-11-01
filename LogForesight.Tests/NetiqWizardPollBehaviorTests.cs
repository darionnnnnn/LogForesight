using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 匯入精靈輪詢掃描工作的容錯行為（第 59 輪 F5）：實際執行 netiq-import-wizard.js 的
/// startPolling／stopPolling，只替換 api、計時器與畫面邊界。
/// </summary>
public class NetiqWizardPollBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("一次502後繼續輪詢")]
    [InlineData("連續三次失敗才停止並提示")]
    [InlineData("成功一次歸零連續失敗計數")]
    [InlineData("工作不存在或已逾期立即停止")]
    public void 精靈輪詢容錯行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "netiq-wizard-poll.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
