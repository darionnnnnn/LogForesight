using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AIService 的取消語意（docs/archive/FEEDBACK-12-PLAN.md §3.6）：抽出 IAiService 時新增了
/// <c>CancellationToken</c> 貫穿，目的是讓使用者按「停止」能立即掐斷排隊中／進行中的 AI 呼叫，
/// 而不是像過去一樣完全沒有取消路徑，最壞情況要等到 600 秒逾時 ×重試全部跑完。
///
/// 這裡釘住一個容易踩空的前提：Polly v8 的 <c>ResiliencePipeline.ExecuteAsync</c> 對「傳入的
/// <c>CancellationToken</c> 本身觸發」的 <see cref="OperationCanceledException"/> 一律不重試、
/// 直接往外拋——即使重試策略的 <c>ShouldHandle</c> 有納入 <c>TaskCanceledException</c>
/// （本專案的重試設定就有，用來重試逾時）。如果這個前提不成立，使用者取消時仍會被
/// Polly 誤判成可重試的暫時性失敗，跑滿全部重試次數才停下來——「停止」按鈕等於沒接上。
/// </summary>
public class AIServiceCancellationTests
{
    /// <summary>
    /// 已取消的 token：ExecuteAsync 應在第一次嘗試前就直接拋出，不觸發任何重試延遲。
    /// RetryDelaySeconds 刻意設得很大（10 秒），若行為退化成「當作一般失敗重試」，
    /// 耗時會遠超過本測試的斷言門檻，兩者形成明確反差，不依賴計時的巧合。
    /// </summary>
    [Fact]
    public async Task 已取消的token會立即中止不觸發重試()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 5, RetryDelaySeconds = 10, TimeoutSeconds = 5
        };
        var service = new AIService(settings);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChatAsync("hi", ct: cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"耗時 {sw.Elapsed}，疑似被 Polly 誤判為可重試的暫時性失敗而非立即取消（RetryDelaySeconds=10 秒）");
    }

    /// <summary>ChatJsonAsync 的重問迴圈裡沒有額外 try/catch 吞掉 ChatAsync 的取消例外，
    /// 取消要能原樣穿透整個 JSON 重試迴圈，消費者才能在迴圈外統一處理。</summary>
    [Fact]
    public async Task ChatJsonAsync也會讓取消例外原樣穿透()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 1, RetryDelaySeconds = 10, TimeoutSeconds = 5, JsonRetryCount = 3
        };
        var service = new AIService(settings);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 型別本身無意義：取消在第一次 ChatAsync 呼叫就會拋出，走不到解析階段
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ChatJsonAsync<object>("hi", ct: cts.Token));
    }

    /// <summary>對照組：一般連線失敗（未取消）仍走既有的降級路徑，回傳失敗結果而不是拋例外——
    /// 確保這次改動沒有把「AI 真的失敗」也誤判成取消。</summary>
    [Fact]
    public async Task 未取消時連線失敗仍回傳失敗結果而非拋例外()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 1, RetryDelaySeconds = 1, TimeoutSeconds = 1
        };
        var service = new AIService(settings);

        var response = await service.ChatAsync("hi");

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
    }
}
