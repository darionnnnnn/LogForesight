using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書 AI 問答的選節計分（docs/archive/FEEDBACK-15-PLAN.md 批次E-3）：純靜態、
/// 用合成的 HelpChapter 清單驗證，不依賴真實內嵌資源或 AI。
/// </summary>
public class HelpChapterScorerTests
{
    private static HelpChapter Chapter(string id, string title, string content,
        List<string>? keywords = null, List<string>? related = null) =>
        new(id, title, content, keywords ?? new List<string>(), related ?? new List<string>(), Icon: "");

    [Fact]
    public void 標題命中的章節排在只有內文命中的章節之前()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("rules", "規則維護", "這裡完全沒提到抑制兩個字"),
            Chapter("other", "其他章節", "順便提一下告警抑制這個功能")
        };

        var result = HelpChapterScorer.SelectChapters("告警抑制怎麼設定？", chapters, maxTokenBudget: 10000);

        Assert.NotEmpty(result);
        Assert.Equal("other", result[0].Id);
    }

    [Fact]
    public void keywords命中會拉高分數但不如標題命中()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("a", "無關標題", "無關內文", keywords: new List<string> { "郵件通知" }),
            Chapter("b", "郵件通知", "無關內文")
        };

        var result = HelpChapterScorer.SelectChapters("郵件通知設定在哪？", chapters, maxTokenBudget: 10000);

        Assert.Equal("b", result[0].Id);
    }

    [Fact]
    public void 選入最高分章節與其related章節()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("rules", "規則維護", "規則維護的比對順序說明", related: new List<string> { "suppression" }),
            Chapter("suppression", "告警抑制", "告警抑制的四種目標說明"),
            Chapter("unrelated", "不相關章節", "完全無關的內容")
        };

        var result = HelpChapterScorer.SelectChapters("規則維護要怎麼調整比對順序？", chapters, maxTokenBudget: 10000);

        Assert.Equal(new[] { "rules", "suppression" }, result.Select(c => c.Id));
    }

    [Fact]
    public void 完全比對不到任何關鍵字時回傳空清單()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("a", "規則維護", "比對順序與遮蔽警告")
        };

        var result = HelpChapterScorer.SelectChapters("今天天氣如何？高鐵誤點了嗎？", chapters, maxTokenBudget: 10000);

        Assert.Empty(result);
    }

    [Fact]
    public void 預算超出時停止加入更多章節但保留最高分章節()
    {
        var bigContent = new string('風', 5000);   // EstimateTokens 對 CJK 約 1:1，5000 字 ≈ 5000 token
        var chapters = new List<HelpChapter>
        {
            Chapter("main", "郵件通知", bigContent, related: new List<string> { "related1", "related2" }),
            Chapter("related1", "系統設定", "系統設定相關內容"),   // 8 個 CJK 字 ≈ 8 token
            Chapter("related2", "排程作業", "排程作業相關內容")
        };

        // 預算只比 main 的用量多 4 token，不夠再放下 related1（8 token），應在 main 之後停止加節
        var result = HelpChapterScorer.SelectChapters("郵件通知怎麼設定？", chapters, maxTokenBudget: 5004);

        Assert.Single(result);
        Assert.Equal("main", result[0].Id);
    }

    [Fact]
    public void 最高分章節本身超過預算仍會被保留()
    {
        var hugeContent = new string('郵', 20000);
        var chapters = new List<HelpChapter>
        {
            Chapter("main", "郵件通知", hugeContent)
        };

        var result = HelpChapterScorer.SelectChapters("郵件通知怎麼設定？", chapters, maxTokenBudget: 100);

        Assert.Single(result);
        Assert.Equal("main", result[0].Id);
    }

    [Fact]
    public void 英文以空白切詞比對()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("smtp", "SMTP 設定", "設定 SMTP Server 與 Port")
        };

        var result = HelpChapterScorer.SelectChapters("SMTP 要怎麼設定？", chapters, maxTokenBudget: 10000);

        Assert.Single(result);
    }

    [Fact]
    public void related清單裡不存在的章節id會被忽略不拋例外()
    {
        var chapters = new List<HelpChapter>
        {
            Chapter("main", "郵件通知", "郵件通知相關內容", related: new List<string> { "does-not-exist" })
        };

        var result = HelpChapterScorer.SelectChapters("郵件通知怎麼設定？", chapters, maxTokenBudget: 10000);

        Assert.Single(result);
        Assert.Equal("main", result[0].Id);
    }
}
