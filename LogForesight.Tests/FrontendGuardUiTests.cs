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

    [Fact]
    public void LocalStorage直接呼叫只出現在白名單檔()
    {
        var allowed = new HashSet<string> { "permission-changes.js", "settings.js", "note-editor.js", "layout.js", "prtg-admin.js", "record-detail.js" };
        var bad = new List<string>();
        foreach (var file in PageFiles().Append(Path.Combine(JsRoot(), "core", "ui.js")))
        {
            var name = Path.GetFileName(file);
            if (allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*")) continue;
                if (lines[i].Contains("localStorage.")) bad.Add($"{name}:{i + 1}: {trimmed}");
            }
        }

        Assert.True(bad.Count == 0, "請改用 core/storage.js 的 safeStorage：" + Environment.NewLine + string.Join(Environment.NewLine, bad));
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
