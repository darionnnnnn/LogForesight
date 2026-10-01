using LogForesight.Core.Analysis;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgCoveredRuleEvaluatorTests
{
    private static readonly DateTime Day = new(2026, 9, 20);
    private static readonly DateTimeOffset Start = new(Day);
    private static readonly KnownIssueRule Down = new() { Id = "down", PrtgRuleCode = "down", PrtgThreshold = 30,
        Severity = IssueSeverity.High, ElevatesDayRisk = true };
    private static PrtgSensorTimelineEvidence Proof(params (DateTimeOffset At, string Status)[] states)
    {
        var proof = new PrtgSensorTimelineEvidence();
        proof.Bind(10, 1, "core", "sensor", Start.AddDays(-1));
        proof.Accept(Start.AddDays(-1), Start.AddDays(4), states.Select(s =>
            new PrtgTimedState(10, s.At, s.Status, proof.SourceGeneration, proof.ResourceGeneration)));
        return proof;
    }
    private static PrtgEvaluationResult Evaluate(PrtgSensorTimelineEvidence proof, DateTime? day = null) =>
        PrtgCoveredRuleEvaluator.Evaluate(day ?? Day, [new(10, 100, "Down", "Ping", "availability")],
            new Dictionary<long, PrtgSensorTimelineEvidence> { [10] = proof }, [Down]);

    [Fact]
    public void 三天無新訊息持續Down_正式評估與重啟後身分相同()
    {
        var proof = Proof((Start.AddHours(-2), "Down"));
        var raw = System.Text.Json.JsonSerializer.Serialize(proof);
        var restored = System.Text.Json.JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(raw)!;
        for (var i = 0; i < 3; i++)
        {
            var finding = Assert.Single(Evaluate(restored, Day.AddDays(i)));
            Assert.Equal(1440, finding.Magnitude);
            Assert.Equal(Start.AddHours(-2), finding.IncidentStartedAt);
            Assert.Equal(PrtgFindingMapper.ToSignature(Assert.Single(Evaluate(proof)), Day).EventKey,
                PrtgFindingMapper.ToSignature(finding, Day.AddDays(i)).EventKey);
        }
    }
    [Fact]
    public void 同狀態檢查點及ACK不重設事故起點或分割故障時長()
    {
        var proof = Proof((Start.AddHours(-1), "Down"), (Start.AddHours(4), "Down"),
            (Start.AddHours(12), "Down (Acknowledged)"));
        var finding = Assert.Single(Evaluate(proof));
        Assert.Equal(1440, finding.Magnitude);
        Assert.Equal(Start.AddHours(-1), finding.IncidentStartedAt);
        Assert.True(PrtgFindingMapper.ToSignature(finding, Day).ElevatesDayRisk);
    }
    [Fact]
    public void 当日恢復後仍保存先前可證故障_再發起點不同()
    {
        var proof = Proof((Start.AddHours(1), "Down"), (Start.AddHours(3), "Up"));
        var finding = Assert.Single(Evaluate(proof));
        Assert.Equal(120, finding.Magnitude);
        var next = Proof((Start.AddHours(1), "Down"), (Start.AddHours(3), "Up"), (Start.AddHours(8), "Down"));
        Assert.Equal(Start.AddHours(8), Assert.Single(Evaluate(next)).IncidentStartedAt);
    }
    [Fact]
    public void 今日鏡像與空訊息不能回填昨日Down()
    {
        var proof = Proof((Start.AddDays(1).AddHours(1), "Down"));
        Assert.Empty(Evaluate(proof));
    }
    [Fact]
    public void 來源資源或歸戶改變清除連續證據()
    {
        var proof = Proof((Start.AddHours(-1), "Down"));
        var generation = proof.ResourceGeneration;
        proof.Bind(10, 2, "core", "sensor", Start);
        Assert.NotEqual(generation, proof.ResourceGeneration);
        Assert.Empty(Evaluate(proof));
        proof.Bind(10, 2, "other-core", "sensor", Start);
        Assert.Empty(proof.Coverage);
    }
    [Fact]
    public void 缺口不能跨越累算到門檻()
    {
        var proof = Proof((Start, "Down"));
        proof.Coverage = [new(10, Start, Start.AddMinutes(20), proof.SourceGeneration, proof.ResourceGeneration),
            new(10, Start.AddHours(1), Start.AddDays(1), proof.SourceGeneration, proof.ResourceGeneration)];
        Assert.Empty(Evaluate(proof));
    }
}
