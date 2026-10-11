using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

// Consumer acceptance reuses the sealed manifest suite's scenario dispatch rather than
// carrying a second, less complete copy of its state-negative setup.
public sealed partial class PrtgFormalRuleCaseManifestTests
{
    internal static WebHost SeedConsumerStateScenario(PrtgFormalRuleCaseFixture fixture,
        string family, JsonElement mutations)
    {
        var scenario = RequiredScenario(mutations, "consumer manifest bridge");
        var categoryFilter = mutations.TryGetProperty("ruleCategoryFilter", out var category)
            ? category.GetString() : null;
        if (categoryFilter is not null)
            SetStateRuleCategoryFilter(fixture, family, categoryFilter);
        return SeedStateScenario(fixture, family, scenario, mutations);
    }

    internal static WebHost SeedConsumerStatePositive(PrtgFormalRuleCaseFixture fixture,
        string family, string caseId, JsonElement mutations)
    {
        var code = family.ToLowerInvariant();
        if (mutations.TryGetProperty("thresholdMinutes", out var minuteThreshold))
            fixture.SetPrtgThreshold(code, family == "Down" ? PrtgSensorCategories.Availability : null,
                minuteThreshold.GetInt32());
        if (mutations.TryGetProperty("threshold", out var flapThreshold))
            fixture.SetPrtgThreshold(code, null, flapThreshold.GetInt32());

        IReadOnlyList<(DateTime At, string Status)> transitions;
        var day = fixture.AnalysisDay;
        if (family == "Down")
        {
            var duration = mutations.TryGetProperty("durationMinutes", out var durationValue)
                ? durationValue.GetInt32() : mutations.GetProperty("secondDurationMinutes").GetInt32();
            var downStatus = mutations.TryGetProperty("acknowledged", out var ack) && ack.GetBoolean()
                ? "Down (Acknowledged)" : "Down";
            if (mutations.TryGetProperty("crossesMidnight", out var cross) && cross.GetBoolean())
                transitions = [(day.AddMinutes(-10), downStatus), (day.AddMinutes(duration - 10), "Up")];
            else if (mutations.TryGetProperty("episodeCount", out var count) && count.GetInt32() > 1)
            {
                var first = mutations.GetProperty("secondDurationMinutes").GetInt32();
                var recovery = mutations.GetProperty("recoveryGapMinutes").GetInt32();
                transitions = [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(first), "Up"),
                    (day.AddHours(1).AddMinutes(first + recovery), "Down"),
                    (day.AddHours(1).AddMinutes(first + recovery + duration), "Up")];
            }
            else transitions = [(day.AddHours(1), downStatus), (day.AddHours(1).AddMinutes(duration), "Up")];
        }
        else if (family == "Warning")
        {
            var segments = new List<int>();
            var gaps = new List<int>();
            if (mutations.TryGetProperty("segments", out var segmentValues))
            {
                segments.AddRange(segmentValues.EnumerateArray().Select(value => value.GetInt32()));
                if (segments.Count > 1)
                    gaps.Add(mutations.TryGetProperty("upGapMinutes", out var gap) ? gap.GetInt32() : 5);
            }
            else if (mutations.TryGetProperty("episodes", out var episodes))
            {
                foreach (var episode in episodes.EnumerateArray())
                {
                    segments.Add(episode.GetProperty("warningMinutes").GetInt32());
                    if (segments.Count > 1) gaps.Add(60);
                }
            }
            else segments.Add(mutations.GetProperty("warningMinutes").GetInt32());
            if (mutations.TryGetProperty("crossesMidnight", out var crosses) && crosses.GetBoolean())
                transitions = [(day.AddMinutes(-120), "Warning"), (day.AddHours(2), "Up")];
            else
            {
                var cursor = day.AddMinutes(mutations.TryGetProperty("startsAtMinute", out var start)
                    ? start.GetInt32() : 60);
                var items = new List<(DateTime At, string Status)>();
                for (var index = 0; index < segments.Count; index++)
                {
                    items.Add((cursor, "Warning"));
                    cursor = cursor.AddMinutes(segments[index]);
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
                items.Add((day.AddHours(-1), "Down"));
                items.Add((day.AddMinutes(30), "Up"));
                firstCycle = 1;
            }
            else if (mutations.TryGetProperty("recoveryBeforeWindow", out var recovered) && recovered.GetBoolean())
            {
                items.Add((day.AddDays(-2), "Down"));
                items.Add((day.AddDays(-2).AddMinutes(30), "Up"));
            }
            for (var index = firstCycle; index < roundTrips; index++)
            {
                var start = day.AddHours(2 + index * 2);
                items.Add((start, "Down"));
                items.Add((start.AddHours(1), "Up"));
            }
            transitions = items;
        }
        return fixture.SeedCoveredStateCase($"consumer-{family}-{caseId}", "Ping", "Ping", "Up",
            _ => transitions, PrtgSensorCategories.Availability);
    }

    internal static WebHost SeedConsumerResourceCase(PrtgFormalRuleCaseFixture fixture,
        string manifestFamily, JsonElement mutations)
    {
        var family = manifestFamily switch
        {
            "CPUFormal" => PrtgResourceFamily.Cpu,
            "MemoryFormal" => PrtgResourceFamily.Memory,
            "DiskTwoHourLowWater" => PrtgResourceFamily.Disk,
            _ => throw new ArgumentOutOfRangeException(nameof(manifestFamily))
        };
        var values = mutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var scenario = mutations.TryGetProperty("scenario", out var scenarioValue)
            ? scenarioValue.GetString() : null;
        var evidenceMutation = scenario is "duplicate-physical-slot" or "summary-proof-disagrees" or
            "unproved-mean" or "future-samples" or "future-sample" or "insufficient-slot-window"
            ? scenario : null;
        var authorityMutation = scenario is "channel-generation-drift" or "source-generation-drift" or
            "resource-epoch-drift" or "wrong-host-scope" or "paused-sensor" or
            "formal-source-policy-unready" or "unverified-time-basis" or "missing-maintain-permission" or
            "trial-profile-drift" or "resource-generation-drift" or "live-source-generation-mismatch" or
            "live-resource-generation-mismatch" or "live-channel-generation-mismatch" or "live-epoch-mismatch"
            ? scenario : null;
        var crossesAnalysisMidnight = mutations.TryGetProperty("crossesAnalysisMidnight", out var analysisCross)
            && analysisCross.GetBoolean();
        var crossesHostMidnight = mutations.TryGetProperty("crossesHostMidnight", out var hostCross)
            && hostCross.GetBoolean();
        var enabled = !mutations.TryGetProperty("ruleEnabled", out var enabledValue) || enabledValue.GetBoolean();
        var memoryRemaining = family == PrtgResourceFamily.Memory &&
            mutations.TryGetProperty("semantic", out var semantic) &&
            semantic.GetString() == "memory-remaining-percent";
        return fixture.SeedFormalResourceCase(family, values,
            mutations.TryGetProperty("goodPhysicalSlotsPerHour", out var slots) ? slots.GetInt32() : 4,
            crossesAnalysisMidnight, crossesHostMidnight, memoryRemaining, enabled,
            mutations.TryGetProperty("periodMutation", out var period) ? period.GetString() : null,
            mutations.TryGetProperty("profileMutation", out var profile) ? profile.GetString() : null,
            evidenceMutation, authorityMutation);
    }

    internal static (WebHost Host, DateTime LaterHostDay) SeedConsumerHostDayStraddle(
        PrtgFormalRuleCaseFixture fixture, string manifestFamily, JsonElement positiveMutations)
    {
        var family = manifestFamily switch
        {
            "CPUFormal" => PrtgResourceFamily.Cpu,
            "MemoryFormal" => PrtgResourceFamily.Memory,
            _ => PrtgResourceFamily.Disk
        };
        fixture.UseAnalysisDay(DateTime.Today.AddDays(-2));
        new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.ValidFrom = new DateTimeOffset(fixture.AnalysisDay.AddDays(-32)));
        var values = positiveMutations.GetProperty("completedHourAverages").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var memoryRemaining = positiveMutations.TryGetProperty("semantic", out var semantic) &&
            semantic.GetString() == "memory-remaining-percent";
        var host = fixture.SeedFormalResourceCase(family, values, crossesHostMidnight: true,
            memoryValuesAreRemainingPercent: memoryRemaining);
        var laterDay = fixture.AnalysisDay.AddDays(1);
        fixture.Backend.PrtgStore().ReplaceHostMapForDate(laterDay, [new PrtgHostMapRow
        {
            DeviceObjid = PrtgFormalRuleCaseFixture.DeviceId, MapDate = laterDay,
            HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok,
            CreatedAt = DateTime.UtcNow
        }]);
        var records = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        var earlierParent = Assert.Single(records.ReadRecent(fixture.AnalysisDay, 1));
        records.Append(new DailyAnalysisRecord
        {
            LogSource = earlierParent.LogSource,
            LatestNetiqAttemptStatus = earlierParent.LatestNetiqAttemptStatus,
            Date = laterDay,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = earlierParent.RiskLevel,
            RiskBasis = earlierParent.RiskBasis,
            TopIssues = earlierParent.TopIssues.ToList()
        });
        return (host, laterDay);
    }

    internal static string? NormalizeConsumerSilentScenario(JsonElement mutations) =>
        mutations.TryGetProperty("scenario", out var scenario)
            ? NormalizeSilentScenario(scenario.GetString()!) : null;

    internal static void DeleteConsumerParent(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day) =>
        DeleteParentRecord(fixture, host, day);

    internal static void SetConsumerParentAttemptStatus(PrtgFormalRuleCaseFixture fixture,
        WebHost host, DateTime day, string status) => SetParentAttemptStatus(fixture, host, day, status);

    internal static string CaptureConsumerHumanSidecars(PrtgFormalRuleCaseFixture fixture,
        WebHost host, DateTime day) => CaptureHumanSidecars(fixture, host, day);

    internal static async Task<(WebHost Host, DateTime Day)> SeedConsumerSilentCase(
        PrtgFormalRuleCaseFixture fixture, JsonElement mutations)
    {
        if (!mutations.TryGetProperty("scenario", out var scenarioValue) &&
            !mutations.TryGetProperty("sourcePoint", out _) &&
            !mutations.TryGetProperty("priorUnknownDurationHours", out _) &&
            !mutations.TryGetProperty("unknownDurationHours", out _) &&
            !mutations.TryGetProperty("sourceAsOfOffsetSeconds", out _))
        {
            var historicalAvailabilityCount = mutations.TryGetProperty("availabilityUpSensors", out var historicalCount)
                ? historicalCount.GetInt32() : 1;
            var historicalUnknownCount = mutations.TryGetProperty("unknownNonAvailabilitySensors", out var historicalUnknown)
                ? historicalUnknown.GetInt32() : 1;
            var historicalHost = fixture.SeedSilentCase(availabilitySensorCount: historicalAvailabilityCount);
            fixture.StoreSyntheticHistoricalSilentProof(historicalHost);
            var policy = new PrtgMonitoringPolicyStore(
                fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
            var historicalSourceAsOf = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(
                fixture.AnalysisDay.Date.AddHours(23), DateTimeKind.Unspecified), sourceZone));
            var sourceDay = TimeZoneInfo.ConvertTime(historicalSourceAsOf, sourceZone).Date;
            var snapshot = new PrtgSilentPresenceSnapshotStore(fixture.Backend.Blob(
                PrtgSilentPresenceSnapshotStore.BlobKey(PrtgFormalRuleCaseFixture.DeviceId, sourceDay)))
                .Get(PrtgFormalRuleCaseFixture.DeviceId);
            Assert.NotNull(snapshot);
            Assert.Equal(PrtgPresenceReadQuality.Complete, snapshot!.ReadQuality);
            Assert.Equal(historicalAvailabilityCount + historicalUnknownCount, snapshot.ReportedSensorCount);
            return (historicalHost, fixture.AnalysisDay);
        }
        if (mutations.TryGetProperty("scenario", out scenarioValue) &&
            !mutations.TryGetProperty("sourcePoint", out _) &&
            !mutations.TryGetProperty("priorUnknownDurationHours", out _) &&
            !mutations.TryGetProperty("unknownDurationHours", out _) &&
            !mutations.TryGetProperty("sourceAsOfOffsetSeconds", out _))
        {
            var scenarioAsOf = DateTimeOffset.UtcNow;
            var scenarioDay = TimeZoneInfo.ConvertTime(scenarioAsOf, TimeZoneInfo.Local).Date;
            fixture.UseAnalysisDay(scenarioDay);
            var scenario = NormalizeSilentScenario(scenarioValue.GetString()!);
            var noUpAvailability = scenario == "all-unknown-no-up-availability";
            var resourceStatus = noUpAvailability ? "Up" : "Unknown";
            var availabilityStatus = noUpAvailability ? "Unknown" : "Up";
            var scenarioHost = fixture.SeedSilentCase(resourceStatus, availabilityStatus);
            if (scenario != "missing-snapshot")
                await fixture.CaptureSyntheticSilentSourceAsync(scenarioHost, scenarioAsOf,
                    resourceStatus: resourceStatus, availabilityStatus: availabilityStatus);
            MutateSilentSnapshot(fixture, scenarioHost, scenarioDay, scenario, scenarioAsOf);
            return (scenarioHost, scenarioDay);
        }
        var availabilityCount = mutations.TryGetProperty("availabilityUpSensors", out var count)
            ? count.GetInt32() : 1;
        DateTimeOffset sourceAsOf;
        DateTimeOffset? deviceAsOf = null;
        DateTime? unknownSince = null;
        if (mutations.TryGetProperty("sourcePoint", out _))
            sourceAsOf = DateTimeOffset.UtcNow.AddSeconds(-30);
        else if (mutations.TryGetProperty("priorUnknownDurationHours", out var duration) ||
                 mutations.TryGetProperty("unknownDurationHours", out duration))
        {
            sourceAsOf = DateTimeOffset.UtcNow.AddMinutes(-mutations.GetProperty("sourceAgeMinutes").GetDouble());
            var localAsOf = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).DateTime;
            unknownSince = DateTime.SpecifyKind(localAsOf.AddHours(-duration.GetDouble()), DateTimeKind.Unspecified);
        }
        else
        {
            var offsetSeconds = mutations.GetProperty("sourceAsOfOffsetSeconds").GetInt32();
            sourceAsOf = DateTimeOffset.UtcNow.AddSeconds(offsetSeconds);
            deviceAsOf = sourceAsOf.AddSeconds(-offsetSeconds);
        }
        var day = TimeZoneInfo.ConvertTime(sourceAsOf, TimeZoneInfo.Local).Date;
        fixture.UseAnalysisDay(day);
        var host = fixture.SeedSilentCase(availabilitySensorCount: availabilityCount, unknownSince: unknownSince);
        if (mutations.TryGetProperty("sourcePoint", out _))
        {
            var boundaryZone = TimeZoneInfo.GetSystemTimeZones()
                .Where(zone => TimeZoneInfo.ConvertTime(sourceAsOf, zone).Date != day)
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
            Assert.NotEqual(day, captured.SourceDay);
            Assert.NotNull(captured.SourceAsOf);
            Assert.InRange(Math.Abs((captured.SourceAsOf!.Value - sourceAsOf).TotalSeconds), 0, 120);
        }
        return (host, day);
    }
}
