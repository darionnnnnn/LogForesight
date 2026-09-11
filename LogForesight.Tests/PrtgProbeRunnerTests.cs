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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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
    public async Task RunAsync_unit樣本可自lastvalue推導且輸出TypeIPv4交叉統計()
    {
        // PRTG sensors 表沒有 unit 欄位：樣本只給 lastvalue，unit 應由 lastvalue 萃取。
        // 純文字狀態（OK）與「-」不得被當成單位。
        // parentid：101/102 → device 1（IPv4），103 → device 2（DNS）。
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/api/status.json"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""prtg-version"": ""24.1""}"));
                if (url.Contains("content=devices") && url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 2, ""devices"": []}"));
                if (url.Contains("content=sensors") && url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 4, ""sensors"": []}"));
                if (url.Contains("columns=objid,device,sensor,type,tags,unit,lastvalue,parentid"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 4,
                        ""sensors"": [
                            {""objid"": 101, ""type"": ""ping"", ""lastvalue"": ""12 msec"", ""parentid"": 1},
                            {""objid"": 102, ""type"": ""ping"", ""lastvalue"": ""<1 ms"", ""parentid"": 1},
                            {""objid"": 103, ""type"": ""ping"", ""lastvalue"": ""-"", ""parentid"": 2},
                            {""objid"": 104, ""type"": ""http"", ""lastvalue"": ""OK"", ""parentid"": 1}
                        ]
                    }"));
                }
                if (url.Contains("columns=objid,dependency"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 101, ""dependency"": ""200""}]}"));
                if (url.Contains("content=groups"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""groups"": []}"));
                if (url.Contains("content=devices") && url.Contains("columns=objid,device,host,group"))
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{
                        ""treesize"": 2,
                        ""devices"": [
                            {""objid"": 1, ""device"": ""Router"", ""host"": ""192.168.1.1""},
                            {""objid"": 2, ""device"": ""WebServer"", ""host"": ""web.corp.local""}
                        ]
                    }"));
                }

                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
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

        // unit 樣本自 lastvalue 推導：ping 應含 msec 與 ms；http 的 OK 不是單位 → 無
        var pingLine = console.Lines.First(l => l.Contains("ping | 3 |"));
        Assert.Contains("msec", pingLine);
        Assert.Contains("ms", pingLine);
        var httpLine = console.Lines.First(l => l.Contains("http | 1 |"));
        Assert.Contains("unit 樣本：無", httpLine);

        // dependency 預設值註記
        Assert.Contains(console.Lines, l => l.Contains("此比例含預設值"));

        // 步驟 7：type × IPv4 交叉統計（ping：2/3 在 IPv4 device 上；http：1/1）
        Assert.Contains(console.Lines, l => l.Contains("Type × IPv4 覆蓋"));
        Assert.Contains(console.Lines, l => l.Contains("ping | 2/3 | 66.7%"));
        Assert.Contains(console.Lines, l => l.Contains("http | 1/1 | 100.0%"));
    }

    [Fact]
    public async Task RunAsync_無parentid資料時步驟7略過但不算失敗()
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
                if (url.Contains("columns=objid,device,sensor,type,tags,unit,lastvalue,parentid"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1, ""type"": ""ping""}]}"));
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

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("略過此統計"));
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

                if (url.Contains("content=devices") && url.Contains("count=1"))
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"treesize\": {deviceTreesize}, \"devices\": [{{\"objid\": 1}}]}}"));
                if (url.Contains("content=sensors") && url.Contains("count=1"))
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("devices：✓ 遵守 start，超出範圍回空頁"));
        Assert.Contains(console.Lines, l => l.Contains("sensors：✓ 遵守 start"));
        Assert.Contains(console.Lines, l => l.Contains("messages：✓ 遵守 start"));
        // messages 的診斷必須帶相對日期過濾，否則是整台訊息歷史的查詢
        Assert.Contains(stub.RequestedUrls, u => u.Contains("content=messages") && u.Contains("count=5&start=0") && u.Contains("filter_drel=7days"));
    }

    [Fact]
    public async Task RunAsync_步驟8_完全忽略start時判為只能單次大count()
    {
        var stub = BuildPagingStub((_, _) => new long[] { 1, 2, 3, 4, 5 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "步驟 8 是診斷，結論不影響探測成敗");
        Assert.Contains(console.Lines, l => l.Contains("devices：✗ 完全忽略 start"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices：⚠ 遵守 start，但超出範圍時回到第一頁"));
        Assert.Contains(console.Lines, l => l.Contains("sensors：⚠ 遵守 start，但超出範圍時夾到最後一頁"));
    }

    [Fact]
    public async Task RunAsync_步驟8_單次查詢失敗只印原因不算探測失敗()
    {
        var stub = BuildPagingStub((content, _) => content == "messages"
            ? throw new HttpRequestException("messages 端點 500")
            : new long[] { 1, 2 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("messages：無法判定（"));
        Assert.Contains(console.Lines, l => l.Contains("devices：總筆數不足 5 筆"));
    }

    [Fact]
    public async Task RunAsync_步驟8_預設順序不穩定但sortby有效時判為必須帶sortby()
    {
        // 實機（24.1.92）的 messages 同頁內 objid 不遞增，這是分頁漏列的來源
        var stub = BuildPagingStub(
            (_, start) => start == 0 ? new long[] { 59590, 82114, 56991, 85029, 57288 } : new long[] { 87261, 59520 },
            sortedResponder: _ => new long[] { 1001, 1002, 1003, 1004, 1005 });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices：頁內 objid 遞增＝否") && l.Contains("遞增＝是"));
        Assert.Contains(console.Lines, l => l.Contains("devices：✓ sortby=objid 有效"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result, "排序診斷不影響探測成敗");
        Assert.Contains(console.Lines, l => l.Contains("sensors：✗ sortby=objid 無效，且預設順序非遞增"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("devices：✓ 預設順序已遞增，sortby=objid 不改變結果"));
    }

    /// <summary>
    /// 沒設 dependency 的 sensor 這一欄是缺的。mapper 若把它當損壞列剔除，分母會縮水，
    /// 截斷警告就會在明明沒截斷時誤報——探測輸出裡出現一句錯的警告比沒有警告更糟。
    /// </summary>
    [Fact]
    public async Task RunAsync_步驟4_sensor沒有dependency欄位時不誤報截斷()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("count=1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{""treesize"": 3, ""sensors"": [{""objid"": 10}]}"));
            if (url.Contains("columns=objid,dependency"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    @"{""treesize"": 3, ""sensors"": [{""objid"": 1, ""dependency"": ""200""}, {""objid"": 2}, {""objid"": 3}]}"));
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("有設定相依性的 Sensor 數：1 / 3"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("僅取樣到") && l.Contains("下列比例僅供參考"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("群組總數為 1500，本次查詢僅取樣到 2 筆"));
    }

    [Fact]
    public async Task RunAsync_步驟6_device大count取樣少於treesize時警告截斷()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>(), deviceTreesize: 10, deviceRows: 3);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("警告：Device 總數為 10 筆，本次查詢僅取樣到 3 筆"));
    }

    [Fact]
    public async Task RunAsync_步驟6_treesize小於實際筆數時指出treesize不是總筆數()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>(), deviceTreesize: 2, deviceRows: 4);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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
                if (url.Contains("count=1"))
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

    [Fact]
    public async Task RunAsync_步驟9a_四個count都發出並印每千筆與結論()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
        {
            if (!url.Contains("columns=objid,parentid,sensor,type,tags,unit,status,paused,dependency")) return null;
            var count = CountFromUrl(url);
            var rows = Enumerable.Range(1, count).Select(i => $"{{\"objid\": {i}}}");
            return JsonResponse(HttpStatusCode.OK, "{\"sensors\": [" + string.Join(",", rows) + "]}");
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9a") || l.Contains("9b") || l.Contains("9c")))
            _output.WriteLine(line);

        Assert.True(result);
        foreach (var count in new[] { 500, 2500, 5000, 50000 })
        {
            Assert.Contains(stub.RequestedUrls, u => u.Contains("columns=objid,parentid,sensor,type,tags,unit,status,paused,dependency") && u.Contains($"count={count}&start=0"));
            Assert.Single(console.Lines, l => l.Contains($"9a：count={count} →"));
        }
        Assert.Equal(4, console.Lines.Count(l => l.Contains("9a：count=")));
        Assert.Single(console.Lines, l => l.Contains("9a：") && !l.Contains("9a：count="));
    }

    [Fact]
    public async Task RunAsync_步驟9c_無可用感測器時印略過且探測仍回true()
    {
        var stub = BuildPerfStub(SensorRows(3, status: "Paused"));

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("無可用感測器，略過"));
    }

    [Fact]
    public async Task RunAsync_步驟9c_樣本8顆時印分配警告且四級各一行()
    {
        var stub = BuildPerfStub(SensorRows(8), url => url.Contains("/api/historicdata.json")
            ? JsonResponse(HttpStatusCode.OK, @"{""histdata"": [{""datetime"": ""2026-09-10 00:00:00"", ""value"": ""1""}]}")
            : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9c")))
            _output.WriteLine(line);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("可用感測器只有 8 顆"));
        Assert.Equal(4, console.Lines.Count(l => l.Contains("9c：併發 ") && l.Contains("→ 總耗時")));
        Assert.Equal(3, console.Lines.Count(l => l.Contains("相對併發 1：總耗時 ×")));
        Assert.Contains(stub.RequestedUrls, u => u.Contains("/api/historicdata.json") && u.Contains("avg=3600") && u.Contains("sdate="));
    }

    [Fact]
    public async Task RunAsync_步驟9c_單顆historicdata失敗計入失敗數不中斷()
    {
        // objid 1~8 依序分成 [1,2]／[3,4]／[5,6]／[7,8]：objid 5 落在併發 4 那一級
        var stub = BuildPerfStub(SensorRows(8), url =>
        {
            if (!url.Contains("/api/historicdata.json")) return null;
            if (url.Contains("id=5&")) return JsonResponse(HttpStatusCode.InternalServerError, @"{""error"": ""boom""}");
            return JsonResponse(HttpStatusCode.OK, @"{""histdata"": [{""value"": ""1""}]}");
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9c")))
            _output.WriteLine(line);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9c：併發 4 → ") && l.Contains("失敗 1"));
    }

    [Fact]
    public async Task RunAsync_步驟9a_全欄位50000失敗時9b仍印自己的數字()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
        {
            if (url.Contains("columns=objid,parentid,") && url.Contains("count=50000"))
                return JsonResponse(HttpStatusCode.InternalServerError, @"{""error"": ""boom""}");
            if (url.Contains("columns=objid,parentid,"))
            {
                var count = CountFromUrl(url);
                var rows = Enumerable.Range(1, count).Select(i => $"{{\"objid\": {i}}}");
                return JsonResponse(HttpStatusCode.OK, "{\"sensors\": [" + string.Join(",", rows) + "]}");
            }
            if (url.Contains("columns=objid&count=50000"))
                return JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1}]}");
            return null;
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9a") || l.Contains("9b")))
            _output.WriteLine(line);

        Assert.True(result);
        var objidOnly = console.Lines.Single(l => l.Contains("9b：objid-only"));
        Assert.DoesNotContain("%", objidOnly);
    }
    /// <summary>
    /// 相依性查詢回非 JSON（錯誤頁、登入頁）時，只印「無法解析」看不出回了什麼；
    /// 長度與開頭是實機唯一能判斷對方到底回什麼的線索。
    /// </summary>
    [Fact]
    public async Task RunAsync_步驟4_回應無法解析時印長度與開頭()
    {
        var stub = BuildPagingStub((_, _) => Array.Empty<long>());
        var inner = stub.OnSend;
        stub.OnSend = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("columns=objid,dependency"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "<html>error</html>"));
            return inner(req, ct);
        };

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("回應無法解析：長度") && l.Contains("bytes"));
        Assert.Contains(console.Lines, l => l.Contains("開頭：<html>"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("messages：")))
            _output.WriteLine(line);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("messages：頁內 datetime 單調＝是（遞減）"));
        Assert.Contains(console.Lines, l => l.Contains("messages：✓ 依時間排序、順序穩定，分頁可行（列鍵含時間）"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("messages：✗ sortby=objid 無效"));
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

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("messages：頁內 datetime 單調＝否（不單調）"));
        Assert.Contains(console.Lines, l => l.Contains("messages：⚠ 頁內順序不依時間也不依 objid，分頁可能漏列"));
    }

    [Fact]
    public async Task RunAsync_步驟9d1_印快照成本與三種分布()
    {
        const string snapshot = @"{""sensors"": [
            {""objid"": 1, ""status"": ""Up"", ""interval"": ""60"", ""lastvalue"": ""1 ms"", ""lastvalue_raw"": ""1.0""},
            {""objid"": 2, ""status"": ""Up"", ""interval"": ""60"", ""lastvalue"": ""2 ms"", ""lastvalue_raw"": ""2.0""},
            {""objid"": 3, ""status"": ""Up"", ""interval"": ""60"", ""lastvalue"": ""3 ms"", ""lastvalue_raw"": ""3.0""},
            {""objid"": 4, ""status"": ""Up"", ""interval"": ""60"", ""lastvalue"": ""4 ms"", ""lastvalue_raw"": ""4.0""},
            {""objid"": 5, ""status"": ""Down"", ""interval"": ""300"", ""lastvalue"": ""5 ms"", ""lastvalue_raw"": ""5.0""},
            {""objid"": 6, ""status"": ""Down"", ""interval"": ""300"", ""lastvalue"": ""OK"", ""lastvalue_raw"": ""abc""}
        ]}";

        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
            url.Contains("columns=objid,status,interval,lastcheck,lastvalue,lastvalue_raw")
                ? JsonResponse(HttpStatusCode.OK, snapshot)
                : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9d-")))
            _output.WriteLine(line);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-1：快照 耗時 ") && l.Contains("回傳 6 筆") && l.Contains(" bytes"));
        Assert.Contains(console.Lines, l => l.Contains("9d-1：interval 分布（前 5）：60×4（66.7%）、300×2（33.3%）"));
        Assert.Contains(console.Lines, l => l.Contains("9d-1：status 分布（前 5）：Up×4（66.7%）、Down×2（33.3%）"));
        Assert.Contains(console.Lines, l => l.Contains("9d-1：lastvalue_raw 可解析為數字：5/6 筆（83.3%）"));
    }

    [Fact]
    public async Task RunAsync_步驟9d1_無interval欄位時印欄位不可用()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
            url.Contains("columns=objid,status,interval,lastcheck,lastvalue,lastvalue_raw")
                ? JsonResponse(HttpStatusCode.OK, @"{""sensors"": [{""objid"": 1, ""status"": ""Up""}]}")
                : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("9d-1：interval 分布（前 5）：interval 欄位不可用"));
    }

    /// <summary>指定各 type 的筆數，objid 依序編號（供 9c／9d 的不重複挑選測試）。</summary>
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

    private static List<long> HistoricIdsFromLines(List<string> lines, string marker)
        => lines.Where(l => l.Contains(marker) && l.Contains("objid="))
                .Select(l => long.Parse(l.Split("objid=")[1].Split(' ')[0]))
                .ToList();

    [Fact]
    public async Task RunAsync_步驟9d2_五種type各挑3顆且不與9c重複()
    {
        // 前 64 顆 Ping 會被 9c 用掉；9d-2 只能挑到 objid > 64 的樣本
        var step3 = TypedSensorRows(
            ("Ping", 69),
            ("SNMP CPU Load", 5),
            ("SNMP Memory", 5),
            ("SNMP Disk Free", 5),
            ("SNMP Traffic 64bit", 5));

        var stub = BuildPerfStub(step3, url => url.Contains("/api/historicdata.json")
            ? JsonResponse(HttpStatusCode.OK, @"{""histdata"": [{""datetime"": ""2026-09-10 23:00:00"", ""value"": ""1""}, {""datetime"": ""2026-09-11 00:00:00"", ""value"": ""2""}]}")
            : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9d-2")))
            _output.WriteLine(line);

        Assert.True(result);
        foreach (var type in new[] { "SNMP CPU Load", "SNMP Memory", "SNMP Disk Free", "SNMP Traffic 64bit", "Ping" })
        {
            Assert.Equal(3, console.Lines.Count(l => l.Contains($"9d-2：{type} objid=")));
            Assert.Single(console.Lines, l => l.Contains($"9d-2：{type} 平均延遲 ") && l.Contains("（3 顆）"));
        }

        var ids9d2 = HistoricIdsFromLines(console.Lines, "9d-2：");
        Assert.Equal(15, ids9d2.Count);
        Assert.Equal(15, ids9d2.Distinct().Count());
        // 9c 用掉 objid 1~64（Ping 依 type 優先序排在最前面）
        Assert.DoesNotContain(ids9d2, id => id <= 64);
        Assert.Contains(console.Lines, l => l.Contains("histdata 末列：") && l.Contains("快照 lastvalue=「"));
    }

    [Fact]
    public async Task RunAsync_步驟9d2_某type無樣本時印該type無樣本()
    {
        var stub = BuildPerfStub(TypedSensorRows(("Ping", 3)), url => url.Contains("/api/historicdata.json")
            ? JsonResponse(HttpStatusCode.OK, @"{""histdata"": []}")
            : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("9d-2：SNMP CPU Load 該 type 無樣本"));
    }

    [Fact]
    public async Task RunAsync_步驟9d3_一小時與一天各四顆並印結論()
    {
        var step3 = TypedSensorRows(("Ping", 80));
        var stub = BuildPerfStub(step3, url => url.Contains("/api/historicdata.json")
            ? JsonResponse(HttpStatusCode.OK, @"{""histdata"": [{""value"": ""1""}]}")
            : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        foreach (var line in console.Lines.Where(l => l.Contains("9d-3")))
            _output.WriteLine(line);

        Assert.True(result);
        var hourSdate = "sdate=" + DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd-23-00-00");
        var daySdate = "sdate=" + DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd-00-00-00");
        var edate = "edate=" + DateTime.Today.ToString("yyyy-MM-dd-00-00-00");
        Assert.Equal(4, stub.RequestedUrls.Count(u => u.Contains("/api/historicdata.json") && u.Contains(hourSdate) && u.Contains(edate)));
        // 9c 64 顆＋9d-2 的 3 顆 Ping＋9d-3 的 4 顆 1 天查詢
        Assert.Equal(71, stub.RequestedUrls.Count(u => u.Contains("/api/historicdata.json") && u.Contains(daySdate)));
        Assert.Contains(console.Lines, l => l.Contains("9d-3：1 小時 平均延遲 ") && l.Contains("；1 天 平均延遲 "));
        Assert.Contains(console.Lines, l => l.Contains("9d-3：✓ historicdata 成本以每次呼叫為主") || l.Contains("9d-3：⚠ 成本隨時間跨度成長"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("9d-3：⚠ 可用 Ping 感測器只有"));
    }

    [Fact]
    public async Task RunAsync_步驟9d4_印messages量級()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
        {
            if (!url.Contains("content=messages") || !url.Contains("count=1&id=0&filter_drel=")) return null;
            return url.Contains("filter_drel=today")
                ? JsonResponse(HttpStatusCode.OK, @"{""treesize"": 12, ""messages"": []}")
                : JsonResponse(HttpStatusCode.OK, @"{""treesize"": 345, ""messages"": []}");
        });

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-4：messages treesize today=12、7days=345"));
    }

    [Fact]
    public async Task RunAsync_步驟9d_量測失敗只印原因不影響回傳值()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}", url =>
            url.Contains("columns=objid,status,interval,lastcheck,lastvalue,lastvalue_raw")
                ? JsonResponse(HttpStatusCode.InternalServerError, @"{""error"": ""boom""}")
                : null);

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        var result = await PrtgProbeRunner.RunAsync(client, console);

        Assert.True(result);
        Assert.Contains(console.Lines, l => l.Contains("9d-1：無法量測（"));
        Assert.Contains(console.Lines, l => l.Contains("9d-4：messages treesize"));
    }

    [Fact]
    public async Task RunAsync_步驟9_成本行標示87次historicdata與9次tablejson()
    {
        var stub = BuildPerfStub(@"{""sensors"": []}");

        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        var console = new TestConsole();
        await PrtgProbeRunner.RunAsync(client, console);

        Assert.Contains(console.Lines, l => l.Contains("本步驟會發 87 次 historicdata 與 9 次 table.json"));
    }
}
