using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>執行正式 bindPrtgDataTransfer 函式的可控 DOM/API 行為案例。</summary>
public sealed class PrtgTransferControlsBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("doubleUploadClickStartsOneOperationAndRestoresButtons")]
    [InlineData("statusAndAbandonAreMutuallyExclusive")]
    [InlineData("confirmationBlocksImportAndCancelledForgetPreservesRecord")]
    [InlineData("statusForbiddenOrMissingStillAllowsLocalForget")]
    [InlineData("forgetSuccessIsLocalOnly")]
    [InlineData("uploadRestoresControlsAfterCompletion")]
    public void PRTG診斷搬運控制的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "prtg-transfer-controls.behavior.mjs");
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
