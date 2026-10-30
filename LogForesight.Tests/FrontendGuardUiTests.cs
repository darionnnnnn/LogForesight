using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪 G3 的前端結構守門：toast 型別、localStorage 安全存取、清單頁序號守門與失敗態。
/// 能用行為測試的部分見 <see cref="ListPagesBehaviorTests"/>；這裡只守原始碼層級的約定。
/// </summary>
public class FrontendGuardUiTests
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

    private static string JsRoot() => Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");

    private static string ReadPage(string name)
    {
        var path = Path.Combine(JsRoot(), "pages", name);
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

    private static IEnumerable<string> PageFiles() =>
        Directory.GetFiles(Path.Combine(JsRoot(), "pages"), "*.js", SearchOption.AllDirectories);

    [Fact]
    public void Toast第二參數只能是Bootstrap有的型別()
    {
        var allowed = new HashSet<string> { "primary", "secondary", "success", "danger", "warning", "info" };
        var pattern = new Regex(@"\btoast\([^;\n]*?,\s*'([a-zA-Z]+)'", RegexOptions.Compiled);
        var bad = new List<string>();
        foreach (var file in PageFiles())
        {
            foreach (var line in File.ReadAllLines(file))
            {
                foreach (Match m in pattern.Matches(line))
                {
                    if (!allowed.Contains(m.Groups[1].Value)) bad.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(bad.Count == 0, "toast 型別不在 Bootstrap 色系內（會得到不存在的 text-bg-*）：" + Environment.NewLine + string.Join(Environment.NewLine, bad));
    }

    private static IEnumerable<string> CoreAndPageFiles() =>
        Directory.GetFiles(Path.Combine(JsRoot(), "core"), "*.js", SearchOption.AllDirectories)
            .Concat(PageFiles())
            .Where(f => !string.Equals(Path.GetFileName(f), "storage.js", StringComparison.OrdinalIgnoreCase));

    private static bool IsCommentLine(string trimmed) =>
        trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*");

    /// <summary>
    /// 全部 core 與 pages（core/storage.js 本身除外）的 localStorage／sessionStorage 直接呼叫，
    /// 要嘛改用 safeStorage／safeSession，要嘛前幾行內就有 try——封鎖站台資料時直接呼叫會擲 SecurityError，
    /// 模組載入階段執行的呼叫（例如 layout.js 的字級偏好）擲出去就是整個模組起不來。
    /// 白名單只留真正有 try/catch 包住的呼叫；無法用行數判定的檔案不得整檔豁免。
    /// </summary>
    [Fact]
    public void Storage直接呼叫必須改用安全包裝或包在try內()
    {
        var bad = new List<string>();
        foreach (var file in CoreAndPageFiles())
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (IsCommentLine(trimmed)) continue;
                if (!Regex.IsMatch(lines[i], @"\b(localStorage|sessionStorage)\.")) continue;

                var wrapped = false;
                for (var k = Math.Max(0, i - 8); k <= i && !wrapped; k++)
                {
                    wrapped = Regex.IsMatch(lines[k], @"\btry\s*\{");
                }

                if (!wrapped) bad.Add($"{name}:{i + 1}: {trimmed}");
            }
        }

        Assert.True(bad.Count == 0,
            "請改用 core/storage.js 的 safeStorage／safeSession，或確實包在 try/catch 內：" + Environment.NewLine + string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void 載入階段會執行的檔案不直接碰Storage()
    {
        // layout.js 的 initFontScale 在模組載入時就執行；report-view 的展開狀態在每次渲染報告時讀寫
        foreach (var rel in new[] { Path.Combine("core", "layout.js"), Path.Combine("core", "report-view.js") })
        {
            var lines = File.ReadAllLines(Path.Combine(JsRoot(), rel));
            var code = lines.Where(l => !IsCommentLine(l.TrimStart())).ToList();
            Assert.DoesNotContain(code, l => l.Contains("localStorage."));
            Assert.Contains(code, l => l.Contains("safeStorage."));
        }
    }

    [Fact]
    public void 主機詳情的問題明細展開走onRowExpand與onRowToggle且不再手掛click()
    {
        var js = ReadPage("host-detail.js");
        Assert.Contains("onRowExpand: (s, cell)", js);
        Assert.Contains("onRowToggle:", js);
        Assert.DoesNotContain("rowDetail: s => occurrenceDetailPanel", js);
        var panel = js[js.IndexOf("function occurrenceDetailPanel(", StringComparison.Ordinal)..];
        panel = panel[..panel.IndexOf("function renderOccurrencePanel", StringComparison.Ordinal)];
        Assert.DoesNotContain("setTimeout", panel);
        Assert.DoesNotContain("addEventListener", panel);

        var ui = File.ReadAllText(Path.Combine(JsRoot(), "core", "ui.js"));
        Assert.Contains("onRowToggle(row, !open, cell)", ui);
    }

    [Fact]
    public void 天數切換與自動刷新經reloadRuns且不裸呼叫load()
    {
        var js = ReadPage("runs.js");
        Assert.Matches(@"function reloadRuns\(\)\s*\{\s*return guardLoad\(runLoadContainers\(\), load\);", js);
        Assert.DoesNotMatch(@"(?m)^\s+load\(\);", js);
    }

    [Fact]
    public void 權限異動頁查詢失敗不回填空清單()
    {
        var js = ReadPage("permission-changes.js").Replace("\r\n", "\n");
        var start = js.IndexOf("async function load()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = js.IndexOf("function reload()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var body = js[start..end];
        Assert.Contains("throw err;", body);
        // 網段錯誤允許清空（欄位已標紅）；其餘位置不得出現 items: [] 回填
        var emptyFills = Regex.Matches(body, @"items:\s*\[\]").Count;
        Assert.True(emptyFills <= 1, "load 內只有網段錯誤分支可以回填空清單");
        Assert.DoesNotContain("toast(err.message || '查詢失敗'", body);
    }

    [Fact]
    public void 儀表板serverAdmin隱藏清單與guardLoad容器含重點問題()
    {
        var js = ReadPage("dashboard.js").Replace("\r\n", "\n");
        var hide = Regex.Match(js, @"for \(const id of \[([^\]]*)\]\) \{\s*document\.getElementById\(id\)\?\.closest\('\.row'\)");
        Assert.True(hide.Success, "找不到 serverAdmin 隱藏 row 的清單");
        Assert.Contains("'dashboard-issues'", hide.Groups[1].Value);

        var containers = js[js.IndexOf("function dashboardLoadContainers()", StringComparison.Ordinal)..];
        containers = containers[..containers.IndexOf("];", StringComparison.Ordinal)];
        Assert.Contains("dashboard-issues", containers);
        Assert.Contains("dashboard-kpi", containers);
        Assert.Contains("guardLoad(dashboardLoadContainers(), load)", js);
    }

    [Theory]
    [InlineData("hosts.js")]
    [InlineData("audit.js")]
    [InlineData("permission-changes.js")]
    [InlineData("dashboard.js")]
    [InlineData("reports.js")]
    public void 清單頁匯入並使用序號守門(string file)
    {
        var js = ReadPage(file);
        Assert.Contains("createSeqGuard", js);
        Assert.Matches(@"import \{[^}]*createSeqGuard[^}]*\} from '\.\./core/async-guards\.js'", js);
        Assert.Contains("createSeqGuard()", js);
        Assert.Contains(".isCurrent(", js);
    }

    [Theory]
    [InlineData("hosts.js", "guardLoad(listContainer, search)")]
    [InlineData("audit.js", "guardLoad(document.getElementById('audit-list'), search)")]
    [InlineData("permission-changes.js", "guardLoad(document.getElementById('perm-list'), load)")]
    public void 篩選換頁重載經過guardLoad(string file, string expected)
    {
        var js = ReadPage(file);
        Assert.Contains(expected, js);
    }

    [Fact]
    public void 主機頁儲存後重載不再裸呼叫load()
    {
        var js = ReadPage("hosts.js");
        Assert.DoesNotContain("await load();", js);
    }
}
