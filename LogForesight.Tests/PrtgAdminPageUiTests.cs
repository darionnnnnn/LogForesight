using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>PRTG 維護頁（Views/Pages/Prtg.cshtml＋pages/prtg-admin.js）的結構契約（docs/WEB-SPEC.md §9.9e）。</summary>
public sealed class PrtgAdminPageUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string View() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));

    private static string Script() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));

    /// <summary>從宣告處取出整個函式本體（大括號配對）。</summary>
    private static string FunctionBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {declaration}");
        var depth = 0;
        for (var i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException($"{declaration} 未閉合");
    }

    [Fact]
    public void 啟用準備清單六項_不含重整就歸零的本次頁面連線測試()
    {
        var js = Script();
        var summary = FunctionBody(js, "function renderSetupSummary()");

        Assert.DoesNotContain("本次頁面連線測試", js);
        Assert.DoesNotContain("connectionTest", js);
        var labels = Regex.Matches(summary, @"label: '([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "已儲存的連線位址與認證", "PRTG 擷取", "上次結構與主機對應同步", "PRTG 規則", "每日排程", "上次環境探測" }, labels);
    }

    [Fact]
    public void 最近一次排程驗收段在啟用準備卡內_預設隱藏_全部確認後才顯示()
    {
        var view = View();
        var cardStart = view.IndexOf("aria-labelledby=\"prtg-setup-title\"", StringComparison.Ordinal);
        var cardEnd = view.IndexOf("</section>", cardStart, StringComparison.Ordinal);
        var card = view[cardStart..cardEnd];
        Assert.Matches(new Regex("<div id=\"prtg-acceptance\"[^>]*\\bhidden\\b"), card);

        var js = Script();
        Assert.Contains("renderAcceptance(pending === 0);", FunctionBody(js, "function renderSetupSummary()"));
        var render = FunctionBody(js, "function renderAcceptance(allConfirmed)");
        Assert.Contains("evaluateScheduledAcceptance(setupState)", render);
        Assert.Contains("textContent = '最近一次排程驗收'", render);
        // 三個來源都會寫回 setupState：排程狀態、AI 狀態、快照診斷
        Assert.Contains("setupState.scheduleStatus = status;", js);
        Assert.Contains("setupState.aiStatus = await api.get('/api/admin/schedule/ai-status'", js);
        Assert.Contains("setupState.snapshotHours = response?.hours ?? [];", js);
    }
}
