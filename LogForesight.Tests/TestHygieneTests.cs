using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 測試衛生守門：每個 [Fact]／[Theory]（含自訂的 *Fact／*Theory 屬性）的方法本體都必須有驗證動作。
/// 沒有斷言的測試永遠綠，等於沒測；意圖是「不擲例外」的要寫成 <c>Record.Exception</c> 再斷言為 null。
/// 掃描前先把註解、字串、字元常值抹成空白（避免字串裡的大括號或 Assert 字樣干擾），再以大括號配對取本體；
/// 運算式主體（<c>=&gt;</c>）取到第一個頂層分號。
/// </summary>
public sealed class TestHygieneTests
{
    private static readonly Regex TestAttribute = new(
        @"\[\s*(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?<attr>[A-Za-z0-9_]*(?:Fact|Theory))\b[^\]]*\]",
        RegexOptions.Compiled);

    private static readonly Regex MoreAttributes = new(@"\G\s*\[[^\]]*\]", RegexOptions.Compiled);

    private static readonly Regex MethodHead = new(
        @"\G\s*(?:public|internal|protected|private)?\s*(?:static\s+)?(?:async\s+)?[A-Za-z_][A-Za-z0-9_<>,\.\?\[\]\s\(\)]*?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Verifies = new(
        @"Assert|Throws|Record\.Exception|Should|Verify|Expect|Received|\bFail\(|\bCheck\w*\(|\bEnsure\w*\(|\bMust\w*\(|\bSnapshot\w*\(",
        RegexOptions.Compiled);

    [Fact]
    public void 所有測試方法都含有驗證動作()
    {
        var offenders = FindTestsWithoutAssertions(TestsDirectory());
        Assert.True(offenders.Count == 0,
            "下列測試方法沒有任何斷言（補斷言，或改用 Record.Exception 並 Assert.Null）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 掃描器能抓到空測試並放過有斷言的測試()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-hygiene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Sample.cs"), string.Join("\n", new[]
            {
                "public class S",
                "{",
                "    [Fact] public void Empty() { }",
                "    [Fact] public void StringOnly() { var s = \"Assert { \"; }",
                "    [Theory, InlineData(1)] public void TheoryEmpty(int x) { // Assert.True(true)",
                "    }",
                "    [Fact] public void Good() { Assert.True(true); }",
                "    [Fact] public async Task GoodExpr() => Assert.Equal(1, await Task.FromResult(1));",
                "    [Fact] public void GoodRecord() { var e = Record.Exception(() => { }); Assert.Null(e); }",
                "    [Fact] public void HelperStyle() { AssertSomething(); }",
                "    [Fact] public void RegexBraces() { var r = new Regex(@\"\\{(.+?)\\}\"); Assert.NotNull(r); }",
                "}",
            }));
            var found = FindTestsWithoutAssertions(dir).Select(x => x.Split(' ').Last()).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Empty", "StringOnly", "TheoryEmpty" }, found);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string TestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return Path.Combine(dir!.FullName, "LogForesight.Tests");
    }

    private static List<string> FindTestsWithoutAssertions(string directory)
    {
        var result = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var code = Blank(File.ReadAllText(file));
            foreach (Match attr in TestAttribute.Matches(code))
            {
                var pos = attr.Index + attr.Length;
                // 吃掉同一方法上的其他屬性群組
                while (true)
                {
                    var more = MoreAttributes.Match(code, pos);
                    if (!more.Success) break;
                    pos = more.Index + more.Length;
                }
                var head = MethodHead.Match(code, pos);
                if (!head.Success) continue;
                var open = head.Index + head.Length - 1;          // '(' 的位置
                var close = MatchClose(code, open, '(', ')');
                if (close < 0) continue;
                var bodyStart = close + 1;
                while (bodyStart < code.Length && char.IsWhiteSpace(code[bodyStart])) bodyStart++;
                string body;
                if (bodyStart + 1 < code.Length && code[bodyStart] == '=' && code[bodyStart + 1] == '>')
                {
                    var end = FindStatementEnd(code, bodyStart + 2);
                    body = code[bodyStart..end];
                }
                else if (bodyStart < code.Length && code[bodyStart] == '{')
                {
                    var end = MatchClose(code, bodyStart, '{', '}');
                    if (end < 0) continue;
                    body = code[bodyStart..(end + 1)];
                }
                else continue;     // 抽象或無本體，不是實際測試
                if (!Verifies.IsMatch(body))
                    result.Add(Path.GetFileName(file) + " " + head.Groups["name"].Value);
            }
        }
        return result;
    }

    private static int MatchClose(string s, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            if (s[i] == open) depth++;
            else if (s[i] == close && --depth == 0) return i;
        }
        return -1;
    }

    private static int FindStatementEnd(string s, int start)
    {
        int paren = 0, brace = 0;
        for (var i = start; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '{': brace++; break;
                case '}': brace--; break;
                case ';' when paren == 0 && brace == 0: return i;
            }
        }
        return s.Length;
    }

    /// <summary>把註解、字串（一般／逐字／插值／原始字串）與字元常值的內容換成空白，長度與換行位置不變。</summary>
    private static string Blank(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        void Blanks(int from, int to)
        {
            for (var k = from; k < to && k < src.Length; k++) sb.Append(src[k] == '\n' ? '\n' : ' ');
        }
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                var end = src.IndexOf('\n', i);
                if (end < 0) end = src.Length;
                Blanks(i, end); i = end; continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? src.Length : end + 2;
                Blanks(i, end); i = end; continue;
            }
            // 字串前綴 $ @ 的任意組合
            var j = i;
            var verbatim = false;
            while (j < src.Length && (src[j] == '$' || src[j] == '@'))
            {
                if (src[j] == '@') verbatim = true;
                j++;
            }
            if (j < src.Length && src[j] == '"' && j - i <= 2)
            {
                // 原始字串 """ ... """
                var q = 0;
                while (j + q < src.Length && src[j + q] == '"') q++;
                if (q >= 3)
                {
                    var closing = new string('"', q);
                    var end = src.IndexOf(closing, j + q, StringComparison.Ordinal);
                    end = end < 0 ? src.Length : end + q;
                    sb.Append(src, i, j - i);   // 保留前綴
                    Blanks(j, end); i = end; continue;
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
                sb.Append(src, i, j - i);
                sb.Append('"'); Blanks(j + 1, stop - 1); if (stop - 1 > j) sb.Append('"');
                i = stop; continue;
            }
            if (c == '\'')
            {
                var k = i + 1;
                while (k < src.Length && src[k] != '\'' && src[k] != '\n') { if (src[k] == '\\') k++; k++; }
                var stop = Math.Min(k + 1, src.Length);
                // 只有確定是字元常值（長度 <= 6）才抹掉，避免誤吞
                if (stop - i <= 6 && stop - 1 < src.Length && src[stop - 1] == '\'')
                {
                    sb.Append('\''); Blanks(i + 1, stop - 1); sb.Append('\''); i = stop; continue;
                }
            }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }
}
