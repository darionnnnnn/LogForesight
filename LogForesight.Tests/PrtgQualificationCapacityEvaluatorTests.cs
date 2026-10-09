using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgQualificationCapacityEvaluatorTests
{
    private static PrtgQualificationCapacityPilot Pilot(int sensors = 5, string? source = null,
        string? scope = null, string? shape = null, DateTimeOffset? at = null) => new(
        source ?? "source-fingerprint", scope ?? "scope-fingerprint",
        shape ?? PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), "native-version-1",
        PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), at ?? DateTimeOffset.UtcNow,
        sensors, sensors, sensors, sensors, sensors, sensors,
        sensors * 2, 2, RawIdentityAndTimeValidated: true);

    [Fact]
    public void MissingOrStaleCapacityEvidenceNeverAdmitsAJob()
    {
        var now = DateTimeOffset.UtcNow;
        var missing = PrtgQualificationCapacityEvaluator.Evaluate(10, 72, 3, null,
            "source-fingerprint", "scope-fingerprint", PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);
        var stale = PrtgQualificationCapacityEvaluator.Evaluate(10, 72, 3,
            Pilot(at: now.AddDays(-8)), "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);

        Assert.False(missing.Admitted);
        Assert.Equal("qualification-capacity-pilot-missing-or-stale", missing.Reason);
        Assert.Equal(120, missing.WorstCaseTableRequests); // 10 sensors × 3 attempts × 4 table/property calls.
        Assert.Equal(30, missing.WorstCaseHistoricRequests);
        Assert.False(stale.Admitted);
    }

    [Fact]
    public void PilotMustMatchSourceScopeAndExactFiveRequestShapeAndValidatedRawTime()
    {
        var now = DateTimeOffset.UtcNow;
        var shape = PrtgQualificationCapacityEvaluator.RequestShapeFingerprint();
        var mismatched = Pilot(source: "other-source");
        var wrongCount = Pilot() with { HistoricXmlGets = 2 };
        var unknownTime = Pilot() with { RawIdentityAndTimeValidated = false };
        var differentApplication = Pilot() with { ApplicationRuntimeFingerprint = "other-runtime" };

        Assert.False(PrtgQualificationCapacityEvaluator.ValidPilot(mismatched, "source-fingerprint",
            "scope-fingerprint", shape, PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now));
        Assert.False(PrtgQualificationCapacityEvaluator.ValidPilot(wrongCount, "source-fingerprint",
            "scope-fingerprint", shape, PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now));
        Assert.False(PrtgQualificationCapacityEvaluator.ValidPilot(unknownTime, "source-fingerprint",
            "scope-fingerprint", shape, PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now));
        Assert.False(PrtgQualificationCapacityEvaluator.ValidPilot(differentApplication, "source-fingerprint",
            "scope-fingerprint", shape, PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now));
    }

    [Fact]
    public void InvalidFleetInputIsRejectedWithoutReportingFabricatedVerifiedCost()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(15_001, 72, 1, Pilot(at: now),
            "source-fingerprint", "scope-fingerprint", PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);

        Assert.False(decision.Admitted);
        Assert.Equal("capacity-input-invalid", decision.Reason);
        Assert.Equal(0, decision.WorstCaseHistoricRequests);
        Assert.Equal(0, decision.WorstCaseTableRequests);
    }

    [Theory]
    [InlineData(15_000, 72, 3)]
    [InlineData(15_000, 720, 3)]
    public void FullFleetWorstCaseRetriesDoNotFitUnprovenWindow(int sensors, int hours, int attempts)
    {
        var now = DateTimeOffset.UtcNow;
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(sensors, hours, attempts,
            Pilot(at: now), "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);

        Assert.False(decision.Admitted);
        Assert.Equal((long)sensors * attempts, decision.WorstCaseHistoricRequests);
    }

    [Fact]
    public void CostUsesAllRetriesRawRequestsTableShapeAndWindowDrain()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(100, 72, 3,
            Pilot(at: now), "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);

        Assert.True(decision.Admitted);
        Assert.Equal(300, decision.WorstCaseHistoricRequests);
        Assert.Equal(1200, decision.WorstCaseTableRequests);
        Assert.True(decision.RequiredSeconds >= PrtgQualificationCapacityEvaluator.InitialWindowDrainSeconds);
    }

    [Fact]
    public void SlowProbeIsAddedAfterHistoricLaneWaitInsteadOfOverlapped()
    {
        var now = DateTimeOffset.UtcNow;
        var pilot = Pilot(sensors: 5, at: now) with { ObservedElapsedSeconds = 150 };
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(10, 72, 1, pilot,
            "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 100);

        var expectedHistoric = 10 / (PrtgQualificationCapacityEvaluator.QualificationHistoricRequestsPerMinute / 60d * 0.75);
        var expectedProbe = 10 * (pilot.MaximumRequestSeconds + 4d / 100) / 0.75;
        var expected = PrtgQualificationCapacityEvaluator.InitialWindowDrainSeconds + expectedHistoric + expectedProbe;
        Assert.True(decision.Admitted);
        Assert.InRange(Math.Abs(decision.RequiredSeconds - expected), 0, 0.000001);
        Assert.True(decision.RequiredSeconds > PrtgQualificationCapacityEvaluator.InitialWindowDrainSeconds + expectedHistoric);
    }

    [Fact]
    public void FiveSensorPilotKeepsAggregateCountsAndUsesSlowestSensorForConservativeCost()
    {
        var now = DateTimeOffset.UtcNow;
        var pilot = Pilot(sensors: 5, at: now) with { ObservedElapsedSeconds = 21, MaximumRequestSeconds = 9 };
        Assert.True(PrtgQualificationCapacityEvaluator.ValidPilot(pilot, "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(),
            PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now));
        Assert.Equal(5, pilot.SensorTableGets);

        var decision = PrtgQualificationCapacityEvaluator.Evaluate(10, 72, 1, pilot,
            "source-fingerprint", "scope-fingerprint", PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 100);
        var expectedHistoric = 10 / (PrtgQualificationCapacityEvaluator.QualificationHistoricRequestsPerMinute / 60d * 0.75);
        var expectedMeasured = 10 * (9 + 4d / 100) / 0.75;
        Assert.InRange(Math.Abs(decision.RequiredSeconds -
            (PrtgQualificationCapacityEvaluator.InitialWindowDrainSeconds + expectedHistoric + expectedMeasured)), 0, 0.000001);
    }

    [Fact]
    public void FifteenThousandSensorsRequireOneAttemptAndAtLeastTheLongBoundedWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var pilot = Pilot(at: now);
        var fullFleetOneAttempt = PrtgQualificationCapacityEvaluator.Evaluate(15_000, 720, 1,
            pilot, "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 2);
        var fullFleetThreeAttempts = PrtgQualificationCapacityEvaluator.Evaluate(15_000, 720, 3,
            pilot, "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 2);

        Assert.True(fullFleetOneAttempt.Admitted, fullFleetOneAttempt.Reason);
        Assert.False(fullFleetThreeAttempts.Admitted);
        Assert.Equal(60_000, fullFleetOneAttempt.WorstCaseTableRequests);
        Assert.Equal(15_000, fullFleetOneAttempt.WorstCaseHistoricRequests);
    }

    [Fact]
    public void UnknownTableRateFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(5, 72, 3,
            Pilot(at: now), "source-fingerprint", "scope-fingerprint",
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 0);
        Assert.False(decision.Admitted);
        Assert.Equal("capacity-input-invalid", decision.Reason);
    }

    [Fact]
    public void ZeroEligibleBindingsDoesNotRequireRawCapacityPilotButKeepsNoCostOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(0, 72, 1, null,
            "source-fingerprint", "scope-fingerprint", PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), now, 1);

        Assert.True(decision.Admitted);
        Assert.Equal("no-binding-raw-qualification-requests-required", decision.Reason);
        Assert.Equal(0, decision.WorstCaseHistoricRequests);
    }

    [Theory]
    [InlineData(28, 1, false)]
    [InlineData(18.5, 1, true)]
    [InlineData(18.500001, 1, false)]
    [InlineData(2, 0.1, false)]
    [InlineData(2, 0.2, true)]
    public void EachProbeMustFitSourcePlusPurposePacingWithHeadroomEvenWhenWholeWaveFits(
        double elapsed, double tableRate, bool expectedAdmitted)
    {
        var now = DateTimeOffset.UtcNow;
        var pilot = Pilot(sensors: 1, at: now) with
        {
            ObservedElapsedSeconds = elapsed,
            MaximumRequestSeconds = elapsed
        };
        var decision = PrtgQualificationCapacityEvaluator.Evaluate(1, 720, 1, pilot,
            "source-fingerprint", "scope-fingerprint", PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(),
            now, tableRate);
        Assert.True(decision.RequiredSeconds < decision.AvailableSeconds);
        Assert.Equal(expectedAdmitted, decision.Admitted);
        if (!expectedAdmitted)
            Assert.Equal("qualification-probe-deadline-headroom-insufficient", decision.Reason);
        Assert.Equal(1, decision.WorstCaseHistoricRequests);
        Assert.Equal(4, decision.WorstCaseTableRequests);
    }
}
