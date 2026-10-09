using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgFormalRuleCaseManifestTests
{
    public static IEnumerable<object[]> StateFamilyDailyNegativeCases()
    {
        foreach (var family in Manifest.Value.Families.Where(item => item.Family is "Down" or "Warning" or "Flapping"))
        foreach (var testCase in family.NegativeAndBoundaryCases.Where(item =>
                     item.CaseId.Contains("-N", StringComparison.Ordinal) && item.Mutations.ValueKind == JsonValueKind.Object))
            yield return [family.Family, testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(StateFamilyDailyNegativeCases))]
    public async Task StateFamilyJsonNegativeHasDailyPositiveBaselineAndPreservesHumanState(string familyName, string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == familyName);
        var testCase = family.NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        var scenario = RequiredScenario(mutations, caseId);
        Assert.Equal(familyName, caseId.Split('-')[1]);
        var configuredCategoryFilter = mutations.TryGetProperty("ruleCategoryFilter", out var categoryFilter)
            ? categoryFilter.GetString() : null;
        if (scenario == "wrong-category-profile")
        {
            Assert.Equal(PrtgSensorCategories.Availability, configuredCategoryFilter);
            var positiveCategory = family.PositiveCases[0].Mutations.TryGetProperty("ruleCategoryFilter", out var positiveFilter)
                ? positiveFilter.GetString() : null;
            Assert.Equal(configuredCategoryFilter, positiveCategory);
        }

        using (var baselineFixture = new PrtgFormalRuleCaseFixture())
        {
            if (configuredCategoryFilter is not null)
                SetStateRuleCategoryFilter(baselineFixture, familyName, configuredCategoryFilter);
            var baselineHost = SeedStateScenario(baselineFixture, familyName, "baseline", null);
            var baselineRun = await baselineFixture.RunDailyAsync(baselineHost, baselineFixture.AnalysisDay);
            var baselineFinding = RequireDailyFinding(baselineRun, baselineHost, baselineFixture.AnalysisDay,
                "PRTG:" + familyName.ToLowerInvariant(), testCase.CaseId + " baseline");
            if (familyName == "Down")
                Assert.Equal("builtin-prtg-down-availability", baselineFinding.RuleId);
        }

        using var fixture = new PrtgFormalRuleCaseFixture();
        if (configuredCategoryFilter is not null)
            SetStateRuleCategoryFilter(fixture, familyName, configuredCategoryFilter);
        var host = SeedStateScenario(fixture, familyName, scenario, mutations);
        _ = fixture.SeedHumanOwnedBaseline(host);
        var beforeHuman = scenario == "missing-parent"
            ? CaptureHumanSidecars(fixture, host, fixture.AnalysisDay)
            : fixture.CaptureHumanOwnedData(host);
        var beforeOpenCaseIds = fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (scenario == "missing-parent")
            DeleteParentRecord(fixture, host, fixture.AnalysisDay);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        var source = "PRTG:" + familyName.ToLowerInvariant();
        if (scenario is "wrong-category-profile" && familyName == "Down")
            Assert.DoesNotContain(run.Registry.For(host.HostId, fixture.AnalysisDay),
                finding => finding.RuleId == "builtin-prtg-down-availability");
        else if (scenario is not ("missing-parent" or "partial-parent" or "failed-parent" or "running-parent" or "unknown-parent"))
            Assert.DoesNotContain(run.Registry.For(host.HostId, fixture.AnalysisDay), finding => finding.Source == source);
        var expectedOtherPrtgSources = testCase.ExpectedFormalSideEffects
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var actualOtherPrtgSources = run.Registry.For(host.HostId, fixture.AnalysisDay)
            .Where(finding => finding.Source.StartsWith("PRTG:", StringComparison.Ordinal) && finding.Source != source)
            .Select(finding => finding.Source).Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedOtherPrtgSources, actualOtherPrtgSources);
        if (scenario == "missing-parent")
            Assert.Equal(beforeHuman, CaptureHumanSidecars(fixture, host, fixture.AnalysisDay));
        else
        {
            Assert.Equal(beforeHuman, fixture.CaptureHumanOwnedData(host));
            var parent = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
            Assert.Equal(AnalysisLogSource.Netiq, parent.LogSource);
            Assert.Equal(parentStatusForScenario(scenario), parent.LatestNetiqAttemptStatus, ignoreCase: true);
            Assert.Contains(parent.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline" && issue.EventId == 53053);
        }
        Assert.Equal(beforeOpenCaseIds, fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
        if (scenario == "missing-parent")
            Assert.Empty(fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
                .ReadRecent(fixture.AnalysisDay, 1));
        else
        {
            var parent = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
            Assert.Equal(AnalysisLogSource.Netiq, parent.LogSource);
            Assert.Equal(parentStatusForScenario(scenario), parent.LatestNetiqAttemptStatus, ignoreCase: true);
            Assert.DoesNotContain(parent.TopIssues, issue => issue.Source == source);
            if (scenario is "partial-parent" or "failed-parent" or "running-parent" or "unknown-parent")
                Assert.False(parent.CanSupplementWithPrtg());
        }
    }

    public static IEnumerable<object[]> SilentFamilyDailyNegativeCases()
    {
        foreach (var testCase in Manifest.Value.Families.Single(item => item.Family == "Silent")
                     .NegativeAndBoundaryCases.Where(item => item.CaseId.Contains("-N", StringComparison.Ordinal)))
            yield return [testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(SilentFamilyDailyNegativeCases))]
    public async Task SilentJsonNegativeHasDailyPositiveBaselineAndPreservesHumanState(string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "Silent");
        var testCase = family.NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        var binding = testCase.Mutations.ValueKind == JsonValueKind.Object
            ? NormalizeSilentScenario(RequiredScenario(testCase.Mutations, caseId))
            : throw new InvalidOperationException($"{caseId} has no JSON-driven scenario mutation.");

        using (var baselineFixture = new PrtgFormalRuleCaseFixture())
        {
            var baselineAsOf = DateTimeOffset.UtcNow;
            var baselineDay = TimeZoneInfo.ConvertTime(baselineAsOf, TimeZoneInfo.Local).Date;
            baselineFixture.UseAnalysisDay(baselineDay);
            var baselineHost = baselineFixture.SeedSilentCase();
            await baselineFixture.CaptureSyntheticSilentSourceAsync(baselineHost, baselineAsOf);
            var baselineRun = await baselineFixture.RunDailyAsync(baselineHost, baselineDay);
            _ = RequireDailyFinding(baselineRun, baselineHost, baselineDay, "PRTG:silent", testCase.CaseId + " baseline");
        }

        using var fixture = new PrtgFormalRuleCaseFixture();
        var sourceAsOf = DateTimeOffset.UtcNow;
        var day = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date;
        fixture.UseAnalysisDay(day);
        var availabilityStatus = binding == "all-unknown-no-up-availability" ? "Unknown" : "Up";
        var resourceStatus = binding == "all-unknown-no-up-availability" ? "Up" : "Unknown";
        var host = fixture.SeedSilentCase(resourceStatus, availabilityStatus);
        _ = fixture.SeedHumanOwnedBaseline(host);
        if (binding == "missing-parent")
            DeleteParentRecord(fixture, host, day);
        else if (binding == "partial-parent")
            SetParentAttemptStatus(fixture, host, day, "partial");
        var beforeHuman = binding == "missing-parent"
            ? CaptureHumanSidecars(fixture, host, day)
            : fixture.CaptureHumanOwnedBaseline(host);
        var beforeOpenCaseIds = fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray();

        if (binding != "missing-snapshot")
            await fixture.CaptureSyntheticSilentSourceAsync(host, sourceAsOf,
                resourceStatus: resourceStatus, availabilityStatus: availabilityStatus);
        MutateSilentSnapshot(fixture, host, day, binding, sourceAsOf);

        var run = await fixture.RunDailyAsync(host, day);
        if (binding is not ("missing-parent" or "partial-parent"))
            Assert.DoesNotContain(run.Registry.For(host.HostId, day), finding => finding.Source == "PRTG:silent");
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var configuredUrl = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().PrtgUrl;
        IReadOnlyDictionary<long, long> currentMap = binding == "wrong-host-map"
            ? new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId + 1 }
            : new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId };
        var readiness = new PrtgSilentPresenceFormalConsumer(fixture.Backend).EvaluateWithReadiness(day, policy,
            configuredUrl, currentMap,
            KnownIssueCatalog.Rules).Devices[PrtgFormalRuleCaseFixture.DeviceId];
        if (binding is "missing-parent" or "partial-parent")
        {
            Assert.Equal(PrtgSilentPresenceReadinessState.QualifiedHit, readiness.State);
            Assert.Equal("qualified-silent-hit", readiness.Reason);
            Assert.Equal(binding == "missing-parent" ? "missing-netiq-parent" : "partial-netiq-parent",
                testCase.FailedQualification);
        }
        else if (binding is "all-unknown-no-up-availability" or "unknown-sensor-no-availability-category")
        {
            Assert.Equal(PrtgSilentPresenceReadinessState.QualifiedNoHit, readiness.State);
            Assert.Equal("qualified-no-silent-hit", readiness.Reason);
            Assert.Equal(readiness.Reason, testCase.FailedQualification);
        }
        else
        {
            var expectedReadiness = ExpectedSilentReadiness(binding);
            Assert.Equal(expectedReadiness.State, readiness.State);
            Assert.Equal(expectedReadiness.Reason, readiness.Reason);
            Assert.Equal(expectedReadiness.Reason, testCase.FailedQualification);
        }
        if (binding == "missing-parent")
            Assert.Equal(beforeHuman, CaptureHumanSidecars(fixture, host, day));
        else
            Assert.Equal(beforeHuman, fixture.CaptureHumanOwnedBaseline(host));
        Assert.Equal(beforeOpenCaseIds, fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
        if (binding == "missing-parent")
            Assert.Empty(fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
                .ReadRecent(day, 1));
        else
        {
            var parent = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1));
            Assert.Equal(AnalysisLogSource.Netiq, parent.LogSource);
            Assert.Equal(binding == "partial-parent" ? "partial" : "success",
                parent.LatestNetiqAttemptStatus, ignoreCase: true);
            Assert.DoesNotContain(parent.TopIssues, issue => issue.Source == "PRTG:silent");
            if (binding == "partial-parent") Assert.False(parent.CanSupplementWithPrtg());
        }
    }

    private static WebHost SeedStateScenario(PrtgFormalRuleCaseFixture fixture, string family,
        string scenario, JsonElement? mutations)
    {
        if (scenario == "baseline")
            return fixture.SeedCoveredStateCase("F53-state-baseline", "Ping", "Ping", "Up",
                day => PositiveStateTransitions(family, day), PrtgSensorCategories.Availability);

        var parentStatus = scenario switch
        {
            "partial-parent" => "partial",
            "failed-parent" => "failed",
            "running-parent" => "running",
            "unknown-parent" => "unknown",
            _ => "success"
        };
        const bool includeParent = true;
        var category = scenario == "wrong-category-profile" ? PrtgSensorCategories.Cpu : PrtgSensorCategories.Availability;
        var host = fixture.SeedCoveredStateCase("F53-state-negative-" + scenario, "Ping", "Ping", "Up",
            day => NegativeStateTransitions(family, scenario, day, mutations), category, parentStatus, includeParent);

        switch (scenario)
        {
            case "paused-interval":
                fixture.Backend.PrtgStore().UpsertSensors([new PrtgSensorRow
                { Objid = 53002, DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId, Name = "Ping", SensorType = "Ping",
                    Status = "Up", Category = PrtgSensorCategories.Availability, Paused = true }], DateTime.Now);
                break;
            case "uncovered-short-gap":
                new PrtgSensorTimelineStore(fixture.Backend.Blob(PrtgSensorTimelineStore.Prefix + "53002"))
                    .Update(timeline =>
                    {
                        var covered = timeline.Coverage.Single();
                        timeline.Coverage =
                        [
                            covered with { Through = new DateTimeOffset(fixture.AnalysisDay.AddHours(1).AddMinutes(15)) },
                            covered with { From = new DateTimeOffset(fixture.AnalysisDay.AddHours(1).AddMinutes(25)) }
                        ];
                    });
                break;
            case "future-only-transition":
            case "late-transition-outside-asof":
                new PrtgSensorTimelineStore(fixture.Backend.Blob(PrtgSensorTimelineStore.Prefix + "53002")).Update(timeline =>
                {
                    var afterCutoff = fixture.AnalysisDay.AddDays(1).AddHours(scenario == "future-only-transition" ? 1 : 4);
                    timeline.Accept(timeline.ValidFrom, new DateTimeOffset(afterCutoff.AddHours(1)),
                        [new PrtgTimedState(53002, new DateTimeOffset(afterCutoff), "Down",
                            timeline.SourceGeneration, timeline.ResourceGeneration)]);
                });
                break;
            case "wrong-resource-generation":
            case "wrong-source-generation":
                new PrtgSensorTimelineStore(fixture.Backend.Blob(PrtgSensorTimelineStore.Prefix + "53002")).Update(timeline =>
                {
                    if (scenario == "wrong-resource-generation") timeline.ResourceGeneration = "stale-resource-generation";
                    else timeline.SourceGeneration = "stale-source-generation";
                });
                break;
            case "inactive-resource":
                SetResourceIdentity(fixture, identity => identity.Active = false);
                break;
            case "mapping-drift":
                fixture.Backend.PrtgStore().ReplaceHostMapForDate(fixture.AnalysisDay,
                    [new PrtgHostMapRow { DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId,
                        MapDate = fixture.AnalysisDay, HostId = host.HostId + 1, HostName = "OTHER-HOST",
                        MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
                break;
            case "scope-excluded":
                new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
                    .Update(policy => policy.SensorIds = []);
                break;
            case "disabled-rule":
                SetRuleEnabled(fixture, family, enabled: false);
                break;
        }
        return host;
    }

    private static IReadOnlyList<(DateTime At, string Status)> PositiveStateTransitions(string family, DateTime day) => family switch
    {
        "Down" => [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(30), "Up")],
        "Warning" => [(day.AddHours(1), "Warning"), (day.AddHours(5), "Up")],
        _ => Enumerable.Range(0, 5).SelectMany(index => new[]
        { (day.AddHours(1 + index * 2), "Down"), (day.AddHours(2 + index * 2), "Up") }).ToArray()
    };

    private static IReadOnlyList<(DateTime At, string Status)> NegativeStateTransitions(string family,
        string scenario, DateTime day, JsonElement? mutations)
    {
        if (scenario is "future-only-transition" or "late-transition-outside-asof" or "up-state-only" or
            "ack-only-without-down" or "missing-down-transition" or "missing-up-transition")
        {
            return scenario switch
            {
                "future-only-transition" or "late-transition-outside-asof" => [],
                "up-state-only" or "ack-only-without-down" => [(day.AddHours(1), "Up (Acknowledged)")],
                "missing-down-transition" => [(day.AddHours(1), "Up"), (day.AddHours(2), "Up")],
                _ => [(day.AddHours(1), "Down")]
            };
        }
        if (scenario == "uncovered-short-gap") return [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(29), "Up")];
        if (scenario == "four-of-five-round-trips")
            return Enumerable.Range(0, 4).SelectMany(index => new[]
                { (day.AddHours(1 + index * 2), "Down"), (day.AddHours(2 + index * 2), "Up") }).ToArray();
        if (scenario == "duplicate-poll-checkpoint" && family == "Down")
        {
            var up = day.AddHours(1).AddMinutes(29);
            return [(day.AddHours(1), "Down"), (up, "Up"), (up, "Up")];
        }
        if (scenario == "duplicate-poll-checkpoint" && family == "Warning")
        {
            var up = day.AddHours(4).AddMinutes(59);
            return [(day.AddHours(1), "Warning"), (up, "Up"), (up, "Up")];
        }
        if (scenario is "unknown-gap" or "unknown-gap-not-counted")
        {
            const string status = "Unknown";
            return family switch
            {
                "Down" => [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(12), status),
                    (day.AddHours(2), "Down"), (day.AddHours(2).AddMinutes(12), "Up")],
                "Warning" => [(day.AddHours(1), "Warning"), (day.AddHours(2), status),
                    (day.AddHours(3), "Warning"), (day.AddHours(4), "Up")],
                _ => [(day.AddHours(1), "Down"), (day.AddHours(2), status),
                    (day.AddHours(3), "Down"), (day.AddHours(4), "Up")]
            };
        }
        if (scenario is "below-threshold" or "below-threshold-total")
        {
            var threshold = mutations is { } json && json.TryGetProperty("threshold", out var thresholdValue)
                ? thresholdValue.GetInt32() : family == "Down" ? 30 : family == "Warning" ? 240 : 5;
            if (family == "Down") return [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(Math.Max(1, threshold - 1)), "Up")];
            if (family == "Warning") return [(day.AddHours(1), "Warning"), (day.AddHours(1).AddMinutes(Math.Max(1, threshold - 1)), "Up")];
            return Enumerable.Range(0, Math.Max(1, threshold - 1)).SelectMany(index => new[]
                { (day.AddHours(1 + index * 2), "Down"), (day.AddHours(2 + index * 2), "Up") }).ToArray();
        }
        if (scenario == "two-segments-not-consecutive-claim")
            return [(day.AddHours(1), "Warning"), (day.AddHours(3), "Up"),
                (day.AddHours(4), "Warning"), (day.AddHours(5).AddMinutes(59), "Up")];
        if (scenario == "recovered-before-threshold")
            return family == "Down"
                ? [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(29), "Up")]
                : family == "Warning"
                    ? [(day.AddHours(1), "Warning"), (day.AddHours(1).AddMinutes(239), "Up")]
                    : Enumerable.Range(0, 4).SelectMany(index => new[]
                        { (day.AddHours(1 + index * 2), "Down"), (day.AddHours(2 + index * 2), "Up") }).ToArray();
        if (scenario == "repeated-poll-checkpoints" || scenario == "duplicate-poll-checkpoint")
            return Enumerable.Range(0, 4).SelectMany(index => new[]
                { (day.AddHours(1 + index * 2), "Down"), (day.AddHours(2 + index * 2), "Up"),
                    (day.AddHours(2 + index * 2), "Up") }).ToArray();
        if (scenario == "missing-parent" || scenario is "partial-parent" or "failed-parent" or "running-parent" or "unknown-parent" ||
            scenario is "wrong-resource-generation" or "wrong-source-generation" or "inactive-resource" or "mapping-drift" or
            "scope-excluded" or "wrong-category-profile" or "disabled-rule" or "paused-interval")
            return PositiveStateTransitions(family, day);
        throw new InvalidOperationException($"{family} case has unsupported scenario '{scenario}'.");
    }

    private static void SetResourceIdentity(PrtgFormalRuleCaseFixture fixture, Action<PrtgResourceIdentity> mutation)
    {
        var store = fixture.Backend.PrtgStore();
        var identity = store.GetResourceIdentity(53002);
        mutation(identity);
        var json = JsonSerializer.Serialize(identity);
        fixture.Backend.Blob(PrtgResourceIdentityStore.Prefix + "53002").Mutate(_ => (json, true));
    }

    private static void SetRuleEnabled(PrtgFormalRuleCaseFixture fixture, string family, bool enabled)
    {
        var ruleId = family switch
        {
            "Down" => "builtin-prtg-down-availability",
            "Warning" => "builtin-prtg-warning",
            "Silent" => "builtin-prtg-silent",
            _ => "builtin-prtg-flapping"
        };
        var rules = KnownIssueSeed.CreateRules().Select(rule => rule.Id == ruleId
            ? rule.CloneForSeedOverwrite(enabled) : rule).ToList();
        new KnownIssueRuleStore(fixture.Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    private static void SetStateRuleCategoryFilter(PrtgFormalRuleCaseFixture fixture,
        string family, string category)
    {
        if (family == "Down")
        {
            Assert.Equal(PrtgSensorCategories.Availability, category);
            SetDownAvailabilityRule(fixture);
            return;
        }
        var ruleId = family switch
        {
            "Warning" => "builtin-prtg-warning",
            "Flapping" => "builtin-prtg-flapping",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown state rule family.")
        };
        var rules = KnownIssueSeed.CreateRules();
        var rule = rules.Single(item => item.Id == ruleId);
        typeof(KnownIssueRule).GetProperty(nameof(KnownIssueRule.PrtgSensorCategory))!
            .SetValue(rule, category);
        new KnownIssueRuleStore(fixture.Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    [Fact]
    public async Task SilentSnapshotMutationUsesSourceZoneDayAcrossUtcMidnight()
    {
        var sourceAsOf = DateTimeOffset.UtcNow.AddSeconds(-5);
        var utcDay = sourceAsOf.UtcDateTime.Date;
        var sourceZone = TimeZoneInfo.GetSystemTimeZones()
            .Where(zone => TimeZoneInfo.ConvertTime(sourceAsOf, zone).Date != utcDay)
            .OrderBy(zone => zone.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No installed timezone crosses the current UTC day boundary.");
        var expectedSourceDay = TimeZoneInfo.ConvertTime(sourceAsOf, sourceZone).Date;

        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.UseSilentSourceTimeZone(sourceZone.Id);
        fixture.UseAnalysisDay(TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date);
        var host = fixture.SeedSilentCase();
        var captured = await fixture.CaptureSyntheticSilentSourceAsync(host, sourceAsOf);

        Assert.NotEqual(utcDay, expectedSourceDay);
        Assert.Equal(expectedSourceDay, captured.SourceDay);
        MutateSilentSnapshot(fixture, host, fixture.AnalysisDay, "failed-source-read", sourceAsOf);

        var mutated = new PrtgSilentPresenceSnapshotStore(fixture.Backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(PrtgFormalRuleCaseFixture.DeviceId, expectedSourceDay)))
            .Get(PrtgFormalRuleCaseFixture.DeviceId);
        Assert.Equal(PrtgPresenceReadQuality.Failed, mutated?.ReadQuality);
        Assert.Null(new PrtgSilentPresenceSnapshotStore(fixture.Backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(PrtgFormalRuleCaseFixture.DeviceId, utcDay)))
            .Get(PrtgFormalRuleCaseFixture.DeviceId));
    }

    private static void SetDownAvailabilityRule(PrtgFormalRuleCaseFixture fixture)
    {
        var rules = KnownIssueSeed.CreateRules().Select(rule => rule.Id == "builtin-prtg-down"
            ? rule.CloneForSeedOverwrite(false) : rule).ToList();
        new KnownIssueRuleStore(fixture.Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    private static void MutateSilentSnapshot(PrtgFormalRuleCaseFixture fixture, WebHost host,
        DateTime day, string scenario, DateTimeOffset sourceAsOf)
    {
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
        var sourceDay = TimeZoneInfo.ConvertTime(sourceAsOf, sourceZone).Date;
        var store = new PrtgSilentPresenceSnapshotStore(fixture.Backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(PrtgFormalRuleCaseFixture.DeviceId, sourceDay)));
        var snapshot = store.Get(PrtgFormalRuleCaseFixture.DeviceId);
        if (scenario == "missing-snapshot") return;
        if (snapshot is null) throw new InvalidOperationException($"{scenario} setup failed to capture its baseline source snapshot.");
        switch (scenario)
        {
            case "inventory-count-mismatch": snapshot = snapshot with { ReportedSensorCount = snapshot.ReportedSensorCount + 1 }; break;
            case "partial-inventory": snapshot = snapshot with { ReadQuality = PrtgPresenceReadQuality.Partial }; break;
            case "missing-source-asof": snapshot = snapshot with { SourceAsOf = null }; break;
            case "stale-source-response": snapshot = snapshot with { CapturedAtUtc = sourceAsOf.AddMinutes(5) }; break;
            case "failed-source-read": snapshot = snapshot with { ReadQuality = PrtgPresenceReadQuality.Failed }; break;
            case "mapping-fingerprint-changed": snapshot = snapshot with { MappingFingerprint = "stale-map" }; break;
            case "source-generation-drift": snapshot = snapshot with { SourceGeneration = "stale-source-generation" }; break;
            case "source-authority-drift": snapshot = snapshot with { SourceAuthorityFingerprint = "stale-source-authority" }; break;
            case "wrong-analysis-day": snapshot = snapshot with { SourceDay = day.AddDays(-2) }; break;
            case "future-source-point": snapshot = snapshot with { SourceAsOf = sourceAsOf.AddDays(1), DeviceStatusAsOf = sourceAsOf.AddDays(1), CapturedAtUtc = sourceAsOf.AddDays(1) }; break;
            case "unknown-sensor-no-availability-category":
                snapshot = snapshot with { Sensors = snapshot.Sensors.Select(sensor => sensor.Category == PrtgSensorCategories.Availability
                    ? sensor with { Category = PrtgSensorCategories.Hardware } : sensor).ToArray() };
                snapshot = snapshot with { InventoryFingerprint = InventoryFingerprint(snapshot.Sensors) };
                break;
            case "paused-device": snapshot = snapshot with { DevicePaused = true }; break;
            case "all-sensors-paused":
                snapshot = snapshot with { Sensors = snapshot.Sensors.Select(sensor => sensor with { Paused = true }).ToArray() };
                snapshot = snapshot with { InventoryFingerprint = InventoryFingerprint(snapshot.Sensors) };
                break;
            case "missing-timeline-coverage":
            case "timeline-before-asof":
                foreach (var sensor in snapshot.Sensors)
                    new PrtgSensorTimelineStore(fixture.Backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.SensorObjid))
                        .Update(timeline =>
                        {
                            if (scenario == "missing-timeline-coverage") timeline.Coverage.Clear();
                            else timeline.Coverage = timeline.Coverage.Select(coverage => coverage with
                                { Through = sourceAsOf.AddMinutes(-1) }).ToList();
                        });
                break;
            case "scope-excluded":
                new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy => policy.HostIds = []);
                break;
            case "wrong-host-map":
                fixture.Backend.PrtgStore().ReplaceHostMapForDate(day, [new PrtgHostMapRow
                { DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId, MapDate = day, HostId = host.HostId + 1,
                    HostName = "OTHER-HOST", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now }]);
                break;
            case "missing-parent":
            case "partial-parent":
                break;
            case "disabled-rule":
                SetRuleEnabled(fixture, "Silent", enabled: false);
                break;
            case "all-unknown-no-up-availability":
                break;
            default: throw new InvalidOperationException($"Silent case has unsupported scenario '{scenario}'.");
        }
        store.Save(snapshot);
    }

    private static string InventoryFingerprint(IReadOnlyList<PrtgSilentSensorSnapshot> sensors)
    {
        var canonical = string.Join("\n", sensors.OrderBy(sensor => sensor.SensorObjid)
            .Select(sensor => $"{sensor.SensorObjid}|{sensor.DeviceObjid}|{sensor.SensorType}|{sensor.Category}|{sensor.SourceStatus}|{sensor.Paused}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void DeleteParentRecord(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day)
    {
        var store = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        Assert.IsType<EfAnalysisRecordStore>(store).DeleteDays([day.Date]);
    }

    private static void SetParentAttemptStatus(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day, string status)
    {
        using var context = fixture.Backend.CreateContext();
        var row = context.DailyRecords.Single(record => record.HostId == host.HostId && record.RecordDate == day.Date);
        var parent = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)
            ?? throw new InvalidDataException("Daily parent row has invalid JSON.");
        parent.LatestNetiqAttemptStatus = status;
        row.ContentJson = JsonSerializer.Serialize(parent);
        context.SaveChanges();
    }

    private static string CaptureHumanSidecars(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day)
    {
        var openCase = Assert.Single(fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName));
        var handling = Assert.Single(fixture.Backend.IssueHandlingStore().GetForDay(host.HostName, day));
        var recordHandling = fixture.Backend.RecordHandlingStore().Get(host.HostName, day);
        var owner = new IssueOwnerStore(fixture.Backend.Blob("issue_owners")).Get("Synthetic NetIQ disk baseline", 53053);
        return JsonSerializer.Serialize(new
        {
            Case = new { openCase.CaseId, openCase.IssueKey, openCase.Status, openCase.HandlerId },
            Handling = new { handling.Status, handling.ActorId, handling.ActorAccount, handling.Note, handling.CaseId },
            RecordHandling = recordHandling is null ? null : new
                { recordHandling.Status, recordHandling.HandlerId, recordHandling.Note },
            Owner = owner is null ? null : new { owner.SourceName, owner.EventId, owner.OwnerUserIds, owner.Note }
        });
    }

    private static string parentStatusForScenario(string scenario) => scenario switch
    {
        "partial-parent" => "partial",
        "failed-parent" => "failed",
        "running-parent" => "running",
        "unknown-parent" => "unknown",
        _ => "success"
    };

    private static string RequiredScenario(JsonElement mutations, string caseId)
    {
        if (!mutations.TryGetProperty("scenario", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"{caseId} does not declare a scenario mutation.");
        return value.GetString()!;
    }

    private static string NormalizeSilentScenario(string scenario) => scenario switch
    {
        "missing-netiq-parent" => "missing-parent",
        "partial-netiq-parent" => "partial-parent",
        "scope-excluded-device" => "scope-excluded",
        "unknown-sensor-without-availability-qualifier" => "unknown-sensor-no-availability-category",
        "timeline-ends-before-asof" => "timeline-before-asof",
        "all-unknown-inventory-no-up-availability" => "all-unknown-no-up-availability",
        "policy-revision-drift" => "source-authority-drift",
        _ => scenario
    };

    private static (PrtgSilentPresenceReadinessState State, string Reason) ExpectedSilentReadiness(string scenario) => scenario switch
    {
        "missing-snapshot" or "wrong-analysis-day" or "future-source-point" =>
            (PrtgSilentPresenceReadinessState.Waiting, "source-proof-unavailable"),
        "missing-source-asof" =>
            (PrtgSilentPresenceReadinessState.Waiting, "source-proof-unavailable"),
        "scope-excluded" =>
            (PrtgSilentPresenceReadinessState.Waiting, "source-authority-unverified"),
        "inventory-count-mismatch" or "partial-inventory" or "stale-source-response" or
            "failed-source-read" or "mapping-fingerprint-changed" or "source-generation-drift" or "source-authority-drift" or
            "wrong-host-map" =>
            (PrtgSilentPresenceReadinessState.Waiting, "source-inventory-time-or-mapping-proof-incomplete"),
        "unknown-sensor-no-availability-category" or "all-unknown-no-up-availability" =>
            (PrtgSilentPresenceReadinessState.QualifiedNoHit, "qualified-no-silent-hit"),
        "paused-device" or "all-sensors-paused" =>
            (PrtgSilentPresenceReadinessState.ExplicitlyExcluded, "device-or-all-sensors-paused"),
        "missing-timeline-coverage" or "timeline-before-asof" =>
            (PrtgSilentPresenceReadinessState.Waiting, "sensor-identity-or-timeline-not-current-at-source-asof"),
        "disabled-rule" => (PrtgSilentPresenceReadinessState.ExplicitlyExcluded, "silent-rule-disabled"),
        _ => throw new InvalidOperationException($"Silent scenario '{scenario}' has no expected readiness contract.")
    };
}
