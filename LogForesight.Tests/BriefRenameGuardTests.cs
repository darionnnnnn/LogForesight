using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 68 輪 A-1／A-3 守門：風險日詳情的 AI 子卡改名「重點速報」（舊名「給主管的說明」只留在舊報告與說明書詞彙表），
/// 以及「脈絡」卡排在「證據」卡之前。
/// </summary>
public class BriefRenameGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

    [Fact]
    public void 前端與畫面檔零命中舊名給主管的說明()
    {
        var hits = new List<string>();
        foreach (var root in new[]
                 {
                     Path.Combine(RepoRoot(), "LogForesight.Web", "wwwroot"),
                     Path.Combine(RepoRoot(), "LogForesight.Web", "Views"),
                 })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(file).Contains("給主管的說明", StringComparison.Ordinal)) hits.Add(file);
            }
        }
        Assert.True(hits.Count == 0, "仍含舊名「給主管的說明」：" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void briefBlock眉標與複製鈕使用重點速報()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js");
        // 「重點速報」是與其他三段共用的眉標（h3.lf-text-label），不再是徽章
        Assert.Contains("label.textContent = '重點速報';", js);
        Assert.Contains("label.className = 'lf-text-label lf-briefing__label';", js);
        Assert.DoesNotContain("badge.textContent = '重點速報';", js);
        Assert.Contains("copy.setAttribute('aria-label', '複製重點速報');", js);
    }

    [Fact]
    public void 詢問AI在重點問題卡之後且沒有脈絡收合區()
    {
        // 第 73 輪 B：「脈絡」只剩詢問 AI 一張卡，拿掉這一層；詢問 AI 留在主欄、緊接重點問題，類型分布留在右欄
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "RecordDetail.cshtml");
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js");
        Assert.DoesNotContain("record-context", cshtml);
        Assert.DoesNotContain("record-context", js);
        var issuesAt = cshtml.IndexOf("id=\"detail-issues\"", StringComparison.Ordinal);
        var chatAt = cshtml.IndexOf("id=\"chat-card\"", StringComparison.Ordinal);
        var sideAt = cshtml.IndexOf("id=\"record-side\"", StringComparison.Ordinal);
        Assert.True(issuesAt >= 0 && chatAt > issuesAt && chatAt < sideAt, "詢問 AI 應在主欄、重點問題卡之後");
        Assert.True(cshtml.IndexOf("id=\"categories-card\"", StringComparison.Ordinal) > sideAt, "類型分布應在右欄");
        Assert.DoesNotContain("['chat-card',", js);
        Assert.DoesNotContain("['categories-card',", js);
        // 詢問 AI 預設收合（不帶 show）、開合由共用的 bindRememberedCollapse 記住（行為見 ui-collapse.behavior.mjs）
        Assert.Contains("<div class=\"collapse\" id=\"chat-collapse\">", cshtml);
        Assert.Contains("bindRememberedCollapse(document.getElementById('chat-collapse'), document.getElementById('chat-toggle'), 'record-detail-chat');", js);
    }

    [Fact]
    public void 說明書風險日詳情章關鍵字新舊名都搜得到()
    {
        var manifest = Read("LogForesight.Web", "HelpContent", "manifest.json");
        using var doc = JsonDocument.Parse(manifest);
        var chapter = doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Array)
            .SelectMany(p => p.Value.EnumerateArray())
            .Single(c => c.GetProperty("id").GetString() == "record-detail");
        var keywords = chapter.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Contains("重點速報", keywords);
        Assert.Contains("給主管的說明", keywords);
    }
}
