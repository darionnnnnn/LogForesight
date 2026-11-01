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
    [InlineData("aiSummaryStaleResponseIsDropped")]
    [InlineData("aiSummaryFailureShowsNoticeAndKeepsPrevious")]
    [InlineData("aiSummaryFailureAfterFilterChangeIsSilent")]
    [InlineData("aiSummary429KeepsRestMessage")]
    [InlineData("aiSummary429ThenFailureShowsSingleNoticeAndKeepsPrevious")]
    public void 查詢頁search行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "records-search.behavior.mjs");
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
