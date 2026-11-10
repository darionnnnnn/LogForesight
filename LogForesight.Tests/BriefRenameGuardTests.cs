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
    public void briefBlock徽章與複製鈕使用重點速報()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js");
        Assert.Contains("badge.textContent = '重點速報';", js);
        Assert.Contains("copy.setAttribute('aria-label', '複製重點速報');", js);
    }

    [Fact]
    public void 脈絡區塊排在證據區塊之前()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "RecordDetail.cshtml");
        var contextAt = cshtml.IndexOf("id=\"record-context\"", StringComparison.Ordinal);
        var evidenceAt = cshtml.IndexOf("id=\"record-evidence\"", StringComparison.Ordinal);
        Assert.True(contextAt >= 0, "找不到 record-context");
        Assert.True(evidenceAt >= 0, "找不到 record-evidence");
        Assert.True(contextAt < evidenceAt, $"record-context({contextAt}) 應在 record-evidence({evidenceAt}) 之前");
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
