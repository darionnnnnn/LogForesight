using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureEvaluatorTests
{
    public static IEnumerable<object[]> NegativeCases()
    {
        var cases = new[]
        {
            "missing-proof", "missing-quality", "missing-previous", "missing-latest", "gap",
            "low-coverage", "duplicate-hour", "false-average", "false-coverage", "foreign-sensor",
            "source-drift", "resource-drift", "channel-drift", "epoch-drift", "semantic-drift",
            "strategy-drift", "raw-zone-drift", "analysis-zone-drift", "wrong-semantic", "wrong-unit",
            "unverified-semantic", "wrong-scale", "quality-slot-mismatch", "duplicate-physical-sample",
            "out-of-range-value", "nonnumeric-value", "open-hour", "wall-key-has-offset", "excessive-window",
            "future-as-of"
        };
        foreach (var family in Enum.GetValues<PrtgResourceFamily>())
            foreach (var caseId in cases) yield return [family, caseId];
    }

    public static IEnumerable<object[]> PositiveCases()
    {
        foreach (var family in Enum.GetValues<PrtgResourceFamily>())
        {
            var cases = family == PrtgResourceFamily.Disk
                ? new (double, double, PrtgResourceDecisionKind)[]
                {
                    (10, 10, PrtgResourceDecisionKind.Hit), (0, 10, PrtgResourceDecisionKind.Hit),
                    (16, 16, PrtgResourceDecisionKind.Recovery), (15, 16, PrtgResourceDecisionKind.NoHit),
                    (11, 11, PrtgResourceDecisionKind.NoHit)
                }
                : new (double, double, PrtgResourceDecisionKind)[]
                {
                    (90, 90, PrtgResourceDecisionKind.Hit), (99, 90, PrtgResourceDecisionKind.Hit),
                    (84, 84, PrtgResourceDecisionKind.Recovery), (85, 84, PrtgResourceDecisionKind.NoHit),
                    (90, 89, PrtgResourceDecisionKind.NoHit)
                };
            foreach (var test in cases) yield return [family, test.Item1, test.Item2, test.Item3];
        }
    }

    [Theory]
    [MemberData(nameof(PositiveCases))]
    public void FixedPositiveMatrixCoversBothHoursForEveryResourceFamily(PrtgResourceFamily family,
        double earlier, double latest, PrtgResourceDecisionKind expected)
    {
        var semantic = family switch
        {
            PrtgResourceFamily.Cpu => PrtgResourceSemantic.CpuUsed,
            PrtgResourceFamily.Memory => PrtgResourceSemantic.MemoryUsed,
            _ => PrtgResourceSemantic.DiskRemaining
        };
        var result = PrtgResourcePressureEvaluator.Evaluate(Input(family, semantic, earlier, latest));
        Assert.Equal(expected, result.Kind);
        Assert.Equal(2, result.Window.Count);
        Assert.Equal(100, result.WindowCoveragePercent);
    }

    [Fact]
    public void AssessmentEvidenceFingerprintChangesWhenPhysicalProofChangesButAverageDoesNot()
    {
        var input = Input(PrtgResourceFamily.Disk, PrtgResourceSemantic.DiskRemaining, 8, 9);
        var changedHours = input.Hours!.Select((hour, hourIndex) => hourIndex == 0
            ? hour with
            {
                ContextProof = hour.ContextProof! with
                {
                    Slots = hour.ContextProof!.Slots.Select((slot, slotIndex) => slotIndex == 0
                        ? slot with { PhysicalIdHash = new string('F', 32) } : slot).ToArray()
                }
            }
            : hour).ToArray();
        var changedInput = input with { Hours = changedHours };
        var firstDecision = PrtgResourcePressureEvaluator.Evaluate(input);
        var changedDecision = PrtgResourcePressureEvaluator.Evaluate(changedInput);
        var context = input.AuthorizedContext!;
        var first = new PrtgResourcePeriodAssessment(20, 30, context.SensorObjid,
            PrtgResourceFamily.Disk, input, firstDecision, DateTime.UtcNow, context.SourceGeneration,
            context.ResourceGeneration, context.ChannelGeneration, context.ResourceEpoch,
            context.SemanticVersion, context.StrategyVersion, null, "", "");
        var changed = new PrtgResourcePeriodAssessment(20, 30, context.SensorObjid,
            PrtgResourceFamily.Disk, changedInput, changedDecision, DateTime.UtcNow, context.SourceGeneration,
            context.ResourceGeneration, context.ChannelGeneration, context.ResourceEpoch,
            context.SemanticVersion, context.StrategyVersion, null, "", "");

        Assert.Equal(firstDecision.Window.Select(hour => hour.AveragePercent),
            changedDecision.Window.Select(hour => hour.AveragePercent));
        Assert.NotEqual(first.EvidenceFingerprint, changed.EvidenceFingerprint);
    }

    [Theory]
    [InlineData(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, 90)]
    [InlineData(PrtgResourceFamily.Memory, PrtgResourceSemantic.MemoryUsed, 90)]
    [InlineData(PrtgResourceFamily.Disk, PrtgResourceSemantic.DiskRemaining, 10)]
    public void ExactlySeventyFivePercentPhysicalSlotCoverageIsEligible(PrtgResourceFamily family,
        PrtgResourceSemantic semantic, double threshold)
    {
        var input = Input(family, semantic, threshold, threshold);
        var hours = input.Hours!.Select(hour =>
        {
            var proof = hour.ContextProof! with { Slots = hour.ContextProof!.Slots.Take(3).ToArray() };
            return hour with { ContextProof = proof, QualityGoodSlots = [0, 1, 2], SummaryCoveragePercent = 75 };
        }).ToArray();
        var result = PrtgResourcePressureEvaluator.Evaluate(input with { Hours = hours });
        Assert.Equal(PrtgResourceDecisionKind.Hit, result.Kind);
        Assert.Equal(75, result.WindowCoveragePercent);
    }

    [Theory]
    [InlineData(9, PrtgResourceDecisionKind.Hit)]
    [InlineData(8, PrtgResourceDecisionKind.Insufficient)]
    public void FiveMinuteStrategyKeepsFixedTwelveSlotDenominator(int goodSlots,
        PrtgResourceDecisionKind expected)
    {
        var zone = TimeZoneInfo.Utc;
        var context = Context(zone) with { StrategyMinutes = 5, StrategyVersion = "strategy-5m" };
        var now = DateTime.UtcNow;
        var current = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Unspecified);
        var first = HourFiveMinute(context, zone, current.AddHours(-2), 95, goodSlots);
        var second = HourFiveMinute(context, zone, current.AddHours(-1), 95, goodSlots);
        var input = BuildInput(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed,
            context, [first, second], now);
        var result = PrtgResourcePressureEvaluator.Evaluate(input);
        Assert.Equal(expected, result.Kind);
        if (expected == PrtgResourceDecisionKind.Hit) Assert.Equal(75, result.WindowCoveragePercent);
    }

    [Theory]
    [MemberData(nameof(NegativeCases))]
    public void FixedNegativeMatrixFailsClosedForEveryResourceFamily(PrtgResourceFamily family, string caseId)
    {
        var input = NegativeInput(family, caseId);
        var result = PrtgResourcePressureEvaluator.Evaluate(input);
        Assert.Equal(PrtgResourceDecisionKind.Insufficient, result.Kind);
        Assert.Equal(ExpectedReason(caseId), result.ReasonCode);
    }

    [Theory]
    [InlineData(90, PrtgResourceDecisionKind.Hit)]
    [InlineData(89.99, PrtgResourceDecisionKind.NoHit)]
    [InlineData(85, PrtgResourceDecisionKind.NoHit)]
    [InlineData(84.99, PrtgResourceDecisionKind.Recovery)]
    public void CpuThresholdsAreStrictAndUseBothCompletedHours(double value, PrtgResourceDecisionKind expected)
    {
        var input = Input(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, value, value);
        var result = PrtgResourcePressureEvaluator.Evaluate(input);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(PrtgResourceDecisionMode.Hint, result.Mode);
        Assert.Equal(2, result.Window.Count);
    }

    [Theory]
    [InlineData(10, PrtgResourceDecisionKind.Hit)]
    [InlineData(11, PrtgResourceDecisionKind.NoHit)]
    [InlineData(15, PrtgResourceDecisionKind.NoHit)]
    [InlineData(15.01, PrtgResourceDecisionKind.Recovery)]
    public void DiskThresholdsUseRemainingPercentAndStrictRecovery(double value, PrtgResourceDecisionKind expected)
    {
        var input = Input(PrtgResourceFamily.Disk, PrtgResourceSemantic.DiskRemaining, value, value);
        var result = PrtgResourcePressureEvaluator.Evaluate(input);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(PrtgResourceDecisionMode.FormalRisk, result.Mode);
    }

    [Fact]
    public void MemoryRemainingIsConvertedToUsedBeforeApplyingPressureThreshold()
    {
        var hit = PrtgResourcePressureEvaluator.Evaluate(Input(PrtgResourceFamily.Memory,
            PrtgResourceSemantic.MemoryRemaining, 10, 10));
        var noHit = PrtgResourcePressureEvaluator.Evaluate(Input(PrtgResourceFamily.Memory,
            PrtgResourceSemantic.MemoryRemaining, 11, 11));
        Assert.Equal(PrtgResourceDecisionKind.Hit, hit.Kind);
        Assert.Equal(90, hit.LatestHourAveragePercent);
        Assert.Equal(PrtgResourceDecisionKind.NoHit, noHit.Kind);
    }

    [Fact]
    public void RejectsSummaryThatDisagreesWithPhysicalSlotProof()
    {
        var input = Input(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, 95, 95);
        var hours = input.Hours!.ToArray();
        hours[0] = hours[0] with { SummaryAverage = 1 };
        var result = PrtgResourcePressureEvaluator.Evaluate(input with { Hours = hours });
        Assert.Equal(PrtgResourceDecisionKind.Insufficient, result.Kind);
        Assert.Equal("summary-does-not-match-proved-slots", result.ReasonCode);
    }

    [Fact]
    public void LatestTwoCompletedHoursFollowIndiaWallClockAcrossUtcHalfHourBoundary()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var context = Context(zone);
        var asOfUtc = DateTime.UtcNow.AddHours(-1);
        var asOfWall = TimeZoneInfo.ConvertTimeFromUtc(asOfUtc, zone);
        var secondWall = new DateTime(asOfWall.Year, asOfWall.Month, asOfWall.Day, asOfWall.Hour, 0, 0,
            DateTimeKind.Unspecified).AddHours(-1);
        var firstWall = secondWall.AddHours(-1);
        var input = BuildInput(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, context,
            [Hour(context, zone, firstWall, 92), Hour(context, zone, secondWall, 93)],
            asOfUtc);
        var result = PrtgResourcePressureEvaluator.Evaluate(input);
        Assert.Equal(PrtgResourceDecisionKind.Hit, result.Kind);
        Assert.Equal(firstWall, result.Window[0].WallPeriodStart);
        Assert.Equal(secondWall, result.Window[1].WallPeriodStart);
    }

    [Fact]
    public void MissingLatestHourCannotBeFilledByOlderConsecutiveHistory()
    {
        var zone = TimeZoneInfo.Utc;
        var context = Context(zone);
        var asOf = DateTime.UtcNow;
        var current = new DateTime(asOf.Year, asOf.Month, asOf.Day, asOf.Hour, 0, 0, DateTimeKind.Unspecified);
        var latest = current.AddHours(-1);
        var hours = new[] { Hour(context, zone, latest.AddHours(-2), 99), Hour(context, zone, latest.AddHours(-1), 99) };
        var result = PrtgResourcePressureEvaluator.Evaluate(BuildInput(PrtgResourceFamily.Cpu,
            PrtgResourceSemantic.CpuUsed, context, hours, asOf));
        Assert.Equal(PrtgResourceDecisionKind.Insufficient, result.Kind);
        Assert.Equal("latest-two-completed-hours-missing", result.ReasonCode);
    }

    [Fact]
    public void PublicPureEvaluatorCannotAcceptClientConstructedFormalAuthorization()
    {
        var publicEvaluate = typeof(PrtgResourcePressureEvaluator).GetMethods()
            .Where(method => method.Name == nameof(PrtgResourcePressureEvaluator.Evaluate)).ToArray();
        Assert.Single(publicEvaluate);
        Assert.Single(publicEvaluate[0].GetParameters());

        var decision = PrtgResourcePressureEvaluator.Evaluate(
            Input(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, 95, 95));
        Assert.Equal(PrtgResourceDecisionMode.Hint, decision.Mode);
        Assert.Equal(PrtgResourceDecisionKind.Hit, decision.Kind);
    }

    [Fact]
    public void ChangedProfileFingerprintInvalidatesThePriorTrialFingerprint()
    {
        var original = Input(PrtgResourceFamily.Cpu, PrtgResourceSemantic.CpuUsed, 95, 95);
        var oldFingerprint = PrtgResourcePressureEvaluator.GetProfileFingerprint(
            PrtgResourceFamily.Cpu, original.AuthorizedContext);
        var changedContext = original.AuthorizedContext! with
        { ProfileFingerprint = new string('F', 64) };
        var changedHours = original.Hours!.Select(hour => Hour(changedContext, TimeZoneInfo.Utc,
            hour.WallPeriodStart, hour.SummaryAverage!.Value)).ToArray();
        var changed = original with { AuthorizedContext = changedContext, Hours = changedHours };
        var currentFingerprint = PrtgResourcePressureEvaluator.GetProfileFingerprint(
            PrtgResourceFamily.Cpu, changedContext);
        Assert.NotEqual(oldFingerprint, currentFingerprint);
        var result = PrtgResourcePressureEvaluator.Evaluate(changed);
        Assert.Equal(PrtgResourceDecisionMode.Hint, result.Mode);
        Assert.Equal(PrtgResourceDecisionKind.Hit, result.Kind);
    }

    private static PrtgResourceReadinessInput Input(PrtgResourceFamily family, PrtgResourceSemantic semantic,
        double earlier, double latest)
    {
        var zone = TimeZoneInfo.Utc;
        var ctx = Context(zone);
        var now = DateTime.UtcNow;
        var current = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Unspecified);
        var wallLatest = current.AddHours(-1);
        var wallPrevious = current.AddHours(-2);
        return BuildInput(family, semantic, ctx,
            [Hour(ctx, zone, wallPrevious, earlier), Hour(ctx, zone, wallLatest, latest)], now);
    }

    private static PrtgResourceReadinessInput NegativeInput(PrtgResourceFamily family, string caseId)
    {
        var semantic = family switch
        {
            PrtgResourceFamily.Cpu => PrtgResourceSemantic.CpuUsed,
            PrtgResourceFamily.Memory => PrtgResourceSemantic.MemoryUsed,
            _ => PrtgResourceSemantic.DiskRemaining
        };
        var input = Input(family, semantic, family == PrtgResourceFamily.Disk ? 5 : 95,
            family == PrtgResourceFamily.Disk ? 5 : 95);
        var baseline = PrtgResourcePeriodReadinessEvaluator.Evaluate(input);
        Assert.True(baseline.IsReady, $"Negative fixture baseline must be ready; got {baseline.ReasonCode}.");
        var hours = input.Hours!.ToArray();
        var first = hours[0];
        var latest = hours[1];
        switch (caseId)
        {
            case "missing-proof": hours[0] = first with { ContextProof = null }; break;
            case "missing-quality": hours[0] = first with { QualityGoodSlots = null }; break;
            case "missing-previous": hours = [latest]; break;
            case "missing-latest": hours = [first]; break;
            case "gap": hours = [first]; break;
            case "low-coverage": hours[0] = LowerCoverage(first); break;
            case "duplicate-hour": hours = [first, latest, latest]; break;
            case "false-average": hours[0] = first with { SummaryAverage = 1 }; break;
            case "false-coverage": hours[0] = first with { SummaryCoveragePercent = 75 }; break;
            case "foreign-sensor": hours[0] = first with { SensorObjid = first.SensorObjid + 1 }; break;
            case "source-drift": hours[0] = MutateProof(first, p => p with { SourceGeneration = "other" }); break;
            case "resource-drift": hours[0] = MutateProof(first, p => p with { ResourceGeneration = "other" }); break;
            case "channel-drift": hours[0] = MutateProof(first, p => p with { ChannelGeneration = "other" }); break;
            case "epoch-drift": hours[0] = MutateProof(first, p => p with { ResourceEpoch = "other" }); break;
            case "semantic-drift": hours[0] = MutateProof(first, p => p with { SemanticVersion = "other" }); break;
            case "strategy-drift": hours[0] = MutateProof(first, p => p with { StrategyVersion = "other" }); break;
            case "raw-zone-drift": hours[0] = MutateProof(first, p => p with { RawTimestampTimeZoneId = "other" }); break;
            case "analysis-zone-drift": hours[0] = MutateProof(first, p => p with { AnalysisTimeZoneId = "other" }); break;
            case "wrong-semantic": input = input with { Measurement = input.Measurement! with
                { Semantic = family == PrtgResourceFamily.Disk ? PrtgResourceSemantic.CpuUsed : PrtgResourceSemantic.DiskRemaining } }; break;
            case "wrong-unit": input = input with { Measurement = input.Measurement! with { Unit = "bytes" } }; break;
            case "unverified-semantic": input = input with { Measurement = input.Measurement! with { IsVerified = false } }; break;
            case "wrong-scale": input = input with { Measurement = input.Measurement! with { ScaleToPercent = 0.01 } }; break;
            case "quality-slot-mismatch": hours[0] = first with { QualityGoodSlots = [0, 1, 2] }; break;
            case "duplicate-physical-sample": hours[0] = MutateProof(first, p => p with
                { Slots = p.Slots.Select((s, i) => i == 1 ? s with { PhysicalIdHash = p.Slots[0].PhysicalIdHash } : s).ToArray() }); break;
            case "out-of-range-value": hours[0] = (MutateProof(first, p => p with
                { Slots = p.Slots.Select((s, i) => i == 0 ? s with { Value = 101 } : s).ToArray() })) with
                { SummaryAverage = family == PrtgResourceFamily.Disk ? 29 : 96.5 }; break;
            case "nonnumeric-value": hours[0] = MutateProof(first, p => p with
                { Slots = p.Slots.Select((s, i) => i == 0 ? s with { Value = double.NaN } : s).ToArray() }); break;
            case "open-hour": hours = [first, latest, latest with { WallPeriodStart = latest.WallPeriodStart.AddHours(1) }]; break;
            case "wall-key-has-offset": hours[0] = first with { WallPeriodStart = DateTime.SpecifyKind(first.WallPeriodStart, DateTimeKind.Utc) }; break;
            case "excessive-window":
            {
                var extra = Enumerable.Range(1, 71).Select(i => Hour(input.AuthorizedContext!, TimeZoneInfo.Utc,
                    first.WallPeriodStart.AddHours(-i), family == PrtgResourceFamily.Disk ? 5 : 95));
                hours = [.. hours, .. extra];
                break;
            }
            case "future-as-of": input = input with { AsOfUtc = DateTime.UtcNow.AddMinutes(1) }; break;
        }
        return input with { Hours = hours };
    }

    private static string ExpectedReason(string caseId) => caseId switch
    {
        "missing-proof" or "missing-quality" or "source-drift" or "resource-drift" or "channel-drift" or
            "epoch-drift" or "semantic-drift" or "strategy-drift" or "raw-zone-drift" or
            "analysis-zone-drift" or "duplicate-physical-sample" or "nonnumeric-value" => "invalid-hour-proof",
        "missing-previous" or "missing-latest" or "gap" => "latest-two-completed-hours-missing",
        "low-coverage" => "hour-coverage-below-75-percent",
        "duplicate-hour" => "duplicate-hour",
        "false-average" or "false-coverage" or "out-of-range-value" => "summary-does-not-match-proved-slots",
        "foreign-sensor" or "wall-key-has-offset" => "invalid-hour-identity",
        "wrong-semantic" or "wrong-unit" or "unverified-semantic" or "wrong-scale" => "invalid-authorized-context-or-semantics",
        "quality-slot-mismatch" => "quality-slots-do-not-match-proof",
        "open-hour" => "open-or-future-hour",
        "excessive-window" => "input-window-exceeds-bound",
        "future-as-of" => "future-as-of",
        _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown negative case.")
    };

    private static PrtgResourceHourlyEvidence LowerCoverage(PrtgResourceHourlyEvidence hour)
    {
        var proof = hour.ContextProof! with { Slots = hour.ContextProof!.Slots.Take(2).ToArray() };
        return hour with { ContextProof = proof, QualityGoodSlots = [0, 1], SummaryCoveragePercent = 50 };
    }

    private static PrtgResourceHourlyEvidence MutateProof(PrtgResourceHourlyEvidence hour,
        Func<PrtgTrustedSampleProof, PrtgTrustedSampleProof> change) => hour with
    { ContextProof = change(hour.ContextProof!) };

    private static PrtgResourceReadinessInput BuildInput(PrtgResourceFamily family,
        PrtgResourceSemantic semantic, PrtgResourceCurrentContext context,
        IReadOnlyCollection<PrtgResourceHourlyEvidence> hours, DateTime asOf) => new(family, context,
        new(semantic, "%", 1, true), hours, asOf);

    private static PrtgResourceCurrentContext Context(TimeZoneInfo zone)
    {
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        var start = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localToday.AddDays(-10), DateTimeKind.Unspecified), zone);
        return new(42, "source-1", "resource-1", "channel-1", "epoch-1", "semantic-1",
            "strategy-1", 15, start, TimeSpan.FromMinutes(15), "UTC", zone.Id);
    }

    private static PrtgResourceHourlyEvidence Hour(PrtgResourceCurrentContext context,
        TimeZoneInfo zone, DateTime wallStart, double value)
    {
        wallStart = DateTime.SpecifyKind(wallStart, DateTimeKind.Unspecified);
        var slots = Enumerable.Range(0, 4).Select(slot =>
        {
            var wall = wallStart.AddMinutes(slot * 15);
            var measured = TimeZoneInfo.ConvertTimeToUtc(wall, zone);
            return new PrtgTrustedSampleSlot(slot, (slot + 1).ToString("X32"), measured,
                measured.AddMinutes(1), value);
        }).ToArray();
        var sample = new PrtgTrustedSample(context.SensorObjid, value, context.SourceGeneration,
            context.ResourceGeneration, context.ChannelGeneration, context.ResourceEpoch,
            context.SemanticVersion, context.StrategyVersion, context.StrategyMinutes,
            context.StrategyEffectiveFromHour, slots[0].MeasuredAt, slots[0].ReceivedAt,
            context.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "physical",
            context.RawTimestampTimeZoneId, context.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new(context.SensorObjid, wallStart, proof, [0, 1, 2, 3], value, 100);
    }

    private static PrtgResourceHourlyEvidence HourFiveMinute(PrtgResourceCurrentContext context,
        TimeZoneInfo zone, DateTime wallStart, double value, int slotCount)
    {
        wallStart = DateTime.SpecifyKind(wallStart, DateTimeKind.Unspecified);
        var slots = Enumerable.Range(0, slotCount).Select(slot =>
        {
            var measured = TimeZoneInfo.ConvertTimeToUtc(wallStart.AddMinutes(slot * 5), zone);
            return new PrtgTrustedSampleSlot(slot, (slot + 1).ToString("X32"), measured,
                measured.AddMinutes(1), value);
        }).ToArray();
        var sample = new PrtgTrustedSample(context.SensorObjid, value, context.SourceGeneration,
            context.ResourceGeneration, context.ChannelGeneration, context.ResourceEpoch,
            context.SemanticVersion, context.StrategyVersion, context.StrategyMinutes,
            context.StrategyEffectiveFromHour, slots[0].MeasuredAt, slots[0].ReceivedAt,
            context.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "physical",
            context.RawTimestampTimeZoneId, context.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new(context.SensorObjid, wallStart, proof, slots.Select(s => s.Slot).ToArray(), value,
            slotCount * 100.0 / 12);
    }
}
