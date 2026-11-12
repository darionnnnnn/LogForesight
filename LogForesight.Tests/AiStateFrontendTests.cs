using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪 B-b-6：AI 摘要狀態（降級待補／已放棄）的前端守門。
/// 判定改看欄位（<c>aiStateOf</c>），不再用標題文字比對；這裡的靜態斷言防止字串比對回來，
/// 也比對前端上限常數與後端 <see cref="AiDegradation.MaxAttempts"/> 同值。
/// </summary>
public class AiStateUiTests
{
    private static string JsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "js");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { JsRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

    [Theory]
    [InlineData("core", "format.js")]
    [InlineData("pages", "records.js")]
    [InlineData("pages", "record-detail.js")]
    [InlineData("pages", "dashboard.js")]
    public void 不再以標題文字比對AI待補(string folder, string file)
    {
        var source = Read(folder, file);
        Assert.DoesNotContain("includes('AI 待補')", source);
        Assert.DoesNotContain("includes(\"AI 待補\")", source);
        Assert.DoesNotContain("isAiRetryPending", source);
    }

    [Theory]
    [InlineData("dashboard.js")]
    [InlineData("records.js")]
    public void 主機彙總的最新狀況都走latestStatusCell(string file)
    {
        // 欄位選項（例如 truncate）可以寫在 title 與 render 之間
        Assert.Matches(new Regex(@"title: '最新狀況',[^}]*?render: h => latestStatusCell\(h, "), Read("pages", file));
    }

    [Fact]
    public void 清單與詳情共用format的aiStateOf()
    {
        var format = Read("core", "format.js");
        Assert.Contains("export function aiStateOf(", format);
        Assert.Contains("export function aiDegradedBadge(", format);

        var records = Read("pages", "records.js");
        Assert.Matches(new Regex(@"import \{[^}]*\baiStateOf\b[^}]*\} from '\.\./core/format\.js'"), records);
        Assert.Matches(new Regex(@"import \{[^}]*\baiDegradedBadge\b[^}]*\} from '\.\./core/format\.js'"), records);

        var detail = Read("pages", "record-detail.js");
        Assert.Matches(new Regex(@"import \{[^}]*\baiStateOf\b[^}]*\} from '\.\./core/format\.js'"), detail);
        Assert.Matches(new Regex(@"import \{[^}]*\baiDegradedBadge\b[^}]*\} from '\.\./core/format\.js'"), detail);
    }

    [Fact]
    public void 徽章不用inline樣式與含插值的innerHTML()
    {
        var format = Read("core", "format.js");
        var start = format.IndexOf("export function aiDegradedBadge(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = format.IndexOf("\n}\n", start, StringComparison.Ordinal);
        var body = format.Substring(start, end - start);
        Assert.DoesNotContain("innerHTML", body);
        Assert.DoesNotContain("style=", body);
        Assert.Contains("textContent", body);
    }

    [Fact]
    public void 前端放棄上限與後端同值()
    {
        var match = Regex.Match(Read("core", "format.js"), @"export const AI_DEGRADED_MAX_ATTEMPTS = (\d+);");
        Assert.True(match.Success, "format.js 找不到 AI_DEGRADED_MAX_ATTEMPTS");
        Assert.Equal(AiDegradation.MaxAttempts, int.Parse(match.Groups[1].Value));
    }
}

/// <summary>AI 摘要狀態行為：載入 production 的 format.js／records.js／record-detail.js 函式，以替身 DOM 驅動。</summary>
public class AiStateBehaviorTests
{
    [NodeTheory]
    [InlineData("aiStateOf四態")]
    [InlineData("aiStateOf完全失敗標題只看欄位不看文字")]
    [InlineData("aiStateOf詳情已清除的列不再說會再試")]
    [InlineData("aiStateOf歷史舊標題與缺欄位維持完成")]
    [InlineData("上限常數是3")]
    [InlineData("降級徽章文字與用色")]
    [InlineData("清單徽章文字對照")]
    [InlineData("清單降級時標題文字照常顯示")]
    [InlineData("最新狀況一格只有降級或放棄才加徽章")]
    [InlineData("詳情徽章文字對照")]
    [InlineData("詳情案件授與檢視不顯示AI狀態")]
    [InlineData("摘要區塊重點速報位置與複製鈕")]
    [InlineData("摘要區塊沒有brief或未分析時不建節點")]
    [InlineData("摘要區塊複製鈕成功與失敗提示")]
    public void AI狀態行為案例(string caseName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var scriptDir = Path.Combine(dir!.FullName, "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "ai-state.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(15), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
