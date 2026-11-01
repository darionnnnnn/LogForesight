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
    [InlineData("dashboard自動AI卡遇429顯示暫時休息且不隱藏")]
    [InlineData("dashboard自動AI卡一般失敗維持隱藏")]
    public void 清單頁查詢行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "list-pages.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(15), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
