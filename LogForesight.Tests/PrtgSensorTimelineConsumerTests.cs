using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSensorTimelineConsumerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-timeline-consumer-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgMonitoringPolicy _policy;

    public PrtgSensorTimelineConsumerTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _settings.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = "https://fixture.example";
            settings.PrtgApiTokenEnc = "fixture-token";
        });
        _policy = new PrtgMonitoringPolicy
        {
            CoreSystemId = "core-id", SourceGeneration = "source-a",
            EndpointHint = EfPrtgObservationStore.SourceHintFor("https://fixture.example"),
            HostIds = [1], SensorIds = [101], ValidFrom = DateTimeOffset.UtcNow.AddDays(-40),
            SourceTimeZoneId = TimeZoneInfo.Local.Id, SourceCultureName = "en-US"
        };
        SavePolicy(_policy);
        SeedMirror(_policy.SensorIds);
    }

    [Fact]
    public async Task 新資源只保存目前狀態錨點且不把舊歷史算作涵蓋()
    {
        var messagePages = 0;
        var identitySnapshots = 0;
        var handler = new Handler((request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("content=sensors", StringComparison.Ordinal))
            {
                identitySnapshots++;
                return Task.FromResult(Json(IdentityRows(IdsFromFilters(query))));
            }
            messagePages++;
            var start = ReadQueryInt(query, "start");
            MessageRow[] rows = start == 0
                ? Enumerable.Range(1, 1000).Select(i => new MessageRow(101,
                    DateTime.Now.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "Down", $"row-{i}"))
                    .ToArray()
                : [new MessageRow(101, DateTime.Now.AddDays(-32).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "Down", "boundary")];
            return Task.FromResult(Json(JsonSerializer.Serialize(new { messages = rows })));
        });
        var consumer = Consumer(handler, new PrtgSamplingActivity());

        var tick = await consumer.TickAsync(CancellationToken.None);

        Assert.True(tick.DidWork);
        Assert.Equal("initial-collection-complete-readiness-separate", tick.Outcome);
        Assert.Equal(1, messagePages);
        Assert.Equal(2, identitySnapshots);
        Assert.Equal(3, identitySnapshots + messagePages);
        var saved = Evidence(101);
        Assert.NotNull(saved.LastCompleteThrough);
        Assert.Equal("capacity-unverified", saved.BootstrapStatus);
        Assert.Empty(saved.Coverage);
        Assert.Single(saved.States);
        Assert.Null(saved.BootstrapDeadlineAt);
        Assert.Equal(1, saved.BootstrapPagesRead);
        var cycle = new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get();
        Assert.Equal(72, cycle.BootstrapDeadlineHours);
        Assert.Equal("initial-collection-complete", cycle.BootstrapCycleOutcome);
        Assert.NotNull(cycle.BootstrapCycleDeadlineAtUtc);

        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + "101")).Update(evidence =>
            evidence.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1));
        var subsequentMessagePages = 0;
        var restarted = Consumer(new Handler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("content=sensors", StringComparison.Ordinal))
                return Task.FromResult(Json(IdentityRows(IdsFromFilters(request.RequestUri.Query))));
            subsequentMessagePages++;
            return Task.FromResult(Json("{\"messages\":[]}"));
        }), new PrtgSamplingActivity());
        await restarted.TickAsync(CancellationToken.None);
        Assert.True(subsequentMessagePages > 0);
        Assert.True(Evidence(101).LastCompleteThrough > saved.LastCompleteThrough);
        Assert.Equal(cycle.BootstrapCycleId, Evidence(101).BootstrapCollectionCycleId);
    }

    [Fact]
    public async Task 前五十顆失敗後新consumer仍公平推進第五十一顆()
    {
        var ids = Enumerable.Range(101, 51).Select(id => (long)id).ToArray();
        SavePolicy(PolicyFor(ids));
        SeedMirror(ids);
        var firstRound = true;
        var handler = new Handler((request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("content=sensors", StringComparison.Ordinal))
                return Task.FromResult(Json(IdentityRows(IdsFromFilters(query))));
            var id = ReadQueryInt(query, "id");
            if (firstRound && id <= 150)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("source unavailable", Encoding.UTF8, "text/plain") });
            return Task.FromResult(Json("{\"messages\":[]}"));
        });
        var first = Consumer(handler, new PrtgSamplingActivity());

        await first.TickAsync(CancellationToken.None);
        Assert.Null(Evidence(151).LastCompleteThrough);
        firstRound = false;
        var restarted = Consumer(handler, new PrtgSamplingActivity());
        await restarted.TickAsync(CancellationToken.None);

        Assert.NotNull(Evidence(151).LastCompleteThrough);
        Assert.Null(Evidence(101).LastCompleteThrough);
        Assert.Equal(151, new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get().LastServedSensorId);
    }

    [Fact]
    public async Task 前景採樣搶占在途頁面時不提交coverage或watermark()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, cancellationToken) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("content=sensors", StringComparison.Ordinal))
                return Json(IdentityRows(IdsFromFilters(query)));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"messages\":[]}");
        });
        var sampling = new PrtgSamplingActivity();
        var consumer = Consumer(handler, sampling);
        var task = consumer.TickAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (sampling.BeginSampling())
        {
            var result = await task;
            Assert.Equal("yielded-to-sampling", result.Outcome);
        }

        var saved = Evidence(101);
        Assert.Null(saved.LastCompleteThrough);
        Assert.Empty(saved.Coverage);
        Assert.Equal("yielded-to-sampling", saved.QualityReason);
    }

    [Fact]
    public async Task 進度API只回每頁selected範圍且不發布未驗證bootstrap期限()
    {
        var hosts = new FakeHostStore();
        hosts.Touch("fixture-host", DateTime.Now, "netiq");
        var controller = new PrtgTimelineProgressController(_backend, hosts, new AlwaysVisibleService(hosts),
            FakeCurrentUser.ForUser(1, Capability.Maintain))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Get(1, 1, CancellationToken.None);

        var body = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(body.Value);
        Assert.Equal("capacity-unverified", envelope.Data!.CapacityStatus);
        var row = Assert.Single(envelope.Data.Rows);
        Assert.Equal("not-started", row.BootstrapStatus);
        Assert.Null(row.DeadlineAt);
        Assert.Equal(64, envelope.Data.Revision.Length);
        Assert.Equal(PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours,
            row.CalculatedBootstrapBaselineHours);
        Assert.Equal(72, envelope.Data.BootstrapDeadlineHours);
        Assert.Equal("unknown", envelope.Data.BudgetTelemetryStatus);

        _backend.Blob(PrtgSensorTimelineStore.Prefix + "101").Mutate(_ => ("{ malformed", true));
        var malformedResult = Assert.IsType<OkObjectResult>(await controller.Get(1, 1, CancellationToken.None));
        var malformedPage = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(malformedResult.Value).Data!;
        Assert.Equal("malformed", Assert.Single(malformedPage.Rows).BootstrapStatus);
        Assert.Equal("metadata-malformed", malformedPage.Rows[0].QualityReason);

        var validFixture = new PrtgSensorTimelineEvidence
        {
            SensorId = 101,
            HostId = 1,
            SourceGeneration = _policy.SourceGeneration,
            ResourceGeneration = "resource-generation",
            EffectiveScopeFingerprint = _policy.EffectiveSensorScope(101, 1),
            BootstrapStatus = "capacity-unverified",
            QualityReason = "identity-warmup"
        };
        _backend.Blob(PrtgSensorTimelineStore.Prefix + "101").Mutate(_ =>
            (JsonSerializer.Serialize(validFixture, LfJsonOptions.Pretty), true));
        var waitingResult = Assert.IsType<OkObjectResult>(await controller.Get(1, 1, CancellationToken.None));
        var waitingPage = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(waitingResult.Value).Data!;
        Assert.Equal("waiting-identity", Assert.Single(waitingPage.Rows).BootstrapStatus);

        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + "101")).Update(evidence =>
        {
            evidence.SensorId = 101;
            evidence.HostId = 1;
            evidence.SourceGeneration = "old-source";
            evidence.ResourceGeneration = "resource-generation";
            evidence.EffectiveScopeFingerprint = _policy.EffectiveSensorScope(101, 1);
            evidence.BootstrapStatus = "complete";
            evidence.BootstrapPagesRead = 20;
            evidence.QualityReason = "covered";
        });
        var staleResult = Assert.IsType<OkObjectResult>(await controller.Get(1, 1, CancellationToken.None));
        var stalePage = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(staleResult.Value).Data!;
        Assert.Equal("source-scope-stale", Assert.Single(stalePage.Rows).BootstrapStatus);

        Assert.IsType<BadRequestObjectResult>(await controller.Get(1, 101, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        controller.ControllerContext.HttpContext.RequestAborted = canceled.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Get(1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task 已逾期週期不會自動重設或送出任何新HTTP請求()
    {
        var now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var scope = PrtgSensorTimelineProgressStore.ScopeFingerprint(_policy, _settings.Get());
        new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Update(state =>
        {
            state.BootstrapCycleId = "expired-cycle";
            state.BootstrapSourceGeneration = _policy.SourceGeneration;
            state.BootstrapScopeFingerprint = scope;
            state.BootstrapCycleStartedAtUtc = now.AddHours(-73);
            state.BootstrapCycleAsOfUtc = now.AddHours(-73);
            state.BootstrapCycleDeadlineAtUtc = now.AddSeconds(-1);
            state.BootstrapCycleSelectedSensors = 1;
            state.BootstrapCycleOutcome = "running";
        });
        var requests = 0;
        var consumer = new PrtgSensorTimelineConsumer(_settings, _backend, () => false,
            settings => PrtgClientFactory.Create(settings, new Handler((_, _) =>
            {
                requests++;
                return Task.FromResult(Json("{\"messages\":[]}"));
            }), new PrtgRequestBudget()), new PrtgSamplingActivity(), new FixedTimeProvider(now));

        var tick = await consumer.TickAsync(CancellationToken.None);

        Assert.Equal("bootstrap-deadline-exceeded", tick.Outcome);
        Assert.Equal(0, requests);
        var saved = new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get();
        Assert.Equal("deadline-exceeded", saved.BootstrapCycleOutcome);
        Assert.Equal("expired-cycle", saved.BootstrapCycleId);
    }

    [Fact]
    public async Task 期限於HTTP請求中到達時取消該請求並停止本輪後續流量()
    {
        var now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var clock = new MutableTimeProvider(now);
        var messageRequests = 0;
        var handler = new Handler(async (request, token) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("content=sensors", StringComparison.Ordinal))
                return Json(IdentityRows(IdsFromFilters(query)));
            messageRequests++;
            clock.Advance(TimeSpan.FromHours(73));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{\"messages\":[]}");
        });
        var consumer = new PrtgSensorTimelineConsumer(_settings, _backend, () => false,
            settings => PrtgClientFactory.Create(settings, handler, new PrtgRequestBudget()),
            new PrtgSamplingActivity(), clock);

        var tick = await consumer.TickAsync(CancellationToken.None);

        Assert.Equal("bootstrap-deadline-exceeded", tick.Outcome);
        Assert.Equal(1, messageRequests);
        Assert.Null(Evidence(101).LastCompleteThrough);
        Assert.Equal("deadline-exceeded", new PrtgSensorTimelineProgressStore(
            _backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get().BootstrapCycleOutcome);
    }

    [Fact]
    public async Task 呼叫端取消會取消共享HTTP等待且不提交部分水位()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageRequests = 0;
        var handler = new Handler(async (request, token) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("content=sensors", StringComparison.Ordinal))
                return Json(IdentityRows(IdsFromFilters(query)));
            messageRequests++;
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{\"messages\":[]}");
        });
        var consumer = Consumer(handler, new PrtgSamplingActivity());
        using var cancellation = new CancellationTokenSource();
        var task = consumer.TickAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, messageRequests);
        Assert.Null(Evidence(101).LastCompleteThrough);
    }

    [Fact]
    public void 最後一頁已完整但先前頁感測器在最終CAS前改變時週期仍未完成()
    {
        var ids = Enumerable.Range(101, 101).Select(id => (long)id).ToArray();
        var policy = PolicyFor(ids);
        SavePolicy(policy);
        SeedMirror(ids);

        var asOf = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string cycleId = "two-page-cycle";
        var timelineKeys = ids.Select(id => PrtgSensorTimelineStore.Prefix + id.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        foreach (var id in ids)
        {
            var evidence = new PrtgSensorTimelineEvidence
            {
                SensorId = id,
                HostId = 1,
                SourceGeneration = policy.SourceGeneration,
                ResourceGeneration = $"resource-{id}",
                IdentityEpoch = 1,
                ValidFrom = asOf.AddHours(-1),
                LastCompleteThrough = asOf,
                BootstrapCollectionCycleId = cycleId,
                BootstrapStatus = "capacity-unverified",
                BootstrapPagesRead = 1,
                EffectiveScopeFingerprint = policy.EffectiveSensorScope(id, 1),
                QualityReason = "identity-warmup"
            };
            _backend.Blob(PrtgSensorTimelineStore.Prefix + id.ToString(CultureInfo.InvariantCulture))
                .Mutate(_ => (JsonSerializer.Serialize(evidence, LfJsonOptions.Pretty), true));
        }

        var scope = PrtgSensorTimelineProgressStore.ScopeFingerprint(policy, _settings.Get());
        var progress = new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey));
        progress.Update(state =>
        {
            state.BootstrapCycleId = cycleId;
            state.BootstrapSourceGeneration = policy.SourceGeneration;
            state.BootstrapScopeFingerprint = scope;
            state.BootstrapCycleStartedAtUtc = asOf.AddMinutes(-5);
            state.BootstrapCycleAsOfUtc = asOf;
            state.BootstrapCycleDeadlineAtUtc = asOf.AddHours(72);
            state.BootstrapCycleSelectedSensors = ids.Length;
            state.BootstrapCycleStartSensorId = ids[0];
            state.BootstrapCycleSweepEndSensorId = ids[^1];
            state.BootstrapCycleSweepPassedEnd = true;
            state.BootstrapCycleOutcome = "running";
            state.LastServedSensorId = ids[^1];
        });

        // Model the final proof's first 100 selected rows already checked and the 101st ready.
        // A source reconciliation changes the first-page row after that read but before the
        // durable completion CAS. The CAS must fence every selected timeline version.
        var expectedTimelineVersions = timelineKeys.ToDictionary(key => key,
            key => _backend.Blob(key).ReadVersion(), StringComparer.Ordinal);
        Assert.All(ids, id => Assert.Equal(asOf, Evidence(id).LastCompleteThrough));
        Assert.Equal(cycleId, Evidence(ids[^1]).BootstrapCollectionCycleId);

        new PrtgSensorTimelineStore(_backend.Blob(timelineKeys[0])).Update(evidence =>
        {
            evidence.BootstrapCollectionCycleId = "replacement-cycle";
            evidence.ResourceGeneration = "replacement-generation";
        });

        var expected = progress.Get();
        var completed = progress.TryUpdateExpected(expected.BootstrapCycleId, expected.BootstrapCycleOutcome,
            expected.BootstrapSettingsRevision,
            _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion(),
            _backend.Blob("system_settings").ReadVersion(),
            _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), expectedTimelineVersions,
            state =>
            {
                state.BootstrapCycleOutcome = "initial-collection-complete";
                state.BootstrapCycleReason = "all-selected-captured";
            },
            state => state.BootstrapCycleSelectedSensors == ids.Length &&
                state.BootstrapCycleSweepPassedEnd && state.LastServedSensorId == ids[^1]);

        Assert.False(completed);
        Assert.Equal("running", progress.Get().BootstrapCycleOutcome);
        Assert.Equal(cycleId, progress.Get().BootstrapCycleId);
        Assert.Equal("replacement-cycle", Evidence(ids[0]).BootstrapCollectionCycleId);
        Assert.Equal(cycleId, Evidence(ids[^1]).BootstrapCollectionCycleId);
    }

    [Fact]
    public async Task Maintain期限設定以revision保護且明確續行保留週期游標()
    {
        var hosts = new FakeHostStore();
        hosts.Touch("fixture-host", DateTime.Now, "netiq");
        var controller = new PrtgTimelineProgressController(_backend, hosts, new AlwaysVisibleService(hosts),
            FakeCurrentUser.ForUser(1, Capability.Maintain))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var first = Assert.IsType<OkObjectResult>(await controller.Get(1, 50, CancellationToken.None));
        var firstPage = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(first.Value).Data!;
        var now = DateTimeOffset.UtcNow;
        var scope = PrtgSensorTimelineProgressStore.ScopeFingerprint(_policy, _settings.Get());
        var progressStore = new PrtgSensorTimelineProgressStore(_backend.Blob(PrtgSensorTimelineProgressStore.BlobKey));
        progressStore.Update(state =>
        {
            state.BootstrapCycleId = "cycle-for-maintain";
            state.BootstrapSourceGeneration = _policy.SourceGeneration;
            state.BootstrapScopeFingerprint = scope;
            state.BootstrapCycleStartedAtUtc = now;
            state.BootstrapCycleAsOfUtc = now;
            state.BootstrapCycleDeadlineAtUtc = now.AddHours(12);
            state.BootstrapCycleOutcome = "running";
            state.LastServedSensorId = 777;
        });
        var active = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(
            Assert.IsType<OkObjectResult>(await controller.Get(1, 50, CancellationToken.None)).Value).Data!;

        Assert.IsType<OkObjectResult>(controller.Configure(new(24, active.Revision, 1, 50)));
        var configured = progressStore.Get();
        Assert.Equal(24, configured.BootstrapDeadlineHours);
        Assert.Equal(now.AddHours(12), configured.BootstrapCycleDeadlineAtUtc);
        Assert.Equal(777, configured.LastServedSensorId);
        Assert.IsType<ConflictObjectResult>(controller.Configure(new(36, active.Revision, 1, 50)));

        var expectedFenceVersions = new[] { "hosts", "users", "user_groups", "group_access", "permission_version" }
            .ToDictionary(key => key, key => _backend.Blob(key).ReadVersion(), StringComparer.Ordinal);
        _backend.Blob("hosts").Mutate(_ => ("[]", true));
        Assert.False(progressStore.TryUpdateExpected(configured.BootstrapCycleId, configured.BootstrapCycleOutcome,
            configured.BootstrapSettingsRevision,
            _backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion(),
            _backend.Blob("system_settings").ReadVersion(),
            _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), expectedFenceVersions,
            state => state.BootstrapDeadlineHours = 36));

        var oldDeadline = now.AddSeconds(-1);
        progressStore.Update(state =>
        {
            state.BootstrapCycleOutcome = "deadline-exceeded";
            state.BootstrapCycleDeadlineAtUtc = oldDeadline;
        });
        var expired = Assert.IsType<ApiResponse<PrtgTimelineProgressPage>>(
            Assert.IsType<OkObjectResult>(await controller.Get(1, 50, CancellationToken.None)).Value).Data!;
        Assert.IsType<OkObjectResult>(controller.Resume(new(expired.Revision, 1, 50)));

        var resumed = progressStore.Get();
        Assert.Equal("running", resumed.BootstrapCycleOutcome);
        Assert.Equal("cycle-for-maintain", resumed.BootstrapCycleId);
        Assert.Equal(777, resumed.LastServedSensorId);
        Assert.Equal(oldDeadline, resumed.PreviousBootstrapCycle!.DeadlineAtUtc);
        Assert.Equal("deadline-exceeded", resumed.PreviousBootstrapCycle.Outcome);
        Assert.True(resumed.BootstrapCycleDeadlineAtUtc > DateTimeOffset.UtcNow);
        Assert.NotEqual(firstPage.Revision, active.Revision);
    }

    private PrtgSensorTimelineConsumer Consumer(Handler handler, PrtgSamplingActivity sampling) =>
        new(_settings, _backend, () => false,
            settings => PrtgClientFactory.Create(settings, handler, new PrtgRequestBudget()), sampling);

    private void SeedMirror(IReadOnlyCollection<long> sensorIds)
    {
        var store = _backend.PrtgStore();
        var devices = sensorIds.Select(id => new PrtgDeviceRow { Objid = id + 1000, Name = $"device-{id}" }).ToArray();
        var sensors = sensorIds.Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = id + 1000, Name = $"sensor-{id}", SensorType = "Ping",
            Category = "availability", Status = "Down", Paused = false
        }).ToArray();
        store.UpsertDevices(devices, DateTime.Now);
        store.UpsertSensors(sensors, DateTime.Now);
        store.ReplaceHostMapForDate(DateTime.Today, sensorIds.Select(id => new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = id + 1000, HostId = 1,
            HostName = "fixture-host", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now
        }).ToArray());
    }

    private void SavePolicy(PrtgMonitoringPolicy policy) =>
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(saved =>
        {
            saved.Revision = policy.Revision;
            saved.CoreSystemId = policy.CoreSystemId;
            saved.SourceGeneration = policy.SourceGeneration;
            saved.EndpointHint = policy.EndpointHint;
            saved.ValidFrom = policy.ValidFrom;
            saved.HostIds = [.. policy.HostIds];
            saved.SensorIds = [.. policy.SensorIds];
            saved.SourceTimeZoneId = policy.SourceTimeZoneId;
            saved.SourceCultureName = policy.SourceCultureName;
            saved.ConfirmedBy = "1";
        });

    private PrtgMonitoringPolicy PolicyFor(IReadOnlyCollection<long> ids) => new()
    {
        Revision = _policy.Revision,
        CoreSystemId = _policy.CoreSystemId,
        SourceGeneration = _policy.SourceGeneration,
        EndpointHint = _policy.EndpointHint,
        ValidFrom = _policy.ValidFrom,
        HostIds = [.. _policy.HostIds],
        SensorIds = [.. ids],
        SourceTimeZoneId = _policy.SourceTimeZoneId,
        SourceCultureName = _policy.SourceCultureName
    };

    private PrtgSensorTimelineEvidence Evidence(long id) =>
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + id)).Get();

    private static string IdentityRows(IEnumerable<long> ids) => JsonSerializer.Serialize(new
    {
        sensors = ids.Select(id => new { objid = id, parentid = id + 1000, type = "ping", status = "Down", cumsince = $"created-{id}" }).ToArray()
    });

    private static long[] IdsFromFilters(string query) =>
        System.Text.RegularExpressions.Regex.Matches(query, @"filter_objid=(\d+)")
            .Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = new();
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers) _timers.Add((callback, state));
            return new NeverTimer();
        }

        public void Advance(TimeSpan amount)
        {
            _now = _now.Add(amount);
            List<(TimerCallback Callback, object? State)> timers;
            lock (_timers) timers = _timers.ToList();
            foreach (var (callback, state) in timers) callback(state);
        }

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static int ReadQueryInt(string query, string name)
    {
        var pair = query.TrimStart('?').Split('&').FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return pair == null ? 0 : int.Parse(pair[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed record MessageRow(long objid, string datetime, string status, string message);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
