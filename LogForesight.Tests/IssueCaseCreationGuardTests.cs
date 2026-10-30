using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門：正式碼的「建案」只能經 <see cref="WorkOrderCoordinator"/>（CLAUDE.md 紅線）。
/// 直接 <c>new IssueCase</c> 等於繞過交辦單，會產生處理人清單、徽章、看板都數不到的散案。
/// 掃描 Core 與 Web 的原始碼（排除 bin／obj），白名單寫死；新增的建案入口必須有意識地加進白名單。
/// </summary>
public class IssueCaseCreationGuardTests
{
    /// <summary>允許出現 <c>new IssueCase</c> 的檔案（相對專案根目錄、正斜線）；資料層對映檔目前沒有直接 new 的寫法</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogForesight.Core/Persistence/WorkOrderCoordinator.cs"
    };

    private static readonly Regex NewIssueCase = new(@"\bnew\s+IssueCase\b", RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string root, string project) =>
        Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(Path.Combine(root, project), f).Replace(Path.DirectorySeparatorChar, '/');
                return !rel.StartsWith("bin/") && !rel.StartsWith("obj/");
            });

    [Fact]
    public void 正式碼只有WorkOrderCoordinator能直接建立IssueCase()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            foreach (var file in SourceFiles(root, project))
            {
                scanned++;
                var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (Allowed.Contains(rel)) continue;
                if (NewIssueCase.IsMatch(File.ReadAllText(file))) offenders.Add(rel);
            }
        }

        Assert.True(scanned > 100, $"掃描檔數異常偏少（{scanned}），守門可能已失效");
        Assert.True(offenders.Count == 0,
            "下列檔案直接 new IssueCase，繞過 WorkOrderCoordinator 會產生沒有交辦單的散案：" + string.Join("、", offenders));
    }

    [Fact]
    public void 白名單內的檔案確實仍有建案寫法_避免白名單過期()
    {
        var root = FindRepoRoot();
        foreach (var rel in Allowed)
            Assert.Matches(NewIssueCase, File.ReadAllText(Path.Combine(root, rel)));
    }
}
