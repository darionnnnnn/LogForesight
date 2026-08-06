using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelRestDirectoryClient：網段範圍掃描的真實 client
/// （docs/NETIQ-API-REFERENCE.md §3.4；**2026-08-06 涵蓋保證改版**
/// docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §三）。
///
/// <para><b>這組測試守的是「涵蓋保證」</b>：掃描結果只有三種——完整、
/// 顯性警告不完整、顯性失敗。被消滅的是第四種：**靜默漏掉主機**。
/// 舊版用「事件越多、掃描窗口越短」控制取回筆數，被裁掉的時間裡安靜主機的
/// 少數幾筆事件就這樣消失，而畫面上看不出來——那些「窗口依比例縮短」的舊測試
/// 因此連同該行為一起移除，不是忘了搬。</para>
///
/// 用 <see cref="ScriptedHandler"/> 依 job 建立順序給定回應，
/// 並保留每個 job 的 filter 供斷言（驗證排除子句真的送出去了）。
/// </summary>
public class SentinelRestDirectoryClientTests
{
    private static SentinelServer Server() => new()
    {
        Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443", Username = "svc", Password = "pw"
    };

    private static NetiqOptions Options() => new() { RetryCount = 1, TimeoutSeconds = 30, QueryDelayMs = 0 };

    private const string AuthUrl = "https://sentinel.local:8443/SentinelAuthServices/auth/tokens";
    private const string JobCollectionUrl = "https://sentinel.local:8443/SentinelRESTServices/objects/event-search";

    /// <summary>一個 job 的模擬回應：found 與實際回傳的事件。
    /// truncated 由 <c>found &gt; 事件數</c> 自然成立，與真實 client 的判斷同源。</summary>
    private sealed record JobScript(int Found, params (string Ip, string? Name)[] Events);

    private static JobScript Empty() => new(0);

    // ── 主掃描：一輪掃完 ────────────────────────────────────────────────────

    [Fact]
    public async Task 一輪掃完_主掃描與補充掃描各發一個job()
    {
        var handler = new ScriptedHandler(
            new JobScript(2, ("10.1.2.5", "srv-dc01"), ("10.1.2.6", "srv-dc02")),   // 主掃描
            Empty());                                                               // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, handler.Filters.Count);

        // 主掃描窄化到低量頻道（成本正比主機數而非事件量）；補充掃描是全事件短窗
        Assert.Contains("rv150:System", handler.Filters[0]);
        Assert.DoesNotContain("rv150:System", handler.Filters[1]);
    }

    /// <summary>
    /// 掃描窗口固定 24 小時，**不再隨事件量縮短**——那正是舊版靜默漏機的機制
    /// （事件越多窗口越短，被裁掉的時間裡安靜主機的少數幾筆事件就這樣消失，且無警告）。
    /// 這裡直接斷言送出去的時間區間，不是斷言文案；而且每一輪殘差輪掃都要維持 24 小時，
    /// 不能「第一輪 24 小時、後面偷偷縮短」。
    /// </summary>
    [Fact]
    public async Task 主掃描窗口固定24小時_不因事件量縮短()
    {
        // 每輪都給極大的 found（＝每輪都觸頂），把殘差輪掃全部跑滿
        var scripts = Enumerable.Range(0, SentinelRestDirectoryClient.MaxResidualRounds)
            .Select(i => new JobScript(9_999_999, ($"10.1.2.{i}", $"srv{i}")))
            .Append(Empty())   // 補充掃描
            .ToArray();
        var handler = new ScriptedHandler(scripts);

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var mainWindows = handler.Windows.Take(SentinelRestDirectoryClient.MaxResidualRounds);
        Assert.All(mainWindows, w => Assert.Equal(24, (w.End - w.Start).TotalHours, precision: 1));
    }

    [Fact]
    public async Task 補充掃描用短窗且排除主掃描已見的主機()
    {
        var handler = new ScriptedHandler(
            new JobScript(1, ("10.1.2.5", "srv1")),
            new JobScript(1, ("10.1.2.9", "srv9")));

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        // 短窗
        var (start, end) = handler.Windows[1];
        Assert.Equal(SentinelRestDirectoryClient.SupplementWindowMinutes, (end - start).TotalMinutes, precision: 1);

        // 排除主掃描已見：不排除的話取回的絕大多數是已知主機的 Security 洪流，純浪費
        Assert.Contains("NOT (repip:10.1.2.5)", handler.Filters[1]);

        // 兩段聯集
        Assert.Equal(2, result.Hosts.Count);
        Assert.Contains(result.Hosts, h => h.IpAddress == "10.1.2.9");
    }

    // ── 殘差輪掃：涵蓋保證的核心 ────────────────────────────────────────────

    /// <summary>
    /// 單輪取回觸頂時**不縮短窗口**，改為排除已發現的主機後重查——
    /// 下一輪取回的只有還沒見過的主機。這是「上限有限、但不會漏機」的關鍵。
    /// </summary>
    [Fact]
    public async Task 主掃描觸頂_排除已見主機後重查直到掃完()
    {
        var handler = new ScriptedHandler(
            new JobScript(500, ("10.1.2.5", "srv5")),                   // 第 1 輪：found > 取回 → 觸頂
            new JobScript(2, ("10.1.2.6", "srv6"), ("10.1.2.7", "srv7")), // 第 2 輪：殘差掃完
            Empty());                                                    // 補充掃描

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(3, result.Hosts.Count);
        Assert.Empty(result.Warnings);   // 掃完了就不該有「可能不完整」的警告

        // 第 2 輪把第 1 輪見到的主機排除掉
        Assert.DoesNotContain("NOT", handler.Filters[0]);
        Assert.Contains("NOT (repip:10.1.2.5)", handler.Filters[1]);
    }

    /// <summary>輪數用盡仍未掃完＝**可能漏**，必須警告——涵蓋保證的三種結果之一</summary>
    [Fact]
    public async Task 輪數用盡仍未掃完_顯性警告而不是靜默回傳()
    {
        // 每輪都觸頂（found 恆大於取回筆數）
        var scripts = Enumerable.Range(0, SentinelRestDirectoryClient.MaxResidualRounds)
            .Select(i => new JobScript(9999, ($"10.1.2.{i}", $"srv{i}")))
            .Append(Empty())   // 補充掃描
            .ToArray();
        var handler = new ScriptedHandler(scripts);

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal(SentinelRestDirectoryClient.MaxResidualRounds, result.Hosts.Count);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("主掃描", warning);
        Assert.Contains("清單可能不完整", warning);
        Assert.Contains("更小的網段", warning);   // 訊息要說得出下一步怎麼辦
    }

    /// <summary>
    /// NOT 子句在本環境**尚未實測**（probe 驗過 OR／片語／前綴萬用字元，沒驗過 NOT）。
    /// 萬一被 Sentinel 忽略，每輪都會取回同一批主機、白燒完輪數，而使用者只看到「掃很久」。
    /// 當場偵測、立刻停止、講清楚——不讓壞掉的機制安靜地假裝在工作。
    /// </summary>
    [Fact]
    public async Task 排除語法未生效_當場偵測並停止輪掃()
    {
        var handler = new ScriptedHandler(
            new JobScript(9999, ("10.1.2.5", "srv5")),   // 第 1 輪觸頂
            new JobScript(9999, ("10.1.2.5", "srv5")),   // 第 2 輪又回同一台＝NOT 沒生效
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Single(result.Hosts);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("排除語法", warning);
        Assert.Contains("未生效", warning);

        // 偵測到就停：不該把 5 輪燒完（2 輪主掃描 + 1 輪補充掃描）
        Assert.Equal(3, handler.Filters.Count);
    }

    // ── 重掃增量：已登錄主機在 server 端就排除 ──────────────────────────────

    [Fact]
    public async Task 帶已登錄主機_第一輪就排除且結果不含它們()
    {
        var handler = new ScriptedHandler(Empty(), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None,
                knownIps: new[] { "10.1.2.11", "10.1.2.12" });

        Assert.Contains("NOT (repip:10.1.2.11 OR repip:10.1.2.12)", handler.Filters[0]);
        Assert.Empty(result.Hosts);   // 沒有新主機——重掃的正常結果
        Assert.Contains("已登錄 2 台未重查", result.CoverageNote);
    }

    /// <summary>已登錄數超過子句上限就整組放棄排除：退回一般掃描只是慢，
    /// 送出超長 filter 會被 Sentinel 整個拒絕——寧可慢也不要整趟失敗</summary>
    [Fact]
    public async Task 已登錄主機數超過子句上限_退回不排除的一般掃描()
    {
        var tooMany = Enumerable.Range(1, SentinelRestDirectoryClient.ExclusionClauseLimit + 1)
            .Select(i => $"10.1.{i / 256}.{i % 256}")
            .ToList();
        var handler = new ScriptedHandler(Empty(), Empty());

        await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1", CancellationToken.None, knownIps: tooMany);

        Assert.DoesNotContain("NOT", handler.Filters[0]);
    }

    // ── 誠實申報 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 兩段皆無事件_講明白沒有回報而不是假裝掃到空網段()
    {
        var handler = new ScriptedHandler(Empty(), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Empty(result.Hosts);
        Assert.Contains("無任何事件回報", result.CoverageNote);
    }

    [Fact]
    public async Task CoverageNote講明兩個窗口與看不到的那一類主機()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.5", "srv5")), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Contains("24 小時", result.CoverageNote);
        Assert.Contains($"{SentinelRestDirectoryClient.SupplementWindowMinutes} 分鐘", result.CoverageNote);
        // 事件掃描原理上看不到「完全沒講話」的主機——這是資料源極限，不能不講
        Assert.Contains("原理上看不到", result.CoverageNote);
    }

    /// <summary>
    /// 主掃描 0 台、補充掃描有台數＝這台 Sentinel 的 collector 可能不轉送
    /// System/Application，窄化 filter 因此失效。說出來讓人去查，不要長期靠短窗硬撐
    /// 而沒有人知道涵蓋率為什麼偏低。
    /// </summary>
    [Fact]
    public async Task 主掃描零台但補充掃描有台數_提示頻道覆蓋可能有問題()
    {
        var handler = new ScriptedHandler(
            Empty(),
            new JobScript(1, ("10.1.2.9", "srv9")));

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("collector 可能不轉送", warning);
        Assert.Contains("診斷", warning);
    }

    [Fact]
    public async Task HostName取同一IP最常見的sn值()
    {
        var handler = new ScriptedHandler(
            new JobScript(3, ("10.1.2.5", "srv-dc01"), ("10.1.2.5", "srv-dc01"), ("10.1.2.5", "WEIRD")),
            Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        var host = Assert.Single(result.Hosts);
        Assert.Equal("srv-dc01", host.HostName);
        Assert.Equal("10.1.2.5", host.IpAddress);
    }

    [Fact]
    public async Task sn缺席時HostName退回IP()
    {
        var handler = new ScriptedHandler(new JobScript(1, ("10.1.2.9", null)), Empty());

        var result = await new SentinelRestDirectoryClient(Options(), handler)
            .ListHostsAsync(Server(), "10.1.2", CancellationToken.None);

        Assert.Equal("10.1.2.9", Assert.Single(result.Hosts).HostName);
    }

    // ── 輸入與失敗路徑（行為不變，沿用改版前的守則）────────────────────────

    [Fact]
    public async Task BaseUrl未設定_立即擲例外不發任何請求()
    {
        var handler = new ScriptedHandler();
        var client = new SentinelRestDirectoryClient(Options(), handler);
        var server = new SentinelServer { Name = "S", BaseUrl = "", Username = "u", Password = "p" };

        await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(server, "10.1.2", CancellationToken.None));
        Assert.Empty(handler.Filters);
    }

    [Fact]
    public async Task 網段格式不正確_轉成NetiqDiscoveryException()
    {
        var client = new SentinelRestDirectoryClient(Options(), new ScriptedHandler());

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "not-a-subnet", CancellationToken.None));
        Assert.Contains("網段格式不正確", ex.Message);
    }

    [Fact]
    public async Task Sentinel回應錯誤_轉成可顯示的NetiqDiscoveryException()
    {
        var handler = new ScriptedHandler { AuthStatus = HttpStatusCode.Unauthorized };
        var client = new SentinelRestDirectoryClient(Options(), handler);

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", CancellationToken.None));
        Assert.Contains("SENTINEL-A", ex.Message);
        Assert.DoesNotContain("pw", ex.Message);   // 密碼不得出現在例外訊息
    }

    /// <summary>
    /// 分頁迴圈不受單次逾時約束（SentinelClient 的 TimeoutSeconds 只管單次呼叫與 job 輪詢），
    /// 整趟掃描必須有自己的總預算，且逾時要**明確報錯**而不是回傳半套清單
    /// （半套會被誤認為該網段的完整名單）。殘差輪掃讓查詢次數變多，這道防線更重要。
    /// </summary>
    [Fact]
    public async Task 整趟掃描超過總預算_明確報錯且不回傳半套結果()
    {
        var handler = new ScriptedHandler(
            Enumerable.Range(0, 20).Select(i => new JobScript(9999, ($"10.1.2.{i}", $"h{i}"))).ToArray())
        {
            PageDelay = TimeSpan.FromMilliseconds(400)
        };

        // 總預算壓到 2 秒：驗證的是「有沒有這道 deadline」，不是正式值本身
        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 2);
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", CancellationToken.None));

        Assert.Contains("超過 2 秒", ex.Message);
        Assert.Contains("更小的網段", ex.Message);
    }

    /// <summary>呼叫端自己取消（管理員關掉分頁）不是掃描失敗，不該被包成假的錯誤訊息</summary>
    [Fact]
    public async Task 呼叫端取消_不轉成掃描失敗訊息()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(Empty()) { OnAuth = () => cts.Cancel() };

        var client = new SentinelRestDirectoryClient(Options(), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListHostsAsync(Server(), "10.1.2", cts.Token));
    }

    /// <summary>
    /// 依 job 建立順序回應的 Sentinel 替身：第 n 個建立的 job 套用第 n 個 <see cref="JobScript"/>。
    /// 同時記下每個 job 的 filter 與時間區間——殘差輪掃的斷言全部落在「送出去的查詢長什麼樣」，
    /// 那才是行為本身，比斷言文案穩固。
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly JobScript[] _scripts;
        private int _jobsCreated;

        public ScriptedHandler(params JobScript[] scripts) => _scripts = scripts;

        /// <summary>每個 job 送出的 filter，依建立順序</summary>
        public List<string> Filters { get; } = new();

        /// <summary>每個 job 的查詢時間區間，依建立順序</summary>
        public List<(DateTimeOffset Start, DateTimeOffset End)> Windows { get; } = new();

        public HttpStatusCode AuthStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>模擬真實 Sentinel 的每頁往返延遲（實測約 1.7 秒），驗證總預算用</summary>
        public TimeSpan PageDelay { get; init; } = TimeSpan.Zero;

        public Action? OnAuth { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Post && url == AuthUrl)
            {
                OnAuth?.Invoke();
                return AuthStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}")
                    : new HttpResponseMessage(AuthStatus);
            }

            if (request.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                Filters.Add(doc.RootElement.GetProperty("filter").GetString()!);
                Windows.Add((
                    DateTimeOffset.Parse(doc.RootElement.GetProperty("start").GetString()!),
                    DateTimeOffset.Parse(doc.RootElement.GetProperty("end").GetString()!)));

                _jobsCreated++;
                return Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{_jobsCreated}");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/results"))
            {
                if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, CancellationToken.None);
                var index = JobIndex(url);
                return Json(HttpStatusCode.OK, EventsJson(_scripts[index]));
            }

            if (request.Method == HttpMethod.Get && url.StartsWith($"{JobCollectionUrl}/job"))
            {
                var index = JobIndex(url);
                if (index >= _scripts.Length)
                    throw new InvalidOperationException($"測試腳本只給了 {_scripts.Length} 個 job，實際建立了第 {index + 1} 個");

                var script = _scripts[index];
                var avail = script.Events.Length;
                if (avail == 0)
                    return Json(HttpStatusCode.OK, $"{{\"status\":2,\"found\":{script.Found},\"avail\":0}}");

                return Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":{script.Found},\"avail\":{avail}," +
                    $"\"results\":{{\"@href\":\"{JobCollectionUrl}/job{index + 1}/results\"}}}}");
            }

            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);

            throw new InvalidOperationException($"未預期的請求：{request.Method} {url}");
        }

        /// <summary>從 .../jobN 或 .../jobN/results 取出 0-based 索引</summary>
        private static int JobIndex(string url)
        {
            var afterJob = url[(url.IndexOf("/job", StringComparison.Ordinal) + 4)..];
            var digits = new string(afterJob.TakeWhile(char.IsDigit).ToArray());
            return int.Parse(digits) - 1;
        }

        private static string EventsJson(JobScript script)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < script.Events.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var (ip, name) = script.Events[i];
                sb.Append($"{{\"repip\":\"{ip}\"");
                if (name != null) sb.Append($",\"sn\":\"{name}\"");
                sb.Append('}');
            }
            return sb.Append(']').ToString();
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
