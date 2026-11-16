using System.Net;
using System.Net.Sockets;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 今日焦點：模型回空形狀（[]、{}）時視為「沒有焦點」並快取，同一組輸入不再重打 AI；
/// 其他型別與真正的格式錯誤維持失敗、不快取。以本機 HttpListener 扮 AI 端點，走真的 WebAiService 與 AIService。
/// </summary>
public class AiFocusEmptyResultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-ai-empty-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public AiFocusEmptyResultTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* SQLite 連線池可能仍占用檔案 */ }
    }

    private static DashboardDto Dashboard() => new() { From = "2026-11-01", To = "2026-11-14", HighRiskDays = 4, MediumRiskDays = 2 };

    [Theory]
    [InlineData("[]")]
    [InlineData("[ \n ]")]
    [InlineData("{}")]
    public async Task 空形狀回覆視為沒有焦點且快取_第二次不再呼叫(string content)
    {
        using var server = new FakeAiServer(content);
        var service = new AiInsightService(CreateWebAi(server.Port));

        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        var afterFirst = server.Requests;
        Assert.True(afterFirst > 0, "前提：第一次確實打到 AI 端點");

        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        Assert.Equal(afterFirst, server.Requests);
    }

    [Fact]
    public async Task 合法的空清單回覆_同樣沒有焦點且快取()
    {
        using var server = new FakeAiServer("{\"items\":[]}");
        var service = new AiInsightService(CreateWebAi(server.Port));

        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        var afterFirst = server.Requests;
        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        Assert.Equal(afterFirst, server.Requests);
    }

    [Fact]
    public async Task 真正的格式錯誤_不快取_下次仍會重打()
    {
        using var server = new FakeAiServer("這不是 JSON");
        var service = new AiInsightService(CreateWebAi(server.Port));

        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        var afterFirst = server.Requests;
        Assert.Null(await service.TodayFocusAsync(Dashboard()));
        Assert.True(server.Requests > afterFirst, "格式錯誤不是「沒有焦點」，不應被當成空結果快取");
    }

    [Fact]
    public async Task 未標記允許空結果的型別_空形狀維持失敗()
    {
        using var server = new FakeAiServer("[]");
        var ai = CreateWebAi(server.Port);

        Assert.Null(await ai.GenerateAsync<PlainResult>("plain|1", "system", "user"));
        var afterFirst = server.Requests;
        Assert.Null(await ai.GenerateAsync<PlainResult>("plain|1", "system", "user"));
        Assert.True(server.Requests > afterFirst);
    }

    private sealed class PlainResult
    {
        public string Text { get; set; } = string.Empty;
    }

    private WebAiService CreateWebAi(int port)
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value =>
        {
            value.AiProvider = LogForesight.Core.Configuration.AiProviders.Local;
            value.AiBaseUrl = $"http://127.0.0.1:{port}";
            value.AiModel = "test-model";
        });
        return new WebAiService(new AiCacheStore(_backend.Blob("ai_cache")), settings,
            new AiUsageStore(_backend.Blob("ai_usage")), new AiRequestGate(() => 1));
    }

    /// <summary>每個請求都回同一段 chat completions 內容，記錄請求數。</summary>
    private sealed class FakeAiServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public int Port { get; }
        public int Requests => Volatile.Read(ref _requests);

        public FakeAiServer(string content)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
            });
            var bytes = Encoding.UTF8.GetBytes(body);
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                        Interlocked.Increment(ref _requests);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                        context.Response.Close();
                    }
                }
                catch (OperationCanceledException) { }
                catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
