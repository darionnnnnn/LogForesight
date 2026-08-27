using System.Text.Json;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：IWebAiService ──────────────────────────────────────────────────
// 原本是 AiInsightServiceTests 裡的巢狀 private 類別，搬到這裡集中管理。

internal sealed class FakeWebAi : IWebAiService
{
    public bool Available { get; set; } = true;
    public string? Response { get; set; }   // 要回的 JSON；null＝降級
    public int Calls { get; private set; }
    public string? LastUserPrompt { get; private set; }

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class
    {
        Calls++;
        LastUserPrompt = userPrompt;
        return Task.FromResult(Response == null ? null : JsonSerializer.Deserialize<T>(Response, Opts));
    }

    public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt)
    {
        Calls++;
        LastUserPrompt = userPrompt;
        return Task.FromResult(Response);
    }
}

// ── 測試替身：IAiService（docs/archive/FEEDBACK-12-PLAN.md §3.8-1/§3.8-3）────────────────
// 讓 LogAnalysisService／NetiqPipelineService 的測試不必真的打 AI API。

/// <summary>
/// IAiService 測試替身。預設立即回傳 <see cref="NextContent"/>（合法 JSON）成功；
/// 需要模擬逾時、取消、失敗或依序回不同內容時，覆寫 <see cref="Behavior"/>——
/// 例如掛一個永遠不完成的 <see cref="TaskCompletionSource"/> 直到 <c>ct</c> 觸發，
/// 用來驗證「搜尋不等 AI」（NetIQ 搜尋不因 AI 卡住而停下）。
///
/// 呼叫紀錄（<see cref="Calls"/>／<see cref="Prompts"/>／<see cref="Labels"/>）用 lock 保護：
/// 現行 pipeline 平行處理多台 Sentinel 時，AI 呼叫可能來自不同執行緒同時進來
/// （§3 兩階段改造完成前是如此；改造後單一消費者則天然序列化，但替身本身仍保持執行緒安全）。
/// </summary>
internal sealed class FakeAiService : IAiService
{
    private readonly object _lock = new();
    private readonly List<string> _prompts = new();
    private readonly List<string> _labels = new();
    private int _calls;

    public int Calls => _calls;
    public IReadOnlyList<string> Prompts { get { lock (_lock) return _prompts.ToList(); } }
    public IReadOnlyList<string> Labels { get { lock (_lock) return _labels.ToList(); } }

    /// <summary>每次 ChatAsync 呼叫時執行；未設定時用預設行為（立即回傳 NextContent 成功）。</summary>
    public Func<string, CancellationToken, Task<AiResponse>>? Behavior { get; set; }

    /// <summary>Behavior 未覆寫時的預設回應內容——通常是合法 JSON，供 ChatJsonAsync&lt;T&gt; 解析</summary>
    public string NextContent { get; set; } = "{}";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<AiResponse> ChatAsync(string prompt, string? systemPrompt = null, bool jsonMode = false,
        string? model = null, double temperature = 0.2, int? maxTokens = null, string label = "chat",
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        lock (_lock)
        {
            _prompts.Add(prompt);
            _labels.Add(label);
        }

        if (Behavior != null)
        {
            return await Behavior(prompt, ct);
        }

        ct.ThrowIfCancellationRequested();
        return new AiResponse { Success = true, Content = NextContent };
    }

    public async Task<AiJsonResult<T>> ChatJsonAsync<T>(string prompt, string? systemPrompt = null,
        Func<T, bool>? validate = null, string? model = null, double temperature = 0.2, int? maxTokens = null,
        string label = "chat-json", CancellationToken ct = default) where T : class
    {
        var response = await ChatAsync(prompt, systemPrompt, jsonMode: true, model: model, temperature: temperature,
            maxTokens: maxTokens, label: label, ct: ct);

        if (!response.Success)
        {
            return new AiJsonResult<T> { Success = false, Error = response.Error, Attempts = 1 };
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(response.Content, JsonOpts);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed == null || (validate != null && !validate(parsed)))
        {
            return new AiJsonResult<T> { Success = false, RawContent = response.Content, Error = "測試替身：解析失敗或未通過 validate", Attempts = 1 };
        }

        return new AiJsonResult<T> { Success = true, Value = parsed, RawContent = response.Content, Attempts = 1 };
    }
}
