using System.Xml.Linq;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門：Web 的 nlog.config 不得開 autoReload。執行紀錄擷取的常駐 target／規則只在啟動與 BatchRunRecorder 建構時安裝
/// （RunLogCapture.EnsureInstalled），執行中整份更換 NLog 設定會讓該趟的警告計數靜默歸零。
/// </summary>
public class NLogConfigAutoReloadGuardTests
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
    public void NLog設定根元素不得開autoReload()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "nlog.config");
        Assert.True(File.Exists(path), $"找不到 {path}");

        var root = XDocument.Load(path).Root;
        Assert.NotNull(root);
        Assert.Equal("nlog", root!.Name.LocalName, ignoreCase: true);

        var attr = root.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "autoReload", StringComparison.OrdinalIgnoreCase));
        Assert.True(attr == null || string.Equals(attr.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase),
            "nlog.config 不得開 autoReload：執行紀錄擷取的常駐規則只在啟動與建構時安裝，執行中更換設定會讓該趟警告計數靜默歸零");
    }
}
