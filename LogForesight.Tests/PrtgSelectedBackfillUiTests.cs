using Xunit;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LogForesight.Tests;

public sealed class PrtgSelectedBackfillUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 指定主機回填頁提供受控選取預覽確認及狀態操作()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));

        Assert.Contains("data-tab=\"selected-backfill\"", view);
        Assert.Contains("id=\"prtg-selected-backfill-hosts\"", view);
        Assert.Contains("type=\"date\"", view);
        Assert.Contains("最多選 5 台主機、最多 7 天", view);
        Assert.Contains("不會重跑歷史 finding 評估", view);
        Assert.Contains("prtg-selected-backfill-preview", view);
        Assert.Contains("prtg-selected-backfill-start", view);
        Assert.Contains("prtg-selected-backfill-cancel", view);

        Assert.Contains("/api/admin/hosts/all", js);
        Assert.Contains("option", js); // Existing host choices are constructed as DOM nodes, not interpolated HTML.
        Assert.Contains("name.textContent", js);
        Assert.Contains("selectedBackfillHostIds.size >= 5", js);
        Assert.Contains("days > 7", js);
        Assert.Contains("prtg-backfill/selected/preview", js);
        Assert.Contains("prtg-backfill/selected/start", js);
        Assert.Contains("prtg-backfill/status", js);
        Assert.Contains("prtg-backfill/cancel", js);
        Assert.Contains("status.runKind !== 'selected'", js);
        Assert.Contains("{ runId: status.runId }", js);
        Assert.DoesNotContain("selectedBackfillStartedHere", js);
        Assert.Contains("分類可重疊，不能相加；資料/語意就緒為前100抽樣。", js);
        Assert.Contains("confirmAction", js);
        Assert.Contains("preview.estimatedRequests", js);
        Assert.Contains("preview.estimatedSampledRowsToReplace ?? 0", js);
        Assert.Contains("上限估計，非保證", js);
    }

    [Fact]
    public void 指定主機回填沿用快照診斷且名稱不經插值HTML()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));

        Assert.Contains("prtg-snapshot-diagnostics", view);
        Assert.Contains("loadSnapshotDiagnostics", js);
        Assert.DoesNotContain("name.innerHTML", js);
        Assert.DoesNotContain("label.innerHTML", js);
    }

    [Fact]
    public void 缺少小時日期可解析DateTimeJson並以當地日跨DST遞增()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        var match = Regex.Match(js, @"function missingHourDate\([^)]*\)\s*(\{[\s\S]*?\n\})");
        Assert.True(match.Success, "missingHourDate helper must remain directly executable in this test.");

        var psi = new ProcessStartInfo("node", "--input-type=module")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["TZ"] = "America/New_York";
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.WriteLine($"function missingHourDate(windowStart, dayIndex, hour) {match.Groups[1].Value}");
        process.StandardInput.WriteLine("const d = missingHourDate('2026-10-31T00:00:00', 1, 0); if (Number.isNaN(d.getTime()) || d.getFullYear() !== 2026 || d.getMonth() !== 10 || d.getDate() !== 1 || d.getHours() !== 0) throw new Error(`bad local DST date: ${d}`);");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000), "Node.js date behavior test timed out.");
        Assert.True(process.ExitCode == 0, error);
    }
}
