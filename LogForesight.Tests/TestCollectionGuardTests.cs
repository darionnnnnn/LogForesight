using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 64 輪 A-4：<c>KnownIssueCatalog.Initialize</c>／<c>SecurityAuditWatchlist</c> 覆寫的是共用靜態狀態，
/// 呼叫它的測試類別必須放在序列化的 <c>KnownIssueCatalogState</c> 集合，否則會與並行執行、依賴預設規則表的測試互相污染，
/// 而且只在排程剛好撞上時才紅。靜態掃描一次把「加了呼叫、忘了掛集合」擋在提交前。
/// </summary>
public class TestCollectionGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 呼叫KnownIssueCatalog_Initialize的測試類別_必須掛KnownIssueCatalogState集合()
    {
        var testsDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests");
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep))
            .Where(f => Path.GetFileName(f) != nameof(TestCollectionGuardTests) + ".cs")
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(x => x.Text.Contains("KnownIssueCatalog.Initialize(") || x.Text.Contains("SecurityAuditWatchlist.Initialize("))
            .Where(x => !x.Text.Contains("[Collection(\"KnownIssueCatalogState\")]"))
            .Select(x => Path.GetRelativePath(testsDir, x.File))
            .ToList();
        Assert.True(offenders.Count == 0,
            "下列測試檔呼叫了 KnownIssueCatalog／SecurityAuditWatchlist 的 Initialize，卻沒有掛 [Collection(\"KnownIssueCatalogState\")]：\n  "
            + string.Join("\n  ", offenders));
    }
}
