using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSourceWindowProducerTests
{
    private static readonly DateTime Day = new(2026, 9, 20);
    private static readonly DateTimeOffset Start = new(Day);
    private static readonly KnownIssueRule Down = new() { Id = "down", PrtgRuleCode = "down", PrtgThreshold = 30,
        Severity = IssueSeverity.High, ElevatesDayRisk = true };

    [Fact]
    public void DisplayOnlyLegacyFullDayNeverBecomesTypedSourceWindow()
    {
        var signature = PrtgFindingMapper.ToSignature(new(100, 10, "down", "legacy", 60, Down), Day, 7);
        Assert.Equal("00:00", signature.FirstSeen);
        Assert.Equal("23:59", signature.LastSeen);
        Assert.Empty(signature.SourceObservations);
    }

    [Fact]
    public void QualifiedDownWindowReachesRealMapperWithCanonicalHostAndUnknownNativeResourceReference()
    {
        var proof = new PrtgSensorTimelineEvidence();
        proof.Bind(10, 7, "source", "resource", Start.AddDays(-1));
        proof.Accept(Start.AddDays(-1), Start.AddDays(1), new[]
        {
            new PrtgTimedState(10, Start.AddHours(1), "Down", proof.SourceGeneration, proof.ResourceGeneration),
            new PrtgTimedState(10, Start.AddHours(3), "Up", proof.SourceGeneration, proof.ResourceGeneration)
        });
        var finding = Assert.Single(PrtgCoveredRuleEvaluator.Evaluate(Day,
            [new(10, 100, "Up", "Ping", "availability")],
            new Dictionary<long, PrtgSensorTimelineEvidence> { [10] = proof }, [Down]));
        var signature = PrtgFindingMapper.ToSignature(finding, Day, 7);
        var observation = Assert.Single(signature.SourceObservations);
        Assert.Equal(Start.AddHours(1).ToUniversalTime(), observation.WindowStartUtc);
        Assert.Equal(Start.AddHours(3).ToUniversalTime(), observation.WindowEndUtc);
        Assert.Equal("host-id:7", observation.ExactHostKey);
        Assert.Equal(SourceEvidenceKind.Prtg, observation.SourceKind);
        Assert.Equal(SourceResourceScope.Unknown, observation.ResourceScope);
        Assert.Null(observation.ExactResourceKey);
        Assert.Null(observation.SourceReference);
        Assert.False(observation.HasExactReference);
        Assert.Equal(64, observation.ProjectionFingerprint!.Length);
        var restored = SourceEvidence.DeserializeObservations(SourceEvidence.SerializeObservations(signature.SourceObservations));
        Assert.Equal(observation.WindowStartUtc, Assert.Single(restored).WindowStartUtc);
    }

    [Fact]
    public void InvalidOrOffsetWindowsAreRejectedAndNoHostIsInvented()
    {
        var begin = Start.ToUniversalTime();
        var finding = new PrtgFinding(100, 10, "down", "windows", 60, Down)
        {
            EvidenceWindows = [new(begin, begin), new(begin.AddHours(1), begin),
                new(begin.ToOffset(TimeSpan.FromHours(8)), begin.AddHours(1)), new(begin, begin.AddHours(1))]
        };
        var evidence = Assert.Single(PrtgFindingMapper.ToSignature(finding, Day).SourceObservations);
        Assert.Null(evidence.ExactHostKey);
        Assert.Equal(begin, evidence.WindowStartUtc);
        Assert.Equal(begin.AddHours(1), evidence.WindowEndUtc);
    }

    [Fact]
    public void DisjointWarningPeriodsStayDisjointAndBounded()
    {
        var proof = new PrtgSensorTimelineEvidence();
        proof.Bind(10, 7, "source", "resource", Start.AddDays(-1));
        proof.Accept(Start.AddDays(-1), Start.AddDays(1), new[]
        {
            new PrtgTimedState(10, Start.AddHours(1), "Warning", proof.SourceGeneration, proof.ResourceGeneration),
            new PrtgTimedState(10, Start.AddHours(2), "Up", proof.SourceGeneration, proof.ResourceGeneration),
            new PrtgTimedState(10, Start.AddHours(4), "Warning", proof.SourceGeneration, proof.ResourceGeneration),
            new PrtgTimedState(10, Start.AddHours(5), "Up", proof.SourceGeneration, proof.ResourceGeneration)
        });
        var rule = new KnownIssueRule { Id = "warning", PrtgRuleCode = "warning", PrtgThreshold = 30 };
        var finding = Assert.Single(PrtgCoveredRuleEvaluator.Evaluate(Day,
            [new(10, 100, "Up", "WMI", "hardware")],
            new Dictionary<long, PrtgSensorTimelineEvidence> { [10] = proof }, [rule]));
        var signature = PrtgFindingMapper.ToSignature(finding, Day, 7);
        Assert.Equal(2, signature.SourceObservations.Count);
        Assert.Equal(Start.AddHours(2).ToUniversalTime(), signature.SourceObservations[0].WindowEndUtc);
        Assert.Equal(Start.AddHours(4).ToUniversalTime(), signature.SourceObservations[1].WindowStartUtc);
        Assert.False(signature.SourceObservationsTruncated);
    }

    [Fact]
    public void ExcessWindowsKeepExplicitTruncationWithinEightKiB()
    {
        var begin = Start.ToUniversalTime();
        var finding = new PrtgFinding(100, 10, "warning", "many windows", 60, Down)
        {
            EvidenceWindows = Enumerable.Range(0, 100).Select(i => new PrtgEvidenceWindow(
                begin.AddMinutes(i * 2), begin.AddMinutes(i * 2 + 1))).ToArray()
        };
        var signature = PrtgFindingMapper.ToSignature(finding, Day, 7);
        Assert.True(signature.SourceObservationsTruncated);
        Assert.InRange(signature.SourceObservations.Count, 1, 64);
        var raw = SourceEvidence.SerializeObservations(signature.SourceObservations)!;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(raw) <= SourceEvidence.MaximumSerializedBytes);
    }
}
