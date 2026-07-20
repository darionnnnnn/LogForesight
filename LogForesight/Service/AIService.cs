using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NLog;
using Polly;
using Polly.Retry;

namespace LogForesight;

/// <summary>AI 呼叫的結構化結果：錯誤與正常內容分離，呼叫端不需靠字串前綴判斷失敗</summary>
public class AiResponse
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Error { get; init; }
}

/// <summary>ChatJsonAsync 的結果：JSON 解析並通過內容檢查才算成功，含實際嘗試次數供除錯</summary>
public class AiJsonResult<T> where T : class
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string RawContent { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int Attempts { get; init; }
}

public class AIService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly int _maxTokens;
    private readonly int _jsonRetryCount;
    private readonly double? _frequencyPenalty;
    private readonly double? _presencePenalty;
    private readonly JsonObject? _extraRequestFields;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly IPromptDumper _dumper;

    /// <summary>
    /// 請求佇列：同一時間只發出一個 request 給 AI API，其餘呼叫依序排隊。
    /// 本機 llama.cpp 同時處理多個請求會互搶 GPU 資源導致全部變慢甚至逾時，序列化最穩定。
    /// </summary>
    private readonly SemaphoreSlim _requestQueue = new(1, 1);

    public AIService(AiSettings settings, IPromptDumper? dumper = null)
    {
        _dumper = dumper ?? new NullPromptDumper();
        // 完全停用連線池（PooledConnectionLifetime=0 依官方文件即為「歸還後立即失效」）。
        // 從實際 log 的時間戳確認："response ended prematurely" 幾乎都發生在前一次呼叫剛
        // 結束後幾十毫秒內，不是生成到一半斷線——這是「連線池裡的連線其實已被對方關閉，
        // 用戶端還不知道就拿去重用」的典型特徵，跟 HTTP/2 協商無關（先前以為是協商問題，
        // 加了固定 HTTP/1.1 版本也沒解決，故排除該假設）。
        // 每次呼叫都間隔數秒到數十秒、單次又動輒數十秒，重用連線省下的 TCP/握手成本
        // 相對生成時間微乎其微，直接停用連線池換取穩定性划算。
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            // 固定用 HTTP/1.1，不讓 .NET 嘗試 HTTP/2 協商——llama.cpp server 本身只說 HTTP/1.1，
            // 但長時間生成（本模型單次常見 60~80 秒）中途，某些反向代理/負載平衡器對 HTTP/2
            // 雙向串流處理不完整時容易在回應途中被切斷（症狀："The response ended
            // prematurely."），固定版本可避免這類協商造成的中途斷線
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        _baseUrl = settings.BaseUrl.TrimEnd('/');
        _maxTokens = settings.MaxTokens;
        _jsonRetryCount = settings.JsonRetryCount;
        _frequencyPenalty = settings.FrequencyPenalty;
        _presencePenalty = settings.PresencePenalty;

        // 額外請求欄位原封不動合併進送給 llama.cpp 的 JSON（例如限制模型「思考」長度的參數）。
        // 這類參數的實際欄位名稱因模型/伺服器版本而異（沒有統一標準，跟 max_tokens 不同），
        // 用原始 JSON 透傳而不是寫死成強型別欄位，設定檔可直接調整不需要改程式碼再重新編譯
        if (settings.ExtraRequestFields is { Count: > 0 })
        {
            _extraRequestFields = new JsonObject();
            foreach (var (key, value) in settings.ExtraRequestFields)
            {
                _extraRequestFields[key] = JsonNode.Parse(value.GetRawText());
            }
        }

        // Polly 重試：連線失敗、HTTP 錯誤、逾時、空回應皆重試，間隔指數遞增（10s → 20s → 40s）。
        // 涵蓋模型剛重啟、瞬間過載等暫時性失敗；重試全部耗盡才回報失敗，由呼叫端降級處理。
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = settings.RetryCount,
                Delay = TimeSpan.FromSeconds(settings.RetryDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<EmptyAiResponseException>()
                    .Handle<AiEnvelopeParseException>(),
                OnRetry = args =>
                {
                    var msg = $"AI 呼叫失敗（{args.Outcome.Exception?.Message}），" +
                             $"{args.RetryDelay.TotalSeconds:0} 秒後重試（第 {args.AttemptNumber + 1}/{settings.RetryCount} 次）...";
                    Console.WriteLine($"  {msg}");
                    Log.Warn(args.Outcome.Exception, "AI 網路層呼叫失敗，第 {Attempt}/{Total} 次重試", args.AttemptNumber + 1, settings.RetryCount);
                    return default;
                }
            })
            .Build();
    }

    /// <param name="jsonMode">true 時透過 response_format=json_object 讓 llama.cpp 以 grammar 強制輸出合法 JSON</param>
    /// <param name="maxTokens">覆寫預設的 token 上限；null 則用設定檔的 Ai.MaxTokens。
    /// 短小的終端 JSON（如每日總覽、前置掃描）該用較小的上限——模型一旦退化重複輸出，
    /// 上限越大只是讓失敗的嘗試跑越久才觸頂，不會讓成功率變高；篇幅本來就較長的深入分析
    /// 才需要調大</param>
    public async Task<AiResponse> ChatAsync(string prompt, string? systemPrompt = null, bool jsonMode = false,
        string model = "local-model", double temperature = 0.2, int? maxTokens = null, string label = "chat")
    {
        var messages = new List<OpenAIMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new OpenAIMessage { Role = "system", Content = systemPrompt });
        }
        messages.Add(new OpenAIMessage { Role = "user", Content = prompt });

        var effectiveMaxTokens = maxTokens ?? _maxTokens;

        // context 預算的共用防線：所有 AI 呼叫都經過這裡，是唯一同時知道 prompt 與輸出上限的咽喉點。
        // 小模型（實測 context 20480）爆 context 時 server 端行為不可靠（可能靜默截頭、可能報錯），
        // 這裡先保守估算並在超標時記 WARN——各呼叫類型本身已有結構性上限或字元硬上限做實際截斷，
        // 這道防線負責在那些截斷失效時把問題顯性化，而不是等 server 端悄悄吞掉一段輸入。
        if (effectiveMaxTokens > 0 &&
            PromptBudget.ExceedsBudget(prompt + (systemPrompt ?? ""), effectiveMaxTokens, out var estimatedPromptTokens))
        {
            Console.WriteLine($"  ⚠ [{label}] prompt 估計 {estimatedPromptTokens} tokens + 輸出上限 {effectiveMaxTokens}，" +
                              $"可能超出 context 預算（約 {PromptBudget.UsableTokens}），回應有被 server 端截斷的風險。");
            Log.Warn("[{Label}] prompt 估計 {PromptTokens} tokens + maxTokens {MaxTokens} 可能超出可用預算 {Usable}",
                label, estimatedPromptTokens, effectiveMaxTokens, PromptBudget.UsableTokens);
        }

        var requestBody = new OpenAIRequest
                          {
                              Model = model,
                              Temperature = temperature,
                              Messages = messages,
                              ResponseFormat = jsonMode ? new OpenAIResponseFormat() : null,
                              MaxTokens = effectiveMaxTokens > 0 ? effectiveMaxTokens : null,
                              FrequencyPenalty = _frequencyPenalty,
                              PresencePenalty = _presencePenalty
                          };

        // 把設定檔裡的額外欄位（如思考長度上限）合併進標準欄位之外送出
        JsonNode requestNode = JsonSerializer.SerializeToNode(requestBody)!;
        if (_extraRequestFields != null)
        {
            foreach (var (key, value) in _extraRequestFields)
            {
                requestNode[key] = value?.DeepClone();
            }
        }

        // 只記長度不記內容：prompt 本身可能有數 KB，完整寫進 log 會讓檔案隨呼叫次數暴增
        Log.Debug("Chat 請求：jsonMode={JsonMode}, promptChars={PromptChars}, systemPromptChars={SystemPromptChars}",
            jsonMode, prompt.Length, systemPrompt?.Length ?? 0);

        await _requestQueue.WaitAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            var content = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/chat/completions", requestNode, ct);
                response.EnsureSuccessStatusCode();

                // 先讀成字串再自己解析，而不是直接 ReadFromJsonAsync：中間的 proxy/gateway
                // 偶爾會用 HTTP 200 回傳非 JSON 的純文字/HTML 錯誤頁（例如逾時橫幅），
                // 這樣才能在解析失敗時把實際內容記下來，而不是只有一句無從查起的例外訊息
                var rawBody = await response.Content.ReadAsStringAsync(ct);
                OpenAIResponse? result;
                try
                {
                    result = JsonSerializer.Deserialize<OpenAIResponse>(rawBody);
                }
                catch (JsonException ex)
                {
                    var preview = PreviewForLog(rawBody);
                    Console.WriteLine($"    AI 回應信封不是合法 JSON，預覽：{preview}");
                    Log.Warn(ex, "AI 回應信封不是合法 JSON，HTTP 狀態碼={StatusCode}，預覽：{Preview}",
                        (int)response.StatusCode, preview);
                    // 包裝成同一種例外類型丟出去，讓 Polly 依 ShouldHandle 判斷是否重試，
                    // 且不掩蓋原始例外（透過 InnerException 保留完整堆疊供除錯）
                    throw new AiEnvelopeParseException(ex);
                }

                var text = result?.Choices.FirstOrDefault()?.Message.Content;

                // 空回應視為失敗觸發重試，不讓「無內容」流進分析結果
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new EmptyAiResponseException();
                }

                return text;
            });

            Log.Info("Chat 完成：耗時={ElapsedMs}ms, 回應長度={ResponseChars} 字元", sw.ElapsedMilliseconds, content.Length);
            _dumper.Dump(label, systemPrompt ?? "", prompt, content);
            return new AiResponse { Success = true, Content = content };
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Chat 最終失敗（重試已耗盡）：耗時={ElapsedMs}ms", sw.ElapsedMilliseconds);
            return new AiResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            _requestQueue.Release();
        }
    }

    /// <summary>
    /// 呼叫 AI 並要求回覆符合 JSON 契約，格式或內容檢查未通過時重新請求（最多 JsonRetryCount 次）。
    /// response_format=json_object 只保證輸出是「合法 JSON」，不保證是我們要的物件形狀
    /// （例如模型可能回傳陣列 [ {...}, {...} ] 或內容欄位塞入異常冗長的重複文字，兩者都語法合法但不符期望），
    /// 所以語法解析之外還需要 validate 檢查內容是否合理。地端模型呼叫不用省，失敗就重問。
    /// </summary>
    /// <param name="validate">額外的內容合理性檢查（如必填欄位非空、長度未超出正常摘要範圍），null 則只要求解析成功</param>
    /// <param name="maxTokens">覆寫預設的 token 上限，見 <see cref="ChatAsync"/> 的說明</param>
    public async Task<AiJsonResult<T>> ChatJsonAsync<T>(string prompt, string? systemPrompt = null,
        Func<T, bool>? validate = null, string model = "local-model", double temperature = 0.2, int? maxTokens = null,
        string label = "chat-json") where T : class
    {
        string rawContent = string.Empty;
        string? lastError = null;
        int totalAttempts = _jsonRetryCount + 1;

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            var response = await ChatAsync(prompt, systemPrompt, jsonMode: true, model: model, temperature: temperature, maxTokens: maxTokens,
                label: totalAttempts > 1 ? $"{label}-a{attempt}" : label);

            if (!response.Success)
            {
                lastError = response.Error;
            }
            else
            {
                rawContent = response.Content;
                var parsed = AiJson.TryParse<T>(response.Content);

                if (parsed != null && (validate == null || validate(parsed)))
                {
                    if (attempt > 1)
                    {
                        Log.Info("ChatJsonAsync 於第 {Attempt}/{Total} 次嘗試成功，型別={Type}", attempt, totalAttempts, typeof(T).Name);
                    }
                    return new AiJsonResult<T> { Success = true, Value = parsed, RawContent = rawContent, Attempts = attempt };
                }

                if (parsed == null)
                {
                    lastError = "AI 回覆不是合法 JSON 或格式不符契約";
                    // 印出回覆預覽方便診斷（截斷、前言文字、格式跑掉等），不然完全是黑盒子；
                    // 只取頭尾各一截、控制在數百字元內，不是把整段回覆存進去
                    var preview = PreviewForLog(response.Content);
                    Console.WriteLine($"    回覆預覽：{preview}");
                    Log.Warn("JSON 解析失敗（第 {Attempt}/{Total} 次），型別={Type}，回覆長度={ResponseChars}，預覽：{Preview}",
                        attempt, totalAttempts, typeof(T).Name, response.Content.Length, preview);
                }
                else
                {
                    lastError = "AI 回覆內容未通過檢查（可能為異常重複文字或欄位不合理）";
                    // JSON 語法沒問題、是內容合理性沒過關：記錄解析出的物件本身（本來就是我們定義的
                    // 小結構，不是原始長文字），才看得出究竟是哪個欄位不合理
                    Log.Warn("JSON 內容檢查未通過（第 {Attempt}/{Total} 次），型別={Type}，解析結果：{Parsed}",
                        attempt, totalAttempts, typeof(T).Name, SafeSerialize(parsed));
                }
            }

            if (attempt < totalAttempts)
            {
                Console.WriteLine($"  {lastError}，重新請求（第 {attempt + 1}/{totalAttempts} 次）...");
            }
        }

        Log.Error("ChatJsonAsync 最終失敗（{Total} 次嘗試皆未通過），型別={Type}，原因：{Error}",
            totalAttempts, typeof(T).Name, lastError);
        return new AiJsonResult<T> { Success = false, RawContent = rawContent, Error = lastError, Attempts = totalAttempts };
    }

    /// <summary>把解析出的（已是我們自訂的小型結構化物件，不是原始長文字）結果序列化成一行方便寫入 log，
    /// 並保守截斷長度以防單一欄位異常冗長時仍把 log 撐大</summary>
    private static string SafeSerialize<T>(T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            return json.Length > 500 ? json[..500] + "...(截斷)" : json;
        }
        catch
        {
            return "(無法序列化)";
        }
    }

    /// <summary>把回覆頭尾各取一截、攤平成單行，方便印在 console 診斷格式問題而不洗版</summary>
    private static string PreviewForLog(string content, int headLength = 120, int tailLength = 60)
    {
        var flat = string.Join(' ', content.Split('\r', '\n').Where(s => s.Length > 0));
        if (flat.Length <= headLength + tailLength)
        {
            return flat;
        }
        return $"{flat[..headLength]} …(共 {content.Length} 字元)… {flat[^tailLength..]}";
    }

    private class EmptyAiResponseException : Exception
    {
        public EmptyAiResponseException() : base("模型回傳空內容") { }
    }

    /// <summary>HTTP 狀態碼是成功，但回應本體不是合法 JSON（常見於中間 proxy/gateway 用 200
    /// 回傳純文字/HTML 錯誤頁）。視為暫時性失敗交給 Polly 重試，而不是直接判定整次呼叫失敗。</summary>
    private class AiEnvelopeParseException : Exception
    {
        public AiEnvelopeParseException(JsonException inner) : base("AI 回應信封不是合法 JSON", inner) { }
    }

    private class OpenAIRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OpenAIMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenAIResponseFormat? ResponseFormat { get; set; }

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FrequencyPenalty { get; set; }

        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? PresencePenalty { get; set; }
    }

    private class OpenAIResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "json_object";
    }

    private class OpenAIMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private class OpenAIResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAIChoice> Choices { get; set; } = new();
    }

    private class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage Message { get; set; } = new();
    }
}