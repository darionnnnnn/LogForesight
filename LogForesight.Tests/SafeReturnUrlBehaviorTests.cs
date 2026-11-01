using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 登入後導回網址 isSafeReturnUrl 的行為：直接載入 production 模組逐案檢查。
/// </summary>
public class SafeReturnUrlBehaviorTests
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
    [InlineData("controlTab")]
    [InlineData("controlLf")]
    [InlineData("controlCr")]
    [InlineData("doubleSlash")]
    [InlineData("backslash")]
    [InlineData("absoluteHttp")]
    [InlineData("noLeadingSlash")]
    [InlineData("nullValue")]
    [InlineData("emptyString")]
    [InlineData("root")]
    [InlineData("pathQuery")]
    [InlineData("pathHash")]
    public void 導回網址安全檢查(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "safe-return-url.behavior.mjs");
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
