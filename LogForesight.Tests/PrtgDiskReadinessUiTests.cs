using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskReadinessUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Probe頁載入唯讀分頁磁碟準備度並分開呈現資料與語意狀態()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        var query = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Services", "PrtgDiskReadinessQueryService.cs"));

        Assert.Contains("id=\"prtg-disk-readiness\"", view);
        Assert.Contains("aria-live=\"polite\"", view);
        Assert.Contains("prtg-readiness-retry", view);
        Assert.Contains("prtg-readiness-prev", view);
        Assert.Contains("prtg-readiness-next", view);
        Assert.Contains("/api/prtg/disk-readiness?page=", js);
        Assert.Contains("onChange: name => { if (name === 'probe') queueMicrotask(loadDiskReadiness); }", js);
        Assert.Contains("row.usableDays} / ${row.requiredDays}", js);
        Assert.Contains("row.latestUsableHour", js);
        Assert.Contains("row.semanticVerified", js);
        Assert.Contains("bool SemanticReady", query);
        Assert.Contains("資料累積中", query);
        Assert.Contains("語意未確認", query);
        Assert.Contains("可試算", query);
        Assert.Contains("已失效", query);
        Assert.Contains("PrtgDiskSemanticEvidenceStore", query);
        Assert.Contains("PrtgDiskVerificationResultStore", query);
        Assert.Contains("page.dataReadyOnPage", js);
        Assert.Contains("page.reasonCountsOnPage", js);
        Assert.Contains("page.candidateSensors", js);
        Assert.Contains("前 ${page.readinessSummaryCandidateCount} 顆候選抽樣", js);
        Assert.Contains("missingHourMasks", js);
        Assert.Contains("missingDetails.addEventListener('toggle'", js);
        Assert.Contains("ReadinessSummaryComputed", query);
        Assert.Contains("MissingHourMasks", query);
        Assert.Contains("node.textContent", js);
        Assert.Contains("prtg-probe-flow-start", view);
        Assert.Contains("prtg-probe-start", view);
        Assert.Contains("calibration-export-btn", view);
        Assert.Contains("prtg-selected-backfill-hosts", view);
    }
}
