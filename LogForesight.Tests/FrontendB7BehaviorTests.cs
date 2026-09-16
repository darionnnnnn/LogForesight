using System.Diagnostics;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪批次 B7 的**行為**守門：GET 逾時／POST 不逾時（C1）與失敗狀態的重試鈕（C2）。
///
/// 這兩條沒辦法用字串比對證明——「原始碼裡有 AbortController」不能證明 POST 沒被中止，
/// 「有『重試』兩個字」也不能證明按下去真的會重跑。案例本體是 JsBehavior/ 底下的
/// node 腳本（用可控的假 fetch 與極簡 DOM 替身真的跑一次 core/api.js 與 core/ui.js），
/// 這裡逐案啟動 node 執行並把輸出原樣帶回失敗訊息。
///
/// 前端頁面檔是 ESM 但副檔名為 .js（瀏覽器靠 &lt;script type="module"&gt; 載入，
/// 專案沒有 package.json），因此以 --experimental-default-type=module 執行。
///
/// 標 <see cref="NodeTheoryAttribute"/>：本專案沒有 node 依賴，部署機上不一定裝得到——
/// 偵測不到 node 時顯示為略過而不是紅（環境造成的假警報會讓「全綠」失去意義）；
/// 但 node 在而案例失敗時仍然要紅。
/// </summary>
public class FrontendB7BehaviorTests
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
    [InlineData("GET超過逾時拋出可辨識的逾時錯誤")]
    [InlineData("POST超過同樣時間不會被中止")]
    [InlineData("逾時在非silent時發一次toast")]
    [InlineData("逾時在silent時不發toast")]
    [InlineData("載入失敗顯示重試鈕且按下會重跑")]
    [InlineData("找不到資料不顯示重試鈕")]
    public void 前端等待體驗的行為案例(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "api-guard.behavior.mjs");
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
            // 走到這裡表示 NodeTheory 偵測時 node 還在、真正要跑時卻啟動不了——
            // 那是環境在測試中途壞掉，不是「沒有 node」，照常紅
            throw new InvalidOperationException(
                "偵測到 node 卻無法啟動它（前端行為測試腳本因此沒有跑到）。", ex);
        }

        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), $"node 執行逾時：{caseName}");

        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
