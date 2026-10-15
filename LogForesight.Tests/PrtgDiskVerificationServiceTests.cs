using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskVerificationServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;
    private readonly PrtgProbeRunState _run = new();
    private readonly PrtgBackfillRunState _backfill = new();
    private readonly PrtgStructureSyncRunState _structure = new();
    private readonly SchedulerRunState _scheduler = new();
    private readonly PrtgDiskVerificationResultStore _results;
    private readonly KnownIssueRuleStore _ruleStore;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly FakeHostStore _hosts;
    private readonly PrtgDiskVerificationService _service;

    public PrtgDiskVerificationServiceTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-disk-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "test.db")}" }, dir);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _settings.Update(s =>
        {
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("test-token");
            s.PrtgSensorTypeWhitelist = new List<string> { "snmpdiskfree" };
        });
        _results = new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey));
        _ruleStore = new KnownIssueRuleStore(_backend.Blob("rules"));
        _evidence = new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        _hosts = new FakeHostStore();
        _hosts.Upsert(new WebHost { HostName = "disk-host", Active = true });
        using (var db = _fx.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 2 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 1, DeviceObjid = 2, Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree" });
            // 試算目標是昨天；歷史候選只可使用目標日以前的對應，不能用今天的映射回填昨天。
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 2, MapDate = DateTime.Today.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Ok });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 2, MapDate = DateTime.Today, HostId = 1, MapStatus = PrtgMapStatus.Ok });
            db.SaveChanges();
        }
        _service = new PrtgDiskVerificationService(_settings, new EfPrtgStore(_fx.NewContext), _hosts,
            _run, _evidence, _results, _backfill, _structure, _scheduler, _ruleStore);
    }

    [Fact]
    public void BatchStart_RejectsZeroDuplicateAndMoreThanFiveSensorIdsWithActionableErrors()
    {
        var date = DateTime.Today.AddDays(-1);
        Assert.False(_service.TryStartBatch(new(new long[] { 0 }, date, Guid.NewGuid().ToString()), out var zeroError));
        Assert.Contains("大於 0", zeroError);
        Assert.False(_service.TryStartBatch(new(new long[] { 1, 1 }, date, Guid.NewGuid().ToString()), out var duplicateError));
        Assert.Contains("不可重複", duplicateError);
        Assert.False(_service.TryStartBatch(new(new long[] { 1, 2, 3, 4, 5, 6 }, date, Guid.NewGuid().ToString()), out var countError));
        Assert.Contains("1 至 5", countError);
        Assert.False(_run.Snapshot().IsRunning);
    }

    [Fact]
    public void RuleTrial_ReportsMissingSourceTimeAuthorityWithoutWritingEvidence()
    {
        _ruleStore.Save(new RuleFileContent { Rules = new List<KnownIssueRule>() });

        var trial = _service.AssessRuleTrial(1);
        Assert.Equal(PrtgDiskVerificationService.ParserSemanticVersion, trial.SemanticVersion);
        Assert.Equal(DateTimeKind.Utc, trial.AssessedAtUtc.Kind);
        Assert.Equal("source-authority-unconfirmed", trial.Status);
        Assert.Contains("授權", trial.Message);
        Assert.Empty(_evidence.GetAll());
        Assert.Empty(_results.GetRecent());
    }

    [Fact]
    public async Task NormalServiceRunUsesSavedContractAndPersistsVerifiedEvidence()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.UtcNow;
        var effectiveFrom = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
        ConfigureReadySourcePolicy("UTC");
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey),
            _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey), 1, 2, 1, "snmpdiskfree",
            effectiveFrom, sourceGeneration: "source-v1", channelIdentifier: "8");
        var day = PrtgDiskVerificationService.CompletedDay(DateTime.UtcNow, profile.AnalysisTimeZoneId);
        var window = PrtgDiskVerificationService.AnalysisDayWindow(day, profile.AnalysisTimeZoneId);
        var sourceApiZone = TimeZoneInfo.FindSystemTimeZoneById(profile.SourceApiTimeZoneId);
        var storedTimesUtc = Enumerable.Range(0, 6).Select(index => window.StartUtc.AddHours(index * 4)).ToArray();
        using (var db = _fx.NewContext())
        {
            db.PrtgValues.AddRange(storedTimesUtc.Select(timestampUtc => new PrtgValueRow
            {
                SensorObjid = 1, PeriodStart = DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTimeFromUtc(timestampUtc, sourceApiZone), DateTimeKind.Unspecified), AvgValue = 72.5,
                MinValue = 72.5, MaxValue = 72.5, Coverage = 100, Quality = PrtgDataQuality.Ok,
                CreatedAt = DateTime.UtcNow
            }));
            db.SaveChanges();
        }
        var rawHistory = "{\"histdata\":[" + string.Join(",", storedTimesUtc.Take(5).Select(timestampUtc =>
            "{\"datetime_raw\":" + timestampUtc.ToOADate().ToString("R", CultureInfo.InvariantCulture) +
            ",\"Free Space_raw\":72.5}")) + "]}";

        var requests = new List<string>();
        var driftAfterHistory = false;
        var clientFactory = new Func<SystemSettings, PrtgClient>(currentSettings =>
            PrtgClientFactory.Create(currentSettings, new DelegateHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            var body = path.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1,\"parentid\":2,\"type\":\"snmpdiskfree\",\"cumsince_raw\":\"fixture-created-1\"}]}"
                : path.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true}]}"
                    : rawHistory;
            if (path.Contains("historicdata.json", StringComparison.Ordinal) && driftAfterHistory)
            {
                driftAfterHistory = false;
                new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey))
                    .Update(policy => policy.SourceGeneration = "source-v2");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }), new PrtgRequestBudget()));
        var runState = new PrtgProbeRunState();
        var service = new PrtgDiskVerificationService(_settings, store, _hosts, runState, _evidence,
            _results, new PrtgBackfillRunState(), new PrtgStructureSyncRunState(), new SchedulerRunState(),
            _ruleStore, clientFactory);
        var savedPolicyContext = store.GetTrustedSamplingPolicyContext("conservative", 15);
        Assert.True(savedPolicyContext.Policy.Ready(_settings.Get().PrtgUrl),
            $"The fixture policy must be ready: core={savedPolicyContext.Policy.CoreSystemId.Length > 0}, " +
            $"generation={savedPolicyContext.Policy.SourceGeneration.Length > 0}, " +
            $"validFrom={savedPolicyContext.Policy.ValidFrom != default}, " +
            $"endpointMatch={savedPolicyContext.Policy.EndpointHint == EfPrtgObservationStore.SourceHintFor(_settings.Get().PrtgUrl)}, " +
            $"hosts={savedPolicyContext.Policy.HostIds.Count}, sensors={savedPolicyContext.Policy.SensorIds.Count}, " +
            $"apiZone={savedPolicyContext.Policy.SourceTimeZoneId.Length > 0}, " +
            $"culture={savedPolicyContext.Policy.SourceCultureName.Length > 0}.");
        Assert.NotEmpty(savedPolicyContext.Policy.SourceAuthorityFingerprint(_settings.Get().PrtgUrl));
        Assert.True(savedPolicyContext.Strategy.Ready, "The fixture strategy must be ready before the API-to-service flow.");
        var savedIdentity = store.GetResourceIdentity(1);
        var savedProfile = store.GetTrustedSamplingProfiles([1]).GetValueOrDefault(1);
        var profileResolution = PrtgTrustedSamplingProfileResolver.Resolve(savedProfile, savedIdentity,
            savedPolicyContext.Policy, 1, "snmpdiskfree", savedPolicyContext.Strategy,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        Assert.True(profileResolution.Ready,
            $"Fixture profile must resolve before HTTP: rejection={profileResolution.RejectionReason}, " +
            $"missing={string.Join(',', profileResolution.MissingFacts)}.");
        var publishedDate = service.GetStatus(1);
        Assert.Equal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), publishedDate.CompletedAnalysisDay);
        Assert.Equal(profile.AnalysisTimeZoneId, publishedDate.AnalysisTimeZoneId);

        var endpoint = new PrtgDiskVerificationController(service, new NoopAudit(), new MaintainUser());
        var endpointResult = endpoint.Start(new(1, day.ToDateTime(TimeOnly.MinValue), Guid.NewGuid().ToString()));
        Assert.IsType<OkObjectResult>(endpointResult);
        Assert.True(SpinWait.SpinUntil(() => !runState.Snapshot().IsRunning, TimeSpan.FromSeconds(10)));

        var result = Assert.IsType<PrtgDiskVerificationResult>(_results.Get(1));
        Assert.True(result.Status == nameof(PrtgDiskSemanticProbeStatus.Verified),
            $"status={result.Status}; message={result.Summary}; requests={requests.Count}.");
        Assert.Equal("8", result.ChannelIdentifier);
        Assert.Equal("Free Space", result.ChannelName);
        Assert.Equal(5, result.ComparedPointCount);
        Assert.True(result.ValuesMatch);
        using (var savedValues = _fx.NewContext()) Assert.Equal(6, savedValues.PrtgValues.Count());
        Assert.Single(_evidence.GetAll());
        Assert.Equal(3, requests.Count);
        Assert.Contains(requests, path => path.Contains("content=sensors", StringComparison.Ordinal));
        Assert.Contains(requests, path => path.Contains("content=channels", StringComparison.Ordinal));
        Assert.Contains(requests, path => path.Contains("historicdata.json", StringComparison.Ordinal));
        var evidenceCount = _evidence.GetAll().Count;
        driftAfterHistory = true;
        Assert.IsType<OkObjectResult>(endpoint.Start(new(1, day.ToDateTime(TimeOnly.MinValue), Guid.NewGuid().ToString())));
        Assert.True(SpinWait.SpinUntil(() => !runState.Snapshot().IsRunning, TimeSpan.FromSeconds(10)));
        Assert.Equal("Stale", _results.Get(1)?.Status);
        Assert.Equal(evidenceCount, _evidence.GetAll().Count);
    }

    [Fact]
    public void RepairInvalidatedProbeCannotBeManuallyConfirmedUntilNormalProbeSucceeds()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.UtcNow;
        var effectiveFrom = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
        ConfigureReadySourcePolicy("UTC");
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey),
            _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey), 1, 2, 1, "snmpdiskfree",
            effectiveFrom, sourceGeneration: "source-v1", channelIdentifier: "8");
        var identity = store.GetResourceIdentity(1);
        var day = PrtgDiskVerificationService.CompletedDay(now, profile.AnalysisTimeZoneId);
        var oldCandidate = new PrtgDiskVerificationResult(1, 2, 1, "snmpdiskfree", "NeedsManualReview",
            "candidate before repair", "8", "Free Space", "%", 1, "descending-danger", 5, true,
            now, day.ToDateTime(TimeOnly.MinValue), PrtgDiskVerificationService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch);
        _evidence.ConfirmManually(new(1, 2, 1, "snmpdiskfree", "8", "Free Space", "%", 1,
                "descending-danger"), 42, "fixture evidence before repair", now.AddMinutes(-2),
            PrtgDiskVerificationService.ParserSemanticVersion, identity.SourceGeneration,
            identity.Generation, identity.ChannelGeneration, identity.Epoch);
        var unrelated = oldCandidate with { SensorObjid = 2, DeviceObjid = 3, HostId = 3, Summary = "other fixture" };
        var raw = System.Text.Json.JsonSerializer.Serialize(new Dictionary<long, PrtgDiskVerificationResult>
        {
            [1] = oldCandidate, [2] = unrelated
        });
        var secondKey = raw.IndexOf("\"2\"", StringComparison.Ordinal);
        var comma = raw.LastIndexOf(',', secondKey);
        Assert.True(secondKey > 0 && comma > 0);
        var resultBlob = _backend.Blob(PrtgDiskVerificationResultStore.BlobKey);
        resultBlob.Mutate(_ => (raw.Remove(comma, 1), true));
        var repair = new PrtgAuthorityRepairStore(_backend);
        var snapshot = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskVerificationResults);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, snapshot.State);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskVerificationResults, snapshot.Version,
            [new(comma, 0, ",", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        var semanticBlob = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey);
        var semanticRaw = semanticBlob.Read()!;
        var semanticClose = semanticRaw.LastIndexOf('}');
        Assert.True(semanticClose > 0);
        semanticBlob.Mutate(current => (current!.Remove(semanticClose, 1), true));
        var semanticSnapshot = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, semanticSnapshot.State);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, semanticSnapshot.Version,
            [new(semanticClose, 0, "}", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        var invalidated = _results.Get(1)!;
        Assert.Equal("Stale", invalidated.Status);
        Assert.Equal("repair-invalidated", invalidated.ParserSemanticVersion);
        var invalidatedEvidence = _evidence.Get(1)!;
        Assert.Equal("repair-invalidated", invalidatedEvidence.ParserSemanticVersion);
        Assert.Equal(now.AddMinutes(-2), invalidatedEvidence.VerifiedAtUtc);
        Assert.True(invalidatedEvidence.RepairInvalidatedAtUtc > oldCandidate.CheckedAtUtc);
        Assert.Throws<InvalidOperationException>(() => _service.ConfirmManually(
            new(1, "8", "Free Space", "%", 1, "descending-danger", "must reject old candidate"), 42));

        var window = PrtgDiskVerificationService.AnalysisDayWindow(day, profile.AnalysisTimeZoneId);
        using (var db = _fx.NewContext())
        {
            db.PrtgValues.AddRange(Enumerable.Range(0, 6).Select(index => new PrtgValueRow
            {
                SensorObjid = 1,
                PeriodStart = DateTime.SpecifyKind(window.StartUtc.AddHours(index * 4), DateTimeKind.Unspecified),
                AvgValue = 72.5, MinValue = 72.5, MaxValue = 72.5, Coverage = 100,
                Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.UtcNow
            }));
            db.SaveChanges();
        }
        var history = "{\"histdata\":[" + string.Join(",", Enumerable.Range(0, 5).Select(index =>
            "{\"datetime_raw\":" + window.StartUtc.AddHours(index * 4).ToOADate().ToString("R", CultureInfo.InvariantCulture) +
            ",\"Free Space_raw\":72.5}")) + "]}";
        var clientFactory = new Func<SystemSettings, PrtgClient>(currentSettings =>
            PrtgClientFactory.Create(currentSettings, new DelegateHandler((request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                var body = path.Contains("content=sensors", StringComparison.Ordinal)
                    ? "{\"sensors\":[{\"objid\":1,\"parentid\":2,\"type\":\"snmpdiskfree\",\"cumsince_raw\":\"fixture-created-1\"}]}"
                    : path.Contains("content=channels", StringComparison.Ordinal)
                        ? "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true}]}"
                        : history;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }), new PrtgRequestBudget()));
        var runState = new PrtgProbeRunState();
        var normalProbe = new PrtgDiskVerificationService(_settings, store, _hosts, runState, _evidence,
            _results, new PrtgBackfillRunState(), new PrtgStructureSyncRunState(), new SchedulerRunState(),
            _ruleStore, clientFactory);
        Assert.True(normalProbe.TryStart(new(1, day.ToDateTime(TimeOnly.MinValue), Guid.NewGuid().ToString()), out var error), error);
        Assert.True(SpinWait.SpinUntil(() => _results.Get(1)?.Status == nameof(PrtgDiskSemanticProbeStatus.Verified),
            TimeSpan.FromSeconds(10)));
        Assert.Null(_evidence.Get(1)?.RepairInvalidatedAtUtc);
        Assert.True(_service.CheckEvidence(1).IsValid);
    }

    [Fact]
    public void SemanticSyntaxRepairBlocksOldManualCandidateAndFailsClosedWithoutUsableBarrier()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.UtcNow;
        var fixtureHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        ConfigureReadySourcePolicy("UTC");
        var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            _fx.Blob(PrtgMonitoringPolicyStore.BlobKey),
            _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey), 1, 2, 1, "snmpdiskfree",
            fixtureHour.AddHours(-1), sourceGeneration: "source-v1", channelIdentifier: "8");
        var identity = store.GetResourceIdentity(1);
        var day = PrtgDiskVerificationService.CompletedDay(now, profile.AnalysisTimeZoneId);
        var evidenceAt = now.AddMinutes(-5);
        _evidence.ConfirmManually(new(1, 2, 1, "snmpdiskfree", "8", "Free Space", "%", 1,
                "descending-danger"), 42, "fixture evidence before semantic repair", evidenceAt,
            PrtgDiskVerificationService.ParserSemanticVersion, identity.SourceGeneration,
            identity.Generation, identity.ChannelGeneration, identity.Epoch);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "candidate before repair",
            "8", "Free Space", "%", 1, "descending-danger", 5, true, now.AddMinutes(-1),
            day.ToDateTime(TimeOnly.MinValue), PrtgDiskVerificationService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));

        var semanticBlob = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey);
        var raw = semanticBlob.Read()!;
        var closingBrace = raw.LastIndexOf('}');
        Assert.True(closingBrace > 0);
        semanticBlob.Mutate(current => (current!.Remove(closingBrace, 1), true));
        var repair = new PrtgAuthorityRepairStore(_backend);
        var snapshot = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, snapshot.State);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, snapshot.Version,
            [new(closingBrace, 0, "}", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        var repaired = _evidence.Get(1)!;
        Assert.Equal(evidenceAt, repaired.VerifiedAtUtc);
        Assert.Equal(PrtgDiskSemanticEvidenceStore.RepairInvalidationMarker, repaired.ParserSemanticVersion);
        Assert.True(repaired.RepairInvalidatedAtUtc > now.AddMinutes(-1));
        Assert.Throws<InvalidOperationException>(() => _service.ConfirmManually(
            new(1, "8", "Free Space", "%", 1, "descending-danger", "old candidate"), 42));

        _evidence.Update(all => all[1] = repaired with { RepairInvalidatedAtUtc = null });
        Assert.Throws<InvalidOperationException>(() => _service.ConfirmManually(
            new(1, "8", "Free Space", "%", 1, "descending-danger", "missing barrier"), 42));
        _evidence.Update(all => all[1] = repaired with { RepairInvalidatedAtUtc = now.AddMinutes(1) });
        Assert.Throws<InvalidOperationException>(() => _service.ConfirmManually(
            new(1, "8", "Free Space", "%", 1, "descending-danger", "future barrier"), 42));
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void AnalysisDayWindowUsesRealDayLengthAcrossDst(int year, int month, int day, int expectedHours)
    {
        var window = PrtgDiskVerificationService.AnalysisDayWindow(new DateOnly(year, month, day), "Eastern Standard Time");
        Assert.Equal(TimeSpan.FromHours(expectedHours), window.EndUtc - window.StartUtc);
        Assert.Equal(DateTimeKind.Utc, window.StartUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, window.EndUtc.Kind);
    }

    [Fact]
    public void CompletedAnalysisDayIsIndependentOfBrowserAndServerLocalYesterday()
    {
        var utcNearMidnight = new DateTime(2026, 10, 14, 1, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 12), PrtgDiskVerificationService.CompletedDay(utcNearMidnight, "Pacific Standard Time"));
        Assert.Equal(new DateOnly(2026, 10, 13), DateOnly.FromDateTime(utcNearMidnight).AddDays(-1));
    }

    [Fact]
    public void StatusWithMalformedSavedPolicyReportsAuthorityUnavailableInsteadOfThrowing()
    {
        ConfigureReadySourcePolicy("UTC");
        using (var db = _fx.NewContext())
        {
            var policyRow = db.Blobs.Single(row => row.BlobKey == PrtgMonitoringPolicyStore.BlobKey);
            policyRow.Content = "{";
            db.SaveChanges();
        }

        var status = _service.GetStatus(1);

        Assert.Null(status.CompletedAnalysisDay);
        Assert.Null(status.AnalysisTimeZoneId);
    }

    private PrtgResourceIdentity CaptureCurrentIdentity(EfPrtgStore store)
    {
        var now = DateTime.UtcNow;
        var effectiveFrom = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
        ConfigureReadySourcePolicy("UTC");
        PrtgResourceFixture.ConfigureDiskTrustedProfile(store, _fx.Blob(PrtgMonitoringPolicyStore.BlobKey),
            _fx.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey), 1, 2, 1, "snmpdiskfree",
            effectiveFrom, sourceGeneration: "source-v1", channelIdentifier: "8");
        return store.GetResourceIdentity(1);
    }

    private void ConfigureReadySourcePolicy(string sourceApiTimeZone)
    {
        var url = _settings.Get().PrtgUrl;
        new SystemSettingsStore(_fx.Blob("system_settings")).Update(fixtureSettings =>
        {
            fixtureSettings.PrtgEnabled = true;
            fixtureSettings.PrtgUrl = url;
            fixtureSettings.PrtgFetchStrategy = "conservative";
        });
        new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.CoreSystemId = "disk-verification-fixture-core";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(url);
            policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            policy.SourceTimeZoneId = sourceApiTimeZone;
            policy.SourceCultureName = "en-US";
        });
    }

    private static (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category,
        string? Unit, bool Paused, bool DevicePaused) CurrentDisk() =>
        (1, 2, 1, "disk", "snmpdiskfree", PrtgSensorCategories.Disk, "%", false, false);

    [Fact]
    public void RuleTrial_SelectsOrdinalFirstEnabledDiskRuleRegardlessOfStoredOrder()
    {
        SaveTrialRules(
            TrialRule("z-disk", enabled: true, lowWater: 30),
            TrialRule("b-disk", enabled: true, lowWater: 22),
            TrialRule("a-disk", enabled: false, lowWater: 10));

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("b-disk", trial.RuleId);
        Assert.True(trial.RuleEnabled);
        Assert.Equal(22, trial.LowWaterPercent);
    }

    [Fact]
    public void RuleTrial_PrefersEnabledDiskRuleEvenWhenDisabledIdSortsFirst()
    {
        SaveTrialRules(
            TrialRule("a-disabled", enabled: false, lowWater: 10),
            TrialRule("z-enabled", enabled: true, lowWater: 30));

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("z-enabled", trial.RuleId);
        Assert.True(trial.RuleEnabled);
        Assert.Equal(30, trial.LowWaterPercent);
    }

    [Fact]
    public void RuleTrial_UsesOrdinalFirstDisabledDiskRuleWhenNoEnabledDiskRuleExists()
    {
        SaveTrialRules(
            TrialRule("z-disabled", enabled: false, lowWater: 30),
            TrialRule("b-disabled", enabled: false, lowWater: 22),
            TrialRule("0-invalid-disabled", enabled: false, lowWater: 200),
            new KnownIssueRule
            {
                Id = "a-enabled-cpu", Origin = "custom", Enabled = true, Platform = "prtg",
                PrtgRuleCode = PrtgRuleEvaluator.RuleDown, PrtgSensorCategory = "cpu", PrtgThreshold = 5,
                Description = "CPU down", PlainExplanation = "CPU is down", CountThreshold = 1,
                Impact = "CPU service may be unavailable", LikelyCauses = new[] { "CPU service failure" },
                NextSteps = new[] { "Check CPU service health" }
            });

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("b-disabled", trial.RuleId);
        Assert.False(trial.RuleEnabled);
        Assert.Equal(22, trial.LowWaterPercent);
    }

    [Fact]
    public void RuleTrial_ExcludesNonDiskRulesWhenChoosingTrialRule()
    {
        SaveTrialRules(new KnownIssueRule
        {
            Id = "cpu-rule", Origin = "custom", Enabled = true, Platform = "prtg",
            PrtgRuleCode = PrtgRuleEvaluator.RuleDown, PrtgSensorCategory = "cpu", PrtgThreshold = 5,
            Description = "CPU down", PlainExplanation = "CPU is down", CountThreshold = 1,
            Impact = "CPU service may be unavailable", LikelyCauses = new[] { "CPU service failure" },
            NextSteps = new[] { "Check CPU service health" }
        });

        var trial = _service.AssessRuleTrial(1);

        Assert.Null(trial.RuleId);
        Assert.Null(trial.RuleEnabled);
        Assert.Equal("no-configured-rule", trial.Status);
    }

    [Fact]
    public void RuleTrial_SkipsInvalidEnabledRuleBeforeSelectingValidatedDiskRule()
    {
        var invalid = TrialRule("a-invalid-enabled", enabled: true, lowWater: 200);
        SaveTrialRules(invalid, TrialRule("z-valid-disabled", enabled: false, lowWater: 30));

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("z-valid-disabled", trial.RuleId);
        Assert.False(trial.RuleEnabled);
        Assert.Equal(30, trial.LowWaterPercent);
    }

    [Fact]
    public void RuleTrial_LoadFailureDoesNotPresentSeedAsConfiguredRule()
    {
        EnsureSourceAuthority();
        _ruleStore.Save(new RuleFileContent { Rules = new List<KnownIssueRule> { TrialRule("persisted", true, 20) } });
        _backend.Blob("rules").Mutate(_ => ("{ invalid json", 0));

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("no-configured-rule", trial.Status);
        Assert.Null(trial.RuleId);
        Assert.Null(trial.RuleEnabled);
    }

    [Fact]
    public void RuleTrial_ReportsSixOfTwentyEightDaysAsInsufficient()
    {
        SaveTrialRule();
        var end = DateTime.Today.AddDays(-1);
        using (var db = _fx.NewContext())
        {
            var sensor = db.PrtgSensors.Single();
            var values = new List<PrtgValueRow>();
            var maps = new List<PrtgHostMapRow>();
            for (var offset = -5; offset <= 0; offset++)
            {
                var day = end.AddDays(offset);
                if (day != end) maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
                for (var hour = 0; hour < 12; hour++) values.Add(new()
                {
                    SensorObjid = sensor.Objid, PeriodStart = day.Date.AddHours(hour), AvgValue = 70,
                    MinValue = 70, MaxValue = 70, Coverage = 100, Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Today
                });
            }
            db.PrtgHostMaps.AddRange(maps);
            db.PrtgValues.AddRange(values);
            db.SaveChanges();
        }
        ConfigureReadySourcePolicy("UTC");
        PrtgResourceFixture.AuthorizeSeededDiskHistory(_fx.NewContext, 1, 2, 1, "snmpdiskfree", "source-v1");
        var trial = _service.AssessRuleTrial(1);
        Assert.Equal("insufficient-data", trial.Status);
        Assert.Equal(6, trial.UsableDays);
        Assert.Equal(28, trial.RequiredDays);
    }

    [Fact]
    public void RuleTrial_WithVerifiedTwentyEightDaysReportsNoHitAndMakesNoBusinessWrite()
    {
        SaveTrialRule(enabled: false);
        var end = DateTime.Today.AddDays(-1);
        using (var db = _fx.NewContext())
        {
            var sensor = db.PrtgSensors.Single();
            var values = new List<PrtgValueRow>();
            var maps = new List<PrtgHostMapRow>();
            for (var offset = -27; offset <= 0; offset++)
            {
                var day = end.AddDays(offset);
                if (day != end) maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
                for (var hour = 0; hour < 12; hour++) values.Add(new()
                {
                    SensorObjid = sensor.Objid, PeriodStart = day.Date.AddHours(hour), AvgValue = 70,
                    MinValue = 70, MaxValue = 70, Coverage = 100, Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Today
                });
            }
            db.PrtgHostMaps.AddRange(maps);
            db.PrtgValues.AddRange(values);
            db.SaveChanges();
        }
        ConfigureReadySourcePolicy("UTC");
        PrtgResourceFixture.AuthorizeSeededDiskHistory(_fx.NewContext, 1, 2, 1, "snmpdiskfree", "source-v1");
        ConfirmTrialSemanticEvidence();
        var beforeResults = _results.GetRecent().Count;
        var trial = _service.AssessRuleTrial(1);
        Assert.Equal("ready-no-hit", trial.Status);
        Assert.True(trial.SemanticVerified);
        Assert.Equal(28, trial.UsableDays);
        Assert.False(trial.PredictedHit);
        Assert.Contains("真實正向效果尚待觀察", trial.Message);
        Assert.False(trial.RuleEnabled);
        Assert.Equal(beforeResults, _results.GetRecent().Count);
        using var dbAfter = _fx.NewContext();
        Assert.Equal(336, dbAfter.PrtgValues.Count());
    }

    [Fact]
    public void RuleTrial_WithMinimumTwentyEightDayHistoryReportsExactSevenDayLowWaterAsTrialOnly()
    {
        SaveTrialRule(enabled: false, thresholds: new PrtgDiskTrendThresholds(20, 0.5, 30, 28, 28, 0.70, 7));
        SaveTrialHistory(28, dayIndex => 54 - dayIndex);
        ConfirmTrialSemanticEvidence();
        var beforeResults = _results.GetRecent().Count;

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("ready-hit", trial.Status);
        Assert.True(trial.SemanticVerified);
        Assert.Equal(28, trial.UsableDays);
        Assert.Equal(28, trial.RequiredDays);
        Assert.True(trial.PredictedHit);
        Assert.Equal(27, trial.CurrentAvailablePercent);
        Assert.Equal(7d, trial.EstimatedDaysToLowWater);
        Assert.Equal(7d, trial.MaximumDaysToLowWater);
        Assert.Equal("predicted-low-water", Assert.Single(trial.Reasons));
        Assert.Contains("真實正向尚未觀察", trial.Message);
        Assert.Equal("真實正向尚未觀察", trial.RealPositiveStatus);
        Assert.False(trial.RuleEnabled);
        Assert.Equal(beforeResults, _results.GetRecent().Count);
        using var dbAfter = _fx.NewContext();
        Assert.Equal(336, dbAfter.PrtgValues.Count());
    }

    [Fact]
    public void RuleTrial_AllowsLegalWindowOverThirtyFiveDaysAndReportsPositiveHitAsTrialOnly()
    {
        var thresholds = new PrtgDiskTrendThresholds(20, 0.5, 30, 36, 36, 0.70);
        SaveTrialRule(thresholds: thresholds);
        SaveTrialHistory(36, offset => 45 - offset);
        ConfirmTrialSemanticEvidence();

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("ready-hit", trial.Status);
        Assert.True(trial.PredictedHit);
        Assert.Equal(28, trial.UsableDays);
        Assert.Equal(28, trial.RequiredDays);
        Assert.Contains("真實正向尚未觀察", trial.Message);
        Assert.Equal("真實正向尚未觀察", trial.RealPositiveStatus);
    }

    [Fact]
    public void RuleTrial_ReportsTrueReadinessOrSemanticExclusionSeparatelyFromNoHit()
    {
        SaveTrialRule();
        SaveTrialHistory(35, _ => 70);

        var trial = _service.AssessRuleTrial(1);

        Assert.Equal("excluded", trial.Status);
        Assert.False(trial.PredictedHit);
        Assert.Equal(nameof(PrtgDiskDecisionExclusion.SemanticNotReady), trial.Exclusion);
    }

    private void SaveTrialHistory(int days, Func<int, double> valueForDay)
    {
        var end = DateTime.Today.AddDays(-1);
        using var db = _fx.NewContext();
        var sensor = db.PrtgSensors.Single();
        var values = new List<PrtgValueRow>();
        var maps = new List<PrtgHostMapRow>();
        for (var offset = -(days - 1); offset <= 0; offset++)
        {
            var day = end.AddDays(offset);
            if (day != end) maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
            for (var hour = 0; hour < 12; hour++) values.Add(new()
            {
                SensorObjid = sensor.Objid, PeriodStart = day.Date.AddHours(hour), AvgValue = valueForDay(offset + days - 1),
                MinValue = valueForDay(offset + days - 1), MaxValue = valueForDay(offset + days - 1), Coverage = 100,
                Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Today
            });
        }
        db.PrtgHostMaps.AddRange(maps);
        db.PrtgValues.AddRange(values);
        db.SaveChanges();
        ConfigureReadySourcePolicy("UTC");
        PrtgResourceFixture.AuthorizeSeededDiskHistory(_fx.NewContext, sensor.Objid, sensor.DeviceObjid,
            1, sensor.SensorType, "source-v1");
    }

    private void ConfirmTrialSemanticEvidence()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var policyBlob = _fx.Blob(PrtgMonitoringPolicyStore.BlobKey);
        var identity = store.GetResourceIdentity(1);
        if (!PrtgConsumerProfileFixtureClosure.HasCurrentQualifiedBinding(policyBlob, identity))
        {
            identity = CaptureCurrentIdentity(store);
            identity = PrtgResourceFixture.BindChannel(store, identity, "free", "Free", "%", 1,
                "descending-danger");
        }
        _evidence.ConfirmManually(new(1, 2, 1, "snmpdiskfree", "free", "Free", "%", 1, "descending-danger"),
            42, "test fixture semantic evidence", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion,
            identity.SourceGeneration, identity.Generation, identity.ChannelGeneration, identity.Epoch);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "Verified", "matched", "free", "Free", "%", 1,
            "descending-danger", 1, true, DateTime.UtcNow, DateTime.Today.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));
        Assert.True(PrtgResourceQualification.IsChannelCurrent(
            _evidence.Get(1), _results.Get(1), store.GetResourceIdentity(1)));
    }

    private void SaveTrialRule(bool enabled = true, PrtgDiskTrendThresholds? thresholds = null)
    {
        _ruleStore.Save(new RuleFileContent
        {
            Rules = new List<KnownIssueRule> { new()
            {
                Id = "trial-disk", Origin = "custom", Enabled = enabled, Platform = "prtg", PrtgRuleCode = "disk_free_trend",
                PrtgSensorCategory = PrtgSensorCategories.Disk, PrtgDiskTrendThresholds = thresholds ?? PrtgDiskTrendThresholds.Provisional,
                Description = "Disk free trend trial", PlainExplanation = "Disk capacity is declining",
                CountThreshold = 1, Impact = "Disk capacity may be exhausted", LikelyCauses = new[] { "Free disk space is declining" },
                NextSteps = new[] { "Review disk usage and capacity" }
            } }
        });
    }

    private void SaveTrialRules(params KnownIssueRule[] rules)
    {
        EnsureSourceAuthority();
        _ruleStore.Save(new RuleFileContent { Rules = rules.ToList() });
    }

    private void EnsureSourceAuthority() => CaptureCurrentIdentity(new EfPrtgStore(_fx.NewContext));

    private static KnownIssueRule TrialRule(string id, bool enabled, double lowWater, string category = PrtgSensorCategories.Disk) => new()
    {
        Id = id, Origin = "custom", Enabled = enabled, Platform = "prtg", PrtgRuleCode = "disk_free_trend",
        PrtgSensorCategory = category,
        PrtgDiskTrendThresholds = new PrtgDiskTrendThresholds(lowWater, 0.5, 30, 36, 36, 0.70),
        Description = "Disk free trend trial", PlainExplanation = "Disk capacity is declining",
        CountThreshold = 1, Impact = "Disk capacity may be exhausted", LikelyCauses = new[] { "Free disk space is declining" },
        NextSteps = new[] { "Review disk usage and capacity" }
    };

    [Fact]
    public void Start_RejectsEachConflictingRunWithSpecificReason()
    {
        EnsureSourceAuthority();
        Assert.True(_scheduler.TryBeginRun("manual:test", out _));
        AssertConflict("取數排程");
        _scheduler.EndRun();

        Assert.True(_backfill.TryBeginRun(out _));
        AssertConflict("回填");
        _backfill.FinishRun(false, false);

        Assert.True(_structure.TryBeginRun(out _));
        AssertConflict("結構同步");
        _structure.FinishRun(false, false);
    }

    [Fact]
    public void CancelAndStatus_DoNotClaimOrCancelAnotherSharedProbe()
    {
        Assert.True(_run.TryBeginRun(out _));

        Assert.False(_service.Cancel());
        Assert.False(_service.GetStatus().IsRunning);
        Assert.True(_run.Snapshot().IsRunning);

        _run.FinishRun(false, false);
    }

    [Theory]
    [InlineData("Mismatch", false, 3, 0)]
    [InlineData("Failed", true, 3, 0)]
    [InlineData("NeedsManualReview", false, 3, 0)]
    [InlineData("NeedsManualReview", true, 0, 0)]
    [InlineData("NeedsManualReview", true, 3, 25)]
    public void ManualConfirmation_RejectsFailedMismatchedEmptyOrStaleProbe(string status, bool? matches, int count, int ageHours)
    {
        var now = DateTime.UtcNow;
        _results.Save(new(1, 2, 3, "snmpdiskfree", status, "probe", "1", "Free", "%", 1,
            "descending-danger", count, matches, now.AddHours(-ageHours), DateTime.Today.AddDays(-1),
            PrtgDiskVerificationService.ParserSemanticVersion));

        Assert.Throws<InvalidOperationException>(() => _service.ConfirmManually(
            new(1, "1", "Free", "%", 1, "descending-danger", "checked"), 42));
    }

    [Fact]
    public void ManualEvidence_IsValidFromRecentReviewProbe_ThenInvalidatedByContradictoryProbe()
    {
        ConfirmManualEvidenceFromPartialProbe();
        Assert.True(_service.CheckEvidence(1).IsValid);

        Thread.Sleep(5);
        var identity = new EfPrtgStore(_fx.NewContext).GetResourceIdentity(1);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "channel differs",
            "channel-2", "Used", "%", 1, "descending-danger", 1, true, DateTime.UtcNow,
            DateTime.Today.AddDays(-1), PrtgDiskVerificationService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));
        Assert.False(_service.CheckEvidence(1).IsValid);
    }

    [Fact]
    public void ManualEvidence_RemainsValidWhenMatchingProbeIsOlderThan25Hours()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var identity = CaptureCurrentIdentity(store);
        identity = PrtgResourceFixture.BindChannel(store, identity, "channel-1", "Free space", "%", 1,
            "descending-danger");
        var verifiedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "metadata incomplete",
            "channel-1", "Free space", "%", 1, "descending-danger", 1, true, verifiedAt,
            DateTime.Today.AddDays(-1), PrtgDiskVerificationService.ParserSemanticVersion,
            SourceGeneration: identity.SourceGeneration, ResourceGeneration: identity.Generation,
            ChannelGeneration: identity.ChannelGeneration, IdentityEpoch: identity.Epoch));
        _evidence.ConfirmManually(
            new(1, 2, 1, "snmpdiskfree", "channel-1", "Free space", "%", 1, "descending-danger"),
            42, "reviewed", verifiedAt, PrtgDiskVerificationService.ParserSemanticVersion,
            identity.SourceGeneration, identity.Generation, identity.ChannelGeneration, identity.Epoch);

        Assert.True(DateTime.UtcNow - verifiedAt > TimeSpan.FromHours(25));
        Assert.True(_service.CheckEvidence(1).IsValid);
    }

    [Fact]
    public void ManualEvidence_IsInvalidatedWhenCurrentMappingChanges()
    {
        ConfirmManualEvidenceFromPartialProbe();
        Assert.True(_service.CheckEvidence(1).IsValid);

        new EfPrtgStore(_fx.NewContext).ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 2, MapDate = DateTime.Today, HostId = 99,
                MapStatus = PrtgMapStatus.Ok }]);
        Assert.False(_service.CheckEvidence(1).IsValid);
    }

    private void ConfirmManualEvidenceFromPartialProbe()
    {
        var identity = CaptureCurrentIdentity(new EfPrtgStore(_fx.NewContext));
        var probeAt = DateTime.UtcNow;
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "metadata incomplete",
            null, null, null, null, null, 1, true, probeAt,
            PrtgDiskVerificationService.CompletedDay(probeAt, "UTC").ToDateTime(TimeOnly.MinValue),
            PrtgDiskVerificationService.ParserSemanticVersion, SourceGeneration: identity.SourceGeneration,
            ResourceGeneration: identity.Generation, ChannelGeneration: identity.ChannelGeneration,
            IdentityEpoch: identity.Epoch));

        Assert.Throws<ArgumentException>(() => _service.ConfirmManually(new(1, "8", "Free Space", "%", 2,
            "descending-danger", "scale must be one"), 42));
        var evidence = _service.ConfirmManually(new(1, "8", "Free Space", "percent", 1,
            "descending-danger", "已從 PRTG 核對頻道標籤與百分比單位。"), 42);
        Assert.Equal(PrtgDiskSemanticEvidenceSource.Manual, evidence.Source);
    }

    private void AssertConflict(string expected)
    {
        Assert.False(_service.TryStart(new(1, DateTime.Today.AddDays(-1)), out var error));
        Assert.Contains(expected, error);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class NoopAudit : IAuditService
    {
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) { }
        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    private sealed class MaintainUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long UserId => 42;
        public string Account => "disk-test";
        public string DisplayName => "Disk Test";
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Maintain };
        public bool IsServerAdmin => false;
        public bool Has(Capability capability) => Capabilities.Contains(capability);
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}
