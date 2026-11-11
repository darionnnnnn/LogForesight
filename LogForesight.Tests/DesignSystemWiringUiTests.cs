using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 設計系統 v3 套用後的接線守門：類別改名、欄位收進窄版摘要、訊息改成就地顯示這類改動，
/// 行為測試只切函式本體時跑不到頁面層的接線，用原始碼斷言守住（docs/DESIGN-SYSTEM.md §8）。
/// </summary>
public sealed class DesignSystemWiringUiTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(WebRoot(), relative), Encoding.UTF8).Replace("\r\n", "\n");

    [Fact]
    public void 處理面板的AI備註以問題格標題類別取問題名稱()
    {
        // 問題格標題的類別是 lf-issue-cell__title；面板改用舊的 Bootstrap 工具類別會對不到，AI 永遠拿不到問題名稱
        Assert.Contains("'lf-text-title lf-issue-cell__title'", Read("wwwroot/js/pages/record-detail.js"));
        var panel = Read("wwwroot/js/pages/handling-panel.js");
        Assert.Contains(".querySelector('.lf-issue-cell__title')", panel);
        Assert.DoesNotContain(".lf-issue-cell .fw-semibold", panel);
    }

    [Fact]
    public void 處理人工作頁窄版摘要保留單號連結與期限操作()
    {
        // < 576px 隱藏單號與期限欄；摘要行若只剩文字，手機上就沒有到交辦單詳情與設定期限的入口
        var js = Read("wwwroot/js/pages/handler-detail.js");
        var narrow = Regex.Match(js, @"const narrow = metaLine\(\[(.*?)\]", RegexOptions.Singleline);
        Assert.True(narrow.Success, "找不到 orderIssueCell 的窄版摘要行");
        Assert.Contains("workOrderLink(row)", narrow.Groups[1].Value);
        Assert.Contains("narrowDueDate(row)", narrow.Groups[1].Value);
        Assert.Contains("dueDateCell(row)", Regex.Match(js, @"function narrowDueDate\(row\) \{.*?\n\}", RegexOptions.Singleline).Value);
        // 同一張單有寬版與窄版兩個期限實例：存檔後要全部重繪，不能只重繪被操作的那個
        Assert.Contains("dueDateViews.get(row).forEach(refresh => refresh());", js);
        Assert.Contains("dueDateViews.get(row).push(showValue);", js);
    }

    [Fact]
    public void 設定頁成功與驗證訊息在之後修改欄位時清除()
    {
        var js = Read("wwwroot/js/pages/settings.js");
        var listener = Regex.Match(js, @"getElementById\('settings-form'\)\?\.addEventListener\('input'.*?\n\}\);", RegexOptions.Singleline);
        Assert.True(listener.Success, "設定表單要監聽 input 清掉過期的儲存列訊息");
        Assert.Contains("saveNoteType === 'success' || saveNoteType === 'warning'", listener.Value);
        Assert.Contains("type === 'file'", listener.Value);   // 選圖檔的「按儲存才生效」提醒不能被自己的事件清掉
    }

    [Fact]
    public void NetIQ的Sentinel缺欄位時標紅並移入焦點()
    {
        var js = Read("wwwroot/js/pages/netiq.js");
        var helper = Regex.Match(js, @"function flagMissingField\(inputId, message\) \{.*?\n\}", RegexOptions.Singleline).Value;
        Assert.Contains("classList.add('is-invalid')", helper);
        Assert.Contains("input.focus()", helper);
        Assert.Contains("flagMissingField('sentinel-name'", js);
        Assert.Equal(4, Regex.Matches(js, @"'sentinel-(name|base-url|username|password)', '請").Count);
        // 關掉重開或換一台編輯時不留上一次的紅框
        Assert.Contains("classList.remove('is-invalid');",
            Regex.Match(js, @"function openSentinelModal\(sentinel\) \{.*?\n\}", RegexOptions.Singleline).Value);
    }

    [Fact]
    public void 頁頂提醒列不帶外距工具類別_與內容間距由樣式表決定()
    {
        // .alert 的 1rem 外距疊上內容區上內距會在提醒列與頁首之間空出一大段
        var layout = Read("wwwroot/js/core/layout.js");
        var bars = Regex.Matches(layout, @"bar\.className = '(alert [^']*)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, bars.Count);
        Assert.All(bars, c => Assert.DoesNotMatch(@"\bmb-\d\b", c));
        var css = SiteCss.Read();
        Assert.Matches(@"#lf-top-alerts \.alert \{\s*margin-bottom: 0;\s*border-width: 0 0 1px;\s*border-radius: 0;", css);
    }

    [Fact]
    public void 列印時揭露目標一律印出_問題格標題可換到徽章下方()
    {
        var css = SiteCss.Read();
        var print = Regex.Matches(css, @"@media print \{(.*?)\n\}", RegexOptions.Singleline).Select(m => m.Groups[1].Value);
        Assert.Contains(print, block => block.Contains(".lf-disclosure-target.d-none") && block.Contains("display: block !important")
            && block.Contains(".lf-meta-line.lf-disclosure-target.d-none") && block.Contains("display: flex !important"));
        // 工具列的「更多條件」是篩選欄位不是內容：收合時列印不印出
        Assert.Contains("target.classList.remove('lf-disclosure-target');", Read("wwwroot/js/core/elements.js"));

        var head = Regex.Match(css, @"\.lf-issue-cell__head \{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("flex-wrap: wrap", head);
        Assert.Matches(@"\.lf-issue-cell__title \{ flex: 1 1 \d+rem;", css);
    }

    [Fact]
    public void 必填星號帶角色類別()
    {
        var bare = Directory.GetFiles(Path.Combine(WebRoot(), "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<span(?![^>]*lf-required-mark)[^>]*>\*</span>"))
            .Select(Path.GetFileName).ToList();
        Assert.True(bare.Count == 0, "必填星號要用 lf-required-mark：" + string.Join("、", bare));
        Assert.Contains(".lf-required-mark {", SiteCss.Read());
    }
}
