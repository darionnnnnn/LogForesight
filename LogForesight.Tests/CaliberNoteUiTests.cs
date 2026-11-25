using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// K2：我的交辦（處理人頁）與負載看板各有一句口徑說明，講清楚是否含「問題靜音中而暫停」的單。
/// 兩個數字的差等於靜音暫停的單，由 <c>WorkOrderQueryServiceTests.我的交辦與負載看板的差等於靜音暫停的單</c> 守。
/// </summary>
public class CaliberNoteUiTests
{
    private static string View(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Views", "Pages", name));
    }

    [Fact]
    public void 處理人頁說明不含靜音暫停的單()
    {
        var view = View("HandlerDetail.cshtml");
        Assert.Contains("id=\"handler-caliber-note\"", view);
        Assert.Contains("不含「問題靜音中而暫停」的單", view);
    }

    [Fact]
    public void 負載看板說明包含靜音暫停的單()
    {
        var view = View("WorkOrders.cshtml");
        Assert.Contains("id=\"wo-load-caliber-note\"", view);
        Assert.Contains("包含「問題靜音中而暫停」的單", view);
    }
}
