using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 程式碼註解對規格文件的章節引用（例如「WEB-SPEC §8.1a」）守門：
/// 文件改版後章節號會變，註解卻不會跟著動，下一位讀者照著找就找不到。
/// 只認 <c>(WEB|PRTG|DB|DETECTION|RULES)-SPEC §id</c> 句型；同一行含 <c>docs/archive/</c> 的引用指的是歸檔文件，不在此檢查。
/// </summary>
public class DocCrossReferenceTests
{
    private static readonly Regex ReferencePattern = new(
        @"(?<doc>WEB|PRTG|DB|DETECTION|RULES)-SPEC(?:\.md)?\s*§\s*(?<id>\d+(?:\.\d+)*[a-z]?)",
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

    private static IEnumerable<string> SourceFiles(string root)
    {
        var extensions = new[] { ".cs", ".js", ".mjs", ".cshtml" };
        foreach (var project in new[] { "LogForesight.Web", "LogForesight.Core", "LogForesight.Tests" })
        {
            var projectDir = Path.Combine(root, project);
            if (!Directory.Exists(projectDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.Ordinal) ||
                    relative.StartsWith("obj/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal) ||
                    relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.StartsWith("wwwroot/lib/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static bool HeadingMatches(string line, string id)
    {
        var escaped = Regex.Escape(id);
        if (Regex.IsMatch(line, @"^#{2,4} " + escaped + @"(?:[\s.：、]|$)"))
        {
            return true;
        }

        return line.StartsWith('#') && Regex.IsMatch(line, "§" + escaped + @"(?![\d.])");
    }

    [Fact]
    public void 程式碼註解引用的規格章節在文件中都存在標題()
    {
        var root = FindRepoRoot();
        var headings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var doc in new[] { "WEB", "PRTG", "DB", "DETECTION", "RULES" })
        {
            var path = Path.Combine(root, "docs", doc + "-SPEC.md");
            Assert.True(File.Exists(path), $"找不到 {path}");
            headings[doc] = File.ReadAllLines(path)
                .Select(l => l.TrimStart('﻿'))
                .Where(l => l.StartsWith('#'))
                .ToList();
        }

        var checkedCount = 0;
        var broken = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("docs/archive/", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match m in ReferencePattern.Matches(lines[i]))
                {
                    checkedCount++;
                    var doc = m.Groups["doc"].Value;
                    var id = m.Groups["id"].Value;
                    if (!headings[doc].Any(h => HeadingMatches(h, id)))
                    {
                        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                        broken.Add($"{relative}:{i + 1} → {doc}-SPEC §{id}");
                    }
                }
            }
        }

        Assert.True(checkedCount > 0, "一條章節引用都沒掃到，守門本身失效");
        Assert.True(broken.Count == 0,
            "下列註解引用的文件章節不存在（文件改版後請改引用，不要加白名單）：\n" + string.Join("\n", broken));
    }
}
