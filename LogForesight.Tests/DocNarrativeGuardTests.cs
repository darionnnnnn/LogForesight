using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 現行文件敘事守門：<c>docs/</c> 主目錄、README、CLAUDE.md 只陳述現況，不寫開發輪次沿革
/// （CLAUDE.md「文件紀律」）。只鎖「開發輪次標記」，避免誤傷「本輪寄送」「第一輪退回」這類領域用語。
/// 掃描對象：<c>docs/*.md</c>（排除進行中的 <c>FEEDBACK-*-PLAN.md</c>）、README.md、CLAUDE.md。
/// 允許清單（每一條都有理由）：
/// 1. 同一行含 <c>docs/archive/</c> 的行——那是指向歷程的一行背景說明。
/// 2. CLAUDE.md「文件紀律」條目——規則本文正在列舉禁詞。
/// 3. README.md「升級注意事項」章——升級注意以版本差異為本質。
/// </summary>
public class DocNarrativeGuardTests
{
    private static readonly Regex Forbidden = new(
        @"第\s*[0-9一二三四五六七八九十百]+\s*輪|回饋[一二三四五六七八九十百0-9]+輪|體檢輪|審查輪",
        RegexOptions.Compiled);

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

    private static IEnumerable<string> GuardedFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("FEEDBACK-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("-PLAN.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }

        yield return Path.Combine(root, "README.md");
        yield return Path.Combine(root, "CLAUDE.md");
    }

    /// <summary>回傳該檔案中不在允許清單內、卻含禁止樣式的行（行號從 1 起算）。</summary>
    private static List<(int Line, string Text)> FindViolations(string fileName, string[] lines)
    {
        var result = new List<(int, string)>();
        var inDisciplineItem = false;
        var inUpgradeSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (fileName == "CLAUDE.md")
            {
                if (line.StartsWith("- **文件紀律", StringComparison.Ordinal))
                {
                    inDisciplineItem = true;
                }
                else if (inDisciplineItem && line.StartsWith("- **", StringComparison.Ordinal))
                {
                    inDisciplineItem = false;
                }
            }

            if (fileName == "README.md")
            {
                if (line.StartsWith("## 升級注意事項", StringComparison.Ordinal))
                {
                    inUpgradeSection = true;
                }
                else if (inUpgradeSection && line.StartsWith("## ", StringComparison.Ordinal))
                {
                    inUpgradeSection = false;
                }
            }

            if (!Forbidden.IsMatch(line))
            {
                continue;
            }

            if (line.Contains("docs/archive/", StringComparison.Ordinal) || inDisciplineItem || inUpgradeSection)
            {
                continue;
            }

            result.Add((i + 1, line.Trim()));
        }

        return result;
    }

    [Fact]
    public void CurrentDocs_DoNotCarryDevelopmentRoundNarrative()
    {
        var root = FindRepoRoot();
        var violations = new List<string>();

        foreach (var file in GuardedFiles(root))
        {
            Assert.True(File.Exists(file), $"守門對象不存在：{file}");
            var name = Path.GetFileName(file);
            foreach (var (lineNo, text) in FindViolations(name, File.ReadAllLines(file)))
            {
                var shown = text.Length > 120 ? text[..120] + "…" : text;
                violations.Add($"{name}:{lineNo}：{shown}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "現行文件不得寫開發輪次沿革（改寫成現況陳述，或改成一行指向 docs/archive/ 的連結）：\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void AllowListSections_ExistSoTheyCannotSilentlyBecomeBlanketExemptions()
    {
        // 允許清單靠章節標記界定範圍；標記改名後範圍會落空，守門等於放行整份檔案，所以先確認標記還在。
        var root = FindRepoRoot();
        Assert.Contains(
            File.ReadAllLines(Path.Combine(root, "CLAUDE.md")),
            l => l.StartsWith("- **文件紀律", StringComparison.Ordinal));
        Assert.Contains(
            File.ReadAllLines(Path.Combine(root, "README.md")),
            l => l.StartsWith("## 升級注意事項", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_FlagsRoundMarkersOutsideTheAllowList()
    {
        // 守門自身的判定：一般文件行含輪次標記會被抓到；同行有 archive 連結、以及子字串「首輪」不會。
        Assert.Single(FindViolations("RULES-SPEC.md", new[] { "第 70 輪新增" }));
        Assert.Single(FindViolations("RULES-SPEC.md", new[] { "回饋二十七輪補上" }));
        Assert.Single(FindViolations("RULES-SPEC.md", new[] { "體檢輪抓到" }));
        Assert.Empty(FindViolations("RULES-SPEC.md", new[] { "第 70 輪見 docs/archive/README.md" }));
        Assert.Empty(FindViolations("RULES-SPEC.md", new[] { "首輪退回不排除照掃" }));
    }
}
