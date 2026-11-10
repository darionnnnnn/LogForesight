using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 66 輪 D2：風險日詳情右側側軌（core/side-rail.js）的 production 行為，以及 cshtml／JS／CSS 的接線斷言。</summary>
public sealed class SideRailBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("side-rail.behavior.mjs", "initialCollapsed")]
    [InlineData("side-rail.behavior.mjs", "clickExpandsAndFocusesHeading")]
    [InlineData("side-rail.behavior.mjs", "collapseReturnsFocusToTrigger")]
    [InlineData("side-rail.behavior.mjs", "collapseButtonMarksUserChoice")]
    [InlineData("side-rail.behavior.mjs", "setSummaryUpdatesButton")]
    [InlineData("side-rail.behavior.mjs", "expandWithoutFocusDoesNotFocus")]
    [InlineData("side-rail.behavior.mjs", "expandWithoutScrollDoesNotScroll")]
    [InlineData("side-rail.behavior.mjs", "recordDetailUrlHandle")]
    [InlineData("side-rail.behavior.mjs", "isRailModeFollowsLgBreakpoint")]
    public void 側軌與詳情連結的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()), Encoding.UTF8).Replace("\r\n", "\n");

    private static string PageJs(string name) => Read("LogForesight.Web", "wwwroot", "js", "pages", name);

    [Fact]
    public void RecordDetail版面改用側軌layout_不再用col_lg_8與col_lg_4排主欄()
    {
        var view = Read("LogForesight.Web", "Views", "Pages", "RecordDetail.cshtml");
        var main = view.Substring(0, view.IndexOf("id=\"record-context\"", StringComparison.Ordinal));
        Assert.DoesNotContain("col-lg-8", main);
        Assert.DoesNotContain("col-lg-4", main);
        Assert.Contains("lf-detail-layout", main);
        Assert.Contains("lf-detail-main", main);
        Assert.Contains("lf-detail-side", main);
        Assert.Contains("id=\"record-layout\"", main);
        Assert.Contains("id=\"record-side\"", main);
    }

    [Fact]
    public void RecordDetail接線_hash_勾選自動展開_案件授與不建立側軌()
    {
        var js = PageJs("record-detail.js");
        Assert.Contains("createSideRail", js);
        Assert.Contains("'#handling'", js);
        Assert.Contains("history.replaceState", js);
        Assert.Contains("hasUserChoice", js);
        Assert.Contains("derivedStatusLabel", js);
        // 建立側軌的呼叫必須在「不是案件授與」的條件之內：條件被拿掉時這條要變紅
        Assert.Contains("if (!currentDetail.caseGrantOnly) sideRail = setupSideRail();", js);
        Assert.Contains("if (!sideRail) {", js);
        // 單列勾選與表頭全選兩條路徑都要能自動展開（定義一處＋呼叫兩處）
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(js, @"expandSideRailForSelection\(").Count);
    }

    [Fact]
    public void 去處理意圖的連結都帶handle_true()
    {
        foreach (var file in new[] { "records.js", "record-detail.js", "handler-detail.js", "user-detail.js" })
        {
            var js = PageJs(file);
            Assert.Contains("recordDetailUrl(", js);
            Assert.Contains("{ handle: true }", js);
        }
        Assert.Contains("recordDetailUrl(record.hostId, record.date, { handle: true })", PageJs("records.js"));
        Assert.Contains("recordDetailUrl(next.hostId, next.date, { handle: true })", PageJs("record-detail.js"));
        Assert.Contains("rowHref: d => recordDetailUrl(d.hostId, d.date, { handle: true })", PageJs("handler-detail.js"));
        Assert.Contains("rowHref: d => recordDetailUrl(d.hostId, d.date, { handle: true })", PageJs("user-detail.js"));
        // 展開列整列點擊與「進行中的交辦」列點擊都是去處理意圖，不得退回手組網址
        Assert.Contains("rowHref: r => recordDetailUrl(r.hostId, r.date, { handle: true })", PageJs("records.js"));
        Assert.Contains("rowHref: c => recordDetailUrl(c.hostId, c.lastLinkedDate, { handle: true })", PageJs("handler-detail.js"));
        Assert.DoesNotContain("rowHref: r => `/records/${r.hostId}/${r.date}`,", PageJs("records.js"));
        Assert.DoesNotContain("rowHref: c => `/records/${c.hostId}/${c.lastLinkedDate}`", PageJs("handler-detail.js"));
    }

    [Fact]
    public void 樣式表有側軌兩欄與列印隱藏規則()
    {
        var css = SiteCss.Read();
        Assert.Contains(".lf-detail-layout", css);
        Assert.Contains(".lf-side-rail__btn", css);
        var start = css.IndexOf("風險日詳情兩欄版面與右側側軌", StringComparison.Ordinal);
        Assert.True(start > 0);
        var print = css.IndexOf("@media print", start, StringComparison.Ordinal);
        Assert.True(print > 0, "側軌段應有 @media print");
        var block = css.Substring(print);
        Assert.Contains(".lf-side-rail", block);
        Assert.Contains("display: none !important", block);
        Assert.Contains(".lf-detail-side__body", block);
    }
}
