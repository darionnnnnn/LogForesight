using System.Diagnostics;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>PRTG 維護頁的判定行為：最近一次排程驗收（core/prtg-acceptance.js）與全量回填的完成水位按鈕。</summary>
public sealed class PrtgAcceptanceBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("prtg-acceptance.behavior.mjs", "scheduledRun")]
    [InlineData("prtg-acceptance.behavior.mjs", "ai")]
    [InlineData("prtg-acceptance.behavior.mjs", "snapshot")]
    [InlineData("prtg-full-backfill.behavior.mjs", "startButtonFollowsCompletionAndRedo")]
    [InlineData("prtg-full-backfill.behavior.mjs", "noCompletedDaysHasNoRedoCheckbox")]
    public void PRTG維護頁判定的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--experimental-default-type=module");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(caseName);
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), $"node 執行逾時：{caseName}");
        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
