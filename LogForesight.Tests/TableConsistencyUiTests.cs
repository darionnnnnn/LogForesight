using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 表格歸一守門：全站表格密度統一 table-sm（列高目標 36px），
/// 自建表格一律改走 renderTable／buildKeyValueTable，不再各自拼 class 與排序、空狀態邏輯。
/// </summary>
public class TableConsistencyUiTests
{
    // JS 內自建表格的保留清單：只能放「renderTable 做不到」且在回報中列明理由的處。
    // 目前沒有保留處（所有自建表格都已改用共用元件）；日後新增必須在這裡附理由註解。
    private static readonly string[] AllowedHandBuiltTables = Array.Empty<string>();

    // ui.js 是元件本體；charts.js 的資料表替代與 markdown-lite 的 Markdown 渲染器是刻意豁免
    private static readonly string[] ExemptJsFiles = { "ui.js", "charts.js", "markdown-lite.js" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string WebDir() => Path.Combine(RepoRoot(), "LogForesight.Web");

    [Fact]
    public void 前端自建表格數量等於保留清單()
    {
        var jsRoot = Path.Combine(WebDir(), "wwwroot", "js");
        var files = new[] { "pages", "core" }
            .SelectMany(sub => Directory.GetFiles(Path.Combine(jsRoot, sub), "*.js", SearchOption.AllDirectories))
            .Where(path => !ExemptJsFiles.Contains(Path.GetFileName(path)))
            .ToList();
        Assert.NotEmpty(files);

        var found = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(text, @"createElement\(\s*['""]table['""]\s*\)|<table\b"))
            {
                found.Add($"{Path.GetFileName(path)}: {m.Value}");
            }
        }

        Assert.True(found.Count == AllowedHandBuiltTables.Length,
            "自建表格請改用 ui.js 的 renderTable／buildKeyValueTable；確實做不到才加進保留清單並附理由。違規處：\n"
            + string.Join("\n", found));
    }

    [Fact]
    public void Views的每個table都帶table_sm且被lf_table_wrap包住()
    {
        var viewsDir = Path.Combine(WebDir(), "Views");
        var files = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"<table\b")) continue;

                var where = $"{Path.GetFileName(path)}:{i + 1}";
                if (!Regex.IsMatch(lines[i], @"class=""[^""]*\btable-sm\b"))
                {
                    violations.Add($"{where} 缺 table-sm");
                }

                // 鄰近行檢查：往上最多 3 行內要有 lf-table-wrap 容器
                var wrapped = false;
                for (var j = Math.Max(0, i - 3); j < i; j++)
                {
                    if (lines[j].Contains("lf-table-wrap", StringComparison.Ordinal)) { wrapped = true; break; }
                }

                if (!wrapped) violations.Add($"{where} 沒有被 lf-table-wrap 包住");
            }
        }

        Assert.True(violations.Count == 0, "靜態 <table> 違規：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void renderTable含table_sm與truncate且有鍵值表與截斷樣式()
    {
        var ui = File.ReadAllText(Path.Combine(WebDir(), "wwwroot", "js", "core", "ui.js"));

        var start = ui.IndexOf("export function renderTable(", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 renderTable");
        var end = ui.IndexOf("\nexport function buildKeyValueTable(", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 renderTable 的結尾（buildKeyValueTable 應緊接其後）");
        var body = ui.Substring(start, end - start);

        Assert.Contains("table table-sm table-hover align-middle mb-0", body);
        Assert.Contains("col.truncate", body);
        Assert.Contains("text-truncate", body);

        Assert.Contains("export function buildKeyValueTable(", ui);
        Assert.Matches(@"table table-sm mb-0", ui);

        var css = File.ReadAllText(Path.Combine(WebDir(), "wwwroot", "css", "site.css"));
        Assert.Matches(@"\.lf-table-wrap td\.text-truncate\s*\{[^}]*max-width:\s*18rem", css);
    }

    [Fact]
    public void 單行純文字的主機與問題欄都設truncate()
    {
        // 主機名、問題標籤是全站最長的字串；只顯示純文字（或主機連結）的欄位一律截斷並以 title 看全文。
        // 多行區塊（issueCell、issueGroupCell、hostNameCell 這類含徽章與說明的儲存格）不套 nowrap，不在此列
        var plain = new Regex(@"\{ title: '(主機|問題)', (?:sortKey: '[^']+', )?(?:sortValue: [^,]+, )?render: (\w+) => (?:\2\.hostName|hostLink\(\2\)|textCell\(\2\.hostName\)|\2\.issueLabel) \}");
        var missing = Directory.GetFiles(Path.Combine(WebDir(), "wwwroot", "js", "pages"), "*.js")
            .SelectMany(f => plain.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.True(missing.Count == 0, "缺 truncate 的欄位：\n" + string.Join("\n", missing));
    }
}
