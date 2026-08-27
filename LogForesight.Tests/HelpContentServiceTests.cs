using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書內容載入與精靈導引卡整合（回饋十八輪批次H，終檢輪補上 H-5 規劃的這組測試）：
/// manifest 與各章節都是內嵌資源，直接驗證真實內容——這裡釘住的是「manifest 擴充 type/href
/// 欄位後既有 14 章 DTO 向後相容」與「Hidden 過濾」兩條批次H 的核心語意。
/// </summary>
public class HelpContentServiceTests
{
    private readonly HelpContentService _service = new();

    [Fact]
    public void 精靈導引卡在第一項且type與href正確()
    {
        var manual = _service.GetManual(hideSetupWizard: false);

        var first = manual.Chapters[0];
        Assert.Equal("setup-wizard", first.Id);
        Assert.Equal("link", first.Type);
        Assert.Equal("/setup", first.Href);
        Assert.Equal("", first.Content);   // link 章節沒有 Markdown 內容
    }

    [Fact]
    public void Hidden時精靈導引卡被濾掉_其餘章節不受影響()
    {
        var visible = _service.GetManual(hideSetupWizard: false);
        var hidden = _service.GetManual(hideSetupWizard: true);

        Assert.Equal(visible.Chapters.Count - 1, hidden.Chapters.Count);
        Assert.DoesNotContain(hidden.Chapters, c => c.Id == "setup-wizard");
        // 只濾精靈那一項，其餘章節（id 集合）完全一致
        Assert.Equal(
            visible.Chapters.Where(c => c.Id != "setup-wizard").Select(c => c.Id),
            hidden.Chapters.Select(c => c.Id));
    }

    /// <summary>既有 Markdown 章節向後相容：manifest 沒填 type/href 的章節預設 markdown、
    /// Href 為 null、內容非空——加欄位不能改變既有 14 章的行為。</summary>
    [Fact]
    public void 既有Markdown章節_Type預設markdown且內容非空()
    {
        var markdownChapters = _service.GetManual().Chapters.Where(c => c.Id != "setup-wizard").ToList();

        Assert.NotEmpty(markdownChapters);
        Assert.All(markdownChapters, c =>
        {
            Assert.Equal("markdown", c.Type);
            Assert.Null(c.Href);
            Assert.False(string.IsNullOrWhiteSpace(c.Content));
        });
    }

    // ── 回饋二十七輪作業 G：使用者版與 AI 版雙內容 ──────────────────────────
    //
    // 使用者手冊要好讀、AI 問答要夠細，兩者對「同一章該寫多長」的要求相反。
    // 章節可另附 AI 版（manifest 的 aiFile），沒有的章節 fallback 回使用者版。

    /// <summary>有 AI 版時：餵給 AI 的是詳細版，使用者版原封不動。</summary>
    [Fact]
    public void 章節有AI版時_ContentForAi取詳細版且Content不受影響()
    {
        var chapter = new HelpChapter(
            "demo", "示範", "使用者看的簡明內容", new List<string>(), new List<string>(), "info-circle",
            AiContent: "AI 看的詳細內容");

        Assert.Equal("AI 看的詳細內容", chapter.ContentForAi);
        Assert.Equal("使用者看的簡明內容", chapter.Content);
    }

    /// <summary>沒有 AI 版時 fallback 回使用者版——fallback 寫在型別上而不是載入端，
    /// 任何直接建構章節的地方（測試、未來其他來源）都不會拿到空內容。</summary>
    [Fact]
    public void 章節沒有AI版時_ContentForAi退回使用者版()
    {
        var chapter = new HelpChapter(
            "demo", "示範", "只有一份內容", new List<string>(), new List<string>(), "info-circle");

        Assert.Equal("只有一份內容", chapter.ContentForAi);
    }

    /// <summary>GetManual 是使用者手冊的出口，不得夾帶 AI 版內容；
    /// DTO 本身也不該有這個欄位（AI 版不外洩到前端）。</summary>
    [Fact]
    public void GetManual回傳的章節只帶使用者版內容()
    {
        var manual = _service.GetManual();

        foreach (var dto in manual.Chapters.Where(c => c.Id != "setup-wizard"))
        {
            var source = _service.Chapters.Single(c => c.Id == dto.Id);
            Assert.Equal(source.Content, dto.Content);
        }

        Assert.DoesNotContain(
            typeof(LogForesight.Web.Models.Dto.HelpChapterDto).GetProperties(),
            p => p.Name.Contains("Ai", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// manifest 宣告了 aiFile 的章節，實際一定要讀得到那個檔。
    /// 沒有這條的話，檔名打錯／檔案沒被編進組件都只會靜默 fallback 成使用者版——
    /// 畫面一切正常，只有 AI 答得比預期差，而且沒有任何人會發現。
    /// </summary>
    [Fact]
    public void 宣告了aiFile的章節都要真的讀到AI版內容()
    {
        var assembly = typeof(HelpContentService).Assembly;
        var manifestName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("HelpContent.manifest.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(manifestName)!;
        using var reader = new StreamReader(stream);
        using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());

        var idsWithAiFile = doc.RootElement.GetProperty("chapters").EnumerateArray()
            .Where(c => c.TryGetProperty("aiFile", out _))
            .Select(c => c.GetProperty("id").GetString()!)
            .ToList();

        Assert.NotEmpty(idsWithAiFile);   // 一章都沒有的話這條測試等於沒測到東西

        foreach (var id in idsWithAiFile)
        {
            var chapter = _service.Chapters.Single(c => c.Id == id);
            Assert.False(string.IsNullOrWhiteSpace(chapter.AiContent),
                $"章節 {id} 的 manifest 有 aiFile，卻沒讀到 AI 版內容（檔名打錯或沒被編進組件）");
            Assert.NotEqual(chapter.Content, chapter.AiContent);
        }
    }
}
