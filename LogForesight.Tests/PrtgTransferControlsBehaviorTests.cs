using System.Diagnostics;
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
