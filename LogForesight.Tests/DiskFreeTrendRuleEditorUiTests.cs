using Xunit;

namespace LogForesight.Tests;

public sealed class DiskFreeTrendRuleEditorUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void DiskFreeTrendEditorUsesDedicatedThresholdsAndPreservesSafeDefaults()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Rules.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "rules.js"));

        Assert.Contains("value=\"disk_free_trend\"", view);
        Assert.Contains("rule-prtg-disk-trend-fields", view);
        Assert.Contains("資料不足", view);
        Assert.Contains("暫定值", view);
        Assert.Contains("百分點／日", view);
        Assert.Contains("最大預估耗盡時間（日）", view);
        Assert.Contains("最低有效資料天數（日）", view);
        Assert.Contains("最低下降日比例（%）", view);

        Assert.Contains("lowWaterPercent: 20", js);
        Assert.Contains("minimumDeclinePercentagePointsPerDay: 0.5", js);
        Assert.Contains("maximumDaysToDepletion: 30", js);
        Assert.Contains("minimumValidDays: 28", js);
        Assert.Contains("recentWindowDays: 35", js);
        Assert.Contains("minimumDecliningDayRatio: 0.70", js);
        Assert.Contains("if (asTemplate && rule?.prtgRuleCode === 'disk_free_trend')", js);
        Assert.Contains("prtgThreshold: platform === 'prtg' && document.getElementById('rule-prtg-code').value !== 'disk_free_trend'", js);
        Assert.Contains("prtgDiskTrendThresholds:", js);
        Assert.Contains("diskTrendThresholdError()", js);
        Assert.Contains("r.prtgDiskTrendThresholds", js);
    }

    [Fact]
    public void DiskTrendDraftPreviewUsesBoundedRequestAndRendersReadOnlyResultSafely()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Rules.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "rules.js"));
        var dto = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Models", "Dto", "RuleDtos.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Controllers", "Api", "RulesController.cs"));

        Assert.Contains("id=\"rule-disk-preview-run\"", view);
        Assert.Contains("id=\"rule-prtg-trend-clear\"", view);
        Assert.Contains("id=\"rule-disk-preview-days\" min=\"1\" max=\"730\"", view);
        Assert.Contains("id=\"rule-disk-preview-limit\" min=\"1\" max=\"100\"", view);
        Assert.Contains("id=\"rule-disk-preview-previous\"", view);
        Assert.Contains("id=\"rule-disk-preview-next\"", view);
        Assert.Contains("id=\"rule-disk-preview-page\"", view);
        Assert.DoesNotContain("rule-disk-preview-offset", view);
        Assert.Contains("id=\"rule-disk-enable-guard\"", view);
        Assert.Contains("6 日資料不等於已驗證", view);
        Assert.Contains("'/api/rules/disk-trend-preview'", js);
        Assert.Contains("fromDate: dateText(from), throughDate: dateText(through), offset, limit, rule: collectRule()", js);
        Assert.Contains("days < 1 || days > 730", js);
        Assert.Contains("日期範圍需為 1–730 個已完成日", js);
        Assert.Contains("through.setDate(through.getDate() - 1)", js);
        Assert.Contains("through.setHours(12, 0, 0, 0)", js);
        Assert.Contains("diskPreviewOffset += Number", js);
        Assert.Contains("error?.message", js);
        Assert.Contains("clearDiskPreview('範圍已變更，請重新試算。')", js);
        Assert.Contains("result.candidateCount", js);
        Assert.Contains("result.dateSensorAssessmentRowCount ?? candidateCount", js);
        Assert.Contains("共 ${totalCount} 組", js);
        Assert.Contains("整段日期範圍（${result.fromDate} 至 ${result.throughDate}）候選配對", js);
        Assert.Contains("result.assessedCount", js);
        Assert.Contains("result.verifiedCandidateCount", js);
        Assert.Contains("result.dataReadyCount", js);
        Assert.Contains("result.semanticReadyCount", js);
        Assert.Contains("result.applicableCount", js);
        Assert.Contains("result.latestPreviewAt", js);
        Assert.Contains("result.eligibleCount", js);
        Assert.Contains("result.hitCount", js);
        Assert.Contains("result.uniqueHitHostCount", js);
        Assert.Contains("result.unsuppressedHitRowCount", js);
        Assert.Contains("不能推算實際交辦量", js);
        Assert.DoesNotContain("上界", js);
        Assert.Contains("不代表全站", js);
        Assert.Contains("實際派工仍受既有閘門控制", js);
        Assert.Contains("if (!diskPreviewHasResult || currentFingerprint !== diskPreviewFingerprint)", js);
        Assert.Contains("啟用磁碟趨勢規則前，請先對目前草稿與頁面執行試算。", js);
        Assert.Contains("latestDiskPreview.latestPreviewAt", js);
        Assert.Contains("if (!rule.enabled && rule.platform === 'prtg' && rule.prtgRuleCode === 'disk_free_trend')", js);
        Assert.Contains("已開啟編輯預覽", js);
        Assert.Contains("approximateForPrtg", js);
        Assert.Contains("粗略歷史估算，不能視為精確影響", js);
        Assert.Contains("result.exclusionCounts", js);
        Assert.Contains("完成日 × sensor", js);
        Assert.Contains("row.verifiedCandidate", js);
        Assert.Contains("row.dataReady", js);
        Assert.Contains("row.semanticReady", js);
        Assert.Contains("row.applicable", js);
        Assert.Contains("row.eligible", js);
        Assert.Contains("沒有足夠資料，無法判斷是否命中", js);
        Assert.Contains("已評估，未達命中條件", js);
        Assert.Contains("exclusionReasonLabel", js);
        Assert.Contains("目前正式站真實 28 日證據仍待確認", view);
        Assert.Contains("rule: collectRule()", js);
        Assert.Contains("document.createElement('li')", js);
        Assert.Contains("item.textContent =", js);
        Assert.Contains("[HttpPost(\"disk-trend-preview\")]", controller);
        Assert.Contains("class DiskTrendRulePreviewRequest", dto);
        Assert.Contains("class DiskTrendPreviewSensorDto", dto);
        Assert.Contains("public bool Eligible", dto);
        Assert.Contains("public bool WouldHit", dto);
        Assert.Contains("public int VerifiedCandidateCount", dto);
        Assert.Contains("public int DataReadyCount", dto);
        Assert.Contains("public int SemanticReadyCount", dto);
        Assert.Contains("public int ApplicableCount", dto);
        Assert.Contains("public bool VerifiedCandidate", dto);
        Assert.Contains("public bool DataReady", dto);
        Assert.Contains("public bool SemanticReady", dto);
        Assert.Contains("public bool Applicable", dto);
        Assert.Contains("public string? ExclusionReason", dto);
        var previewStart = js.IndexOf("// Draft preview is read-only", StringComparison.Ordinal);
        var previewEnd = js.IndexOf("function exclusionReasonLabel", previewStart, StringComparison.Ordinal);
        Assert.True(previewStart >= 0 && previewEnd > previewStart);
        Assert.DoesNotContain("innerHTML", js.Substring(previewStart, previewEnd - previewStart));
    }

    [Fact]
    public void DiskTrendDraftPreviewSupportsSingleCompletedDayAndKeyboardFriendlyResponsiveStates()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Rules.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "rules.js"));

        Assert.Contains("class=\"row g-2 align-items-end\"", view);
        Assert.Contains("col-12 col-sm-6 d-flex gap-2", view);
        Assert.Contains("可用 Tab 鍵操作", view);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", view);
        Assert.Contains("id=\"rule-disk-preview-run\"", view);
        Assert.Contains("id=\"rule-disk-preview-previous\"", view);
        Assert.Contains("id=\"rule-disk-preview-next\"", view);
        Assert.Contains("id=\"rule-prtg-trend-clear\"", view);
        Assert.Contains("正在計算未儲存草稿", js);
        Assert.Contains("沒有足夠資料，無法判斷是否命中", js);
        Assert.Contains("目前無法完成預覽", js);
        Assert.Contains("共 0 頁", js);
        Assert.Contains("rule: collectRule()", js);
        Assert.Contains("enabled: document.getElementById('rule-enabled').checked", js);
        Assert.Contains("if (!editingRule && document.getElementById('rule-prtg-code').value === 'disk_free_trend')", js);
    }
}
