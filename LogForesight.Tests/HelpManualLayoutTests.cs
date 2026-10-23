using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書頁的排版（docs/HELP-WRITING-GUIDE.md、site.css .lf-doc）：
/// markdown-lite 的文件模式（語意 h3 段落標題、提示框、清單交給 .lf-doc 排版）與預設模式不受影響，
/// 內文開頭重複的章名拿掉，以及章節目錄每個圖示都要在 icons.svg 裡（缺圖示時目錄會出現空白）。
/// </summary>
public sealed class HelpManualLayoutTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("documentMode")]
    [InlineData("defaultModeUnchanged")]
    [InlineData("titleLineStripped")]
    public void 說明書文件排版的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--experimental-default-type=module");
        psi.ArgumentList.Add(Path.Combine(scriptDir, "markdown-doc.behavior.mjs"));
        psi.ArgumentList.Add(caseName);
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), $"node 執行逾時：{caseName}");
        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }

    [Fact]
    public void 章節目錄的每個圖示都存在於icons_svg()
    {
        var root = FindRepoRoot();
        var svg = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "img", "icons.svg"));
        var symbols = Regex.Matches(svg, "symbol id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "LogForesight.Web", "HelpContent", "manifest.json")));
        var missing = manifest.RootElement.GetProperty("chapters").EnumerateArray()
            .Select(c => (Id: c.GetProperty("id").GetString(), Icon: c.TryGetProperty("icon", out var i) ? i.GetString() : null))
            .Where(c => c.Icon is null || !symbols.Contains(c.Icon))
            .Select(c => $"{c.Id}→{c.Icon ?? "(未設定)"}")
            .ToList();

        Assert.True(missing.Count == 0, "章節圖示不存在於 icons.svg：" + string.Join("、", missing));
    }

    [Fact]
    public void 說明書頁以文件模式渲染章節_樣式使用既有token()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "help-manual.js"));
        Assert.Contains("renderAiText(container, withoutTitleLine(chapter.content), { asDocument: true })", js);

        var css = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "css", "site.css"));
        var start = css.IndexOf(".lf-doc {", StringComparison.Ordinal);
        Assert.True(start >= 0, "site.css 缺少 .lf-doc");
        var block = css[start..css.IndexOf("/* ── 卡片", start, StringComparison.Ordinal)];
        // 只用既有 token，不寫死色碼
        Assert.DoesNotMatch(new Regex("#[0-9a-fA-F]{3,6}\\b"), block);
        foreach (var selector in new[] { ".lf-doc__heading", ".lf-doc__note", ".lf-doc li + li" })
            Assert.Contains(selector, block);
    }
}
