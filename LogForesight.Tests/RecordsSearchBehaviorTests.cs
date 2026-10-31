using System.Diagnostics;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題查詢頁 search() 的競態與失敗態：抽出 production 函式逐案檢查。
/// </summary>
public class RecordsSearchBehaviorTests
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
    [InlineData("舊回應晚到不渲染")]
    [InlineData("視角切換後舊回應不進render")]
    [InlineData("同序號但視角已變也丟棄")]
    [InlineData("api擲錯呼叫renderError")]
    [InlineData("舊請求失敗不蓋掉新結果")]
    [InlineData("依主機視角不送逾期不顯示標籤且提示被略過")]
    [InlineData("切回明細恢復逾期與標籤且提示消失")]
    [InlineData("依問題視角只略過逾期")]
    [InlineData("支援矩陣與後端一致")]
    public void 查詢頁search行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "records-search.behavior.mjs");
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
