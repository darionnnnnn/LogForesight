using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪 G4：對話面板換目標殘留、下載走 api 出口等前端小項的守門。
/// 只切出目標函式主體斷言，避免整檔斷言恆真。
/// </summary>
public class FrontendMiscUiTests
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

    private static string JsRoot() =>
        Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");

    private static string ReadPage(string file) =>
        File.ReadAllText(Path.Combine(JsRoot(), "pages", file)).Replace("\r\n", "\n");

    /// <summary>切出以 declaration 開頭的函式主體（含大括號配對），並確認非空</summary>
    private static string FunctionBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到函式：{declaration}");
        var open = source.IndexOf('{', source.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }

        throw new InvalidOperationException($"函式未閉合：{declaration}");
    }

    [Fact]
    public void 系統健康背景工作表接上郵件帳本提示且不含案件字樣()
    {
        var source = ReadPage("settings.js");
        var render = FunctionBody(source, "function renderBackgroundJobs(");
        Assert.Contains("mailLedgerNotice(detail.mailContributionLedger)", render);
        Assert.Contains("item: '郵件貢獻帳本'", render);
        var notice = FunctionBody(source, "function mailLedgerNotice(");
        Assert.DoesNotContain("案件", notice);
        Assert.DoesNotContain("innerHTML", notice);
    }

    [Fact]
    public void 頁面模組不得直接呼叫fetch或location_assign()
    {
        var pagesDir = Path.Combine(JsRoot(), "pages");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(pagesDir, "*.js"))
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"(?<![\w.])fetch\s*\(") || Regex.IsMatch(text, @"location\.assign\s*\("))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "頁面直接 fetch／location.assign 會繞過逾時、401 導頁與錯誤提示，改走 api.js：" + string.Join("、", offenders));
    }

    [Fact]
    public void 兩處下載都走api_download並以暫時連結儲存()
    {
        var calibration = FunctionBody(ReadPage("prtg-calibration.js"), "async function downloadPackage(");
        Assert.Contains("api.download(", calibration);
        Assert.Contains("saveBlobAsFile(", calibration);
        Assert.Contains("'danger'", calibration);

        var admin = ReadPage("prtg-admin.js");
        var exportIdx = admin.IndexOf("prtg-export?from=", StringComparison.Ordinal);
        Assert.True(exportIdx >= 0, "找不到 PRTG 匯出呼叫");
        var around = admin.Substring(Math.Max(0, exportIdx - 400), 1200);
        Assert.Contains("api.download(", around);
        Assert.Contains("saveBlobAsFile(", around);
        Assert.Contains("'danger'", around);

        var api = File.ReadAllText(Path.Combine(JsRoot(), "core", "api.js")).Replace("\r\n", "\n");
        var save = FunctionBody(api, "export function saveBlobAsFile(");
        Assert.Contains("URL.createObjectURL", save);
        Assert.Contains("URL.revokeObjectURL", save);
        Assert.Contains("removeChild", save);
    }

    [Fact]
    public void 對話面板以世代計數器丟棄舊回覆且收尾依是否已選問題決定輸入框()
    {
        var source = ReadPage("chat-panel.js");
        var reset = FunctionBody(source, "function resetConversation(");
        Assert.Contains("generation++", reset);

        var submit = FunctionBody(source, "async function onSubmit(");
        Assert.Contains("const requestGeneration = generation", submit);
        Assert.Contains("requestGeneration !== generation", submit);
        Assert.Contains("setConversationControlsDisabled(true)", submit);
        Assert.Contains("input.disabled = !hasIssue", submit);
        Assert.DoesNotContain("input.disabled = false;", submit);
    }

    [Fact]
    public void PRTG維護頁六個寫入點都先呼叫requireScopeRevision且不寫死runs路徑()
    {
        var admin = ReadPage("prtg-admin.js");
        Assert.DoesNotContain("'/runs'", admin);
        FunctionBody(admin, "function requireScopeRevision(");

        var sites = Regex.Matches(admin, @"expectedScopeRevision(:\s*|=\$\{)scopeRevision").Select(m => m.Index).ToList();
        Assert.Equal(6, sites.Count);
        foreach (var site in sites)
        {
            var before = admin.Substring(0, site);
            var guardIdx = before.LastIndexOf("requireScopeRevision()", StringComparison.Ordinal);
            var handlerIdx = before.LastIndexOf("addEventListener(", StringComparison.Ordinal);
            Assert.True(handlerIdx >= 0 && guardIdx > handlerIdx,
                $"寫入點（第 {before.Count(c => c == '\n') + 1} 行）所在的事件處理器沒有先呼叫 requireScopeRevision()");
        }

        var view = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        Assert.Matches(@"id=""prtg-full-backfill-preview-result""[^>]*data-runs-url=", view);
    }

    [Fact]
    public void 詳情頁報告各自失敗不拖垮載入且批次儲存後重載經guardLoad()
    {
        var source = ReadPage("record-detail.js");
        var reports = FunctionBody(source, "async function loadReports(");
        Assert.Equal(2, Regex.Matches(reports, @"\.catch\(\(\) => null\)").Count);

        var saved = FunctionBody(source, "async function onBatchSaved(");
        Assert.Contains("guardLoad(", saved);
        Assert.DoesNotContain("await load();", saved);
    }

    [Fact]
    public void 主機清單的有無篩選與查詢參數共用同一份條件()
    {
        var hosts = ReadPage("hosts.js");
        var render = FunctionBody(hosts, "function render(");
        Assert.Contains("hasActiveFilter()", render);
        Assert.Contains("activeFilterEntries()", FunctionBody(hosts, "function hasActiveFilter("));
        Assert.Contains("activeFilterEntries()", FunctionBody(hosts, "function currentFilterParams("));
        Assert.Contains("'prtgMap'", FunctionBody(hosts, "function activeFilterEntries("));
    }

    [Fact]
    public void 預計完成日上限常數集中在處理狀態目錄且與後端一致()
    {
        var statuses = File.ReadAllText(Path.Combine(JsRoot(), "core", "handling-statuses.js"));
        var match = Regex.Match(statuses, @"export const MAX_DUE_DATE_DAYS = (\d+);");
        Assert.True(match.Success, "handling-statuses.js 找不到 MAX_DUE_DATE_DAYS");
        Assert.Equal(LogForesight.Web.Services.WorkOrderReplyService.MaxDueDateDays, int.Parse(match.Groups[1].Value));

        var reply = ReadPage("issue-status-reply.js");
        Assert.Contains("MAX_DUE_DATE_DAYS", reply);
        Assert.Matches(@"(?m)^\s*dueInput\.max = ", reply);   // 排除被註解掉的寫法

        var handler = ReadPage("handler-detail.js");
        Assert.Contains("MAX_DUE_DATE_DAYS", handler);
        Assert.DoesNotContain("const MAX_DUE_DATE_DAYS", handler);
    }

    [Fact]
    public void 負責人匯入確認文字說明既有負責人會被整組取代()
    {
        var imports = ReadPage("imports.js");
        var idx = imports.IndexOf("整組取代", StringComparison.Ordinal);
        Assert.True(idx >= 0, "imports.js 沒有「整組取代」說明");
        Assert.Contains("currentKind === 'Owners'", imports);
        Assert.DoesNotContain("案件", imports.Substring(Math.Max(0, idx - 300), 600));
    }

    [Fact]
    public void 說明書只有首次載入關閉捲動且報表起訖日與範圍chip守門()
    {
        var help = ReadPage("help-manual.js");
        Assert.Equal(1, Regex.Matches(help, @"scroll: false").Count);

        var reports = ReadPage("reports.js");
        var load = FunctionBody(reports, "async function load(");
        Assert.Contains("from > to", load);
        Assert.Contains("syncScopeChips()", load);

        // 點 chip 只發查詢，不再當場改 currentScope／active
        var chipHandler = reports.Substring(reports.IndexOf("report-scope-chips').addEventListener", StringComparison.Ordinal), 500);
        Assert.DoesNotContain("currentScope =", chipHandler);
        Assert.Contains("requestedScope = btn.dataset.scope", chipHandler);
    }
}

/// <summary>G4 小項行為：載入 production 頁面模組的目標函式，以替身驅動。</summary>
public class FrontendMiscBehaviorTests
{
    [NodeTheory]
    [InlineData("requireScopeRevision未載入時擋下並提示")]
    [InlineData("requireScopeRevision已載入時放行")]
    [InlineData("loadReports一份失敗另一份仍顯示")]
    [InlineData("loadReports兩份都失敗也不外擲")]
    [InlineData("hosts只有prtgMap篩選也算有篩選且帶進查詢")]
    [InlineData("reports起日晚於迄日時擋下")]
    [InlineData("reports載入失敗時範圍chip與currentScope不變")]
    [InlineData("reports載入成功時才以回應範圍更新chip")]
    [InlineData("reports起訖日顛倒時清掉待生效範圍")]
    [InlineData("reports載入失敗後清掉待生效範圍")]
    [InlineData("reports已亮起的chip可取消待生效範圍")]
    [InlineData("reports已亮起的chip無待生效範圍時不重查")]
    [InlineData("hostDetail收合後回應不呼叫highlightTimeline")]
    [InlineData("hostDetail仍展開時照常高亮")]
    [InlineData("hostDetail收合再展開沿用快取並高亮")]
    [InlineData("runs天數連點舊回應晚到不蓋新結果")]
    [InlineData("runs舊載入失敗不外擲")]
    [InlineData("runs最新載入失敗要外擲")]
    [InlineData("runs慢的舊loadSummary不覆寫新載入的總表")]
    [InlineData("runs載入期間換總表頁時總表交給較新的請求")]
    [InlineData("說明書首次載入不捲動而一般切章會捲動")]
    [InlineData("郵件帳本提示_未達九成且無滿載紀錄時不顯示")]
    [InlineData("郵件帳本提示_滿載時帶出數字時間與收件人")]
    [InlineData("郵件帳本提示_達九成但無滿載紀錄時顯示接近上限")]
    public void 小項行為案例(string caseName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var scriptDir = Path.Combine(dir!.FullName, "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "misc-pages.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--experimental-default-type=module");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(caseName);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("偵測到 node 卻無法啟動行為測試腳本。", ex);
        }

        Assert.NotNull(process);
        // 先非同步讀輸出、逾時就結束整個程序樹再判失敗，避免實作壞掉時 node 不結束而卡死
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
            Assert.Fail($"node 執行逾時：{caseName}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}

/// <summary>對話面板世代行為：載入 production 的 chat-panel.js，以替身驅動換問題與回覆時序。</summary>
public class ChatPanelGenerationBehaviorTests
{
    [NodeTheory]
    [InlineData("pendingLocksControls")]
    [InlineData("sameConversationReplyKept")]
    [InlineData("staleReplyDropped")]
    [InlineData("staleFailureDropped")]
    [InlineData("finallyKeepsInputDisabledWithoutIssue")]
    [InlineData("舊請求收尾不得重新開啟新請求等待中的控制項")]
    public void 對話面板世代行為案例(string caseName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var scriptDir = Path.Combine(dir!.FullName, "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "chat-panel-generation.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--experimental-default-type=module");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(caseName);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("偵測到 node 卻無法啟動行為測試腳本。", ex);
        }

        Assert.NotNull(process);
        // 先非同步讀輸出、逾時就結束整個程序樹再判失敗，避免實作壞掉時 node 不結束而卡死
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
            Assert.Fail($"node 執行逾時：{caseName}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
