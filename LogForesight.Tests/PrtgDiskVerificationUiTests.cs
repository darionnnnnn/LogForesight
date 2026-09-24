using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskVerificationUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 語意驗證工作台以單顆證據和明確人工責任呈現()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));

        Assert.Contains("驗證這顆", js);
        Assert.Contains("/api/prtg/disk-verification/start", js);
        Assert.Contains("/api/prtg/disk-verification/cancel", js);
        Assert.Contains("/api/admin/settings/prtg-disk-verification/batch/start", js);
        Assert.Contains("sensorObjids: ids", js);
        Assert.Contains("row.status === 'Ready'", js);
        Assert.Contains("const requestId = pendingDiskRequestId('single'", js);
        Assert.Contains("const requestId = pendingDiskRequestId('batch'", js);
        Assert.Contains("/evidence", js);
        Assert.Contains("ComparedPointCount", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valuesMatch === true", js);
        Assert.Contains("channelIdentifier:", js);
        Assert.Contains("channelName:", js);
        Assert.Contains("descending-danger", view);
        Assert.Contains("api.post('/api/prtg/disk-verification/confirm'", js);
        Assert.Contains("textContent", js);
        Assert.Contains("aria-live=\"polite\"", view);
        Assert.Contains("人工覆核語意", view);
        Assert.Contains("不會自動啟用規則", view);
        Assert.Contains("只證明 API→資料庫資料流", view);
        Assert.Contains("prtg-probe-start", view);
        Assert.Contains("calibration-export-btn", view);
        Assert.Contains("prtg-snapshot-diagnostics", view);
        Assert.Contains("prtg-selected-backfill-hosts", view);
        Assert.Contains("同一感測器規則試算", view);
        Assert.Contains("/rule-trial", js);
        Assert.Contains("real effect pending", js);
        Assert.Contains("編輯 disk_free_trend 規則", view);
    }

    [Fact]
    public void POST回應遺失後相同參數重送沿用PendingRequestId直到完成()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        var helperStart = js.IndexOf("function pendingDiskRequestId(", StringComparison.Ordinal);
        var helperEnd = js.IndexOf("function updateDiskBatchButton(", helperStart, StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart);
        var helper = js[helperStart..helperEnd];
        Assert.Contains("slot?.signature === signature", helper);
        Assert.Contains("return slot.requestId", helper);
        Assert.Contains("crypto.randomUUID()", helper);
        Assert.Contains("pendingDiskRequestId('single', `${id}|${dataDate}`)", js);
        Assert.Contains("pendingDiskRequestId('batch', `${ids.join(',')}|${dataDate}`)", js);
        Assert.Contains("pendingDiskSingleRequest = null", js);
        Assert.Contains("pendingDiskBatchRequest = null", js);
        var postHandler = js[js.IndexOf("function bindDiskVerification()", StringComparison.Ordinal)..];
        var postCatch = postHandler[..postHandler.IndexOf("document.getElementById('prtg-disk-verification-refresh')", StringComparison.Ordinal)];
        Assert.Contains("catch (error)", postCatch);
        Assert.DoesNotContain("pendingDiskSingleRequest = null", postCatch);
        Assert.DoesNotContain("pendingDiskBatchRequest = null", postCatch);
        var service = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Services", "PrtgDiskVerificationService.cs"));
        Assert.Contains("public bool Cancel() => Volatile.Read(ref _ownsRun) != 0 && _run.TryCancel()", service);
        Assert.DoesNotContain("_runningSensor.HasValue && _run.TryCancel()", service);
        Assert.Contains("if (ct.IsCancellationRequested) { cancelled = true; break; }", service);
        Assert.Contains("await ProbeAndSaveAsync(sensor, request.DataDate, settings, ct)", service);
        var cancellationCheck = service.IndexOf("if (ct.IsCancellationRequested) { cancelled = true; break; }", StringComparison.Ordinal);
        var firstSensorCall = service.IndexOf("await ProbeAndSaveAsync(sensor, request.DataDate, settings, ct)", StringComparison.Ordinal);
        Assert.True(cancellationCheck >= 0 && firstSensorCall > cancellationCheck,
            "A batch cancelled immediately after start must break before any per-sensor PRTG call.");
        var refreshStart = js.IndexOf("async function refreshDiskVerification()", StringComparison.Ordinal);
        var refreshEnd = js.IndexOf("function selectDiskVerificationSensor(", refreshStart, StringComparison.Ordinal);
        var refresh = js[refreshStart..refreshEnd];
        var runningBranchStart = refresh.IndexOf("if (running)", StringComparison.Ordinal);
        var completedBranchStart = refresh.IndexOf("} else {", runningBranchStart, StringComparison.Ordinal);
        Assert.True(runningBranchStart >= 0 && completedBranchStart > runningBranchStart);
        Assert.DoesNotContain("renderDiskVerificationResult", refresh[runningBranchStart..completedBranchStart]);
        Assert.Contains("renderDiskVerificationResult(selectedRun.selectedResult)", refresh[completedBranchStart..]);
        Assert.Contains("批次驗證進行中：${run.batchCompleted}/${run.batchTotal}", refresh);
    }

    [Fact]
    public void E1低Coverage指標說明落盤範圍並與無資料狀態區分()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        Assert.Contains("summary.lowCoverageSampledHours", js);
        Assert.Contains("已落盤 sampled 且 coverage 低於門檻的小時列數；不包含完全缺值", js);
        Assert.Contains("不涵蓋所有磁碟不就緒原因", js);
        Assert.Contains("summary.lowCoverageSampledHours, summary.prtgFindings", js);
    }
}
