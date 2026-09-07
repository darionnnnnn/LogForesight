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

    [Fact]
    public void RunsJs包含三路成果欄位與四種徽章文字()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        // 欄位名稱
        Assert.Contains("localDaysAnalyzed", jsContent);
        Assert.Contains("netiqDaysAnalyzed", jsContent);
        Assert.Contains("prtgOutcome", jsContent);

        // 四種徽章文字
        Assert.Contains("未啟用", jsContent);
        Assert.Contains("成功", jsContent);
        Assert.Contains("部分失敗", jsContent);
        Assert.Contains("失敗", jsContent);
    }

    [Fact]
    public void RunsJs包含AI排程未啟用提示與元素Id()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("ai-schedule-disabled-hint", jsContent);
        Assert.Contains("AI 分析排程未啟用", jsContent);

        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var cshtmlContent = File.ReadAllText(cshtmlPath);

        Assert.Contains("ai-schedule-disabled-hint", cshtmlContent);
    }

    [Fact]
    public void RunsJs包含pausedReason且RunsCshtml包含暫停徽章容器()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("pausedReason", jsContent);

        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var cshtmlContent = File.ReadAllText(cshtmlPath);

        Assert.Contains("schedule-paused-badge", cshtmlContent);
    }

    /// <summary>
    /// 異常彙總那一欄的資料來源是 BatchRun.HostName＝**跑批次的站台**，不是被分析的主機。
    /// 標成「影響主機」會讓管理者把站台名誤讀成出問題的主機——本輪回饋 2.5
    /// 「本機執行一直有錯誤」的誤判成因之一就是這個標題。這條釘住它不被改回去。
    /// </summary>
    [Fact]
    public void 異常彙總欄位標題為執行站台而非影響主機()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("title: '執行站台', sortKey: 'affectedHosts'", js);
        Assert.DoesNotContain("title: '影響主機'", js);
    }
}
