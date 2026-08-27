using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 掃描匯入的分段平行化測試（作業 C1）。
/// 驗證 Client Pool 節流、每分段獨立 ScanState、決定性結果合併、集合差集未掃描網段推算、
/// 以及單段/多段在不同併發度下的正確行為。
/// </summary>
public class NetiqScanConcurrencyTests
{
    private static SentinelServer Server() => new()
    {
        Name = "SENTINEL-TEST",
        BaseUrl = "https://sentinel.local:8443",
        Username = "svc",
        Password = "pw"
    };

    private static NetiqOptions Options() => new()
    {
        RetryCount = 1,
        TimeoutSeconds = 30,
        QueryDelayMs = 0
    };

    // ── 案例 1：併發度 1 行為不變 ─────────────────────────────────────────────

    /// <summary>
    /// 1. 併發度 1 行為不變：多分段輸入下，假 handler 記錄到的請求順序與改動前相同
    /// （分段依原順序、一段的查詢跑完才輪到下一段，峰值恆為 1）。
    /// </summary>
    [Fact]
    public async Task 併發度1_多分段依序執行且請求順序不變()
    {
        var handler = new ConcurrencyTrackingHandler
        {
            DefaultDelay = TimeSpan.FromMilliseconds(5),
            EventSelector = MakeFourSegmentHosts
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: 1);

        // 4 個分段，每段主掃描 + 補充掃描各 1 次，共 8 個 job
        Assert.Equal(8, handler.CreatedFilters.Count);
        Assert.Equal(1, handler.PeakConcurrentRequests);

        // 驗證順序嚴格為 segment 0 -> 1 -> 2 -> 3
        Assert.Contains("repip:192.168.1.0", handler.CreatedFilters[0]);
        Assert.Contains("repip:192.168.1.0", handler.CreatedFilters[1]);
        Assert.Contains("repip:192.168.1.64", handler.CreatedFilters[2]);
        Assert.Contains("repip:192.168.1.64", handler.CreatedFilters[3]);
        Assert.Contains("repip:192.168.1.128", handler.CreatedFilters[4]);
        Assert.Contains("repip:192.168.1.128", handler.CreatedFilters[5]);
        Assert.Contains("repip:192.168.1.192", handler.CreatedFilters[6]);
        Assert.Contains("repip:192.168.1.192", handler.CreatedFilters[7]);

        // 前一段的時間區間結束時間應小於或等於下一段開始時間（依序）
        for (var i = 0; i < handler.RequestSpans.Count - 1; i++)
        {
            Assert.True(handler.RequestSpans[i].CompletedAt <= handler.RequestSpans[i + 1].StartedAt);
        }

        Assert.Equal(4, result.Hosts.Count);
    }

    // ── 案例 2：併發峰值受限 ──────────────────────────────────────────────────

    /// <summary>
    /// 2. 併發峰值受限：併發度 3、多分段 → 假 handler 記錄「同時進行中的請求數」，
    /// 峰值必須 <= 3（在 handler 內用 Interlocked 計數，記錄最大值）。
    /// </summary>
    [Fact]
    public async Task 併發度3_併發請求峰值不超過3()
    {
        var handler = new ConcurrencyTrackingHandler
        {
            DefaultDelay = TimeSpan.FromMilliseconds(40),
            EventSelector = MakeFourSegmentHosts
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: 3);

        // 併發度應有重疊（大於 1），但嚴格不超過 3
        Assert.True(handler.PeakConcurrentRequests > 1, $"預期發生平行請求，但實際峰值為 {handler.PeakConcurrentRequests}");
        Assert.True(handler.PeakConcurrentRequests <= 3, $"併發峰值超出上限 3，實際為 {handler.PeakConcurrentRequests}");
        Assert.Equal(4, result.Hosts.Count);
    }

    // ── 案例 3：涵蓋完整 ──────────────────────────────────────────────────────

    /// <summary>
    /// 3. 涵蓋完整：併發度 3、多分段 → 所有段全部被查詢過，回傳的主機清單是各段結果的聯集且無重複。
    /// </summary>
    [Fact]
    public async Task 併發度3_所有分段皆被查詢且結果聯集無重複()
    {
        var handler = new ConcurrencyTrackingHandler
        {
            EventSelector = MakeFourSegmentHosts
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: 3);

        // 4 個分段全數查詢（每段主掃描 + 補充掃描各 1 次，共 8 次查詢）
        Assert.Equal(8, handler.CreatedFilters.Count);
        Assert.Contains(handler.CreatedFilters, f => f.Contains("repip:192.168.1.0"));
        Assert.Contains(handler.CreatedFilters, f => f.Contains("repip:192.168.1.64"));
        Assert.Contains(handler.CreatedFilters, f => f.Contains("repip:192.168.1.128"));
        Assert.Contains(handler.CreatedFilters, f => f.Contains("repip:192.168.1.192"));

        // 回傳的主機清單為各段聯集且去重
        Assert.Equal(4, result.Hosts.Count);
        Assert.Equal(4, result.Hosts.Select(h => h.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.10" && h.HostName == "host-0");
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.70" && h.HostName == "host-1");
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.130" && h.HostName == "host-2");
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.200" && h.HostName == "host-3");
        Assert.Empty(result.Warnings);
    }

    // ── 案例 4：決定性 ────────────────────────────────────────────────────────

    /// <summary>
    /// 4. 決定性：同一組輸入以併發度 3 連跑兩次 → Hosts 的順序完全相同（不受分段完成順序影響）。
    /// </summary>
    [Fact]
    public async Task 併發度3_同一輸入連跑兩次結果順序完全相同_保證決定性()
    {
        // 第一次跑：段 3 最快、段 0 最慢
        var handler1 = new ConcurrencyTrackingHandler
        {
            DelaySelector = filter =>
            {
                if (filter.Contains("repip:192.168.1.0")) return TimeSpan.FromMilliseconds(50);
                if (filter.Contains("repip:192.168.1.64")) return TimeSpan.FromMilliseconds(30);
                if (filter.Contains("repip:192.168.1.128")) return TimeSpan.FromMilliseconds(20);
                if (filter.Contains("repip:192.168.1.192")) return TimeSpan.FromMilliseconds(5);
                return TimeSpan.Zero;
            },
            EventSelector = MakeFourSegmentHosts
        };

        // 第二次跑：段 0 最快、段 3 最慢
        var handler2 = new ConcurrencyTrackingHandler
        {
            DelaySelector = filter =>
            {
                if (filter.Contains("repip:192.168.1.0")) return TimeSpan.FromMilliseconds(5);
                if (filter.Contains("repip:192.168.1.64")) return TimeSpan.FromMilliseconds(20);
                if (filter.Contains("repip:192.168.1.128")) return TimeSpan.FromMilliseconds(30);
                if (filter.Contains("repip:192.168.1.192")) return TimeSpan.FromMilliseconds(50);
                return TimeSpan.Zero;
            },
            EventSelector = MakeFourSegmentHosts
        };

        var client1 = new SentinelRestDirectoryClient(Options(), handler1);
        var result1 = await client1.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: 3);

        var client2 = new SentinelRestDirectoryClient(Options(), handler2);
        var result2 = await client2.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: 3);

        Assert.Equal(4, result1.Hosts.Count);
        Assert.Equal(4, result2.Hosts.Count);

        // 兩次的主機清單順序必須完全一致（依分段原始索引 0, 1, 2, 3 排序）
        for (var i = 0; i < result1.Hosts.Count; i++)
        {
            Assert.Equal(result1.Hosts[i].IpAddress, result2.Hosts[i].IpAddress);
            Assert.Equal(result1.Hosts[i].HostName, result2.Hosts[i].HostName);
        }

        Assert.Equal("192.168.1.10", result1.Hosts[0].IpAddress);
        Assert.Equal("192.168.1.70", result1.Hosts[1].IpAddress);
        Assert.Equal("192.168.1.130", result1.Hosts[2].IpAddress);
        Assert.Equal("192.168.1.200", result1.Hosts[3].IpAddress);
    }

    // ── 案例 5：併發度夾住 ────────────────────────────────────────────────────

    /// <summary>
    /// 5. 併發度夾住：傳 0 → 實際等同 1；傳 5 → 實際等同 3（以併發峰值與驗證 client 數斷言）。
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 3)]
    public async Task 併發度夾住_傳入0等同1且傳入5等同3(int inputConcurrency, int expectedEffectiveConcurrency)
    {
        var handler = new ConcurrencyTrackingHandler
        {
            DefaultDelay = TimeSpan.FromMilliseconds(20),
            EventSelector = MakeFourSegmentHosts
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            granularity: ScanGranularity.Slash26,
            concurrency: inputConcurrency);

        Assert.Equal(4, result.Hosts.Count);
        Assert.True(
            handler.PeakConcurrentRequests <= expectedEffectiveConcurrency,
            $"併發峰值 {handler.PeakConcurrentRequests} 超出預期有效併發度 {expectedEffectiveConcurrency}");

        // 每個 client 實例在首次請求時都會向 Sentinel 認證 1 次
        // 因此認證次數等於建立並投入使用的 client 實例數
        Assert.Equal(expectedEffectiveConcurrency, handler.AuthCount);
    }

    // ── 案例 6：未掃描網段正確（集合差集） ────────────────────────────────────

    /// <summary>
    /// 6. 未掃描網段正確：安排讓部分分段在預算用盡前完成、其餘沒完成，斷言警告訊息中列出的
    /// 「未掃描的網段」確實是沒被查過的那些段（驗證集合差集，而非舊版的 Skip(count) 算法）。
    /// </summary>
    [Fact]
    public async Task 預算用盡_未掃描網段以差集計算正確反映未查網段()
    {
        // 4 個分段：
        // 段 0 (.0/26)：立即完成
        // 段 2 (.128/26)：立即完成
        // 段 1 (.64/26)：延遲 5 秒（會被 1 秒預算打斷）
        // 段 3 (.192/26)：尚未開始（或剛要開始即逾時）
        var handler = new ConcurrencyTrackingHandler
        {
            DelaySelector = filter =>
            {
                // 段 1 與段 3 延遲 5 秒，預算 1 秒下必定逾時未完成
                if (filter.Contains("repip:192.168.1.64") || filter.Contains("repip:192.168.1.192"))
                    return TimeSpan.FromSeconds(5);
                return TimeSpan.Zero;
            },
            EventSelector = MakeFourSegmentHosts
        };

        // 總預算設定為 1 秒（覆寫）
        using var cts = new CancellationTokenSource();
        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 1);

        var result = await client.ListHostsAsync(
            Server(), "192.168.1", cts.Token,
            granularity: ScanGranularity.Slash26,
            concurrency: 3);

        // 段 0 與段 2 完成，所以新發現主機包含 192.168.1.10 與 192.168.1.130
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.10");
        Assert.Contains(result.Hosts, h => h.IpAddress == "192.168.1.130");

        // 未完成的段不應出現在主機結果中
        Assert.DoesNotContain(result.Hosts, h => h.IpAddress == "192.168.1.70");
        Assert.DoesNotContain(result.Hosts, h => h.IpAddress == "192.168.1.200");

        // 斷言警告訊息
        var warning = Assert.Single(result.Warnings, w => w.Contains("掃描時間用盡"));
        Assert.Contains("已完成 2/4 段", warning);
        Assert.Contains("未掃描的網段", warning);

        // 關鍵驗證：未掃描的網段「確實是沒查完的那兩段」，不是前兩段也不是後兩段
        Assert.Contains("192.168.1.64/26", warning);
        Assert.Contains("192.168.1.192/26", warning);
        Assert.DoesNotContain("192.168.1.0/26", warning);
        Assert.DoesNotContain("192.168.1.128/26", warning);
    }

    // ── 案例 7：分段數 1 時行為不變 ──────────────────────────────────────────

    /// <summary>
    /// 7. 分段數 1 時：併發度 3 與併發度 1 的行為相同（不會建立多餘的 client、文案為單段的三段式）。
    /// </summary>
    [Fact]
    public async Task 分段數為1時_併發度3與併發度1行為完全一致不建多餘client()
    {
        var progressMessages = new List<string>();
        var handler = new ConcurrencyTrackingHandler
        {
            EventSelector = filter =>
            {
                if (filter.Contains("rv150:System"))
                {
                    return new (string Ip, string? Name)[] { ("192.168.1.5", "host-single") };
                }
                return Array.Empty<(string Ip, string? Name)>();
            }
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(
            Server(), "192.168.1", CancellationToken.None,
            onProgress: (stage, _) => progressMessages.Add(stage),
            granularity: ScanGranularity.Slash24,
            concurrency: 3);

        // 單分段下即使傳入 concurrency: 3，池大小仍為 1，僅向 Sentinel 認證 1 次
        Assert.Equal(1, handler.AuthCount);
        Assert.Equal(1, handler.PeakConcurrentRequests);

        // 進度文案維持單段既有的三段式
        Assert.Contains("主掃描中", progressMessages);
        Assert.Contains("主掃描完成", progressMessages);
        Assert.Contains("補充掃描中", progressMessages);

        // 不應出現多段掃描的分數形式（例如 "1/1"、"掃描中 1/1"）
        Assert.DoesNotContain(progressMessages, m => m.Contains("1/1"));
        Assert.DoesNotContain(progressMessages, m => m.Contains("/"));

        Assert.Single(result.Hosts);
        Assert.Equal("192.168.1.5", result.Hosts[0].IpAddress);
    }

    // ── 案例 8：單段預算用盡仍直接擲 NetiqDiscoveryException（回歸保護） ─────────

    /// <summary>
    /// 單段預算用盡時應直接擲出 NetiqDiscoveryException，不轉成半套結果（與多段容錯行為區分）。
    /// </summary>
    [Fact]
    public async Task 單段預算用盡仍直接擲NetiqDiscoveryException_回歸保護()
    {
        var handler = new ConcurrencyTrackingHandler
        {
            DefaultDelay = TimeSpan.FromMilliseconds(2500)
        };

        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 1);
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "192.168.1", CancellationToken.None, concurrency: 3));

        Assert.Contains("超過 1 秒仍未完成", ex.Message);
        Assert.Contains("更小的網段", ex.Message);
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    private static (string Ip, string? Name)[] MakeFourSegmentHosts(string filter)
    {
        if (!filter.Contains("rv150:System")) return Array.Empty<(string Ip, string? Name)>();
        if (filter.Contains("repip:192.168.1.0")) return new (string Ip, string? Name)[] { ("192.168.1.10", "host-0") };
        if (filter.Contains("repip:192.168.1.64")) return new (string Ip, string? Name)[] { ("192.168.1.70", "host-1") };
        if (filter.Contains("repip:192.168.1.128")) return new (string Ip, string? Name)[] { ("192.168.1.130", "host-2") };
        if (filter.Contains("repip:192.168.1.192")) return new (string Ip, string? Name)[] { ("192.168.1.200", "host-3") };
        return Array.Empty<(string Ip, string? Name)>();
    }

    /// <summary>
    /// 執行緒安全的 Sentinel HTTP 替身，專門記錄併發狀態與請求軌跡。
    /// </summary>
    private sealed class ConcurrencyTrackingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private int _inFlightRequests;
        private int _peakConcurrentRequests;
        private int _authCount;
        private int _jobIdSequence;
        private readonly ConcurrentDictionary<int, (string Filter, (string Ip, string? Name)[] Events)> _jobs = new();

        public int PeakConcurrentRequests => Volatile.Read(ref _peakConcurrentRequests);
        public int AuthCount => Volatile.Read(ref _authCount);
        public List<string> CreatedFilters { get; } = new();
        public List<(string Filter, DateTimeOffset StartedAt, DateTimeOffset CompletedAt)> RequestSpans { get; } = new();

        public TimeSpan DefaultDelay { get; set; } = TimeSpan.Zero;
        public Func<string, TimeSpan>? DelaySelector { get; set; }
        public Func<string, (string Ip, string? Name)[]>? EventSelector { get; set; }

        private void TrackPeak(int current)
        {
            int currentMax;
            do
            {
                currentMax = _peakConcurrentRequests;
                if (current <= currentMax) break;
            } while (Interlocked.CompareExchange(ref _peakConcurrentRequests, current, currentMax) != currentMax);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var inFlight = Interlocked.Increment(ref _inFlightRequests);
            TrackPeak(inFlight);
            var start = DateTimeOffset.UtcNow;
            try
            {
                var url = request.RequestUri!.ToString();

                if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelAuthServices/auth/tokens"))
                {
                    Interlocked.Increment(ref _authCount);
                    return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
                }

                if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelRESTServices/objects/event-search"))
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var filter = doc.RootElement.GetProperty("filter").GetString()!;

                    var delay = DelaySelector?.Invoke(filter) ?? DefaultDelay;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct);
                    }

                    lock (_lock)
                    {
                        CreatedFilters.Add(filter);
                    }

                    var jobId = Interlocked.Increment(ref _jobIdSequence);
                    var events = EventSelector?.Invoke(filter) ?? Array.Empty<(string Ip, string? Name)>();
                    _jobs[jobId] = (filter, events);

                    lock (_lock)
                    {
                        RequestSpans.Add((filter, start, DateTimeOffset.UtcNow));
                    }

                    return Json(HttpStatusCode.Created, "{}", $"https://sentinel.local:8443/SentinelRESTServices/objects/event-search/job{jobId}");
                }

                if (request.Method == HttpMethod.Get && url.Contains("/results"))
                {
                    var jobId = ExtractJobId(url);
                    if (_jobs.TryGetValue(jobId, out var jobData))
                    {
                        return Json(HttpStatusCode.OK, EventsJson(jobData.Events));
                    }
                    return Json(HttpStatusCode.OK, "[]");
                }

                if (request.Method == HttpMethod.Get && url.Contains("/objects/event-search/job"))
                {
                    var jobId = ExtractJobId(url);
                    if (_jobs.TryGetValue(jobId, out var jobData))
                    {
                        var avail = jobData.Events.Length;
                        if (avail == 0)
                        {
                            return Json(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}");
                        }
                        return Json(HttpStatusCode.OK,
                            $"{{\"status\":2,\"found\":{avail},\"avail\":{avail},\"results\":{{\"@href\":\"https://sentinel.local:8443/SentinelRESTServices/objects/event-search/job{jobId}/results\"}}}}");
                    }
                    return Json(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}");
                }

                if (request.Method == HttpMethod.Delete)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                throw new InvalidOperationException($"未預期的請求：{request.Method} {url}");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightRequests);
            }
        }

        private static int ExtractJobId(string url)
        {
            var afterJob = url[(url.IndexOf("/job", StringComparison.Ordinal) + 4)..];
            var digits = new string(afterJob.TakeWhile(char.IsDigit).ToArray());
            return int.Parse(digits);
        }

        private static string EventsJson((string Ip, string? Name)[] events)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < events.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var (ip, name) = events[i];
                sb.Append($"{{\"repip\":\"{ip}\"");
                if (name != null) sb.Append($",\"sn\":\"{name}\"");
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json, string? location = null)
        {
            var resp = new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (location != null) resp.Headers.Location = new Uri(location);
            return resp;
        }
    }
}
