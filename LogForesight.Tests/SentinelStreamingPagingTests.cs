using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Sentinel 取數串流化（回饋三十四輪 A3）：單一 job 上限 10 萬筆、最多 12 個 job 並行，
/// 舊做法讓同一批資料同時有三份在記憶體（扁平清單→分組字典→映射結果）。
/// 串流模式下事件只經 PageObserver 交付，不累積扁平清單——這裡驗證換路徑後
/// 分組結果與舊路徑等價、截斷判定不失真。
/// </summary>
public class SentinelStreamingPagingTests
{
    private static SentinelEvent Evt(string ip, string id) => new(new Dictionary<string, string>
    {
        [SentinelFieldMap.HostIp] = ip,
        [SentinelFieldMap.EventId] = id
    });

    private static List<SentinelEvent> ThreeHostsAcrossPages() => new()
    {
        Evt("10.0.0.1", "1"), Evt("10.0.0.2", "2"), Evt("10.0.0.1", "3"),
        Evt("10.0.0.3", "4"), Evt("10.0.0.2", "5"), Evt("10.0.0.1", "6"),
        Evt("10.0.0.3", "7")
    };

    /// <summary>非串流路徑的分組方式（舊行為），作為比對基準</summary>
    private static Dictionary<string, List<SentinelEvent>> GroupByIpEagerly(IReadOnlyList<SentinelEvent> events) =>
        events
            .GroupBy(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    private static async Task<(SentinelSearchResult Result, Dictionary<string, List<SentinelEvent>> ByIp)>
        RunStreamingAsync(List<SentinelEvent> events, int pageSize, int found)
    {
        var client = new FakeSentinelSearchClient
        {
            Responder = _ => new SentinelSearchResult
            {
                Events = events, Found = found, State = SentinelJobState.Completed
            }
        };

        var byIp = new Dictionary<string, List<SentinelEvent>>(StringComparer.OrdinalIgnoreCase);
        var result = await client.SearchAsync(new SentinelSearchRequest(
            "filter", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now,
            PageSize: pageSize,
            PageObserver: page =>
            {
                foreach (var evt in page)
                {
                    var ip = evt.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty);
                    if (!byIp.TryGetValue(ip, out var bucket))
                    {
                        bucket = new List<SentinelEvent>();
                        byIp[ip] = bucket;
                    }
                    bucket.Add(evt);
                }
                return true;
            },
            StreamOnly: true));

        return (result, byIp);
    }

    [Fact]
    public async Task 串流分組結果與先全量再分組完全一致()
    {
        var events = ThreeHostsAcrossPages();
        var expected = GroupByIpEagerly(events);

        // 每頁 2 筆：同一台主機的事件會跨多頁進來，正是串流最容易分錯的形狀
        var (_, actual) = await RunStreamingAsync(events, pageSize: 2, found: events.Count);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (ip, expectedEvents) in expected)
        {
            Assert.True(actual.ContainsKey(ip), $"分組結果缺少主機 {ip}");
            // 內容與**順序**都必須一致：後續的映射與分析吃的是同一份清單
            Assert.Equal(expectedEvents, actual[ip]);
        }
    }

    [Fact]
    public async Task 串流模式下結果不帶事件但取回筆數正確()
    {
        var events = ThreeHostsAcrossPages();

        var (result, byIp) = await RunStreamingAsync(events, pageSize: 3, found: events.Count);

        Assert.Empty(result.Events);
        Assert.Equal(events.Count, result.Retrieved);
        Assert.Equal(events.Count, byIp.Values.Sum(v => v.Count));
    }

    [Fact]
    public async Task 取回筆數少於Found時判定為截斷()
    {
        var events = ThreeHostsAcrossPages();

        var (truncated, _) = await RunStreamingAsync(events, pageSize: 3, found: events.Count + 10);
        var (complete, _) = await RunStreamingAsync(events, pageSize: 3, found: events.Count);

        Assert.True(truncated.Truncated);
        Assert.False(complete.Truncated);
    }

    [Fact]
    public void 非串流結果未指定取回筆數時以事件數判定截斷()
    {
        var events = ThreeHostsAcrossPages();

        var result = new SentinelSearchResult
        {
            Events = events, Found = events.Count, State = SentinelJobState.Completed
        };

        Assert.Equal(events.Count, result.Retrieved);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task 串流模式未提供PageObserver時擲例外()
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
