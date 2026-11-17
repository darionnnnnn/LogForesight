using System.Text.RegularExpressions;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 C-4（C13-b）：健康頁有警示樣式的卡片都在整體狀態的併入清單內。
/// 健康頁每張卡片以 <c>data-health-alert</c> 宣告它的警示對應哪些原因種類（<see cref="HealthAttentionKeys"/>；none＝不出現警示樣式）；
/// 渲染函式把警示樣式寫進某張卡片的容器時，那張卡片必須宣告非 none 的種類。
/// 新增一張會亮警示的卡片卻沒併進清單 → 這裡轉紅（反例見 <see cref="反例_暫存一張不在清單的警示卡_會被抓到"/>）。
/// </summary>
public class HealthAttentionGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string HealthPanel(string cshtml)
    {
        var start = cshtml.IndexOf("data-panel=\"health\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到健康頁籤");
        var next = cshtml.IndexOf("data-panel=\"", start + 1, StringComparison.Ordinal);
        return next < 0 ? cshtml[start..] : cshtml[start..next];
    }

    /// <summary>警示樣式的寫法：徽章警示色、警示型 statusNote／statusBadge、風險紅字。</summary>
    private static readonly Regex WarningStyle = new(
        @"text-bg-warning|text-bg-danger|statusBadge\([^)]*'(?:warning|danger)'\)|statusNote\('(?:warning|error)'|lf-text-alert|lf-status-note--(?:warning|error)",
        RegexOptions.Compiled);

    private static string Title(string block) => Regex.Match(block, @"lf-card__title"">([^<]*)").Groups[1].Value;

    /// <summary>回傳違規描述；空＝通過。</summary>
    internal static List<string> Violations(string cshtml, string js)
    {
        var violations = new List<string>();
        var panel = HealthPanel(cshtml);

        // 卡片：lf-card 區塊（到下一張 lf-card 或面板結尾）；每張都必須宣告 data-health-alert
        var cardStarts = Regex.Matches(panel, @"<div class=""lf-card(?:\s[^""]*)?""[^>]*>").Cast<Match>().ToList();
        Assert.NotEmpty(cardStarts);   // 空集合會讓下面的檢查恆真
        var cards = new List<(string Declared, string Block)>();
        for (var i = 0; i < cardStarts.Count; i++)
        {
            var end = i + 1 < cardStarts.Count ? cardStarts[i + 1].Index : panel.Length;
            var block = panel[cardStarts[i].Index..end];
            var declared = Regex.Match(cardStarts[i].Value, @"data-health-alert=""([^""]*)""");
            if (!declared.Success)
            {
                violations.Add($"健康頁卡片沒有宣告 data-health-alert：{Title(block)}");
                cards.Add(("none", block));
                continue;
            }
            foreach (var key in declared.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (key != "none" && !HealthAttentionKeys.All.Contains(key))
                    violations.Add($"卡片宣告的原因種類不在併入清單內：{key}");
            }
            cards.Add((declared.Groups[1].Value, block));

            // 靜態標記裡直接寫警示樣式的卡片，不能宣告 none
            if (declared.Groups[1].Value.Trim() == "none" && WarningStyle.IsMatch(block))
                violations.Add($"宣告 none 的卡片含警示樣式：{Title(block)}");
        }

        // 渲染函式：寫警示樣式又指向某個卡片內的容器 id → 那張卡片必須宣告非 none
        foreach (var fn in Regex.Split(js, @"(?m)^(?=(?:export\s+)?(?:async\s+)?function\s)"))
        {
            if (!WarningStyle.IsMatch(fn)) continue;
            foreach (Match id in Regex.Matches(fn, @"getElementById\('([^']+)'\)|resolveSlowQueryHost"))
            {
                var containerId = id.Value.StartsWith("resolveSlowQueryHost") ? "slow-query-status" : id.Groups[1].Value;
                var owner = cards.FirstOrDefault(c => c.Block.Contains($"id=\"{containerId}\"", StringComparison.Ordinal));
                if (owner.Block == null) continue;   // 不在健康頁的容器
                if (owner.Declared.Trim() == "none")
                    violations.Add($"警示樣式寫進宣告為 none 的健康頁卡片（容器 {containerId}）：{Regex.Match(fn, @"function\s+(\w+)").Groups[1].Value}");
            }
        }
        return violations;
    }

    [Fact]
    public void 健康頁有警示樣式的卡片都在併入清單內()
    {
        var violations = Violations(
            Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml"),
            Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js"));

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void 併入清單的每個原因種類至少有一張健康頁卡片承接()
    {
        var panel = HealthPanel(Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml"));
        var declared = Regex.Matches(panel, @"data-health-alert=""([^""]*)""").Cast<Match>()
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet();

        // 儲存體連不上時整頁都無從載入，沒有對應卡片；其餘種類都要有卡片呈現
        foreach (var key in HealthAttentionKeys.All.Where(k => k != HealthAttentionKeys.Storage))
            Assert.True(declared.Contains(key), $"原因種類沒有任何健康頁卡片承接：{key}");
    }

    [Fact]
    public void 反例_暫存一張不在清單的警示卡_會被抓到()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        Assert.Empty(Violations(cshtml, js));   // 基準：現況通過

        // 反例一：新增一張卡片沒宣告
        var undeclared = cshtml.Replace("<div class=\"mb-3\" id=\"health-overall\"",
            "<div class=\"lf-card mb-3\"><div class=\"lf-card__header\"><h2 class=\"lf-card__title\">暫存卡</h2></div></div>\n<div class=\"mb-3\" id=\"health-overall\"");
        Assert.NotEqual(cshtml, undeclared);
        Assert.NotEmpty(Violations(undeclared, js));

        // 反例二：宣告了不在清單內的種類
        var unknownKey = cshtml.Replace("data-health-alert=\"memory\"", "data-health-alert=\"notInList\"");
        Assert.NotEqual(cshtml, unknownKey);
        Assert.Contains(Violations(unknownKey, js), v => v.Contains("notInList"));

        // 反例三：登入暫停卡（宣告 none）的渲染函式寫進警示樣式
        var warned = js + "\nfunction renderTempThrottle() { document.getElementById('health-login-throttle').append(statusNote('warning', 'x')); }\n";
        Assert.Contains(Violations(cshtml, warned), v => v.Contains("health-login-throttle"));
    }
}
