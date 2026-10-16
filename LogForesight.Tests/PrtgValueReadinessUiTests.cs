using Xunit;

namespace LogForesight.Tests;

public class PrtgValueReadinessUiTests
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

    [Fact]
    public void Prtg頁面包含數值資料就緒度卡片與完整無障礙標記()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var content = File.ReadAllText(cshtmlPath);

        var requiredIds = new[]
        {
            "prtg-value-readiness-refresh",
            "prtg-value-readiness-backfill",
            "prtg-value-readiness-status",
            "prtg-value-readiness-groups",
            "prtg-value-readiness-table-region",
            "prtg-value-readiness-body",
            "prtg-value-readiness-prev",
            "prtg-value-readiness-next",
            "prtg-value-readiness-page-info"
        };

        foreach (var id in requiredIds)
        {
            Assert.Contains($"id=\"{id}\"", content);
        }

        Assert.Contains("role=\"status\"", content);
        Assert.Contains("aria-live=\"polite\"", content);
        Assert.Contains("role=\"region\"", content);
        Assert.Contains("tabindex=\"0\"", content);
        Assert.Contains("<caption", content);
    }

    [Fact]
    public void PrtgValueReadiness腳本包含端點呼叫與屬性操作且不含innerHTML()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-value-readiness.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("/api/prtg/value-readiness?page=", js);
        Assert.Contains("aria-pressed", js);
        Assert.Contains("textContent", js);
        Assert.Contains("data-tab=\"mirror\"", js);
        Assert.Contains("data-tab=\"selected-backfill\"", js);
        Assert.Contains("AbortController", js);
        Assert.DoesNotContain("innerHTML =", js);
    }

    [Fact]
    public void PrtgAdmin腳本接上InitValueReadiness呼叫()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("initValueReadiness();", js);
    }
}
