using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// I2 任務測試：空狀態提供下一步與能力分流守門。
/// </summary>
public class EmptyStateGuidanceUiTests
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

    private static string PagesDir()
    {
        return Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages");
    }

    private static string ReadPage(string fileName)
    {
        var path = Path.Combine(PagesDir(), fileName);
        Assert.True(File.Exists(path), $"找不到頁面腳本: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 全站Pages空狀態無遺漏Hint()
    {
        var pagesPath = PagesDir();
        var jsFiles = Directory.GetFiles(pagesPath, "*.js");
        Assert.NotEmpty(jsFiles);

        var missingList = new List<string>();

        // 檢查 renderEmpty 與 renderTable 的 empty 設定
        var renderEmptyRegex = new Regex(@"renderEmpty\s*\([^,]+?,\s*\{(?<content>[^{}]*(?:\{[^{}]*\}[^{}]*)*)\}\s*\)", RegexOptions.Compiled);
        var emptyPropRegex = new Regex(@"empty\s*:\s*\{(?<content>[^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", RegexOptions.Compiled);

        foreach (var file in jsFiles)
        {
            var fileName = Path.GetFileName(file);
            var text = File.ReadAllText(file);

            void CheckMatch(Match match, string kind)
            {
                var content = match.Groups["content"].Value;
                if (!content.Contains("title")) return;

                // 檢查是否缺 hint 或 hint 為空字串
                var hasHint = Regex.IsMatch(content, @"hint\s*:\s*(?!['""]\s*['""])");
                // 若含有三元運算子，確保兩路皆非空字串
                var hasEmptyBranch = Regex.IsMatch(content, @"hint\s*:\s*[^,\n]+\?\s*['""]\s*['""]");

                if (!hasHint || hasEmptyBranch)
                {
                    missingList.Add($"{fileName} ({kind}): {content.Trim().Replace("\r\n", " ")}");
                }
            }

            foreach (Match m in renderEmptyRegex.Matches(text))
            {
                CheckMatch(m, "renderEmpty");
            }

            foreach (Match m in emptyPropRegex.Matches(text))
            {
                CheckMatch(m, "empty: { ... }");
            }
        }

        Assert.True(missingList.Count == 0, $"以下空狀態缺少具體 hint 或 hint 為空字串:\n{string.Join("\n", missingList)}");
    }

    [Fact]
    public void 儀表板依使用者能力分流空狀態提示()
    {
        var js = ReadPage("dashboard.js");

        // 1. 確認 load() 中將取得的使用者資訊傳入 renderGroupRisk
        Assert.Contains("renderGroupRisk(data, user);", js);

        // 2. renderGroupRisk 判斷使用者是否具備 Maintain 能力
        Assert.Contains("hasCapability(user, 'Maintain')", js);

        // 3. 具備 Maintain 時建議前往「群組與授權」
        Assert.Contains("可於「群組與授權」頁建立主機群組並指派主機", js);

        // 4. 無 Maintain 時建議聯絡系統管理員，不給前往不可進入之管理頁的連結/文字
        Assert.Contains("請聯絡系統管理員建立主機群組並指派主機", js);
    }

    [Fact]
    public void 最常用頁面提供具體下一步動線()
    {
        // 1. handler-detail.js
        var handlerJs = ReadPage("handler-detail.js");
        Assert.Contains("請調整狀態篩選或取消勾選「只看暫停」", handlerJs);
        Assert.Contains("前往總覽儀表板", handlerJs);
        Assert.Contains("請切換上方的處理狀態篩選條件", handlerJs);
        Assert.Contains("目前無任何指派至此處理人的風險日紀錄", handlerJs);

        // 2. work-orders.js
        var workOrdersJs = ReadPage("work-orders.js");
        Assert.Contains("請調整狀態、群組或勾選條件，或至「待派」頁籤查看可建立交辦的問題", workOrdersJs);
        Assert.Contains("目前區間內所有問題皆已指派交辦，或請調整上方的起訖日期", workOrdersJs);
        Assert.Contains("目前沒有啟用中的靜音規則；需要暫停特定問題通知時，可在問題詳情中設定靜音", workOrdersJs);
        Assert.Contains("目前系統中沒有設定任何可指派的處理人或尚無案件負載資料", workOrdersJs);

        // 3. runs.js
        var runsJs = ReadPage("runs.js");
        Assert.Contains("分析執行後會自動登記；請至上方「排程設定」啟用排程，或按「立即執行」手動觸發", runsJs);
        Assert.Contains("目前查無該日主機分析資料。需要回補時，可按「立即執行」並設定回望天數", runsJs);
        Assert.Contains("請調整上方的查詢期間，或至排程設定確認執行週期", runsJs);
    }

    [Fact]
    public void 各頁面空狀態提示詞不千篇一律()
    {
        var filesToCheck = new[]
        {
            "handler-detail.js",
            "work-orders.js",
            "runs.js",
            "records.js",
            "record-detail.js",
            "issue-owners.js",
            "settings.js"
        };

        var hintRegex = new Regex(@"hint\s*:\s*['""](?<hint>[^'""]+)['""]", RegexOptions.Compiled);
        var hints = new HashSet<string>();

        foreach (var file in filesToCheck)
        {
            var js = ReadPage(file);
            foreach (Match m in hintRegex.Matches(js))
            {
                var hint = m.Groups["hint"].Value.Trim();
                if (!string.IsNullOrEmpty(hint))
                {
                    hints.Add(hint);
                }
            }
        }

        // 應有多種相異且針對各情境之提示，絕非單一泛用字串
        Assert.True(hints.Count >= 10, $"空狀態提示樣式過少（僅 {hints.Count} 種），應針對各頁面控制項提供具體導引。");
    }
}
