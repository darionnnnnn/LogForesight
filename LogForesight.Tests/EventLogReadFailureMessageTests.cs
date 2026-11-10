using System.Security;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 讀取 Security 等記錄檔被拒（登錄存取被拒＝服務帳號沒有讀取權）時，警告要告訴管理者處理方式；
/// 其他例外維持原句。範本保留 {LogName} 佔位，由 NLog 帶入頻道名稱。
/// </summary>
public class EventLogReadFailureMessageTests
{
    private const string OriginalTemplate = "讀取 {LogName} Event Log 失敗";

    [Fact]
    public void SecurityException回傳權限說明範本()
    {
        var text = EventLogService.ReadFailureTemplate(new SecurityException("denied"));
        Assert.Contains("Event Log Readers", text);
        Assert.Contains("{LogName}", text);
    }

    [Fact]
    public void UnauthorizedAccessException回傳權限說明範本()
    {
        var text = EventLogService.ReadFailureTemplate(new UnauthorizedAccessException("denied"));
        Assert.Contains("Event Log Readers", text);
        Assert.Contains("{LogName}", text);
    }

    [Fact]
    public void 其他例外維持原句()
    {
        Assert.Equal(OriginalTemplate, EventLogService.ReadFailureTemplate(new IOException("io")));
    }

    [Fact]
    public void 守門_原句字面只出現一次()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "EventLogService.cs"));
        var count = CountOccurrences(text, OriginalTemplate);
        Assert.True(count == 1, $"「{OriginalTemplate}」字面應只出現一次（於 ReadFailureTemplate 內），實際 {count} 次");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
