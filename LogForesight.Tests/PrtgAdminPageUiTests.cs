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

    [Theory]
    [InlineData("connection")]
    [InlineData("params")]
    [InlineData("mirror")]
    [InlineData("probe")]
    [InlineData("selected-backfill")]
    [InlineData("effectiveness")]
    public void 每個頁籤開頭都有用途說明_做什麼什麼時候來不會做什麼(string panel)
    {
        var view = View();
        var marker = $"<div data-panel=\"{panel}\"";
        var start = view.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到頁籤 {panel}");
        // 用途區必須緊接在頁籤容器開頭（第一個子元素），不是藏在頁籤底部
        var head = view.Substring(start, Math.Min(900, view.Length - start));
        Assert.Contains("aria-label=\"這個頁籤的用途\"", head);
        Assert.Contains("這頁做什麼", head);
        Assert.Contains("什麼時候來", head);
        Assert.Contains("不會做什麼", head);
    }

    [Fact]
    public void 收合鈕都帶Bootstrap觸發屬性_否則按了展不開()
    {
        // bindRememberedCollapse 只負責初始狀態與記住，不綁 click；觸發靠 data-bs-toggle＋data-bs-target
        var view = View();
        foreach (var (button, target) in new[]
                 {
                     ("prtg-name-mismatch-toggle-btn", "#prtg-name-mismatch-collapse"),
                     ("prtg-monitoring-toggle", "#prtg-monitoring-body")
                 })
        {
            var start = view.IndexOf($"id=\"{button}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, button);
            var tagStart = view.LastIndexOf('<', start);
            var tag = view.Substring(tagStart, view.IndexOf('>', start) - tagStart);
            Assert.Contains("data-bs-toggle=\"collapse\"", tag);
            Assert.Contains($"data-bs-target=\"{target}\"", tag);
        }
    }

    [Fact]
    public void 環境探測與歷史回填的用途說明講清楚範圍()
    {
        var view = View();
        Assert.Contains("結果包含不在主機清單內的裝置", view);
        Assert.Contains("保守策略補全部已對應主機", view);
    }

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

    /// <summary>取出 data-panel 區塊（到下一個頂層 data-panel 為止）。</summary>
    private static string Panel(string view, string name)
    {
        var start = view.IndexOf($"<div data-panel=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 data-panel={name}");
        var next = view.IndexOf("\n<div data-panel=", start + 1, StringComparison.Ordinal);
        return next < 0 ? view[start..] : view[start..next];
    }

    [Fact]
    public void 版面_啟用準備與提示之後第一個區塊就是頁籤_進階區塊收在頁籤內()
    {
        var view = View().Replace("\r\n", "\n");
        var tabsAt = view.IndexOf("<ul class=\"nav nav-tabs mb-3\" id=\"prtg-tabs\">", StringComparison.Ordinal);
        var beforeTabs = view[..tabsAt];
        Assert.Equal(1, Regex.Matches(beforeTabs, "<section").Count);
        Assert.Contains("aria-labelledby=\"prtg-setup-title\"", beforeTabs);

        var parameters = Panel(view, "params");
        Assert.Contains("id=\"prtg-monitoring-form\"", parameters);
        // 監看範圍是獨立表單：必須在擷取參數表單結束之後，不能巢狀
        Assert.True(parameters.IndexOf("</form>", StringComparison.Ordinal) < parameters.IndexOf("id=\"prtg-monitoring-form\"", StringComparison.Ordinal));
        Assert.Matches(new Regex("<div class=\"collapse\" id=\"prtg-monitoring-body\">"), parameters);
        Assert.Contains("data-bs-target=\"#prtg-monitoring-body\" aria-expanded=\"false\" aria-controls=\"prtg-monitoring-body\"", parameters);
        // 同一頁籤至多一個實心主要按鈕
        Assert.Equal(1, Regex.Matches(parameters, "class=\"btn btn-primary").Count);

        Assert.Contains("id=\"prtg-operations-status\"", Panel(view, "effectiveness"));

        var js = Script();
        Assert.Contains("bindRememberedCollapse(document.getElementById('prtg-monitoring-body'),", js);
    }

    /// <summary>體檢輪：§13 第 6 步叫人看快照表的「品質達標」欄，該欄要與驗收判定同一個數（databaseUsableValues）；
    /// 三個驗收來源都只讀一次，段內要有「重新確認」重抓；全部重做勾選在重新預覽後要保留。</summary>
    [Fact]
    public void 快照表品質達標欄與驗收同一個數_驗收段可重新確認_全部重做勾選重畫不洗掉()
    {
        var js = Script();

        var table = FunctionBody(js, "function renderSnapshotDiagnostics(hours)");
        Assert.Contains("title: '品質達標列／目標', render: row => `${formatNumber(row.databaseUsableValues)} / ${formatNumber(row.targets)}`", table);
        Assert.Contains("title: '快照寫入', render: row => formatNumber(row.availableValues)", table);

        var acceptance = FunctionBody(js, "function renderAcceptance(allConfirmed)");
        Assert.Contains("refresh.addEventListener('click', refreshAcceptance)", acceptance);
        var refresh = FunctionBody(js, "function refreshAcceptance()");
        foreach (var call in new[] { "refreshScheduleWarning();", "loadSnapshotDiagnostics();", "refreshAcceptanceAi();" })
            Assert.Contains(call, refresh);

        var preview = FunctionBody(js, "function renderFullBackfillPreview(preview)");
        Assert.True(preview.IndexOf("const redoWas = fullBackfillRedoChecked();", StringComparison.Ordinal) <
                    preview.IndexOf("root.replaceChildren();", StringComparison.Ordinal));
        Assert.Contains("input.checked = redoWas;", preview);
    }

    [Fact]
    public void 全量回填啟動帶完成水位選擇_逐日表標出略過的日子()
    {
        var js = Script();
        Assert.Contains("{ previewId: preview.previewId, confirmed: true, resume: !redo }", js);
        var render = FunctionBody(js, "function renderFullBackfillPreview(preview)");
        Assert.Contains("if (preview.completedDays > 0)", render);
        Assert.Contains("input.id = 'prtg-full-backfill-redo';", render);
        Assert.Contains("day.alreadyCompleted ? '已完成，略過' : '回填'", render);
        Assert.Contains("syncFullBackfillStart(preview);", render);
    }

    [Fact]
    public void E3_鏡像狀態頁只呈現主機清單內的主機_檢視殼不含舊字樣()
    {
        var view = View();
        foreach (var banned in new[] { "鏡像裝置數", "PRTG 獨有", "衝突（多筆）" })
            Assert.DoesNotContain(banned, view);
        Assert.Contains("已對應主機", view);
        Assert.Contains("未對應主機", view);
    }

    [Fact]
    public void 人工對應列表_裝置已不在PRTG鏡像時顯示標示且以textContent呈現()
    {
        var js = Script();
        Assert.Contains("PRTG 已無此裝置", js);
        Assert.Contains("missingInMirror", js);
        Assert.DoesNotContain("innerHTML = `", js.Substring(js.IndexOf("function renderManualMaps", StringComparison.Ordinal), 2500));
    }

    [Fact]
    public void E3_鏡像狀態腳本改走主機端端點_舊分支與欄位已移除_原因都有中文()
    {
        var js = Script();
        foreach (var banned in new[] { "status=unmatched", "'multi-device'", "'hostname-host-taken'", "mapUnmatched", "mapConflict", "deviceCount" })
            Assert.DoesNotContain(banned, js);
        foreach (var required in new[] { "prtg-unmapped-hosts", "prtg-devices/search", "prtg-name-mismatch" })
            Assert.Contains(required, js);
        foreach (var (key, text) in new[] { ("no-ip", "主機沒有 IP"), ("ip-excluded", "IP 已排除"), ("conflict", "此 IP 由多台主機共用"), ("not-in-prtg", "PRTG 找不到此 IP") })
            Assert.Matches(new Regex(@"'?" + Regex.Escape(key) + @"'?\s*:\s*'" + Regex.Escape(text) + "'"), js);
    }
}
