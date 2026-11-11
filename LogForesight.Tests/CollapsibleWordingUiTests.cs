using Xunit;

namespace LogForesight.Tests;

/// <summary>收合／展開字樣統一：前端與說明書不得再出現舊字樣。</summary>
public sealed class CollapsibleWordingUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Theory]
    [InlineData("顯示全文（")]
    [InlineData("檢視來源觀測")]
    [InlineData("展開…")]   // 就地展開一律是 disclosure（標籤寫所指對象，如「全文」「佐證」）
    public void 前端與說明書不再出現舊的收合字樣(string retired)
    {
        var web = Path.Combine(FindRepoRoot(), "LogForesight.Web");
        var files = Directory.EnumerateFiles(Path.Combine(web, "wwwroot", "js"), "*.js", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "HelpContent"), "*", SearchOption.AllDirectories));
        var hits = files.Where(f => File.ReadAllText(f).Contains(retired)).ToList();
        Assert.True(hits.Count == 0, $"「{retired}」不應再出現：{string.Join(", ", hits)}");
    }
}
