using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 64 輪 C-4：「哪些狀態要重查」只能有一份。清單散在多處時新增一個狀態就會漏：
/// 帳本拒寫、補跑預覽回 409、排程永遠不補。只守判定（`is "failed" or ...`），寫入點用字面值不在此限。
/// </summary>
public class NetiqAttemptStatusGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 重查判定只在NetiqAttemptStatus()
    {
        var root = FindRepoRoot();
        var sep = Path.DirectorySeparatorChar;
        var pattern = new Regex("is\\s+\"(failed|running|partial|retained-incomplete)\"\\s+or\\s+\"", RegexOptions.CultureInvariant);
        var offenders = new[] { "LogForesight.Core", "LogForesight.Web" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep))
            .Where(f => Path.GetFileName(f) != "NetiqAttemptStatus.cs")
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(offenders.Count == 0, "這些檔案自己寫了 NetIQ 重查狀態清單，請改用 NetiqAttemptStatus：\n  " + string.Join("\n  ", offenders));
    }
}
