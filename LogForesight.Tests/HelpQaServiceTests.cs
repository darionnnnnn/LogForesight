using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書 AI 問答的服務層串接（docs/archive/FEEDBACK-15-PLAN.md 批次E-3）：AI 未設定短路、
/// 問題格式驗證、成功／失敗路徑。選節演算法本身的計分細節見 HelpChapterScorerTests——
/// 這裡刻意用真實的 HelpContentService（內嵌資源內容穩定，不是每次跑測試都變的東西），
/// 只有 AI 呼叫本身打樁。
/// </summary>
public class HelpQaServiceTests
{
    private readonly HelpContentService _content = new();
    private readonly FakeWebAi _ai = new();

    private HelpQaService Create() => new(_content, _ai);

    [Fact]
    public async Task AI未設定時直接回傳null不呼叫AI()
    {
        _ai.Available = false;

        var result = await Create().AskAsync("怎麼抑制規則的告警？");

        Assert.Null(result);
        Assert.Equal(0, _ai.Calls);
    }

    [Fact]
    public async Task 問題為空白時拋出驗證例外()
    {
        await Assert.ThrowsAsync<DomainException>(() => Create().AskAsync("   "));
    }

    [Fact]
    public async Task 問題超過長度上限時拋出驗證例外()
    {
        var tooLong = new string('a', 501);
        await Assert.ThrowsAsync<DomainException>(() => Create().AskAsync(tooLong));
    }

    [Fact]
    public async Task AI呼叫失敗降級回傳null()
    {
        _ai.Response = null;   // FakeWebAi：null＝降級

        var result = await Create().AskAsync("怎麼抑制規則的告警？");

        Assert.Null(result);
    }

    [Fact]
    public async Task 成功時回傳AI答案並附上引用章節()
    {
        _ai.Response = "測試回答內容";

        var result = await Create().AskAsync("怎麼抑制某條規則的告警通知？");

        Assert.NotNull(result);
        Assert.Equal("測試回答內容", result!.Answer);
        Assert.NotEmpty(result.CitedChapterIds);
    }

    [Fact]
    public async Task 選中的章節內容與問題本身都會出現在送給AI的prompt裡()
    {
        _ai.Response = "答";

        await Create().AskAsync("SMTP 郵件通知要怎麼設定？");

        Assert.Contains("SMTP", _ai.LastUserPrompt);
        Assert.Contains("使用者問題", _ai.LastUserPrompt);
        Assert.Contains("SMTP 郵件通知要怎麼設定？", _ai.LastUserPrompt);
    }

    [Fact]
    public async Task 完全比對不到章節時仍會呼叫AI但不附任何章節內容()
    {
        _ai.Response = "答";

        var result = await Create().AskAsync("貓咪螞蟻打呵欠 zzz qqq xyz");

        Assert.NotNull(result);
        Assert.Empty(result!.CitedChapterIds);
        Assert.Contains("沒有找到與這個問題相關的說明書章節", _ai.LastUserPrompt);
    }
}
