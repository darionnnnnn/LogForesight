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
}
