using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSensorTimelineCollectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-covered-http-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly PrtgMonitoringPolicy _policy = new() { CoreSystemId = "core-id", SourceGeneration = "core", EndpointHint = EfPrtgObservationStore.SourceHintFor("https://fixture.example"), HostIds = [1], SensorIds = [10], ValidFrom = DateTimeOffset.Now.AddDays(-3), SourceTimeZoneId = TimeZoneInfo.Local.Id, SourceCultureName = "en-US", MessageDateFormat = PrtgMessageDateTimeParser.Ymd24, MessageTimeAuthorityRevision = 1 };
    private PrtgSensorTimelineStore Store => new(_backend.Blob(PrtgSensorTimelineStore.Prefix + 10));
    public PrtgSensorTimelineCollectorTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        { s.PrtgEnabled = true; s.PrtgUrl = "https://fixture.example"; });
        Assert.Equal("https://fixture.example", new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgUrl);
        Assert.NotEmpty(_policy.MessageTimeFingerprint("https://fixture.example"));
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
            p.MessageDateFormat = _policy.MessageDateFormat;
            p.MessageTimeAuthorityRevision = _policy.MessageTimeAuthorityRevision;
        });
    }
    private sealed class Handler(Func<string, string> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> QueryUris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; QueryUris.Add(request.RequestUri!.Query); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri.Query), Encoding.UTF8, "application/json") }); }
    }
    private sealed class CancellableHandler(Func<string, CancellationToken, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await response(request.RequestUri!.Query, ct), Encoding.UTF8, "application/json") };
    }
    private static string Sensor(long id = 10, string since = "created-A") => JsonSerializer.Serialize(new
    { sensors = new[] { new { objid = id, parentid = 100, type = "ping", status = "Down", status_raw = 5, cumsince = since } } });
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
            e.MessageTimeFingerprint = _policy.MessageTimeFingerprint("https://fixture.example");
            e.Accept(_policy.ValidFrom, DateTimeOffset.Now.AddDays(-1),
                [new(10, _policy.ValidFrom.AddMinutes(1), "Down", e.SourceGeneration, e.ResourceGeneration)]);
        });
    }
    private async Task<PrtgSensorTimelineEvidence> Collect(HttpMessageHandler handler,
        PrtgSamplingActivity.BackgroundLease? admission = null, CancellationToken cancellationToken = default)
    {
        using var client = new PrtgClient("https://fixture.example", "fixture", 10, false, handler, PrtgAuthModes.Token, "", "", "");
        return await new PrtgSensorTimelineCollector(_backend, client).CollectAsync(10, 1, _policy, cancellationToken, admission);
    }
    private async Task<PrtgSensorTimelineEvidence> CollectBatch(Handler handler)
    {
        using var client = new PrtgClient("https://fixture.example", "fixture", 10, false, handler, PrtgAuthModes.Token, "", "", "");
        return Assert.Single(await new PrtgSensorTimelineCollector(_backend, client).CollectBatchAsync(
            [new PrtgTimelineWorkItem(10, 1)], _policy, CancellationToken.None));
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

    [Theory]
    [InlineData("0", "None")]
    [InlineData("1", "Unknown")]
    [InlineData("2", "Scanning")]
    [InlineData("3", "Up")]
    [InlineData("4", "Warning")]
    [InlineData("5", "Down")]
    [InlineData("6", "No Probe")]
    [InlineData("7", "Paused")]
    [InlineData("8", "Paused")]
    [InlineData("9", "Paused")]
    [InlineData("10", "Unusual")]
    [InlineData("11", "Not Licensed")]
    [InlineData("12", "Paused")]
    [InlineData("13", "Down (Acknowledged)")]
    [InlineData("14", "Down (Partial)")]
    public void OfficialNativeStatusCodeIsPreserved(string raw, string expected)
    {
        Assert.True(PrtgSensorStatuses.TryParseSourceStatus(raw, "contradictory display text", out var status));
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("999", "Up")]
    [InlineData("invalid", "Unknown")]
    [InlineData(null, "")]
    public void UnknownRawStatusRejectsDisplayFallbackAndMissingBothIsUnavailable(string? raw, string display)
    {
        Assert.False(PrtgSensorStatuses.TryParseSourceStatus(raw, display, out _));
    }

    [Fact]
    public async Task CollectorKeepsNativeNoneDistinctFromUnknown()
    {
        Seed();
        static string NoneSensor() => JsonSerializer.Serialize(new
        {
            sensors = new[] { new { objid = 10, parentid = 100, type = "ping", status = "None", status_raw = 0, cumsince = "created-A" } }
        });
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? NoneSensor() : "{\"messages\":[]}"));

        Assert.Equal("None", Store.Get().States.Last().Status);
        Assert.Equal("covered", evidence.QualityReason);
    }

    [Fact]
    public async Task CollectorUsesCanonicalDisplayWhenRawIsMissing()
    {
        Seed();
        static string TextOnlyUnknownSensor() => JsonSerializer.Serialize(new
        {
            sensors = new[] { new { objid = 10, parentid = 100, type = "ping", status = "Unknown", cumsince = "created-A" } }
        });
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? TextOnlyUnknownSensor() : "{\"messages\":[]}"));

        Assert.Equal("covered", evidence.QualityReason);
        Assert.Equal("Unknown", Store.Get().States.Last().Status);
    }

    [Theory]
    [InlineData("607", "Up")]
    [InlineData("608", "Down")]
    [InlineData("609", "Warning")]
    [InlineData("619", "Down (Acknowledged)")]
    [InlineData("620", "Down (Partial)")]
    public async Task MessageStatusUsesOfficialCategoryRawCode(string raw, string expected)
    {
        Seed();
        Store.Update(e => e.States = [new(10, e.ValidFrom.AddMinutes(1), "None", e.SourceGeneration, e.ResourceGeneration)]);
        var at = DateTime.Now.AddHours(-2);
        at = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, DateTimeKind.Local);
        var messages = JsonSerializer.Serialize(new { messages = new[] { new
        {
            objid = 10, datetime = at.ToString("yyyy-MM-dd HH:mm:ss"), status = "misleading display",
            status_raw = raw, message = "sensor state"
        } } });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : messages);

        var evidence = await Collect(handler);

        Assert.Contains(evidence.States, state => state.At == new DateTimeOffset(
            DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(at)) && state.Status == expected);
        Assert.Contains(handler.QueryUris, uri => uri.Contains("columns=objid,datetime,status,message", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.QueryUris, uri => uri.Contains("columns=objid,datetime,status,status_raw", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("3", "System Reset")]
    [InlineData("5", "Probe Denied Key")]
    public async Task MessageSystemCategoriesArePaginatedButDoNotBecomeSensorStates(string raw, string display)
    {
        Seed();
        var at = DateTime.Now.AddHours(-2);
        at = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, DateTimeKind.Local);
        var messages = JsonSerializer.Serialize(new { messages = new[] { new
        {
            objid = 10, datetime = at.ToString("yyyy-MM-dd HH:mm:ss"), status = display,
            status_raw = raw, message = "system event"
        } } });
        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : messages));

        Assert.DoesNotContain(evidence.States, state => state.At == new DateTimeOffset(
            DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(at)));
        Assert.Equal("covered", evidence.QualityReason);
    }

    [Theory]
    [InlineData("999", "Unknown", "message-status-code-unrecognized")]
    [InlineData("", "System Reset", "message-status-text-unrecognized")]
    public async Task UnknownMessageCategoriesFailClosed(string raw, string display, string reason)
    {
        Seed();
        var before = Store.Get().Coverage.ToArray();
        var messages = JsonSerializer.Serialize(new { messages = new[] { new
        {
            objid = 10, datetime = DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss"),
            status = display, status_raw = raw, message = "not a sensor transition"
        } } });

        var evidence = await Collect(new Handler(q => q.Contains("content=sensors") ? Sensor() : messages));

        Assert.Equal(reason, evidence.QualityReason);
        Assert.Equal(before, evidence.Coverage);
    }

    [Fact]
    public async Task NonStatePageDoesNotHideOlderStatusPredecessorOnNextPage()
    {
        Seed();
        var pageTime = DateTime.Now.AddDays(-3);
        var nonStatePage = JsonSerializer.Serialize(new { messages = Enumerable.Range(0, 1000).Select(i => new
        {
            objid = 10, datetime = pageTime.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"),
            status = "System Reset", status_raw = "3", message = "system-" + i
        }).ToArray() });
        var olderState = JsonSerializer.Serialize(new { messages = new[] { new
        {
            objid = 10, datetime = pageTime.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"),
            status = "Up", status_raw = "607", message = "older state predecessor"
        } } });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() :
            q.Contains("start=0") ? nonStatePage : q.Contains("start=1000") ? olderState : "{}");

        var evidence = await Collect(handler);

        Assert.Equal("covered", evidence.QualityReason);
        Assert.Contains(handler.QueryUris, uri => uri.Contains("start=1000", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyTimelineJsonIsNotCurrentAndCannotProducePeriods()
    {
        var legacy = JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>("""
            {"SensorId":10,"SourceGeneration":"core","ResourceGeneration":"resource","Coverage":[],"States":[]}
            """)!;

        Assert.Equal(0, legacy.MessageStatusSemanticVersion);
        Assert.Empty(legacy.Periods(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
        Assert.Equal(PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent,
            new PrtgSensorTimelineEvidence().MessageStatusSemanticVersion);
    }

    [Fact]
    public void LegacyTimelineFailsFormalResourceQualification()
    {
        Seed();
        Store.Update(e => e.MessageStatusSemanticVersion = 0);
        var evidence = Store.Get();
        var identity = _backend.PrtgStore().GetResourceIdentity(10);

        Assert.False(PrtgResourceQualification.IsCurrent(evidence, identity, _policy.SourceGeneration,
            10, 100, 1, _policy.MessageTimeFingerprint("https://fixture.example")));
        Assert.Empty(evidence.Periods(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task LegacyTimelineRefreshResetsOnlyMessageEvidenceAndCanResumePageCheckpoint()
    {
        Seed();
        var before = Store.Get();
        var diskCheckedAt = DateTimeOffset.UtcNow.AddDays(-1);
        Store.Update(e =>
        {
            e.MessageStatusSemanticVersion = 0;
            e.States.Add(new(10, diskCheckedAt.AddMinutes(-5), "Up", e.SourceGeneration, e.ResourceGeneration));
            e.DiskSemanticFingerprint = "preserved-disk-semantic";
            e.DiskSemanticValidFrom = diskCheckedAt.AddDays(-10);
            e.DiskSemanticCheckedAt = diskCheckedAt;
            e.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(5);
        });

        var pageTime = DateTime.Now.AddHours(-2);
        var page = JsonSerializer.Serialize(new { messages = Enumerable.Range(0, 1000).Select(i => new
        {
            objid = 10, datetime = pageTime.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"),
            status = "Up", status_raw = "607", message = "state-" + i
        }).ToArray() });
        var first = new Handler(q => q.Contains("content=sensors") ? Sensor() :
            q.Contains("start=0") ? page : "{}");

        var partial = await Collect(first);

        Assert.Equal(PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent, partial.MessageStatusSemanticVersion);
        Assert.Equal(before.ResourceGeneration, partial.ResourceGeneration);
        Assert.Equal(before.ValidFrom, partial.ValidFrom);
        Assert.Equal("preserved-disk-semantic", partial.DiskSemanticFingerprint);
        Assert.Equal(diskCheckedAt, partial.DiskSemanticCheckedAt);
        Assert.Null(partial.LastCompleteThrough);
        Assert.Equal(2, partial.BootstrapPagesRead); // successful page plus failed request both consume the fixed request budget
        Assert.Empty(partial.Coverage);
        Assert.Equal(1, partial.PendingNextPage);
        Assert.Equal("messages-array-missing", partial.QualityReason);

        var retry = new Handler(q => q.Contains("content=sensors") ? Sensor() :
            q.Contains("start=0") ? page : "{\"messages\":[]}");
        var completed = await Collect(retry);

        Assert.Equal(PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent, completed.MessageStatusSemanticVersion);
        Assert.Equal(0, completed.PendingNextPage);
        Assert.Equal("covered", completed.QualityReason);
        Assert.Contains(retry.QueryUris, uri => uri.Contains("start=1000", StringComparison.Ordinal));
        Assert.DoesNotContain(completed.States, state => state.At == diskCheckedAt.AddMinutes(-5));
    }

    [Fact]
    public async Task ParserRefreshBootstrapThatExhaustedItsPageBudgetStaysExplicitlyUnqualified()
    {
        Seed();
        Store.Update(e =>
        {
            e.MessageStatusSemanticVersion = PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent;
            e.LastCompleteThrough = null;
            e.Coverage.Clear();
            e.States.Clear();
            e.PendingStates.Clear();
            e.PendingEventKeys.Clear();
            e.BootstrapPagesRead = 20;
            e.PendingNextPage = 20;
            e.NextAttemptAt = null;
        });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}");

        var evidence = await Collect(handler);

        Assert.Equal("timeline-bootstrap-capacity-shortfall", evidence.QualityReason);
        Assert.Null(evidence.LastCompleteThrough);
        Assert.Empty(evidence.Coverage);
        Assert.Empty(evidence.Periods(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
        Assert.DoesNotContain(handler.QueryUris, uri => uri.Contains("content=messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootstrapBoundaryRereadConsumesNearLimitRequestBudget()
    {
        Seed();
        Store.Update(e =>
        {
            e.MessageStatusSemanticVersion = PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent;
            e.LastCompleteThrough = null;
            e.Coverage.Clear();
            e.States.Clear();
            e.PendingStates.Clear();
            e.PendingEventKeys.Clear();
            e.BootstrapPagesRead = 19;
            e.PendingNextPage = 20;
            e.NextAttemptAt = null;
            e.PendingMessageTimeFingerprint = _policy.MessageTimeFingerprint("https://fixture.example");
        });
        var pageTime = DateTime.Now.AddHours(-2);
        var page = JsonSerializer.Serialize(new { messages = Enumerable.Range(0, 1000).Select(i => new
        {
            objid = 10, datetime = pageTime.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"),
            status = "Unknown", status_raw = "601", message = "non-state-" + i
        }).ToArray() });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : page);

        var evidence = await Collect(handler);

        Assert.Equal("timeline-bootstrap-capacity-shortfall", evidence.QualityReason);
        Assert.Equal(20, evidence.BootstrapPagesRead);
        Assert.Single(handler.QueryUris.Where(uri => uri.Contains("content=messages", StringComparison.Ordinal)));
        Assert.Contains(handler.QueryUris, uri => uri.Contains("start=19000", StringComparison.Ordinal));
        Assert.Empty(evidence.Coverage);
    }

    [Fact]
    public async Task BatchBootstrapWithExhaustedRequestBudgetMakesNoMessageRequest()
    {
        Seed();
        Store.Update(e =>
        {
            e.MessageStatusSemanticVersion = PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent;
            e.LastCompleteThrough = null;
            e.Coverage.Clear();
            e.States.Clear();
            e.PendingStates.Clear();
            e.PendingEventKeys.Clear();
            e.BootstrapPagesRead = 20;
            e.PendingNextPage = 20;
            e.NextAttemptAt = null;
        });
        var handler = new Handler(q => q.Contains("content=sensors") ? Sensor() : "{\"messages\":[]}");

        var evidence = await CollectBatch(handler);

        Assert.Equal("timeline-bootstrap-capacity-shortfall", evidence.QualityReason);
        Assert.Null(evidence.LastCompleteThrough);
        Assert.Equal(20, evidence.BootstrapPagesRead);
        Assert.Empty(evidence.Coverage);
        Assert.DoesNotContain(handler.QueryUris, uri => uri.Contains("content=messages", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sampling")]
    [InlineData("caller")]
    public async Task InterruptedSingleBootstrapPersistsAttemptedRequestBudget(string interruption)
    {
        Seed();
        Store.Update(e => e.MessageStatusSemanticVersion = 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CancellableHandler(async (q, token) =>
        {
            if (q.Contains("content=sensors", StringComparison.Ordinal)) return Sensor();
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "{\"messages\":[]}";
        });
        var sampling = new PrtgSamplingActivity();
        PrtgSamplingActivity.BackgroundLease? admission = null;
        CancellationTokenSource? callerCancellation = null;
        if (interruption == "sampling")
        {
            Assert.True(sampling.TryBeginBackground(out var acquired));
            admission = acquired;
        }
        else callerCancellation = new CancellationTokenSource();

        var collect = Collect(handler, admission, callerCancellation?.Token ?? CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (interruption == "sampling")
        {
            using (sampling.BeginSampling()) await collect;
        }
        else
        {
            callerCancellation!.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collect);
        }

        var evidence = Store.Get();
        Assert.Null(evidence.LastCompleteThrough);
        Assert.Empty(evidence.Coverage);
        Assert.Equal(1, evidence.BootstrapPagesRead);
        callerCancellation?.Dispose();
        admission?.Dispose();
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
            10, 100, 1, _policy.MessageTimeFingerprint("https://fixture.example")));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
