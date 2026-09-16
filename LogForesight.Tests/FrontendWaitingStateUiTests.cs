using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪批次 B7 的結構守門：五個頁面補載入指示（C3）與權限異動頁的每頁筆數上限（C5 前端）。
///
/// 與 <see cref="FrontendA4CleanupUiTests"/> 同一個紀律——**不對整檔 Assert.Contains**。
/// 大型頁面檔案裡 `guardLoad`／`renderLoading` 到處都有，整檔斷言等於沒有斷言：
/// 每條都先把目標函式主體切出來（並確認切到非空），再對主體斷言。
/// </summary>
public class FrontendWaitingStateUiTests
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

    private static string ReadJs(params string[] parts)
    {
        var segments = new List<string> { FindRepoRoot(), "LogForesight.Web", "wwwroot", "js" };
        segments.AddRange(parts);
        var path = Path.Combine(segments.ToArray());
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>從 header 之後的第一個 '{' 起做大括號配對取出主體（字串／樣板／註解內的括號會跳過）。</summary>
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

    // ── C3：五個頁面的載入流程都有載入指示且被 guardLoad 包住 ────────────────

    public static TheoryData<string, string, string> LoadFlows() => new()
    {
        // 頁面檔、載入函式的起始樣式、被包住的內層流程函式名
        { "reports.js", @"async function load\(\)", "loadReport" },
        { "settings.js", @"async function load\(\)", "loadSettings" },
        { "prtg-admin.js", @"async function loadSettings\(\)", "loadPrtgSettings" },
        { "prtg-calibration.js", @"async function runAssessment\(\)", "" },
        { "help-manual.js", @"async function load\(\)", "loadManual" }
    };

    [Theory]
    [MemberData(nameof(LoadFlows))]
    public void C3_五個頁面的載入流程都有載入指示且被guardLoad包住(string file, string header, string innerFn)
    {
        var js = ReadJs("pages", file);
        var body = ExtractBody(js, header);

        Assert.Contains("guardLoad(", body);
        Assert.True(
            body.Contains("renderLoading(") || body.Contains("renderSpinner("),
            $"{file} 的載入函式主體沒有骨架列／載入指示：{body}");

        // 骨架必須在取數之前放上去，否則等待期間畫面依然毫無變化
        var indicator = Math.Min(
            body.Contains("renderLoading(") ? body.IndexOf("renderLoading(", StringComparison.Ordinal) : int.MaxValue,
            body.Contains("renderSpinner(") ? body.IndexOf("renderSpinner(", StringComparison.Ordinal) : int.MaxValue);
        Assert.True(indicator < body.IndexOf("guardLoad(", StringComparison.Ordinal),
            $"{file} 的載入指示必須在 guardLoad 之前放上");

        if (innerFn.Length > 0)
        {
            // 實際取數搬到內層函式，且確實被 guardLoad 包住（不是各自獨立跑）
            Assert.Contains(innerFn, body);
            var innerBody = ExtractBody(js, $@"async function {Regex.Escape(innerFn)}\(\)");
            Assert.Contains("api.get(", innerBody);
        }
        else
        {
            Assert.Contains("api.get(", body);
        }
    }

    [Fact]
    public void C3_五個頁面都從共用UI模組匯入載入指示元件()
    {
        foreach (var file in new[] { "reports.js", "settings.js", "prtg-admin.js", "prtg-calibration.js", "help-manual.js" })
        {
            var js = ReadJs("pages", file);
            var match = Regex.Match(js, @"import\s*\{([^}]*)\}\s*from\s*'\.\./core/ui\.js';", RegexOptions.Singleline);
            Assert.True(match.Success, $"{file} 找不到 core/ui.js 的 import");
            var list = match.Groups[1].Value;
            Assert.Contains("guardLoad", list);
            Assert.True(list.Contains("renderLoading") || list.Contains("renderSpinner"),
                $"{file} 沒有匯入共用的骨架／載入元件");
        }
    }

    // ── C5：前端不送出超過上限的每頁筆數 ────────────────────────────────

    [Fact]
    public void C5_網址參數的每頁筆數被夾在上限內()
    {
        var js = ReadJs("pages", "permission-changes.js");

        var maxMatch = Regex.Match(js, @"const MAX_PAGE_SIZE = (\d+);");
        Assert.True(maxMatch.Success, "permission-changes.js 沒有定義每頁筆數上限");

        // 與後端 Paging.Normalize 的預設上限同值——不一致就會出現「顯示筆數與實際拿到的對不上」
        Assert.Equal(200, int.Parse(maxMatch.Groups[1].Value));

        // 只擷取讀取 pageSize 參數的那段（整檔比對會撞到分頁列與請求組裝處的同名字串）
        var branch = ExtractBody(js, @"if \(params\.has\('pageSize'\)\)");
        Assert.Contains("Math.min(ps, MAX_PAGE_SIZE)", branch);
        Assert.DoesNotContain("pageSize = ps;", branch);
    }
}
