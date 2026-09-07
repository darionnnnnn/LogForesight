using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public class BranchOutcomeAndCacheTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private BatchRunStore Runs() => new(_fx.LogStore("runs"), _fx.LogStore("run_logs"));
    private EfAnalysisRecordStore Records() => new(_fx.NewContext, "test");
    private ScheduleOptionsStore Options() => new(_fx.Blob("schedule_options"));

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly UserDisplayNameService _displayNames = new(new FakeSystemSettingsStore());
    private RunMonitorService CreateService() => new(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

    [Fact]
    public void GetRunList與GetDetail_完整分路欄位與舊格式皆正確帶出()
    {
        var runs = Runs();
        var fullRun = new BatchRun
        {
            HostName = "HOST-FULL",
            StartedAt = DateTime.Now.AddMinutes(-30),
            FinishedAt = DateTime.Now.AddMinutes(-20),
            ExitCode = 0,
            DaysAnalyzed = 10,
            LocalDaysAnalyzed = 5,
            LocalDaysFailed = 1,
            NetiqDaysAnalyzed = 8,
            NetiqDaysFailed = 0,
            NetiqHostsSkipped = 2,
            PrtgOutcome = BatchRun.PrtgOutcomeSuccess,
            PrtgSensorsFetched = 12,
            PrtgSensorsFailed = 0,
            PrtgTriggeredHosts = 3
        };
        var legacyRun = new BatchRun
        {
            HostName = "HOST-LEGACY",
            StartedAt = DateTime.Now.AddMinutes(-15),
            FinishedAt = DateTime.Now.AddMinutes(-10),
            ExitCode = 0,
            DaysAnalyzed = 3,
            LocalDaysAnalyzed = null,
            LocalDaysFailed = null,
            NetiqDaysAnalyzed = null,
            NetiqDaysFailed = null,
            NetiqHostsSkipped = null,
            PrtgOutcome = null,
            PrtgSensorsFetched = null,
            PrtgSensorsFailed = null,
            PrtgTriggeredHosts = null
        };

        var fullId = runs.StartRun(fullRun);
        fullRun.RunId = fullId;
        runs.FinishRun(fullRun);
        var legacyId = runs.StartRun(legacyRun);
        legacyRun.RunId = legacyId;
        runs.FinishRun(legacyRun);

        var service = CreateService();
        var list = service.GetRunList(7);

        var fullItem = Assert.Single(list, r => r.RunId == fullId);
        Assert.Equal(5, fullItem.LocalDaysAnalyzed);
        Assert.Equal(1, fullItem.LocalDaysFailed);
        Assert.Equal(8, fullItem.NetiqDaysAnalyzed);
        Assert.Equal(0, fullItem.NetiqDaysFailed);
        Assert.Equal(2, fullItem.NetiqHostsSkipped);
        Assert.Equal(BatchRun.PrtgOutcomeSuccess, fullItem.PrtgOutcome);
        Assert.Equal(12, fullItem.PrtgSensorsFetched);
        Assert.Equal(0, fullItem.PrtgSensorsFailed);
        Assert.Equal(3, fullItem.PrtgTriggeredHosts);

        var legacyItem = Assert.Single(list, r => r.RunId == legacyId);
        Assert.Null(legacyItem.LocalDaysAnalyzed);
        Assert.Null(legacyItem.LocalDaysFailed);
        Assert.Null(legacyItem.NetiqDaysAnalyzed);
        Assert.Null(legacyItem.NetiqDaysFailed);
        Assert.Null(legacyItem.NetiqHostsSkipped);
        Assert.Null(legacyItem.PrtgOutcome);
        Assert.Null(legacyItem.PrtgSensorsFetched);
        Assert.Null(legacyItem.PrtgSensorsFailed);
        Assert.Null(legacyItem.PrtgTriggeredHosts);

        // 驗證 GetDetail
        var fullDetail = service.GetDetail(fullId);
        Assert.Equal(5, fullDetail.LocalDaysAnalyzed);
        Assert.Equal(1, fullDetail.LocalDaysFailed);
        Assert.Equal(8, fullDetail.NetiqDaysAnalyzed);
        Assert.Equal(0, fullDetail.NetiqDaysFailed);
        Assert.Equal(2, fullDetail.NetiqHostsSkipped);
        Assert.Equal(BatchRun.PrtgOutcomeSuccess, fullDetail.PrtgOutcome);
        Assert.Equal(12, fullDetail.PrtgSensorsFetched);
        Assert.Equal(0, fullDetail.PrtgSensorsFailed);
        Assert.Equal(3, fullDetail.PrtgTriggeredHosts);

        var legacyDetail = service.GetDetail(legacyId);
        Assert.Null(legacyDetail.LocalDaysAnalyzed);
        Assert.Null(legacyDetail.LocalDaysFailed);
        Assert.Null(legacyDetail.NetiqDaysAnalyzed);
        Assert.Null(legacyDetail.NetiqDaysFailed);
        Assert.Null(legacyDetail.NetiqHostsSkipped);
        Assert.Null(legacyDetail.PrtgOutcome);
        Assert.Null(legacyDetail.PrtgSensorsFetched);
        Assert.Null(legacyDetail.PrtgSensorsFailed);
        Assert.Null(legacyDetail.PrtgTriggeredHosts);
    }

    [Fact]
    public void GetDaySummaries_每日彙總取最後一筆取數執行的PrtgOutcome_且AI執行不影響結果()
    {
        var runs = Runs();
        var baseDate = DateTime.Today;

        // 同一天兩筆取數執行（前者 success、後者 partial）
        var run1 = new BatchRun
        {
            HostName = "FETCH-1",
            StartedAt = baseDate.AddHours(2),
            FinishedAt = baseDate.AddHours(2).AddMinutes(10),
            ExitCode = 0,
            PrtgOutcome = BatchRun.PrtgOutcomeSuccess,
            JobType = null
        };
        var run2 = new BatchRun
        {
            HostName = "FETCH-2",
            StartedAt = baseDate.AddHours(4),
            FinishedAt = baseDate.AddHours(4).AddMinutes(10),
            ExitCode = 0,
            PrtgOutcome = BatchRun.PrtgOutcomePartial,
            JobType = null
        };
        // 同一天另有一筆 JobType=ai 的執行（PrtgOutcome 為 failed）
        var runAi = new BatchRun
        {
            HostName = "AI-RUN",
            StartedAt = baseDate.AddHours(6),
            FinishedAt = baseDate.AddHours(6).AddMinutes(10),
            ExitCode = 1,
            PrtgOutcome = BatchRun.PrtgOutcomeFailed,
            JobType = BatchRun.JobTypeAi
        };

        runs.StartRun(run1);
        runs.StartRun(run2);
        runs.StartRun(runAi);

        var service = CreateService();
        var summaries = service.GetDaySummaries(1, page: 1, pageSize: 30);

        var todaySummary = Assert.Single(summaries, s => s.Date == baseDate.ToString("yyyy-MM-dd"));
        // 應取後者取數執行的 partial，AI 執行的 failed 不得影響
        Assert.Equal(BatchRun.PrtgOutcomePartial, todaySummary.PrtgOutcome);
    }

    [Fact]
    public void GetDaySummaries_當日無取數執行時PrtgOutcome為null()
    {
        var service = CreateService();
        var summaries = service.GetDaySummaries(1, page: 1, pageSize: 30);

        var todaySummary = Assert.Single(summaries, s => s.Date == DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.Null(todaySummary.PrtgOutcome);
    }

    private class CountingAnalysisRecordQuery : FakeAnalysisRecordQuery, IAnalysisRecordQuery
    {
        public int CountPendingAiCalls { get; private set; }

        int IAnalysisRecordQuery.CountPendingAi()
        {
            CountPendingAiCalls++;
            return base.CountPendingAi();
        }
    }

    [Fact]
    public void GetAiStatus_待補件數走快取_失效後重新查詢()
    {
        var optionsStore = Options();
        var runState = new AiAnalysisRunState();
        var countingRecords = new CountingAnalysisRecordQuery();
        countingRecords.Add(new DailyAnalysisRecord
        {
            HostId = 1,
            Host = "HOST-TEST",
            Date = DateTime.Today.AddDays(-1),
            RiskLevel = "高",
            AiPending = true
        });

        var controller = new ScheduleController(
            optionsStore,
            scheduler: null!,
            new SchedulerRunState(),
            hosts: new FakeHostStore(),
            sentinels: new FakeSentinelStore(),
            audit: new RecordingAuditService(),
            currentUser: new FakeCurrentUser(),
            users: new FakeUserStore(),
            records: countingRecords,
            settingsStore: new FakeSystemSettingsStore(),
            userDisplayNames: new UserDisplayNameService(new FakeSystemSettingsStore()),
            aiScheduler: null!,
            aiRunState: runState);

        // 第一次呼叫：快取未命中，底層呼叫 1 次
        var resp1 = controller.GetAiStatus();
        Assert.True(resp1.Success);
        Assert.Equal(1, resp1.Data!.PendingTotal);
        Assert.Equal(1, countingRecords.CountPendingAiCalls);

        // 第二次呼叫：30 秒快取命中，底層呼叫次數仍為 1
        var resp2 = controller.GetAiStatus();
        Assert.True(resp2.Success);
        Assert.Equal(1, resp2.Data!.PendingTotal);
        Assert.Equal(1, countingRecords.CountPendingAiCalls);

        // 使快取失效
        runState.InvalidatePendingAiCache();

        // 第三次呼叫：快取已失效，底層再次查詢，呼叫次數變為 2
        var resp3 = controller.GetAiStatus();
        Assert.True(resp3.Success);
        Assert.Equal(1, resp3.Data!.PendingTotal);
        Assert.Equal(2, countingRecords.CountPendingAiCalls);
    }

    private class TestHttpMessageHandler : HttpMessageHandler
    {
        public string ResponseContent { get; set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var jsonResponse = $$"""
                {
                    "choices": [
                        {
                            "message": {
                                "role": "assistant",
                                "content": {{JsonSerializer.Serialize(ResponseContent)}}
                            }
                        }
                    ]
                }
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private class SamplePayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task ChatJsonAsync_AI契約失敗錯誤訊息包含折疊後回覆前段且不含換行()
    {
        var handler = new TestHttpMessageHandler
        {
            ResponseContent = "{\n  \"invalid\": json syntax\n  with multiple lines\n  and   consecutive   spaces\n}"
        };
        var settings = new AiSettings
        {
            Provider = "OpenAi",
            BaseUrl = "http://localhost:9999",
            ApiKey = "test-key",
            JsonRetryCount = 0,
            RetryCount = 1,
            RetryDelaySeconds = 0,
            TimeoutSeconds = 5
        };
        var service = new AIService(settings, handler);

        var result = await service.ChatJsonAsync<SamplePayload>("測試 prompt");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("AI 回覆不是合法 JSON 或格式不符契約", result.Error);
        // 換行與連續空白被折疊成單一空白
        Assert.Contains("with multiple lines and consecutive spaces", result.Error);
        // 不得包含 \r 或 \n
        Assert.DoesNotContain("\r", result.Error);
        Assert.DoesNotContain("\n", result.Error);
        // 不包含 prompt 內容
        Assert.DoesNotContain("測試 prompt", result.Error);
    }

    [Fact]
    public async Task ChatJsonAsync_AI契約失敗超過300字時截斷並補省略符號()
    {
        // 產生超過 300 字元的長非法回應
        var longChunk = string.Join(" ", Enumerable.Range(1, 100).Select(i => $"item{i}_content_segment"));
        var handler = new TestHttpMessageHandler
        {
            ResponseContent = "invalid json prefix: \n\n" + longChunk
        };
        var settings = new AiSettings
        {
            Provider = "OpenAi",
            BaseUrl = "http://localhost:9999",
            ApiKey = "test-key",
            JsonRetryCount = 0,
            RetryCount = 1,
            RetryDelaySeconds = 0,
            TimeoutSeconds = 5
        };
        var service = new AIService(settings, handler);

        var result = await service.ChatJsonAsync<SamplePayload>("長回應測試");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("…", result.Error);
        Assert.DoesNotContain("\n", result.Error);

        var snippetPart = result.Error["AI 回覆不是合法 JSON 或格式不符契約：".Length..];
        // 300 字元 + 結尾 '…'
        Assert.Equal(AIService.MaxErrorResponseSnippetLength + 1, snippetPart.Length);
    }

    [Fact]
    public async Task ChatJsonAsync_AI契約失敗回覆為空時錯誤訊息包含空回覆()
    {
        var handler = new TestHttpMessageHandler
        {
            ResponseContent = "   \r\n\t   "
        };
        var settings = new AiSettings
        {
            Provider = "OpenAi",
            BaseUrl = "http://localhost:9999",
            ApiKey = "test-key",
            JsonRetryCount = 0,
            RetryCount = 1,
            RetryDelaySeconds = 0,
            TimeoutSeconds = 5
        };
        var service = new AIService(settings, handler);

        var result = await service.ChatJsonAsync<SamplePayload>("空回應測試");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("空回覆", result.Error);
    }

    /// <summary>
    /// 查詢刻意在鎖外跑（實機 7~15 秒）。期間 AI 排程結束使快取失效時，這筆查到的是「執行前的舊件數」，
    /// 寫回會讓畫面最長 30 秒顯示錯誤件數——世代號不符就必須丟棄。
    /// </summary>
    [Fact]
    public void GetPendingAiCount_查詢期間被失效時不寫回快取()
    {
        var runState = new AiAnalysisRunState();
        var calls = 0;

        // 第一次：fetcher 執行途中模擬 AI 排程結束 → 失效
        var first = runState.GetPendingAiCount(() =>
        {
            calls++;
            runState.InvalidatePendingAiCache();
            return 99;
        });
        Assert.Equal(99, first);

        // 第二次：若舊值被寫回，這裡會直接回 99 且 fetcher 不再被呼叫
        var second = runState.GetPendingAiCount(() => { calls++; return 3; });
        Assert.Equal(3, second);
        Assert.Equal(2, calls);
    }
}
