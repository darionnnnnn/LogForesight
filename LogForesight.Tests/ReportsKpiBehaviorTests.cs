using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表頁（pages/reports.js）的行為：第一排四格（含資料完整度五種狀態）、處理進度條、排行標籤、
/// 風險趨勢長條圖與工具列條件文字。實際執行 production 函式原文，只替換 DOM 與 Chart 包裝層。
/// </summary>
public class ReportsKpiBehaviorTests
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
    [InlineData("資料完整度_無資料")]
    [InlineData("資料完整度_完整")]
    [InlineData("資料完整度_缺漏")]
    [InlineData("資料完整度_落後")]
    [InlineData("資料完整度_缺漏且落後")]
    [InlineData("未結案_有逾期副標寫逾期")]
    [InlineData("未結案_無逾期不出現逾期字樣")]
    [InlineData("未結案_四種顯示範圍的下鑽網址")]
    [InlineData("受影響主機寫成N除以M台")]
    [InlineData("前期為0與兩期皆0的對比文字")]
    [InlineData("前期為空時對比副標留空")]
    [InlineData("處理進度_總數為0顯示空狀態且沒有條")]
    [InlineData("處理進度_三段相加等於總數且替代文字含三段數字")]
    [InlineData("處理進度_零值段不畫但數字仍列出")]
    [InlineData("處理進度_中風險被隱藏時風險層級只列高")]
    [InlineData("排行標籤_有顯示名")]
    [InlineData("排行標籤_沒有顯示名")]
    [InlineData("趨勢圖是堆疊長條且沒有tension")]
    [InlineData("趨勢圖_中風險被隱藏時只畫高")]
    [InlineData("趨勢圖_期間全為0顯示無資料")]
    [InlineData("工具列條件文字")]
    public void 報表頁行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "reports-kpi.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
