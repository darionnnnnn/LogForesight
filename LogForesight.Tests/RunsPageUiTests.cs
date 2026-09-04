using Xunit;

namespace LogForesight.Tests;

public class RunsPageUiTests
{
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

    [Fact]
    public void RunsJs包含三軌完成旗標與文案()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("localCompleted", jsContent);
        Assert.Contains("netiqCompleted", jsContent);
        Assert.Contains("prtgCompleted", jsContent);
        Assert.Contains("已取 ", jsContent);
        Assert.Contains("已完成 ", jsContent);
    }

    [Fact]
    public void RunsCshtml狀態文字預設包含載入中()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var lines = File.ReadAllLines(cshtmlPath);

        var statusLine = Array.Find(lines, l => l.Contains("schedule-status-text"));
        Assert.NotNull(statusLine);
        Assert.Contains("載入中", statusLine);
    }

    [Fact]
    public void UpdateProgressBar函式本體內不含特定Phase字串()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        const string startToken = "function updateProgressBar(";
        const string endToken = "function renderScheduleProgress(";
        var startIndex = jsContent.IndexOf(startToken, StringComparison.Ordinal);
        var endIndex = jsContent.IndexOf(endToken, StringComparison.Ordinal);

        Assert.True(startIndex >= 0, "找不到 function updateProgressBar(");
        Assert.True(endIndex > startIndex, "找不到 function renderScheduleProgress(");

        var updateProgressBarBody = jsContent.Substring(startIndex, endIndex - startIndex);
        Assert.DoesNotContain("prtg-triggered", updateProgressBarBody);
    }
}