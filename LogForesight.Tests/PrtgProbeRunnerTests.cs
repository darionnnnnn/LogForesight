using System.Net;
using System.Text;
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
                if (url.Contains("content=devices") && url.Contains("count=1"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": ""10"", ""devices"": [{""objid"": 1}]}"));
                }
                if (url.Contains("content=sensors") && url.Contains("count=1"))
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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
                if (url.Contains("content=devices") && url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""devices"": []}"));
                if (url.Contains("content=sensors") && url.Contains("count=1"))
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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
                if (url.Contains("content=devices") && url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 5, ""devices"": []}"));
                if (url.Contains("content=sensors") && url.Contains("count=1"))
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
                            {""objid"": 5, ""device"": ""Host5"", ""host"": """" }
                        ]
                    }"));
                }

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);

        Assert.Contains(console.Lines, l => l.Contains("有設定 host 值的 Device 數：4"));
        Assert.Contains(console.Lines, l => l.Contains("其中為 IPv4 位址者：2 台"));
        Assert.Contains(console.Lines, l => l.Contains("其中非 IPv4（DNS 名稱或其它）者：2 台"));
    }

    [Fact]
    public async Task RunAsync_dependency欄位不支援時跳過但不算失敗()
    {
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""20.1""}"));
                if (url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 1, ""devices"": [], ""sensors"": []}"));
                if (url.Contains("columns=objid,device,sensor,type,tags,unit"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1, ""type"": ""ping""}]}"));
                if (url.Contains("columns=objid,dependency"))
                {
                    // 模擬 PRTG 舊版不支援 dependency 欄位回傳 HTTP 400 Bad Request
                    return Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, @"{""error"": ""Invalid column: dependency""}"));
                }
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""devices"": []}"));

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "即使 dependency 欄位不支援，整個 probe 仍應回 true");
        Assert.Contains(console.Lines, l => l.Contains("此 PRTG 版本不支援 dependency 欄位查詢"));
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
                if (url.Contains("count=1"))
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.False(result);
        Assert.Contains(console.Lines, l => l.Contains("失敗：連線 PRTG 伺服器失敗"));
        Assert.Contains(console.Lines, l => l.Contains("探測終止（連線或認證失敗）"));
    }
}
