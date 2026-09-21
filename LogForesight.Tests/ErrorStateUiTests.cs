using Xunit;

namespace LogForesight.Tests;

public class ErrorStateUiTests
{
    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Fact]
    public void 載入失敗與空狀態不同且有原位置重試()
    {
        var ui = Read("LogForesight.Web/wwwroot/js/core/ui.js");
        Assert.Contains("export function renderError(container", ui);
        Assert.Contains("el.className = 'lf-error'", ui);
        Assert.Contains("el.setAttribute('role', 'alert')", ui);
        Assert.Contains("button.addEventListener('click', () => onRetry())", ui);

        var records = Read("LogForesight.Web/wwwroot/js/pages/records.js");
        Assert.Contains("onRetry: () => renderIssueOccurrences(cell, group)", records);
        Assert.DoesNotContain("renderEmpty(wrap, { title: '載入受影響主機失敗'", records);

        var runs = Read("LogForesight.Web/wwwroot/js/pages/runs.js");
        Assert.Contains("onRetry: () => renderDayDetailInto(cell, date)", runs);
        Assert.Contains("onRetry: () => showDetail(runId, body)", runs);
        Assert.Contains("if (!retryBody) showDetailModal", runs);
        Assert.DoesNotContain("renderEmpty(body, { title: '載入執行詳情失敗'", runs);
    }

    [Fact]
    public void 無權限仍保留返回交辦和改派提示()
    {
        var detail = Read("LogForesight.Web/wwwroot/js/pages/work-order-detail.js");
        Assert.Contains("renderError(headerContainer, { message: error.message", detail);
        Assert.Contains("回到我的交辦", detail);
        Assert.Contains("可能已改派給其他人", detail);
    }
}
