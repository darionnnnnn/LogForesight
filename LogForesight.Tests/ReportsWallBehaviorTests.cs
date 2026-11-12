using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表頁大螢幕模式（戰情中心，WEB-SPEC §9.6）的行為：自動更新排程、分頁可見性、快捷期間滾動、更新失敗、
/// 離開（Esc／全螢幕退出）、寬度不足、字級倍率與圖表字級出口。
/// reports-wall.js 以 production 原檔載入、計時器與瀏覽器環境換成假時鐘；reports.js 與 charts.js 切出 production 函式原文。
/// </summary>
public class ReportsWallBehaviorTests
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
    [InlineData("完成後15分才發下一次且未完成時不發第二次")]
    [InlineData("分頁隱藏時不發_恢復可見且已逾間隔立刻發_未逾間隔不發")]
    [InlineData("快捷期間跨午夜後起訖各前進一天_自訂日期不變")]
    [InlineData("更新失敗保留舊資料且標頭寫更新失敗_下一次成功後消失")]
    [InlineData("首次載入失敗不寫舊資料提示並排下一次重試")]
    [InlineData("重試間隔_連續失敗逐次加倍到一輪封頂")]
    [InlineData("Esc離開時網址與類別與計時器全部還原")]
    [InlineData("Esc離開只有display參數時網址回到無查詢字串")]
    [InlineData("按鈕進入後全螢幕退出事件會離開模式")]
    [InlineData("按鈕離開時仍在全螢幕就退出全螢幕")]
    [InlineData("全螢幕被拒絕時模式照樣生效且不受fullscreenchange影響")]
    [InlineData("網址進入時全螢幕退出事件不離開")]
    [InlineData("寬度不足時不啟動自動更新且按鈕隱藏")]
    [InlineData("字級倍率隨視窗高度等比放大且下限為1")]
    [InlineData("進入時套用倍率_視窗高度改變時重算_離開還原")]
    [InlineData("離開鈕滑鼠移動出現且3秒後淡出")]
    [InlineData("大螢幕生效時第一排卡片不帶連結")]
    [InlineData("圖表轉接_大螢幕不掛下鑽_重繪不做進場動畫")]
    [InlineData("charts字級取自token_根字級16為12與14_18為13點5_14時下限12")]
    [InlineData("charts頁面自訂座標軸補上token字級_頁面自己指定的優先")]
    [InlineData("charts字級偏好切換時現存圖表重畫_大螢幕倍率變兩倍時字級變兩倍")]
    public void 大螢幕模式行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "reports-wall.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
