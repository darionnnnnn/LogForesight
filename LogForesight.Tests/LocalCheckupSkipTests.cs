using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 體檢以本機主機為對象：本機未登記或已停用時必須略過（否則每趟重跑體檢 15～31 秒，最後因找不到當日紀錄而寫 Warn）。
/// 單元測試驗判斷函式；守門測試驗呼叫順序（略過判斷必須在 ShouldRun 之前）。
/// </summary>
public class LocalCheckupSkipTests
{
    [Fact]
    public void 本機未登記時略過體檢並說明未登記()
    {
        var reason = AnalysisOrchestrator.LocalCheckupSkipReason(0, false, localAnalysisRan: true, baselineRecordExists: true);
        Assert.NotNull(reason);
        Assert.Contains("未登記", reason);
    }

    [Fact]
    public void 本機已停用時略過體檢並說明已停用()
    {
        var reason = AnalysisOrchestrator.LocalCheckupSkipReason(5, true, localAnalysisRan: true, baselineRecordExists: true);
        Assert.NotNull(reason);
        Assert.Contains("已停用", reason);
    }

    [Fact]
    public void 本機登記且啟用時可以跑體檢()
    {
        Assert.Null(AnalysisOrchestrator.LocalCheckupSkipReason(5, false, localAnalysisRan: true, baselineRecordExists: true));
    }

    [Fact]
    public void 守門_體檢略過判斷必須位於ShouldRun之前()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "AnalysisOrchestrator.cs"));

        var skipIndex = text.IndexOf("LocalCheckupSkipReason(currentHostId", StringComparison.Ordinal);
        var shouldRunIndex = text.IndexOf("weeklyCheckupService.ShouldRun(", StringComparison.Ordinal);

        Assert.True(skipIndex >= 0, "AnalysisOrchestrator.cs 找不到 LocalCheckupSkipReason(currentHostId 的呼叫");
        Assert.True(shouldRunIndex >= 0, "AnalysisOrchestrator.cs 找不到 weeklyCheckupService.ShouldRun( 的呼叫");
        Assert.True(skipIndex < shouldRunIndex, "體檢略過判斷必須先於 weeklyCheckupService.ShouldRun 執行");
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
