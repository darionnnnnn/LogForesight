using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <remarks>CalibrationService 的判定快取是行程層級的靜態欄位；標進專屬 Collection 讓日後
/// 任何第二個碰它的測試類別都被迫序列化，而不是靠「目前只有一個類別用它」這種默契。</remarks>
[Collection("CalibrationCacheState")]
public class CalibrationServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();

    public CalibrationServiceTests()
    {
        _ruleStore.Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() };
        // 預設開啟 PRTG 設定
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free", "SNMP CPU Load" };
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationService CreateService(IKnownIssueRuleStore? ruleStore = null,
        IIssueAggregateQuery? issueQueryOverride = null, PrtgCalibrationCaptureLimits? captureLimits = null)
    {
        // 判定快取是靜態的，測試之間必須清乾淨（否則前一個測試的結論會漏到下一個）
        CalibrationService.ClearAssessmentCache();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        return new CalibrationService(_fx.NewContext, prtgStore, issueQueryOverride ?? issueQuery,
            _settingsStore, ruleStore ?? _ruleStore, captureLimits);
    }

    private static KnownIssueRule ValidPrtgRule(string id, string code, int threshold, string? category = null, bool enabled = true) => new()
    {
        Id = id, Platform = "prtg", Enabled = enabled, PrtgRuleCode = code, PrtgThreshold = threshold,
        PrtgSensorCategory = category, CountThreshold = 1, Category = IssueCategory.Other, Severity = IssueSeverity.Low,
        Description = "Valid calibration fixture rule", PlainExplanation = "Valid calibration fixture rule",
        Impact = "Valid calibration fixture impact", LikelyCauses = ["fixture"], NextSteps = ["fixture"]
    };

    private void InstallCalibrationPolicy(DateTime anchor, long[] sensorIds, string sourceGeneration = "source-v1")
    {
        const string url = "https://prtg.example.invalid/";
        _settingsStore.Update(s => s.PrtgUrl = url);
        var scopeRevision = new EfJsonBlobStore(_fx.NewContext, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(_fx.NewContext, PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.CoreSystemId = "fixture-core";
            p.SourceGeneration = sourceGeneration;
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(url);
            p.ValidFrom = new DateTimeOffset(anchor.Date.AddDays(-60));
            p.HostIds = [1];
            p.SensorIds = sensorIds.ToList();
            p.SourceTimeZoneId = TimeZoneInfo.Local.Id;
            p.SourceCultureName = "en-US";
        });
    }

    private void InstallTimeline(long sensorId, DateTime anchor, params (DateTimeOffset At, string Status)[] states)
    {
        var from = new DateTimeOffset(anchor.Date.AddDays(-1));
        var through = new DateTimeOffset(anchor.Date.AddDays(1));
        InstallTimelineWindow(sensorId, from, through, states);
    }

    private void InstallTimelineWindow(long sensorId, DateTimeOffset from, DateTimeOffset through,
        params (DateTimeOffset At, string Status)[] states)
    {
        var policy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(_fx.NewContext,
            PrtgMonitoringPolicyStore.BlobKey)).Get();
        PrtgResourceIdentity? identity = null;
        if (policy.SensorIds.Contains(sensorId) && policy.HostIds.Contains(1) && policy.SourceGeneration.Length > 0)
        {
            try
            {
                identity = new EfPrtgStore(_fx.NewContext).BindObservedResource(sensorId, 1,
                    policy.SourceGeneration, $"calibration-resource-{sensorId}");
            }
            catch (InvalidOperationException)
            {
                // 沒有目前有效鏡像對應時，保留無權威世代的診斷 fixture。
            }
        }
        var store = new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + sensorId));
        store.Update(e =>
        {
            if (identity is not null)
                e.Bind(sensorId, 1, identity.SourceGeneration, $"calibration-resource-{sensorId}",
                    identity.Generation, identity.Epoch, identity.ChannelGeneration, from);
            else
                e.Bind(sensorId, 1, "source-v1", $"resource-{sensorId}", from);
            e.MappingRevision = new EfJsonBlobStore(_fx.NewContext, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
            e.Accept(from, through, states.Select(s => new PrtgTimedState(sensorId, s.At, s.Status,
                e.SourceGeneration, e.ResourceGeneration)));
        });
    }

    [Fact]
    public void RuleCalibration_RawStateChangesWithoutReadyPolicy_IsUnavailableAndExportsNoMagnitude()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        store.AppendStateChanges([new PrtgStateChangeRow
        {
            SensorObjid = 1001, ChangedAt = anchor.AddHours(8), Status = "Down", Quality = "Good"
        }]);

        var service = CreateService();
        var summary = service.AssessStatus(anchor, forceRefresh: true).PrtgRuleThresholds;
        var package = service.BuildExportPackage(anchor);

        Assert.Equal(CalibrationStatus.Unavailable, summary.Status);
        Assert.Contains(summary.Explanations, x => x.Contains("timeline", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(package.RuleThresholds.MagnitudeSamples);
        var explanations = typeof(CalibrationRuleThresholdDataset).GetProperty("Explanations")?.GetValue(package.RuleThresholds) as IEnumerable<string> ?? [];
        Assert.Contains(explanations, x => x.Contains("timeline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleCalibration_TrustedTimelineCrossMidnight_UsesContinuousThresholdMagnitudeAndDayMagnitude()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));
        new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001)).Update(e =>
        {
            e.States.Add(new PrtgTimedState(1001, new DateTimeOffset(anchor.Date.AddDays(2)), "Down",
                e.SourceGeneration, e.ResourceGeneration));
            e.LastAttemptAt = new DateTimeOffset(anchor.Date.AddDays(3));
        });
        _ruleStore.Content = new RuleFileContent { Rules =
        [
            ValidPrtgRule("down-global", "down", 60),
            ValidPrtgRule("down-availability", "down", 30, "availability")
        ] };

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);
        var down = Assert.Single(package.RuleThresholds.MagnitudeSamples.Where(s => s.RuleCode == PrtgRuleEvaluator.RuleDown));
        var thresholdMagnitude = (int?)typeof(CalibrationRuleMagnitudeRow).GetProperty("ThresholdMagnitude")?.GetValue(down);
        var dayMagnitude = (int?)typeof(CalibrationRuleMagnitudeRow).GetProperty("DayMagnitude")?.GetValue(down);

        Assert.Equal(35, down.Magnitude);
        Assert.Equal(35, thresholdMagnitude);
        Assert.Equal(20, dayMagnitude);
        var formalRule = ValidPrtgRule("down-availability", "down", 30, "availability");
        var evidence = new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001)).Get();
        var formal = PrtgCoveredRuleEvaluator.Evaluate(anchor, [new(1001, 10, "Up", "Ping", "availability")],
            new Dictionary<long, PrtgSensorTimelineEvidence> { [1001] = evidence }, [formalRule]);
        var finding = Assert.Single(formal);
        Assert.Equal(dayMagnitude, finding.Magnitude);
        Assert.Equal(thresholdMagnitude, (int?)typeof(PrtgFinding).GetProperty("ThresholdMagnitude")?.GetValue(finding));
        var categoryHit = Assert.Single(package.RuleThresholds.FormalCurrentHitCounts.Where(h => h.RuleId == "down-availability"));
        Assert.Equal(1, categoryHit.FindingCount);
        var globalDistribution = Assert.Single(package.RuleThresholds.MagnitudeSummaries.Where(h => h.RuleCode == PrtgRuleEvaluator.RuleDown));
        Assert.Equal(0, globalDistribution.HitsAtCurrentThreshold); // 35-minute sample is below global 60-minute threshold
        Assert.Contains(package.RuleThresholds.Explanations, x => x.Contains("不代表正式 finding 命中數", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, package.FormatVersion);
        var basis = typeof(CalibrationRuleThresholdDataset).GetProperty("MagnitudeBasis")?.GetValue(package.RuleThresholds) as string;
        Assert.Contains("covered", basis, StringComparison.OrdinalIgnoreCase);
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(package)))
        {
            var jsonDown = json.RootElement.GetProperty("RuleThresholds").GetProperty("MagnitudeSamples")
                .EnumerateArray().Single(s => s.GetProperty("RuleCode").GetString() == PrtgRuleEvaluator.RuleDown);
            Assert.Equal(35, jsonDown.GetProperty("Magnitude").GetInt32());
            Assert.Equal(35, jsonDown.GetProperty("ThresholdMagnitude").GetInt32());
            Assert.Equal(20, jsonDown.GetProperty("DayMagnitude").GetInt32());
        }

        var timelineStore = new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001));
        timelineStore.Update(e => e.States.Add(new PrtgTimedState(1001, new DateTimeOffset(anchor.Date.AddMinutes(5)), "Up",
            e.SourceGeneration, e.ResourceGeneration)));
        var afterTimelineMutation = service.BuildExportPackage(anchor);
        var revisedSample = Assert.Single(afterTimelineMutation.RuleThresholds.MagnitudeSamples.Where(s => s.RuleCode == PrtgRuleEvaluator.RuleDown));
        Assert.NotEqual(package.RuleThresholds.EvidenceFingerprint, afterTimelineMutation.RuleThresholds.EvidenceFingerprint);
        Assert.Equal(20, revisedSample.Magnitude);
        Assert.Equal(5, revisedSample.DayMagnitude);

        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 2,
            HostName = "OTHER-HOST", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        var afterMapMutation = service.BuildExportPackage(anchor);
        Assert.NotEqual(afterTimelineMutation.RuleThresholds.EvidenceFingerprint, afterMapMutation.RuleThresholds.EvidenceFingerprint);
        Assert.Empty(afterMapMutation.RuleThresholds.MagnitudeSamples);
        Assert.Contains(afterMapMutation.RuleThresholds.Explanations, x => x.Contains("主機對應", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleCalibration_TimelineWithChangedResourceGeneration_IsNotTrusted()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.Date.AddHours(8)), "Down"));
        new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001))
            .Update(e => e.ResourceGeneration = "different-resource-generation");

        var package = CreateService().BuildExportPackage(anchor);

        Assert.Empty(package.RuleThresholds.MagnitudeSamples);
        var explanations = typeof(CalibrationRuleThresholdDataset).GetProperty("Explanations")?.GetValue(package.RuleThresholds) as IEnumerable<string> ?? [];
        Assert.Contains(explanations, x => x.Contains("generation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleCalibration_PartialTrustedDownWindow_MatchesFormalFindingButDoesNotCountFullCoverageDay()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.Date), "Down"));
        new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001)).Update(e =>
        {
            e.Coverage = [new PrtgSensorCoverage(1001, new DateTimeOffset(anchor.Date), new DateTimeOffset(anchor.Date.AddHours(1)),
                e.SourceGeneration, e.ResourceGeneration)];
            e.States = e.States.Where(s => s.At <= new DateTimeOffset(anchor.Date.AddHours(1))).ToList();
            e.QualityReason = "query-time-budget-exceeded";
            e.LastAttemptAt = new DateTimeOffset(anchor.AddDays(2));
        });
        _ruleStore.Content = new RuleFileContent { Rules =
        [ValidPrtgRule("down-availability", "down", 30, "availability")] };

        var package = CreateService().BuildExportPackage(anchor);
        var sample = Assert.Single(package.RuleThresholds.MagnitudeSamples.Where(s => s.RuleCode == PrtgRuleEvaluator.RuleDown));
        var formalHit = Assert.Single(package.RuleThresholds.FormalCurrentHitCounts);

        Assert.Equal(60, sample.Magnitude);
        Assert.Equal(60, sample.ThresholdMagnitude);
        Assert.Equal(1, formalHit.FindingCount);
        Assert.Contains("DistinctCoverageDays", package.Summary.PrtgRuleThresholds.KeyMetrics.Keys);
        Assert.Equal(0, (int)package.Summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]);
        Assert.Contains(package.RuleThresholds.Explanations, x => x.Contains("已涵蓋區間仍依正式 evaluator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleCalibration_TrustedFullUpTimeline_HasCoverageButInsufficientAndEmptyDistribution()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Up", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.Date.AddDays(-1)), "Up"));
        _ruleStore.Content = new RuleFileContent { Rules =
        [ValidPrtgRule("down-availability", "down", 30, "availability")] };

        var package = CreateService().BuildExportPackage(anchor);

        Assert.Empty(package.RuleThresholds.MagnitudeSamples);
        Assert.Equal(CalibrationStatus.Insufficient, package.Summary.PrtgRuleThresholds.Status);
        Assert.True((int)package.Summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"] > 0);
        Assert.Contains(package.RuleThresholds.Explanations, x => x.Contains("涵蓋存在", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(package.RuleThresholds.Explanations, x => x.Contains("沒有可評估的可信保存 timeline", StringComparison.OrdinalIgnoreCase));

        var timeline = new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001));
        timeline.Update(e => e.States = e.States.Select(s => s with { Status = "UnrecognizedState" }).ToList());
        var unknownStatusPackage = CreateService().BuildExportPackage(anchor);
        Assert.Equal(0, (int)unknownStatusPackage.Summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]);
    }

    [Fact]
    public void RuleCalibration_TodayEvidenceIsCroppedAtCapturedAsOf()
    {
        var anchor = DateTime.Today;
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Up", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        var futureAt = DateTimeOffset.Now.AddHours(1);
        InstallTimelineWindow(1001, new DateTimeOffset(anchor.AddDays(-1)), futureAt.AddHours(1),
            (new DateTimeOffset(anchor.AddDays(-1)), "Up"), (futureAt, "Down"));
        _ruleStore.Content = new RuleFileContent { Rules =
        [ValidPrtgRule("down-availability", "down", 1, "availability")] };

        var package = CreateService().BuildExportPackage(anchor);

        Assert.Empty(package.RuleThresholds.MagnitudeSamples);
        Assert.True(package.RuleThresholds.AsOf <= DateTimeOffset.Now);
        Assert.True(package.RuleThresholds.AsOf < futureAt);
        Assert.Equal(0, (int)package.Summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]);
    }

    [Fact]
    public void RuleCalibration_DisabledRuleStillProducesThresholdDiscoveryDistributionButNoFormalHits()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Down", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.Date.AddDays(-1)), "Down"));
        _ruleStore.Content = new RuleFileContent { Rules =
        [ValidPrtgRule("disabled-down", PrtgRuleEvaluator.RuleDown, 1, enabled: false)] };

        var package = CreateService().BuildExportPackage(anchor);

        Assert.Single(package.RuleThresholds.MagnitudeSamples, s => s.RuleCode == PrtgRuleEvaluator.RuleDown);
        Assert.Empty(package.RuleThresholds.FormalCurrentHitCounts);
    }

    [Fact]
    public void RuleCalibration_InvalidStoredRuleIsExcludedLikeFormalBootstrapValidation()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));
        var invalid = new KnownIssueRule { Id = "a-invalid-down", Platform = "prtg", Enabled = true,
            PrtgRuleCode = "down", PrtgSensorCategory = "availability", PrtgThreshold = 1 };
        _ruleStore.Content = new RuleFileContent { Rules =
        [invalid, ValidPrtgRule("z-valid-down", "down", 50, "availability")] };

        var package = CreateService().BuildExportPackage(anchor);

        Assert.DoesNotContain(package.RuleThresholds.CurrentRules, r => r.RuleCode == "down" && r.Threshold == 1);
        Assert.Empty(package.RuleThresholds.FormalCurrentHitCounts);
        Assert.Single(package.RuleThresholds.MagnitudeSamples, s => s.RuleCode == "down" && s.Magnitude == 35);
    }

    [Fact]
    public void RuleCalibration_RuleReadIsReadOnlyAndUsesBootstrapFallbackSemantics()
    {
        var anchor = new DateTime(2026, 9, 1);
        var emptyStore = new CalibrationRuleStore(true, RuleLoadOutcome.Ok(new RuleFileContent()));
        var emptyPackage = CreateService(emptyStore).BuildExportPackage(anchor);
        Assert.Empty(emptyPackage.RuleThresholds.CurrentRules);
        Assert.Empty(emptyPackage.RuleThresholds.FormalCurrentHitCounts);
        Assert.Equal(0, emptyStore.SaveCount);

        var missingStore = new CalibrationRuleStore(false, RuleLoadOutcome.Fail("missing"));
        var missingPackage = CreateService(missingStore).BuildExportPackage(anchor);
        Assert.Contains(missingPackage.RuleThresholds.CurrentRules, r => r.RuleCode == "down");
        Assert.Equal(0, missingStore.LoadCount);
        Assert.Equal(0, missingStore.SaveCount);

        var failedStore = new CalibrationRuleStore(true, RuleLoadOutcome.Fail("corrupt"));
        var failedPackage = CreateService(failedStore).BuildExportPackage(anchor);
        Assert.Contains(failedPackage.RuleThresholds.CurrentRules, r => r.RuleCode == "down");
        Assert.True(failedStore.LoadCount > 0);
        Assert.Equal(0, failedStore.SaveCount);
    }

    [Fact]
    public void RuleCalibration_RuleMutationAtDailyHitsConsumerBoundaryRejectsMixedSnapshot()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));
        _ruleStore.Content = new RuleFileContent { Rules =
            [ValidPrtgRule("down-availability", "down", 30, "availability")] };
        var mutationObserved = false;
        var queryProxy = System.Reflection.DispatchProxy.Create<IIssueAggregateQuery, RuleMutationIssueQueryProxy>();
        var proxy = (RuleMutationIssueQueryProxy)(object)queryProxy;
        proxy.Inner = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        proxy.BeforeAggregateRuleHits = () =>
        {
            mutationObserved = true;
            _ruleStore.Content = new RuleFileContent { Rules =
                [ValidPrtgRule("down-availability", "down", 50, "availability")] };
        };

        var service = CreateService(issueQueryOverride: queryProxy);

        var exception = Assert.Throws<InvalidOperationException>(() => service.BuildExportPackage(anchor));
        Assert.Contains("校準規則在匯出組裝期間變更", exception.Message);
        Assert.True(mutationObserved);
    }

    [Fact]
    public void RuleCalibration_HostMapMutationAtDailyHitsConsumerBoundaryRejectsMixedSnapshot()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping",
            Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10,
            HostId = 1, MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));
        _ruleStore.Content = new RuleFileContent { Rules =
            [ValidPrtgRule("down-availability", "down", 30, "availability")] };
        var mutationObserved = false;
        var queryProxy = System.Reflection.DispatchProxy.Create<IIssueAggregateQuery, RuleMutationIssueQueryProxy>();
        var proxy = (RuleMutationIssueQueryProxy)(object)queryProxy;
        proxy.Inner = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        proxy.BeforeAggregateRuleHits = () =>
        {
            mutationObserved = true;
            store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10,
                HostId = 2, MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        };

        var service = CreateService(issueQueryOverride: queryProxy);

        var exception = Assert.Throws<InvalidOperationException>(() => service.BuildExportPackage(anchor));
        Assert.Contains("校準主機對應在匯出組裝期間變更", exception.Message);
        Assert.True(mutationObserved);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("timeline")]
    [InlineData("resource-identity")]
    [InlineData("settings")]
    public void RuleCalibration_AuthorityMutationAtDailyHitsConsumerBoundaryRejectsMixedSnapshot(string source)
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping",
            Category = "availability", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10,
            HostId = 1, MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));
        _ruleStore.Content = new RuleFileContent { Rules =
            [ValidPrtgRule("down-availability", "down", 30, "availability")] };
        var mutationObserved = false;
        var queryProxy = System.Reflection.DispatchProxy.Create<IIssueAggregateQuery, RuleMutationIssueQueryProxy>();
        var proxy = (RuleMutationIssueQueryProxy)(object)queryProxy;
        proxy.Inner = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        proxy.BeforeAggregateRuleHits = () =>
        {
            mutationObserved = true;
            switch (source)
            {
                case "policy":
                    new PrtgMonitoringPolicyStore(new EfJsonBlobStore(_fx.NewContext,
                        PrtgMonitoringPolicyStore.BlobKey)).Update(policy => policy.SourceGeneration = "source-v2");
                    break;
                case "timeline":
                    InstallTimeline(1001, anchor,
                        (new DateTimeOffset(anchor.Date.AddMinutes(-10)), "Up"));
                    break;
                case "resource-identity":
                    store.BindObservedResource(1001, 1, "source-v1", "changed-resource-fingerprint");
                    break;
                case "settings":
                    _settingsStore.Update(settings => settings.PrtgSensorTypeWhitelist = ["SNMP CPU Load"]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(source));
            }
        };

        var service = CreateService(issueQueryOverride: queryProxy);
        var exception = Assert.Throws<InvalidOperationException>(() => service.BuildExportPackage(anchor));
        Assert.Contains("校準來源版本或設定在匯出組裝期間變更", exception.Message);
        Assert.True(mutationObserved);
    }

    [Theory]
    [InlineData("sensor-status")]
    [InlineData("map-status")]
    public void BuildExportPackage_LegacyOversizedStatusFieldsRejectFullAndSummary(string field)
    {
        var anchor = DateTime.Today;
        var now = DateTime.Now;
        _settingsStore.Update(settings => settings.PrtgSensorTypeWhitelist = ["Ping"]);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability",
            Status = "Up", Paused = false
        }], now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow
        {
            MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1",
            MapStatus = PrtgMapStatus.Ok, CreatedAt = now
        }]);
        store.UpsertValues([new PrtgValueRow
        {
            SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50,
            Quality = PrtgDataQuality.Ok
        }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor,
            (new DateTimeOffset(anchor.Date.AddMinutes(-15)), "Down"),
            (new DateTimeOffset(anchor.Date.AddMinutes(20)), "Up"));

        var service = CreateService();
        var valid = service.BuildExportPackage(anchor);
        Assert.NotEmpty(valid.ValueBaselines);

        using (var ctx = _fx.NewContext())
        {
            if (field == "sensor-status")
                ctx.Database.ExecuteSqlRaw("UPDATE lf_prtg_sensors SET status = {0} WHERE objid = {1}",
                    new string('S', 65), 1001L);
            else
                ctx.Database.ExecuteSqlRaw("UPDATE lf_prtg_host_map SET map_status = {0} WHERE map_date = {1} AND device_objid = {2}",
                    new string('M', 17), anchor, 10L);
        }

        foreach (var summaryOnly in new[] { false, true })
        {
            var exception = Assert.Throws<CalibrationCapacityException>(
                () => service.BuildExportPackage(anchor, summaryOnly));
            Assert.Contains(field == "sensor-status" ? "狀態" : "MapStatus", exception.Message,
                StringComparison.Ordinal);
        }
    }

    public class RuleMutationIssueQueryProxy : System.Reflection.DispatchProxy
    {
        public IIssueAggregateQuery Inner { get; set; } = null!;
        public Action? BeforeAggregateRuleHits { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) throw new MissingMethodException();
            if (targetMethod.Name == nameof(IIssueAggregateQuery.AggregatePrtgRuleHitsBounded)) BeforeAggregateRuleHits?.Invoke();
            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class CalibrationRuleStore(bool exists, RuleLoadOutcome outcome) : IKnownIssueRuleStore
    {
        public string Location => "(calibration-fixture)";
        public bool Exists => exists;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public RuleLoadOutcome Load() { LoadCount++; return outcome; }
        public void Save(RuleFileContent content) => SaveCount++;
    }

    [Fact]
    public void RuleCalibration_OversizedTimelineBlobIsRejectedWithoutPartialSamples()
    {
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Status = "Down", Paused = false }], DateTime.Now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.Date.AddDays(-1)), "Down"));
        new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext, PrtgSensorTimelineStore.Prefix + 1001))
            .Update(e => e.States = e.States.Select(s => s with { Status = new string('x', 4 * 1024 * 1024 + 1) }).ToList());

        var package = CreateService().BuildExportPackage(anchor);

        Assert.Empty(package.RuleThresholds.MagnitudeSamples);
        Assert.Equal(CalibrationStatus.Unavailable, package.Summary.PrtgRuleThresholds.Status);
        Assert.Contains(package.RuleThresholds.Explanations, x => x.Contains("容量上限", StringComparison.OrdinalIgnoreCase));
    }

    // ── 1. PRTG 值型基線：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_值型基線_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        // (a) PRTG 未啟用
        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.PrtgValueBaseline.Status);
        Assert.Equal("無法取得", summary1.PrtgValueBaseline.StatusText);
        Assert.Contains(summary1.PrtgValueBaseline.Explanations, s => s.Contains("「擷取參數」頁籤選擇取數範圍"));

        // (b) PRTG 已啟用但鏡像無任何 sensor
        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.PrtgValueBaseline.Status);
    }

    [Fact]
    public void AssessStatus_值型基線_未達可用主機數或天數_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 5 台主機（<10 台），每台 1 個 sensor 涵蓋 30 天
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 5; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            // 涵蓋 30 天，每天 12 列 ok
            for (int d = 0; d < 30; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 50.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgValueBaseline.Status);
        Assert.Equal("不足", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(5, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["MappedHosts"]));
        Assert.Contains(summary.PrtgValueBaseline.Explanations, s => s.Contains("目前只有 5 台主機有基線資料，需要 10 台"));
    }

    [Fact]
    public void AssessStatus_值型基線_達10台且涵蓋28天_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 30 天（≥28 天，<56 天）
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 30; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 40.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.PrtgValueBaseline.Status);
        Assert.Equal("可用", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingAvailable"]));
        Assert.Equal(0, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingSufficient"]));
    }

    [Fact]
    public void AssessStatus_值型基線_達10台且涵蓋56天_充足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 56 天
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 56; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 40.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.PrtgValueBaseline.Status);
        Assert.Equal("充足", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingSufficient"]));
    }

    [Fact]
    public void AssessStatus_值型基線_只有取樣列且coverage足夠_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 28 天，皆為 Coverage 100 的 sampled 列
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 28; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow
                    {
                        SensorObjid = sensorId,
                        PeriodStart = day.AddHours(hour),
                        AvgValue = 40.0,
                        Quality = PrtgDataQuality.Sampled,
                        Coverage = 100.0
                    });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.PrtgValueBaseline.Status);
        Assert.Equal("可用", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingAvailable"]));
    }

    [Fact]
    public void AssessStatus_值型基線_取樣列coverage不足_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 28 天，但 Coverage 全為 40（不足門檻 75）
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 28; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow
                    {
                        SensorObjid = sensorId,
                        PeriodStart = day.AddHours(hour),
                        AvgValue = 40.0,
                        Quality = PrtgDataQuality.Sampled,
                        Coverage = 40.0
                    });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgValueBaseline.Status);
        Assert.Equal("不足", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(0, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingAvailable"]));
    }

    // ── 2. PRTG 規則門檻：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_規則門檻_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.PrtgRuleThresholds.Status);
        Assert.Equal("無法取得", summary1.PrtgRuleThresholds.StatusText);

        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.PrtgRuleThresholds.Status);
    }

    [Fact]
    public void AssessStatus_規則門檻_RawCoverageAndIssueRowsDoNotGrantTrustedReadiness()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 狀態變更涵蓋 10 天（<28 天）
        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 10; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        // 寫入 15 筆 PRTG 規則命中（<30 筆）
        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            for (int i = 0; i < 15; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId,
                    HostId = 1,
                    RecordDate = anchor,
                    LogName = "PRTG",
                    SourceName = "PRTG:down",
                    EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{1000 + i}",
                    Category = "Service",
                    SeverityRank = 2
                });
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Unavailable, summary.PrtgRuleThresholds.Status);
        Assert.Equal(0, Convert.ToInt32(summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]));
        Assert.Equal(0, Convert.ToInt32(summary.PrtgRuleThresholds.KeyMetrics["TotalRuleHits"]));
        Assert.Contains(summary.PrtgRuleThresholds.Explanations, x => x.Contains("timeline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessStatus_規則門檻_可信Timeline涵蓋30天且正式命中30筆_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Paused = false }], DateTime.Now);
        var from = anchor.AddDays(-29).Date;
        var through = anchor.Date.AddDays(1);
        for (var day = from; day < through; day = day.AddDays(1))
            store.ReplaceHostMapForDate(day, [new PrtgHostMapRow { MapDate = day, DeviceObjid = 10, HostId = 1,
                HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        InstallCalibrationPolicy(anchor, [1001]);
        var states = Enumerable.Range(0, 30).SelectMany(i =>
        {
            var day = from.AddDays(i);
            return new[] { (new DateTimeOffset(day.AddHours(8)), "Down"), (new DateTimeOffset(day.AddHours(8).AddMinutes(10)), "Up") };
        }).ToArray();
        InstallTimelineWindow(1001, new DateTimeOffset(from), new DateTimeOffset(through), states);
        _ruleStore.Content = new RuleFileContent { Rules =
        [ValidPrtgRule("down-availability", "down", 1, "availability")] };

        var summary = CreateService().AssessStatus(anchor, forceRefresh: true).PrtgRuleThresholds;

        Assert.Equal(CalibrationStatus.Available, summary.Status);
        Assert.Equal("可用", summary.StatusText);
        Assert.Equal(29, Convert.ToInt32(summary.KeyMetrics["DistinctCoverageDays"]));
        Assert.Equal(30, Convert.ToInt32(summary.KeyMetrics["DownSensorDays"]));
        Assert.Equal(30, Convert.ToInt32(summary.KeyMetrics["TotalRuleHits"]));
    }

    [Fact]
    public void AssessStatus_同一錨定日在TTL內回快取_forceRefresh則重算()
    {
        var anchor = new DateTime(2026, 8, 31);
        var service = CreateService();

        var first = service.AssessStatus(anchor);
        var second = service.AssessStatus(anchor);
        Assert.Same(first, second);

        var forced = service.AssessStatus(anchor, forceRefresh: true);
        Assert.NotSame(first, forced);
        Assert.Equal(first.PrtgValueBaseline.Status, forced.PrtgValueBaseline.Status);

        // 換一個錨定日不得回上一個的快取
        var other = service.AssessStatus(anchor.AddDays(-1));
        Assert.NotSame(forced, other);
    }

    [Fact]
    public void AssessStatus_StaticCacheIsSeparatedAcrossDatabasesWithSameBlobVersions()
    {
        var anchor = new DateTime(2026, 9, 1);
        const string url = "https://prtg.example.invalid/";
        var primary = new EfPrtgStore(_fx.NewContext);
        primary.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Up", Paused = false }], DateTime.Now);
        primary.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        _settingsStore.Update(s => { s.PrtgUrl = url; s.PrtgEnabled = true; });
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(_fx.NewContext, PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.CoreSystemId = "fixture-core"; p.SourceGeneration = "source-v1";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(url); p.ValidFrom = new DateTimeOffset(anchor.AddDays(-60));
            p.HostIds = [1]; p.SensorIds = [1001]; p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US";
        });
        var start = new DateTimeOffset(anchor.Date.AddDays(-1)); var end = new DateTimeOffset(anchor.Date.AddDays(1));
        primary.ApplyAutoCategories(new Dictionary<string, string>());
        PrtgResourceIdentity primaryIdentity;
        using (var db = _fx.NewContext())
        {
            primaryIdentity = PrtgResourceIdentityStore.Set(db, 1001, "source-v1", 10, 1,
                "cache-primary-resource", "Ping|availability|auto", "", true, start.ToUniversalTime());
            db.SaveChanges();
        }
        new PrtgSensorTimelineStore(new EfJsonBlobStore(_fx.NewContext,
            PrtgSensorTimelineStore.Prefix + 1001)).Update(e =>
        {
            e.Bind(1001, 1, primaryIdentity.SourceGeneration, "cache-primary-resource",
                primaryIdentity.Generation, primaryIdentity.Epoch, primaryIdentity.ChannelGeneration, start);
            e.Accept(start, end, [new PrtgTimedState(1001, new DateTimeOffset(anchor.Date.AddHours(8)),
                "Down", e.SourceGeneration, e.ResourceGeneration)]);
        });
        var primarySummary = CreateService().AssessStatus(anchor).PrtgRuleThresholds;

        using var otherFx = new EfSqliteFixture();
        var otherSettings = new FakeSystemSettingsStore();
        var currentSettings = _settingsStore.Get();
        otherSettings.Update(s =>
        {
            s.PrtgEnabled = currentSettings.PrtgEnabled; s.PrtgUrl = currentSettings.PrtgUrl;
            s.PrtgRetentionDays = currentSettings.PrtgRetentionDays; s.PrtgFetchStrategy = currentSettings.PrtgFetchStrategy;
            s.PrtgSensorTypeWhitelist = currentSettings.PrtgSensorTypeWhitelist?.ToList();
        });
        var otherPrtg = new EfPrtgStore(otherFx.NewContext);
        otherPrtg.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Up", Paused = false }], DateTime.Now);
        otherPrtg.ApplyAutoCategories(new Dictionary<string, string>());
        otherPrtg.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(otherFx.NewContext, PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.CoreSystemId = "fixture-core"; p.SourceGeneration = "source-v1";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(url); p.ValidFrom = new DateTimeOffset(anchor.AddDays(-60));
            p.HostIds = [1]; p.SensorIds = [1001]; p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US";
        });
        PrtgResourceIdentity otherIdentity;
        using (var db = otherFx.NewContext())
        {
            otherIdentity = PrtgResourceIdentityStore.Set(db, 1001, "source-v1", 10, 1,
                "cache-separation-resource", "Ping|availability|auto", "", true, start.ToUniversalTime());
            db.SaveChanges();
        }
        new PrtgSensorTimelineStore(new EfJsonBlobStore(otherFx.NewContext, PrtgSensorTimelineStore.Prefix + 1001)).Update(e =>
        {
            e.Bind(1001, 1, otherIdentity.SourceGeneration, "cache-separation-resource",
                otherIdentity.Generation, otherIdentity.Epoch, otherIdentity.ChannelGeneration, start);
            e.Accept(start, end, [new PrtgTimedState(1001, start, "Up", e.SourceGeneration, e.ResourceGeneration)]);
        });
        var otherService = new CalibrationService(otherFx.NewContext, otherPrtg,
            new EfIssueAggregateQuery(otherFx.NewContext, _hostStore), otherSettings, new FakeRuleStore());
        var otherSummary = otherService.AssessStatus(anchor).PrtgRuleThresholds;

        Assert.NotEqual(primarySummary.KeyMetrics["EvidenceFingerprint"], otherSummary.KeyMetrics["EvidenceFingerprint"]);
        Assert.True(Convert.ToInt32(primarySummary.KeyMetrics["DownSensorDays"]) > 0);
        Assert.Equal(0, Convert.ToInt32(otherSummary.KeyMetrics["DownSensorDays"]));
    }

    /// <summary>
    /// 門檻校準是逐規則進行的：四條加總會讓「down 只有 3 筆但 flapping 有 100 筆」
    /// 被誤判成資料充足，而 down 的門檻其實仍然無從校準。
    /// </summary>
    [Fact]
    public void AssessStatus_規則門檻_RawIssueAggregatesDoNotReplaceFormalTimelineHits()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 狀態變更涵蓋 30 天（滿足可用的 28 天）
        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 30; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            // down 只有 3 筆（遠低於 30），flapping 有 40 筆——四條加總 43 筆會超過門檻
            for (int i = 0; i < 3; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 1, RecordDate = anchor.AddDays(-i),
                    LogName = "PRTG", SourceName = "PRTG:down", EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{2000 + i}",
                    Category = "Service", SeverityRank = 2
                });
            }
            for (int i = 0; i < 40; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 1, RecordDate = anchor.AddDays(-i % 30),
                    LogName = "PRTG", SourceName = "PRTG:flapping", EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleFlapping}:{3000 + i}",
                    Category = "Service", SeverityRank = 3
                });
            }
            ctx.SaveChanges();
        }

        var summary = CreateService().AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Unavailable, summary.PrtgRuleThresholds.Status);
        Assert.Equal(0, summary.PrtgRuleThresholds.KeyMetrics["DownSensorDays"]);
        Assert.Equal(0, summary.PrtgRuleThresholds.KeyMetrics["FlappingSensorDays"]);
        Assert.Equal(0, summary.PrtgRuleThresholds.KeyMetrics["TotalRuleHits"]);
        Assert.Contains(summary.PrtgRuleThresholds.Explanations, x => x.Contains("timeline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessStatus_規則門檻_Raw56DayCountsDoNotClaimSufficientWithoutRetainedTimeline()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 56; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            for (int i = 0; i < 110; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId,
                    HostId = 1,
                    RecordDate = anchor.AddDays(-i % 56),
                    LogName = "PRTG",
                    SourceName = "PRTG:down",
                    EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{1000 + i}",
                    Category = "Service",
                    SeverityRank = 2
                });
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Unavailable, summary.PrtgRuleThresholds.Status);
        Assert.Equal(0, summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]);
        Assert.Equal(0, summary.PrtgRuleThresholds.KeyMetrics["DownSensorDays"]);
        Assert.Contains(summary.PrtgRuleThresholds.Explanations, x => x.Contains("timeline", StringComparison.OrdinalIgnoreCase));
    }

    // ── 3. 數值取得量級：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_觸發式量級_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.TriggeredFetchMagnitude.Status);

        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.TriggeredFetchMagnitude.Status);
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數未達14天_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 8 天有數值（<14 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 8; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal(8, Convert.ToInt32(summary.TriggeredFetchMagnitude.KeyMetrics["DaysWithValues"]));
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數達14天_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 18 天有數值（≥14 天，<28 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 18; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal("可用", summary.TriggeredFetchMagnitude.StatusText);
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數達28天_充足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 29 天有數值（≥28 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 29; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal("充足", summary.TriggeredFetchMagnitude.StatusText);
    }

    [Fact]
    public void AssessStatus_數值取得量級_回報可用與取樣佔比()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 造 15 天資料（每晚 4 列：1 ok, 2 sampled 且 coverage>=75, 1 sampled 且 coverage 40）
        // 總列數 = 60
        // OkCount = 15 -> OkRatio = 15/60 = 0.25
        // SampledCount = 30 -> SampledRatio = 30/60 = 0.5
        // UsableCount = 45 -> UsableRatio = 45/60 = 0.75
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 15; i++)
        {
            var day = anchor.AddDays(-i);
            values.Add(new PrtgValueRow { SensorObjid = 2001, PeriodStart = day.AddHours(1), AvgValue = 10.0, Quality = PrtgDataQuality.Ok });
            values.Add(new PrtgValueRow { SensorObjid = 2001, PeriodStart = day.AddHours(2), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 });
            values.Add(new PrtgValueRow { SensorObjid = 2001, PeriodStart = day.AddHours(3), AvgValue = 30.0, Quality = PrtgDataQuality.Sampled, Coverage = 80.0 });
            values.Add(new PrtgValueRow { SensorObjid = 2001, PeriodStart = day.AddHours(4), AvgValue = 40.0, Quality = PrtgDataQuality.Sampled, Coverage = 40.0 });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        var mag = summary.TriggeredFetchMagnitude;
        Assert.Equal("數值取得量級", mag.ItemName);
        Assert.Equal(CalibrationStatus.Available, mag.Status);
        Assert.Equal(0.25, Convert.ToDouble(mag.KeyMetrics["OkRatio"]));
        Assert.Equal(0.5, Convert.ToDouble(mag.KeyMetrics["SampledRatio"]));
        Assert.Equal(0.75, Convert.ToDouble(mag.KeyMetrics["UsableRatio"]));
    }

    // ── 4. 殘留判定門檻：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_殘留判定_保留期內無候選_無法取得()
    {
        var service = CreateService();
        var summary = service.AssessStatus(new DateTime(2026, 8, 31));

        Assert.Equal(CalibrationStatus.Unavailable, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("無法取得", summary.ResidualCredentialThresholds.StatusText);
        Assert.Contains(summary.ResidualCredentialThresholds.Explanations, s => s.Contains("請確認 4625／4771 與 Linux 登入失敗規則為啟用狀態"));
    }

    [Fact]
    public void AssessStatus_殘留判定_候選主機日數或天數未達標_不足()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 造 50 個候選主機日，涵蓋 5 天（<200 主機日，<14 天）
            for (int h = 1; h <= 10; h++)
            {
                for (int d = 0; d < 5; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("不足", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(50, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(5, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    [Fact]
    public void AssessStatus_殘留判定_達200主機日且涵蓋14天_可用()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 20 台主機 × 15 天 = 300 主機日（≥200 主機日，≥14 天，<1000 主機日）
            for (int h = 1; h <= 20; h++)
            {
                for (int d = 0; d < 15; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("可用", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(300, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(15, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    [Fact]
    public void AssessStatus_殘留判定_達1000主機日且涵蓋28天_充足()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 40 台主機 × 30 天 = 1200 主機日（≥1000 主機日，≥28 天）
            for (int h = 1; h <= 40; h++)
            {
                for (int d = 0; d < 30; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("充足", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(1200, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(30, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    // ── 5. 分母為零判不足測試（突變驗證核心） ─────────────────────────

    [Fact]
    public void AssessStatus_分母為零或無任何資料時一律判不足而非可用()
    {
        // 模擬 PRTG 已啟用、鏡像有 sensor，但「完全沒有任何時序資料」（零數值、零狀態變更、零命中、零候選）
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.Now;
        var anchor = new DateTime(2026, 8, 31);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 9001, DeviceObjid = 1, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 1, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 1. 值型基線：有 sensor 有對應主機，但數值列數為 0 → 必須為「不足」，絕不得為「可用」或「充足」
        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgValueBaseline.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.PrtgValueBaseline.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.PrtgValueBaseline.Status);

        // 2. 規則門檻：沒有可信 timeline → 無法取得，而非把 raw/mirror 缺席當作完整歷史。
        Assert.Equal(CalibrationStatus.Unavailable, summary.PrtgRuleThresholds.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.PrtgRuleThresholds.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.PrtgRuleThresholds.Status);

        // 3. 數值取得量級：無任何數值紀錄 → 必須為「不足」，絕不得為「可用」或「充足」
        Assert.Equal(CalibrationStatus.Insufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.TriggeredFetchMagnitude.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.TriggeredFetchMagnitude.Status);

        // 4. 殘留判定門檻：無候選紀錄 → 為「無法取得」
        Assert.Equal(CalibrationStatus.Unavailable, summary.ResidualCredentialThresholds.Status);
    }

    // ── 6. 主機層涵蓋天數取 sensor 最大值 ──────────────────────────────

    [Fact]
    public void AssessStatus_主機層涵蓋天數取Sensor的最大值()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 同一台主機 HOST-1（Device 10）底下有兩個 sensor：
        // Sensor 101 涵蓋 10 天
        // Sensor 102 涵蓋 30 天
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 102, DeviceObjid = 10, SensorType = "SNMP CPU Load", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var values = new List<PrtgValueRow>();
        // Sensor 101: 10 天
        for (int d = 0; d < 10; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 101, PeriodStart = day.AddHours(h), AvgValue = 10.0, Quality = PrtgDataQuality.Ok });
            }
        }
        // Sensor 102: 30 天
        for (int d = 0; d < 30; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 102, PeriodStart = day.AddHours(h), AvgValue = 20.0, Quality = PrtgDataQuality.Ok });
            }
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 主機最大涵蓋天數應為 30 天（不是 10 天，也不是 40 天）
        Assert.Equal(30, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["MaxCoverageDays"]));
    }

    // ── 7. 白名單留空＝不限制 ────────────────────────────────────────

    [Fact]
    public void AssessStatus_白名單留空時納入全部Sensor()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 清空白名單（留空＝不限制）
        _settingsStore.Update(s => s.PrtgSensorTypeWhitelist = new List<string>());

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 501, DeviceObjid = 1, SensorType = "Custom Ping Sensor", Paused = false },
            new() { Objid = 502, DeviceObjid = 1, SensorType = "Custom HTTP Sensor", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 1, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 白名單為空時，2 個 sensor 全部被納入計算
        Assert.Equal(2, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["WhitelistedSensors"]));
    }

    // ── 8. 補充說明包含實際數字 ──────────────────────────────────────

    [Fact]
    public void AssessStatus_補充說明包含實際數字與所需值()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        _settingsStore.Update(s => s.PrtgRetentionDays = 30); // 小於充足所需 56 天

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var values = new List<PrtgValueRow>();
        for (int d = 0; d < 8; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 101, PeriodStart = day.AddHours(h), AvgValue = 10.0, Quality = PrtgDataQuality.Ok });
            }
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        var explanations = summary.PrtgValueBaseline.Explanations;
        Assert.Contains(explanations, s => s.Contains("30 天") && s.Contains("56 天"));
        Assert.Contains(explanations, s => s.Contains("目前只有 1 台主機有基線資料，需要 10 台"));
        Assert.Contains(explanations, s => s.Contains("目前最長涵蓋 8 天，還需要約 20 天"));
    }

    // ── 9. 殘留資料集不含帳號名稱 ────────────────────────────────────

    /// <summary>
    /// 規則門檻資料集的核心：以最低門檻逐日評估取得 magnitude 分佈。
    /// 只有「現行門檻下命中幾次」無法回答「門檻該設多少」——那正是這一頁存在的理由。
    /// </summary>
    [Fact]
    public void BuildExportPackage_規則門檻資料集含最低門檻評估的magnitude分佈與分位數()
    {
        var anchor = new DateTime(2026, 8, 31);
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 1002, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 10, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }]);
        InstallCalibrationPolicy(anchor, [1001, 1002]);
        InstallTimeline(1001, anchor, (new DateTimeOffset(anchor.AddHours(8)), "Down"));
        InstallTimeline(1002, anchor, (new DateTimeOffset(anchor.AddHours(23).AddMinutes(30)), "Down"));

        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            // 08:00 進入 Down 且日終未恢復 → magnitude 960 分鐘（現行門檻 60 也命中）
            new() { SensorObjid = 1001, ChangedAt = anchor.AddHours(8), Status = "Down", Quality = "Good" },
            // 23:30 才進入 Down → magnitude 30 分鐘，**低於現行門檻 60**。
            // 校準要看的正是這種「目前不會報、但門檻調低就會報」的樣本；
            // 若評估時用現行門檻而非最低門檻，這筆會整個消失。
            new() { SensorObjid = 1002, ChangedAt = anchor.AddHours(23).AddMinutes(30), Status = "Down", Quality = "Good" }
        });

        var package = CreateService().BuildExportPackage(anchor);
        var dataset = package.RuleThresholds;

        var downSamples = dataset.MagnitudeSamples
            .Where(r => r.RuleCode == PrtgRuleEvaluator.RuleDown)
            .OrderBy(r => r.Magnitude)
            .ToList();
        Assert.Equal(2, downSamples.Count);
        Assert.Equal(30, downSamples[0].Magnitude);
        Assert.Equal(1002, downSamples[0].SensorObjid);
        Assert.Equal(960, downSamples[1].Magnitude);
        Assert.Equal(anchor.Date, downSamples[1].Date);

        var summary = dataset.MagnitudeSummaries.Single(x => x.RuleCode == PrtgRuleEvaluator.RuleDown);
        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(30, summary.Min);
        Assert.Equal(960, summary.Max);
        Assert.Equal(PrtgRuleCatalog.DefaultDownMinutes, summary.CurrentThreshold);
        // 兩筆樣本中只有一筆達現行門檻——這個對比就是校準的依據
        Assert.Equal(1, summary.HitsAtCurrentThreshold);
    }

    /// <summary>
    /// 規則門檻分佈的 sensor 母體要與夜間規則評估一致：不受取數白名單限制（Ping 預設不在白名單），
    /// 且不做同裝置折疊——Ping Down 時同裝置 traffic 的 Down 在正式判定會被合併，
    /// 但校準要看的是每顆 sensor 自己的量值，兩顆都要留在分佈裡。
    /// </summary>
    [Fact]
    public void BuildExportPackage_規則門檻分佈不受取數白名單限制且不做同裝置折疊()
    {
        var anchor = new DateTime(2026, 8, 31);
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 20, SensorType = "Ping", Paused = false },
            new() { Objid = 2002, DeviceObjid = 20, SensorType = "SNMP Traffic 64bit", Paused = false }
        }, now);
        // Ping 自動分類為 availability——若校準把分類傳進評估器，2002 會被折疊掉
        store.ApplyAutoCategories(new Dictionary<string, string>());

        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow { MapDate = anchor, DeviceObjid = 20, HostId = 1,
            HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }]);
        InstallCalibrationPolicy(anchor, [2001, 2002]);
        InstallTimeline(2001, anchor, (new DateTimeOffset(anchor.AddHours(8)), "Down"));
        InstallTimeline(2002, anchor, (new DateTimeOffset(anchor.AddHours(8)), "Down"));

        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 2001, ChangedAt = anchor.AddHours(8), Status = "Down", Quality = "Good" },
            new() { SensorObjid = 2002, ChangedAt = anchor.AddHours(8), Status = "Down", Quality = "Good" }
        });

        var package = CreateService().BuildExportPackage(anchor);

        var downSensors = package.RuleThresholds.MagnitudeSamples
            .Where(r => r.RuleCode == PrtgRuleEvaluator.RuleDown)
            .Select(r => r.SensorObjid)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(new long[] { 2001, 2002 }, downSensors.Select(x => (long)x).ToArray());
    }

    /// <summary>
    /// IsMatch 要能為 true：條件 4（跨日重現）需要 history，若匯出端不撈歷史紀錄，
    /// 這一欄會整欄恆為 false，下一輪拿它校準會得到「現行門檻命中率 0%」的錯誤結論。
    /// </summary>
    [Fact]
    public void BuildExportPackage_跨日重現的候選_IsMatch為true()
    {
        var anchor = new DateTime(2026, 8, 31);

        LogIssueSignature Sig() => new()
        {
            LogName = "Security",
            Source = "Microsoft-Windows-Security-Auditing",
            EventId = 4625,
            Count = 50,
            LoginFailureDetails = new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS-01", LogonType = 3, ReasonCode = "bad_password", Count = 50 }
            },
            LoginFailureTotalCount = 50
        };

        using (var ctx = _fx.NewContext())
        {
            // 前一日同一組 (帳號, 來源) 也有登入失敗 → 條件 4 成立
            foreach (var date in new[] { anchor.AddDays(-1), anchor })
            {
                var record = new DailyAnalysisRecord
                {
                    HostId = 101, Host = "SEC-SRV-01", Date = date, RiskLevel = "高",
                    TopIssues = new List<LogIssueSignature> { Sig() }
                };
                var dr = new DailyRecordRow
                {
                    HostId = 101, HostName = "SEC-SRV-01", RecordDate = date, RiskLevel = "高",
                    DetailPruned = false, ContentJson = JsonSerializer.Serialize(record)
                };
                ctx.DailyRecords.Add(dr);
                ctx.SaveChanges();

                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 101, RecordDate = date,
                    SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
                });
                ctx.SaveChanges();
            }
        }

        var package = CreateService().BuildExportPackage(anchor);

        var todays = package.ResidualCandidates.Single(c => c.Date.Date == anchor.Date);
        Assert.True(todays.IsMatch, "跨日重現成立時 IsMatch 應為 true；恆 false 代表 history 沒有被傳入");
        Assert.Equal(1.0, todays.ConcentrationRatio);
    }

    [Fact]
    public void BuildExportPackage_同錨點重跑會讀取最新NetIQ殘留資料()
    {
        var anchor = new DateTime(2026, 7, 17);
        DailyAnalysisRecord MakeRecord(int totalCount, long hostId = 101, string hostName = "SEC-SRV-01") => new()
        {
            HostId = hostId, Host = hostName, Date = anchor, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "Security", Source = "Microsoft-Windows-Security-Auditing", EventId = 4625,
                    LoginFailureDetails = new List<LoginFailureDetail>
                    { new() { Account = "redacted", Source = "redacted", LogonType = 3, Count = totalCount } },
                    LoginFailureTotalCount = totalCount
                }
            }
        };
        using (var ctx = _fx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor,
                DetailPruned = false, ContentJson = JsonSerializer.Serialize(MakeRecord(1))
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId, HostId = 101, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var before = service.BuildExportPackage(anchor).ResidualCandidates.Single();
        Assert.Equal(1, before.TotalDetailCount);
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single();
            row.ContentJson = JsonSerializer.Serialize(MakeRecord(27));
            ctx.SaveChanges();
        }

        var after = service.BuildExportPackage(anchor).ResidualCandidates.Single();
        Assert.Equal(27, after.TotalDetailCount);

        using var otherFx = new EfSqliteFixture();
        using (var ctx = otherFx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 202, HostName = "OTHER-SRV", RecordDate = anchor,
                DetailPruned = false, ContentJson = JsonSerializer.Serialize(MakeRecord(39, 202, "OTHER-SRV"))
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId, HostId = 202, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            ctx.SaveChanges();
        }
        var otherStore = new EfPrtgStore(otherFx.NewContext);
        var otherService = new CalibrationService(otherFx.NewContext, otherStore,
            new EfIssueAggregateQuery(otherFx.NewContext, _hostStore), _settingsStore, _ruleStore);
        var other = otherService.BuildExportPackage(anchor).ResidualCandidates.Single();
        Assert.Equal(39, other.TotalDetailCount);
        Assert.Equal(202, other.HostId);
    }

    [Fact]
    public void BuildExportPackage_巨大TopIssues空物件陣列在反序列化前拒絕()
    {
        var anchor = new DateTime(2026, 7, 17);
        using (var ctx = _fx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor,
                DetailPruned = false,
                ContentJson = JsonSerializer.Serialize(new DailyAnalysisRecord
                {
                    HostId = 101, Host = "SEC-SRV-01", Date = anchor,
                    TopIssues = [new LogIssueSignature { EventId = 4625, LogName = "Security" }]
                })
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId, HostId = 101, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var validPackage = service.BuildExportPackage(anchor);
        Assert.NotNull(validPackage.Summary);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single();
            row.ContentJson = "{\"TopIssues\":[" + string.Join(',', Enumerable.Repeat("{}", PrtgCalibrationCaptureBudget.MaximumTopIssues + 1)) + "]}";
            ctx.SaveChanges();
        }

        var exception = Assert.Throws<CalibrationCapacityException>(() => service.BuildExportPackage(anchor));
        Assert.Contains("陣列項目", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 校準作業budget會拒絕超額並在短暫reservation釋放後恢復()
    {
        using var budget = new PrtgCalibrationCaptureBudget(1024);
        budget.Charge(512, "retained fixture");
        using (budget.ReserveTransient(256, "temporary page"))
            Assert.Equal(768, budget.ReservedBytes);
        Assert.Equal(512, budget.ReservedBytes);
        Assert.Throws<CalibrationCapacityException>(() => budget.Charge(513, "over-limit fixture"));
    }

    [Fact]
    public void 零位元組transientReservation不會冒充retainReference()
    {
        var budget = new PrtgCalibrationCaptureBudget(1024);
        var zeroReservation = budget.ReserveTransient(0, "empty page");
        budget.Charge(1, "still active");
        var retained = budget.Retain();
        budget.Dispose();
        budget.Charge(1, "retained response lifetime");
        zeroReservation.Dispose();
        retained.Dispose();
        Assert.Throws<ObjectDisposedException>(() => budget.Charge(1, "released"));
    }

    [Fact]
    public void KnownIssueRuleStore校準讀取超限時先回傳有界失敗()
    {
        var blob = new EfJsonBlobStore(_fx.NewContext, "rules");
        blob.Mutate<string?>(_ => ("{\"Rules\":[]}" + new string(' ', 1024 * 1024), null));
        var store = new KnownIssueRuleStore(blob);

        var bounded = store.LoadBounded(1024 * 1024);

        Assert.False(bounded.Success);
        Assert.Contains("超過", bounded.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownIssueRuleStore有界讀取保留註解與尾逗號相容性()
    {
        var blob = new EfJsonBlobStore(_fx.NewContext, "rules");
        blob.Mutate<string?>(_ => ("// persisted rule note\n{\"SchemaVersion\":1,\"SeedVersion\":0,\"Rules\":[],}\n", null));
        var store = new KnownIssueRuleStore(blob);

        var bounded = store.LoadBounded(1024 * 1024);

        Assert.True(bounded.Success, bounded.Error);
        Assert.NotNull(bounded.Content);
        Assert.Empty(bounded.Content.Rules);
    }

    [Fact]
    public void KnownIssueRuleStore校準讀取在建立rule物件圖前拒絕過大Rules陣列()
    {
        var blob = new EfJsonBlobStore(_fx.NewContext, "rules");
        var json = "{\"Rules\":[" + string.Join(',', Enumerable.Repeat("{}", 10_001)) + "]}";
        blob.Mutate<string?>(_ => (json, null));
        var store = new KnownIssueRuleStore(blob);

        var bounded = store.LoadBounded(1024 * 1024);

        Assert.False(bounded.Success);
        Assert.Contains("Rules", bounded.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 校準遇到超限實際rulesBlob時狀態與兩種匯出都明確拒絕而不退回seed()
    {
        var anchor = DateTime.Today;
        var now = DateTime.Now;
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        prtgStore.UpsertSensors([new PrtgSensorRow
        {
            Objid = 1001, DeviceObjid = 10, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false
        }], now);
        prtgStore.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow
        {
            MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now
        }]);
        prtgStore.UpsertValues([new PrtgValueRow
        {
            SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50, Quality = PrtgDataQuality.Ok
        }]);

        var blob = new EfJsonBlobStore(_fx.NewContext, "rules");
        blob.Mutate<string?>(_ => (JsonSerializer.Serialize(new RuleFileContent { Rules = KnownIssueSeed.CreateRules() }), null));
        var ruleStore = new KnownIssueRuleStore(blob);
        var service = CreateService(ruleStore);
        var validPackage = service.BuildExportPackage(anchor);
        Assert.NotEmpty(validPackage.RuleThresholds.CurrentRules);
        Assert.NotEmpty(validPackage.ValueBaselines);

        blob.Mutate<string?>(_ => ("{\"SchemaVersion\":1,\"Rules\":[" +
            string.Join(',', Enumerable.Repeat("{}", 10_001)) + "]}", null));

        foreach (var summaryOnly in new[] { false, true })
        {
            var exception = Assert.Throws<CalibrationCapacityException>(
                () => service.BuildExportPackage(anchor, summaryOnly));
            Assert.Contains("Rules", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        var controller = new CalibrationController(service, new RecordingAuditService());
        var statusResult = Assert.IsType<ObjectResult>(controller.GetStatus());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusResult.StatusCode);
        var envelope = Assert.IsAssignableFrom<ApiResponse<object>>(statusResult.Value);
        Assert.Equal("capacity_exceeded", envelope.Error?.Code);
        Assert.Contains("Rules", envelope.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExportPackage_超長SQLiteEventKey在來源列物件化前明確拒絕()
    {
        var anchor = DateTime.Today;
        var now = DateTime.Now;
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = 1001, DeviceObjid = 10, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false
        }], now);
        store.ReplaceHostMapForDate(anchor, [new PrtgHostMapRow
        {
            MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now
        }]);
        store.UpsertValues([new PrtgValueRow
        {
            SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50, Quality = PrtgDataQuality.Ok
        }]);
        var service = CreateService();
        var validPackage = service.BuildExportPackage(anchor);
        Assert.NotEmpty(validPackage.ValueBaselines);

        using (var ctx = _fx.NewContext())
        {
            var record = new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}"
            };
            ctx.DailyRecords.Add(record);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = record.RecordId, HostId = 1, RecordDate = anchor, LogName = "PRTG",
                SourceName = "PRTG:down", EventId = 0, EventKey = new string('x', 256),
                Category = "Service", SeverityRank = 2
            });
            ctx.SaveChanges();
        }

        foreach (var summaryOnly in new[] { false, true })
        {
            var exception = Assert.Throws<CalibrationCapacityException>(
                () => service.BuildExportPackage(anchor, summaryOnly));
            Assert.Contains("EventKey", exception.Message, StringComparison.Ordinal);
            Assert.Contains("未截斷", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildExportPackage_殘留歷史超過單筆容量時明確拒絕完整回看()
    {
        var anchor = new DateTime(2026, 7, 17);
        using (var ctx = _fx.NewContext())
        {
            var candidate = new DailyAnalysisRecord
            {
                HostId = 101, Host = "SEC-SRV-01", Date = anchor, RiskLevel = "高",
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = "Security", Source = "Microsoft-Windows-Security-Auditing", EventId = 4625,
                        LoginFailureDetails = new List<LoginFailureDetail>
                        { new() { Account = "redacted", Source = "redacted", LogonType = 3, Count = 2 } },
                        LoginFailureTotalCount = 2
                    }
                }
            };
            var candidateRow = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor,
                DetailPruned = false, ContentJson = JsonSerializer.Serialize(candidate)
            };
            var historyRow = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor.AddDays(-1),
                DetailPruned = false, ContentJson = new string(' ', CalibrationConstants.ResidualHistoryJsonMaxCharacters + 1)
            };
            ctx.DailyRecords.AddRange(candidateRow, historyRow);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = candidateRow.RecordId, HostId = 101, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            ctx.SaveChanges();
        }

        var exception = Assert.Throws<CalibrationCapacityException>(() => CreateService().BuildExportPackage(anchor));
        Assert.Contains("單筆內容", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExportPackage_殘留資料集不含任何帳號名稱()
    {
        var anchor = new DateTime(2026, 8, 31);
        const string secretAccount = "victim_admin_user_secret";

        using (var ctx = _fx.NewContext())
        {
            var record = new DailyAnalysisRecord
            {
                HostId = 101,
                Host = "SEC-SRV-01",
                Date = anchor,
                RiskLevel = "高",
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = "Security",
                        Source = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625,
                        LoginFailureDetails = new List<LoginFailureDetail>
                        {
                            new() { Account = secretAccount, Source = "192.168.1.100", LogonType = 3, ReasonCode = "bad_password", Count = 50 }
                        }
                    }
                }
            };

            var dr = new DailyRecordRow
            {
                HostId = 101,
                HostName = "SEC-SRV-01",
                RecordDate = anchor,
                RiskLevel = "高",
                DetailPruned = false,
                ContentJson = JsonSerializer.Serialize(record)
            };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = dr.RecordId,
                HostId = 101,
                RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing",
                EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        // 1. 斷言序列化後的整個 JSON 物件絕不含該帳號字串
        var json = JsonSerializer.Serialize(package);
        Assert.DoesNotContain(secretAccount, json);

        // 2. 斷言指標資料列正確產出且數值正確
        Assert.Single(package.ResidualCandidates);
        var candidate = package.ResidualCandidates[0];
        Assert.Equal(101, candidate.HostId);
        Assert.Equal("SEC-SRV-01", candidate.HostName);
        Assert.Equal(4625, candidate.EventId);
        Assert.Equal(1, candidate.CandidateGroupCount);
        Assert.Equal(50, candidate.TotalDetailCount);
        Assert.Equal(1.0, candidate.ConcentrationRatio);
        Assert.Equal(1.0, candidate.MechanicalLogonTypeRatio);
        Assert.Equal(1.0, candidate.SingleGroupRatio);
        Assert.False(candidate.IsTruncated);
    }

    // ── 10. 精簡紀錄被跳過 ──────────────────────────────────────────

    [Fact]
    public void BuildExportPackage_已精簡紀錄DetailPruned被跳過()
    {
        var anchor = new DateTime(2026, 8, 31);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow
            {
                HostId = 201,
                HostName = "PRUNED-SRV",
                RecordDate = anchor,
                RiskLevel = "高",
                DetailPruned = true, // 已精簡
                ContentJson = string.Empty
            };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = dr.RecordId,
                HostId = 201,
                RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing",
                EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        // 已精簡紀錄的明細已不在，必須被跳過
        Assert.Empty(package.ResidualCandidates);
    }

    // ── 11. 匯出包完整組裝測試 ────────────────────────────────────────

    [Fact]
    public void BuildExportPackage_完整組裝_包含版本時間摘要與四大資料集()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(2), AvgValue = 55.0, MinValue = 50.0, MaxValue = 60.0, Quality = PrtgDataQuality.Ok }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        Assert.Equal(CalibrationConstants.CurrentFormatVersion, package.FormatVersion);
        Assert.NotNull(package.Summary);
        Assert.NotNull(package.Summary.PrtgValueBaseline);
        Assert.NotNull(package.Summary.PrtgRuleThresholds);
        Assert.NotNull(package.Summary.TriggeredFetchMagnitude);
        Assert.NotNull(package.Summary.ResidualCredentialThresholds);

        // 值型基線資料集
        Assert.Single(package.ValueBaselines);
        var vb = package.ValueBaselines[0];
        Assert.Equal(1001, vb.SensorObjid);
        Assert.Equal(10, vb.DeviceObjid);
        Assert.Equal(1, vb.HostId);
        Assert.Equal("HOST-1", vb.HostName);
        Assert.Equal("SNMP Disk Free", vb.SensorType);
        Assert.Equal(55.0, vb.AvgValue);

        // 規則門檻資料集（預設 12 條：4 條不限分類＋4 條分類規則＋3 條資源規則＋1 條停用磁碟趨勢範本）
        Assert.NotNull(package.RuleThresholds);
        Assert.Equal(2, package.RuleThresholds.MagnitudeSemanticsVersion);
        Assert.Equal(12, package.RuleThresholds.CurrentRules.Count);
        Assert.Equal(4, package.RuleThresholds.CurrentRules.Count(r => r.SensorCategory == null));
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "disk_free_trend" && r.SensorCategory == "disk");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "down" && r.SensorCategory == "availability" && r.Threshold == 30);
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "down");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "flapping");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "warning");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "silent");
        Assert.Contains(package.RuleThresholds.Explanations, text => text.Contains("RuleEnabled", StringComparison.Ordinal));

        // Resource-period rules have no scalar threshold. The serialized export must state exactly
        // which rules its covered-state evaluator can recompute and which it cannot evaluate.
        var resourceCodes = new[]
        {
            "resource_cpu_sustained_pressure",
            "resource_memory_sustained_pressure",
            "resource_disk_pressure"
        };
        foreach (var code in resourceCodes)
        {
            var resourceRule = Assert.Single(package.RuleThresholds.CurrentRules, r => r.RuleCode == code);
            Assert.Null((object?)resourceRule.Threshold);
        }

        using (var exported = JsonDocument.Parse(JsonSerializer.Serialize(package)))
        {
            var dataset = exported.RootElement.GetProperty("RuleThresholds");
            Assert.True(dataset.TryGetProperty("FormalEvaluationScope", out var scope));
            Assert.Equal("trusted-status-timeline-only", scope.GetString());
            Assert.True(dataset.TryGetProperty("FormalEvaluationSupportedRuleCodes", out var supportedCodes));
            Assert.Equal(new[] { "down", "flapping", "warning" },
                supportedCodes.EnumerateArray()
                    .Select(value => value.GetString()).OrderBy(value => value, StringComparer.Ordinal).ToArray());

            Assert.True(dataset.TryGetProperty("UnsupportedByMagnitudeAnalysisRuleCodes", out var unsupportedCodesElement));
            var unsupportedCodes = unsupportedCodesElement
                .EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("silent", unsupportedCodes);
            Assert.Contains("disk_free_trend", unsupportedCodes);
            Assert.Contains("resource_cpu_sustained_pressure", unsupportedCodes);
            Assert.Contains("resource_memory_sustained_pressure", unsupportedCodes);
            Assert.Contains("resource_disk_pressure", unsupportedCodes);

            var serializedResourceRules = dataset.GetProperty("CurrentRules").EnumerateArray()
                .Where(row => resourceCodes.Contains(row.GetProperty("RuleCode").GetString(), StringComparer.Ordinal)).ToArray();
            Assert.Equal(resourceCodes.Length, serializedResourceRules.Length);
            foreach (var code in resourceCodes)
            {
                var serializedResourceRule = Assert.Single(serializedResourceRules,
                    row => row.GetProperty("RuleCode").GetString() == code);
                Assert.True(serializedResourceRule.TryGetProperty("Threshold", out var serializedThreshold));
                Assert.Equal(JsonValueKind.Null, serializedThreshold.ValueKind);
                Assert.True(serializedResourceRule.TryGetProperty("ThresholdKind", out var thresholdKind));
                Assert.Equal("resource-period-profile", thresholdKind.GetString());
                Assert.True(serializedResourceRule.TryGetProperty("RuleEnabled", out var ruleEnabled));
                Assert.True(ruleEnabled.GetBoolean());
            }

            var serializedDiskTrendRule = Assert.Single(dataset.GetProperty("CurrentRules").EnumerateArray(),
                row => row.GetProperty("RuleCode").GetString() == "disk_free_trend");
            Assert.True(serializedDiskTrendRule.TryGetProperty("Threshold", out var diskTrendThreshold));
            Assert.Equal(JsonValueKind.Null, diskTrendThreshold.ValueKind);
            Assert.True(serializedDiskTrendRule.TryGetProperty("ThresholdKind", out var diskTrendThresholdKind));
            Assert.Equal("disk-trend-profile", diskTrendThresholdKind.GetString());
            Assert.True(serializedDiskTrendRule.TryGetProperty("RuleEnabled", out var diskTrendEnabled));
            Assert.False(diskTrendEnabled.GetBoolean());

            var formalHits = dataset.GetProperty("FormalCurrentHitCounts").EnumerateArray().ToArray();
            Assert.DoesNotContain(formalHits, row =>
                !new[] { "down", "flapping", "warning" }.Contains(row.GetProperty("RuleCode").GetString(), StringComparer.Ordinal));
        }

        // 觸發式量級資料集
        Assert.Single(package.TriggeredMagnitudes);
        Assert.Equal(anchor.Date, package.TriggeredMagnitudes[0].Date);
    }

    [Fact]
    public void BuildExportPackage_格式版本3_含取樣小時與觀測極值且欄位順序正確()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        // 寫入 1 列 ok, 1 列 sampled
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(2), AvgValue = 50.0, MinValue = null, MaxValue = null, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(3), AvgValue = 60.0, MinValue = 45.0, MaxValue = 65.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        Assert.Equal(3, package.FormatVersion);
        Assert.Single(package.ValueBaselines);
        var row = package.ValueBaselines[0];
        Assert.Equal(1, row.OkHours);
        Assert.Equal(1, row.SampledHours);
        Assert.Equal(45.0, row.MinObserved);
        Assert.Equal(65.0, row.MaxObserved);

        var json = JsonSerializer.Serialize(package);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("FormatVersion").GetInt32());

        var vbElement = root.GetProperty("ValueBaselines")[0];
        Assert.True(vbElement.TryGetProperty("SampledHours", out var sampledHoursProp));
        Assert.Equal(1, sampledHoursProp.GetInt32());
        Assert.True(vbElement.TryGetProperty("MinObserved", out var minObservedProp));
        Assert.Equal(45.0, minObservedProp.GetDouble());
        Assert.True(vbElement.TryGetProperty("MaxObserved", out var maxObservedProp));
        Assert.Equal(65.0, maxObservedProp.GetDouble());

        var okHoursIdx = json.IndexOf("\"OkHours\":", StringComparison.Ordinal);
        var sampledHoursIdx = json.IndexOf("\"SampledHours\":", StringComparison.Ordinal);
        Assert.True(okHoursIdx >= 0, "JSON 應包含 OkHours");
        Assert.True(sampledHoursIdx >= 0, "JSON 應包含 SampledHours");
        Assert.True(okHoursIdx < sampledHoursIdx, "OkHours 在 JSON 中的位置應早於 SampledHours");
    }

    [Fact]
    public void 值型基線_KeyMetrics含快照涵蓋與列數()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        // 寫入 24 小時內之 sampled 列
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = DateTime.Now.AddHours(-5), AvgValue = 50.0, Quality = PrtgDataQuality.Sampled, Coverage = 80.0 },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1), AvgValue = 50.0, Quality = PrtgDataQuality.Ok, Coverage = 100.0 }
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        var km = summary.PrtgValueBaseline.KeyMetrics;
        Assert.True(km.ContainsKey("SnapshotTargets"));
        Assert.True(km.ContainsKey("SnapshotSensors24h"));
        Assert.True(km.ContainsKey("SnapshotCoverage24h"));
        Assert.True(km.ContainsKey("ValueBaselineRows"));

        Assert.Equal(1, Convert.ToInt32(km["SnapshotTargets"]));
        Assert.Equal(1, Convert.ToInt32(km["SnapshotSensors24h"]));
        Assert.Equal(80.0, Convert.ToDouble(km["SnapshotCoverage24h"]));
        Assert.True(Convert.ToInt32(km["ValueBaselineRows"]) > 0);
    }

    [Fact]
    public void 值型基線_有可用取樣列時說明含快照取樣小時數()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        // 3 列 coverage 100 的 sampled ＋ 若干 ok
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(1), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(2), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(3), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(4), AvgValue = 20.0, Quality = PrtgDataQuality.Ok, Coverage = 100.0 },
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Contains(summary.PrtgValueBaseline.Explanations, s => s.Contains("3 小時為快照取樣值"));

        // 只有 ok 列時不含「快照取樣值」
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgValues.RemoveRange(ctx.PrtgValues);
            ctx.SaveChanges();
        }
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(1), AvgValue = 20.0, Quality = PrtgDataQuality.Ok, Coverage = 100.0 },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(2), AvgValue = 20.0, Quality = PrtgDataQuality.Ok, Coverage = 100.0 },
        });

        CalibrationService.ClearAssessmentCache();
        var summaryOkOnly = service.AssessStatus(anchor, forceRefresh: true);
        Assert.DoesNotContain(summaryOkOnly.PrtgValueBaseline.Explanations, s => s.Contains("快照取樣值"));
    }

    [Fact]
    public void 值型基線_快照取樣小時與逐日列數只算白名單內的sensor()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 1001 在白名單內（SNMP Disk Free）；2001 是 Ping，不在測試預設白名單
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "Ping", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var day = anchor.AddDays(-1);
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = day.AddHours(1), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 1001, PeriodStart = day.AddHours(2), AvgValue = 20.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            // 白名單外：同一天 5 小時、另一天 1 小時，都不得計入
            new() { SensorObjid = 2001, PeriodStart = day.AddHours(1), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 2001, PeriodStart = day.AddHours(2), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 2001, PeriodStart = day.AddHours(3), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 2001, PeriodStart = day.AddHours(4), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 2001, PeriodStart = day.AddHours(5), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
            new() { SensorObjid = 2001, PeriodStart = day.AddDays(-1).AddHours(1), AvgValue = 1.0, Quality = PrtgDataQuality.Sampled, Coverage = 100.0 },
        });

        CalibrationService.ClearAssessmentCache();
        var summary = CreateService().AssessStatus(anchor, forceRefresh: true);

        Assert.Contains(summary.PrtgValueBaseline.Explanations, s => s.Contains("有 2 小時為快照取樣值"));
        // 逐日列數要與匯出 ValueBaselines 的白名單篩選一致：只有 1001 的那一天
        Assert.Equal(1, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["ValueBaselineRows"]));
    }

    [Fact]
    public void 匯出_感測器摘要統計依nearest_rank與母體標準差()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Unit = "%", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        // 某顆感測器每日平均 [10, 20, 30, 40]
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-3).AddHours(1), AvgValue = 10.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-2).AddHours(1), AvgValue = 20.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(1), AvgValue = 30.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 40.0, Quality = PrtgDataQuality.Ok },
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        var sensorSummary = Assert.Single(package.ValueSensorSummaries);
        Assert.Equal(1001, sensorSummary.SensorObjid);
        Assert.Equal("HOST-1", sensorSummary.HostName);
        Assert.Equal("SNMP Disk Free", sensorSummary.SensorType);
        Assert.Equal("%", sensorSummary.Unit);
        Assert.False(sensorSummary.IsVolumeNormalized);
        Assert.Equal(4, sensorSummary.Days);
        Assert.Equal(4, sensorSummary.UsableHours);
        Assert.Equal(0.0, sensorSummary.SampledRatio);

        // Mean=25、P50=20（ceil(2)=2 → 第 2 個）、P90=40（ceil(3.6)=4）、Max=40、StdDev=√125≈11.1803（容許 4 位小數）
        Assert.Equal(25.0, sensorSummary.Mean);
        Assert.Equal(20.0, sensorSummary.P50);
        Assert.Equal(40.0, sensorSummary.P90);
        Assert.Equal(40.0, sensorSummary.P99);
        Assert.Equal(40.0, sensorSummary.Max);
        Assert.NotNull(sensorSummary.StdDev);
        Assert.Equal(11.1803, sensorSummary.StdDev.Value, 4);
    }

    [Fact]
    public void 匯出_型別曲線長度24且只在有資料的小時有值()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        // 只在 2 時與 14 時有資料
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(2), AvgValue = 25.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(14), AvgValue = 75.0, Quality = PrtgDataQuality.Ok }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        var profile = Assert.Single(package.ValueTypeProfiles);
        Assert.Equal("SNMP Disk Free", profile.SensorType);
        Assert.Equal(24, profile.HourlyCurve.Length);
        Assert.Equal(25.0, profile.HourlyCurve[2]);
        Assert.Equal(75.0, profile.HourlyCurve[14]);
        Assert.Null(profile.HourlyCurve[0]);
        Assert.Null(profile.HourlyCurve[1]);
        Assert.Null(profile.HourlyCurve[3]);
        Assert.Null(profile.HourlyCurve[23]);
    }

    [Fact]
    public void 匯出_流量類標示IsVolumeNormalized()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        _settingsStore.Update(s =>
        {
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Traffic 64bit", "Ping" };
        });

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Traffic 64bit", Paused = false },
            new() { Objid = 1002, DeviceObjid = 10, SensorType = "Ping", Paused = false },
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 1000.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1002, PeriodStart = anchor.AddHours(1), AvgValue = 2.0, Quality = PrtgDataQuality.Ok },
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        var traffic = Assert.Single(package.ValueSensorSummaries, s => s.SensorObjid == 1001);
        var ping = Assert.Single(package.ValueSensorSummaries, s => s.SensorObjid == 1002);

        Assert.True(traffic.IsVolumeNormalized);
        Assert.False(ping.IsVolumeNormalized);
    }

    [Fact]
    public void 匯出_Context帶齊條件()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        _settingsStore.Update(s =>
        {
            s.PrtgFetchStrategy = "conservative";
            s.PrtgRetentionDays = 90;
        });

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        var ctx = package.Context;
        Assert.NotNull(ctx);
        Assert.Equal("full", ctx.Detail);
        Assert.Equal("conservative", ctx.FetchStrategy);
        Assert.Equal(15, ctx.SnapshotIntervalMinutes);
        Assert.Equal(75.0, ctx.SampledMinCoverage);
        Assert.False(string.IsNullOrWhiteSpace(ctx.StatisticsBasis));
    }

    [Fact]
    public void 匯出_summaryOnly時ValueBaselines為空但摘要照出()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50.0, Quality = PrtgDataQuality.Ok }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor, summaryOnly: true);

        Assert.Empty(package.ValueBaselines);
        Assert.NotEmpty(package.ValueSensorSummaries);
        Assert.NotEmpty(package.ValueTypeProfiles);
        Assert.Equal("summary", package.Context.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 匯出_感測器單位超過SQL欄位長度時完整與摘要模式都拒絕(bool summaryOnly)
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Unit = "%", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50.0, Quality = PrtgDataQuality.Ok }
        });

        var service = CreateService();
        var validPackage = service.BuildExportPackage(anchor);
        Assert.NotEmpty(validPackage.ValueBaselines);
        Assert.NotEmpty(validPackage.ValueSensorSummaries);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Unit = new string('u', 65), Paused = false }
        }, now.AddMinutes(1));

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => service.BuildExportPackage(anchor, summaryOnly));
        Assert.Contains("容量", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void 匯出_每日聚合小budget完整分頁或明確拒絕(int maximumRows, bool shouldSucceed)
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Unit = "%", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddDays(-1).AddHours(1), AvgValue = 40.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(1), AvgValue = 50.0, Quality = PrtgDataQuality.Ok }
        });

        var limits = new PrtgCalibrationCaptureLimits(1, 10, maximumRows, 1, 16 * 1024 * 1024, 10);
        var service = CreateService(captureLimits: limits);
        if (!shouldSucceed)
        {
            var exception = Assert.Throws<CalibrationCapacityException>(() => service.BuildExportPackage(anchor));
            Assert.Contains("每日聚合", exception.Message, StringComparison.Ordinal);
            return;
        }

        var package = service.BuildExportPackage(anchor);
        Assert.Equal(2, package.ValueBaselines.Count);
        Assert.Equal(new[] { anchor.Date.AddDays(-1), anchor.Date }, package.ValueBaselines.Select(row => row.Date).ToArray());
    }

    [Fact]
    public void 擷取_不允許放大正式budget或超過56日日期範圍()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var oversized = EfPrtgStore.DefaultCalibrationCaptureLimits with { MaximumMirrorSensors = int.MaxValue };

        Assert.Throws<ArgumentOutOfRangeException>(() => store.CaptureCalibrationData(
            anchor.AddDays(-55), anchor.AddDays(1), Array.Empty<string>(), anchor, oversized));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.CaptureCalibrationData(
            anchor.AddDays(-56), anchor.AddDays(1), Array.Empty<string>(), anchor));
    }

    [Fact]
    public void 擷取_正式SQLite非WAL時明確拒絕()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lf-calibration-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options;
            var store = new EfPrtgStore(() => new LfDbContext(options));
            var anchor = new DateTime(2026, 8, 31);

            var exception = Assert.Throws<CalibrationCapacityException>(() => store.CaptureCalibrationData(
                anchor.AddDays(-55), anchor.AddDays(1), Array.Empty<string>(), anchor));
            Assert.Contains("WAL", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}

[Collection("CalibrationCacheState")]
public class CalibrationControllerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();
    private readonly RecordingAuditService _audit = new();

    public CalibrationControllerTests()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free" };
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationController CreateController(ISystemSettingsStore? settingsStore = null,
        long maximumExportBytes = 64L * 1024 * 1024, PrtgCalibrationCaptureLimits? captureLimits = null)
    {
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        var service = new CalibrationService(_fx.NewContext, prtgStore, issueQuery,
            settingsStore ?? _settingsStore, _ruleStore, captureLimits);
        return new CalibrationController(service, _audit, maximumExportBytes);
    }

    private sealed class DisablePrtgOnSecondReadStore(FakeSystemSettingsStore inner) : ISystemSettingsStore
    {
        private int _reads;
        public bool FirstReadWasEnabled { get; private set; }

        public SystemSettings Get()
        {
            var settings = inner.Get();
            var read = Interlocked.Increment(ref _reads);
            if (read == 1) FirstReadWasEnabled = settings.PrtgEnabled;
            if (read >= 2) settings.PrtgEnabled = false;
            return settings;
        }

        public SystemSettings Update(Action<SystemSettings> mutation) => inner.Update(mutation);
    }

    // ── 1. 授權：非 Maintain 角色打兩個端點皆被拒 ───────────────────────

    [Fact]
    public void CalibrationController_標註Maintain能力()
    {
        var attr = typeof(CalibrationController).GetCustomAttributes(typeof(PermissionAttribute), true)
            .Cast<PermissionAttribute>()
            .FirstOrDefault();
        Assert.NotNull(attr);
        Assert.NotNull(attr.Arguments);
        Assert.Equal(new[] { Capability.Maintain }, attr.Arguments[0]);
    }

    [Theory]
    [InlineData(Capability.Handle)]
    [InlineData(Capability.Assign)]
    [InlineData(Capability.ViewAll)]
    [InlineData(Capability.DevMonitor)]
    [InlineData(Capability.ConfirmPermission)]
    [InlineData(Capability.ViewAudit)]
    public void 非Maintain角色_存取Controller端點被PermissionFilter拒絕(Capability cap)
    {
        var user = FakeCurrentUser.WithCapabilities(cap);
        var filter = new PermissionFilter(new[] { Capability.Maintain }, user, _audit);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/admin/calibration/status";
        httpContext.Request.Method = "GET";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        filter.OnAuthorization(filterContext);

        var result = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        var apiRes = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(apiRes.Success);
        Assert.Equal(ApiErrorCodes.Forbidden, apiRes.Error?.Code);
    }

    [Fact]
    public void Maintain角色_通過PermissionFilter()
    {
        var user = FakeCurrentUser.WithCapabilities(Capability.Maintain);
        var filter = new PermissionFilter(new[] { Capability.Maintain }, user, _audit);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/admin/calibration/status";
        httpContext.Request.Method = "GET";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        filter.OnAuthorization(filterContext);

        Assert.Null(filterContext.Result);
    }

    // ── 2. 匯出閘門：四項未全部達標且未帶 override → 擲驗證例外；帶 override=true → 放行 ─

    [Fact]
    public void 匯出閘門_四項未達標且未覆寫_擲驗證例外()
    {
        var controller = CreateController();

        // 預設無資料時四項皆不足/無法取得
        var ex = Assert.Throws<DomainException>(() => controller.Export(isOverride: false));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("校準資料累積量未達標", ex.Message);
        Assert.Contains("仍要匯出", ex.Message);
    }

    [Fact]
    public void 匯出閘門_四項未達標但帶override_放行並回傳檔案()
    {
        var controller = CreateController();

        var result = controller.Export(isOverride: true);
        var fileResult = Assert.IsAssignableFrom<FileStreamResult>(result);
        Assert.Equal("application/json", fileResult.ContentType);
        Assert.StartsWith("calibration-", fileResult.FileDownloadName);
        Assert.EndsWith(".json", fileResult.FileDownloadName);
        Assert.True(fileResult.FileStream.Length > 0);
        ((IDisposable)fileResult).Dispose();
    }

    // ── 3. 稽核：匯出成功寫稽核，覆寫匯出標記 Override = true ──────────────

    [Fact]
    public void 匯出稽核_覆寫匯出成功記錄稽核包含四項狀態與Override旗標()
    {
        var controller = CreateController();

        var result = Assert.IsAssignableFrom<IDisposable>(controller.Export(isOverride: true));
        result.Dispose();

        var auditEntry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.CalibrationExport, auditEntry.Action);
        Assert.Equal("calibration_data", auditEntry.TargetKind);
        Assert.Contains("PRTG值型基線", auditEntry.Summary);
        Assert.Contains("PRTG規則門檻", auditEntry.Summary);
        Assert.Contains("數值取得量級", auditEntry.Summary);
        Assert.Contains("殘留判定門檻", auditEntry.Summary);
        Assert.Contains("覆寫匯出", auditEntry.Summary);

        Assert.NotNull(auditEntry.DetailJson);
        using var doc = JsonDocument.Parse(auditEntry.DetailJson!);
        Assert.True(doc.RootElement.GetProperty("Override").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("PrtgValueBaseline", out _));
        Assert.True(doc.RootElement.TryGetProperty("PrtgRuleThresholds", out _));
        Assert.True(doc.RootElement.TryGetProperty("TriggeredFetchMagnitude", out _));
        Assert.True(doc.RootElement.TryGetProperty("ResidualCredentialThresholds", out _));
    }

    [Fact]
    public void 匯出稽核_閘門阻擋失敗時不寫入匯出稽核()
    {
        var controller = CreateController();

        Assert.Throws<DomainException>(() => controller.Export(isOverride: false));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 匯出稽核_組包期間設定改變時稽核採實際封包摘要()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 51, DeviceObjid = 6, Name = "sensor", SensorType = "SNMP Disk Free",
                Status = "Up", Paused = false, SyncedAt = DateTime.Now, CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }

        var initialService = new CalibrationService(_fx.NewContext, new EfPrtgStore(_fx.NewContext),
            new EfIssueAggregateQuery(_fx.NewContext, _hostStore), _settingsStore, _ruleStore);
        Assert.Equal(CalibrationStatus.Insufficient, initialService.AssessStatus(forceRefresh: true).PrtgValueBaseline.Status);

        var togglingStore = new DisablePrtgOnSecondReadStore(_settingsStore);
        var file = Assert.IsAssignableFrom<FileStreamResult>(CreateController(togglingStore).Export(isOverride: true));
        Assert.True(togglingStore.FirstReadWasEnabled);

        using var package = JsonDocument.Parse(ReadFile(file));
        var packageStatus = (CalibrationStatus)package.RootElement.GetProperty("Summary")
            .GetProperty("PrtgValueBaseline").GetProperty("Status").GetInt32();
        using var audit = JsonDocument.Parse(Assert.Single(_audit.Entries).DetailJson!);
        Assert.Equal(packageStatus.ToString(), audit.RootElement.GetProperty("PrtgValueBaseline").GetString());
        Assert.Equal(CalibrationStatus.Unavailable, packageStatus);
    }

    [Fact]
    public void GetStatus_正常回傳四項判定狀態與CanExport()
    {
        var controller = CreateController();

        var response = Assert.IsAssignableFrom<OkObjectResult>(controller.GetStatus());
        var payload = Assert.IsType<ApiResponse<CalibrationStatusDto>>(response.Value);
        Assert.True(payload.Success);
        Assert.NotNull(payload.Data);

        var data = payload.Data!;
        Assert.NotNull(data.PrtgValueBaseline);
        Assert.NotNull(data.PrtgRuleThresholds);
        Assert.NotNull(data.TriggeredFetchMagnitude);
        Assert.NotNull(data.ResidualCredentialThresholds);
        Assert.False(data.CanExport);
        Assert.IsAssignableFrom<IDisposable>(response).Dispose();
    }

    [Fact]
    public void Export_帶detail為summary_檔名含summary且匯出包ValueBaselines為空且稽核Detail為summary()
    {
        var controller = CreateController();

        var result = controller.Export(isOverride: true, detail: "summary");
        var fileResult = Assert.IsAssignableFrom<FileStreamResult>(result);

        Assert.Equal($"calibration-{DateTime.Today:yyyyMMdd}-summary.json", fileResult.FileDownloadName);
        Assert.Contains("-summary", fileResult.FileDownloadName);

        using var doc = JsonDocument.Parse(ReadFile(fileResult));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ValueBaselines", out var vb));
        Assert.Equal(0, vb.GetArrayLength());
        Assert.True(root.TryGetProperty("Context", out var ctx));
        Assert.Equal("summary", ctx.GetProperty("Detail").GetString());

        var auditEntry = Assert.Single(_audit.Entries);
        Assert.NotNull(auditEntry.DetailJson);
        using var auditDoc = JsonDocument.Parse(auditEntry.DetailJson!);
        Assert.True(auditDoc.RootElement.TryGetProperty("Detail", out var auditDetailProp));
        Assert.Equal("summary", auditDetailProp.GetString());
    }

    private static byte[] ReadFile(FileStreamResult result)
    {
        using var stream = new MemoryStream();
        result.FileStream.CopyTo(stream);
        Assert.IsAssignableFrom<IDisposable>(result).Dispose();
        return stream.ToArray();
    }

    private static (ServiceProvider Services, ActionContext Context) CreateMvcActionContext(Stream responseBody,
        CancellationToken requestAborted = default)
    {
        var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = responseBody;
        if (requestAborted.CanBeCanceled)
            http.RequestAborted = requestAborted;
        return (services, new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));
    }

    [Fact]
    public async Task Export_MVC傳送期間保留Admission直到response完成()
    {
        var controller = CreateController();
        await using var body = new BlockingResponseStream();
        var mvc = CreateMvcActionContext(body);
        using var services = mvc.Services;
        controller.ControllerContext = new ControllerContext(mvc.Context);
        var file = Assert.IsAssignableFrom<FileStreamResult>(controller.Export(isOverride: true));

        var execution = file.ExecuteResultAsync(mvc.Context);
        await body.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = Assert.IsType<ObjectResult>(controller.Export(isOverride: true));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("capture_busy", Assert.IsType<ApiResponse<object>>(blocked.Value).Error?.Code);

        body.AllowWrite.TrySetResult(true);
        await execution;
        var next = Assert.IsAssignableFrom<IDisposable>(controller.Export(isOverride: true));
        next.Dispose();
    }

    [Fact]
    public async Task GetStatus_MVC傳送期間保留Admission並阻擋匯出()
    {
        var controller = CreateController();
        await using var body = new BlockingResponseStream();
        var mvc = CreateMvcActionContext(body);
        using var services = mvc.Services;
        controller.ControllerContext = new ControllerContext(mvc.Context);
        var status = Assert.IsAssignableFrom<OkObjectResult>(controller.GetStatus());

        var execution = status.ExecuteResultAsync(mvc.Context);
        await body.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = Assert.IsType<ObjectResult>(controller.Export(isOverride: true));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("capture_busy", Assert.IsType<ApiResponse<object>>(blocked.Value).Error?.Code);

        body.AllowWrite.TrySetResult(true);
        await execution;
        var next = Assert.IsAssignableFrom<IDisposable>(controller.Export(isOverride: true));
        next.Dispose();
    }

    [Fact]
    public async Task Export_MVC傳送失敗與clientAbort都釋放Admission()
    {
        var controller = CreateController();
        var failingMvc = CreateMvcActionContext(new ThrowingResponseStream());
        using (failingMvc.Services)
        {
            controller.ControllerContext = new ControllerContext(failingMvc.Context);
            var failedFile = Assert.IsAssignableFrom<FileStreamResult>(controller.Export(isOverride: true));
            await Assert.ThrowsAnyAsync<IOException>(() => failedFile.ExecuteResultAsync(failingMvc.Context));
        }

        var afterFailure = Assert.IsAssignableFrom<IDisposable>(controller.Export(isOverride: true));
        afterFailure.Dispose();

        using var abort = new CancellationTokenSource();
        await using var blockedBody = new BlockingResponseStream();
        var abortMvc = CreateMvcActionContext(blockedBody, abort.Token);
        var abortFeature = new RecordingRequestLifetimeFeature { RequestAborted = abort.Token };
        abortMvc.Context.HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(abortFeature);
        using (abortMvc.Services)
        {
            controller.ControllerContext = new ControllerContext(abortMvc.Context);
            var abortFile = Assert.IsAssignableFrom<FileStreamResult>(controller.Export(isOverride: true));
            var execution = abortFile.ExecuteResultAsync(abortMvc.Context);
            await blockedBody.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abort.Cancel();
            await execution;
            Assert.True(abortFeature.AbortCalled, "MVC must abort the canceled file response.");
            Assert.False(blockedBody.AllowWrite.Task.IsCompleted);
        }

        var afterAbort = Assert.IsAssignableFrom<IDisposable>(CreateController().Export(isOverride: true));
        afterAbort.Dispose();
    }

    private sealed class RecordingRequestLifetimeFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool AbortCalled { get; private set; }
        public void Abort() => AbortCalled = true;
    }

    private sealed class BlockingResponseStream : MemoryStream
    {
        public TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult(true);
            await AllowWrite.Task.WaitAsync(cancellationToken);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult(true);
            await AllowWrite.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowingResponseStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("fixture response failure"));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("fixture response failure"));
    }

    [Fact]
    public void Export_JSON超過注入上限時override也回容量拒絕而非部分檔案()
    {
        var result = CreateController(maximumExportBytes: 1).Export(isOverride: true);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.StatusCode);
        var envelope = Assert.IsType<ApiResponse<object>>(problem.Value);
        Assert.False(envelope.Success);
        Assert.Equal("capacity_exceeded", envelope.Error?.Code);
        Assert.Contains("使用摘要模式取得同一完整範圍的統計；需要逐時明細請用診斷資料搬運匯出", envelope.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_audit.Entries);
    }
}

