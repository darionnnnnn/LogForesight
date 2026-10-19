using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgProbeRunner 的單元測試：驗證 6 個探測步驟、排序、累積覆蓋率、IP 判定、相依性容錯、髒資料容錯與連線失敗處理。
/// </summary>
public class PrtgProbeRunnerTests
{
    private const string BaseUrl = "https://prtg.example.com";
    private const string SampleToken = "test-token-xyz";

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();

        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return await OnSend(request, cancellationToken);
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PrtgProbeRunnerTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EnvironmentProbeClient_capsRequestTimeoutWithoutChangingOtherClients()
    {
        var settings = new LogForesight.Core.Models.SystemSettings
        {
            PrtgUrl = BaseUrl,
            PrtgApiTokenEnc = SampleToken,
            PrtgTimeoutSeconds = 600
        };

        using var probeDefault = PrtgClientFactory.CreateForEnvironmentProbe(settings);
        Assert.Equal(PrtgProbeRunner.RequestTimeout, probeDefault.Timeout);

        settings.PrtgTimeoutSeconds = 5;
        using var probeShort = PrtgClientFactory.CreateForEnvironmentProbe(settings);
        Assert.Equal(TimeSpan.FromSeconds(5), probeShort.Timeout);

        settings.PrtgTimeoutSeconds = 600;
        using var ordinary = PrtgClientFactory.Create(settings);
        Assert.Equal(TimeSpan.FromSeconds(600), ordinary.Timeout);
    }

    [Fact]
    public async Task RunWithOverallDeadlineAsync_turnsShortControlledDeadlineIntoTimeoutFailure()
    {
        var handler = new StubHandler
        {
            OnSend = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable after cancellation");
            }
        };
        var settings = new LogForesight.Core.Models.SystemSettings
        {
            PrtgUrl = BaseUrl,
            PrtgApiTokenEnc = SampleToken,
            PrtgTimeoutSeconds = 600
        };
        using var client = PrtgClientFactory.Create(settings, handler);
        var console = new TestConsole();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            PrtgProbeRunner.RunWithOverallDeadlineAsync(client, console, TimeSpan.FromMilliseconds(40)));

        Assert.Contains("整輪時間上限", error.Message);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("/api/status.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunWithOverallDeadlineAsync_preservesCallerCancellationClassification()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var handler = new StubHandler();
        var settings = new LogForesight.Core.Models.SystemSettings
        {
            PrtgUrl = BaseUrl,
            PrtgApiTokenEnc = SampleToken,
            PrtgTimeoutSeconds = 600
        };
        using var client = PrtgClientFactory.Create(settings, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrtgProbeRunner.RunWithOverallDeadlineAsync(client, new TestConsole(), TimeSpan.FromHours(1),
                ct: caller.Token));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task RunAsync_正常回應時輸出type分布且回true()
    {
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""24.2.98""}"));
                }
                if (url.Contains("content=devices") && url.Contains("count=1&"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": ""10"", ""devices"": [{""objid"": 1}]}"));
                }
                if (url.Contains("content=sensors") && url.Contains("count=1&"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": ""5"", ""sensors"": [{""objid"": 10}]}"));
                }
                if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
                {
                    // 5 個 sensor，3 種 type：
                    // ping (3 筆), snmpcpu (1 筆), http (1 筆)
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 5,
                        ""sensors"": [
                            {""objid"": 101, ""device"": ""DeviceA"", ""sensor"": ""Ping 1"", ""type"": ""ping"", ""unit"": ""ms""},
                            {""objid"": 102, ""device"": ""DeviceB"", ""sensor"": ""Ping 2"", ""type"": ""ping"", ""unit"": ""ms""},
                            {""objid"": 103, ""device"": ""DeviceC"", ""sensor"": ""CPU 1"", ""type"": ""snmpcpu"", ""unit"": ""%""},
                            {""objid"": 104, ""device"": ""DeviceD"", ""sensor"": ""Ping 3"", ""type"": ""ping"", ""unit"": ""msec""},
                            {""objid"": 105, ""device"": ""DeviceE"", ""sensor"": ""Web 1"", ""type"": ""http"", ""unit"": ""ms""}
                        ]
                    }"));
                }
                if (url.Contains("content=sensors") && url.Contains("columns=objid,dependency"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 5,
                        ""sensors"": [
                            {""objid"": 101, ""dependency"": ""0""},
                            {""objid"": 102, ""dependency"": ""200""}
                        ]
                    }"));
                }
                if (url.Contains("content=groups"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 2,
                        ""groups"": [
                            {""objid"": 10, ""group"": ""Core Network""},
                            {""objid"": 11, ""group"": ""Servers""}
                        ]
                    }"));
                }
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 2,
                        ""devices"": [
                            {""objid"": 1, ""device"": ""Router"", ""host"": ""192.168.1.1"", ""group"": ""Core Network""},
                            {""objid"": 2, ""device"": ""WebServer"", ""host"": ""web.corp.local"", ""group"": ""Servers""}
                        ]
                    }"));
                }

                return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines)
        {
            _output.WriteLine(line);
        }

        Assert.True(result);

        // 驗證版本有輸出
        Assert.Contains(console.Lines, l => l.Contains("PRTG 版本：24.2.98"));

        // 斷言由多到少排序：ping (3筆) 應排在 snmpcpu 或 http (各1筆) 前面
        var pingIdx = console.Lines.FindIndex(l => l.Contains("ping | 3 | 60.0%"));
        var snmpIdx = console.Lines.FindIndex(l => l.Contains("snmpcpu | 1 | 20.0%"));
        var httpIdx = console.Lines.FindIndex(l => l.Contains("http | 1 | 20.0%"));

        Assert.True(pingIdx >= 0, "應包含 ping 分布輸出行");
        Assert.True(snmpIdx >= 0, "應包含 snmpcpu 分布輸出行");
        Assert.True(httpIdx >= 0, "應包含 http 分布輸出行");
        Assert.True(pingIdx < snmpIdx, "數量多的 ping 應排在數量少的 snmpcpu 前面");
        Assert.True(pingIdx < httpIdx, "數量多的 ping 應排在數量少的 http 前面");
    }

    [Fact]
    public async Task RunAsync_累積覆蓋率統計正確()
    {
        // 10 筆 sensor：type A 佔 8 筆 (80%)、type B 佔 1 筆 (10%)、type C 佔 1 筆 (10%)
        // 累積 50% 需要 1 個 (type A: 80% >= 50%)
        // 累積 80% 需要 1 個 (type A: 80% >= 80%)
        // 累積 90% 需要 2 個 (type A+B: 90% >= 90%)
        // 累積 95% 需要 3 個 (type A+B+C: 100% >= 95%)
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""Version"": ""23.1""}"));
                if (url.Contains("content=devices") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""devices"": []}"));
                if (url.Contains("content=sensors") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 10, ""sensors"": []}"));
                if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
                {
                    var items = new List<string>();
                    for (int i = 1; i <= 8; i++) items.Add($@"{{""objid"": {i}, ""type"": ""TypeA"", ""unit"": ""ms""}}");
                    items.Add(@"{""objid"": 9, ""type"": ""TypeB"", ""unit"": ""%""}");
                    items.Add(@"{""objid"": 10, ""type"": ""TypeC"", ""unit"": ""kbps""}");
                    var json = @"{""treesize"": 10, ""sensors"": [" + string.Join(",", items) + @"]}";
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, json));
                }
                if (url.Contains("content=sensors") && url.Contains("columns=objid,dependency"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 0, ""sensors"": []}"));
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 0, ""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 0, ""devices"": []}"));

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);

        var summaryLine = console.Lines.FirstOrDefault(l => l.Contains("[總結] 不重複 type 數量：3 種"));
        Assert.NotNull(summaryLine);
        Assert.Contains("累積達 50% 需要 1 個 type", summaryLine);
        Assert.Contains("累積達 80% 需要 1 個 type", summaryLine);
        Assert.Contains("累積達 90% 需要 2 個 type", summaryLine);
        Assert.Contains("累積達 95% 需要 3 個 type", summaryLine);
    }

    [Fact]
    public async Task RunAsync_IP覆蓋統計能分辨IPv4與DNS名稱()
    {
        // 5 台 device：
        // 2 台 IPv4 ("192.168.1.10", "10.0.0.1")
        // 2 台 DNS ("server.example.local", "db-01.corp")
        // 1 台空值/無 host
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""version"": ""22.4""}"));
                if (url.Contains("content=devices") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 5, ""devices"": []}"));
                if (url.Contains("content=sensors") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 5, ""sensors"": []}"));
                if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1, ""type"": ""ping""}]}"));
                if (url.Contains("content=sensors") && url.Contains("columns=objid,dependency"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": []}"));
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 5,
                        ""devices"": [
                            {""objid"": 1, ""device"": ""Host1"", ""host"": ""192.168.1.10""},
                            {""objid"": 2, ""device"": ""Host2"", ""host"": ""10.0.0.1""},
                            {""objid"": 3, ""device"": ""Host3"", ""host"": ""server.example.local""},
                            {""objid"": 4, ""device"": ""Host4"", ""host"": ""db-01.corp""},
                            {""objid"": 5, ""device"": ""Host5"", ""host"": """" },
                            {""objid"": 6, ""device"": ""Host6"", ""host"": ""10.0.0.2:8080""},
                            {""objid"": 7, ""device"": ""Placeholder"", ""host"": ""10.2xx.x.x""},
                            {""objid"": 8, ""device"": ""Host8"", ""host"": ""[fe80::1]:8080""}
                        ]
                    }"));
                }

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);

        Assert.Contains(console.Lines, l => l.Contains("有設定 host 值的 Device 數：7"));
        // IPv6 是合法位址，不能被列進「無法判定」叫人去修
        Assert.Contains(console.Lines, l => l.Contains("其中為 IPv6 位址者：1 台"));
        // 「IP 帶 port」與主機對應同一份判定，算 IPv4
        Assert.Contains(console.Lines, l => l.Contains("其中為 IPv4 位址者：3 台"));
        Assert.Contains(console.Lines, l => l.Contains("其中為 DNS 名稱者：2 台"));
        Assert.Contains(console.Lines, l => l.Contains("無法判定") && l.Contains("1 台") && l.Contains("objid 7「10.2xx.x.x」"));
    }

    [Fact]
    public async Task RunAsync_髒資料容錯()
    {
        // 3 筆 sensor，其中 1 筆缺 type，1 筆 type 為 null，1 筆正常
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""23.4""}"));
                if (url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 3, ""devices"": [], ""sensors"": []}"));
                if (url.Contains("columns=objid,device,sensor,type,tags,unit"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 3,
                        ""sensors"": [
                            {""objid"": 1, ""device"": ""Dev1"", ""type"": ""ping"", ""unit"": ""ms""},
                            {""objid"": 2, ""device"": ""Dev2"", ""type"": null},
                            {""objid"": 3, ""device"": ""Dev3""}
                        ]
                    }"));
                }
                if (url.Contains("columns=objid,dependency"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": []}"));
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""devices"": []}"));

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "遇到髒資料不應擲例外");
        Assert.Contains(console.Lines, l => l.Contains("有 2 筆 sensor 無法解析"));
        Assert.Contains(console.Lines, l => l.Contains("ping | 1 | 100.0%"));
    }

    [Fact]
    public async Task RunAsync_連線失敗時輸出錯誤行並回false()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => throw new HttpRequestException("連線逾時，無法建立 socket 連線")
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.False(result);
        Assert.Contains(console.Lines, l => l.Contains("失敗：連線 PRTG 伺服器失敗"));
        Assert.Contains(console.Lines, l => l.Contains("探測終止（連線或認證失敗）"));
    }

    /// <summary>
    /// 建一個把步驟 1~7 都餵最小合法資料的替身，步驟 8 的三次 count=5 查詢交給 pagingResponder
    /// （依 content 與 start 決定回哪些 objid）。步驟 6 的 devices 大 count 回 deviceRows 筆。
    /// </summary>
    private static StubHandler BuildPagingStub(
        Func<string, int, long[]> pagingResponder,
        int deviceTreesize = 2,
        int deviceRows = 2,
        Func<string, long[]>? sortedResponder = null)
    {
        return new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""24.2.98""}"));

                if (url.Contains("count=5&start="))
                {
                    var content = url.Contains("content=devices") ? "devices" : url.Contains("content=sensors") ? "sensors" : "messages";
                    var startText = url.Split("start=")[1].Split('&')[0];
                    // sortby 查詢固定打 start=0；未指定 sortedResponder 時等同「排序參數不改變結果」
                    var ids = url.Contains("sortby=objid")
                        ? (sortedResponder?.Invoke(content) ?? pagingResponder(content, 0))
                        : pagingResponder(content, int.Parse(startText));
                    var rows = string.Join(",", ids.Select(id => $"{{\"objid\": {id}}}"));
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"treesize\": 12, \"{content}\": [{rows}]}}"));
                }

                if (url.Contains("content=devices") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"treesize\": {deviceTreesize}, \"devices\": [{{\"objid\": 1}}]}}"));
                if (url.Contains("content=sensors") && url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""sensors"": [{""objid"": 10}]}"));
                if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""sensors"": [{""objid"": 101, ""device"": ""A"", ""sensor"": ""Ping"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1}]}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                {
                    var rows = string.Join(",", Enumerable.Range(1, deviceRows).Select(i => $"{{\"objid\": {i}, \"device\": \"D{i}\", \"host\": \"10.0.0.{i}\", \"group\": \"G\"}}"));
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"treesize\": {deviceTreesize}, \"devices\": [{rows}]}}"));
                }

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };
    }

    [Fact]
    public async Task RunAsync_步驟8_遵守start且超出範圍回空頁時判為可正常收斂()
    {
        var stub = BuildPagingStub((_, start) => start switch
        {
            0 => new long[] { 1, 2, 3, 4, 5 },
            5 => new long[] { 6, 7, 8, 9, 10 },
            _ => Array.Empty<long>()
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("devices 順序穩定、超界回空頁"));
        Assert.Contains(console.Lines, l => l.Contains("sensors 順序穩定、超界回空頁"));
        // messages 的診斷必須以單一裝置＋相對日期過濾查詢，否則是整台訊息歷史的查詢
        Assert.Contains(stub.RequestedUrls, u => u.Contains("content=messages") && u.Contains("filter_drel=7days") && !u.Contains("id=0&"));
    }

    [Fact]
    public async Task RunAsync_步驟8_完全忽略start時判為只能單次大count()
    {
        var stub = BuildPagingStub((_, _) => new long[] { 1, 2, 3, 4, 5 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "分頁語意是診斷，結論不影響探測成敗");
        Assert.Contains(console.Lines, l => l.Contains("devices 順序穩定、✗ 忽略 start（只能單次大 count）"));
    }

    [Fact]
    public async Task RunAsync_步驟8_超出範圍夾到末頁或回第一頁時提示需要保險絲()
    {
        var stub = BuildPagingStub((content, start) => (content, start) switch
        {
            (_, 0) => new long[] { 1, 2, 3, 4, 5 },
            (_, 5) => new long[] { 6, 7, 8, 9, 10 },
            ("devices", _) => new long[] { 1, 2, 3, 4, 5 },   // 回到第一頁
            _ => new long[] { 8, 9, 10, 11, 12 }               // 夾到最後一頁
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices 順序穩定、超界回第一頁（分頁保險絲已處理）"));
        Assert.Contains(console.Lines, l => l.Contains("sensors 順序穩定、超界夾末頁（分頁保險絲已處理）"));
    }

    [Fact]
    public async Task RunAsync_步驟8_單次查詢失敗只印原因不算探測失敗()
    {
        var stub = BuildPagingStub((content, _) => content == "messages"
            ? throw new HttpRequestException("messages 端點 500")
            : new long[] { 1, 2 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("messages 無法判定（"));
        Assert.Contains(console.Lines, l => l.Contains("devices 順序穩定、資料不足以判定 start"));
    }

    [Fact]
    public async Task RunAsync_步驟8_預設順序不穩定但sortby有效時判為必須帶sortby()
    {
        // 實機（24.1.92）的 messages 同頁內 objid 不遞增，這是分頁漏列的來源
        var stub = BuildPagingStub(
            (_, start) => start == 0 ? new long[] { 59590, 82114, 56991, 85029, 57288 } : new long[] { 87261, 59520 },
            sortedResponder: _ => new long[] { 1001, 1002, 1003, 1004, 1005 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices 需帶 sortby=objid"));
        Assert.Contains(stub.RequestedUrls, u => u.Contains("content=devices") && u.Contains("sortby=objid"));
        // messages 的 sortby 查詢必須保留相對日期過濾，否則是整台訊息歷史的查詢
        Assert.Contains(stub.RequestedUrls, u => u.Contains("content=messages") && u.Contains("sortby=objid") && u.Contains("filter_drel=7days"));
    }

    [Fact]
    public async Task RunAsync_步驟8_sortby無效且預設非遞增時判為只能單次大count()
    {
        var unsorted = new long[] { 59590, 82114, 56991, 85029, 57288 };
        var stub = BuildPagingStub(
            (_, start) => start == 0 ? unsorted : new long[] { 87261, 59520 },
            sortedResponder: _ => unsorted);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "排序診斷不影響探測成敗");
        Assert.Contains(console.Lines, l => l.Contains("sensors ✗ 順序不穩（可能漏列）"));
    }

    [Fact]
    public async Task RunAsync_步驟8_預設已遞增時判為不需要sortby()
    {
        var stub = BuildPagingStub((_, start) => start switch
        {
            0 => new long[] { 1, 2, 3, 4, 5 },
            5 => new long[] { 6, 7, 8, 9, 10 },
            _ => Array.Empty<long>()
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices 順序穩定、超界回空頁"));
    }

    [Fact]
    public async Task RunAsync_步驟5_群組取樣少於treesize時警告截斷()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=groups"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    @"{""treesize"": 1500, ""groups"": [{""objid"": 10, ""group"": ""A""}, {""objid"": 11, ""group"": ""B""}]}"));
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("群組總數為 1500，本次查詢僅取樣到 2 筆"));
    }

    [Fact]
    public async Task RunAsync_步驟6_device大count取樣少於treesize時警告截斷()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>(), deviceTreesize: 10, deviceRows: 3);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("警告：Device 總數為 10 筆，本次查詢僅取樣到 3 筆"));
    }

    [Fact]
    public async Task RunAsync_步驟6_treesize小於實際筆數時指出treesize不是總筆數()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>(), deviceTreesize: 2, deviceRows: 4);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("treesize 回報 2 筆但實際取得 4 筆"));
    }

    /// <summary>
    /// 步驟 1~8 餵最小合法資料、步驟 3 的 sensor 表由 step3Sensors 指定，
    /// 步驟 9 的量測查詢（9a／9b／historicdata）交給 perfResponder，回 null 表示走預設空回應。
    /// </summary>
    private static StubHandler BuildPerfStub(string step3Sensors, Func<string, HttpResponseMessage?>? perfResponder = null)
    {
        return new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();

                var custom = perfResponder?.Invoke(url);
                if (custom != null) return Task.FromResult(custom);

                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""24.2.98""}"));
                if (url.Contains("count=1&"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""devices"": [], ""sensors"": []}"));
                if (url.Contains("columns=objid,device,sensor,type,tags,unit"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, step3Sensors));
                if (url.Contains("columns=objid,dependency"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": []}"));
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""devices"": []}"));

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };
    }

    private static string SensorRows(int count, string type = "Ping", string status = "Up")
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => $"{{\"objid\": {i}, \"type\": \"{type}\", \"status\": \"{status}\", \"parentid\": 1}}");
        return "{\"sensors\": [" + string.Join(",", rows) + "]}";
    }

    private static int CountFromUrl(string url) => int.Parse(url.Split("count=")[1].Split('&')[0]);

    private static string TypedSensorRows(params (string Type, int Count)[] specs)
    {
        var rows = new List<string>();
        var objid = 1;
        foreach (var (type, count) in specs)
        {
            for (var i = 0; i < count; i++)
            {
                rows.Add($"{{\"objid\": {objid}, \"type\": \"{type}\", \"status\": \"Up\", \"parentid\": 1}}");
                objid++;
            }
        }
        return "{\"sensors\": [" + string.Join(",", rows) + "]}";
    }

    [Fact]
    public async Task RunAsync_步驟9_不發出舊版壓測請求()
    {
        var stub = BuildPerfStub(SensorRows(10));

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.DoesNotContain(stub.RequestedUrls, u => u.Contains("columns=objid,parentid,sensor,type,tags,unit,status,paused,dependency"));
        Assert.DoesNotContain(stub.RequestedUrls, u => u.Contains("content=messages") && (u.Contains("id=0&") || u.Contains("id=0")));
        Assert.DoesNotContain(console.Lines, l => l.Contains("9a：") || l.Contains("9b：") || l.Contains("9c："));
        Assert.DoesNotContain(console.Lines, l => l.Contains("本步驟會發 87 次 historicdata"));
    }

    [Fact]
    public async Task RunAsync_步驟9_歷史資料回應HTML時印錯誤紀錄不中斷探測()
    {
        const string html = @"<HTML><BODY class=""no-content""><B class=""no-content"">OK</B></BODY></HTML>";
        var stub = BuildPerfStub(SensorRows(1, type: "SNMP CPU Load"), url =>
            url.Contains("/api/historicdata.json")
                ? JsonResponse(HttpStatusCode.OK, html)
                : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("historicdata 回應不是合法 JSON") || l.Contains("historicdata 請求失敗") || l.Contains("探測"));
    }

    /// <summary>正常解析時不能冒出這一行，否則每次探測都會多一句假警告。</summary>
    [Fact]
    public async Task RunAsync_步驟4_正常解析時不印無法解析行()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("columns=objid,dependency"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1, ""dependency"": ""200""}]}"));
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.DoesNotContain(console.Lines, l => l.Contains("回應無法解析：長度"));
    }

    /// <summary>
    /// 步驟 8 的 messages 查詢改帶 datetime，並把 messages 的頁內 datetime 序列交給 datetimes（依 start 給）。
    /// </summary>
    private static StubHandler BuildMessageDatetimeStub(Func<int, (long Id, string Dt)[]> datetimes)
    {
        var stub = BuildPagingStub((_, start) => start switch
        {
            0 => new long[] { 1, 2, 3, 4, 5 },
            5 => new long[] { 6, 7, 8, 9, 10 },
            _ => Array.Empty<long>()
        });
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages") && url.Contains("count=5&start="))
            {
                var start = int.Parse(url.Split("start=")[1].Split('&')[0]);
                var rows = string.Join(",", datetimes(start).Select(r => $"{{\"objid\": {r.Id}, \"datetime\": \"{r.Dt}\"}}"));
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"treesize\": 12, \"messages\": [{rows}]}}"));
            }
            return inner(req, ct);
        };
        return stub;
    }

    [Fact]
    public async Task RunAsync_步驟8_messages時間單調時判為分頁可行()
    {
        // 實機的 messages 頁內 objid 亂序但時間是遞減的：用 objid 判順序會誤判成「順序不穩」
        var stub = BuildMessageDatetimeStub(start => start switch
        {
            0 => new (long, string)[]
            {
                (59590, "2026-09-11 10:00:00"), (82114, "2026-09-11 09:00:00"), (56991, "2026-09-11 08:00:00"),
                (85029, "2026-09-11 07:00:00"), (57288, "2026-09-11 06:00:00")
            },
            5 => new (long, string)[]
            {
                (87261, "2026-09-11 05:00:00"), (59520, "2026-09-11 04:00:00"), (11, "2026-09-11 03:00:00"),
                (12, "2026-09-11 02:00:00"), (13, "2026-09-11 01:00:00")
            },
            _ => Array.Empty<(long, string)>()
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("messages 依時間排序"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("messages ⚠ 順序不依時間"));
        // messages 要多帶 datetime；devices／sensors 不能多帶一個它們沒有的欄位
        Assert.Contains(stub.RequestedUrls, u => u.Contains("content=messages") && u.Contains("columns=objid,datetime"));
        Assert.DoesNotContain(stub.RequestedUrls, u => u.Contains("content=devices") && u.Contains("datetime"));
        Assert.DoesNotContain(stub.RequestedUrls, u => u.Contains("content=sensors") && u.Contains("datetime"));
    }

    [Fact]
    public async Task RunAsync_步驟8_messages時間不單調時警告()
    {
        var stub = BuildMessageDatetimeStub(start => start switch
        {
            0 => new (long, string)[]
            {
                (1, "2026-09-11 10:00:00"), (2, "2026-09-11 12:00:00"), (3, "2026-09-11 08:00:00"),
                (4, "2026-09-11 15:00:00"), (5, "2026-09-11 09:00:00")
            },
            5 => new (long, string)[] { (6, "2026-09-11 05:00:00"), (7, "2026-09-11 04:00:00") },
            _ => Array.Empty<(long, string)>()
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("messages ⚠ 順序不依時間（可能漏列）"));
    }

    [Fact]
    public async Task RunAsync_步驟9_9d5逐裝置查詢仍會執行()
    {
        var stub = BuildPerfStub(SensorRows(5), url =>
        {
            if (url.Contains("/api/historicdata.json"))
                return JsonResponse(HttpStatusCode.OK, @"{""histdata"": [{""datetime"": ""2026-10-01 10:00:00"", ""value_raw"": 1.0}]}");
            return null;
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5："));
    }

    [Fact]
    public async Task RunAsync_Type分布明細附內建分類與補充對照指引()
    {
        var rows = @"{""sensors"": [
            {""objid"": 1, ""type"": ""Ping"", ""status"": ""Up"", ""parentid"": 1},
            {""objid"": 2, ""type"": ""Ping"", ""status"": ""Up"", ""parentid"": 1},
            {""objid"": 3, ""type"": ""HTTP Advanced"", ""status"": ""Up"", ""parentid"": 1}
        ]}";
        var stub = BuildPerfStub(rows);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("Ping | 2 |") && l.EndsWith("| 內建分類：availability"));
        Assert.Contains(console.Lines, l => l.Contains("HTTP Advanced | 1 |") && l.EndsWith("| 內建分類：未分類"));

        var hintIdx = console.Lines.FindIndex(l => l.Contains("未分類的 type 可在 PRTG 維護頁『sensor type 分類補充對照』指定。"));
        var lastDetailIdx = console.Lines.FindLastIndex(l => l.Contains("| 內建分類："));
        Assert.True(hintIdx > lastDetailIdx, "指引行應在 Type 分布明細之後");
    }

    private static bool UrlHasId(string url, long id)
    {
        return url.Contains($"&id={id}&") || url.EndsWith($"&id={id}") ||
               url.Contains($"?id={id}&") || url.EndsWith($"?id={id}");
    }

    // 步驟 8 的 messages 分頁診斷請求：count=5（完整值，避免誤中 9d-5 的 count=50）且 filter_drel=7days
    // （9d-4 的分頁量測也是 count=5，但固定 id=0&filter_drel=today，依規格保留，不屬於步驟 8）。
    private static bool IsStep8MessagesUrl(string url)
    {
        return url.Contains("content=messages") && url.Contains("&count=5&") && url.Contains("filter_drel=7days");
    }

    [Fact]
    public async Task RunAsync_步驟8_messages診斷使用樣本裝置且不含id0()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        var step8 = stub.RequestedUrls.Where(IsStep8MessagesUrl).ToList();
        Assert.NotEmpty(step8);
        Assert.All(step8, u => Assert.True(UrlHasId(u, 1), u));
        Assert.DoesNotContain(step8, u => UrlHasId(u, 0));
    }

    [Fact]
    public async Task RunAsync_挑樣本_三台以上裝置時有Down感測器排第一()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                    ""treesize"": 4,
                    ""sensors"": [
                        {""objid"": 101, ""parentid"": 1, ""status"": ""Up"", ""type"": ""ping"", ""unit"": ""ms""},
                        {""objid"": 102, ""parentid"": 2, ""status"": ""Down"", ""type"": ""ping"", ""unit"": ""ms""},
                        {""objid"": 103, ""parentid"": 3, ""status"": ""Up"", ""type"": ""ping"", ""unit"": ""ms""},
                        {""objid"": 104, ""parentid"": 4, ""status"": ""Up"", ""type"": ""ping"", ""unit"": ""ms""}
                    ]
                }"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(stub.RequestedUrls, u => IsStep8MessagesUrl(u) && UrlHasId(u, 2));
        Assert.DoesNotContain(stub.RequestedUrls, u => IsStep8MessagesUrl(u) && !UrlHasId(u, 2));
    }

    [Fact]
    public async Task RunAsync_樣本為空時_略過messages診斷與9d5且不擲例外()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 0, ""sensors"": []}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.DoesNotContain(stub.RequestedUrls, IsStep8MessagesUrl);
        Assert.Contains(console.Lines, l => l.Contains("messages 略過（沒有可用的裝置樣本）"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：略過（沒有可用的裝置樣本）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_b_含下層感測器訊息時印結論可用()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""messages"": [{""objid"": 101, ""datetime"": ""2026-09-18 10:00:00""}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取狀態變更") && l.Contains("✓ 含下層感測器訊息"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：結論 ✓ 逐裝置查詢狀態變更可用"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_b_只有裝置自身時印結論需改為逐感測器()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""messages"": [{""objid"": 1, ""datetime"": ""2026-09-18 10:00:00""}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取狀態變更") && l.Contains("✗ 只有裝置自身——狀態變更取數需改為逐感測器"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：結論 ✗ 逐裝置查詢只回裝置自身訊息，狀態變更取數需改為逐感測器"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_b_無資料時印無法判定()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""messages"": []}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取狀態變更") && l.Contains("無資料，無法判定"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：結論 無法判定（樣本裝置近 7 天都沒有狀態變更）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_b_回傳不屬於該裝置objid時警告()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""messages"": [{""objid"": 999, ""datetime"": ""2026-09-18 10:00:00""}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取狀態變更") && l.Contains("⚠ 回傳的 objid 不屬於該裝置（id 參數可能未生效）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_a_回傳含其他裝置感測器時標示錯誤()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 999}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取感測器") && l.Contains("✗ 回傳含其他裝置的感測器（id 參數未生效）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_e_分批取值回傳等量時判為可用()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                    ""treesize"": 2,
                    ""sensors"": [
                        {""objid"": 101, ""device"": ""A"", ""sensor"": ""Ping1"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1},
                        {""objid"": 102, ""device"": ""A"", ""sensor"": ""Ping2"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1}
                    ]
                }"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}, {""objid"": 102}]}"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid,lastvalue_raw"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101, ""lastvalue_raw"": 10}, {""objid"": 102, ""lastvalue_raw"": 20}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：filter_objid 分批取值") && l.Contains("✓ 分批取值可用"));
        var filterUrl = stub.RequestedUrls.FirstOrDefault(u => u.Contains("columns=objid,lastvalue_raw"));
        Assert.NotNull(filterUrl);
        var filterCount = filterUrl.Split("filter_objid=").Length - 1;
        Assert.Equal(2, filterCount);
    }

    [Fact]
    public async Task RunAsync_步驟9d5_e_分批取值少回時標示少回顆數()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                    ""treesize"": 2,
                    ""sensors"": [
                        {""objid"": 101, ""device"": ""A"", ""sensor"": ""Ping1"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1},
                        {""objid"": 102, ""device"": ""A"", ""sensor"": ""Ping2"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1}
                    ]
                }"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}, {""objid"": 102}]}"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid,lastvalue_raw"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101, ""lastvalue_raw"": 10}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：filter_objid 分批取值") && l.Contains("⚠ 少回 1 顆"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_e_分批取值回傳未要求感測器時警告()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid,lastvalue_raw"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 999, ""lastvalue_raw"": 10}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：filter_objid 分批取值") && l.Contains("⚠ 回傳了未要求的感測器（filter_objid 未生效）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_b_單台異常不影響其他台量測()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid,device,sensor,type,tags,unit"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                    ""treesize"": 2,
                    ""sensors"": [
                        {""objid"": 101, ""device"": ""A"", ""sensor"": ""Ping1"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 1},
                        {""objid"": 102, ""device"": ""B"", ""sensor"": ""Ping2"", ""type"": ""ping"", ""unit"": ""ms"", ""parentid"": 2}
                    ]
                }"));
            }
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000"))
            {
                if (UrlHasId(url, 1)) return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
                if (UrlHasId(url, 2)) return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 102}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50"))
            {
                if (UrlHasId(url, 1)) throw new HttpRequestException("連線失敗");
                if (UrlHasId(url, 2)) return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""messages"": [{""objid"": 102, ""datetime"": ""2026-09-18 10:00:00""}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 無法量測"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=2 逐裝置取狀態變更") && l.Contains("✓ 含下層感測器訊息"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_a無感測器時略過逐感測器量測()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": []}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：裝置 objid=1 逐裝置取感測器") && l.Contains("無法判定（沒有回傳任何感測器）"));
        Assert.Contains(console.Lines, l => l.Contains("9d-5：略過逐感測器量測（沒有可用的感測器）"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("filter_objid 分批取值"));
    }

    [Fact]
    public async Task RunAsync_步驟9d5_d_逐感測器取狀態變更輸出成功行()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("columns=objid&count=5000") && UrlHasId(url, 1))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101}]}"));
            }
            if (url.Contains("content=messages") && url.Contains("count=50") && UrlHasId(url, 101))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""messages"": [{""objid"": 101, ""datetime"": ""2026-09-18 10:00:00""}]}"));
            }
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub, PrtgAuthModes.Token, "", "", "", new LogForesight.Core.Service.PrtgRequestBudget());
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-5：感測器 objid=101 逐感測器取狀態變更") && l.Contains("回傳 1 筆"));
    }
}
