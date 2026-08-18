using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回歸測試（docs/archive/FEEDBACK-7-PLAN.md）：Web 排程／立即執行原本必炸的 bug。
///
/// 根因：<c>IConfiguration</c> 的 <c>ConfigurationBinder</c> 綁不出
/// <c>Dictionary&lt;string, JsonElement&gt;</c>——JsonElement 沒有可寫屬性、也沒有
/// TypeConverter，binder 對這種欄位只能產生 <c>default(JsonElement)</c>
/// （<c>ValueKind=Undefined</c>）。舊版 <see cref="AIService"/> 建構子對這種空值呼叫
/// <c>GetRawText()</c> 會丟 <see cref="InvalidOperationException"/>，導致整個排程/立即
/// 執行在分析開始前就中止（本機與 NetIQ 全部主機都不會跑）。
/// </summary>
public class AIServiceExtraRequestFieldsTests
{
    /// <summary>
    /// 用與 Web appsettings.json 相同形狀的 JSON，走真正的 ConfigurationBinder
    /// （<c>IConfiguration.Get&lt;AiSettings&gt;()</c>）重現炸點的前提。
    ///
    /// 實測結果比原先設想的更細緻：巢狀物件（<c>chat_template_kwargs</c>）binder 能正確
    /// 綁出合法的 JsonElement；**純量值**（<c>rep_pen: 1.3</c> 這種沒有子節點的葉節點）
    /// 才會被綁成 <c>default(JsonElement)</c>（ValueKind=Undefined）——這正是實際 log
    /// 炸掉的那個欄位形態，<c>GetRawText()</c> 對它呼叫必定擲例外。
    /// </summary>
    [Fact]
    public void ConfigurationBinder_把純量ExtraRequestField綁成Undefined元素()
    {
        var json = """
            {
              "Ai": {
                "BaseUrl": "http://localhost:8080",
                "ExtraRequestFields": {
                  "chat_template_kwargs": { "enable_thinking": false },
                  "rep_pen": 1.3
                }
              }
            }
            """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var aiSettings = config.GetSection("Ai").Get<AiSettings>();

        Assert.NotNull(aiSettings);
        Assert.NotNull(aiSettings!.ExtraRequestFields);
        // 巢狀物件：binder 綁得出合法 JsonElement
        Assert.Equal(JsonValueKind.Object, aiSettings.ExtraRequestFields!["chat_template_kwargs"].ValueKind);
        // 純量值：binder 只能產生 Undefined，對它呼叫 GetRawText() 必炸——這正是事故重現
        var scalar = aiSettings.ExtraRequestFields["rep_pen"];
        Assert.Equal(JsonValueKind.Undefined, scalar.ValueKind);
        Assert.Throws<InvalidOperationException>(() => scalar.GetRawText());
    }

    /// <summary>
    /// 本次修復的主張：即使 ExtraRequestFields 裡混著 Undefined 元素，AIService 建構子
    /// 也不應該擲例外——這是縱深防禦。（§12 起這個欄位改存 DB 的 JSON 文字、由
    /// RuntimeSettingsResolver 解析，不再經 ConfigurationBinder，但壞值防線仍該留著。）
    /// </summary>
    [Fact]
    public void ExtraRequestFields含Undefined元素_建構不擲例外()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1",
            RetryCount = 1,
            TimeoutSeconds = 1,
            ExtraRequestFields = new Dictionary<string, JsonElement>
            {
                ["bad"] = default, // ValueKind = Undefined，模擬 ConfigurationBinder 的產物
                ["good"] = JsonSerializer.SerializeToElement(1.3)
            }
        };

        var ex = Record.Exception(() => new AIService(settings));

        Assert.Null(ex);
    }

    /// <summary>正常路徑（JsonSerializer 反序列化產生的合法 JsonElement）維持可用，不受本次修復影響。</summary>
    [Fact]
    public void ExtraRequestFields全部合法_建構照常成功()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1",
            RetryCount = 1,
            TimeoutSeconds = 1,
            ExtraRequestFields = new Dictionary<string, JsonElement>
            {
                ["rep_pen"] = JsonSerializer.SerializeToElement(1.3)
            }
        };

        var ex = Record.Exception(() => new AIService(settings));

        Assert.Null(ex);
    }
}

/// <summary>
/// AIService 的 Provider 網址／認證／主體組裝與 ChatJsonAsync 迭代修補重試測試（任務 O2）
/// </summary>
public class AIServiceProviderAndRetryTests
{
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request);

            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                LastRequestBody = body;
                RequestBodies.Add(body);
            }
            else
            {
                RequestBodies.Add("");
            }

            if (ResponseFactory != null)
            {
                return ResponseFactory(request);
            }

            var jsonResponse = """
                {
                    "choices": [
                        {
                            "message": {
                                "role": "assistant",
                                "content": "{\"status\":\"ok\"}"
                            }
                        }
                    ]
                }
                """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private class TestResult
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    [Fact]
    public async Task AzureOpenAi_請求網址含deployment與apiVersion_認證用apiKey標頭_主體不含model()
    {
        var handler = new TestHttpMessageHandler();
        var settings = new AiSettings
        {
            Provider = "AzureOpenAi",
            BaseUrl = "https://my-resource.openai.azure.com",
            AzureDeployment = "gpt-4o-mini",
            AzureApiVersion = "2024-10-21",
            ApiKey = "azure-test-key",
            TimeoutSeconds = 5,
            RetryCount = 1
        };
        var service = new AIService(settings, handler);

        var response = await service.ChatAsync("測試 Azure prompt");

        Assert.True(response.Success);
        Assert.NotNull(handler.LastRequest);

        // 1. 網址檢查：含 deployment 與 api-version
        var uri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://my-resource.openai.azure.com/openai/deployments/gpt-4o-mini/chat/completions?api-version=2024-10-21", uri);

        // 2. 標頭檢查：認證用 api-key 標頭，不使用 Authorization
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.True(handler.LastRequest.Headers.Contains("api-key"));
        Assert.Equal("azure-test-key", handler.LastRequest.Headers.GetValues("api-key").FirstOrDefault());

        // 3. 主體檢查：主體不含 model 欄位
        Assert.NotNull(handler.LastRequestBody);
        var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task OpenAi官方_未填BaseUrl時使用官方端點_認證用Bearer_主體帶模型名稱()
    {
        var handler = new TestHttpMessageHandler();
        var settings = new AiSettings
        {
            Provider = "OpenAi",
            BaseUrl = "",
            Model = "gpt-4o",
            ApiKey = "sk-openai-secret",
            TimeoutSeconds = 5,
            RetryCount = 1
        };
        var service = new AIService(settings, handler);

        var response = await service.ChatAsync("測試 OpenAI prompt");

        Assert.True(response.Success);
        Assert.NotNull(handler.LastRequest);

        // 1. 網址檢查：官方端點
        var uri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://api.openai.com/v1/chat/completions", uri);

        // 2. 標頭檢查：Bearer 認證
        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-openai-secret", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.False(handler.LastRequest.Headers.Contains("api-key"));

        // 3. 主體檢查：帶模型名稱
        Assert.NotNull(handler.LastRequestBody);
        var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(doc.RootElement.TryGetProperty("model", out var modelProp));
        Assert.Equal("gpt-4o", modelProp.GetString());
    }

    [Fact]
    public async Task OpenAi官方_有填BaseUrl時以BaseUrl為準()
    {
        var handler = new TestHttpMessageHandler();
        var settings = new AiSettings
        {
            Provider = "OpenAi",
            BaseUrl = "https://custom-proxy.internal:8443/",
            Model = "gpt-4o",
            ApiKey = "sk-openai-secret",
            TimeoutSeconds = 5,
            RetryCount = 1
        };
        var service = new AIService(settings, handler);

        var response = await service.ChatAsync("測試 OpenAI proxy prompt");

        Assert.True(response.Success);
        Assert.NotNull(handler.LastRequest);

        var uri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://custom-proxy.internal:8443/v1/chat/completions", uri);
    }

    [Fact]
    public async Task LocalProvider_未設金鑰時不送Authorization標頭_既有行為不變()
    {
        var handler = new TestHttpMessageHandler();
        var settings = new AiSettings
        {
            Provider = "Local",
            BaseUrl = "http://localhost:8080",
            ApiKey = "",
            Model = "local-model",
            TimeoutSeconds = 5,
            RetryCount = 1
        };
        var service = new AIService(settings, handler);

        var response = await service.ChatAsync("測試 Local prompt");

        Assert.True(response.Success);
        Assert.NotNull(handler.LastRequest);

        // 1. 網址檢查：{BaseUrl}/v1/chat/completions
        var uri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("http://localhost:8080/v1/chat/completions", uri);

        // 2. 標頭檢查：不送 Authorization 標頭，亦無 api-key
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.False(handler.LastRequest.Headers.Contains("api-key"));

        // 3. 主體檢查：帶模型名稱 local-model
        Assert.NotNull(handler.LastRequestBody);
        var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(doc.RootElement.TryGetProperty("model", out var modelProp));
        Assert.Equal("local-model", modelProp.GetString());
    }

    [Fact]
    public async Task ChatJsonAsync_第二次嘗試送出的prompt含上一次失敗原因_第一次不含()
    {
        var handler = new TestHttpMessageHandler();
        int callCount = 0;
        handler.ResponseFactory = req =>
        {
            callCount++;
            if (callCount == 1)
            {
                var badJson = """
                    {
                        "choices": [
                            {
                                "message": {
                                    "role": "assistant",
                                    "content": "抱歉，這不是合法的 JSON 內容，請見諒。"
                                }
                            }
                        ]
                    }
                    """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(badJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var goodJson = """
                    {
                        "choices": [
                            {
                                "message": {
                                    "role": "assistant",
                                    "content": "{\"status\":\"success\"}"
                                }
                            }
                        ]
                    }
                    """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(goodJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:8080",
            JsonRetryCount = 2,
            RetryCount = 1,
            TimeoutSeconds = 5
        };
        var service = new AIService(settings, handler);

        var result = await service.ChatJsonAsync<TestResult>("請以 JSON 輸出系統摘要");

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("success", result.Value?.Status);
        Assert.Equal(2, handler.RequestBodies.Count);

        // 檢查第 1 次請求的 prompt
        var firstBody = JsonDocument.Parse(handler.RequestBodies[0]);
        var firstMessages = firstBody.RootElement.GetProperty("messages");
        var firstUserPrompt = firstMessages[firstMessages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Equal("請以 JSON 輸出系統摘要", firstUserPrompt);
        Assert.DoesNotContain("前次嘗試失敗", firstUserPrompt);
        Assert.DoesNotContain("AI 回覆不是合法 JSON", firstUserPrompt);

        // 檢查第 2 次請求的 prompt
        var secondBody = JsonDocument.Parse(handler.RequestBodies[1]);
        var secondMessages = secondBody.RootElement.GetProperty("messages");
        var secondUserPrompt = secondMessages[secondMessages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.StartsWith("請以 JSON 輸出系統摘要", secondUserPrompt);
        Assert.Contains("前次嘗試失敗", secondUserPrompt);
        Assert.Contains("AI 回覆不是合法 JSON 或格式不符契約", secondUserPrompt);
        Assert.Contains("抱歉，這不是合法的 JSON 內容", secondUserPrompt);
    }

    [Fact]
    public async Task ChatJsonAsync_內容驗證失敗時第二次嘗試含驗證失敗原因()
    {
        var handler = new TestHttpMessageHandler();
        int callCount = 0;
        handler.ResponseFactory = req =>
        {
            callCount++;
            if (callCount == 1)
            {
                var invalidContentJson = """
                    {
                        "choices": [
                            {
                                "message": {
                                    "role": "assistant",
                                    "content": "{\"status\":\"invalid\"}"
                                }
                            }
                        ]
                    }
                    """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(invalidContentJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var validContentJson = """
                    {
                        "choices": [
                            {
                                "message": {
                                    "role": "assistant",
                                    "content": "{\"status\":\"valid\"}"
                                }
                            }
                        ]
                    }
                    """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(validContentJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:8080",
            JsonRetryCount = 2,
            RetryCount = 1,
            TimeoutSeconds = 5
        };
        var service = new AIService(settings, handler);

        var result = await service.ChatJsonAsync<TestResult>(
            "分析日誌狀態",
            validate: r => r.Status == "valid");

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("valid", result.Value?.Status);
        Assert.Equal(2, handler.RequestBodies.Count);

        var secondBody = JsonDocument.Parse(handler.RequestBodies[1]);
        var secondMessages = secondBody.RootElement.GetProperty("messages");
        var secondUserPrompt = secondMessages[secondMessages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains("AI 回覆內容未通過檢查", secondUserPrompt);
    }
}

