using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ「匯入」分頁掃描列 UI 控制項驗證（task-D2）。
/// 驗證粒度下拉寬度、併發下拉選項、掃描 API 呼叫參數及提示文字。
/// </summary>
public class NetiqScanPickerUiTests
{
    private static string GetWizardJsPath()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "netiq-import-wizard.js");
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return path;
    }

    /// <summary>從測試組件輸出目錄往上找到含 LogForesight.sln 的專案根目錄</summary>
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
    public void Case1_應包含ScanConcurrencySelect控制項識別碼()
    {
        var content = File.ReadAllText(GetWizardJsPath());
        Assert.Contains("scan-concurrency-select", content);
    }

    [Fact]
    public void Case2_應包含三種併發查詢選項標籤()
    {
        var content = File.ReadAllText(GetWizardJsPath());
        Assert.Contains("單一查詢（預設）", content);
        Assert.Contains("同時 2 個查詢", content);
        Assert.Contains("同時 3 個查詢", content);
    }

    [Fact]
    public void Case3_Post到NetiqScan的呼叫同時含Granularity與Concurrency()
    {
        var lines = File.ReadAllLines(GetWizardJsPath());
        var scanLineIndex = Array.FindIndex(lines, l => l.Contains("api.post('/api/admin/netiq/scan'"));
        Assert.True(scanLineIndex >= 0, "找不到 api.post('/api/admin/netiq/scan') 呼叫");

        // 依驗收標準：取出 api.post('/api/admin/netiq/scan' 之後那一行
        var targetLine = lines[scanLineIndex + 1];
        Assert.Contains("granularity", targetLine);
        Assert.Contains("concurrency", targetLine);
    }

    [Fact]
    public void Case4_不再含MaxWidth200px()
    {
        var content = File.ReadAllText(GetWizardJsPath());
        Assert.DoesNotContain("maxWidth = '200px'", content);
        Assert.DoesNotContain("maxWidth = \"200px\"", content);
    }

    [Fact]
    public void Case5_提示文字同段含粒度與併發說明且呼叫次數等於三()
    {
        var content = File.ReadAllText(GetWizardJsPath());
        const string existingHint = "網段事件量大時選較細的粒度可避免安靜主機被吵雜主機擠掉，代價是掃描時間變長。";
        Assert.Contains(existingHint, content);

        var hints = Regex.Matches(content, @"pickerHint\((?:'|"")(?<hint>[\s\S]*?)(?:'|"")\)")
            .Select(m => m.Groups["hint"].Value)
            .ToList();

        var combinedHint = hints.FirstOrDefault(h => h.Contains(existingHint));
        Assert.NotNull(combinedHint);
        Assert.Contains("併發", combinedHint);

        // 斷言整個檔案裡 pickerHint( 的呼叫次數等於 3
        var callMatches = Regex.Matches(content, @"(?<!function\s)pickerHint\(");
        Assert.Equal(3, callMatches.Count);
    }
}
