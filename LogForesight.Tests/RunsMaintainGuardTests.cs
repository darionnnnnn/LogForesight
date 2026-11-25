using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 排程作業頁（runs.js）會被開發維運（DevMonitor、沒有 Maintain）開啟：頁面載入或輪詢時主動送出的 GET，
/// 凡是需要 Maintain 的端點，每一次都會被拒並寫一筆被拒稽核。守門：這類 GET 必須在 Maintain 判斷之內。
/// 「Maintain 端點」由路由快照基準檔推導（任何一組授權條件不含 DevMonitor 即會拒絕 DevMonitor），不另維護一份清單。
/// 非 GET 與使用者按鈕觸發的流程由 <c>data-maintain-only</c> 隱藏按鈕，不在此守門範圍。
/// </summary>
public class RunsMaintainGuardTests
{
    /// <summary>只在「立即執行」確認流程內呼叫的 GET；該流程的入口按鈕沒有 Maintain 看不到</summary>
    private static readonly string[] UserFlowGets =
    {
        "/api/admin/schedule/run-preview",
        "/api/admin/settings/prtg-estimate"
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>快照列：GET 路由且至少一組授權條件不含 DevMonitor（＝DevMonitor 會被拒）</summary>
    private static List<Regex> MaintainOnlyGetRoutes()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "Fixtures", "controller-route-snapshot.txt");
        var routes = new List<Regex>();
        foreach (var line in File.ReadAllLines(path))
        {
            var cols = line.Split('\t');
            if (cols.Length < 3 || cols[0] != "GET" || !cols[1].StartsWith("/api/admin/", StringComparison.Ordinal)) continue;

            var parts = cols[2].Split(';')
                .SelectMany(p => p[(p.IndexOf('=') + 1)..].Split('&'))
                .Where(p => p != "-" && p.Length > 0)
                .ToList();
            if (parts.Count == 0 || parts.All(p => p.Split('|').Contains("DevMonitor"))) continue;

            routes.Add(new Regex("^" + Regex.Replace(Regex.Escape(cols[1]), @"\\\{[^}]*\}", "[^/]+") + "$"));
        }

        return routes;
    }

    private static readonly Regex GetCall = new(
        @"api\.get\(\s*[`'](?<path>/api/admin/[^`'?]*)", RegexOptions.Compiled);

    private static readonly Regex FunctionStart = new(
        @"^(?:async )?function \w+\(", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex EarlyReturnGate = new(
        @"if \(!canMaintainSchedule\)\s*\{[^}]*\breturn\b", RegexOptions.Compiled);

    /// <summary>回傳違規描述（空＝全部在 Maintain 判斷內）：呼叫前要緊接 <c>canMaintainSchedule ?</c>，或同函式先有「沒有 Maintain 就 return」</summary>
    internal static List<string> FindUngatedMaintainGets(string js, IReadOnlyList<Regex> maintainOnly)
    {
        var violations = new List<string>();
        foreach (Match call in GetCall.Matches(js))
        {
            var route = Regex.Replace(call.Groups["path"].Value, @"\$\{[^}]*\}", "x");
            if (UserFlowGets.Contains(route)) continue;
            if (!maintainOnly.Any(r => r.IsMatch(route))) continue;

            var starts = FunctionStart.Matches(js[..call.Index]);
            var functionText = js[(starts.Count == 0 ? 0 : starts[^1].Index)..call.Index];
            var gated = Regex.IsMatch(functionText, @"canMaintainSchedule\s*\?\s*(?:await\s+)?$") || EarlyReturnGate.IsMatch(functionText);
            if (!gated) violations.Add($"{route}（位置 {call.Index}）");
        }

        return violations;
    }

    [Fact]
    public void runs頁面需要Maintain的GET都在Maintain判斷之內()
    {
        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"))
            .Replace("\r\n", "\n");
        var maintainOnly = MaintainOnlyGetRoutes();
        Assert.NotEmpty(maintainOnly);

        // 守門有效性：這三條是已知的 Maintain 端點，必須被推導出來並被掃描到
        Assert.Contains(maintainOnly, r => r.IsMatch("/api/admin/settings"));
        Assert.Contains(maintainOnly, r => r.IsMatch("/api/admin/settings/prtg-backfill/status"));
        Assert.DoesNotContain(maintainOnly, r => r.IsMatch("/api/admin/schedule/status"));
        Assert.True(GetCall.Matches(js).Count >= 6, "掃描不到 runs.js 的 GET 呼叫，守門失效");

        Assert.Empty(FindUngatedMaintainGets(js, maintainOnly));
    }

    [Theory]
    [InlineData("async function f() { const s = await api.get('/api/admin/settings', { silent: true }); }")]
    [InlineData("async function f() { const s = await api.get('/api/admin/settings/prtg-structure-sync/status'); }")]
    // 函式裡別處提到 canMaintainSchedule，但呼叫本身沒有被它擋住
    [InlineData("async function f() { const ok = canMaintainSchedule; const s = await api.get(`/api/admin/settings/prtg-backfill/status`); }")]
    // 三元式的另一支：沒有 Maintain 時才送
    [InlineData("async function f() { const s = !canMaintainSchedule ? 1 : api.get('/api/admin/settings'); }")]
    public void 守門會抓到沒有Maintain判斷的呼叫_反例(string js)
    {
        Assert.NotEmpty(FindUngatedMaintainGets(js, MaintainOnlyGetRoutes()));
    }

    [Theory]
    [InlineData("async function f() { const s = canMaintainSchedule ? await api.get('/api/admin/settings', { silent: true }) : null; }")]
    [InlineData("async function f() { if (!canMaintainSchedule) { return; } const s = await api.get('/api/admin/settings/prtg-backfill/status'); }")]
    // DevMonitor 也能呼叫的端點不需要判斷
    [InlineData("async function f() { const s = await api.get('/api/admin/schedule/status', { silent: true }); }")]
    public void 守門放行有Maintain判斷或DevMonitor可用的呼叫(string js)
    {
        Assert.Empty(FindUngatedMaintainGets(js, MaintainOnlyGetRoutes()));
    }
}
