using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪批次 A4：前端 bug 修正與重複判定收斂的守門。
///
/// 這裡刻意不對「整個檔案」做 Assert.Contains——大型頁面檔案裡同一段字串到處都有，
/// 整檔斷言等於沒有斷言。每條都先把目標函式主體切出來（並確認切到非空），再對主體斷言。
/// </summary>
public class FrontendConsistencyUiTests
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

    private static string JsDir(params string[] parts)
    {
        var segments = new List<string> { FindRepoRoot(), "LogForesight.Web", "wwwroot", "js" };
        segments.AddRange(parts);
        return Path.Combine(segments.ToArray());
    }

    private static string ReadJs(params string[] parts)
    {
        var path = JsDir(parts);
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// 從 header（函式或事件處理的起始樣式）之後的第一個 '{' 起，做大括號配對取出主體。
    /// 掃描時跳過字串與樣板字面值、行／區塊註解，避免裡面的大括號干擾配對。
    /// </summary>
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
            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                    i = SkipString(js, i);
                    continue;
                case '/' when i + 1 < js.Length && js[i + 1] == '/':
                    i = js.IndexOf('\n', i);
                    if (i < 0) i = js.Length - 1;
                    continue;
                case '/' when i + 1 < js.Length && js[i + 1] == '*':
                    i = js.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    Assert.True(i >= 0, "區塊註解沒有結尾");
                    i += 1;
                    continue;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        var body = js.Substring(open, i - open + 1);
                        Assert.False(string.IsNullOrWhiteSpace(body), $"擷取到空主體: {headerPattern}");
                        return body;
                    }

                    break;
            }
        }

        Assert.Fail($"大括號沒有配對成功: {headerPattern}");
        return string.Empty;
    }

    /// <summary>擷取某個模組的 import 名單（大括號內容），確認擷取到非空。</summary>
    private static string ExtractImportList(string js, string modulePath)
    {
        var pattern = @"import\s*\{([^}]*)\}\s*from\s*'" + Regex.Escape(modulePath) + "';";
        var match = Regex.Match(js, pattern);
        Assert.True(match.Success, $"找不到 import ... from '{modulePath}'");
        var list = match.Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(list), $"'{modulePath}' 的 import 名單是空的");
        return list;
    }

    /// <summary>回傳字串字面值結尾引號的索引。樣板字面值內的 ${} 也一併吃掉。</summary>
    private static int SkipString(string js, int start)
    {
        var quote = js[start];
        for (var i = start + 1; i < js.Length; i++)
        {
            if (js[i] == '\\')
            {
                i++;
                continue;
            }

            if (quote == '`' && js[i] == '$' && i + 1 < js.Length && js[i + 1] == '{')
            {
                var depth = 0;
                for (var k = i + 1; k < js.Length; k++)
                {
                    if (js[k] == '{') depth++;
                    else if (js[k] == '}')
                    {
                        depth--;
                        if (depth == 0) { i = k; break; }
                    }
                }

                continue;
            }

            if (js[i] == quote) return i;
        }

        Assert.Fail("字串字面值沒有結尾");
        return start;
    }

    // ── C1：排程頁「執行模式」下拉的未定義變數 ──────────────────────────

    [Fact]
    public void C1_執行模式UI不再引用未定義的daysWrap()
    {
        var runs = ReadJs("pages", "runs.js");

        // Runs.cshtml 的「立即執行」modal 內沒有任何天數包裝元素（run-now-backfill 是恆常顯示的
        // 輸入框、沒有 id 包裝層），所以修法是直接移除該行，而不是改指向某個元素。
        var cshtml = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));
        Assert.DoesNotContain("daysWrap", cshtml);
        Assert.DoesNotContain("daysWrap", runs);

        // 主體仍要具備原有的警示與非預設模式分支
        var body = ExtractBody(runs, @"function updateRerunModeUI\(\)");
        Assert.Contains("run-now-rerun-warning", body);
        Assert.Contains("config.consequence", body);
    }

    // ── C2：AI 判讀失敗不得鎖死按鈕 ────────────────────────────────────

    [Fact]
    public void C2_AI判讀只有成功才鎖按鈕()
    {
        var js = ReadJs("pages", "record-detail.js");
        var handler = ExtractBody(js, @"button\.addEventListener\('click', async event => ");

        Assert.Contains("disabled = true", handler);

        // finally 區塊內不得有 disabled = true——逾時／失敗也會走 finally，鎖了就只能重整整頁
        var finallyBody = ExtractBody(handler, @"\}\s*finally\s*");
        Assert.Contains("restore()", finallyBody);
        Assert.DoesNotContain("disabled", finallyBody);

        // catch 區塊（失敗路徑）同樣不得鎖
        var catchBody = ExtractBody(handler, @"\}\s*catch\s*");
        Assert.DoesNotContain("disabled", catchBody);

        // 成功路徑（renderAiText 那一支）才鎖
        var successIndex = handler.IndexOf("renderAiText(", StringComparison.Ordinal);
        Assert.True(successIndex > 0, "找不到成功路徑的 renderAiText 呼叫");
        var lockIndex = handler.IndexOf("disabled = true", StringComparison.Ordinal);
        Assert.True(lockIndex > successIndex, "鎖定按鈕必須發生在成功取得判讀結果之後");
    }

    // ── C3：PRTG 結構同步的啟動／停止四個處理都要有 catch ─────────────

    [Theory]
    [InlineData("runs.js", @"function bindPrtgSync\(\)")]
    [InlineData("prtg-admin.js", @"function bindStructureSync\(\)")]
    public void C3_結構同步啟動與停止都有catch(string file, string headerPattern)
    {
        var js = ReadJs("pages", file);
        var binder = ExtractBody(js, headerPattern);

        foreach (var action in new[] { "start", "cancel" })
        {
            var marker = $"prtg-structure-sync/{action}";
            var at = binder.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at > 0, $"{file} 找不到 {marker}");

            // 取該 api.post 所在的 try 區塊到後續的 finally，確認中間有 catch
            var tryAt = binder.LastIndexOf("try {", at, StringComparison.Ordinal);
            Assert.True(tryAt > 0, $"{file}/{action} 找不到所屬的 try");
            var finallyAt = binder.IndexOf("finally", at, StringComparison.Ordinal);
            Assert.True(finallyAt > at, $"{file}/{action} 找不到 finally");

            var segment = binder.Substring(tryAt, finallyAt - tryAt);
            Assert.False(string.IsNullOrWhiteSpace(segment));
            Assert.Contains("catch", segment);
        }
    }

    // ── C4：規則驗證鈕要有 withBusy ───────────────────────────────────

    [Fact]
    public void C4_規則驗證鈕有withBusy防連點()
    {
        var js = ReadJs("pages", "rules.js");
        var handler = ExtractBody(js, @"document\.getElementById\('rule-validate'\)\.addEventListener\('click', async [^\r\n]*=> ");

        Assert.Contains("withBusy(", handler);
        Assert.Contains("restore()", handler);
        Assert.Contains("/api/rules/validate", handler);
    }

    // ── C5：spinner 文字 helper 收斂到 core/ui.js ─────────────────────

    [Fact]
    public void C5_SpinnerText僅在coreUi定義並由兩頁呼叫()
    {
        var ui = ReadJs("core", "ui.js");
        Assert.Contains("export function setSpinnerText(", ui);
        var helper = ExtractBody(ui, @"export function setSpinnerText\(container, text\)");
        Assert.Contains("renderSpinner(container, text)", helper);
        Assert.Contains("spinner-border", helper);

        foreach (var file in new[] { "runs.js", "prtg-admin.js" })
        {
            var js = ReadJs("pages", file);

            // 定義為零命中（含改名殘留的舊私有版本）
            Assert.DoesNotContain("function setSpinnerText", js);
            Assert.DoesNotContain("SpinnerText(container, text) {", js);

            // 呼叫與 import 為有命中
            Assert.Contains("setSpinnerText(", js);
            Assert.Contains("setSpinnerText", ExtractImportList(js, "../core/ui.js"));
        }
    }

    // ── C6：aiAvailable 判定收斂到 core/api.js ────────────────────────

    [Fact]
    public void C6_AI可用性收斂到coreApi()
    {
        var api = ReadJs("core", "api.js");
        Assert.Contains("export async function getAiAvailable(", api);

        var fn = ExtractBody(api, @"export async function getAiAvailable\(\)");
        Assert.Contains("/api/ai/status", fn);
        Assert.Contains("aiAvailableCache", fn);
        // 取不到回 null＝還不知道：只做真值判斷的呼叫端仍當不可用，但排程頁比對明確的 false，
        // 暫時性網路失敗才不會被畫成「AI 服務未設定」
        Assert.Contains("return null;", fn);
        Assert.DoesNotContain("return false;", fn);
    }

    [Fact]
    public void C6_Pages底下不得再直接呼叫AiStatus端點()
    {
        var pagesDir = JsDir("pages");
        Assert.True(Directory.Exists(pagesDir), $"找不到目錄: {pagesDir}");

        var offenders = Directory.GetFiles(pagesDir, "*.js", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("/api/ai/status", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("runs.js")]
    [InlineData("records.js")]
    [InlineData("record-detail.js")]
    [InlineData("dashboard.js")]
    [InlineData("netiq.js")]
    public void C6_五個頁面改為匯入getAiAvailable(string file)
    {
        var js = ReadJs("pages", file);
        Assert.Contains("getAiAvailable", ExtractImportList(js, "../core/api.js"));
        Assert.Contains("getAiAvailable(", js);
    }

    [Fact]
    public void C7_Pages底下不得以含插值的樣板字串指派innerHTML()
    {
        var pagesDir = JsDir("pages");
        Assert.True(Directory.Exists(pagesDir), $"找不到目錄: {pagesDir}");

        // 整檔比對（不逐行）：跨行樣板字串也要抓；涵蓋 +=、outerHTML 與 insertAdjacentHTML
        var pattern = new Regex(@"((inner|outer)HTML\s*\+?=\s*|insertAdjacentHTML\s*\([^`]*)`[^`]*\$\{", RegexOptions.Singleline);
        var offenders = Directory.GetFiles(pagesDir, "*.js", SearchOption.AllDirectories)
            .Select(f => (file: Path.GetFileName(f), text: File.ReadAllText(f)))
            .Where(x => pattern.IsMatch(x.text))
            .Select(x => x.file)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void D3_主機頁狀態徽章涵蓋四種PRTG提示()
    {
        var js = ReadJs("pages", "hosts.js");
        Assert.Contains("host.prtgHint", js);
        Assert.Contains("host.prtgHintStale", js);
        Assert.Contains("case 'down': text = 'PRTG：主機失聯'; variant = 'danger'", js);
        Assert.Contains("case 'up': text = 'PRTG：主機在線，問題在日誌取數端'; variant = 'warning'", js);
        Assert.Contains("case 'unknown': text = 'PRTG：無資料'; variant = 'secondary'", js);
        Assert.Contains("case 'no-map': text = '無 PRTG 對應'; variant = 'secondary'", js);
        Assert.Contains("（鏡像過期）", js);
        Assert.Contains("prtgHintBadge(host)", js);
    }

    [Fact]
    public void D3_儀表板未回報卡依PRTG失聯數切換提示()
    {
        var js = ReadJs("pages", "dashboard.js");
        Assert.Contains("data.silentHostsPrtgDownCount > 0", js);
        Assert.Contains("台 PRTG 顯示失聯", js);
        Assert.Contains("'沒回報 ≠ 沒問題'", js);
    }
}
