using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 U（F23-3）：自動派工開關從「郵件通知」頁籤搬到設定頁新的「派工」頁籤。
/// 只搬位置：元件 id、設定鍵與儲存流程不變（讀寫往返見 SystemSettingsServiceTests.AutoDispatchEnabled_讀寫往返）。
/// </summary>
public class SettingsDispatchTabTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string Panel(string cshtml, string name)
    {
        var start = cshtml.IndexOf($"data-panel=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到頁籤面板 {name}");
        var next = cshtml.IndexOf("data-panel=\"", start + 1, StringComparison.Ordinal);
        return next < 0 ? cshtml[start..] : cshtml[start..next];
    }

    [Fact]
    public void 自動派工開關在派工頁籤_不在郵件通知頁籤()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");

        var dispatch = Panel(cshtml, "dispatch");
        Assert.Contains("id=\"auto-dispatch-enabled\"", dispatch);
        Assert.Contains("id=\"auto-dispatch-no-pool-warning\"", dispatch);
        var mail = Panel(cshtml, "mail");
        Assert.DoesNotContain("auto-dispatch-enabled", mail);
        Assert.DoesNotContain("<h3 class=\"h6\">自動派工", mail);
        // 頁籤按鈕存在，網址 #dispatch 可直接開啟
        Assert.Contains("data-tab=\"dispatch\"", cshtml);
    }

    [Fact]
    public void 派工頁籤仍在設定表單內_與其他欄位一起儲存()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        var formStart = cshtml.IndexOf("<form id=\"settings-form\"", StringComparison.Ordinal);
        var formEnd = cshtml.IndexOf("</form>", formStart, StringComparison.Ordinal);
        var dispatchAt = cshtml.IndexOf("data-panel=\"dispatch\"", StringComparison.Ordinal);
        Assert.True(formStart >= 0 && dispatchAt > formStart && dispatchAt < formEnd, "派工頁籤必須在 settings-form 內");
    }

    [Fact]
    public void 儲存與載入仍用同一個欄位id()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        Assert.Contains("document.getElementById('auto-dispatch-enabled').checked = !!settings.autoDispatchEnabled", js);
        Assert.Contains("autoDispatchEnabled: document.getElementById('auto-dispatch-enabled').checked", js);
    }

    [Fact]
    public void 健康頁就緒卡與精靈都連到派工設定或健康頁()
    {
        var setup = Read("LogForesight.Web", "Views", "Pages", "Setup.cshtml");
        Assert.Contains("id=\"setup-dispatch-readiness\"", setup);
        Assert.Contains("admin/settings\")#health", setup);

        var readiness = Read("LogForesight.Web", "Services", "DispatchReadinessService.cs");
        Assert.Contains("\"/admin/settings#dispatch\"", readiness);   // 自動派工開關不通過 → 修正連結指到派工頁籤
    }
}
