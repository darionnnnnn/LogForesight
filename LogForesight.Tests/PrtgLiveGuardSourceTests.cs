using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 直接查 PRTG 的守門來源（docs/PRTG-SPEC.md §12）：分頁合併、欄位映射、
/// 以及「PRTG 不遵守分頁位移」時的保險絲。這條路徑跑在使用者按下「自動偵測並填入」時，
/// 卡住的症狀是畫面轉圈到逾時，不會有錯誤訊息。
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

    private static string DevicePage(int start, int count) =>
        "{\"devices\":[" + string.Join(",",
            Enumerable.Range(start, count).Select(i =>
                $"{{\"objid\":{1000 + i},\"device\":\"DEV-{i}\",\"host\":\"10.0.0.{i % 250}\"}}")) + "]}";

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
    public void GetDevices_多頁時合併且只在最後一頁不滿時停止()
    {
        var page = 0;
        var (client, handler) = CreateClient(_ =>
        {
            page++;
            // 第一頁滿 500、第二頁 3 筆
            return Json(page == 1 ? DevicePage(0, 500) : DevicePage(500, 3));
        });

        using (client)
        {
            var devices = new PrtgLiveGuardSource(client).GetDevices();

            Assert.Equal(503, devices.Count);
            Assert.Equal(2, handler.RequestedUrls.Count);
            Assert.Contains("start=0", handler.RequestedUrls[0]);
            Assert.Contains("start=500", handler.RequestedUrls[1]);
        }
    }

    /// <summary>
    /// PRTG 若忽略 start 位移、每次都回滿一頁，終止條件「不滿一頁」永遠不成立。
    /// 沒有分頁上限的話這裡會是無窮迴圈，而它跑在 HTTP 請求執行緒上。
    /// </summary>
    [Fact]
    public void GetDevices_對方忽略分頁位移時靠上限停止()
    {
        var (client, handler) = CreateClient(_ => Json(DevicePage(0, 500)));

        using (client)
        {
            var devices = new PrtgLiveGuardSource(client).GetDevices();

            Assert.Equal(50, handler.RequestedUrls.Count);   // MaxPages
            Assert.Equal(50 * 500, devices.Count);
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
