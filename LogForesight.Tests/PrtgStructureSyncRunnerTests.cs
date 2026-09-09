using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 「同步結構與對應」執行本體（docs/PRTG-SPEC.md §5a）：
/// 兩步順序、摘要數字、結構同步失敗時不做對應。
/// </summary>
public class PrtgStructureSyncRunnerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

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
        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        return (client, handler);
    }

    /// <summary>回覆一棵最小的裝置／感測器樹，讓結構同步能走完三個階段。</summary>
    private static HttpResponseMessage StructureResponder(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("content=devices"))
        {
            return Json("{\"treesize\":1,\"devices\":[{\"objid\":1001,\"device\":\"SRV-A\",\"host\":\"10.1.1.1:8080\",\"group\":\"G\",\"active\":true}]}");
        }
        if (url.Contains("content=sensors"))
        {
            return Json("{\"treesize\":1,\"sensors\":[{\"objid\":2001,\"sensor\":\"CPU\",\"parentid\":1001,\"type\":\"SNMP CPU Load\",\"status\":\"Up\",\"status_raw\":3}]}");
        }
        if (url.Contains("content=messages"))
        {
            return Json("{\"treesize\":0,\"messages\":[]}");
        }
        return Json("{}");
    }

    [Fact]
    public async Task RunAsync_成功時同步結構並對應_摘要數字正確()
    {
        var store = CreateStore();
        var console = new TestConsole();
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            // device 的 host 欄位帶 port，靠位址正規化才對得上
            hosts.Add(new WebHost { HostId = 51, HostName = "srv-a", IpAddress = "10.1.1.1", Active = true });
        });

        var (client, _) = CreateClient(StructureResponder);
        using (client)
        {
            var fetchService = new PrtgFetchService(client, store, console);
            var today = new DateTime(2026, 9, 9);

            var status = await PrtgStructureSyncRunner.RunAsync(
                fetchService, store, hostStore, new PrtgAddressResolver(),
                concurrency: 1, console, CancellationToken.None, today: today);

            Assert.True(status.Success);
            Assert.Null(status.ErrorMessage);
            Assert.Equal(1, status.Devices);
            Assert.Equal(1, status.Sensors);
            Assert.Equal(today, status.MapDate);
            Assert.Equal(1, status.MapOk);
            Assert.NotEqual(default, status.CompletedAt);

            // 對應結果真的落地了
            var rows = store.GetHostMapForDate(today);
            var row = Assert.Single(rows);
            Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
            Assert.Equal(51, row.HostId);
        }
    }

    [Fact]
    public async Task RunAsync_結構同步失敗時不做對應且既有對應保持不變()
    {
        var store = CreateStore();
        var console = new TestConsole();
        var hostStore = new FakeHostStore();
        var today = new DateTime(2026, 9, 9);

        // 先放一筆既有對應，用來證明失敗路徑沒有把它洗掉
        store.ReplaceHostMapForDate(today, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 9001, HostId = 77, MapStatus = PrtgMapStatus.Ok, Ip = "10.9.9.9" }
        });

        var (client, _) = CreateClient(_ => throw new HttpRequestException("PRTG 連不上"));
        using (client)
        {
            var fetchService = new PrtgFetchService(client, store, console);

            var status = await PrtgStructureSyncRunner.RunAsync(
                fetchService, store, hostStore, new PrtgAddressResolver(),
                concurrency: 1, console, CancellationToken.None, today: today);

            Assert.False(status.Success);
            Assert.NotNull(status.ErrorMessage);
            Assert.NotEqual(default, status.CompletedAt);

            // 對應完全沒被動過
            var rows = store.GetHostMapForDate(today);
            var row = Assert.Single(rows);
            Assert.Equal(77, row.HostId);

            Assert.Contains(console.Lines, l => l.Contains("未進行主機對應"));
            Assert.Contains(console.Lines, l => l.Contains("沒有取得任何裝置"));
        }
    }

    [Fact]
    public async Task RunAsync_一台都對不到時輸出提示()
    {
        var store = CreateStore();
        var console = new TestConsole();
        var hostStore = new FakeHostStore();
        // 主機主檔是空的 → device 對不到任何主機

        var (client, _) = CreateClient(StructureResponder);
        using (client)
        {
            var fetchService = new PrtgFetchService(client, store, console);

            var status = await PrtgStructureSyncRunner.RunAsync(
                fetchService, store, hostStore, new PrtgAddressResolver(),
                concurrency: 1, console, CancellationToken.None, today: new DateTime(2026, 9, 9));

            Assert.True(status.Success);
            Assert.Equal(0, status.MapOk);
            Assert.Contains(console.Lines, l => l.Contains("沒有任何 PRTG 裝置對應到主機主檔"));
        }
    }
}
