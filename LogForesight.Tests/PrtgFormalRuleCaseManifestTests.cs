using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Fixed acceptance contract for the eight formal PRTG rule families.</summary>
[Collection("KnownIssueCatalogState")]
public sealed partial class PrtgFormalRuleCaseManifestTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<CaseManifest> Manifest = new(LoadManifest);

    [Fact]
    public void FixedManifestHasEightFamiliesAndTheRequiredCaseCounts()
    {
        var manifest = Manifest.Value;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("synthetic-only; no native PRTG compatibility, capacity, or operational outcome claim", manifest.SourceTruth);
        Assert.Equal(8, manifest.Families.Count);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in manifest.Families)
        {
            var registeredConsumer = PrtgRuleCatalog.FormalConsumerFor(family.RuleCode);
            Assert.True(registeredConsumer is not null,
                $"{family.Family} has no registered formal consumer for {family.RuleCode}.");
            Assert.True(family.FormalConsumer.Contains(registeredConsumer!, StringComparison.Ordinal),
                $"{family.Family} does not name its registered consumer {registeredConsumer}.");
            Assert.Equal(family.Family, family.PositiveCases[0].CaseId.Split('-')[1]);
            Assert.True(family.PositiveCases.Count >= manifest.CaseContract.MinimumPositivePerFamily,
                $"{family.Family} has fewer than the required positive cases.");
            Assert.True(family.NegativeAndBoundaryCases.Count >= manifest.CaseContract.MinimumNegativeOrBoundaryPerFamily,
                $"{family.Family} has fewer than the required negative/boundary cases.");

            foreach (var testCase in family.PositiveCases)
            {
                Assert.True(ids.Add(testCase.CaseId), $"Duplicate case id: {testCase.CaseId}");
                Assert.True(testCase.Mutations.ValueKind == JsonValueKind.Object &&
                    testCase.Mutations.EnumerateObject().Any(),
                    $"{testCase.CaseId} must contain at least one manifest mutation.");
                Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedFormalResult));
                Assert.Equal("synthetic fixture; never source-native qualification", testCase.FixtureAuthority);
            }
            foreach (var testCase in family.NegativeAndBoundaryCases)
            {
                Assert.True(ids.Add(testCase.CaseId), $"Duplicate case id: {testCase.CaseId}");
                Assert.False(string.IsNullOrWhiteSpace(testCase.Mutation));
                Assert.False(string.IsNullOrWhiteSpace(testCase.FailedQualification));
                Assert.True(testCase.Mutations.ValueKind == JsonValueKind.Object &&
                    testCase.Mutations.EnumerateObject().Any(),
                    $"{testCase.CaseId} must contain a concrete mutation dispatch object.");
                if (testCase.CaseId == "F53-DiskTrend-N08-duplicate-day")
                    Assert.Equal(new[] { "one canonical Daily trend finding", "no duplicate issue or case" },
                        testCase.ExpectedFormalSideEffects);
                else if (testCase.CaseId is "F53-Flapping-N01-four-of-five-round-trips" or
                         "F53-Flapping-N02-repeated-poll-checkpoints" or
                         "F53-Flapping-N04-missing-up-transition" or
                         "F53-Flapping-N05-unknown-gap" or
                         "F53-Flapping-N19-wrong-category-profile" or
                         "F53-Flapping-N20-disabled-rule")
                    Assert.Equal(new[] { "PRTG:down" }, testCase.ExpectedFormalSideEffects);
                else
                    Assert.Empty(testCase.ExpectedFormalSideEffects);
                Assert.True(testCase.PreserveNetiqAndHumanOwnedData);
                if (testCase.Mutations.ValueKind != JsonValueKind.Undefined)
                    Assert.True(testCase.Mutations.ValueKind == JsonValueKind.Object,
                        $"{testCase.CaseId} mutation data must be a JSON object.");
                if (testCase.OriginalPositiveMutation.ValueKind != JsonValueKind.Undefined)
                {
                    Assert.True(testCase.OriginalPositiveMutation.ValueKind == JsonValueKind.Object);
                    Assert.False(string.IsNullOrWhiteSpace(testCase.Reclassification));
                    if (IsHostDayStraddleCase(testCase.CaseId))
                        Assert.False(string.IsNullOrWhiteSpace(testCase.OriginalExpectedFormalResult));
                }
                if (testCase.OriginalMutation.ValueKind != JsonValueKind.Undefined)
                {
                    Assert.True(testCase.OriginalMutation.ValueKind == JsonValueKind.Object);
                    Assert.False(string.IsNullOrWhiteSpace(testCase.Reclassification));
                }
                if (testCase.OriginalFailedQualification is not null)
                    Assert.False(string.IsNullOrWhiteSpace(testCase.Reclassification));
            }
        }
        var expectedNegativeIds = manifest.Families.SelectMany(family => family.NegativeAndBoundaryCases)
            .Select(testCase => testCase.CaseId).ToHashSet(StringComparer.Ordinal);
        var dispatchedNegativeIds = StateFamilyDailyNegativeCases().Select(row => (string)row[1]!)
            .Concat(SilentFamilyDailyNegativeCases().Select(row => (string)row[0]!))
            .Concat(ResourceOneHourBelowThresholdCases().Select(row => (string)row[1]!))
            .Concat(OriginalHostDayStraddlePositiveCases().Select(row => (string)row[1]!))
            .Concat(DiskTrendNegativeScenarios().Select(row => (string)row[0]!))
            .Append("F53-Warning-P03-warning-cross-midnight-evidence")
            .Append("F53-Silent-P02-silent-all-availability-unknown-with-resource-present")
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedNegativeIds.SetEquals(dispatchedNegativeIds),
            $"Negative case dispatch mismatch. Missing: {string.Join(",", expectedNegativeIds.Except(dispatchedNegativeIds))}; " +
            $"unexpected: {string.Join(",", dispatchedNegativeIds.Except(expectedNegativeIds))}.");
    }

    public static IEnumerable<object[]> RemainingStateRulePositiveCases()
    {
        foreach (var family in Manifest.Value.Families.Where(item => item.Family is "Down" or "Warning" or "Flapping"))
        foreach (var testCase in family.PositiveCases.Skip(1))
            yield return [family.Family, testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(RemainingStateRulePositiveCases))]
    public async Task RemainingStateRulePositiveCaseReachesDailyFindingAndPersistedRecordDetail(
        string familyName, string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == familyName);
        var testCase = family.PositiveCases.Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        using var fixture = new PrtgFormalRuleCaseFixture();
        var code = familyName.ToLowerInvariant();
        var ruleId = familyName switch
        {
            "Down" => "builtin-prtg-down-availability",
            "Warning" => "builtin-prtg-warning",
            _ => "builtin-prtg-flapping"
        };
        var ruleCategory = familyName == "Down" ? PrtgSensorCategories.Availability : null;
        if (mutations.TryGetProperty("thresholdMinutes", out var minuteThreshold))
            fixture.SetPrtgThreshold(code, ruleCategory, minuteThreshold.GetInt32());
        if (mutations.TryGetProperty("threshold", out var flapThreshold))
            fixture.SetPrtgThreshold(code, ruleCategory, flapThreshold.GetInt32());

        IReadOnlyList<(DateTime At, string Status)> transitions;
        if (familyName == "Down")
        {
            var duration = mutations.TryGetProperty("durationMinutes", out var durationValue)
                ? durationValue.GetInt32() : mutations.GetProperty("secondDurationMinutes").GetInt32();
            var acknowledged = mutations.TryGetProperty("acknowledged", out var ack) && ack.GetBoolean();
            var downStatus = acknowledged ? "Down (Acknowledged)" : "Down";
            if (mutations.TryGetProperty("crossesMidnight", out var cross) && cross.GetBoolean())
                transitions = [(fixture.AnalysisDay.AddMinutes(-10), downStatus),
                    (fixture.AnalysisDay.AddMinutes(duration - 10), "Up")];
            else if (mutations.TryGetProperty("episodeCount", out var count) && count.GetInt32() > 1)
            {
                var firstDuration = mutations.GetProperty("secondDurationMinutes").GetInt32();
                var recoveryGap = mutations.GetProperty("recoveryGapMinutes").GetInt32();
                transitions = [(fixture.AnalysisDay.AddHours(1), "Down"),
                    (fixture.AnalysisDay.AddHours(1).AddMinutes(firstDuration), "Up"),
                    (fixture.AnalysisDay.AddHours(1).AddMinutes(firstDuration + recoveryGap), "Down"),
                    (fixture.AnalysisDay.AddHours(1).AddMinutes(firstDuration + recoveryGap + duration), "Up")];
            }
            else
                transitions = [(fixture.AnalysisDay.AddHours(1), downStatus),
                    (fixture.AnalysisDay.AddHours(1).AddMinutes(duration), "Up")];
        }
        else if (familyName == "Warning")
        {
            var warningSegments = new List<int>();
            var gaps = new List<int>();
            if (mutations.TryGetProperty("segments", out var segments))
            {
                warningSegments.AddRange(segments.EnumerateArray().Select(value => value.GetInt32()));
                if (warningSegments.Count > 1)
                    gaps.Add(mutations.TryGetProperty("upGapMinutes", out var gap) ? gap.GetInt32() : 5);
            }
            else if (mutations.TryGetProperty("episodes", out var episodes))
            {
                foreach (var episode in episodes.EnumerateArray())
                {
                    warningSegments.Add(episode.GetProperty("warningMinutes").GetInt32());
                    if (warningSegments.Count > 1) gaps.Add(60);
                }
            }
            else
                warningSegments.Add(mutations.GetProperty("warningMinutes").GetInt32());
            if (mutations.TryGetProperty("crossesMidnight", out var crosses) && crosses.GetBoolean())
                transitions = [(fixture.AnalysisDay.AddMinutes(-120), "Warning"),
                    (fixture.AnalysisDay.AddHours(2), "Up")];
            else
            {
                var items = new List<(DateTime At, string Status)>();
                var startsAtMinute = mutations.TryGetProperty("startsAtMinute", out var startMinute)
                    ? startMinute.GetInt32() : 60;
                var cursor = fixture.AnalysisDay.AddMinutes(startsAtMinute);
                for (var index = 0; index < warningSegments.Count; index++)
                {
                    items.Add((cursor, "Warning"));
                    cursor = cursor.AddMinutes(warningSegments[index]);
                    items.Add((cursor, "Up"));
                    if (index < gaps.Count) cursor = cursor.AddMinutes(gaps[index]);
                }
                transitions = items;
            }
        }
        else
        {
            var roundTrips = mutations.GetProperty("roundTrips").GetInt32();
            var items = new List<(DateTime At, string Status)>();
            var firstCycle = 0;
            if (mutations.TryGetProperty("crossesMidnight", out var crosses) && crosses.GetBoolean())
            {
                items.Add((fixture.AnalysisDay.AddHours(-1), "Down"));
                items.Add((fixture.AnalysisDay.AddMinutes(30), "Up"));
                firstCycle = 1;
            }
            else if (mutations.TryGetProperty("recoveryBeforeWindow", out var recovered) && recovered.GetBoolean())
            {
                items.Add((fixture.AnalysisDay.AddDays(-2), "Down"));
                items.Add((fixture.AnalysisDay.AddDays(-2).AddMinutes(30), "Up"));
            }
            for (var index = firstCycle; index < roundTrips; index++)
            {
                var start = fixture.AnalysisDay.AddHours(2 + index * 2);
                items.Add((start, "Down"));
                items.Add((start.AddHours(1), "Up"));
            }
            transitions = items;
        }

        var host = fixture.SeedCoveredStateCase($"{familyName}-positive-{caseId}", "Ping", "Ping", "Up",
            _ => transitions, PrtgSensorCategories.Availability);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, $"PRTG:{code}", caseId);
        Assert.Equal(ruleId, finding.RuleId);
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    public static IEnumerable<object[]> StateRuleNegativeScenarios()
    {
        foreach (var family in Manifest.Value.Families.Where(item => item.Family == "Warning"))
        foreach (var testCase in family.NegativeAndBoundaryCases.Where(item => item.CaseId == "F53-Warning-P03-warning-cross-midnight-evidence"))
            yield return [family.Family, testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(StateRuleNegativeScenarios))]
    public async Task StateRuleBelowThresholdNegativeCaseHasNoFindingAndPreservesHumanData(
        string familyName, string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == familyName);
        var testCase = family.NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        IReadOnlyList<(DateTime At, string Status)> transitions;
        var code = familyName.ToLowerInvariant();
        var expectedFailedQualification = familyName switch
        {
            "Down" => "threshold-not-met",
            "Warning" => "below-threshold-total",
            _ => "flap-count-below-threshold"
        };
        if (familyName == "Warning" && caseId.Contains("P03-", StringComparison.Ordinal))
            expectedFailedQualification = "target-day-warning-overlap-below-240";
        Assert.Equal(expectedFailedQualification, testCase.FailedQualification);
        using var fixture = new PrtgFormalRuleCaseFixture();
        if (familyName == "Down")
        {
            var duration = mutations.GetProperty("durationMinutes").GetInt32();
            transitions = [(fixture.AnalysisDay.AddHours(1), "Down"),
                (fixture.AnalysisDay.AddHours(1).AddMinutes(duration), "Up")];
        }
        else if (familyName == "Warning")
        {
            if (mutations.TryGetProperty("crossesMidnight", out var crosses) && crosses.GetBoolean())
                transitions = [(fixture.AnalysisDay.AddMinutes(-120), "Warning"),
                    (fixture.AnalysisDay.AddHours(2), "Up")];
            else
            {
                var duration = mutations.GetProperty("warningMinutes").GetInt32();
                transitions = [(fixture.AnalysisDay.AddHours(1), "Warning"),
                    (fixture.AnalysisDay.AddHours(1).AddMinutes(duration), "Up")];
            }
        }
        else
        {
            var roundTrips = mutations.GetProperty("roundTrips").GetInt32();
            transitions = Enumerable.Range(0, roundTrips).SelectMany(index => new[]
            {
                (At: fixture.AnalysisDay.AddHours(1 + index * 2), Status: "Down"),
                (At: fixture.AnalysisDay.AddHours(2 + index * 2), Status: "Up")
            }).ToArray();
        }
        var host = fixture.SeedCoveredStateCase($"{familyName}-negative-{caseId}", "Ping", "Ping", "Up",
            _ => transitions, PrtgSensorCategories.Availability);
        var beforeHumanState = fixture.SeedHumanOwnedBaseline(host);
        var beforeCaseIds = fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        Assert.DoesNotContain(run.Registry.For(host.HostId, fixture.AnalysisDay),
            finding => finding.Source == $"PRTG:{code}");
        Assert.Equal(beforeHumanState, fixture.CaptureHumanOwnedBaseline(host));
        Assert.Equal(beforeCaseIds, fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
    }

    public static IEnumerable<object[]> RemainingSilentPositiveCases()
    {
        foreach (var testCase in Manifest.Value.Families.Single(item => item.Family == "Silent").PositiveCases
                     .Where(item => item.CaseId != "F53-Silent-P01-silent-complete-up-availability-unknown-resource"))
            yield return [testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(RemainingSilentPositiveCases))]
    public async Task RemainingSilentPositiveCaseReachesDailyFindingAndPersistedRecordDetail(string caseId)
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "Silent").PositiveCases
            .Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        DateTimeOffset sourceAsOf;
        DateTimeOffset? deviceAsOf = null;
        DateTime? unknownSince = null;
        var availabilityCount = mutations.TryGetProperty("availabilityUpSensors", out var count)
            ? count.GetInt32() : 1;
        if (mutations.TryGetProperty("sourcePoint", out _))
        {
            sourceAsOf = DateTimeOffset.UtcNow.AddSeconds(-30);
        }
        else if (mutations.TryGetProperty("priorUnknownDurationHours", out var priorDuration) ||
            mutations.TryGetProperty("unknownDurationHours", out priorDuration))
        {
            sourceAsOf = DateTimeOffset.UtcNow.AddMinutes(-mutations.GetProperty("sourceAgeMinutes").GetDouble());
            var localAsOf = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).DateTime;
            unknownSince = DateTime.SpecifyKind(localAsOf.AddHours(-priorDuration.GetDouble()), DateTimeKind.Unspecified);
        }
        else
        {
            sourceAsOf = DateTimeOffset.UtcNow.AddSeconds(mutations.GetProperty("sourceAsOfOffsetSeconds").GetInt32());
            deviceAsOf = sourceAsOf.AddSeconds(-mutations.GetProperty("sourceAsOfOffsetSeconds").GetInt32());
        }

        using var fixture = new PrtgFormalRuleCaseFixture();
        var localDay = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date;
        fixture.UseAnalysisDay(localDay);
        var host = fixture.SeedSilentCase(availabilitySensorCount: availabilityCount, unknownSince: unknownSince);
        if (mutations.TryGetProperty("sourcePoint", out _))
        {
            var boundaryZone = TimeZoneInfo.GetSystemTimeZones()
                .Where(zone => TimeZoneInfo.ConvertTime(sourceAsOf, zone).Date != localDay)
                .OrderBy(zone => zone.Id, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException("Could not choose a source timezone that crosses the host-local day boundary.");
            new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
                .Update(policy => policy.SourceTimeZoneId = boundaryZone.Id);
        }
        var captured = await fixture.CaptureSyntheticSilentSourceAsync(host, sourceAsOf, deviceAsOf,
            availabilitySensorCount: availabilityCount);
        Assert.Equal(PrtgPresenceReadQuality.Complete, captured.ReadQuality);
        Assert.Equal(1 + availabilityCount, captured.ReportedSensorCount);
        if (mutations.TryGetProperty("sourcePoint", out _))
        {
            Assert.NotEqual(localDay, captured.SourceDay);
            Assert.NotNull(captured.SourceAsOf);
            Assert.InRange(Math.Abs((captured.SourceAsOf!.Value - sourceAsOf).TotalSeconds), 0, 120);
        }
        var run = await fixture.RunDailyAsync(host, localDay);
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var configuredUrl = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().PrtgUrl;
        var readiness = new PrtgSilentPresenceFormalConsumer(fixture.Backend).EvaluateWithReadiness(localDay,
            policy, configuredUrl, new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId },
            KnownIssueCatalog.Rules).Devices[PrtgFormalRuleCaseFixture.DeviceId];
        var finding = RequireDailyFinding(run, host, localDay, "PRTG:silent", caseId,
            $"Formal readiness: {readiness.State}/{readiness.Reason}");
        Assert.Equal(PrtgSilentPresenceReadinessState.QualifiedHit, readiness.State);
        Assert.Equal("builtin-prtg-silent", finding.RuleId);
        Assert.Contains(finding.SampleMessages, message => message.Contains("不代表主機故障", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(localDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.PrtgPresenceSourceAsOf == finding.PrtgPresenceSourceAsOf &&
            issue.PrtgPresenceInventoryFingerprint == finding.PrtgPresenceInventoryFingerprint);
    }

    [Fact]
    public async Task SilentAllAvailabilityUnknownCaseUsesItsOriginalMutationAndProducesNoFinding()
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "Silent").NegativeAndBoundaryCases
            .Single(item => item.CaseId == "F53-Silent-P02-silent-all-availability-unknown-with-resource-present");
        Assert.Equal("qualified-no-silent-hit", testCase.FailedQualification);
        var originalMutation = testCase.OriginalPositiveMutation;
        var availabilityStatuses = originalMutation.GetProperty("availability").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        Assert.NotEmpty(availabilityStatuses);
        Assert.All(availabilityStatuses, status => Assert.Equal(availabilityStatuses[0], status));
        var resourceStatus = originalMutation.GetProperty("resourceStatus").GetString()!;
        Assert.Equal("Unknown", availabilityStatuses[0]);
        Assert.Equal("Up", resourceStatus);
        using var fixture = new PrtgFormalRuleCaseFixture();
        var sourceAsOf = DateTimeOffset.UtcNow;
        var localDay = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date;
        fixture.UseAnalysisDay(localDay);
        var host = fixture.SeedSilentCase(resourceStatus, availabilityStatuses[0], availabilityStatuses.Length);
        var beforeHumanState = fixture.SeedHumanOwnedBaseline(host);
        var beforeOpenCaseIds = fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var beforeActiveWorkOrderIds = fixture.Backend.WorkOrderStore().GetAllActive()
            .Select(item => item.WorkOrderId).OrderBy(item => item).ToArray();
        var captured = await fixture.CaptureSyntheticSilentSourceAsync(host, sourceAsOf,
            resourceStatus: resourceStatus, availabilityStatus: availabilityStatuses[0],
            availabilitySensorCount: availabilityStatuses.Length);
        Assert.Equal(PrtgPresenceReadQuality.Complete, captured.ReadQuality);
        Assert.Equal(1 + availabilityStatuses.Length, captured.ReportedSensorCount);
        var run = await fixture.RunDailyAsync(host, localDay);
        Assert.Empty(run.Registry.For(host.HostId, localDay).Where(item => item.Source == "PRTG:silent"));
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var configuredUrl = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().PrtgUrl;
        var readiness = new PrtgSilentPresenceFormalConsumer(fixture.Backend).EvaluateWithReadiness(localDay,
            policy, configuredUrl, new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId },
            KnownIssueCatalog.Rules).Devices[PrtgFormalRuleCaseFixture.DeviceId];
        Assert.Equal(PrtgSilentPresenceReadinessState.QualifiedNoHit, readiness.State);
        Assert.Equal("qualified-no-silent-hit", readiness.Reason);
        Assert.Equal(beforeHumanState, fixture.CaptureHumanOwnedBaseline(host));
        Assert.Equal(beforeOpenCaseIds, fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(beforeActiveWorkOrderIds, fixture.Backend.WorkOrderStore().GetAllActive()
            .Select(item => item.WorkOrderId).OrderBy(item => item).ToArray());
    }

    [Fact]
    public async Task FlappingAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "Flapping");
        var testCase = family.PositiveCases.Single(item => item.CaseId == "F53-Flapping-P01-flapping-exact-five-round-trips");
        var roundTrips = testCase.Mutations.GetProperty("roundTrips").GetInt32();

        using var fixture = new PrtgFormalRuleCaseFixture();
        if (testCase.Mutations.TryGetProperty("ruleCategoryFilter", out var flapCategoryFilter))
            SetStateRuleCategoryFilter(fixture, "Flapping", flapCategoryFilter.GetString()!);
        var host = fixture.SeedCoveredStateCase("flapping-formal-case", "Ping", "Ping", "Up", day =>
            Enumerable.Range(0, roundTrips).SelectMany(index => new[]
            {
                (At: day.AddHours(1 + index * 2), Status: "Down"),
                (At: day.AddHours(2 + index * 2), Status: "Up")
            }).ToArray(), PrtgSensorCategories.Availability);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, "PRTG:flapping", testCase.CaseId);
        Assert.Equal("builtin-prtg-flapping", finding.RuleId);
        var findingDetail = Assert.Single(finding.SampleMessages);
        Assert.Contains($"區間 {roundTrips} 次", findingDetail);
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == "PRTG:flapping" && issue.SampleMessages.Contains(findingDetail));
    }

    [Fact]
    public async Task DownAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "Down").PositiveCases
            .Single(item => item.CaseId == "F53-Down-P01-down-exact-30-minute-availability-threshold");
        var expectedThreshold = testCase.Mutations.GetProperty("thresholdMinutes").GetInt32();
        var durationMinutes = testCase.Mutations.GetProperty("durationMinutes").GetInt32();
        using var fixture = new PrtgFormalRuleCaseFixture();
        if (testCase.Mutations.TryGetProperty("ruleCategoryFilter", out var downCategoryFilter))
            SetStateRuleCategoryFilter(fixture, "Down", downCategoryFilter.GetString()!);
        var host = fixture.SeedCoveredStateCase("down-formal-case", "Ping", "Ping", "Up", day =>
            [(day.AddHours(1), "Down"), (day.AddMinutes(60 + durationMinutes), "Up")],
            PrtgSensorCategories.Availability);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, "PRTG:down", testCase.CaseId);
        Assert.Equal("builtin-prtg-down-availability", finding.RuleId);
        Assert.Equal(expectedThreshold, durationMinutes);
        Assert.Contains(finding.SampleMessages,
            message => message.Contains($"連續可信 {durationMinutes} 分鐘", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    [Fact]
    public async Task WarningAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "Warning").PositiveCases
            .Single(item => item.CaseId == "F53-Warning-P01-warning-exact-240-minute-total");
        var warningMinutes = testCase.Mutations.GetProperty("warningMinutes").GetInt32();
        using var fixture = new PrtgFormalRuleCaseFixture();
        if (testCase.Mutations.TryGetProperty("ruleCategoryFilter", out var warningCategoryFilter))
            SetStateRuleCategoryFilter(fixture, "Warning", warningCategoryFilter.GetString()!);
        var host = fixture.SeedCoveredStateCase("warning-formal-case", "Ping", "Ping", "Up", day =>
            [(day.AddHours(1), "Warning"), (day.AddHours(1).AddMinutes(warningMinutes), "Up")],
            PrtgSensorCategories.Availability);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, "PRTG:warning", testCase.CaseId);
        Assert.Equal("builtin-prtg-warning", finding.RuleId);
        Assert.Contains(finding.SampleMessages, message => message.Contains("區間 240 分鐘", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    [Fact]
    public async Task MemoryFormalAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "MemoryFormal");
        var testCase = family.PositiveCases.Single(item => item.CaseId == "F53-MemoryFormal-P01-memory-formal-exact-90-percent-used-two-hours");
        var values = testCase.Mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();

        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Memory, values);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay,
            "PRTG:resource_memory_sustained_pressure", testCase.CaseId);
        Assert.Equal("builtin-prtg-resource-memory-pressure", finding.RuleId);
        Assert.Contains(finding.SampleMessages, message => message.Contains("90", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    [Fact]
    public async Task CpuFormalAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "CPUFormal").PositiveCases
            .Single(item => item.CaseId == "F53-CPUFormal-P01-cpu-formal-exact-90-percent-two-hours");
        var values = testCase.Mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, values);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay,
            "PRTG:resource_cpu_sustained_pressure", testCase.CaseId);
        Assert.Equal("builtin-prtg-resource-cpu-pressure", finding.RuleId);
        Assert.Contains(finding.SampleMessages, message => message.Contains("90", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    [Fact]
    public async Task DiskTwoHourLowWaterAcceptanceCaseReachesDailyRiskAndPersistedRecordDetail()
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "DiskTwoHourLowWater");
        var testCase = family.PositiveCases.Single(item => item.CaseId == "F53-DiskTwoHourLowWater-P01-disk-low-water-exact-ten-percent");
        var values = testCase.Mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();

        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, values);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, "PRTG:disk_free_trend", testCase.CaseId);
        Assert.Equal("builtin-prtg-resource-disk-pressure", finding.RuleId);
        Assert.Contains("disk-two-hour-low-water", finding.PrtgResourceReasonCodes ?? []);
        Assert.Equal(RiskLevels.Medium, PrtgFindingMapper.RiskFromFindings([finding]));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Equal(RiskLevels.Medium, record.RiskLevel);
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.PrtgResourceReasonCodes?.Contains("disk-two-hour-low-water", StringComparer.Ordinal) == true &&
            issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    public static IEnumerable<object[]> RemainingResourcePositiveCases()
    {
        foreach (var manifestFamily in new[] { "CPUFormal", "MemoryFormal", "DiskTwoHourLowWater" })
        foreach (var testCase in Manifest.Value.Families.Single(item => item.Family == manifestFamily)
                     .PositiveCases.Where(item => !item.CaseId.EndsWith("-P01-cpu-formal-exact-90-percent-two-hours", StringComparison.Ordinal) &&
                         !item.CaseId.EndsWith("-P01-memory-formal-exact-90-percent-used-two-hours", StringComparison.Ordinal) &&
                         !item.CaseId.EndsWith("-P01-disk-low-water-exact-ten-percent", StringComparison.Ordinal)))
            yield return [manifestFamily, testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(RemainingResourcePositiveCases))]
    public async Task RemainingResourcePositiveCasesReachDailyRiskAndPersistedRecordDetail(string manifestFamily, string caseId)
    {
        var definition = manifestFamily switch
        {
            "CPUFormal" => (Family: PrtgResourceFamily.Cpu, Source: "PRTG:resource_cpu_sustained_pressure", RuleId: "builtin-prtg-resource-cpu-pressure"),
            "MemoryFormal" => (Family: PrtgResourceFamily.Memory, Source: "PRTG:resource_memory_sustained_pressure", RuleId: "builtin-prtg-resource-memory-pressure"),
            "DiskTwoHourLowWater" => (Family: PrtgResourceFamily.Disk, Source: "PRTG:disk_free_trend", RuleId: "builtin-prtg-resource-disk-pressure"),
            _ => throw new ArgumentOutOfRangeException(nameof(manifestFamily))
        };
        var testCase = Manifest.Value.Families.Single(item => item.Family == manifestFamily).PositiveCases
            .Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        var values = mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var goodSlots = mutations.TryGetProperty("goodPhysicalSlotsPerHour", out var slots) ? slots.GetInt32() : 4;
        var crossesAnalysisMidnight = mutations.TryGetProperty("crossesAnalysisMidnight", out var cross) && cross.GetBoolean();
        var memoryRemaining = definition.Family == PrtgResourceFamily.Memory &&
            mutations.TryGetProperty("semantic", out var semantic) && semantic.GetString() == "memory-remaining-percent";
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(definition.Family, values, goodSlots,
            crossesAnalysisMidnight: crossesAnalysisMidnight, memoryValuesAreRemainingPercent: memoryRemaining);
        if (crossesAnalysisMidnight)
        {
            var assessment = fixture.AssessResourcePeriod(definition.Family);
            Assert.NotNull(assessment);
            Assert.Equal(PrtgResourceDecisionKind.Hit, assessment!.Decision.Kind);
            Assert.Equal(new[] { fixture.AnalysisDay.Date.AddHours(23), fixture.AnalysisDay.Date.AddDays(1) },
                assessment.Decision.Window.Select(hour => hour.WallPeriodStart).ToArray());
            Assert.Equal(fixture.AnalysisDay.Date, assessment.SingleWindowHostDay);
            var analysisTimeZoneId = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().AnalysisTimeZoneId;
            var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(analysisTimeZoneId);
            var hostHours = assessment.Decision.Window.Select(hour => TimeZoneInfo.ConvertTimeFromUtc(
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(hour.WallPeriodStart, DateTimeKind.Unspecified), analysisZone),
                TimeZoneInfo.Local)).ToArray();
            Assert.Equal(new[] { fixture.AnalysisDay.Date.AddHours(22), fixture.AnalysisDay.Date.AddHours(23) }, hostHours);
        }
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, definition.Source, testCase.CaseId);
        Assert.Equal(definition.RuleId, finding.RuleId);
        if (definition.Family == PrtgResourceFamily.Disk)
            Assert.Contains("disk-two-hour-low-water", finding.PrtgResourceReasonCodes ?? []);
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }
    public static IEnumerable<object[]> OriginalHostDayStraddlePositiveCases()
    {
        foreach (var family in Manifest.Value.Families.Where(item => item.Family is
                     "CPUFormal" or "MemoryFormal" or "DiskTwoHourLowWater"))
        foreach (var testCase in family.NegativeAndBoundaryCases.Where(item => IsHostDayStraddleCase(item.CaseId)))
            yield return [family.Family, testCase.CaseId];
    }

    // The frozen fixture keeps its historical NoFormal reclassification and unchanged raw SHA.
    // The current product decision explicitly supersedes that old expectation: these original
    // positive mutations must now produce Formal evidence on the later hour's host day.
    [Theory]
    [MemberData(nameof(OriginalHostDayStraddlePositiveCases))]
    public async Task OriginalHostDayStraddleCaseProducesFindingOnLaterHourHostDay(string manifestFamily, string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == manifestFamily);
        var testCase = family.NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        Assert.True(IsHostDayStraddleCase(caseId));
        Assert.Equal("synthetic fixture; never source-native qualification", testCase.FixtureAuthority);
        Assert.False(string.IsNullOrWhiteSpace(testCase.OriginalExpectedFormalResult));
        Assert.Empty(testCase.ExpectedFormalSideEffects);
        Assert.True(testCase.OriginalPositiveMutation.ValueKind == JsonValueKind.Object);

        var definition = manifestFamily switch
        {
            "CPUFormal" => (Family: PrtgResourceFamily.Cpu, Source: "PRTG:resource_cpu_sustained_pressure", RuleId: "builtin-prtg-resource-cpu-pressure"),
            "MemoryFormal" => (Family: PrtgResourceFamily.Memory, Source: "PRTG:resource_memory_sustained_pressure", RuleId: "builtin-prtg-resource-memory-pressure"),
            _ => (Family: PrtgResourceFamily.Disk, Source: "PRTG:disk_free_trend", RuleId: "builtin-prtg-resource-disk-pressure")
        };
        var mutation = testCase.OriginalPositiveMutation;
        var values = mutation.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var memoryRemaining = mutation.TryGetProperty("semantic", out var semantic) &&
            semantic.GetString() == "memory-remaining-percent";
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.UseAnalysisDay(DateTime.Today.AddDays(-2));
        new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.ValidFrom = new DateTimeOffset(fixture.AnalysisDay.AddDays(-32)));
        var host = fixture.SeedFormalResourceCase(definition.Family, values,
            crossesHostMidnight: true, memoryValuesAreRemainingPercent: memoryRemaining);
        var laterHourHostDay = fixture.AnalysisDay.AddDays(1);
        var hostStore = fixture.Backend.PrtgStore();
        hostStore.ReplaceHostMapForDate(laterHourHostDay, [new PrtgHostMapRow
        {
            DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId, MapDate = laterHourHostDay,
            HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok,
            CreatedAt = DateTime.UtcNow
        }]);
        var records = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        var earlierParent = Assert.Single(records.ReadRecent(fixture.AnalysisDay, 1));
        records.Append(new DailyAnalysisRecord
        {
            LogSource = earlierParent.LogSource,
            LatestNetiqAttemptStatus = earlierParent.LatestNetiqAttemptStatus,
            Date = laterHourHostDay,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = earlierParent.RiskLevel,
            RiskBasis = earlierParent.RiskBasis,
            TopIssues = earlierParent.TopIssues.ToList()
        });
        var humanBefore = fixture.SeedHumanOwnedBaseline(host);

        var cutoffForBothCompletedHours = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(
            fixture.AnalysisDay.Date.AddDays(1).AddHours(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var preview = fixture.AssessResourcePeriod(definition.Family, cutoffForBothCompletedHours);
        Assert.NotNull(preview);
        Assert.Equal(PrtgResourceDecisionKind.Hit, preview!.Decision.Kind);
        Assert.Equal(laterHourHostDay.Date, preview.SingleWindowHostDay?.Date);

        var earlierDaily = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        Assert.DoesNotContain(earlierDaily.Registry.For(host.HostId, fixture.AnalysisDay),
            finding => finding.Source == definition.Source);
        var laterDaily = await fixture.RunDailyAsync(host, laterHourHostDay);
        var finding = RequireDailyFinding(laterDaily, host, laterHourHostDay, definition.Source, caseId);
        Assert.Equal(definition.RuleId, finding.RuleId);
        if (definition.Family == PrtgResourceFamily.Disk)
            Assert.Contains("disk-two-hour-low-water", finding.PrtgResourceReasonCodes ?? []);
        var laterParent = Assert.Single(records.ReadRecent(laterHourHostDay, 1));
        Assert.Contains(laterParent.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
        if (definition.Family == PrtgResourceFamily.Disk)
            Assert.Contains(laterParent.TopIssues, issue => issue.EventKey == finding.EventKey &&
                issue.PrtgResourceReasonCodes?.Contains("disk-two-hour-low-water", StringComparer.Ordinal) == true);
        Assert.Equal(humanBefore, fixture.CaptureHumanOwnedBaseline(host));

        var repeatedDaily = await fixture.RunDailyAsync(host, laterHourHostDay);
        var repeatedFinding = RequireDailyFinding(repeatedDaily, host, laterHourHostDay, definition.Source, caseId + " rerun");
        Assert.Equal(finding.EventKey, repeatedFinding.EventKey);
        Assert.Single(records.ReadRecent(laterHourHostDay, 1).Single().TopIssues.Where(issue =>
            issue.EventKey == finding.EventKey));
    }
    [Fact]
    public async Task CrossHostDayFormalFindingIsNotAttachedWithoutLaterDayParent()
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "DiskTwoHourLowWater");
        var testCase = family.NegativeAndBoundaryCases.Single(item =>
            item.CaseId == "F53-DiskTwoHourLowWater-P04-disk-low-water-crosses-host-midnight");
        var values = testCase.OriginalPositiveMutation.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.UseAnalysisDay(DateTime.Today.AddDays(-2));
        new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.ValidFrom = new DateTimeOffset(fixture.AnalysisDay.AddDays(-32)));
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, values, crossesHostMidnight: true);
        var laterHourHostDay = fixture.AnalysisDay.AddDays(1);
        fixture.Backend.PrtgStore().ReplaceHostMapForDate(laterHourHostDay, [new PrtgHostMapRow
        {
            DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId, MapDate = laterHourHostDay,
            HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok,
            CreatedAt = DateTime.UtcNow
        }]);

        var run = await fixture.RunDailyAsync(host, laterHourHostDay);

        Assert.Empty(run.Registry.For(host.HostId, laterHourHostDay).Where(item =>
            item.Source == "PRTG:disk_free_trend"));
        Assert.Empty(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(laterHourHostDay, 1));
        Assert.Empty(fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName));
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
    }
    public static IEnumerable<object[]> ResourceOneHourBelowThresholdCases()
    {
        foreach (var family in Manifest.Value.Families.Where(item => item.Family is
                     "CPUFormal" or "MemoryFormal" or "DiskTwoHourLowWater"))
        foreach (var testCase in family.NegativeAndBoundaryCases.Where(item => !IsHostDayStraddleCase(item.CaseId)))
            yield return [family.Family, testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(ResourceOneHourBelowThresholdCases))]
    public async Task ResourceNegativeQualificationHasNoFormalDailyFindingAndPreservesHumanData(
        string manifestFamily, string caseId)
    {
        var family = Manifest.Value.Families.Single(item => item.Family == manifestFamily);
        var testCase = family.NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        Assert.Equal("synthetic fixture; never source-native qualification", testCase.FixtureAuthority);

        var values = testCase.Mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var ruleEnabled = !testCase.Mutations.TryGetProperty("ruleEnabled", out var enabled) || enabled.GetBoolean();
        var periodMutation = testCase.Mutations.TryGetProperty("periodMutation", out var period)
            ? period.GetString() : null;
        var profileMutation = testCase.Mutations.TryGetProperty("profileMutation", out var profileCase)
            ? profileCase.GetString() : null;
        var scenario = testCase.Mutations.GetProperty("scenario").GetString()!;
        var evidenceMutation = scenario switch
        {
            "duplicate-physical-slot" or "summary-proof-disagrees" or "unproved-mean" or
                "future-samples" or "future-sample" or "insufficient-slot-window" => scenario,
            _ => null
        };
        var authorityMutation = scenario switch
        {
            "channel-generation-drift" or "source-generation-drift" or "resource-epoch-drift" or
                "wrong-host-scope" or "paused-sensor" or "formal-source-policy-unready" or
                "unverified-time-basis" or "missing-maintain-permission" or "trial-profile-drift" or
                "resource-generation-drift" or "live-source-generation-mismatch" or
                "live-resource-generation-mismatch" or "live-channel-generation-mismatch" or
                "live-epoch-mismatch" => scenario,
            _ => null
        };
        var resourceFamily = resourceFamilyFor(manifestFamily);
        Assert.Equal(caseId.Contains("-N01-", StringComparison.Ordinal) ? 1 : 2, values.Length);
        using (var baselineFixture = new PrtgFormalRuleCaseFixture())
        {
            var baselineValues = resourceFamily == PrtgResourceFamily.Disk ? new[] { 5d, 5d } : new[] { 95d, 95d };
            var baselineHost = baselineFixture.SeedFormalResourceCase(resourceFamily, baselineValues,
                memoryValuesAreRemainingPercent: false);
            var baselineRun = await baselineFixture.RunDailyAsync(baselineHost, baselineFixture.AnalysisDay);
            var baselineSource = resourceFamily switch
            {
                PrtgResourceFamily.Cpu => "PRTG:resource_cpu_sustained_pressure",
                PrtgResourceFamily.Memory => "PRTG:resource_memory_sustained_pressure",
                _ => "PRTG:disk_free_trend"
            };
            _ = RequireDailyFinding(baselineRun, baselineHost, baselineFixture.AnalysisDay,
                baselineSource, caseId + " positive baseline");
        }
        using var fixture = new PrtgFormalRuleCaseFixture();
        var goodSlots = testCase.Mutations.TryGetProperty("goodPhysicalSlotsPerHour", out var slotCount)
            ? slotCount.GetInt32() : 4;
        var memoryRemaining = testCase.Mutations.TryGetProperty("semantic", out var semantic) &&
            semantic.GetString() == "memory-remaining-percent";

        var host = fixture.SeedFormalResourceCase(resourceFamily, values, goodSlots,
            crossesHostMidnight: false,
            memoryValuesAreRemainingPercent: memoryRemaining, formalRuleEnabled: ruleEnabled,
            periodMutation: periodMutation, profileMutation: profileMutation,
            evidenceMutation: evidenceMutation, authorityMutation: authorityMutation);

        var invalidProfile = profileMutation is "wrong-unit" or "wrong-scale";
        PrtgResourcePeriodAssessment? preview = null;
        if (invalidProfile)
        {
            Assert.Throws<InvalidDataException>(() => fixture.AssessResourcePeriod(resourceFamily));
        }
        else if (ruleEnabled)
        {
            preview = fixture.AssessResourcePeriod(resourceFamily);
            if (authorityMutation is "paused-sensor" or "wrong-host-scope")
            {
                Assert.Null(preview);
                var metadata = Assert.Single(fixture.Backend.PrtgStore()
                    .GetResourcePressureSensorMetadata([PrtgFormalRuleCaseFixture.SensorId]));
                if (authorityMutation == "paused-sensor") Assert.True(metadata.Paused);
                var selected = new PrtgResourcePeriodConsumer(fixture.Backend,
                    new SystemSettingsStore(fixture.Backend.Blob("system_settings")))
                    .EvaluateBatch([PrtgFormalRuleCaseFixture.SensorId],
                        TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(fixture.AnalysisDay.AddDays(1), DateTimeKind.Unspecified),
                            TimeZoneInfo.Local), DateTime.UtcNow);
                Assert.DoesNotContain(PrtgFormalRuleCaseFixture.SensorId, selected.SelectedSensorObjids);
                Assert.DoesNotContain(selected.Assessments, item =>
                    item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId);
            }
            else
                Assert.NotNull(preview);
            if (authorityMutation is "paused-sensor" or "wrong-host-scope")
            {
                // The consumer excludes these sensors before creating an assessment.
            }
            else if (authorityMutation == "missing-maintain-permission")
            {
                Assert.Equal(PrtgResourceDecisionMode.Hint, preview!.Decision.Mode);
                Assert.Equal(PrtgResourceDecisionKind.Hit, preview.Decision.Kind);
                Assert.Equal("both-completed-hour-averages-at-or-above-90", preview.Decision.ReasonCode);
            }
            else if (authorityMutation == "trial-profile-drift")
            {
                Assert.Equal(PrtgResourceDecisionMode.Hint, preview!.Decision.Mode);
                Assert.Equal(PrtgResourceDecisionKind.Hit, preview.Decision.Kind);
                Assert.Equal("both-completed-hour-averages-at-or-above-90", preview.Decision.ReasonCode);
            }
            else
                Assert.Equal(testCase.FailedQualification, preview!.Decision.ReasonCode);
        }
        else
        {
            preview = fixture.AssessResourcePeriod(resourceFamily);
            Assert.Null(preview);
            Assert.Equal("formal-rule-disabled", testCase.FailedQualification);
        }
        var pressureHintStore = new PrtgResourcePressureHintStore(fixture.Backend.Blob(
            PrtgResourcePressureHintStore.BlobKey(host.HostId)));
        var preDailyHint = pressureHintStore.GetCurrent(host.HostId, DateTime.UtcNow)
            .SingleOrDefault(item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId && item.Family == resourceFamily);
        if (preview != null)
        {
            Assert.NotNull(preDailyHint);
            AssertHintMatchesPreview(preview, preDailyHint!);
        }
        var beforeHumanState = fixture.SeedHumanOwnedBaseline(host);
        var beforeCaseIds = fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var dailyStartedUtc = DateTime.UtcNow;
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay, captureWorkflowReceipt: invalidProfile);
        var dailyCompletedUtc = DateTime.UtcNow;
        var source = resourceFamily switch
        {
            PrtgResourceFamily.Cpu => "PRTG:resource_cpu_sustained_pressure",
            PrtgResourceFamily.Memory => "PRTG:resource_memory_sustained_pressure",
            _ => "PRTG:disk_free_trend"
        };
        Assert.True(run.Registry.IsPublished(fixture.AnalysisDay));
        var dailyFindings = run.Registry.For(host.HostId, fixture.AnalysisDay);
        var pressureFindings = dailyFindings.Where(finding => finding.Source == source).ToArray();
        Assert.Empty(pressureFindings);
        if (invalidProfile)
        {
            Assert.Empty(pressureFindings);
            Assert.Null(preDailyHint);
            Assert.Null(run.Registry.ManifestFor(host.HostId, fixture.AnalysisDay));
            var receipt = new HostDayWorkflowStore(fixture.Backend).Get(host.HostId, fixture.AnalysisDay);
            Assert.NotNull(receipt);
            Assert.Equal(WorkflowLegState.Waiting, receipt!.Prtg);
            Assert.Equal(testCase.FailedQualification, receipt.PrtgFailure);
            Assert.False(receipt.PrtgReadinessComplete);
            Assert.Contains(run.ConsoleLines, line => line.Contains("資源期間評估未完成", StringComparison.Ordinal));
            AssertSameHint(preDailyHint, pressureHintStore.GetCurrent(host.HostId, DateTime.UtcNow)
                .SingleOrDefault(item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId && item.Family == resourceFamily));
        }
        else
        {
            if (preview is null)
            {
                Assert.Null(preDailyHint);
                AssertSameHint(preDailyHint, pressureHintStore.GetCurrent(host.HostId, DateTime.UtcNow)
                    .SingleOrDefault(item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId && item.Family == resourceFamily));
            }
            else
            {
                // RunDaily's live pass is intentionally current-window evidence; the earlier
                // preview below established only the historical day-close expectation.
                var currentHint = Assert.Single(pressureHintStore.GetCurrent(host.HostId, DateTime.UtcNow)
                    .Where(item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId && item.Family == resourceFamily));
                var dailyHint = currentHint with { MissingFacts = currentHint.MissingFacts?.ToArray() };
                Assert.NotNull(dailyHint.EvidenceAsOfUtc);

                var monitoringPolicy = new PrtgMonitoringPolicyStore(
                    fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(monitoringPolicy.AnalysisTimeZoneId);
                var earliestCurrentCutoff = CurrentWallHourStartUtc(dailyStartedUtc, analysisZone);
                var latestCurrentCutoff = CurrentWallHourStartUtc(dailyCompletedUtc, analysisZone);
                if (authorityMutation == "formal-source-policy-unready" &&
                    dailyHint.AsOfUtc == preDailyHint!.AsOfUtc)
                {
                    // The daily path cannot enter this sensor's resource evaluation when
                    // source authority is unavailable. It preserves the earlier hint.
                    AssertSameHint(preDailyHint, dailyHint);
                    AssertHintMatchesPreview(preview, dailyHint);
                }
                else
                {
                    Assert.InRange(dailyHint.AsOfUtc, preDailyHint!.AsOfUtc, dailyCompletedUtc);
                    if (dailyHint.AsOfUtc < dailyStartedUtc)
                    {
                        // Within the first hour after host midnight, the historical preview
                        // and the live pass can share the same evidence cutoff. An unchanged
                        // projection is deliberately not rewritten just to renew authority time.
                        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(preDailyHint),
                            System.Text.Json.JsonSerializer.Serialize(dailyHint));
                    }
                    Assert.Equal(CurrentWallHourStartUtc(dailyHint.AsOfUtc, analysisZone), dailyHint.EvidenceAsOfUtc.Value);
                    Assert.InRange(dailyHint.EvidenceAsOfUtc.Value, earliestCurrentCutoff, latestCurrentCutoff);

                    // AssessResourcePeriod persists a hint, so compare against the cloned daily hint.
                    var currentAssessment = fixture.AssessResourcePeriod(resourceFamily, dailyHint.EvidenceAsOfUtc.Value);
                    Assert.NotNull(currentAssessment);
                    AssertHintMatchesPreview(currentAssessment!, dailyHint);
                }
            }
        }
        Assert.Equal(beforeHumanState, fixture.CaptureHumanOwnedBaseline(host));
        Assert.Equal(beforeCaseIds, fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName)
            .Select(item => item.CaseId).OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());

        static PrtgResourceFamily resourceFamilyFor(string name) => name switch
        {
            "CPUFormal" => PrtgResourceFamily.Cpu,
            "MemoryFormal" => PrtgResourceFamily.Memory,
            _ => PrtgResourceFamily.Disk
        };

        static void AssertSameHint(PrtgResourcePressureHint? expected, PrtgResourcePressureHint? actual)
        {
            Assert.Equal(expected?.Kind, actual?.Kind);
            Assert.Equal(expected?.ReasonCode, actual?.ReasonCode);
            Assert.Equal(expected?.ProfileFingerprint, actual?.ProfileFingerprint);
            Assert.Equal(expected?.EarlierHourAveragePercent, actual?.EarlierHourAveragePercent);
            Assert.Equal(expected?.LatestHourAveragePercent, actual?.LatestHourAveragePercent);
            Assert.Equal(expected?.WindowCoveragePercent, actual?.WindowCoveragePercent);
            Assert.Equal(expected?.EvidenceAsOfUtc, actual?.EvidenceAsOfUtc);
            Assert.Equal(expected?.EpisodeStartedAtUtc, actual?.EpisodeStartedAtUtc);
        }

        static void AssertHintMatchesPreview(PrtgResourcePeriodAssessment assessment,
            PrtgResourcePressureHint hint)
        {
            Assert.Equal(assessment.Decision.Kind, hint.Kind);
            Assert.Equal(assessment.Decision.ReasonCode, hint.ReasonCode);
            Assert.Equal(assessment.ProfileFingerprint, hint.ProfileFingerprint);
            Assert.Equal(assessment.Decision.EarlierHourAveragePercent, hint.EarlierHourAveragePercent);
            Assert.Equal(assessment.Decision.LatestHourAveragePercent, hint.LatestHourAveragePercent);
            Assert.Equal(assessment.Decision.WindowCoveragePercent, hint.WindowCoveragePercent);
            Assert.Equal(assessment.EvidenceAsOfUtc, hint.EvidenceAsOfUtc);
        }

        static DateTime CurrentWallHourStartUtc(DateTime utc, TimeZoneInfo zone)
        {
            var wall = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            var hour = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(hour) || zone.IsAmbiguousTime(hour))
                return utc;
            return TimeZoneInfo.ConvertTimeToUtc(hour, zone);
        }
    }

    [Fact]
    public async Task DiskTrendAcceptanceCaseReachesDailyFindingAndPersistedRecordDetail()
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "DiskTrend").PositiveCases
            .Single(item => item.CaseId == "F53-DiskTrend-P01-disk-trend-seven-day-low-water-at-boundary");
        var mutations = testCase.Mutations;
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedDiskTrendCase(mutations.GetProperty("validDays").GetInt32(),
            mutations.GetProperty("currentPercent").GetDouble(),
            mutations.GetProperty("declinePercentPerDay").GetDouble());
        var preview = fixture.AssessDiskTrendPreview(host);
        var readiness = preview.Readiness;
        var trend = preview.Decision.Trend;
        var previewDiagnostic = $"Disk trend preview: readiness={readiness.Status}; semanticReady={readiness.SemanticReady}; " +
            $"reason={readiness.Reason}; evidenceValid={preview.EvidenceValidity?.IsValid}; " +
            $"evidenceReason={preview.EvidenceValidity?.InvalidReason}; exclusion={preview.Decision.Exclusion}; " +
            $"decisionReason={preview.Decision.Reason}; trend={trend?.Outcome}; validDays={trend?.ValidDayCount}; " +
            $"latestDay={trend?.LatestDay}; current={trend?.CurrentAvailablePercent}; " +
            $"candidateDay={fixture.AnalysisDay:yyyy-MM-dd}; missingHourWindowStart={preview.MissingHourWindowStart}; " +
            $"missingHourMasks={string.Join(",", preview.MissingHourMasks)}";
        Assert.True(preview.Decision.Eligible, previewDiagnostic);
        Assert.Equal(PrtgDiskTrendOutcome.Hit, trend?.Outcome);
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);

        var finding = RequireDailyFinding(run, host, fixture.AnalysisDay, "PRTG:disk_free_trend", testCase.CaseId);
        Assert.Equal("builtin-prtg-resource-disk-pressure", finding.RuleId);
        Assert.Contains("disk-seven-day-low-water-trend", finding.PrtgResourceReasonCodes ?? []);
        Assert.Contains(finding.SampleMessages,
            message => message.Contains("7.0 日降到低水位", StringComparison.Ordinal));
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == finding.Source &&
            issue.PrtgResourceReasonCodes?.Contains("disk-seven-day-low-water-trend", StringComparer.Ordinal) == true &&
            issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    public static IEnumerable<object[]> RemainingDiskTrendPositiveCases()
    {
        foreach (var testCase in Manifest.Value.Families.Single(item => item.Family == "DiskTrend").PositiveCases
                     .Where(item => item.CaseId != "F53-DiskTrend-P01-disk-trend-seven-day-low-water-at-boundary"))
            yield return [testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(RemainingDiskTrendPositiveCases))]
    public async Task RemainingDiskTrendPositiveCasesReachDailyFindingAndPersistedRecordDetail(string caseId)
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "DiskTrend").PositiveCases
            .Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
            var validDays = mutations.GetProperty("validDays").GetInt32();
            var currentPercent = mutations.GetProperty("currentPercent").GetDouble();
            var declinePercentPerDay = mutations.TryGetProperty("declinePercentPerDay", out var decline)
                ? decline.GetDouble() : 1d;
            var dailyValues = mutations.TryGetProperty("dailyValues", out var values)
                ? values.EnumerateArray().Select(value => value.GetDouble()).ToArray() : null;
            using var fixture = new PrtgFormalRuleCaseFixture();
            var host = fixture.SeedDiskTrendCase(validDays, currentPercent, declinePercentPerDay, dailyValues);
            var preview = fixture.AssessDiskTrendPreview(host);
            Assert.True(preview.Decision.Eligible && preview.Decision.Trend?.Outcome == PrtgDiskTrendOutcome.Hit,
                $"{testCase.CaseId} preview exclusion={preview.Decision.Exclusion}; reason={preview.Decision.Reason}; " +
                $"current={preview.Decision.Trend?.CurrentAvailablePercent}; slope={preview.Decision.Trend?.RobustDeclinePercentagePointsPerDay}; " +
                $"fallingRatio={preview.Decision.Trend?.DecliningDayRatio}; estimatedLowWater={preview.Decision.Trend?.EstimatedDaysToLowWater}; " +
                $"validDays={preview.Decision.Trend?.ValidDayCount}; readiness={preview.Readiness.Status}/{preview.Readiness.Reason}; " +
                $"evidence={preview.EvidenceValidity?.IsValid}/{preview.EvidenceValidity?.InvalidReason}");
            var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
            var finding = RequireDailyFinding(run, host, fixture.AnalysisDay,
                "PRTG:disk_free_trend", testCase.CaseId);
            Assert.Equal("builtin-prtg-resource-disk-pressure", finding.RuleId);
            Assert.Contains("disk-seven-day-low-water-trend", finding.PrtgResourceReasonCodes ?? []);
            var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(fixture.AnalysisDay, 1));
            Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
                issue.PrtgResourceReasonCodes?.Contains("disk-seven-day-low-water-trend", StringComparer.Ordinal) == true &&
                issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    public static IEnumerable<object[]> DiskTrendNegativeScenarios()
    {
        foreach (var testCase in Manifest.Value.Families.Single(item => item.Family == "DiskTrend")
                     .NegativeAndBoundaryCases)
            yield return [testCase.CaseId];
    }

    [Theory]
    [MemberData(nameof(DiskTrendNegativeScenarios))]
    public async Task DiskTrendNegativeCasePreservesHumanDataAndHasNoFormalSideEffects(string caseId)
    {
        var testCase = Manifest.Value.Families.Single(item => item.Family == "DiskTrend")
            .NegativeAndBoundaryCases.Single(item => item.CaseId == caseId);
        var mutations = testCase.Mutations;
        if (caseId == "F53-DiskTrend-N08-duplicate-day")
        {
            Assert.Equal(new[] { "one canonical Daily trend finding", "no duplicate issue or case" },
                testCase.ExpectedFormalSideEffects);
            using var duplicateFixture = new PrtgFormalRuleCaseFixture();
            var duplicateValidDays = mutations.GetProperty("validDays").GetInt32();
            var current = mutations.GetProperty("currentPercent").GetDouble();
            var duplicateDecline = mutations.GetProperty("declinePercentPerDay").GetDouble();
            var duplicateDayOffset = mutations.GetProperty("duplicatePeriodDayOffset").GetInt32();
            var duplicateHost = duplicateFixture.SeedDiskTrendCase(duplicateValidDays, current, duplicateDecline,
                duplicatePeriodDayOffset: duplicateDayOffset);
            duplicateFixture.SeedHumanOwnedBaseline(duplicateHost);
            var ownershipBefore = duplicateFixture.CaptureHumanOwnedData(duplicateHost);
            var duplicatedHostDay = duplicateFixture.AnalysisDay.AddDays(-(duplicateValidDays - 1) + duplicateDayOffset);
            var trustedProfile = new PrtgTrustedSamplingProfileStore(duplicateFixture.Backend)
                .GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
            var profileAnalysisZone = TimeZoneInfo.FindSystemTimeZoneById(trustedProfile.AnalysisTimeZoneId);
            var hostWallHour = DateTime.SpecifyKind(duplicatedHostDay, DateTimeKind.Unspecified);
            var duplicateAnalysisWallHour = TimeZoneInfo.ConvertTime(hostWallHour, TimeZoneInfo.Local,
                profileAnalysisZone);
            Assert.Equal(TimeZoneInfo.Utc.Id, profileAnalysisZone.Id);
            var duplicatePeriodStart = DateTime.SpecifyKind(duplicateAnalysisWallHour, DateTimeKind.Unspecified);
            var canonicalPeriod = duplicateFixture.Backend.PrtgStore().GetValuesForSensor(
                PrtgFormalRuleCaseFixture.SensorId, duplicatePeriodStart, duplicatePeriodStart.AddHours(1));
            var canonicalRow = Assert.Single(canonicalPeriod);
            Assert.Equal(duplicatePeriodStart, canonicalRow.PeriodStart);
            var canonicalProof = PrtgTrustedSampleProof.Deserialize(canonicalRow.TrustedProof!);
            Assert.Equal(4, canonicalProof.Slots.Count);

            var duplicatePreview = duplicateFixture.AssessDiskTrendPreview(duplicateHost);
            Assert.True(duplicatePreview.Decision.Eligible && duplicatePreview.Decision.Trend?.Outcome == PrtgDiskTrendOutcome.Hit,
                $"Idempotent duplicate-period ingestion changed the valid baseline: {duplicatePreview.Decision.Exclusion}/{duplicatePreview.Decision.Reason}");
            var daily = await duplicateFixture.RunDailyAsync(duplicateHost, duplicateFixture.AnalysisDay);
            var finding = RequireDailyFinding(daily, duplicateHost, duplicateFixture.AnalysisDay,
                "PRTG:disk_free_trend", caseId);
            var findings = daily.Registry.For(duplicateHost.HostId, duplicateFixture.AnalysisDay)
                .Where(item => item.Source == "PRTG:disk_free_trend").ToArray();
            Assert.Single(findings);
            var record = Assert.Single(duplicateFixture.Backend.RecordStore(new HostKey
                { HostId = duplicateHost.HostId, HostName = duplicateHost.HostName })
                .ReadRecent(duplicateFixture.AnalysisDay, 1));
            Assert.Single(record.TopIssues.Where(item => item.EventKey == finding.EventKey));
            Assert.Single(duplicateFixture.Backend.IssueCaseStore().GetOpenForHost(duplicateHost.HostName));
            Assert.Equal(ownershipBefore, duplicateFixture.CaptureHumanOwnedData(duplicateHost));
            Assert.Empty(duplicateFixture.Backend.WorkOrderStore().GetAllActive());
            return;
        }
        var validDays = mutations.TryGetProperty("validDays", out var validDayValue) ? validDayValue.GetInt32() : 28;
        var currentPercent = mutations.TryGetProperty("currentPercent", out var currentValue) ? currentValue.GetDouble() : 27d;
        var decline = mutations.TryGetProperty("declinePercentPerDay", out var declineValue)
            ? declineValue.GetDouble() : 1d;
        var dailyValues = mutations.TryGetProperty("dailyValues", out var values)
            ? values.EnumerateArray().Select(value => value.GetDouble()).ToArray() : null;
        var missingDayOffsets = mutations.TryGetProperty("missingDayOffsets", out var missing)
            ? missing.EnumerateArray().Select(value => value.GetInt32()).ToArray() : null;
        var parentStatus = mutations.TryGetProperty("parentStatus", out var parent)
            ? parent.GetString()! : "success";
        var ruleEnabled = !mutations.TryGetProperty("ruleEnabled", out var enabled) || enabled.GetBoolean();
        var trustedHistory = !mutations.TryGetProperty("trustedHistory", out var trusted) || trusted.GetBoolean();
        var profileEffectiveDaysBefore = mutations.TryGetProperty("profileEffectiveDaysBefore", out var effectiveDays)
            ? effectiveDays.GetInt32() : 40;
        var identityDrift = mutations.TryGetProperty("identityDrift", out var drift) ? drift.GetString() : null;
        var hoursPerDay = mutations.TryGetProperty("hoursPerDay", out var dailyHours) ? dailyHours.GetInt32() : 24;
        var outOfRangePercent = mutations.TryGetProperty("outOfRangePercent", out var invalidPercent)
            ? invalidPercent.GetDouble() : (double?)null;
        var outOfRangeDayOffset = mutations.TryGetProperty("outOfRangeDayOffset", out var invalidOffset)
            ? invalidOffset.GetInt32() : (int?)null;
        var futureMeasurements = mutations.TryGetProperty("futureMeasurements", out var future) && future.GetBoolean();
        var physicalSlotsPerHour = mutations.TryGetProperty("physicalSlotsPerHour", out var physicalSlots)
            ? physicalSlots.GetInt32() : 4;
        var duplicatePeriodDayOffset = mutations.TryGetProperty("duplicatePeriodDayOffset", out var duplicateOffset)
            ? duplicateOffset.GetInt32() : (int?)null;
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedDiskTrendCase(validDays, currentPercent, decline, dailyValues,
            missingDayOffsets, "success", trustedHistory: trustedHistory,
            profileEffectiveDaysBefore: profileEffectiveDaysBefore, identityDrift: identityDrift,
            hoursPerDay: hoursPerDay, outOfRangePercent: outOfRangePercent,
            outOfRangeDayOffset: outOfRangeDayOffset, futureMeasurements: futureMeasurements,
            physicalSlotsPerHour: physicalSlotsPerHour, duplicatePeriodDayOffset: duplicatePeriodDayOffset);
        if (!ruleEnabled) fixture.SetDiskTrendRuleEnabled(false);
        _ = fixture.SeedHumanOwnedBaseline(host);
        var beforeHumanSidecars = CaptureHumanSidecars(fixture, host, fixture.AnalysisDay);
        if (parentStatus == "missing") DeleteParentRecord(fixture, host, fixture.AnalysisDay);
        else if (parentStatus != "success") SetParentAttemptStatus(fixture, host, fixture.AnalysisDay, parentStatus);
        var preview = fixture.AssessDiskTrendPreview(host);
        if (!ruleEnabled)
        {
            Assert.True(preview.Decision.Eligible && preview.Decision.WouldHit,
                "A disabled rule may still preview its qualifying trend without publishing a finding.");
            Assert.Null(preview.Decision.Finding);
            var formal = fixture.AssessDiskTrendFormal(host);
            Assert.Equal(PrtgDiskDecisionExclusion.RuleDisabled, formal.Decision.Exclusion);
            Assert.Contains("停用規則", formal.Decision.Reason, StringComparison.Ordinal);
            Assert.Null(formal.Decision.Finding);
        }
        else if (parentStatus != "success")
        {
            Assert.True(preview.Decision.Eligible && preview.Decision.Trend?.Outcome == PrtgDiskTrendOutcome.Hit,
                $"Underlying synthetic trend should qualify before Daily parent gating: {preview.Decision.Exclusion}/{preview.Decision.Reason}");
        }
        else if (caseId.Contains("N01-", StringComparison.Ordinal) ||
                 caseId.Contains("N02-", StringComparison.Ordinal) ||
                 caseId.Contains("N03-", StringComparison.Ordinal) ||
                 caseId.Contains("N04-", StringComparison.Ordinal) ||
                 caseId.Contains("N05-", StringComparison.Ordinal) ||
                 caseId.Contains("N06-", StringComparison.Ordinal) ||
                 caseId.Contains("N07-", StringComparison.Ordinal))
        {
            var expectedExclusion = caseId.Contains("N01-", StringComparison.Ordinal) ||
                caseId.Contains("N02-", StringComparison.Ordinal)
                ? PrtgDiskDecisionExclusion.ReadinessNotReady
                : caseId.Contains("N08-", StringComparison.Ordinal) ||
                caseId.Contains("N09-", StringComparison.Ordinal) ||
                caseId.Contains("N21-", StringComparison.Ordinal)
                ? PrtgDiskDecisionExclusion.TrendInsufficientData : PrtgDiskDecisionExclusion.TrendNoHit;
            Assert.Equal(expectedExclusion, preview.Decision.Exclusion);
            Assert.False(preview.Decision.WouldHit);
            var expectedReason = caseId switch
            {
                var id when id.Contains("N01-", StringComparison.Ordinal) => "有效日不足",
                var id when id.Contains("N02-", StringComparison.Ordinal) => "有效日不足",
                var id when id.Contains("N08-", StringComparison.Ordinal) => "有效日不足",
                var id when id.Contains("N09-", StringComparison.Ordinal) => "有效日不足",
                var id when id.Contains("N21-", StringComparison.Ordinal) => "有效日不足",
                var id when id.Contains("N03-", StringComparison.Ordinal) => "穩健下降斜率",
                var id when id.Contains("N04-", StringComparison.Ordinal) => "下降日比例",
                var id when id.Contains("N05-", StringComparison.Ordinal) => "超出",
                _ => "未達"
            };
            if (caseId.Contains("N01-", StringComparison.Ordinal) || caseId.Contains("N02-", StringComparison.Ordinal))
                Assert.False(string.IsNullOrWhiteSpace(preview.Decision.Reason));
            else
                Assert.Contains(expectedReason, preview.Decision.Reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(preview.Decision.WouldHit, $"{caseId} unexpectedly hit: {preview.Decision.Reason}");
            Assert.False(string.IsNullOrWhiteSpace(preview.Decision.Reason),
                $"{caseId} did not expose a preview exclusion reason.");
        }
        var run = await fixture.RunDailyAsync(host, fixture.AnalysisDay);
        Assert.DoesNotContain(run.Registry.For(host.HostId, fixture.AnalysisDay),
            finding => finding.Source == "PRTG:disk_free_trend");
        Assert.Equal(beforeHumanSidecars, CaptureHumanSidecars(fixture, host, fixture.AnalysisDay));
        Assert.Single(fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName));
        Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
    }

    [Fact]
    public async Task SilentAcceptanceCaseUsesCompleteSyntheticSourceAndReachesDailyRecordDetail()
    {
        var family = Manifest.Value.Families.Single(item => item.Family == "Silent");
        var testCase = family.PositiveCases.Single(item => item.CaseId == "F53-Silent-P01-silent-complete-up-availability-unknown-resource");
        Assert.True(testCase.Mutations.GetProperty("inventoryComplete").GetBoolean());
        var sourceAsOf = DateTimeOffset.UtcNow;

        using var fixture = new PrtgFormalRuleCaseFixture();
        var localDay = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date;
        fixture.UseAnalysisDay(localDay);
        var host = fixture.SeedSilentCase();
        var captured = await fixture.CaptureSyntheticSilentSourceAsync(host, sourceAsOf);
        Assert.Equal(PrtgPresenceReadQuality.Complete, captured.ReadQuality);
        Assert.Equal(2, captured.ReportedSensorCount);

        var run = await fixture.RunDailyAsync(host, localDay);
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var configuredUrl = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().PrtgUrl;
        var readiness = new PrtgSilentPresenceFormalConsumer(fixture.Backend).EvaluateWithReadiness(localDay,
            policy, configuredUrl, new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId }, KnownIssueCatalog.Rules)
            .Devices[PrtgFormalRuleCaseFixture.DeviceId];
        var finding = RequireDailyFinding(run, host, localDay, "PRTG:silent", testCase.CaseId,
            $"Post-Daily formal readiness: {readiness.State}/{readiness.Reason}");
        Assert.Equal("builtin-prtg-silent", finding.RuleId);
        Assert.Contains(finding.SampleMessages, message => message.Contains("不代表主機故障", StringComparison.Ordinal));
        Assert.NotNull(finding.PrtgPresenceSourceAsOf);
        var record = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(localDay, 1));
        Assert.Contains(record.TopIssues, issue => issue.EventKey == finding.EventKey &&
            issue.Source == "PRTG:silent" && issue.PrtgPresenceInventoryFingerprint == finding.PrtgPresenceInventoryFingerprint &&
            issue.SampleMessages.SequenceEqual(finding.SampleMessages));
    }

    private static CaseManifest LoadManifest()
    {
        var relative = Path.Combine("Fixtures", "prtg-round53-formal-cases.json");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return JsonSerializer.Deserialize<CaseManifest>(File.ReadAllText(direct), ManifestJsonOptions)!;
            var projectRelative = Path.Combine(directory.FullName, "LogForesight.Tests", relative);
            if (File.Exists(projectRelative))
                return JsonSerializer.Deserialize<CaseManifest>(File.ReadAllText(projectRelative), ManifestJsonOptions)!;
        }
        throw new FileNotFoundException("Could not locate the formal PRTG case fixture.", relative);
    }

    private static LogIssueSignature RequireDailyFinding(
        (PrtgFindingsRegistry Registry, IReadOnlyList<string> ConsoleLines) run,
        WebHost host, DateTime day, string source, string caseId, string? additionalDiagnostic = null)
    {
        var findings = run.Registry.For(host.HostId, day);
        Assert.True(findings.Any(signature => signature.Source == source),
            $"{caseId} did not reach Daily source {source}. Registered sources: " +
            string.Join(", ", findings.Select(signature => signature.Source)) + Environment.NewLine +
            (additionalDiagnostic ?? string.Empty) + Environment.NewLine +
            "Daily consumer output:" + Environment.NewLine + string.Join(Environment.NewLine, run.ConsoleLines));
        var matches = findings.Where(signature => signature.Source == source).ToArray();
        var detail = string.Join(Environment.NewLine, matches.Select(signature =>
            $"rule={signature.RuleId}; event={signature.EventKey}; reasons={string.Join(",", signature.PrtgResourceReasonCodes ?? [])}; " +
            $"messages={string.Join(" | ", signature.SampleMessages)}"));
        Assert.True(matches.Length == 1,
            $"{caseId} expected exactly one {source} finding; actual count={matches.Length}.{Environment.NewLine}{detail}");
        return matches[0];
    }

    private sealed record CaseManifest(int SchemaVersion, string SourceTruth, CaseContract CaseContract,
        IReadOnlyList<FamilyCaseSet> Families);
    private sealed record CaseContract(int MinimumPositivePerFamily, int MinimumNegativeOrBoundaryPerFamily,
        string PositiveMustReach, string NegativeMustAssert);
    private sealed record FamilyCaseSet(string Family, string RuleCode, string FormalConsumer,
        string FixtureAuthority, IReadOnlyList<PositiveCase> PositiveCases,
        IReadOnlyList<NegativeCase> NegativeAndBoundaryCases);
    private sealed record PositiveCase(string CaseId, JsonElement Mutations, string ExpectedFormalResult,
        string FixtureAuthority);
    private sealed record NegativeCase(string CaseId, string Mutation, string FailedQualification,
        IReadOnlyList<string> ExpectedFormalSideEffects, bool PreserveNetiqAndHumanOwnedData,
        string FixtureAuthority, JsonElement OriginalPositiveMutation, string? Reclassification, JsonElement Mutations,
        JsonElement OriginalMutation = default, string? OriginalFailedQualification = null,
        string? OriginalExpectedFormalResult = null);

    private static bool IsHostDayStraddleCase(string caseId) => caseId is
        "F53-DiskTwoHourLowWater-P04-disk-low-water-crosses-host-midnight" or
        "F53-CPUFormal-P04-cpu-formal-two-completed-hours-cross-midnight" or
        "F53-MemoryFormal-P05-memory-formal-completed-hours-cross-midnight";

    private static string sourceFor(PrtgResourceFamily family) => family switch
    {
        PrtgResourceFamily.Cpu => "PRTG:resource_cpu_sustained_pressure",
        PrtgResourceFamily.Memory => "PRTG:resource_memory_sustained_pressure",
        _ => "PRTG:disk_free_trend"
    };
}
