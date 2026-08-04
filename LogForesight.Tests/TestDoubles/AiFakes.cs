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
