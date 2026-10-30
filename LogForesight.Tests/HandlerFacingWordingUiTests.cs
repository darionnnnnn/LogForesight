using System.Text;
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
}
