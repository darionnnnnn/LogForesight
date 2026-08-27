using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Sentinel 取數串流化（回饋三十四輪 A3）：單一 job 上限 10 萬筆、最多 12 個 job 並行，
/// 舊做法讓同一批資料同時有三份在記憶體（扁平清單→分組字典→映射結果）。
///
/// 這裡守的是**結果物件的語意**：串流模式下 Events 為空，截斷判定必須改看實際取回筆數，
/// 否則「取回 &lt; found」的截斷會被誤判成完整、整批資料悄悄少一截還被當成正常的一天。
/// 分組本身走的是 <see cref="NetiqPipelineService"/> 的正式路徑，由既有的
/// NetiqPipelineBaselineTests／NetiqPipelineAiDecouplingTests 這些整合測試覆蓋
/// （它們現在跑的就是串流分組），這裡不另抄一份分組邏輯來自問自答。
/// </summary>
public class SentinelStreamingPagingTests
{
    private static SentinelEvent Evt(string ip, string id) => new(new Dictionary<string, string>
    {
        [SentinelFieldMap.HostIp] = ip,
        [SentinelFieldMap.EventId] = id
    });

    private static List<SentinelEvent> SevenEvents() => new()
    {
        Evt("10.0.0.1", "1"), Evt("10.0.0.2", "2"), Evt("10.0.0.1", "3"),
        Evt("10.0.0.3", "4"), Evt("10.0.0.2", "5"), Evt("10.0.0.1", "6"),
        Evt("10.0.0.3", "7")
    };

    private static async Task<(SentinelSearchResult Result, List<SentinelEvent> Delivered)>
        RunStreamingAsync(List<SentinelEvent> events, int pageSize, int found)
    {
        var client = new FakeSentinelSearchClient
        {
            Responder = _ => new SentinelSearchResult
            {
                Events = events, Found = found, State = SentinelJobState.Completed
            }
        };

        var delivered = new List<SentinelEvent>();
        var result = await client.SearchAsync(new SentinelSearchRequest(
            "filter", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now,
            PageSize: pageSize,
            PageObserver: page => { delivered.AddRange(page); return true; },
            StreamOnly: true));

        return (result, delivered);
    }

    [Fact]
    public async Task 串流模式下事件只經觀察者交付且內容與順序完整()
    {
        var events = SevenEvents();

        // 每頁 2 筆：同一台主機的事件會跨多頁進來，正是串流最容易漏掉或錯序的形狀
        var (result, delivered) = await RunStreamingAsync(events, pageSize: 2, found: events.Count);

        Assert.Empty(result.Events);
        Assert.Equal(events, delivered);
        Assert.Equal(events.Count, result.Retrieved);
    }

    [Fact]
    public async Task 取回筆數少於Found時判定為截斷()
    {
        var events = SevenEvents();

        var (truncated, _) = await RunStreamingAsync(events, pageSize: 3, found: events.Count + 10);
        var (complete, _) = await RunStreamingAsync(events, pageSize: 3, found: events.Count);

        // 串流模式下 Events 是空的：截斷若還看 Events.Count 會恆為 true，整批被誤標資料不完整
        Assert.True(truncated.Truncated);
        Assert.False(complete.Truncated);
    }

    [Fact]
    public void 非串流結果未指定取回筆數時以事件數判定截斷()
    {
        var events = SevenEvents();

        var result = new SentinelSearchResult
        {
            Events = events, Found = events.Count, State = SentinelJobState.Completed
        };

        Assert.Equal(events.Count, result.Retrieved);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task 串流模式未提供觀察者時擲例外()
    {
        var client = new FakeSentinelSearchClient
        {
            Responder = _ => new SentinelSearchResult
            {
                Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(new SentinelSearchRequest(
            "filter", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now, StreamOnly: true)));
    }
}
