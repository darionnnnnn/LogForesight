using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63 輪批次 F：AI JSON 失敗診斷（F-1 呼叫資訊、F-2 診斷行）。
/// 診斷一律走真實路徑驗證——ChatJsonAsync 在 <see cref="BatchRunRecorder"/> 的作用域內執行，
/// 再從執行紀錄讀回以 <see cref="AiJsonDiagnostics.LoggerName"/> 寫入的那一行，
/// 而不是只比對字串或呼叫內部方法。
/// </summary>
public sealed class AIServiceJsonDiagnosticsTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly BatchRunStore _store;

    public AIServiceJsonDiagnosticsTests()
    {
        _store = new BatchRunStore(_fx.LogStore("runs"), _fx.LogStore("run_logs"));
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private sealed class Reply
    {
        public string Content { get; init; } = "";
        public string? FinishReason { get; init; } = "stop";
        public (int Prompt, int Completion)? Usage { get; init; } = (100, 20);
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        /// <summary>直接指定 finish_reason 的原始 JSON（測試非字串型別用）</summary>
        public string? RawFinishReasonJson { get; init; }
        /// <summary>伺服器另放的思考文字（message 裡的 reasoning_content／reasoning）</summary>
        public string? ReasoningContent { get; init; }
        /// <summary>思考欄位名稱：reasoning_content 或 reasoning</summary>
        public string ReasoningField { get; init; } = "reasoning_content";
        /// <summary>直接指定思考欄位的原始 JSON（測試非字串型別用，欄位名稱用 ReasoningField）</summary>
        public string? RawReasoningJson { get; init; }
    }

    private static HttpResponseMessage ToHttp(Reply r)
    {
        if (r.Status != HttpStatusCode.OK) return new HttpResponseMessage(r.Status);
        var choice = new StringBuilder("{\"message\":{\"role\":\"assistant\",\"content\":")
            .Append(JsonSerializer.Serialize(r.Content));
        if (r.RawReasoningJson != null) choice.Append(",\"").Append(r.ReasoningField).Append("\":").Append(r.RawReasoningJson);
        else if (r.ReasoningContent != null) choice.Append(",\"").Append(r.ReasoningField).Append("\":").Append(JsonSerializer.Serialize(r.ReasoningContent));
        choice.Append('}');
        if (r.RawFinishReasonJson != null) choice.Append(",\"finish_reason\":").Append(r.RawFinishReasonJson);
        else if (r.FinishReason != null) choice.Append(",\"finish_reason\":").Append(JsonSerializer.Serialize(r.FinishReason));
        choice.Append('}');
        var body = new StringBuilder("{\"choices\":[").Append(choice).Append(']');
        if (r.Usage is { } u)
            body.Append($",\"usage\":{{\"prompt_tokens\":{u.Prompt},\"completion_tokens\":{u.Completion},\"total_tokens\":{u.Prompt + u.Completion}}}");
        body.Append('}');
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
        };
    }

    private sealed class ScriptedHandler(Func<int, Reply> script) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        /// <summary>每次請求的本體（依序），供檢查有沒有帶 response_format、重試提示寫了什麼</summary>
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            var attempt = ++RequestCount;
            return ToHttp(script(attempt));
        }
    }

    private static AIService NewService(Func<int, Reply> script, int jsonRetryCount, out ScriptedHandler handler,
        int maxTokens = 2048)
    {
        handler = new ScriptedHandler(script);
        return new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", Model = "test-model", MaxTokens = maxTokens,
            RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = jsonRetryCount
        }, handler, NullAiRequestGate.Instance);
    }

    private static bool Validate(AiAnalysisResult r) =>
        r.RiskLevel.Length > 0 && r.Headline.Length > 0 && r.Story.Length > 0
        && r.Headline.Length <= 60 && r.Story.Length <= 600 && r.Action.Length <= 600;

    /// <summary>在 recorder 作用域內執行呼叫，回傳（執行紀錄裡的診斷行解析結果, 全部紀錄, run）。</summary>
    private async Task<(List<AiJsonDiagnosticsEntry> Diagnostics, List<BatchRunLog> Logs, BatchRun Run, AiJsonResult<AiAnalysisResult> Result)>
        RunAsync(AIService service, string label = "daily-20261101-h7", string prompt = "請分析")
    {
        long runId;
        AiJsonResult<AiAnalysisResult> result;
        using (var recorder = new BatchRunRecorder(_store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            result = await service.ChatJsonAsync<AiAnalysisResult>(prompt, validate: Validate, label: label, maxTokens: 777);
            recorder.Finish(0);
        }

        var logs = _store.GetLogs(runId);
        var diagnostics = logs.Where(l => l.Logger == AiJsonDiagnostics.LoggerName)
            .Select(l => AiJsonDiagnostics.TryRead(l.Message)
                         ?? throw new InvalidOperationException("診斷行不是合法的診斷 JSON：" + l.Message))
            .ToList();
        return (diagnostics, logs, _store.GetRun(runId)!, result);
    }

    private const string GoodJson =
        "{\"risk_level\":\"高\",\"headline\":\"標題\",\"story\":\"內容\",\"trend_story\":\"\",\"action\":\"處理\"}";

    // ── 失敗類別：每個類別各一個輸入 ───────────────────────────────

    [Fact]
    public async Task 空回覆_類別empty_帶出finish_reason與token()
    {
        var service = NewService(_ => new Reply { Content = "", FinishReason = "length", Usage = (321, 0) },
            jsonRetryCount: 2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(1, handler.RequestCount); // 空回覆不進 JSON 重試（既有行為）
        var entry = Assert.Single(diagnostics);
        Assert.Equal(AiJsonDiagnostics.EndedEmpty, entry.EndedBy);
        var attempt = Assert.Single(entry.AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryEmpty, attempt.Category);
        Assert.Equal("length", attempt.FinishReason);
        Assert.Equal(0, attempt.ResponseChars);
        Assert.Equal(321, attempt.PromptTokens);
        Assert.Equal(0, attempt.CompletionTokens);
        Assert.Equal("", attempt.Head);
        Assert.Null(attempt.Tail);
        Assert.Null(attempt.FieldLengths);
    }

    [Fact]
    public async Task 純文字_類別no_json()
    {
        var service = NewService(_ => new Reply { Content = "整體看來沒有問題，但這不是 JSON" }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryNoJson, attempt.Category);
        Assert.Equal("整體看來沒有問題，但這不是 JSON", attempt.Head);
    }

    [Fact]
    public async Task 截斷且finish_reason為length_類別truncated()
    {
        var service = NewService(
            _ => new Reply { Content = "{\"risk_level\":\"高\",\"headline\":\"標題\",\"story\":", FinishReason = "length" },
            0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryTruncated, attempt.Category);
        Assert.Equal("length", attempt.FinishReason);
    }

    [Fact]
    public async Task 括號未閉合但缺finish_reason_仍判truncated_且finish_reason輸出null()
    {
        var service = NewService(
            _ => new Reply { Content = "{\"risk_level\":\"高\",\"headline\":", FinishReason = null, Usage = null },
            0, out _);

        var (diagnostics, logs, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        // 補括號後 headline 缺值仍解析不出來（"headline": 後面沒有值）
        Assert.Equal(AiJsonDiagnostics.CategoryTruncated, attempt.Category);
        Assert.Null(attempt.FinishReason);
        Assert.Null(attempt.PromptTokens);
        Assert.Null(attempt.CompletionTokens);
        // 欄位輸出 null 而不是被省略（讀者才分得出「沒有」與「沒記」）
        Assert.Contains("\"finishReason\":null", logs.Single(l => l.Logger == AiJsonDiagnostics.LoggerName).Message);
        Assert.Contains("\"promptTokens\":null", logs.Single(l => l.Logger == AiJsonDiagnostics.LoggerName).Message);
    }

    [Fact]
    public async Task 語法壞掉但finish_reason為length_也判truncated()
    {
        var service = NewService(
            _ => new Reply { Content = "{\"risk_level\": }", FinishReason = "length" }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        Assert.Equal(AiJsonDiagnostics.CategoryTruncated, Assert.Single(Assert.Single(diagnostics).AttemptDetails).Category);
    }

    [Fact]
    public async Task action為陣列_類別type_mismatch_Path指到action()
    {
        var content = "{\"risk_level\":\"高\",\"headline\":\"標題\",\"story\":\"內容\",\"action\":[\"一\",\"二\"]}";
        var service = NewService(_ => new Reply { Content = content }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryTypeMismatch, attempt.Category);
        Assert.Equal("$.action", attempt.ParseErrorPath);
        Assert.Null(attempt.FieldLengths); // 解析不出物件，沒有各欄長度
    }

    [Fact]
    public async Task 前言加型別不符的物件_仍判type_mismatch()
    {
        var content = "以下是結果：{\"risk_level\":\"高\",\"headline\":\"標題\",\"story\":\"內容\",\"action\":{\"a\":1}}";
        var service = NewService(_ => new Reply { Content = content }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryTypeMismatch, attempt.Category);
        Assert.Equal("$.action", attempt.ParseErrorPath);
    }

    [Fact]
    public async Task 語法錯誤且已閉合_類別parse_other()
    {
        var service = NewService(_ => new Reply { Content = "{\"risk_level\":\"高\",\"headline\": }" }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryParseOther, attempt.Category);
        Assert.NotNull(attempt.ParseErrorPath);
    }

    [Fact]
    public async Task 合法但標題超長_類別validation_並記各欄字串長度()
    {
        var longHeadline = new string('標', 70);
        var content = $"{{\"risk_level\":\"高\",\"headline\":\"{longHeadline}\",\"story\":\"內容\",\"trend_story\":\"\",\"action\":\"處理\"}}";
        var service = NewService(_ => new Reply { Content = content }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryValidation, attempt.Category);
        Assert.Null(attempt.ParseErrorPath);
        var fields = Assert.IsType<Dictionary<string, object>>(attempt.FieldLengths);
        Assert.Equal(70, ((JsonElement)fields["headline"]).GetInt32());
        Assert.Equal(2, ((JsonElement)fields["story"]).GetInt32());
        Assert.Equal(0, ((JsonElement)fields["trend_story"]).GetInt32());
    }

    private sealed class Mixed
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    [Fact]
    public void 各欄長度_字串記長度_非字串記型別名_null記Null()
    {
        var fields = AiJsonDiagnostics.MeasureFields(new Mixed { Name = "甲乙丙", Tags = ["a"], Count = 3, Note = null });

        Assert.NotNull(fields);
        Assert.Equal(3, fields!["name"]);
        Assert.Equal("Array", fields["tags"]);
        Assert.Equal("Number", fields["count"]);
        Assert.Equal("Null", fields["note"]);
    }

    // ── 不寫的情形 ─────────────────────────────────────────────

    [Fact]
    public async Task 一次成功_不寫診斷()
    {
        var service = NewService(_ => new Reply { Content = GoodJson }, 2, out var handler);

        var (diagnostics, _, run, result) = await RunAsync(service);

        Assert.True(result.Success);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(diagnostics);
        Assert.Equal(0, run.WarnCount);
    }

    [Fact]
    public async Task 重試後成功_不寫診斷()
    {
        var service = NewService(a => new Reply { Content = a == 1 ? "不是 JSON" : GoodJson }, 2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.True(result.Success);
        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task 傳輸層失敗_不寫診斷()
    {
        var service = NewService(_ => new Reply { Status = HttpStatusCode.InternalServerError }, 2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task 先有內容層失敗_後來傳輸失敗_只寫已發生的內容層嘗試()
    {
        var service = NewService(
            a => a == 1 ? new Reply { Content = "不是 JSON" } : new Reply { Status = HttpStatusCode.InternalServerError },
            2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(2, handler.RequestCount);
        var entry = Assert.Single(diagnostics);
        Assert.Equal(AiJsonDiagnostics.EndedTransport, entry.EndedBy);
        Assert.Equal(2, entry.Attempts); // 第 2 次是傳輸層失敗，沒有內容層紀錄，但確實發出過
        Assert.Equal(AiJsonDiagnostics.CategoryNoJson, Assert.Single(entry.AttemptDetails).Category);
    }

    // ── 整體形狀 ───────────────────────────────────────────────

    [Fact]
    public async Task 診斷經真實路徑進recorder_Warn等級_單行_不計WarnCount_既有Warn照常()
    {
        var service = NewService(_ => new Reply { Content = "不是 JSON：高風險" }, 0, out _);

        var (diagnostics, logs, run, _) = await RunAsync(service, label: "daily-20261101-h7");

        var line = Assert.Single(logs, l => l.Logger == AiJsonDiagnostics.LoggerName);
        Assert.Equal("Warn", line.Level);
        Assert.DoesNotContain('\n', line.Message);
        Assert.DoesNotContain('\r', line.Message);
        Assert.Contains("不是 JSON：高風險", line.Message); // 非 ASCII 不跳脫
        Assert.DoesNotContain("\\u", line.Message);

        var entry = Assert.Single(diagnostics);
        Assert.Equal("daily-20261101-h7", entry.Label);
        Assert.Equal("AiAnalysisResult", entry.ResponseType);
        Assert.Equal("test-model", entry.Model);
        Assert.Equal(777, entry.MaxTokens);
        Assert.Equal(1, entry.Attempts);
        Assert.Equal(AiJsonDiagnostics.EndedExhausted, entry.EndedBy);

        // AIService 的 Warn：每次嘗試一筆（JSON 解析失敗）＋最終失敗一筆（第 63-2 輪 B-a-4 由 Error 改 Warn）；診斷行不加一筆
        Assert.Equal(2, run.WarnCount);
        Assert.Equal(2, logs.Count(l => l.Logger == "AIService" && l.Level == "Warn"));
        Assert.Equal(0, run.ErrorCount); // 格式異常是警告不是錯誤
    }

    [Fact]
    public async Task 重試中途一次empty一次validation_各記各自類別()
    {
        var longHeadline = new string('標', 70);
        var invalid = $"{{\"risk_level\":\"高\",\"headline\":\"{longHeadline}\",\"story\":\"內容\"}}";
        var service = NewService(a => a == 1 ? new Reply { Content = invalid } : new Reply { Content = "  " },
            2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(2, handler.RequestCount);
        var entry = Assert.Single(diagnostics);
        Assert.Equal(2, entry.Attempts);
        Assert.Equal(AiJsonDiagnostics.EndedEmpty, entry.EndedBy);
        Assert.Equal(
            new[] { AiJsonDiagnostics.CategoryValidation, AiJsonDiagnostics.CategoryEmpty },
            entry.AttemptDetails.Select(a => a.Category).ToArray());
        Assert.Equal(new[] { 1, 2 }, entry.AttemptDetails.Select(a => a.Attempt).ToArray());
    }

    [Fact]
    public async Task 每次嘗試的prompt字數反映重試附加的修正說明()
    {
        var service = NewService(_ => new Reply { Content = "不是 JSON", Usage = (500, 8) }, 1, out _);

        var (diagnostics, _, _, _) = await RunAsync(service, prompt: "原始提示詞");

        var entry = Assert.Single(diagnostics);
        Assert.Equal(2, entry.AttemptDetails.Count);
        Assert.Equal("原始提示詞".Length, entry.AttemptDetails[0].PromptChars);
        Assert.True(entry.AttemptDetails[1].PromptChars > entry.AttemptDetails[0].PromptChars);
        Assert.Equal(0, entry.AttemptDetails[0].SystemPromptChars);
        Assert.Equal("不是 JSON".Length, entry.AttemptDetails[0].ResponseChars);
        Assert.Equal(500, entry.AttemptDetails[0].PromptTokens);
        Assert.Equal(8, entry.AttemptDetails[0].CompletionTokens);
    }

    // ── 開頭／結尾截斷 ─────────────────────────────────────────

    [Theory]
    [InlineData(10, 10, false)]
    [InlineData(1500, 1500, false)]
    [InlineData(2000, 2000, false)]  // 剛好 2,000：只放開頭（全文），結尾為 null
    [InlineData(2001, 1500, true)]   // 超過一個字就切成開頭 1,500＋結尾 500
    [InlineData(5000, 1500, true)]
    public void 開頭結尾切法(int total, int expectedHead, bool hasTail)
    {
        var content = new string('a', total - 1) + "Z";

        var (head, tail) = AiJsonDiagnostics.SplitHeadTail(content);

        Assert.Equal(expectedHead, head.Length);
        if (hasTail)
        {
            Assert.NotNull(tail);
            Assert.Equal(500, tail!.Length);
            Assert.EndsWith("Z", tail);
            Assert.DoesNotContain("Z", head);
        }
        else
        {
            Assert.Null(tail);
            Assert.Equal(content, head); // 沒有結尾時開頭就是全文，不漏
        }
    }

    [Fact]
    public void 開頭結尾切點落在代理對中間_往內退一格_不產生不合法UTF16()
    {
        var emoji = "😀"; // 兩個 UTF-16 單元
        // 第 1,500 與 1,501 個單元是同一個代理對；尾端切點同理
        var content = new string('a', 1499) + emoji + new string('b', 1000) + emoji + new string('c', 499);

        var (head, tail) = AiJsonDiagnostics.SplitHeadTail(content);

        Assert.Equal(1499, head.Length);
        Assert.False(char.IsHighSurrogate(head[^1]));
        Assert.NotNull(tail);
        Assert.False(char.IsLowSurrogate(tail![0]));
        // 兩段都能無例外序列化
        _ = JsonSerializer.Serialize(new { head, tail });
    }

    [Fact]
    public async Task 長回覆經真實路徑_開頭1500結尾500()
    {
        var content = "前言" + new string('x', 3000) + "結尾";
        var service = NewService(_ => new Reply { Content = content }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(1500, attempt.Head.Length);
        Assert.StartsWith("前言", attempt.Head);
        Assert.Equal(500, attempt.Tail!.Length);
        Assert.EndsWith("結尾", attempt.Tail);
        Assert.Equal(content.Length, attempt.ResponseChars);
    }

    // ── F-1：AiResponse 帶出的呼叫資訊 ───────────────────────────

    [Fact]
    public async Task ChatAsync帶出finish_reason_字數_token()
    {
        var service = NewService(_ => new Reply { Content = "你好", FinishReason = "stop", Usage = (12, 3) }, 0, out _);

        var response = await service.ChatAsync("測試提示詞", systemPrompt: "系統");

        Assert.True(response.Success);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal("測試提示詞".Length, response.PromptChars);
        Assert.Equal("系統".Length, response.SystemPromptChars);
        Assert.Equal(2, response.ResponseChars);
        Assert.Equal(12, response.PromptTokens);
        Assert.Equal(3, response.CompletionTokens);
    }

    [Fact]
    public async Task ChatAsync沒有finish_reason與usage_欄位為null_成功判定不變()
    {
        var service = NewService(_ => new Reply { Content = "你好", FinishReason = null, Usage = null }, 0, out _);

        var response = await service.ChatAsync("測試");

        Assert.True(response.Success);
        Assert.Equal("你好", response.Content);
        Assert.Null(response.FinishReason);
        Assert.Null(response.PromptTokens);
        Assert.Null(response.CompletionTokens);
    }

    [Fact]
    public async Task finish_reason是非字串型別_不影響呼叫_回傳null()
    {
        var service = NewService(_ => new Reply { Content = "你好", RawFinishReasonJson = "7" }, 0, out _);

        var response = await service.ChatAsync("測試");

        Assert.True(response.Success);
        Assert.Equal("你好", response.Content);
        Assert.Null(response.FinishReason);
    }

    [Fact]
    public async Task 傳輸層失敗的AiResponse_不帶回應資訊_只有送出的字數()
    {
        var service = NewService(_ => new Reply { Status = HttpStatusCode.BadGateway }, 0, out _);

        var response = await service.ChatAsync("測試");

        Assert.False(response.Success);
        Assert.Null(response.FinishReason);
        Assert.Null(response.ResponseChars);
        Assert.Null(response.PromptTokens);
        Assert.Equal(2, response.PromptChars);
    }

    // ── 寫診斷本身不得讓呼叫失敗 ─────────────────────────────────

    private sealed class Cyclic { public object? Self { get; set; } }

    [Fact]
    public async Task 診斷序列化失敗_不擲例外_不寫診斷行()
    {
        var cyclic = new Cyclic();
        cyclic.Self = cyclic;
        var attempt = new AiJsonAttemptDiagnostics
        {
            Attempt = 1, Category = AiJsonDiagnostics.CategoryNoJson, Head = "x",
            FieldLengths = new Dictionary<string, object> { ["bad"] = cyclic }
        };

        long runId;
        using (var recorder = new BatchRunRecorder(_store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            var ex = Record.Exception(() => AiJsonDiagnostics.Write("label", "T", "m", 1, 1, AiJsonDiagnostics.EndedExhausted, [attempt]));
            Assert.Null(ex);
            recorder.Finish(0);
        }
        await Task.CompletedTask;

        var logs = _store.GetLogs(runId);
        Assert.DoesNotContain(logs, l => l.Logger == AiJsonDiagnostics.LoggerName);
        // 失敗只記一般 Warn
        var general = Assert.Single(logs, l => l.Level == "Warn" && l.Message.Contains("診斷寫入失敗"));
        Assert.NotEqual(AiJsonDiagnostics.LoggerName, general.Logger);
    }

    [Fact]
    public void 沒有任何內容層嘗試_Write什麼都不寫()
    {
        long runId;
        using (var recorder = new BatchRunRecorder(_store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            AiJsonDiagnostics.Write("label", "T", "m", 1, 1, AiJsonDiagnostics.EndedTransport, []);
            recorder.Finish(0);
        }

        Assert.Empty(_store.GetLogs(runId));
    }

    // ── TryParse：舊方法結果與候選順序不變 ───────────────────────

    private static string Obj(string risk) => $"{{\"risk_level\":\"{risk}\",\"headline\":\"h\",\"story\":\"s\"}}";

    public static IEnumerable<object?[]> TryParseCases()
    {
        // (輸入, 期望的 risk_level；null＝解析失敗)
        yield return new object?[] { Obj("高"), "高" };
        yield return new object?[] { "```json\n" + Obj("中") + "\n```", "中" };
        yield return new object?[] { "前言文字 " + Obj("低") + " 結尾", "低" };
        // 多個物件：最後一個配對完整的優先（思考在前、答案在後；第 63-2 輪 B-a-3 由「第一個」改）
        yield return new object?[] { Obj("甲") + " 然後 " + Obj("乙"), "乙" };
        yield return new object?[] { "{\"note\":\"x\"} " + Obj("丙"), "丙" }; // 前面的全空欄位物件不再蓋掉真正的答案
        // 陣列：包著的物件取最後一個
        yield return new object?[] { "[" + Obj("丁") + "," + Obj("戊") + "]", "戊" };
        // 截斷：補括號後解析得出
        yield return new object?[] { "{\"risk_level\":\"己\",\"headline\":\"h\",\"story\":\"寫到一半", "己" };
        // 解析失敗
        yield return new object?[] { "完全不是 JSON", null };
        yield return new object?[] { "", null };
        yield return new object?[] { "null", null };
        yield return new object?[] { "{\"risk_level\":\"高\",\"action\":[\"a\"]}", null };
        yield return new object?[] { "{\"risk_level\": }", null };
    }

    [Theory]
    [InlineData("{\"note\":\"x\"} 之後 {0}")]
    [InlineData("{0} 尾端多一個 {\"note\":\"x\"}")]
    public void TryParseDetailed_選第一個通過內容檢查的候選_思考在前或答案在前都選得對(string template)
    {
        var raw = template.Replace("{0}", Obj("高"));

        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>(raw, r => r.Headline.Length > 0);

        Assert.Equal("高", detailed.Value?.RiskLevel);
    }

    [Theory]
    [InlineData("{\"note\":\"x\"} 之後 {0}")]
    [InlineData("好的，結果如下：{0}\n補充 {\"note\":\"x\"}")]
    public void TryParseDetailed_沒有內容檢查_答案在前或在後都選最長的(string template)
    {
        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>(template.Replace("{0}", Obj("高")));

        Assert.Equal("高", detailed.Value?.RiskLevel);
    }

    [Fact]
    public void TryParseDetailed_都沒通過內容檢查_回第一個解析得出的讓呼叫端走內容檢查失敗()
    {
        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>("{\"note\":\"x\"}", r => r.Headline.Length > 0);

        Assert.NotNull(detailed.Value);
        Assert.Equal("", detailed.Value!.Headline);
    }

    [Theory]
    [MemberData(nameof(TryParseCases))]
    public void TryParse舊方法結果不變_且與Detailed版Value一致(string raw, string? expectedRisk)
    {
        var legacy = AiJson.TryParse<AiAnalysisResult>(raw);
        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>(raw);

        Assert.Equal(expectedRisk, legacy?.RiskLevel);
        Assert.Equal(legacy?.RiskLevel, detailed.Value?.RiskLevel);
        Assert.Equal(legacy == null, detailed.Value == null);
        if (detailed.Value != null)
        {
            Assert.Null(detailed.LastException);
            Assert.Null(detailed.LastSyntaxValidException);
        }
    }

    [Fact]
    public void TryParseDetailed_型別不符時帶出語法合法候選的例外與Path()
    {
        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>("{\"risk_level\":\"高\",\"action\":[\"a\"]}");

        Assert.Null(detailed.Value);
        Assert.NotNull(detailed.LastSyntaxValidException);
        Assert.Equal("$.action", detailed.LastSyntaxValidException!.Path);
        Assert.NotNull(detailed.LastException);
    }

    [Fact]
    public void TryParseDetailed_語法壞掉時沒有語法合法候選()
    {
        var detailed = AiJson.TryParseDetailed<AiAnalysisResult>("{\"risk_level\": }");

        Assert.Null(detailed.Value);
        Assert.NotNull(detailed.LastException);
        Assert.Null(detailed.LastSyntaxValidException);
    }

    [Theory]
    [InlineData("{\"a\":1", true)]
    [InlineData("{\"a\":\"未結束的字串", true)]
    [InlineData("前言 {\"a\":[1,2", true)]
    [InlineData("{\"a\":1}", false)]
    [InlineData("沒有大括號", false)]
    [InlineData("", false)]
    [InlineData("[", true)]
    [InlineData("[{\"a\":1}", true)]
    [InlineData("[1,2", true)]
    [InlineData("[]", false)]
    [InlineData("[\n]", false)]
    public void HasUnclosedBrackets(string raw, bool expected) =>
        Assert.Equal(expected, AiJson.HasUnclosedBrackets(raw));

    // ── 類別判定的優先序（純函式）────────────────────────────────

    [Theory]
    [InlineData("", "stop", false, false, "empty")]
    [InlineData("   ", "length", true, false, "empty")]            // 空白優先於一切
    [InlineData("{\"a\":1}", "length", true, false, "validation")] // 解析得出＝內容檢查沒過，與 finish_reason 無關
    [InlineData("純文字 length", "length", false, false, "no_json")] // 沒有 { 一律 no_json
    [InlineData("[1,2,3]", null, false, true, "no_json")]
    [InlineData("{\"a\":", null, false, false, "truncated")]
    [InlineData("{\"a\":}", "length", false, false, "truncated")]
    [InlineData("{\"a\":}", "stop", false, false, "parse_other")]
    [InlineData("{\"a\":1}", "stop", false, true, "type_mismatch")]
    [InlineData("{\"a\":1}", "LENGTH", false, true, "truncated")]  // truncated 優先於 type_mismatch（清單順序）
    public void 類別判定優先序(string content, string? finish, bool parsed, bool bindFailed, string expected) =>
        Assert.Equal(expected, AiJsonDiagnostics.Classify(content, finish, parsed, bindFailed));


    // ══ 第 63-2 輪批次 B-a：證據、空形狀、無文法重試、候選順序、等級 ═════════════════

    /// <summary>取出某次請求本體裡 user 訊息的內容（重試提示附在這裡）</summary>
    private static string UserPromptOf(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
    }

    private static bool HasResponseFormat(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.TryGetProperty("response_format", out _);
    }

    // ── B-a-1：證據 ────────────────────────────────────────────

    [Fact]
    public async Task 請求本體不含reasoning_content鍵_回應型別與請求型別分開()
    {
        var service = NewService(_ => new Reply { Content = "你好", ReasoningContent = "先想一想" }, 0, out var handler);

        var response = await service.ChatAsync("測試提示詞", systemPrompt: "系統");

        Assert.True(response.Success);
        var body = Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain("reasoning", body);
        using var doc = JsonDocument.Parse(body);
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            Assert.Equal(new[] { "content", "role" }, message.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public async Task 回應含思考欄位_診斷記長度不記內容(string fieldName)
    {
        var thinking = new string('想', 321);
        var service = NewService(_ => new Reply { Content = "不是 JSON", ReasoningContent = thinking, ReasoningField = fieldName },
            0, out _);

        var (diagnostics, logs, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(321, attempt.ReasoningChars);
        Assert.DoesNotContain("想想想想", logs.Single(l => l.Logger == AiJsonDiagnostics.LoggerName).Message); // 只記長度
    }

    [Fact]
    public async Task 回應沒有思考欄位_reasoningChars為null_ChatAsync帶出長度()
    {
        var service = NewService(_ => new Reply { Content = "你好" }, 0, out _);
        Assert.Null((await service.ChatAsync("測試")).ReasoningChars);

        var withThinking = NewService(_ => new Reply { Content = "你好", ReasoningContent = "ab" }, 0, out _);
        Assert.Equal(2, (await withThinking.ChatAsync("測試")).ReasoningChars);
    }

    [Fact]
    public async Task 思考欄位是非字串型別_不影響呼叫()
    {
        var service = NewService(_ => new Reply { Content = "你好", RawReasoningJson = "{\"a\":1}" }, 0, out _);

        var response = await service.ChatAsync("測試");

        Assert.True(response.Success);
        Assert.Null(response.ReasoningChars);
    }

    [Fact]
    public void 舊格式診斷行TryRead成功_新欄位為null()
    {
        const string oldLine = "{\"label\":\"daily-20261101-h7\",\"responseType\":\"AiAnalysisResult\",\"model\":\"m\",\"maxTokens\":2048,"
            + "\"attempts\":1,\"endedBy\":\"exhausted\",\"attemptDetails\":[{\"attempt\":1,\"category\":\"no_json\",\"finishReason\":\"stop\","
            + "\"promptChars\":10,\"systemPromptChars\":0,\"responseChars\":3,\"promptTokens\":5,\"completionTokens\":2,"
            + "\"parseErrorPath\":null,\"fieldLengths\":null,\"head\":\"abc\",\"tail\":null}]}";

        var entry = AiJsonDiagnostics.TryRead(oldLine);

        Assert.NotNull(entry);
        var attempt = Assert.Single(entry!.AttemptDetails);
        Assert.Equal("no_json", attempt.Category);
        Assert.Equal("abc", attempt.Head);
        Assert.Null(attempt.EmptyShape);
        Assert.Null(attempt.RawChars);
        Assert.Null(attempt.RawHead);
        Assert.Null(attempt.RawTail);
        Assert.Null(attempt.ReasoningChars);
    }

    [Fact]
    public async Task 清洗前原文的長度與頭尾_經真實路徑記進診斷()
    {
        // 清洗會剝掉 channel 標記與思考段，只留 final 段；原文頭尾要留在診斷裡
        var raw = "<|channel>thought\n" + new string('思', 3000) + "<|channel>final<|message>這不是 JSON 結尾";
        var service = NewService(_ => new Reply { Content = raw }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal("這不是 JSON 結尾", attempt.Head); // 清洗後
        Assert.Equal(raw.Length, attempt.RawChars);
        Assert.Equal(1500, attempt.RawHead!.Length);
        Assert.StartsWith("<|channel>thought", attempt.RawHead);
        Assert.Equal(500, attempt.RawTail!.Length);
        Assert.EndsWith("這不是 JSON 結尾", attempt.RawTail);
    }

    [Fact]
    public async Task 原文與清洗後相同_不重複記原文頭尾()
    {
        var service = NewService(_ => new Reply { Content = "不是 JSON" }, 0, out _);

        var (diagnostics, _, _, _) = await RunAsync(service);

        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal("不是 JSON", attempt.Head);
        Assert.Null(attempt.RawChars);
        Assert.Null(attempt.RawHead);
        Assert.Null(attempt.RawTail);
    }

    [Fact]
    public async Task 整段皆思考清洗後為空_診斷仍有原文頭與思考長度()
    {
        var service = NewService(_ => new Reply { Content = "<|channel>thought\n只有思考沒有答案", ReasoningContent = "xyz" },
            1, out var handler);

        var (diagnostics, _, _, _) = await RunAsync(service);

        Assert.Equal(1, handler.RequestCount); // 空回覆不進 JSON 重試（既有行為）
        var attempt = Assert.Single(Assert.Single(diagnostics).AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryEmpty, attempt.Category);
        Assert.Equal("", attempt.Head);
        Assert.Equal("<|channel>thought\n只有思考沒有答案".Length, attempt.RawChars);
        Assert.Equal("<|channel>thought\n只有思考沒有答案", attempt.RawHead);
        Assert.Equal(3, attempt.ReasoningChars);
    }

    // ── B-a-2：空形狀、無文法重試 ────────────────────────────────

    [Theory]
    [InlineData("[\n]", "array")]
    [InlineData("[]", "array")]
    [InlineData("  [ ]  ", "array")]
    [InlineData("{}", "object")]
    [InlineData("{\n}", "object")]
    [InlineData("[{}]", "array")]
    public async Task 空形狀_類別empty_帶emptyShape_不進validate(string content, string shape)
    {
        var validated = 0;
        var service = NewService(_ => new Reply { Content = content }, 0, out _);
        long runId;
        AiJsonResult<AiAnalysisResult> result;
        using (var recorder = new BatchRunRecorder(_store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            result = await service.ChatJsonAsync<AiAnalysisResult>("請分析", validate: r => { validated++; return Validate(r); }, label: "daily-20261101-h7");
            recorder.Finish(0);
        }

        Assert.False(result.Success);
        Assert.Equal(0, validated); // {} 能解析成全空物件，但不得走到 validate
        var entry = AiJsonDiagnostics.TryRead(_store.GetLogs(runId).Single(l => l.Logger == AiJsonDiagnostics.LoggerName).Message)!;
        var attempt = Assert.Single(entry.AttemptDetails);
        Assert.Equal(AiJsonDiagnostics.CategoryEmpty, attempt.Category);
        Assert.Equal(shape, attempt.EmptyShape);
        Assert.Equal(AiJsonDiagnostics.EndedExhausted, entry.EndedBy); // 內容層失敗，不是 Polly 層的「空回覆」
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[")]
    [InlineData("[{]")]
    [InlineData("[1]")]
    [InlineData("[{\"a\":1}]")]
    public void 非空形狀_DetectEmptyShape回null(string content) =>
        Assert.Null(AiJsonDiagnostics.DetectEmptyShape(content));

    [Fact]
    public async Task 空形狀不是Polly層的空回覆_會真的打第二次_且第二次不帶文法()
    {
        var service = NewService(a => a == 1 ? new Reply { Content = "[\n]" } : new Reply { Content = GoodJson },
            2, out var handler);

        var (_, _, _, result) = await RunAsync(service);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.RequestCount);
        Assert.True(HasResponseFormat(handler.RequestBodies[0]));
        Assert.False(HasResponseFormat(handler.RequestBodies[1]));
    }

    [Fact]
    public async Task 空形狀連續失敗_第二次起都不帶文法()
    {
        var service = NewService(_ => new Reply { Content = "{}" }, 2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(new[] { true, false, false }, handler.RequestBodies.Select(HasResponseFormat).ToArray());
        Assert.Equal(new[] { "object", "object", "object" }, Assert.Single(diagnostics).AttemptDetails.Select(a => a.EmptyShape).ToArray());
    }

    [Fact]
    public async Task no_json之後也關文法()
    {
        var service = NewService(a => a == 1 ? new Reply { Content = "我先想一下，這不是 JSON" } : new Reply { Content = GoodJson },
            1, out var handler);

        var (_, _, _, result) = await RunAsync(service);

        Assert.True(result.Success);
        Assert.True(HasResponseFormat(handler.RequestBodies[0]));
        Assert.False(HasResponseFormat(handler.RequestBodies[1]));
    }

    [Fact]
    public async Task validation失敗後第二次仍帶文法()
    {
        var longHeadline = new string('標', 70);
        var invalid = $"{{\"risk_level\":\"高\",\"headline\":\"{longHeadline}\",\"story\":\"內容\"}}";
        var service = NewService(_ => new Reply { Content = invalid }, 2, out var handler);

        var (diagnostics, _, _, result) = await RunAsync(service);

        Assert.False(result.Success);
        Assert.Equal(3, handler.RequestCount);
        Assert.All(handler.RequestBodies, body => Assert.True(HasResponseFormat(body)));
        Assert.All(Assert.Single(diagnostics).AttemptDetails, a => Assert.Equal(AiJsonDiagnostics.CategoryValidation, a.Category));
    }

    [Theory]
    [InlineData("{\"risk_level\":\"高\",\"action\":[\"a\"]}")] // type_mismatch
    [InlineData("{\"risk_level\":\"高\",\"headline\":")]        // truncated
    [InlineData("{\"risk_level\":\"高\",\"headline\": }")]      // parse_other
    public async Task 其他類別失敗後第二次仍帶文法(string content)
    {
        var service = NewService(_ => new Reply { Content = content }, 1, out var handler);

        await RunAsync(service);

        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.RequestBodies, body => Assert.True(HasResponseFormat(body)));
    }

    [Fact]
    public async Task 重試提示尾句_有文法的上次類別是嚴格輸出JSON()
    {
        var longHeadline = new string('標', 70);
        var invalid = $"{{\"risk_level\":\"高\",\"headline\":\"{longHeadline}\",\"story\":\"內容\"}}";
        var service = NewService(_ => new Reply { Content = invalid }, 1, out var handler);

        await RunAsync(service, prompt: "原始提示詞");

        Assert.Equal("原始提示詞", UserPromptOf(handler.RequestBodies[0]));
        var retry = UserPromptOf(handler.RequestBodies[1]);
        Assert.EndsWith(AIService.RetryClosingWithGrammar, retry);
        Assert.DoesNotContain(AIService.RetryClosingWithoutGrammar, retry);
        Assert.Contains("嚴格依要求輸出合法的 JSON", retry);
    }

    [Fact]
    public async Task 重試提示尾句_無文法的上次類別是可以先思考最後只輸出一個JSON物件()
    {
        var service = NewService(_ => new Reply { Content = "[\n]" }, 1, out var handler);

        await RunAsync(service, prompt: "原始提示詞");

        var retry = UserPromptOf(handler.RequestBodies[1]);
        Assert.EndsWith(AIService.RetryClosingWithoutGrammar, retry);
        Assert.DoesNotContain(AIService.RetryClosingWithGrammar, retry);
        Assert.Contains("先簡短思考", retry);
        Assert.Contains("只輸出一個 JSON 物件", retry);
    }

    [Fact]
    public async Task 空形狀之後的無文法重試回空內容_帶前次原文走降級_不繞過次數上限()
    {
        var service = NewService(n => new Reply { Content = n == 1 ? "[]" : "" }, 2, out var handler);

        var result = await service.ChatJsonAsync<AiAnalysisResult>("p", validate: Validate);

        Assert.False(result.Success);
        Assert.Equal("[]", result.RawContent);   // 呼叫端靠非空的原文判定成降級，次數才會增加
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task 第一次就回空內容_維持完全失敗語意()
    {
        var service = NewService(_ => new Reply { Content = "" }, 2, out _);

        var result = await service.ChatJsonAsync<AiAnalysisResult>("p", validate: Validate);

        Assert.False(result.Success);
        Assert.Equal("", result.RawContent);
    }

    [Fact]
    public async Task 文法關掉後就不再打開_內容檢查沒過也一樣()
    {
        var service = NewService(n => new Reply { Content = n == 1 ? "[]" : "{\"headline\":\"\"}" }, 2, out var handler);

        await service.ChatJsonAsync<AiAnalysisResult>("p", validate: Validate);

        Assert.Equal(new[] { true, false, false }, handler.RequestBodies.Select(HasResponseFormat));
    }

    [Theory]
    [InlineData("empty", true)]
    [InlineData("no_json", true)]
    [InlineData("validation", false)]
    [InlineData("truncated", false)]
    [InlineData("type_mismatch", false)]
    [InlineData("parse_other", false)]
    [InlineData(null, false)]
    public void 哪些類別之後關文法(string? category, bool expected) =>
        Assert.Equal(expected, AIService.RetryWithoutGrammar(category));

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("{\"a\":1}", null)]
    [InlineData("[]", "array")]
    [InlineData("{}", "object")]
    [InlineData("]", null)]
    [InlineData("[}", null)]
    public void DetectEmptyShape(string content, string? expected) =>
        Assert.Equal(expected, AiJsonDiagnostics.DetectEmptyShape(content));

    // ── B-a-2：孤立代理字元 ─────────────────────────────────────

    [Fact]
    public void 孤立代理字元_TryParseDetailed不擲例外_回失敗結果()
    {
        // 不用 InlineData：xunit 對含孤立代理字元的參數無法產生穩定的測試案例識別
        foreach (var raw in new[] { "垃圾\uD800文字 {x} 壞", "垃圾\uDC00文字 {x} 壞", "\uD800" })
        {
            var outcome = AiJson.TryParseDetailed<AiAnalysisResult>(raw);
            Assert.Null(outcome.Value); // 沒有合格物件：失敗結果，不是 ArgumentException
        }
    }

    [Fact]
    public void 孤立代理字元在截斷的JSON裡_不擲例外_補括號後照常解析()
    {
        var outcome = AiJson.TryParseDetailed<AiAnalysisResult>("{\"risk_level\":\"\uD800\"");

        Assert.Equal("\uFFFD", outcome.Value!.RiskLevel);
    }

    [Fact]
    public void 孤立代理字元換成FFFD_成對的保留()
    {
        Assert.Equal("a\uFFFDb", AiJson.ReplaceLoneSurrogates("a\uD800b"));
        Assert.Equal("a\uFFFDb", AiJson.ReplaceLoneSurrogates("a\uDC00b"));
        Assert.Equal("a\uFFFD", AiJson.ReplaceLoneSurrogates("a\uD800"));
        Assert.Equal("😀x", AiJson.ReplaceLoneSurrogates("😀x"));
        const string clean = "沒有問題";
        Assert.Same(clean, AiJson.ReplaceLoneSurrogates(clean));
    }

    [Fact]
    public void 孤立代理字元在可解析的JSON裡_換成FFFD後仍解析成功()
    {
        var outcome = AiJson.TryParseDetailed<AiAnalysisResult>("{\"risk_level\":\"高\",\"headline\":\"a\uD800b\",\"story\":\"s\"}");

        Assert.NotNull(outcome.Value);
        Assert.Equal("a\uFFFDb", outcome.Value!.Headline);
    }

    [Fact]
    public void 孤立代理字元的回覆_診斷嘗試紀錄不擲例外_可序列化_類別parse_other()
    {
        var content = "垃圾\uD800文字 {x} 壞";
        var response = new AiResponse { Success = true, Content = content, FinishReason = "stop" };
        var outcome = AiJson.TryParseDetailed<AiAnalysisResult>(content);

        var attempt = AiJsonDiagnostics.BuildAttempt<AiAnalysisResult>(1, response, content, outcome);

        Assert.NotNull(attempt);
        Assert.Equal(AiJsonDiagnostics.CategoryParseOther, attempt!.Category);
        Assert.Contains('\uFFFD', attempt.Head);
        Assert.DoesNotContain('\uD800', attempt.Head);
        _ = JsonSerializer.Serialize(attempt); // 不得因孤立代理字元而擲例外
    }

    [Fact]
    public async Task 信封含孤立代理字元跳脫_ChatJsonAsync回失敗結果_不擲例外()
    {
        var handler = new RawBodyHandler("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"壞\\ud800字\"},\"finish_reason\":\"stop\"}]}");
        var service = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", Model = "m", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 1
        }, handler, NullAiRequestGate.Instance);

        var result = await service.ChatJsonAsync<AiAnalysisResult>("請分析", validate: Validate);

        Assert.False(result.Success);
    }

    private sealed class RawBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    // ── B-a-3：候選順序 ─────────────────────────────────────────

    [Fact]
    public void 思考文字含空物件加正確JSON_取正確物件()
    {
        var raw = "輸出格式是 {} 這樣，先確認一下欄位。\n" + GoodJson;

        var parsed = AiJson.TryParse<AiAnalysisResult>(raw);

        Assert.NotNull(parsed);
        Assert.Equal("高", parsed!.RiskLevel);
        Assert.Equal("標題", parsed.Headline);
    }

    [Fact]
    public void 思考文字含奇數個引號加正確JSON_解析成功()
    {
        var raw = "我想說 \"好吧，答案如下：\n" + GoodJson;

        var parsed = AiJson.TryParse<AiAnalysisResult>(raw);

        Assert.NotNull(parsed);
        Assert.Equal("高", parsed!.RiskLevel);
        Assert.Equal("內容", parsed.Story);
    }

    [Fact]
    public void 思考文字同時有空物件與奇數個引號_取最後的正確JSON()
    {
        var raw = "{} 他說 \"好吧\n" + GoodJson;

        var parsed = AiJson.TryParse<AiAnalysisResult>(raw);

        Assert.NotNull(parsed);
        Assert.Equal("高", parsed!.RiskLevel);
    }

    [Fact]
    public void 思考文字的奇數引號後有其他大括號_仍取最後的正確JSON()
    {
        var raw = "他說 \"好吧 {x} 然後 \n" + GoodJson;

        var parsed = AiJson.TryParse<AiAnalysisResult>(raw);

        Assert.NotNull(parsed);
        Assert.Equal("高", parsed!.RiskLevel);
    }

    [Fact]
    public void 截斷物件的內層小物件不當成答案()
    {
        // 補括號後仍型別不符；內層的 {"a":1} 能解析成全空物件，但不是答案，結果必須維持失敗
        var parsed = AiJson.TryParse<AiAnalysisResult>("{\"risk_level\":{\"a\":1},\"story\":\"寫到一半");

        Assert.Null(parsed);
    }

    [Fact]
    public void 多個完整物件_最後一個優先_其餘維持原順序()
    {
        var first = new List<string>();
        foreach (var candidate in new[] { Obj("甲"), Obj("乙"), Obj("丙") })
        {
            first.Add(candidate);
        }
        var parsed = AiJson.TryParse<AiAnalysisResult>(string.Join(" 然後 ", first));

        Assert.Equal("丙", parsed!.RiskLevel);
    }

    [Fact]
    public void 診斷logger名稱不含句點_短名稱與完整名稱一致()
    {
        Assert.Equal("AiJsonDiagnostics", AiJsonDiagnostics.LoggerName);
        Assert.DoesNotContain('.', AiJsonDiagnostics.LoggerName);
    }
}
