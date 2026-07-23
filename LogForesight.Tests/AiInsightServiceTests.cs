using System.Text.Json;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AI 加值層的**確定性部分**（docs/SCALE-2000-PLAN.md §6）：下鑽參數白名單驗證、
/// 靜默降級、無風險不呼叫。AI 實際輸出品質需 koboldcpp 才驗得到，不在單元測試範圍。
/// </summary>
public class AiInsightServiceTests
{
    private sealed class FakeWebAi : IWebAiService
    {
        public bool Available { get; set; } = true;
        public string? Response { get; set; }   // 要回的 JSON；null＝降級
        public int Calls { get; private set; }

        private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class
        {
            Calls++;
            return Task.FromResult(Response == null ? null : JsonSerializer.Deserialize<T>(Response, Opts));
        }
    }

    private static DashboardDto Dashboard(int high, int medium) => new()
    {
        From = "2026-07-17", To = "2026-07-23",
        HighRiskDays = high, MediumRiskDays = medium,
        Categories = new() { new DashboardCategoryDto { Category = "Storage", IssueCount = 8, AffectedHosts = 3, MaxSeverity = "Critical" } }
    };

    [Fact]
    public async Task 今日焦點_合法下鑽參數_組出連結()
    {
        var ai = new FakeWebAi { Response = "{\"items\":[{\"text\":\"磁碟問題集中在三台\",\"categories\":\"Storage\",\"riskLevels\":\"高,中\"}]}" };
        var svc = new AiInsightService(ai);

        var result = await svc.TodayFocusAsync(Dashboard(4, 2));

        Assert.NotNull(result);
        var item = result!.Items[0];
        Assert.Equal("磁碟問題集中在三台", item.Text);
        Assert.NotNull(item.Link);
        Assert.Contains("categories=Storage", item.Link);
        Assert.Contains("from=2026-07-17", item.Link);
    }

    [Fact]
    public async Task 今日焦點_非法類別_丟連結保留文字()
    {
        // AI 亂填一個不存在的類別＋一段 script——白名單擋掉，只留文字、不組連結
        var ai = new FakeWebAi { Response = "{\"items\":[{\"text\":\"注意這件事\",\"categories\":\"<script>\",\"riskLevels\":\"危\"}]}" };
        var svc = new AiInsightService(ai);

        var result = await svc.TodayFocusAsync(Dashboard(4, 2));

        Assert.NotNull(result);
        Assert.Equal("注意這件事", result!.Items[0].Text);
        Assert.Null(result.Items[0].Link);   // 參數沒過白名單 → 不給連結
    }

    [Fact]
    public async Task 今日焦點_無風險_不呼叫AI()
    {
        var ai = new FakeWebAi { Response = "{\"items\":[{\"text\":\"x\"}]}" };
        var svc = new AiInsightService(ai);

        var result = await svc.TodayFocusAsync(Dashboard(0, 0));

        Assert.Null(result);
        Assert.Equal(0, ai.Calls);   // 沒有可排序的東西，根本不發請求
    }

    [Fact]
    public async Task 今日焦點_AI降級_回null()
    {
        var svc = new AiInsightService(new FakeWebAi { Response = null });
        Assert.Null(await svc.TodayFocusAsync(Dashboard(4, 2)));
    }

    [Fact]
    public async Task 查詢歸納_空聚類_不呼叫AI()
    {
        var ai = new FakeWebAi { Response = "{\"text\":\"x\"}" };
        var svc = new AiInsightService(ai);

        var result = await svc.SummarizeQueryAsync(new List<IssueClusterDto>(), "salt");

        Assert.Null(result);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task 查詢歸納_有聚類_回白話()
    {
        var ai = new FakeWebAi { Response = "{\"text\":\"七台主機同日磁碟錯誤，疑似共通儲存設備\"}" };
        var svc = new AiInsightService(ai);

        var clusters = new List<IssueClusterDto>
        {
            new() { Source = "disk", EventId = 153, HostCount = 7, TotalCount = 40 }
        };
        var result = await svc.SummarizeQueryAsync(clusters, "salt");

        Assert.NotNull(result);
        Assert.Contains("儲存設備", result!.Text);
    }

    [Fact]
    public void Available_跟隨底層WebAi()
    {
        Assert.True(new AiInsightService(new FakeWebAi { Available = true }).Available);
        Assert.False(new AiInsightService(new FakeWebAi { Available = false }).Available);
    }

    [Fact]
    public void 輸入雜湊_相同輸入相同雜湊()
    {
        Assert.Equal(WebAiService.HashInput("abc"), WebAiService.HashInput("abc"));
        Assert.NotEqual(WebAiService.HashInput("abc"), WebAiService.HashInput("abd"));
    }
}
