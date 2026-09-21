using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 初始設定引導與頁頂提醒協調（任務 F1c）的前端結構與純函式守門測試。
/// </summary>
public class SetupGuideLayoutUiTests
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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string PanelBlock(string cshtml, string name)
    {
        var start = cshtml.IndexOf($"data-panel=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 data-panel=\"{name}\"");
        var next = cshtml.IndexOf("data-panel=\"", start + 1, StringComparison.Ordinal);
        return next < 0 ? cshtml[start..] : cshtml[start..next];
    }

    private static string ExtractFunctionBody(string js, string functionHeader)
    {
        var match = Regex.Match(js, functionHeader);
        Assert.True(match.Success, $"找不到函式樣式: {functionHeader}");
        var open = js.IndexOf('{', match.Index + match.Length - 1);
        Assert.True(open >= 0, $"找不到 '{{': {functionHeader}");

        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            var c = js[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return js.Substring(open, i - open + 1);
            }
        }

        Assert.Fail($"找不到函式結尾 '}}': {functionHeader}");
        return string.Empty;
    }

    private static string RunNode(string jsCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--experimental-default-type=module --input-type=module",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        proc!.StandardInput.WriteLine(jsCode);
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"Node script failed (exit code {proc.ExitCode}): {stderr}\n{stdout}");
        return stdout;
    }

    [Fact]
    public void 四個來源同時有值_純函式結果只顯示health與run_hiddenCount為2_換順序結果相同_移除health後顯示run與caseSync()
    {
        var jsCode = @"
globalThis.window = { LF_BASE: '' };
const { resolveAlerts } = await import('./LogForesight.Web/wwwroot/js/core/layout.js');
const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

// 1. 四個來源同時有值：純函式結果只顯示 health/run，hiddenCount=2
const r1 = resolveAlerts(['health', 'run', 'case-sync', 'setup']);
assert(r1.visibleKeys.length === 2, 'visibleKeys length should be 2');
assert(r1.visibleKeys[0] === 'health' && r1.visibleKeys[1] === 'run', 'visibleKeys should be health and run');
assert(r1.hiddenCount === 2, 'hiddenCount should be 2');

// 2. 換輸入順序結果相同
const r2 = resolveAlerts(['setup', 'case-sync', 'run', 'health']);
assert(r2.visibleKeys.length === 2, 'visibleKeys length should be 2');
assert(r2.visibleKeys[0] === 'health' && r2.visibleKeys[1] === 'run', 'visibleKeys should be health and run');
assert(r2.hiddenCount === 2, 'hiddenCount should be 2');

// 3. health 移除後顯示 run/case-sync
const r3 = resolveAlerts(['run', 'case-sync', 'setup']);
assert(r3.visibleKeys.length === 2, 'visibleKeys length should be 2');
assert(r3.visibleKeys[0] === 'run' && r3.visibleKeys[1] === 'case-sync', 'visibleKeys should be run and case-sync');
assert(r3.hiddenCount === 1, 'hiddenCount should be 1');

// 4. 僅有兩條或少於兩條時，hiddenCount 為 0
const r4 = resolveAlerts(['run', 'case-sync']);
assert(r4.visibleKeys.length === 2 && r4.hiddenCount === 0, 'hiddenCount should be 0');

console.log('PASS_ALERT_COORDINATION');
";
        var output = RunNode(jsCode);
        Assert.Contains("PASS_ALERT_COORDINATION", output);
    }

    [Fact]
    public void 測試靜態確認非Maintain分支在兩支setupAPI前返回()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        var fnBody = ExtractFunctionBody(js, @"async\s+function\s+loadSetupGuide\s*\(");

        var statusCall = fnBody.IndexOf("/api/admin/setup/status", StringComparison.Ordinal);
        var guideCall = fnBody.IndexOf("/api/me/setup-guide", StringComparison.Ordinal);
        Assert.True(statusCall >= 0, "找不到 /api/admin/setup/status 呼叫");
        Assert.True(guideCall >= 0, "找不到 /api/me/setup-guide 呼叫");

        var earlierCall = Math.Min(statusCall, guideCall);
        var preCheck = fnBody[..earlierCall];

        Assert.Contains("hasCapability", preCheck);
        Assert.Contains("'Maintain'", preCheck);
        Assert.Contains("return", preCheck);
    }

    [Fact]
    public void 測試靜態確認sessionStorage的get與set均在tryCatch中()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        var matches = Regex.Matches(js, @"sessionStorage\.(getItem|setItem)\b");
        Assert.True(matches.Count >= 2, "layout.js 應至少有 sessionStorage getItem 與 setItem");

        foreach (Match m in matches)
        {
            var idx = m.Index;
            var tryIdx = js.LastIndexOf("try {", idx, StringComparison.Ordinal);
            Assert.True(tryIdx >= 0, $"sessionStorage 呼叫未包在 try 區塊中: {m.Value}");
            var catchIdx = js.IndexOf("catch", idx, StringComparison.Ordinal);
            Assert.True(catchIdx > idx, $"sessionStorage 呼叫後未找到對應的 catch: {m.Value}");
        }
    }

    [Fact]
    public void 測試靜態確認fromSetup不直接建立alert()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        var fnBody = ExtractFunctionBody(js, @"function\s+checkSetupReturnParam\s*\(");

        Assert.Contains("params.get('from') === 'setup'", fnBody);
        Assert.Contains("isFromSetup = true", fnBody);
        Assert.Contains("history.replaceState", fnBody);

        // 不得在此函式直接建立或掛載 alert
        Assert.DoesNotContain("alert-info", fnBody);
        Assert.DoesNotContain("createElement", fnBody);
        Assert.DoesNotContain("replaceChildren", fnBody);
        Assert.DoesNotContain("registerAlert", fnBody);
    }

    [Fact]
    public void Settings健康panel有初始設定卡片與按鈕且typeButton且JS_PUT_hidden_false()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        var healthPanel = PanelBlock(cshtml, "health");
        Assert.Contains("id=\"health-setup-guide\"", healthPanel);
        Assert.Contains("初始設定", healthPanel);

        var panelButtons = Regex.Matches(healthPanel, @"<button\b[^>]*>");
        foreach (Match b in panelButtons)
        {
            Assert.Contains("type=\"button\"", b.Value);
        }

        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        Assert.Contains("api.get('/api/me/setup-guide'", js);
        Assert.Contains("api.put('/api/me/setup-guide', { hidden: false })", js);
        Assert.Contains("btn.type = 'button'", js);
        Assert.Contains("重新顯示初始設定引導", js);
        Assert.Contains("引導會在初始設定完成前顯示", js);
    }

    [Fact]
    public void Layout包含頁頂提醒區與摘要容器且runBanner不帶marginPadding()
    {
        var layout = Read("LogForesight.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.Contains("id=\"lf-top-alerts\"", layout);
        Assert.Contains("id=\"lf-setup-return-banner\"", layout);
        Assert.Contains("id=\"lf-health-banner\"", layout);
        Assert.Contains("id=\"lf-run-activity-banner\"", layout);
        Assert.Contains("id=\"lf-alerts-summary\"", layout);

        var line = Array.Find(layout.Split('\n'), l => l.Contains("id=\"lf-run-activity-banner\""));
        Assert.NotNull(line);
        Assert.Contains("lf-no-print", line);
        Assert.DoesNotContain(" mb-", line);
        Assert.DoesNotContain(" p-", line);
    }
}
