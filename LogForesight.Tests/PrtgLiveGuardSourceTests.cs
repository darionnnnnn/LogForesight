using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 直接查 PRTG 的守門來源（docs/PRTG-SPEC.md §12）：單次取回、欄位映射、截斷警告。
/// 這條路徑跑在使用者按下「自動偵測並填入」時，取不齊的症狀是「未偵測到任何受監看的感測器」，
/// 與「PRTG 上真的沒有」看起來一模一樣——所以取不齊一定要出聲。
/// </summary>
public class PrtgLiveGuardSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnSend { get; set; } =
            _ => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestedUrls) RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(OnSend(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (PrtgClient Client, StubHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler { OnSend = responder };
        return (new PrtgClient("https://prtg.example.com", "token123", 30, true, handler), handler);
    }

    private static string DevicePage(int start, int count, int? treesize = null) =>
        "{" + (treesize.HasValue ? $"\"treesize\":{treesize.Value}," : "") + "\"devices\":[" + string.Join(",",
            Enumerable.Range(start, count).Select(i =>
                $"{{\"objid\":{1000 + i},\"device\":\"DEV-{i}\",\"host\":\"10.0.0.{i % 250}\"}}")) + "]}";

    private static string SensorPage(int count, int? treesize = null) =>
        "{" + (treesize.HasValue ? $"\"treesize\":{treesize.Value}," : "") + "\"sensors\":[" + string.Join(",",
            Enumerable.Range(0, count).Select(i =>
                $"{{\"objid\":{20000 + i},\"parentid\":1001,\"sensor\":\"S-{i}\",\"type\":\"Ping\",\"status\":\"Up\"}}")) + "]}";

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    [Fact]
    public void GetDevices_單頁時直接回傳並映射欄位()
    {
        var (client, handler) = CreateClient(_ => Json(
            "{\"devices\":[{\"objid\":1001,\"device\":\"SRV-A\",\"host\":\"10.1.1.1:8080\"}]}"));

        using (client)
        {
            var source = new PrtgLiveGuardSource(client);
            var devices = source.GetDevices();

            var d = Assert.Single(devices);
            Assert.Equal(1001, d.Objid);
            Assert.Equal("SRV-A", d.Name);
            // 原始字串照收，正規化是比對時才做（鏡像也是這個規則）
            Assert.Equal("10.1.1.1:8080", d.Ip);
            Assert.Single(handler.RequestedUrls);
            Assert.Contains("content=devices", handler.RequestedUrls[0]);
        }
    }

    [Fact]
    public void GetDevices_一次取回全部且不分頁()
    {
        var (client, handler) = CreateClient(_ => Json(DevicePage(0, 503, treesize: 503)));

        using (client)
        {
            var console = new TestConsole();
            var devices = new PrtgLiveGuardSource(client, console: console).GetDevices();

            Assert.Equal(503, devices.Count);
            var url = Assert.Single(handler.RequestedUrls);
            Assert.Contains("count=50000", url);
            Assert.DoesNotContain("start=", url);
            Assert.Empty(console.Lines);
        }
    }

    /// <summary>
    /// 正式環境有 42864 個感測器。取不齊時若不出聲，落在後段的感測器一律顯示「未偵測到」，
    /// 使用者只會以為 PRTG 上沒有那些感測器。
    /// </summary>
    [Fact]
    public void GetSensors_取到的筆數少於treesize時發出截斷警告()
    {
        var (client, _) = CreateClient(_ => Json(SensorPage(25000, treesize: 42864)));

        using (client)
        {
            var console = new TestConsole();
            var sensors = new PrtgLiveGuardSource(client, console: console).GetSensors();

            Assert.Equal(25000, sensors.Count);   // 取到的照樣回傳，不是全有或全無
            var warning = Assert.Single(console.Lines);
            Assert.Contains("25000", warning);
            Assert.Contains("42864", warning);
            Assert.Contains("覆寫清單", warning);
        }
    }

    [Fact]
    public void GetDevices_無treesize且剛好取到上限時視為可能截斷()
    {
        var (client, _) = CreateClient(_ => Json(DevicePage(0, 50000)));

        using (client)
        {
            var console = new TestConsole();
            new PrtgLiveGuardSource(client, console: console).GetDevices();

            var warning = Assert.Single(console.Lines);
            Assert.Contains("總數 未知", warning);
        }
    }

    [Fact]
    public void GetDevices_取滿treesize時不發警告()
    {
        var (client, _) = CreateClient(_ => Json(DevicePage(0, 10, treesize: 10)));

        using (client)
        {
            var console = new TestConsole();
            new PrtgLiveGuardSource(client, console: console).GetDevices();

            Assert.Empty(console.Lines);
        }
    }

    [Fact]
    public void GetSensors_映射欄位並判斷暫停()
    {
        var (client, _) = CreateClient(_ => Json(
            "{\"sensors\":[" +
            "{\"objid\":2001,\"parentid\":1001,\"sensor\":\"CPU\",\"type\":\"SNMP CPU Load\",\"status\":\"Up\"}," +
            "{\"objid\":2002,\"parentid\":1001,\"sensor\":\"Mem\",\"type\":\"SNMP Memory\",\"status\":\"Paused by Dependency\"}" +
            "]}"));

        using (client)
        {
            var sensors = new PrtgLiveGuardSource(client).GetSensors();

            Assert.Equal(2, sensors.Count);
            Assert.Equal(1001, sensors[0].DeviceObjid);
            Assert.Equal("SNMP CPU Load", sensors[0].SensorType);
            Assert.False(sensors[0].Paused);
            // 暫停中的 sensor 讀到的值恆為舊值，必須排除
            Assert.True(sensors[1].Paused);
            // 分類由 SensorType 推導，兩個來源在這一欄都是 null
            Assert.Null(sensors[0].Category);
        }
    }

    [Fact]
    public void GetDevices_objid為字串時仍能解析()
    {
        // PRTG 的 table.json 對數字欄位有時回字串
        var (client, _) = CreateClient(_ => Json(
            "{\"devices\":[{\"objid\":\"1234\",\"device\":\"SRV-S\",\"host\":\"10.2.2.2\"}]}"));

        using (client)
        {
            var d = Assert.Single(new PrtgLiveGuardSource(client).GetDevices());
            Assert.Equal(1234, d.Objid);
        }
    }

    [Fact]
    public void GetDevices_連線失敗時擲出可辨識例外()
    {
        var (client, _) = CreateClient(_ => throw new HttpRequestException("連不上"));

        using (client)
        {
            var source = new PrtgLiveGuardSource(client);
            // 呼叫端（preview 端點）靠這個例外決定要不要退回鏡像
            Assert.ThrowsAny<Exception>(() => source.GetDevices());
        }
    }

    [Fact]
    public void 同一實例重複取用只查一次()
    {
        var (client, handler) = CreateClient(_ => Json("{\"devices\":[]}"));

        using (client)
        {
            var source = new PrtgLiveGuardSource(client);
            source.GetDevices();
            source.GetDevices();

            Assert.Single(handler.RequestedUrls);
        }
    }
}
