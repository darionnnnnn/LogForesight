using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 56 輪 F37：頁面與頁模組不寫 inline style 的守門，以及 KPI 欄格統一。
///
/// - cshtml 一律不得出現 <c>style="</c>（固定量值走 site.css 工具類）。
/// - JS 不得以 <c>.style.xxx =</c> 指派；只有「動態量值」可用 <c>style.setProperty('--lf-…')</c>
///   設 CSS 變數，且必須列在下方允許清單（檔名＋用途），由 class 消費。
/// - KPI 卡列已改由 <c>kpiStrip</c> 統一產生（守門見 FrontendConsistencyUiTests）。
/// </summary>
public class InlineStyleUiTests
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

    private static string WebDir(params string[] parts)
    {
        var segments = new List<string> { FindRepoRoot(), "LogForesight.Web" };
        segments.AddRange(parts);
        return Path.Combine(segments.ToArray());
    }

    /// <summary>
    /// 允許保留 <c>style.setProperty</c> 的位置：相對 wwwroot/js 的檔名 → 可設定的 CSS 變數（用途）。
    /// 新增項目前先確認它真的是依資料或量測計算的動態量值，否則改用 class。
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedSetProperty = new()
    {
        // 副標題字距補償與離屏量測節點的字型屬性：依量測結果計算
        ["core/brand-align.js"] = new[]
        {
            "--lf-brand-spacing", "--lf-brand-offset",
            "--lf-probe-font-family", "--lf-probe-font-size", "--lf-probe-font-weight", "--lf-probe-font-style"
        },
        // 圖例色塊顏色、甜甜圈中央文字依容器短邊縮放的字級
        ["core/charts.js"] = new[] { "--lf-swatch", "--lf-center-font-size" },
        // 載入骨架列的隨機寬度
        ["core/ui.js"] = new[] { "--lf-skeleton-width" },
        // 問題密度條寬度（issueDensityCell）
        ["core/format.js"] = new[] { "--lf-progress" },
        // 類別卡色點
        ["pages/dashboard.js"] = new[] { "--lf-swatch" },
        // 時間軸格子的風險色、圖例色塊
        ["pages/host-detail.js"] = new[] { "--lf-swatch" },
        // 狀態圖例色塊、批次進度條寬度、狀態計數文字色
        ["pages/runs.js"] = new[] { "--lf-swatch", "--lf-progress", "--lf-status-color" },
        // 設定精靈進度條寬度、步驟狀態色點
        ["pages/setup.js"] = new[] { "--lf-progress", "--lf-swatch" },
    };

    private static IEnumerable<string> JsFiles()
    {
        var root = WebDir("wwwroot", "js");
        Assert.True(Directory.Exists(root), $"找不到 {root}");
        return Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}"));
    }

    private static string Rel(string path) =>
        Path.GetRelativePath(WebDir("wwwroot", "js"), path).Replace('\\', '/');

    [Fact]
    public void Views_do_not_contain_inline_style()
    {
        var files = Directory.EnumerateFiles(WebDir("Views", "Pages"), "*.cshtml")
            .Concat(Directory.EnumerateFiles(WebDir("Views", "Shared"), "*.cshtml"))
            .ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .Where(p => File.ReadAllText(p).Contains("style=\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "以下檢視含 inline style，請改用 site.css 工具類: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Js_does_not_assign_style_properties()
    {
        var files = JsFiles().ToList();
        Assert.NotEmpty(files);

        var assign = new Regex(@"\.style\.[a-zA-Z]+\s*=(?!=)");
        var inlineAttr = new Regex(@"\bstyle=[""']");
        var offenders = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            if (assign.IsMatch(text)) offenders.Add(Rel(path) + "（.style.x =）");
            if (inlineAttr.IsMatch(text)) offenders.Add(Rel(path) + "（style=\"…\"）");
        }

        Assert.True(offenders.Count == 0, "以下 JS 仍有 inline style: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Js_setProperty_only_at_allowed_locations()
    {
        var anyCall = new Regex(@"\.style\.setProperty\(");
        var literalCall = new Regex(@"\.style\.setProperty\(\s*'(--[a-z0-9-]+)'");
        var seenFiles = new HashSet<string>();

        foreach (var path in JsFiles())
        {
            var text = File.ReadAllText(path);
            var total = anyCall.Matches(text).Count;
            if (total == 0) continue;

            var rel = Rel(path);
            seenFiles.Add(rel);
            Assert.True(AllowedSetProperty.TryGetValue(rel, out var allowedVars),
                $"{rel} 使用 style.setProperty 但不在允許清單");

            var literals = literalCall.Matches(text);
            Assert.True(literals.Count == total, $"{rel} 的 setProperty 屬性名必須是字面的 CSS 變數（--lf-…）");
            foreach (Match m in literals)
            {
                Assert.True(allowedVars!.Contains(m.Groups[1].Value), $"{rel} 設定了不在允許清單的變數 {m.Groups[1].Value}");
            }
        }

        // 允許清單不得殘留已不存在的項目，避免清單只增不減
        foreach (var key in AllowedSetProperty.Keys)
        {
            Assert.True(seenFiles.Contains(key), $"允許清單的 {key} 已無 setProperty 呼叫，請移除");
        }
    }
}
