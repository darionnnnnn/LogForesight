using System.Reflection;
using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers;
using LogForesight.Web.Filters;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 F2～F4 的守門：說明章節的可見條件要與對應頁面的 <c>[Permission]</c> 相同，
/// 章節標題要以導覽名稱開頭，條件不得寫不存在的能力名。
/// 對應表只在這裡（測試專用），不進執行路徑。
/// </summary>
public sealed class HelpChapterPageGuardTests
{
    /// <summary>章節 id → (PagesController 的 action 名稱, layout.js 的導覽顯示文字；沒有導覽項目為 null)。</summary>
    private static readonly Dictionary<string, (string Action, string? NavLabel)> Map = new()
    {
        ["setup-wizard"] = (nameof(PagesController.Setup), null),
        ["dashboard"] = (nameof(PagesController.Dashboard), "總覽儀表板"),
        ["issues"] = (nameof(PagesController.Records), "問題查詢"),
        ["record-detail"] = (nameof(PagesController.RecordDetail), null),
        ["my-work"] = (nameof(PagesController.HandlerDetail), "我的交辦"),
        ["rules"] = (nameof(PagesController.Rules), "規則維護"),
        ["hosts"] = (nameof(PagesController.Hosts), "主機"),
        ["permissions"] = (nameof(PagesController.Groups), "群組與授權"),
        ["permission-changes"] = (nameof(PagesController.PermissionChanges), "權限異動檢核"),
        ["scheduler"] = (nameof(PagesController.Runs), "排程作業"),
        ["imports"] = (nameof(PagesController.Imports), "資料匯入"),
        ["settings"] = (nameof(PagesController.Settings), "設定"),
        ["reports"] = (nameof(PagesController.Reports), "報表"),
        ["issue-owners"] = (nameof(PagesController.IssueOwners), "問題負責與靜音"),
        ["netiq"] = (nameof(PagesController.Netiq), "NetIQ 維護"),
        ["prtg"] = (nameof(PagesController.Prtg), "PRTG 維護"),
        ["audit"] = (nameof(PagesController.Audit), "稽核紀錄")
    };

    /// <summary>沒有獨立頁面、跟著所屬頁面條件走的有條件章節（抑制在規則維護頁、資源守門在 PRTG 維護頁、
    /// 系統健康與問題負責在維護類頁面）；它們的條件另外要求與所屬頁面相同。</summary>
    private static readonly Dictionary<string, string> Embedded = new()
    {
        ["suppression"] = nameof(PagesController.Rules),
        ["resource-guard"] = nameof(PagesController.Prtg),
        ["system-health"] = nameof(PagesController.Settings)
    };

    private readonly HelpContentService _service = new();

    private static string[] PageCapabilities(string action)
    {
        var method = typeof(PagesController).GetMethods().First(m => m.Name == action);
        return method.GetCustomAttributes<PermissionAttribute>(true)
            .SelectMany(a => (Capability[])a.Arguments![0]).Select(c => c.ToString()).Distinct().OrderBy(n => n).ToArray();
    }

    private static string LayoutSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "js", "core", "layout.js"));
    }

    private void AssertSameCondition(string chapterId, string action)
    {
        var chapter = _service.Chapters.Single(c => c.Id == chapterId);
        var chapterCaps = (chapter.Requires ?? Array.Empty<string>()).OrderBy(n => n).ToArray();
        var pageCaps = PageCapabilities(action);
        Assert.True(pageCaps.SequenceEqual(chapterCaps),
            $"章節 {chapterId} 的條件 [{string.Join(",", chapterCaps)}] 與頁面 {action} 的 [Permission] [{string.Join(",", pageCaps)}] 不同");
    }

    [Fact]
    public void F3_對應表裡每個章節_條件與頁面Permission相同()
    {
        foreach (var (id, (action, _)) in Map) AssertSameCondition(id, action);
        foreach (var (id, action) in Embedded) AssertSameCondition(id, action);
    }

    [Fact]
    public void F3_對應表涵蓋所有有條件的章節()
    {
        foreach (var chapter in _service.Chapters.Where(c => c.Requires != null))
            Assert.True(Map.ContainsKey(chapter.Id) || Embedded.ContainsKey(chapter.Id),
                $"有條件的章節 {chapter.Id} 沒有列進頁面對應表");
    }

    [Fact]
    public void F4_對應到導覽項目的章節標題以導覽顯示文字開頭()
    {
        var layout = LayoutSource();
        foreach (var (id, (_, label)) in Map)
        {
            if (label == null) continue;
            Assert.Contains($"label: '{label}'", layout);   // 顯示文字以 layout.js 為準
            var chapter = _service.Chapters.Single(c => c.Id == id);
            Assert.True(chapter.Title.StartsWith(label, StringComparison.Ordinal),
                $"章節 {id} 的標題「{chapter.Title}」沒有以導覽名稱「{label}」開頭");
        }
    }

    [Fact]
    public void F2c_條件寫了不存在的能力名_載入就失敗()
    {
        using var bad = JsonDocument.Parse("\"Maintian\"");
        Assert.Throws<InvalidOperationException>(() => HelpContentService.ParseRequires("demo", bad.RootElement));
        using var badInArray = JsonDocument.Parse("[\"DevMonitor\",\"NoSuchCap\"]");
        Assert.Throws<InvalidOperationException>(() => HelpContentService.ParseRequires("demo", badInArray.RootElement));
        using var empty = JsonDocument.Parse("[]");
        Assert.Throws<InvalidOperationException>(() => HelpContentService.ParseRequires("demo", empty.RootElement));
    }

    [Fact]
    public void F2_字串與陣列兩種寫法都接受()
    {
        using var one = JsonDocument.Parse("\"Maintain\"");
        using var many = JsonDocument.Parse("[\"DevMonitor\",\"Maintain\"]");
        Assert.Equal(new[] { "Maintain" }, HelpContentService.ParseRequires("demo", one.RootElement));
        Assert.Equal(new[] { "DevMonitor", "Maintain" }, HelpContentService.ParseRequires("demo", many.RootElement));
        Assert.Null(HelpContentService.ParseRequires("demo", null));
    }
}
