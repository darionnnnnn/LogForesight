using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 任務 F1d-a：在啟動精靈就地完成 AD 驗證設定的 UI 結構與行為測試。
/// </summary>
public class SetupAdInlineUiTests
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

    private static string ExtractBody(string js, string headerPattern)
    {
        var match = Regex.Match(js, headerPattern);
        Assert.True(match.Success, $"找不到起始樣式: {headerPattern}");

        var open = js.IndexOf('{', match.Index + match.Length - 1);
        Assert.True(open >= 0, $"起始樣式之後找不到 '{{': {headerPattern}");

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

        Assert.Fail($"大括號未配對: {headerPattern}");
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
    public void SetupCSHTML包含AD就地表單Template與所有必要欄位與按鈕()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Setup.cshtml");

        Assert.Contains("id=\"setup-ad-template\"", cshtml);
        Assert.Contains("id=\"setup-ad-auth-enabled\"", cshtml);
        Assert.Contains("id=\"setup-ad-servers\"", cshtml);
        Assert.Contains("id=\"setup-ad-search-base\"", cshtml);
        Assert.Contains("id=\"setup-ad-search-filter\"", cshtml);
        Assert.Contains("id=\"setup-ad-test-account\"", cshtml);
        Assert.Contains("id=\"setup-ad-test-password\"", cshtml);
        Assert.Contains("id=\"setup-ad-test-btn\"", cshtml);
        Assert.Contains("id=\"setup-ad-save-btn\"", cshtml);
    }

    [Fact]
    public void AD步驟未完成時走inlinePath且不建立前往設定連結_完成時不展開表單()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var renderSteps = ExtractBody(js, @"function\s+renderSteps\s*\(");

        // 未完成時渲染就地表單
        Assert.Contains("step.id === 'ad' && !step.done", renderSteps);
        Assert.Contains("renderAdInlineForm(step)", renderSteps);

        // 前往設定連結排除 AD 步驟
        Assert.Contains("step.id !== 'ad'", renderSteps);

        // 完成時不展開表單（只在 !step.done 時 appendChild）
        Assert.DoesNotContain("if (step.id === 'ad')\n            body.appendChild(renderAdInlineForm", renderSteps);
    }

    [Fact]
    public void adTestBody含五欄且settingsPUT區塊不含account與password()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");

        // 1. ad-test POST 區塊含有 5 個必要欄位
        var adTestIndex = js.IndexOf("api.post('/api/admin/settings/ad-test'", StringComparison.Ordinal);
        Assert.True(adTestIndex >= 0, "找不到 /api/admin/settings/ad-test 呼叫");
        var adTestBlock = js.Substring(adTestIndex, Math.Min(300, js.Length - adTestIndex));

        Assert.Contains("servers", adTestBlock);
        Assert.Contains("searchBase", adTestBlock);
        Assert.Contains("searchFilter", adTestBlock);
        Assert.Contains("account", adTestBlock);
        Assert.Contains("password", adTestBlock);

        // 2. settings PUT 區塊包含 snapshot 展開與 4 個 AD 欄位，不得含 account 與 password
        var putIndex = js.IndexOf("api.put('/api/admin/settings'", StringComparison.Ordinal);
        Assert.True(putIndex >= 0, "找不到 /api/admin/settings PUT 呼叫");

        var payloadIndex = js.LastIndexOf("const payload = {", putIndex, StringComparison.Ordinal);
        Assert.True(payloadIndex >= 0, "找不到 payload 物件建立");
        var payloadBlock = js.Substring(payloadIndex, putIndex - payloadIndex);

        Assert.Contains("...settingsSnapshot", payloadBlock);
        Assert.Contains("adAuthEnabled", payloadBlock);
        Assert.Contains("adServers", payloadBlock);
        Assert.Contains("adSearchBase", payloadBlock);
        Assert.Contains("adSearchFilter", payloadBlock);

        Assert.DoesNotContain("account", payloadBlock);
        Assert.DoesNotContain("password", payloadBlock);
    }

    [Fact]
    public void 空servers在測試與儲存前由前端擋下()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");

        // 測試連線防護：servers.length === 0 在 api.post 前擋下並 focus
        var testIndex = js.IndexOf("api.post('/api/admin/settings/ad-test'", StringComparison.Ordinal);
        Assert.True(testIndex >= 0);
        var testPrefix = js.Substring(Math.Max(0, testIndex - 1200), Math.Min(1200, testIndex));

        Assert.Contains("servers.length === 0", testPrefix);
        Assert.Contains("serversInput.focus()", testPrefix);
        Assert.Contains("return;", testPrefix);

        // 儲存防護：adAuthEnabled && adServers.length === 0 在 api.put 前擋下並 focus
        var putIndex = js.IndexOf("api.put('/api/admin/settings'", StringComparison.Ordinal);
        Assert.True(putIndex >= 0);
        var savePrefix = js.Substring(Math.Max(0, putIndex - 1200), Math.Min(1200, putIndex));

        Assert.Contains("adAuthEnabled && adServers.length === 0", savePrefix);
        Assert.Contains("serversInput.focus()", savePrefix);
        Assert.Contains("return;", savePrefix);
    }

    [Fact]
    public void 按鈕一律為typeButton且密碼autocomplete為off()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Setup.cshtml");
        var templateStart = cshtml.IndexOf("id=\"setup-ad-template\"", StringComparison.Ordinal);
        Assert.True(templateStart >= 0);
        var templateEnd = cshtml.IndexOf("</template>", templateStart, StringComparison.Ordinal);
        Assert.True(templateEnd >= 0);
        var templateBlock = cshtml.Substring(templateStart, templateEnd - templateStart);

        var buttons = Regex.Matches(templateBlock, @"<button\b[^>]*>");
        Assert.True(buttons.Count >= 2, "Template 中應至少有測試連線與儲存按鈕");
        foreach (Match b in buttons)
        {
            Assert.Contains("type=\"button\"", b.Value);
        }

        Assert.Contains("id=\"setup-ad-test-password\"", templateBlock);
        Assert.Contains("autocomplete=\"off\"", templateBlock);

        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        Assert.Contains("testBtn.type = 'button'", js);
        Assert.Contains("saveBtn.type = 'button'", js);
        Assert.Contains("autocomplete", js);
    }

    [Fact]
    public void 儲存成功後重新呼叫setupStatus並重繪與捲動()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");

        var putIndex = js.IndexOf("api.put('/api/admin/settings'", StringComparison.Ordinal);
        Assert.True(putIndex >= 0);
        var afterPut = js.Substring(putIndex, Math.Min(600, js.Length - putIndex));

        // 成功後 toast、重新 load()（內含 setup/status 與 settings）並將下一個未完成步驟捲入視野
        Assert.Contains("toast(", afterPut);
        Assert.Contains("'success'", afterPut);
        Assert.Contains("await load()", afterPut);
        Assert.Contains("scrollIntoView", afterPut);

        var loadBody = ExtractBody(js, @"async\s+function\s+load\s*\(");
        Assert.Contains("/api/admin/setup/status", loadBody);
        Assert.Contains("/api/admin/settings", loadBody);
        Assert.Contains("render()", loadBody);
    }

    [Fact]
    public void parseServers純函式去除空白與去重驗證()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var body = ExtractBody(js, @"export\s+function\s+parseServers\s*\(");
        var jsCode = @"function parseServers(raw) " + body + @"
const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

const r1 = parseServers('  dc1.corp.local  \n  dc2.corp.local \n dc1.corp.local \n\n  ');
assert(r1.length === 2, `expected 2 servers, got ${r1.length}`);
assert(r1[0] === 'dc1.corp.local', 'first server mismatch');
assert(r1[1] === 'dc2.corp.local', 'second server mismatch');

const r2 = parseServers('');
assert(r2.length === 0, 'empty string should yield empty array');

const r3 = parseServers(null);
assert(r3.length === 0, 'null should yield empty array');

console.log('PASS_PARSE_SERVERS');
";
        var output = RunNode(jsCode);
        Assert.Contains("PASS_PARSE_SERVERS", output);
    }
}
