using System.Net;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class AIServiceEndpointResolutionTests
{
    private const string CompletionJson = """
        {"choices":[{"message":{"role":"assistant","content":"controlled endpoint success"}}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
        """;

    [Theory]
    [InlineData("Local", "http://127.0.0.1:8080", "http://127.0.0.1:8080/v1/chat/completions")]
    [InlineData("Local", "http://127.0.0.1:8080/v1/", "http://127.0.0.1:8080/v1/chat/completions")]
    [InlineData("OpenAi", "http://127.0.0.1:8080/gateway/v1", "http://127.0.0.1:8080/gateway/v1/chat/completions")]
    [InlineData("Local", "http://127.0.0.1:8080/gateway?tenant=blue", "http://127.0.0.1:8080/gateway/v1/chat/completions?tenant=blue")]
    public async Task ChatAsync_sends_to_compatible_path_with_bearer_and_accepts_chat_response(
        string provider, string baseUrl, string expectedUrl)
    {
        var handler = new CaptureChatHandler();
        var service = new AIService(new AiSettings
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = "fixture-model",
            ApiKey = "controlled-bearer-secret",
            TimeoutSeconds = 5,
            RetryCount = 0
        }, handler);

        var response = await service.ChatAsync("endpoint integration probe");

        Assert.True(response.Success, response.Error);
        Assert.Equal("controlled endpoint success", response.Content);
        Assert.Equal(new Uri(expectedUrl), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("controlled-bearer-secret", handler.AuthorizationParameter);
        Assert.Contains("endpoint integration probe", handler.RequestBody);
    }

    [Fact]
    public async Task AiProbe_uses_the_shared_path_builder_and_bearer_auth_through_its_http_client()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value =>
        {
            value.AiProvider = AiProviders.Local;
            value.AiBaseUrl = "http://127.0.0.1:8080/service/v1?tenant=probe";
            value.AiModel = "fixture-model";
            value.AiApiKeyEnc = CryptoHelper.Encrypt("probe-bearer-secret");
        });

        var handler = new CaptureChatHandler();
        using var client = new HttpClient(handler);
        var result = await new AiProbeService(settings, client).RefreshAsync();

        Assert.True(result.IsReady, result.Detail);
        Assert.Equal(AiProbeStatus.Ready, result.Status);
        Assert.Equal(new Uri("http://127.0.0.1:8080/service/v1/chat/completions?tenant=probe"), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("probe-bearer-secret", handler.AuthorizationParameter);
        Assert.Contains("\"content\":\"ping\"", handler.RequestBody);
    }

    private sealed class CaptureChatHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
