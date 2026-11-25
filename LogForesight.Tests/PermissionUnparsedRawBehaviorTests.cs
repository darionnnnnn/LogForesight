using LogForesight.Core.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>權限異動檢核：無法解析的列可就地展開原文，可解析的列沒有展開鈕。</summary>
public class PermissionUnparsedRawBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("unparsedRowHasToggleThatShowsRawText")]
    [InlineData("unparsedRowFallsBackToAlertTextThenNote")]
    [InlineData("parsedRowHasNoToggle")]
    public void 無法解析列展開原文行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "permission-unparsed-raw.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    [Fact]
    public void 無法解析判定_兩側各一例()
    {
        var unparsed = new PermissionChangeRecord
        {
            HostName = "h", Category = PermissionCategory.Other, Target = "", AlertText = "x", EventId = 4670
        };
        Assert.True(PermissionChangeService.IsUnparsed(unparsed));
        unparsed.Target = "C:\\data";
        Assert.False(PermissionChangeService.IsUnparsed(unparsed));
        unparsed.Target = "Event 4670";   // 舊壞值等同沒有對象
        Assert.True(PermissionChangeService.IsUnparsed(unparsed));
        unparsed.InitiatorAccount = "u";
        Assert.False(PermissionChangeService.IsUnparsed(unparsed));
    }
}
