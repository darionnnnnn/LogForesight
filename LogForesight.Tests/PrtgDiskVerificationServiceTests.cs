using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
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
    public void RuleTrial_ReportsMissingRuleWithoutWritingBusinessOrSemanticEvidence()
    {
        _ruleStore.Save(new RuleFileContent { Rules = new List<KnownIssueRule>() });

        var trial = _service.AssessRuleTrial(1);
        Assert.Equal("no-configured-rule", trial.Status);
        Assert.Contains("沒有已儲存", trial.Message);
        Assert.Empty(_evidence.GetAll());
        Assert.Empty(_results.GetRecent());
    }

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
                maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
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
                maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
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
        _evidence.ConfirmManually(new(1, 2, 1, "snmpdiskfree", "free", "Free", "%", 1, "descending-danger"),
            42, "verified", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "Verified", "matched", "free", "Free", "%", 1,
            "descending-danger", 1, true, DateTime.UtcNow, DateTime.Today.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion));
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
            maps.Add(new() { DeviceObjid = sensor.DeviceObjid, MapDate = day.Date, HostId = 1, MapStatus = PrtgMapStatus.Ok });
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
    }

    private void ConfirmTrialSemanticEvidence()
    {
        _evidence.ConfirmManually(new(1, 2, 1, "snmpdiskfree", "free", "Free", "%", 1, "descending-danger"),
            42, "test fixture semantic evidence", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "Verified", "matched", "free", "Free", "%", 1,
            "descending-danger", 1, true, DateTime.UtcNow, DateTime.Today.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion));
    }

    private void SaveTrialRule(bool enabled = true, PrtgDiskTrendThresholds? thresholds = null) => _ruleStore.Save(new RuleFileContent
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

    private void SaveTrialRules(params KnownIssueRule[] rules) => _ruleStore.Save(new RuleFileContent
    {
        Rules = rules.ToList()
    });

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
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "channel differs",
            "channel-2", "Used", "%", 1, "descending-danger", 1, true, DateTime.UtcNow,
            DateTime.Today.AddDays(-1), PrtgDiskVerificationService.ParserSemanticVersion));
        Assert.False(_service.CheckEvidence(1).IsValid);
    }

    [Fact]
    public void ManualEvidence_RemainsValidWhenMatchingProbeIsOlderThan25Hours()
    {
        var verifiedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "metadata incomplete",
            "channel-1", "Free space", "%", 1, "descending-danger", 1, true, verifiedAt,
            DateTime.Today.AddDays(-1), PrtgDiskVerificationService.ParserSemanticVersion));
        _evidence.ConfirmManually(
            new(1, 2, 1, "snmpdiskfree", "channel-1", "Free space", "%", 1, "descending-danger"),
            42, "reviewed", verifiedAt, PrtgDiskVerificationService.ParserSemanticVersion);

        Assert.True(DateTime.UtcNow - verifiedAt > TimeSpan.FromHours(25));
        Assert.True(_service.CheckEvidence(1).IsValid);
    }

    [Fact]
    public void ManualEvidence_IsInvalidatedWhenCurrentMappingChanges()
    {
        ConfirmManualEvidenceFromPartialProbe();
        Assert.True(_service.CheckEvidence(1).IsValid);

        using (var db = _fx.NewContext())
        {
            var map = db.PrtgHostMaps.Single();
            map.HostId = 99;
            db.SaveChanges();
        }
        Assert.False(_service.CheckEvidence(1).IsValid);
    }

    private void ConfirmManualEvidenceFromPartialProbe()
    {
        var probeAt = DateTime.UtcNow;
        _results.Save(new(1, 2, 1, "snmpdiskfree", "NeedsManualReview", "metadata incomplete",
            null, null, null, null, null, 1, true, probeAt, DateTime.Today.AddDays(-1),
            PrtgDiskVerificationService.ParserSemanticVersion));

        Assert.Throws<ArgumentException>(() => _service.ConfirmManually(new(1, "channel-1", "Free space", "%", 2,
            "descending-danger", "scale must be one"), 42));
        var evidence = _service.ConfirmManually(new(1, "channel-1", "Free space", "percent", 1,
            "descending-danger", "已從 PRTG 核對頻道標籤與百分比單位。"), 42);
        Assert.Equal(PrtgDiskSemanticEvidenceSource.Manual, evidence.Source);
    }

    private void AssertConflict(string expected)
    {
        Assert.False(_service.TryStart(new(1, DateTime.Today.AddDays(-1)), out var error));
        Assert.Contains(expected, error);
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}
