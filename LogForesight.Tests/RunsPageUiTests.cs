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
    /// <summary>
    /// 批次G1：phase 字面值集中在 RunPhases 之後，前端標籤表的完整性由這條測試守住。
    /// 過去 Core／Web／JS 三層各寫裸字串，新增一個 phase 只要漏改前端，
    /// 畫面就直接把裸 phase 字串印給使用者——沒有任何訊號會提醒你。
    /// </summary>
    [Fact]
    public void 每個進度phase在前端都有標籤與單位()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));

        var labelTable = ExtractObjectLiteral(js, "PROGRESS_PHASE_LABEL");
        var unitTable = ExtractObjectLiteral(js, "PROGRESS_PHASE_UNIT");

        foreach (var phase in LogForesight.Core.Service.RunPhases.ProgressTracks)
        {
            // 標籤沒有對應時前端會 fallback 成裸 phase 字串，直接印給使用者——一律要求。
            Assert.True(labelTable.Contains(phase),
                $"phase「{phase}」在 runs.js 的 PROGRESS_PHASE_LABEL 沒有對應文案，畫面會印出裸字串");

            // 單位的 fallback 是「主機日」，對本機／NetIQ 正確、對 PRTG 是錯的
            // （PRTG 的粒度是 sensor／device／筆），所以只對 PRTG 類要求。
            if (phase.StartsWith("prtg-", StringComparison.Ordinal))
            {
                Assert.True(unitTable.Contains(phase),
                    $"phase「{phase}」在 runs.js 的 PROGRESS_PHASE_UNIT 沒有對應單位，會 fallback 成錯誤的「主機日」");
            }
        }
    }

    /// <summary>批次G1：訊號類 phase 不是進度，不得混進進度軌清單。</summary>
    [Fact]
    public void 訊號類phase不列為進度軌()
    {
        var tracks = LogForesight.Core.Service.RunPhases.ProgressTracks;

        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.LocalDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.NetiqDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.PrtgDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.GuardPaused, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.GuardResumed, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.PrtgFindingsReady, tracks);

        // 但它們都要在 All 裡（All 是「全部字面值」的單一清單）
        foreach (var phase in tracks)
        {
            Assert.Contains(phase, LogForesight.Core.Service.RunPhases.All);
        }
    }

    /// <summary>取出 `const NAME = { ... };` 的物件字面值內容。</summary>
    private static string ExtractObjectLiteral(string js, string name)
    {
        var start = js.IndexOf($"const {name} = {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"runs.js 找不到 {name}");

        var open = js.IndexOf('{', start);
        var close = js.IndexOf("};", open, StringComparison.Ordinal);
        Assert.True(close > open, $"{name} 的物件字面值沒有正確結束");

        return js[open..close];
    }
}
