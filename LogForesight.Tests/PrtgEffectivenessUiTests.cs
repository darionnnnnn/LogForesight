using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgEffectivenessUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 使用效果分頁明確呈現各指標分母日期限制與資料就緒入口()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));

        Assert.Contains("data-tab=\"effectiveness\"", view);
        Assert.Contains("data-panel=\"effectiveness\"", view);
        Assert.Contains("data-tab=\"probe\"", view);
        Assert.Contains("type=\"date\" id=\"prtg-effectiveness-from\"", view);
        Assert.Contains("type=\"date\" id=\"prtg-effectiveness-through\"", view);
        Assert.Contains("aria-live=\"polite\"", view);
        Assert.Contains("最多 366 天", js);
        Assert.Contains("/api/prtg/effectiveness?", js);
        Assert.Contains("summary.prtgFindings", js);
        Assert.Contains("summary.lowCoverageSampledHours", js);
        Assert.Contains("已落盤 sampled 且 coverage 低於門檻的小時列數；不包含完全缺值", js);
        Assert.Contains("此指標不涵蓋所有磁碟不就緒原因", js);
        Assert.Contains("summary.casesCreated", js);
        Assert.Contains("summary.workOrdersCreated", js);
        Assert.Contains("summary.workOrdersReplied", js);
        Assert.Contains("summary.suppressedFindings", js);
        Assert.Contains("summary.corroboratedHostDays", js);
        Assert.Contains("value == null ? '—'", js);
        Assert.Contains("目前無法計算（非 0）", js);
        Assert.Contains("指標使用不同計算對象", js);
        Assert.Contains("目前沒有可計數的資料", js);
        Assert.Contains("summary.lowCoverageSampledHours, summary.prtgFindings", js);
        Assert.Contains("retry.addEventListener('click', loadPrtgEffectiveness)", js);
        Assert.DoesNotContain("resolution rate", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conversion rate", js, StringComparison.OrdinalIgnoreCase);
    }
}
