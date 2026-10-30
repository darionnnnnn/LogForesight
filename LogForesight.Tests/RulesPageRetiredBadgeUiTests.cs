using Xunit;

namespace LogForesight.Tests;

public sealed class RulesPageRetiredBadgeUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 規則頁抑制清單對已退役關聯模式顯示徽章()
    {
        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "rules.js"));
        Assert.Contains("已退役", js);
        Assert.Contains("s.retired", js);
        Assert.Contains("statusBadge('已退役'", js);
    }
}
