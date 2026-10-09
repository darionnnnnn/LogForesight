using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Exercises native HTTP response metadata through bounded SQL persistence and the formal rule.</summary>
public sealed class PrtgSilentPresenceSourceFlowTests : IDisposable
{
    private const long DeviceId = 8301;
    private const long HostId = 8302;
    private const long AvailabilityId = 8303;
    private const long UnknownId = 8304;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-silent-source-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly string _url = "https://presence-fixture.example";
    private DateTimeOffset _sourceDate = DateTimeOffset.UtcNow;
    private string _sourceTimeZoneId = "UTC";
    private string _analysisTimeZoneId = TimeZoneInfo.Local.Id;
    private string _policyRevision = "presence-policy-v1";
    private static KnownIssueRule SilentRule => new()
    {
        Id = "silent-test", Platform = "prtg", Enabled = true, PrtgRuleCode = PrtgRuleEvaluator.RuleSilent,
        PrtgThreshold = PrtgRuleCatalog.DefaultSilentThreshold, Severity = IssueSeverity.Medium,
        Category = IssueCategory.Service, Description = "monitoring silence"
    };

    private PrtgMonitoringPolicy Policy => new()
    {
        Revision = _policyRevision, CoreSystemId = "fixture-core", SourceGeneration = "presence-source-v1",
        EndpointHint = EfPrtgObservationStore.SourceHintFor(_url), ValidFrom = _sourceDate.AddDays(-2),
        HostIds = [HostId], SensorIds = [AvailabilityId, UnknownId], ConfirmedBy = "test",
        SourceTimeZoneId = _sourceTimeZoneId, SourceCultureName = "en-US",
        RawTimestampTimeZoneId = "UTC", AnalysisTimeZoneId = _analysisTimeZoneId,
        TimeBasisEvidenceReference = "fixture-verified-time-basis"
    };

    public PrtgSilentPresenceSourceFlowTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        using var context = _backend.CreateContext();
        context.Database.EnsureCreated();
        context.PrtgDevices.Add(new PrtgDeviceRow { Objid = DeviceId, Name = "fixture" });
        context.PrtgSensors.AddRange(
            new PrtgSensorRow { Objid = AvailabilityId, DeviceObjid = DeviceId, SensorType = "Ping", Category = PrtgSensorCategories.Availability },
            new PrtgSensorRow { Objid = UnknownId, DeviceObjid = DeviceId, SensorType = "SNMP CPU Load", Category = PrtgSensorCategories.Cpu });
        context.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = DeviceId, MapDate = DateTime.Today, HostId = HostId, MapStatus = PrtgMapStatus.Ok });
        context.SaveChanges();
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = _policyRevision; policy.CoreSystemId = "fixture-core";
            policy.SourceGeneration = "presence-source-v1"; policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(_url);
            policy.ValidFrom = _sourceDate.AddDays(-2); policy.HostIds = [HostId];
            policy.SensorIds = [AvailabilityId, UnknownId]; policy.ConfirmedBy = "test";
            policy.SourceTimeZoneId = _sourceTimeZoneId; policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC"; policy.AnalysisTimeZoneId = _analysisTimeZoneId;
            policy.TimeBasisEvidenceReference = "fixture-verified-time-basis";
        });
        SeedTimeline(AvailabilityId, "Up", _sourceDate.AddHours(-2));
        SeedTimeline(UnknownId, "Unknown", _sourceDate.AddHours(-1));
    }

    private void SeedTimeline(long sensorId, string status, DateTimeOffset enteredAt)
    {
        PrtgResourceIdentity identity;
        using (var context = _backend.CreateContext())
        {
            identity = PrtgResourceIdentityStore.Set(context, sensorId, "presence-source-v1", DeviceId, HostId,
                $"{DeviceId}|fixture-type-{sensorId}|created-{sensorId}", $"fixture-type-{sensorId}|fixture-category|auto",
                "", true, _sourceDate.AddDays(-1));
            context.SaveChanges();
        }
        var timeline = new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + sensorId));
        timeline.Update(e =>
        {
            e.Bind(sensorId, HostId, "presence-source-v1", identity.ResourceFingerprint, identity.Generation,
                identity.Epoch, identity.ChannelGeneration, _sourceDate.AddDays(-1));
            e.Accept(_sourceDate.AddDays(-1), _sourceDate.AddSeconds(1),
                [new(sensorId, enteredAt, status, e.SourceGeneration, e.ResourceGeneration)]);
        });
    }

    private sealed class SourceHandler(Func<string, string> response, DateTimeOffset date, DateTimeOffset? deviceDate = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var query = request.RequestUri!.Query;
            var content = query.Contains("content=devices", StringComparison.Ordinal) ? "devices" : "sensors";
            var reply = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(content), Encoding.UTF8, "application/json")
            };
            reply.Headers.Date = content == "devices" ? deviceDate ?? date : date;
            return Task.FromResult(reply);
        }
    }

    private static string CompleteSource(string content) => content == "devices"
        ? JsonSerializer.Serialize(new { treesize = 1, devices = new[] { new { objid = DeviceId, paused = false } } })
        : JsonSerializer.Serialize(new { treesize = 2, sensors = new[]
        {
            new { objid = AvailabilityId, parentid = DeviceId, type = "Ping", status = "Up", status_raw = "3", paused = false },
            new { objid = UnknownId, parentid = DeviceId, type = "SNMP CPU Load", status = "Unknown", status_raw = "0", paused = false }
        } });

    private async Task<PrtgSilentDeviceSnapshot> Capture(SourceHandler handler, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, long>? producerMap = null)
    {
        using var client = new PrtgClient(_url, "fixture-token", 10, false, handler, PrtgAuthModes.Token, "", "", "");
        var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(Policy.SourceTimeZoneId);
        var sourceDay = TimeZoneInfo.ConvertTime(_sourceDate, sourceZone).Date;
        var store = new PrtgSilentPresenceSnapshotStore(_backend.Blob(PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, sourceDay)));
        var completeProducerMap = producerMap ?? new Dictionary<long, long> { [DeviceId] = HostId };
        Assert.Equal(HostId, completeProducerMap[DeviceId]);
        _ = PrtgSilentPresenceMappingFingerprint.Compute(completeProducerMap);
        return await new PrtgSilentPresenceCapture(_backend).CaptureDeviceAsync(client, store, Policy, _url,
            DeviceId, HostId, sourceDay,
            PrtgSilentPresenceMappingFingerprint.Compute(DeviceId, HostId),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
    }

    [Fact]
    public async Task NativeSourceDateAndCompleteInventoryPersistThenProduceFormalFinding()
    {
        var captured = await Capture(new SourceHandler(CompleteSource, _sourceDate, _sourceDate.AddSeconds(-9)));
        Assert.Equal(PrtgPresenceReadQuality.Complete, captured.ReadQuality);
        Assert.True(Math.Abs((captured.SourceAsOf!.Value - _sourceDate).TotalSeconds) < 1);
        Assert.NotEqual(captured.SourceAsOf, captured.DeviceStatusAsOf);
        Assert.Equal(2, captured.ReportedSensorCount);

        var analysisDay = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        var finding = Assert.Single(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(analysisDay,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
        Assert.Equal(PrtgRuleEvaluator.RuleSilent, finding.RuleCode);
        Assert.Contains("不代表主機故障", finding.Detail);
        var readiness = new PrtgSilentPresenceFormalConsumer(_backend).EvaluateWithReadiness(analysisDay,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]);
        Assert.Equal(PrtgSilentPresenceReadinessState.QualifiedHit, readiness.Devices[DeviceId].State);
        Assert.Equal(64, readiness.Devices[DeviceId].EvidenceFingerprint.Length);
    }

    [Fact]
    public async Task DisabledSilentRuleIsExplicitlyExcludedAndStaleIdentityWaits()
    {
        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        var day = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        var consumer = new PrtgSilentPresenceFormalConsumer(_backend);
        var disabledRule = new KnownIssueRule
        {
            Id = SilentRule.Id, Platform = "prtg", Enabled = false,
            PrtgRuleCode = PrtgRuleEvaluator.RuleSilent,
            PrtgThreshold = PrtgRuleCatalog.DefaultSilentThreshold,
            Severity = IssueSeverity.Medium, Category = IssueCategory.Service,
            Description = "monitoring silence"
        };
        var disabled = consumer.EvaluateWithReadiness(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [disabledRule]);
        Assert.Equal(PrtgSilentPresenceReadinessState.ExplicitlyExcluded, disabled.Devices[DeviceId].State);

        using (var context = _backend.CreateContext())
        {
            var prior = _backend.PrtgStore().GetResourceIdentities([UnknownId])[UnknownId];
            PrtgResourceIdentityStore.Set(context, UnknownId, "stale-source", DeviceId, HostId,
                prior.ResourceFingerprint, prior.InventoryFingerprint, prior.ChannelFingerprint, true, _sourceDate.AddMinutes(1));
            context.SaveChanges();
        }
        var stale = consumer.EvaluateWithReadiness(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]);
        Assert.Equal(PrtgSilentPresenceReadinessState.Waiting, stale.Devices[DeviceId].State);
        Assert.Empty(stale.Findings);
    }

    [Fact]
    public async Task DeviceSilentProofRevalidationRequiresSameDurablePointAndWholeDeviceIdentity()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = _url; });
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent { Rules = [SilentRule] });
        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        var day = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        var finding = Assert.Single(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
        var signature = PrtgFindingMapper.ToSignature(finding, day);

        Assert.True(PrtgSilentDeviceProofRevalidator.IsCurrent(_backend, HostId, day, signature, SilentRule, _url));
        signature.PrtgPresenceInventoryFingerprint = "stale-inventory";
        Assert.False(PrtgSilentDeviceProofRevalidator.IsCurrent(_backend, HostId, day, signature, SilentRule, _url));
    }

    [Fact]
    public async Task SourceDayDifferentFromAnalysisDayStillAlignsByInstantAndRejectsPreviousDay()
    {
        var sourcePoint = DateTimeOffset.FromUnixTimeSeconds(_sourceDate.ToUnixTimeSeconds());
        var analysisDay = TimeZoneInfo.ConvertTime(sourcePoint, TimeZoneInfo.Local).Date;
        var sourceZone = TimeZoneInfo.GetSystemTimeZones().First(zone =>
            TimeZoneInfo.ConvertTime(sourcePoint, zone).Date != analysisDay);
        _sourceTimeZoneId = sourceZone.Id;
        _analysisTimeZoneId = sourceZone.Id;
        _policyRevision = "presence-policy-multizone-v1";
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = _policyRevision;
            policy.SourceTimeZoneId = _sourceTimeZoneId;
            // Daily parent dates remain in service-local time even when the analysis/source
            // policy timezone labels this same instant with a different calendar date.
            policy.AnalysisTimeZoneId = _analysisTimeZoneId;
        });

        var captured = await Capture(new SourceHandler(CompleteSource, _sourceDate));
        Assert.NotEqual(analysisDay, captured.SourceDay!.Value);
        Assert.Single(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(analysisDay,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(analysisDay.AddDays(-1),
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
        var policyDay = TimeZoneInfo.ConvertTime(sourcePoint, sourceZone).Date;
        if (policyDay != analysisDay)
            Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(policyDay,
                Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
    }

    [Fact]
    public void ElapsedSourcePointRetentionKeeps180DaysAndPrunesOlderProof()
    {
        var sourceDay = new DateTime(2026, 10, 6);
        Assert.Equal(90, PrtgSilentPresenceSnapshotStore.EffectiveRetentionDays(90));
        Assert.Equal(180, PrtgSilentPresenceSnapshotStore.EffectiveRetentionDays(180));
        Assert.Equal(180, PrtgSilentPresenceSnapshotStore.EffectiveRetentionDays(3650));
        var retainedDay = sourceDay.AddDays(-180);
        var expiredDay = sourceDay.AddDays(-181);
        SaveRetentionMarker(retainedDay);
        SaveRetentionMarker(expiredDay);

        PrtgSilentPresenceSnapshotStore.PruneOlderThan(_backend.CreateContext,
            PrtgSilentPresenceSnapshotStore.OldestSourceDayToKeep(sourceDay, 180));

        Assert.NotNull(new PrtgSilentPresenceSnapshotStore(_backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, retainedDay))).Get(DeviceId));
        Assert.Null(new PrtgSilentPresenceSnapshotStore(_backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, expiredDay))).Get(DeviceId));
    }

    private void SaveRetentionMarker(DateTime sourceDay)
    {
        var snapshot = new PrtgSilentDeviceSnapshot(DeviceId, HostId, "source", "policy", 1,
            "mapping", sourceDay, null, null, null, false, 0, string.Empty, [], "retention-test")
        { ReadQuality = PrtgPresenceReadQuality.Failed };
        new PrtgSilentPresenceSnapshotStore(_backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, sourceDay))).Save(snapshot);
    }

    [Fact]
    public async Task NativePointWithoutCoveredIntervalAtItsSourceInstantRemainsWaiting()
    {
        var captured = await Capture(new SourceHandler(CompleteSource, _sourceDate));
        var timeline = new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + UnknownId));
        timeline.Update(e => e.Coverage = e.Coverage.Select(span => span with
        { Through = captured.SourceAsOf!.Value.AddSeconds(-1) }).ToList());
        var day = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
    }

    [Fact]
    public async Task MissingCountAndChangedMappingAreWaiting()
    {
        var incomplete = await Capture(new SourceHandler(content => content == "devices"
            ? CompleteSource(content)
            : JsonSerializer.Serialize(new { treesize = 3, sensors = JsonDocument.Parse(CompleteSource(content)).RootElement.GetProperty("sensors") }), _sourceDate));
        Assert.NotEqual(PrtgPresenceReadQuality.Complete, incomplete.ReadQuality);

        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId + 1 }, [SilentRule]));
    }

    [Fact]
    public async Task PartialDeviceMapAndUnrelatedScopeOrPolicyRevisionKeepUnchangedDeviceProof()
    {
        // Producer inventory scope includes another mapped device; the consumer is an authorized
        // partial run for this one unchanged device. Per-device mapping authority must still match.
        using (var context = _backend.CreateContext())
        {
            context.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = DeviceId + 100, MapDate = DateTime.Today, HostId = HostId, MapStatus = PrtgMapStatus.Ok
            });
            context.SaveChanges();
        }
        var completeProducerMap = new Dictionary<long, long>
        {
            [DeviceId] = HostId, [DeviceId + 100] = HostId
        };
        var captured = await Capture(new SourceHandler(CompleteSource, _sourceDate), producerMap: completeProducerMap);
        Assert.Equal(PrtgSilentPresenceMappingFingerprint.Compute(DeviceId, HostId), captured.MappingFingerprint);
        Assert.NotEqual(PrtgSilentPresenceMappingFingerprint.Compute(completeProducerMap), captured.MappingFingerprint);
        using (var context = _backend.CreateContext())
        {
            context.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = DeviceId + 101, MapDate = DateTime.Today, HostId = HostId, MapStatus = PrtgMapStatus.Ok
            });
            context.SaveChanges();
        }
        _policyRevision = "presence-policy-unrelated-scope-v2";
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = _policyRevision;
            policy.HostIds = [HostId, HostId + 1];
        });
        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(raw =>
            (JsonSerializer.Serialize(Guid.NewGuid().ToString("N")), true));

        var day = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        Assert.Single(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));

        // An unrelated grant edit does not replace source/time authority or this device's scope.
        // Re-capture, then verify another unrelated map edit preserves the same device evidence.
        await Capture(new SourceHandler(CompleteSource, _sourceDate), producerMap: completeProducerMap);
        using (var context = _backend.CreateContext())
        {
            context.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = DeviceId + 102, MapDate = DateTime.Today, HostId = HostId, MapStatus = PrtgMapStatus.Ok
            });
            context.SaveChanges();
        }
        Assert.Single(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));

        // Removing this host is an affected scope change: the retained old point cannot qualify it.
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = "presence-policy-host-removed";
            policy.HostIds = [HostId + 1];
        });
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
    }

    [Fact]
    public async Task SourceClockAuthorityDriftInvalidatesStoredPoint()
    {
        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        var day = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        var unverifiedTimeBasis = Policy;
        unverifiedTimeBasis.TimeBasisEvidenceReference = string.Empty;
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, unverifiedTimeBasis, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));

        var alternateZone = TimeZoneInfo.GetSystemTimeZones().First(zone => zone.Id != _sourceTimeZoneId);
        _sourceTimeZoneId = alternateZone.Id;
        _policyRevision = "presence-policy-source-clock-drift-v2";
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = _policyRevision;
            policy.SourceTimeZoneId = _sourceTimeZoneId;
        });

        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(day, Policy, _url,
            new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
    }

    [Fact]
    public async Task CancelledCaptureReplacesPriorGoodProofAndEpochChangeCannotProduceFinding()
    {
        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Capture(new SourceHandler(CompleteSource, _sourceDate), cancelled.Token));
        Assert.NotEqual(PrtgPresenceReadQuality.Complete,
            new PrtgSilentPresenceSnapshotStore(_backend.Blob(PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, _sourceDate.UtcDateTime.Date))).Get(DeviceId)!.ReadQuality);

        await Capture(new SourceHandler(CompleteSource, _sourceDate));
        using (var context = _backend.CreateContext())
        {
            PrtgResourceIdentityStore.Set(context, UnknownId, "presence-source-v1", DeviceId, HostId,
                "replacement-generation", "fixture-type|cpu|auto", "", true, DateTimeOffset.UtcNow);
            context.SaveChanges();
        }
        var analysisDay = TimeZoneInfo.ConvertTime(_sourceDate, TimeZoneInfo.Local).Date;
        Assert.Empty(new PrtgSilentPresenceFormalConsumer(_backend).Evaluate(analysisDay,
            Policy, _url, new Dictionary<long, long> { [DeviceId] = HostId }, [SilentRule]));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
