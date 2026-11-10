using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 結束代碼非 0 的趟次必須至少有一行 Error 說明原因（排程作業頁「異常彙總」預設只看 Error）。
/// 憑證解不開時前置檢查只寫 Warn（刻意），因此靠收尾補寫這一行。
/// </summary>
public class UnsuccessfulRunErrorTests
{
    [Fact]
    public void 非成功且無Error時回傳含原因的說明()
    {
        var text = AnalysisOrchestrator.UnsuccessfulRunError(false, 0, "NetIQ 分析有 12 個主機日失敗");
        Assert.NotNull(text);
        Assert.Contains("本趟未成功完成", text);
        Assert.Contains("NetIQ 分析有 12 個主機日失敗", text);
    }

    [Fact]
    public void 非成功但已有Error時不補寫()
    {
        Assert.Null(AnalysisOrchestrator.UnsuccessfulRunError(false, 2, "任何原因"));
    }

    [Fact]
    public void 成功時不補寫()
    {
        Assert.Null(AnalysisOrchestrator.UnsuccessfulRunError(true, 0, "任何原因"));
    }

    [Fact]
    public void 非成功且無原因時說明原因未記錄()
    {
        var text = AnalysisOrchestrator.UnsuccessfulRunError(false, 0, null);
        Assert.NotNull(text);
        Assert.Contains("原因未記錄", text);
    }

    [Fact]
    public void 守門_補寫判斷位於Finish之前()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "AnalysisOrchestrator.cs"));

        var callIndex = text.IndexOf("UnsuccessfulRunError(result", StringComparison.Ordinal);
        var finishIndex = text.IndexOf("runRecorder.Finish(exitCode", StringComparison.Ordinal);

        Assert.True(callIndex >= 0, "AnalysisOrchestrator.cs 找不到 UnsuccessfulRunError(result 的呼叫");
        Assert.True(finishIndex >= 0, "AnalysisOrchestrator.cs 找不到 runRecorder.Finish(exitCode 的呼叫");
        Assert.True(callIndex < finishIndex, "UnsuccessfulRunError 的呼叫必須先於 runRecorder.Finish(exitCode");
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
