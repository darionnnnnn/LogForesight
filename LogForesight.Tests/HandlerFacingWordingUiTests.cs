using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers;
using LogForesight.Web.Filters;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪 E6：處理人可見畫面用詞守門。
/// CLAUDE.md 紅線：處理人側使用「我的交辦」「交辦單」「主機」「狀態」，「案件」只留在管理與進階說明。
/// 掃 wwwroot/js/pages 與 core 的字串字面量（單引號、雙引號、反引號內容，排除註解），不得含「案件」；
/// 管理者專屬頁與稽核頁列入白名單。
/// </summary>
public class HandlerFacingWordingUiTests
{
    private static readonly string[] Whitelist = { "user-detail.js", "prtg-admin.js", "runs.js", "audit.js" };

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

    /// <summary>抽出 JS 原始碼中所有字串字面量的內容（略過 // 與 /* */ 註解；單雙引號字串遇換行即重置）</summary>
    internal static List<(int Line, string Text)> ExtractStringLiterals(string src)
    {
        var result = new List<(int, string)>();
        var line = 1;
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                i += 2;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`')
            {
                var quote = c;
                var startLine = line;
                var sb = new StringBuilder();
                i++;
                while (i < src.Length)
                {
                    var d = src[i];
                    if (d == '\\' && i + 1 < src.Length) { sb.Append(src[i + 1]); i += 2; continue; }
                    if (d == quote) { i++; break; }
                    if (d == '\n')
                    {
                        if (quote != '`') break; // 單雙引號字串不跨行：視為誤判（如正規表示式內的引號），重置
                        line++;
                    }
                    sb.Append(d);
                    i++;
                }
                result.Add((startLine, sb.ToString()));
                continue;
            }
            i++;
        }
        return result;
    }

    [Fact]
    public void 掃描器能抽出字串字面量並略過註解()
    {
        var src = "// 案件 註解\nconst a = '案件甲';\n/* 案件\n區塊 */ const b = \"乙\"; const c = `丙${x}`;\n";
        var literals = ExtractStringLiterals(src);
        Assert.Contains(literals, l => l.Text == "案件甲" && l.Line == 2);
        Assert.DoesNotContain(literals, l => l.Text.Contains("註解") || l.Text.Contains("區塊"));
        Assert.Contains(literals, l => l.Text == "乙");
        Assert.Contains(literals, l => l.Text.StartsWith("丙"));
    }

    [Fact]
    public void 處理人可見的前端字串不得出現案件()
    {
        var jsRoot = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");
        var files = Directory.GetFiles(Path.Combine(jsRoot, "pages"), "*.js")
            .Concat(Directory.GetFiles(Path.Combine(jsRoot, "core"), "*.js"))
            .Where(f => !Whitelist.Contains(Path.GetFileName(f)))
            .ToList();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            foreach (var (line, text) in ExtractStringLiterals(File.ReadAllText(file)))
            {
                if (text.Contains("案件"))
                {
                    violations.Add($"{Path.GetFileName(file)}:{line} {text}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "處理人可見字串不得含「案件」（改用「交辦」）：\n" + string.Join("\n", violations));
    }

    // ── 第 61 輪 M-14：擴充到 Razor 頁面殼與郵件文案 ─────────────────────────

    /// <summary>
    /// 管理者頁（排除在處理人用詞守門之外）以反射推導，不手寫清單：
    /// PagesController 中掛 [Permission] 且所需能力含 Maintain 的 action，其同名 view 視為管理者頁。
    /// </summary>
    internal static HashSet<string> AdminViewNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in typeof(PagesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var attr in method.GetCustomAttributes<PermissionAttribute>())
            {
                var capabilities = (Capability[])attr.Arguments![0];
                if (capabilities.Contains(Capability.Maintain)) names.Add(method.Name);
            }
        }

        return names;
    }

    /// <summary>移除 Razor 註解 @* ... *@ 與 HTML 註解 &lt;!-- ... --&gt;（保留換行，行號不變）</summary>
    internal static string StripCshtmlComments(string src)
    {
        static string Blank(Match m) => Regex.Replace(m.Value, @"[^\r\n]", " ");
        src = Regex.Replace(src, @"@\*.*?\*@", Blank, RegexOptions.Singleline);
        return Regex.Replace(src, @"<!--.*?-->", Blank, RegexOptions.Singleline);
    }

    /// <summary>移除 C# 的 // 與 /* */ 註解（含 /// 文件註解），字串／逐字字串／原始字串／字元常值內的斜線不算註解；保留換行</summary>
    internal static string StripCSharpComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n' && src[i] != '\r') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? src.Length : end + 2;
                for (; i < end; i++) sb.Append(src[i] == '\n' || src[i] == '\r' ? src[i] : ' ');
                continue;
            }
            if (c == '"' || ((c == '$' || c == '@') && IsStringPrefix(src, i)))
            {
                var j = i;
                var verbatim = false;
                while (src[j] == '$' || src[j] == '@') { if (src[j] == '@') verbatim = true; j++; }
                var quotes = 0;
                while (j + quotes < src.Length && src[j + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    var closing = new string('"', quotes);
                    var end = src.IndexOf(closing, j + quotes, StringComparison.Ordinal);
                    end = end < 0 ? src.Length : end + quotes;
                    sb.Append(src, i, end - i);
                    i = end;
                    continue;
                }
                var k = j + 1;
                while (k < src.Length)
                {
                    if (verbatim)
                    {
                        if (src[k] == '"' && k + 1 < src.Length && src[k + 1] == '"') { k += 2; continue; }
                        if (src[k] == '"') break;
                    }
                    else
                    {
                        if (src[k] == '\\') { k += 2; continue; }
                        if (src[k] == '"' || src[k] == '\n') break;
                    }
                    k++;
                }
                var stop = Math.Min(k + 1, src.Length);
                sb.Append(src, i, stop - i);
                i = stop;
                continue;
            }
            if (c == '\'' && i + 2 < src.Length)
            {
                var closeAt = src.IndexOf('\'', src[i + 1] == '\\' ? i + 3 : i + 2);
                var stop = closeAt > 0 && closeAt < i + 8 ? closeAt + 1 : i + 1;
                sb.Append(src, i, stop - i);
                i = stop;
                continue;
            }
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsStringPrefix(string s, int i)
    {
        var j = i;
        while (j < s.Length && (s[j] == '$' || s[j] == '@')) j++;
        return j < s.Length && s[j] == '"' && j - i <= 2;
    }

    private static List<string> FindWord(string text, string relative, string word)
    {
        var hits = new List<string>();
        var lines = text.Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            if (lines[n].Contains(word)) hits.Add($"{relative}:{n + 1} {lines[n].Trim()}");
        }

        return hits;
    }

    [Fact]
    public void 註解移除器能處理Razor與CSharp註解()
    {
        var cshtml = StripCshtmlComments("<p>可見</p>\n@* 案件 *@\n<!-- 案件\n多行 -->\n<p>案件X</p>");
        Assert.Single(Regex.Matches(cshtml, "案件"));
        Assert.Contains("可見", cshtml);

        var cs = StripCSharpComments("// 案件\n/// 案件\nvar a = \"案件甲 // 不是註解\"; /* 案件 */ var b = @\"乙\"\"//\"; var c = '/';");
        Assert.Single(Regex.Matches(cs, "案件"));
        Assert.Contains("案件甲 // 不是註解", cs);
    }

    [Fact]
    public void 管理者頁以反射推導且包含已知的Maintain頁()
    {
        var admin = AdminViewNames();
        foreach (var expected in new[] { "Rules", "Users", "UserDetail", "Hosts", "Groups", "IssueOwners", "Imports", "Netiq", "Prtg", "Settings", "Runs" })
            Assert.Contains(expected, admin);
        foreach (var handlerFacing in new[] { "Records", "Dashboard", "HandlerDetail", "WorkOrderDetail", "HelpManual" })
            Assert.DoesNotContain(handlerFacing, admin);
    }

    [Fact]
    public void 處理人可見的Razor頁面殼不得出現案件()
    {
        var viewsDir = Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages");
        var admin = AdminViewNames();
        var all = Directory.GetFiles(viewsDir, "*.cshtml");
        var scanned = all.Where(f => !admin.Contains(Path.GetFileNameWithoutExtension(f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(scanned);

        var violations = new List<string>();
        foreach (var file in scanned)
        {
            var text = StripCshtmlComments(File.ReadAllText(file));
            violations.AddRange(FindWord(text, Path.GetFileName(file), "案件"));
        }

        var excluded = string.Join("、", all.Select(Path.GetFileNameWithoutExtension).Where(n => admin.Contains(n!)).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(violations.Count == 0,
            "處理人可見的頁面殼不得含「案件」（改用「交辦」）：\n" + string.Join("\n", violations) + "\n（已排除的管理者頁：" + excluded + "）");
    }

    [Fact]
    public void 郵件文案的字串不得出現案件()
    {
        var mailDir = Path.Combine(FindRepoRoot(), "LogForesight.Web", "Services", "Mail");
        var files = Directory.GetFiles(mailDir, "*.cs");
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = StripCSharpComments(File.ReadAllText(file));
            violations.AddRange(FindWord(text, Path.GetFileName(file), "案件"));
        }

        Assert.True(violations.Count == 0,
            "郵件文案不得含「案件」（改用「交辦」）：\n" + string.Join("\n", violations));
    }
    // ── 第 76-2 輪 G6：陸詞守門（保存、刷新、數據、點擊）──────────────────────────
    // 範圍是 LogForesight.Web 內使用者看得到的文字：JS 字串字面量、Razor view 的非註解文字、
    // HelpContent 的 .md／manifest.json、C# 字串字面量。程式碼註解是開發者用語，不列入（WEB-SPEC §8.6a）。
    // 台灣用語：點擊→點選、保存→儲存／保留、刷新→重新整理／更新、數據→資料／數值。
    // 白名單：目前沒有任何例外；若日後必須保留，逐項寫「相對路徑 → 理由」。
    private static readonly string[] MainlandTerms = { "保存", "刷新", "數據", "點擊" };

    private static readonly Dictionary<string, string> MainlandWhitelist = new(StringComparer.Ordinal);

    /// <summary>移除 cshtml 中 &lt;script&gt; 區塊內的 // 行註解與 /* */ 註解（保留換行；字串內的斜線由 JS 字串抽取處理）</summary>
    internal static string StripCshtmlScriptComments(string src)
    {
        return Regex.Replace(src, @"(<script\b[^>]*>)(.*?)(</script>)", m =>
        {
            var body = m.Groups[2].Value;
            var kept = new StringBuilder();
            var lines = body.Split('\n');
            foreach (var l in lines)
            {
                var literals = ExtractStringLiterals(l);
                // 逐行：註解之外只留字串字面量內容與其餘非註解文字；以抽取結果重組即可
                kept.Append(string.Join(" ", literals.Select(x => x.Text))).Append('\n');
            }

            return m.Groups[1].Value + kept + m.Groups[3].Value;
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }

    /// <summary>單一檔案的掃描：回傳含四個詞之一的「相對路徑:行 內容」</summary>
    internal static List<string> ScanMainlandTerms(string kind, string relative, string content)
    {
        var hits = new List<string>();
        switch (kind)
        {
            case "js":
                foreach (var (line, text) in ExtractStringLiterals(content))
                    foreach (var w in MainlandTerms.Where(text.Contains))
                        hits.Add($"{relative}:{line} 「{w}」 {text.Trim()}");
                break;
            case "cs":
                foreach (var w in MainlandTerms)
                    hits.AddRange(FindWord(StripCSharpComments(content), relative, w));
                break;
            case "cshtml":
                var view = StripCshtmlScriptComments(StripCshtmlComments(content));
                foreach (var w in MainlandTerms)
                    hits.AddRange(FindWord(view, relative, w));
                break;
            default: // md、json：全文都是使用者可見文字；md 內的 HTML 註解不算
                var doc = Regex.Replace(content, @"<!--.*?-->", m => Regex.Replace(m.Value, @"[^\r\n]", " "), RegexOptions.Singleline);
                foreach (var w in MainlandTerms)
                    hits.AddRange(FindWord(doc, relative, w));
                break;
        }

        return hits;
    }

    [Fact]
    public void 陸詞守門能抓到四個詞且不誤抓註解()
    {
        foreach (var w in MainlandTerms)
        {
            Assert.NotEmpty(ScanMainlandTerms("js", "x.js", $"const a = '請{w}這裡';"));
            Assert.NotEmpty(ScanMainlandTerms("js", "x.js", $"const a = `請{w}${{n}}`;"));
            Assert.NotEmpty(ScanMainlandTerms("cs", "X.cs", $"var m = \"請{w}這裡\";"));
            Assert.NotEmpty(ScanMainlandTerms("cshtml", "X.cshtml", $"<p>請{w}這裡</p>"));
            Assert.NotEmpty(ScanMainlandTerms("cshtml", "X.cshtml", $"<script>\nconst a = '請{w}';\n</script>"));
            Assert.NotEmpty(ScanMainlandTerms("md", "x.md", $"# 標題\n\n請{w}這裡。"));

            // 註解內的詞不得被抓（證明註解排除生效）
            Assert.Empty(ScanMainlandTerms("js", "x.js", $"// 請{w}\n/* 請{w} */\nconst a = '正常';"));
            Assert.Empty(ScanMainlandTerms("cs", "X.cs", $"// 請{w}\n/// 請{w}\n/* 請{w} */\nvar m = \"正常\";"));
            Assert.Empty(ScanMainlandTerms("cshtml", "X.cshtml", $"@* 請{w} *@\n<!-- 請{w} -->\n<p>正常</p>\n<script>\n// 請{w}\nconst a = '正常';\n</script>"));
            Assert.Empty(ScanMainlandTerms("md", "x.md", $"<!-- 請{w} -->\n正常"));
        }
    }

    [Fact]
    public void Web使用者可見文字不得出現陸詞()
    {
        var web = Path.Combine(FindRepoRoot(), "LogForesight.Web");
        static bool Skip(string p) => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            || p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            || p.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")
            || p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}");

        var targets = new List<(string Kind, string Path)>();
        foreach (var (pattern, kind, root) in new[]
        {
            ("*.js", "js", Path.Combine(web, "wwwroot", "js")),
            ("*.cs", "cs", web),
            ("*.cshtml", "cshtml", web),
            ("*.md", "md", Path.Combine(web, "HelpContent")),
            ("*.json", "md", Path.Combine(web, "HelpContent")),
        })
        {
            foreach (var p in Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Where(p => !Skip(p)))
                targets.Add((kind, p));
        }

        Assert.True(targets.Count > 200, "掃描範圍異常偏小：" + targets.Count);

        var violations = new List<string>();
        foreach (var (kind, path) in targets)
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (MainlandWhitelist.ContainsKey(relative)) continue;
            violations.AddRange(ScanMainlandTerms(kind, relative, File.ReadAllText(path)));
        }

        Assert.True(violations.Count == 0,
            "使用者可見文字不得含陸詞（保存→儲存／保留、刷新→重新整理／更新、數據→資料／數值、點擊→點選）：\n" + string.Join("\n", violations));
    }
}
