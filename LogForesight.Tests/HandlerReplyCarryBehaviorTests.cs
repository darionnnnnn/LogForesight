using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 下一張回覆在多主機交辦單上的 carry 行為：實際執行 handler-detail.js 的 production 函式，
/// 只替換 DOM、API 與 UI 邊界，避免以字串存在性或重寫演算法作為通過條件。
/// </summary>
public class HandlerReplyCarryBehaviorTests
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
    [InlineData("nextMultiHostReplyCarriesStatusAndNoteOnce")]
    [InlineData("manualReplyClearsCarryFromExpandedPanel")]
    public void 下一張回覆carry行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "handler-reply-carry.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
