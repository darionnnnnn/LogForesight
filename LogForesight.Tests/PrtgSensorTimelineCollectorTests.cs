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
    private readonly PrtgMonitoringPolicy _policy = new() { CoreSystemId = "core-id", SourceGeneration = "core", EndpointHint = EfPrtgObservationStore.SourceHintFor("https://fixture.example"), HostIds = [1], SensorIds = [10], ValidFrom = DateTimeOffset.Now.AddDays(-3), SourceTimeZoneId = TimeZoneInfo.Local.Id, SourceCultureName = "en-US" };
    private PrtgSensorTimelineStore Store => new(_backend.Blob(PrtgSensorTimelineStore.Prefix + 10));
    public PrtgSensorTimelineCollectorTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        { s.PrtgEnabled = true; s.PrtgUrl = "https://fixture.example"; });
        using (var ctx = _backend.CreateContext())
        {
            ctx.Database.EnsureCreated();
            ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 100 });
            ctx.PrtgSensors.Add(new PrtgSensorRow { Objid = 10, DeviceObjid = 100,
                Category = "availability", CategorySource = "auto", SensorType = "ping" });
            ctx.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 100, MapDate = DateTime.Today,
                HostId = 1, MapStatus = PrtgMapStatus.Ok });
            ctx.SaveChanges();
        }
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.Revision = "r1";
            p.CoreSystemId = _policy.CoreSystemId;
            p.SourceGeneration = _policy.SourceGeneration;
            p.EndpointHint = _policy.EndpointHint;
            p.ValidFrom = _policy.ValidFrom;
            p.HostIds = [1];
            p.SensorIds = [10];
            p.SourceTimeZoneId = _policy.SourceTimeZoneId;
            p.SourceCultureName = _policy.SourceCultureName;
        });
    }
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
        var fingerprint = PrtgTimelineResourceIdentity.BuildResourceFingerprint("100", "ping", "created-A", 0);
        PrtgResourceIdentity identity;
        using (var ctx = _backend.CreateContext())
        {
            identity = PrtgResourceIdentityStore.Set(ctx, 10, _policy.SourceGeneration, 100, 1,
                fingerprint, "ping|availability|auto", "", true, _policy.ValidFrom);
            ctx.SaveChanges();
        }
        Store.Update(e =>
        {
            e.Bind(10, 1, _policy.SourceGeneration, fingerprint, identity.Generation,
                identity.Epoch, identity.ChannelGeneration, _policy.ValidFrom);
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
        Assert.Equal(3, handler.Calls); // 初始身分、完整 messages、提交前身分 fence。
    }
    [Fact]
    public async Task 新身分的現在Down不能宣稱昨日故障()
    {
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}"));
        Assert.Equal("identity-warmup", evidence.QualityReason);
        Assert.Empty(evidence.Periods(new(DateTime.Today.AddDays(-1)), new(DateTime.Today)));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("channel")]
    [InlineData("source")]
    [InlineData("scope")]
    public async Task 舊scope欄位只允許當前權威證據升級_不追認其他世代(string mismatch)
    {
        Seed();
        Store.Update(e =>
        {
            if (mismatch == "epoch") e.IdentityEpoch = 0;
            if (mismatch == "channel") e.ChannelGeneration = "stale-channel";
            if (mismatch == "source") e.SourceGeneration = "stale-source";
            if (mismatch == "scope") e.EffectiveScopeFingerprint = "old-nonempty-scope";
        });
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}"));
        Assert.Empty(evidence.Coverage);
        Assert.Empty(evidence.Periods(new(DateTime.Today.AddDays(-1)), new(DateTime.Today)));
        Assert.Equal(_policy.EffectiveSensorScope(10, 1), evidence.EffectiveScopeFingerprint);
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
        Assert.Equal(before, evidence.Coverage.Last().Through); Assert.Equal(3, handler.Calls); // 身分及兩個重複頁。
    }
    [Fact]
    public async Task 無關裝置對應修訂不重設此Sensor資源世代與既有涵蓋()
    {
        Seed();
        var before = Store.Get();
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
        [
            new PrtgHostMapRow { DeviceObjid = 100, MapDate = DateTime.Today, HostId = 1, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 200, MapDate = DateTime.Today, HostId = 2, MapStatus = PrtgMapStatus.Ok }
        ]);
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}"));
        Assert.Equal(before.ResourceGeneration, evidence.ResourceGeneration);
        Assert.Equal(before.IdentityEpoch, evidence.IdentityEpoch);
        Assert.NotEmpty(evidence.Coverage);
        Assert.Equal("covered", evidence.QualityReason);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("epoch")]
    [InlineData("channel")]
    public async Task 查詢期間來源資源epoch或channel世代改變時不提交涵蓋(string changedPart)
    {
        var mutated = false;
        var evidence = await Collect(new Handler(query =>
        {
            if (query.Contains("content=sensors", StringComparison.Ordinal)) return Sensor();
            if (!mutated)
            {
                mutated = true;
                if (changedPart == "channel")
                {
                    var current = _backend.PrtgStore().GetResourceIdentity(10);
                    _backend.PrtgStore().SetObservedChannel(10, _policy.SourceGeneration,
                        "channel-fingerprint-changed", current.Generation);
                }
                else
                {
                    new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
                        .UpdateWithResourceEpochs("r1", [10], policy =>
                        {
                            if (changedPart == "source") policy.SourceGeneration = "source-changed";
                        });
                }
            }
            return "{\"messages\":[]}";
        }));

        Assert.True(mutated);
        Assert.Null(evidence.LastCompleteThrough);
        Assert.Empty(evidence.Coverage);
        Assert.Equal("identity-or-scope-changed-during-query", evidence.QualityReason);
    }
    [Fact]
    public async Task 對應修訂後即使身分HTTP失敗也不能沿用舊證據()
    {
        Seed();
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today.AddDays(1),
            [new PrtgHostMapRow { DeviceObjid = 100, MapDate = DateTime.Today.AddDays(1),
                HostId = 2, MapStatus = PrtgMapStatus.Ok }]);
        var evidence = await Collect(new Handler(_ => "{}"));
        Assert.Equal("sensor-identity-unavailable", evidence.QualityReason);
        var current = _backend.PrtgStore().GetResourceIdentity(10);
        Assert.False(PrtgResourceQualification.IsCurrent(evidence, current, _policy.SourceGeneration,
            10, 100, 1));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
