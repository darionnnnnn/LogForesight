using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSensorTimelineCollectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-covered-http-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly PrtgMonitoringPolicy _policy = new() { SourceGeneration = "core", HostIds = [1], SensorIds = [10], ValidFrom = DateTimeOffset.Now.AddDays(-3), SourceTimeZoneId = TimeZoneInfo.Local.Id, SourceCultureName = "en-US" };
    private PrtgSensorTimelineStore Store => new(_backend.Blob(PrtgSensorTimelineStore.Prefix + 10));
    public PrtgSensorTimelineCollectorTests() => _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
    private sealed class Handler(Func<string, string> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!.Query), Encoding.UTF8, "application/json") }); }
    }
    private static string Sensor(long id = 10, string since = "created-A") => JsonSerializer.Serialize(new
    { sensors = new[] { new { objid = id, parentid = 100, type = "ping", status = "Down", cumsince = since } } });
    private void Seed()
    {
        Store.Update(e =>
        {
            e.Bind(10, 1, "core", "100|ping|created-A|map:0", _policy.ValidFrom);
            e.Accept(_policy.ValidFrom, DateTimeOffset.Now.AddDays(-1),
                [new(10, _policy.ValidFrom.AddMinutes(1), "Down", e.SourceGeneration, e.ResourceGeneration)]);
        });
    }
    private async Task<PrtgSensorTimelineEvidence> Collect(Handler handler)
    {
        using var client = new PrtgClient("https://fixture.example", "fixture", 10, false, handler, PrtgAuthModes.Token, "", "", "");
        return await new PrtgSensorTimelineCollector(_backend, client).CollectAsync(10, 1, _policy, CancellationToken.None);
    }
    [Fact]
    public async Task 空完整逐Sensor查詢保留可信前導_不依今日鏡像回填()
    {
        Seed(); var generation = Store.Get().ResourceGeneration;
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}");
        var evidence = await Collect(handler);
        Assert.Equal(generation, evidence.ResourceGeneration); Assert.Equal("covered", evidence.QualityReason);
        var periods = evidence.Periods(new(DateTime.Today.AddDays(-1)), new(DateTime.Today));
        Assert.Equal(1440, periods.Sum(p => (p.Through - p.From).TotalMinutes));
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task 新身分的現在Down不能宣稱昨日故障()
    {
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}"));
        Assert.Equal("identity-warmup", evidence.QualityReason);
        Assert.Empty(evidence.Periods(new(DateTime.Today.AddDays(-1)), new(DateTime.Today)));
    }
    [Fact]
    public async Task Sensor重建切斷原世代涵蓋()
    {
        Seed(); var old = Store.Get().ResourceGeneration;
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor(since: "created-B") : "{\"messages\":[]}"));
        Assert.NotEqual(old, evidence.ResourceGeneration); Assert.Empty(evidence.Coverage);
    }
    [Fact]
    public async Task 回應錯Sensor不提交任何涵蓋()
    {
        var evidence = await Collect(new Handler(_ => Sensor(99)));
        Assert.Equal("sensor-identity-not-unique", evidence.QualityReason); Assert.Empty(evidence.Coverage);
    }
    [Fact]
    public async Task 重複頁面保險絲_不把截斷當完整()
    {
        Seed(); var before = Store.Get().Coverage.Last().Through;
        var at = DateTime.Now.AddMinutes(-1);
        var messages = JsonSerializer.Serialize(new { messages = Enumerable.Range(0, 1000).Select(i =>
            new { objid = 10, datetime = at.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"), status = "Down", message = "state" }).ToArray() });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : messages);
        var evidence = await Collect(handler);
        Assert.Contains(evidence.QualityReason, new[] { "messages-repeated-page-or-ambiguous-event", "source-clock-or-order-unverified" });
        Assert.Equal(before, evidence.Coverage.Last().Through); Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task 對應修訂後即使身分HTTP失敗也不能沿用舊證據()
    {
        Seed(); _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("{}", true));
        var evidence = await Collect(new Handler(_ => "{}"));
        Assert.Empty(evidence.States); Assert.Empty(evidence.Coverage);
        Assert.Equal("sensor-identity-unavailable", evidence.QualityReason);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
