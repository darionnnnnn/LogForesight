using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表頁三個視角（總覽／處理成效／問題類型，WEB-SPEC §9.6）的行為：視角名稱與網址參數、延後載入與各視角的序號守門、
/// 處理成效的分桶堆疊條／帳齡／再發生率、問題類型熱度表、顯示範圍提示、列印補載入與大螢幕的視角保存。
/// reports-common／reports-effectiveness／reports-category 以 production 原檔載入，reports.js 切出 production 函式原文。
/// </summary>
public class ReportsViewsBehaviorTests
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
    [InlineData("view參數_無效值退回總覽")]
    [InlineData("view參數_寫入網址時保留其他參數_總覽不留參數")]
    [InlineData("view參數_重新整理保留")]
    [InlineData("切視角只打該視角的端點")]
    [InlineData("初次進入非總覽視角時總覽不載入_切回去才載入")]
    [InlineData("期間改變只重載目前視角_其他視角標記待重載")]
    [InlineData("快速連續重載只畫最後一次")]
    [InlineData("快速連切視角各視角只畫自己最後一次")]
    [InlineData("舊請求失敗不影響新請求的結果")]
    [InlineData("載入失敗時視角標成失敗_待範圍清掉_下次切過來會重試")]
    [InlineData("起日晚於迄日時處理成效不發出查詢")]
    [InlineData("載入成功才讓顯示範圍生效")]
    [InlineData("顯示範圍提示_unresolved有_all沒有")]
    [InlineData("處理成效_分桶堆疊條與未處理段下鑽")]
    [InlineData("處理成效_桶標籤_日週月")]
    [InlineData("處理成效_帳齡五段色階由淡到深且標題寫未結案N")]
    [InlineData("處理成效_沒有未結案時不畫條")]
    [InlineData("處理成效_再發生率分母為0時顯示說明而不是0百分比")]
    [InlineData("處理成效_再發生率大數字與前10名表格")]
    [InlineData("處理成效_三個指標各有說明圖示且文字取自後端")]
    [InlineData("處理成效_期間內沒有風險日顯示無資料")]
    [InlineData("處理成效_數字對得起來_帳齡合計等於未結案_分桶合計等於各桶三態相加")]
    [InlineData("處理成效_下鑽網址函式")]
    [InlineData("熱度色階_0不上色_最大值落在最高級")]
    [InlineData("熱度表_零的格不可點_非零格有下鑽與可讀名稱")]
    [InlineData("熱度表_下鑽網址與名稱函式")]
    [InlineData("列印前補載入尚未載入的視角_全部回應後才呼叫print")]
    [InlineData("列印時某視角載入失敗仍會印_並標記失敗")]
    [InlineData("列印整合_頁面的printReport補載入待重載的視角")]
    [InlineData("大螢幕視角保存_進入切回總覽_離開還原")]
    public void 報表視角行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "reports-views.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
