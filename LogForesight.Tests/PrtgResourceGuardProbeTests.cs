using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgResourceGuardProbeTests
{
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (RequestedUrls)
            {
                RequestedUrls.Add(url);
            }
            return await OnSend(request, cancellationToken);
        }
    }

    private static (PrtgClient Client, StubHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler
        {
            OnSend = (req, _) => Task.FromResult(responder(req))
        };
        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        return (client, handler);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1)
        {
            count++;
            i += pattern.Length;
        }
        return count;
    }

    [Fact]
    public async Task 一次帶完整批_給3個objid只有一次請求且URL包含三個filter_objid()
    {
        var json = """
        {
            "sensors": [
                { "objid": 2001, "device": "SRV-A", "sensor": "CPU", "status": "Up", "lastvalue": "50 %" },
                { "objid": 2002, "device": "SRV-B", "sensor": "CPU", "status": "Up", "lastvalue": "60 %" },
                { "objid": 2003, "device": "SRV-C", "sensor": "CPU", "status": "Up", "lastvalue": "70 %" }
            ]
        }
        """;
        var (client, handler) = CreateClient(_ => JsonResponse(json));
        var objids = new List<long> { 2001, 2002, 2003 };

        var result = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, objids);

        Assert.True(result.IsSuccess);
        Assert.Single(handler.RequestedUrls);
        var url = handler.RequestedUrls[0];
        Assert.Contains("content=sensors", url);
        Assert.Contains("columns=objid,device,sensor,status,lastvalue,lastvalue_raw", url);
        Assert.Contains("filter_objid=2001", url);
        Assert.Contains("filter_objid=2002", url);
        Assert.Contains("filter_objid=2003", url);
        Assert.Equal(3, result.Values.Count);
    }

    [Fact]
    public async Task 分批_給120個objid恰好3次請求_50_50_20()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{ "sensors": [] }"""));
        var objids = Enumerable.Range(1, 120).Select(i => (long)i).ToList();

        var result = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, objids);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.RequestedUrls.Count);
        Assert.Equal(50, CountOccurrences(handler.RequestedUrls[0], "filter_objid="));
        Assert.Equal(50, CountOccurrences(handler.RequestedUrls[1], "filter_objid="));
        Assert.Equal(20, CountOccurrences(handler.RequestedUrls[2], "filter_objid="));
    }

    [Fact]
    public async Task lastvalue_raw優先_同一列同時有lastvalue與lastvalue_raw取raw()
    {
        var json = """
        {
            "sensors": [
                {
                    "objid": 1001,
                    "device": "SRV-A",
                    "sensor": "CPU Load",
                    "status": "Up",
                    "lastvalue": "91 %",
                    "lastvalue_raw": "90.5"
                },
                {
                    "objid": 1002,
                    "device": "SRV-B",
                    "sensor": "CPU Load",
                    "status": "Up",
                    "lastvalue": "85 %",
                    "lastvalue_raw": 84.8
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => JsonResponse(json));
        var objids = new List<long> { 1001, 1002 };

        var result = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, objids);

        Assert.True(result.IsSuccess);
        Assert.Equal(90.5, result.Values[1001].Percentage);
        Assert.Equal(84.8, result.Values[1002].Percentage);
    }

    [Fact]
    public void 百分比字串解析_正確萃取百分比與識別非百分比()
    {
        Assert.Equal(92.0, PrtgResourceGuardProbe.ParsePercentage("92 %"));
        Assert.Equal(92.0, PrtgResourceGuardProbe.ParsePercentage("92%"));
        Assert.Equal(1234.0, PrtgResourceGuardProbe.ParsePercentage("1,234 %"));
        Assert.Equal(0.5, PrtgResourceGuardProbe.ParsePercentage("0.5 %"));

        Assert.Null(PrtgResourceGuardProbe.ParsePercentage("12 kbit/s"));
        Assert.Null(PrtgResourceGuardProbe.ParsePercentage("OK"));
        Assert.Null(PrtgResourceGuardProbe.ParsePercentage("-"));
        Assert.Null(PrtgResourceGuardProbe.ParsePercentage(null));
        Assert.Null(PrtgResourceGuardProbe.ParsePercentage(""));
        Assert.Null(PrtgResourceGuardProbe.ParsePercentage("<1 %"));
    }

    [Fact]
    public void CPU超標_值91門檻85判定超標且說明含device與sensor名稱()
    {
        var categories = new Dictionary<long, string> { [1001] = PrtgSensorCategories.Cpu };
        var values = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1001] = new(1001, "SRV-A", "CPU Load", "Up", 91.0)
        };
        var probeResult = new PrtgResourceGuardProbeResult(values, IsSuccess: true);
        var settings = new SystemSettings { PrtgResourceGuardCpuPercent = 85 };

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.True(eval.IsOverloaded);
        Assert.True(eval.HasMeasurableSensors);
        Assert.NotNull(eval.TriggeredSensorDescription);
        Assert.Contains("SRV-A", eval.TriggeredSensorDescription);
        Assert.Contains("CPU Load", eval.TriggeredSensorDescription);
        Assert.Contains("91", eval.TriggeredSensorDescription);
    }

    [Fact]
    public void 記憶體是越低越糟_值5門檻10超標_值50門檻10不超標()
    {
        var categories = new Dictionary<long, string> { [1002] = PrtgSensorCategories.Memory };
        var settings = new SystemSettings { PrtgResourceGuardMemoryFreePercent = 10 };

        // 情況 A：可用記憶體 5% <= 10% → 超標
        var valuesOverloaded = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1002] = new(1002, "SRV-B", "Memory Free", "Up", 5.0)
        };
        var evalOverloaded = PrtgResourceGuardProbe.Evaluate(
            categories,
            new PrtgResourceGuardProbeResult(valuesOverloaded, IsSuccess: true),
            settings);
        Assert.True(evalOverloaded.IsOverloaded);
        Assert.True(evalOverloaded.HasMeasurableSensors);
        Assert.Contains("SRV-B", evalOverloaded.TriggeredSensorDescription);
        Assert.Contains("Memory Free", evalOverloaded.TriggeredSensorDescription);

        // 情況 B：可用記憶體 50% > 10% → 不超標
        var valuesNormal = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1002] = new(1002, "SRV-B", "Memory Free", "Up", 50.0)
        };
        var evalNormal = PrtgResourceGuardProbe.Evaluate(
            categories,
            new PrtgResourceGuardProbeResult(valuesNormal, IsSuccess: true),
            settings);
        Assert.False(evalNormal.IsOverloaded);
        Assert.True(evalNormal.HasMeasurableSensors);
        Assert.Null(evalNormal.TriggeredSensorDescription);
    }

    [Fact]
    public void 狀態非Up的sensor被忽略_值99但狀態為Down不超標()
    {
        var categories = new Dictionary<long, string> { [1001] = PrtgSensorCategories.Cpu };
        var values = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1001] = new(1001, "SRV-A", "CPU Load", "Down", 99.0)
        };
        var probeResult = new PrtgResourceGuardProbeResult(values, IsSuccess: true);
        var settings = new SystemSettings { PrtgResourceGuardCpuPercent = 85 };

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.False(eval.IsOverloaded);
        Assert.False(eval.HasMeasurableSensors);
        Assert.Null(eval.TriggeredSensorDescription);
        Assert.Single(eval.Details);
        Assert.Equal(PrtgResourceGuardIgnoredReason.StatusNotUp, eval.Details[0].IgnoredReason);
        Assert.False(eval.Details[0].IsMeasurable);
    }

    [Fact]
    public void 完全沒量到時不超標_所有sensor都回非百分比不超標且可辨識沒量到()
    {
        var categories = new Dictionary<long, string>
        {
            [1001] = PrtgSensorCategories.Cpu,
            [1002] = PrtgSensorCategories.Memory
        };
        var values = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1001] = new(1001, "SRV-A", "Traffic", "Up", null, PrtgResourceGuardUnmeasurableReasons.NonPercentage),
            [1002] = new(1002, "SRV-B", "Status Message", "Up", null, PrtgResourceGuardUnmeasurableReasons.NonPercentage)
        };
        var probeResult = new PrtgResourceGuardProbeResult(values, IsSuccess: true);
        var settings = new SystemSettings();

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.False(eval.IsOverloaded);
        Assert.False(eval.HasMeasurableSensors);
        Assert.Null(eval.TriggeredSensorDescription);
        Assert.All(eval.Details, d => Assert.Equal(PrtgResourceGuardIgnoredReason.NonPercentage, d.IgnoredReason));
    }

    [Fact]
    public async Task API失敗不擲例外_回無法取值不超標不擲例外()
    {
        var (client, _) = CreateClient(_ => JsonResponse("Internal Server Error", HttpStatusCode.InternalServerError));
        var objids = new List<long> { 1001 };
        var settings = new SystemSettings();
        var categories = new Dictionary<long, string> { [1001] = PrtgSensorCategories.Cpu };

        var probeResult = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, objids);

        Assert.False(probeResult.IsSuccess);
        Assert.Equal(PrtgResourceGuardUnmeasurableReasons.ApiFailed, probeResult.FailureReason);
        Assert.Equal(PrtgResourceGuardUnmeasurableReasons.ApiFailed, probeResult.Values[1001].UnmeasurableReason);

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.False(eval.IsOverloaded);
        Assert.False(eval.HasMeasurableSensors);
        Assert.Null(eval.TriggeredSensorDescription);
    }

    [Fact]
    public async Task 取消會穿透_已取消的CancellationToken擲OperationCanceledException()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{ "sensors": [] }"""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrtgResourceGuardProbe.FetchSensorValuesAsync(client, new List<long> { 1001 }, cts.Token));
    }

    [Fact]
    public void CoreHealth健康度越低越糟_門檻沿用可用記憶體門檻()
    {
        var categories = new Dictionary<long, string> { [1003] = PrtgResourceGuardTargets.CategoryCoreHealth };
        var settings = new SystemSettings { PrtgResourceGuardMemoryFreePercent = 10 };

        // 核心健康度 5% <= 10% → 超標
        var valuesLow = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1003] = new(1003, "PRTG Core", "Core Health", "Up", 5.0)
        };
        var evalLow = PrtgResourceGuardProbe.Evaluate(
            categories,
            new PrtgResourceGuardProbeResult(valuesLow, IsSuccess: true),
            settings);
        Assert.True(evalLow.IsOverloaded);

        // 核心健康度 100% > 10% → 不超標
        var valuesHigh = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1003] = new(1003, "PRTG Core", "Core Health", "Up", 100.0)
        };
        var evalHigh = PrtgResourceGuardProbe.Evaluate(
            categories,
            new PrtgResourceGuardProbeResult(valuesHigh, IsSuccess: true),
            settings);
        Assert.False(evalHigh.IsOverloaded);
    }

    [Fact]
    public void 分類為unknown一律忽略不參與判定()
    {
        var categories = new Dictionary<long, string> { [1004] = PrtgResourceGuardTargets.CategoryUnknown };
        var values = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            [1004] = new(1004, "SRV-A", "Custom Ping", "Up", 100.0)
        };
        var probeResult = new PrtgResourceGuardProbeResult(values, IsSuccess: true);
        var settings = new SystemSettings { PrtgResourceGuardCpuPercent = 85 };

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.False(eval.IsOverloaded);
        Assert.False(eval.HasMeasurableSensors);
        Assert.Equal(PrtgResourceGuardIgnoredReason.UnknownCategory, eval.Details[0].IgnoredReason);
    }

    [Fact]
    public async Task 回應中遺失sensor_補上NotFound標記且判定為忽略()
    {
        var json = """
        {
            "sensors": [
                { "objid": 2001, "device": "SRV-A", "sensor": "CPU", "status": "Up", "lastvalue": "50 %" }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => JsonResponse(json));
        var objids = new List<long> { 2001, 9999 }; // 9999 不在 PRTG 回應中

        var probeResult = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, objids);

        Assert.True(probeResult.IsSuccess);
        Assert.Equal(PrtgResourceGuardUnmeasurableReasons.NotFound, probeResult.Values[9999].UnmeasurableReason);

        var categories = new Dictionary<long, string>
        {
            [2001] = PrtgSensorCategories.Cpu,
            [9999] = PrtgSensorCategories.Cpu
        };
        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, new SystemSettings());

        var notFoundDetail = eval.Details.First(d => d.Objid == 9999);
        Assert.False(notFoundDetail.IsMeasurable);
        Assert.Equal(PrtgResourceGuardIgnoredReason.NotFound, notFoundDetail.IgnoredReason);
    }

    [Fact]
    public void 呼叫端能明確區分三種無法判定之情況()
    {
        var categories = new Dictionary<long, string>
        {
            [1001] = PrtgSensorCategories.Cpu,
            [1002] = PrtgSensorCategories.Cpu,
            [1003] = PrtgSensorCategories.Cpu
        };
        var values = new Dictionary<long, PrtgResourceGuardSensorValue>
        {
            // 情況 1：查無此 sensor
            [1001] = new(1001, null, null, null, null, PrtgResourceGuardUnmeasurableReasons.NotFound),
            // 情況 2：狀態非 Up
            [1002] = new(1002, "SRV-B", "CPU", "Warning", 95.0),
            // 情況 3：值不是百分比
            [1003] = new(1003, "SRV-C", "CPU", "Up", null, PrtgResourceGuardUnmeasurableReasons.NonPercentage)
        };
        var probeResult = new PrtgResourceGuardProbeResult(values, IsSuccess: true);
        var settings = new SystemSettings();

        var eval = PrtgResourceGuardProbe.Evaluate(categories, probeResult, settings);

        Assert.False(eval.IsOverloaded);
        Assert.False(eval.HasMeasurableSensors);

        var d1 = eval.Details.First(d => d.Objid == 1001);
        var d2 = eval.Details.First(d => d.Objid == 1002);
        var d3 = eval.Details.First(d => d.Objid == 1003);

        Assert.Equal(PrtgResourceGuardIgnoredReason.NotFound, d1.IgnoredReason);
        Assert.Equal(PrtgResourceGuardIgnoredReason.StatusNotUp, d2.IgnoredReason);
        Assert.Equal(PrtgResourceGuardIgnoredReason.NonPercentage, d3.IgnoredReason);
    }
}
