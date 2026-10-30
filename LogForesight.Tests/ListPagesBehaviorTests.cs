using System.Diagnostics;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 清單頁（audit／hosts／permission-changes／reports／dashboard）查詢函式的競態與失敗態：抽出 production 函式逐案檢查。
/// </summary>
public class ListPagesBehaviorTests
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
    [InlineData("audit舊回應晚到不渲染")]
    [InlineData("audit舊請求失敗不外擲")]
    [InlineData("audit最新請求失敗要外擲")]
    [InlineData("hosts舊回應晚到不渲染")]
    [InlineData("hosts舊請求失敗不外擲")]
    [InlineData("hosts最新請求失敗要外擲")]
    [InlineData("perm舊回應晚到不渲染")]
    [InlineData("perm一般錯誤不渲染空狀態")]
    [InlineData("perm網段錯誤顯示欄位錯誤")]
    [InlineData("perm舊請求失敗不外擲")]
    [InlineData("reports舊回應晚到不寫入狀態")]
    [InlineData("reports舊請求失敗不外擲")]
    [InlineData("dashboard舊回應晚到不寫入狀態")]
    [InlineData("dashboard舊請求失敗不外擲")]
    [InlineData("dashboard最新請求失敗要外擲")]
    public void 清單頁查詢行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "list-pages.behavior.mjs");
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

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("偵測到 node 卻無法啟動行為測試腳本。", ex);
        }

        Assert.NotNull(process);
        // 輪詢類案例在實作壞掉時（例如 stop 失效）node 永遠不會結束：先非同步讀輸出、
        // 逾時就結束整個程序樹再判失敗，不能先 ReadToEnd（會無限卡住，逾時保護形同虛設）
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
            Assert.Fail($"node 執行逾時（未結束）：{caseName}");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
