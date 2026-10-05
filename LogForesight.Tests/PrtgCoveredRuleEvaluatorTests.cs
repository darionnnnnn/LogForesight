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

    [Fact]
    public void 跨日連續Down_首日未達門檻不命中_翌日達門檻命中且Magnitude為當日重疊分鐘()
    {
        var proof = Proof((Start.AddMinutes(-15), "Down"), (Start.AddMinutes(20), "Up"));
        // 首日 15 分鐘不命中（門檻 30 分鐘）
        Assert.Empty(Evaluate(proof, Day.AddDays(-1)));

        // 翌日連續 35 分鐘命中（門檻 30 分鐘）
        var day2 = Evaluate(proof, Day);
        var finding = Assert.Single(day2);
        Assert.Equal(20, finding.Magnitude);
        Assert.Equal(35, (int?)typeof(PrtgFinding).GetProperty("ThresholdMagnitude")?.GetValue(finding));
        Assert.Equal(Start.AddMinutes(-15), finding.IncidentStartedAt);
        Assert.Contains("本日 20 分鐘", finding.Detail);
        Assert.Contains("連續可信 35 分鐘", finding.Detail);
        Assert.Contains($"當日故障區間：{Start:yyyy-MM-dd HH:mm:ss zzz}", finding.Detail);

        // 不得看到翌日恢復就回填首日
        Assert.Empty(Evaluate(proof, Day.AddDays(-1)));

        // consumer 映射驗證：Count 為 1（非分鐘），保留規則嚴重度與事故起點。
        var sig = PrtgFindingMapper.ToSignature(finding, Day);
        Assert.Equal(1, sig.Count);
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.Equal(Start.AddMinutes(-15), sig.PrtgIncidentStartedAt);
        Assert.StartsWith("prtg:down:10:", sig.EventKey);
        Assert.DoesNotContain("|", sig.EventKey);
    }

    [Fact]
    public void 連續Down門檻剛好30分命中_29分59秒不命中_全episode35但本日1分仍命中()
    {
        // 剛好 30 分鐘：23:45 至 00:15
        var hit30 = Proof((Start.AddMinutes(-15), "Down"), (Start.AddMinutes(15), "Up"));
        var f30 = Assert.Single(Evaluate(hit30, Day));
        Assert.Equal(15, f30.Magnitude);
        Assert.Contains("連續可信 30 分鐘", f30.Detail);

        // 29 分 59 秒：23:45:00 至 00:14:59
        var miss2959 = Proof((Start.AddMinutes(-15), "Down"), (Start.AddMinutes(15).AddSeconds(-1), "Up"));
        Assert.Empty(Evaluate(miss2959, Day));

        // 全 episode 35 分但本日 1 分鐘：23:26 至 00:01
        var hit1m = Proof((Start.AddMinutes(-34), "Down"), (Start.AddMinutes(1), "Up"));
        var f1m = Assert.Single(Evaluate(hit1m, Day));
        Assert.Equal(1, f1m.Magnitude);
        Assert.Equal(35, (int?)typeof(PrtgFinding).GetProperty("ThresholdMagnitude")?.GetValue(f1m));
        Assert.Contains("本日 1 分鐘", f1m.Detail);
        Assert.Contains("連續可信 35 分鐘", f1m.Detail);
        Assert.Equal(Start.AddMinutes(-34), f1m.IncidentStartedAt);
    }

    [Fact]
    public void 同狀態檢查點與ACK跨日延續_序列化重載後結果一致()
    {
        var proof = Proof(
            (Start.AddMinutes(-15), "Down"),
            (Start.AddMinutes(5), "Down"),
            (Start.AddMinutes(10), "Down (Acknowledged)"),
            (Start.AddMinutes(20), "Up"));

        var raw = System.Text.Json.JsonSerializer.Serialize(proof);
        var restored = System.Text.Json.JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(raw)!;

        var finding = Assert.Single(Evaluate(restored, Day));
        Assert.Equal(20, finding.Magnitude);
        Assert.Equal(Start.AddMinutes(-15), finding.IncidentStartedAt);
        Assert.True(finding.Acknowledged);
        Assert.Contains("本日 20 分鐘", finding.Detail);
        Assert.Contains("連續可信 35 分鐘", finding.Detail);
    }

    [Fact]
    public void Warning與Flapping仍只使用本日累積_不套用Down連續門檻()
    {
        var warningProof = Proof((Start.AddMinutes(-15), "Warning"), (Start.AddMinutes(20), "Up"));
        var rule = new KnownIssueRule { Id = "warning", PrtgRuleCode = "warning", PrtgThreshold = 30 };
        PrtgEvaluationResult Run(PrtgSensorTimelineEvidence proof) => PrtgCoveredRuleEvaluator.Evaluate(Day,
            [new(10, 100, "Up", "Ping", "availability")],
            new Dictionary<long, PrtgSensorTimelineEvidence> { [10] = proof }, [rule]);
        Assert.Empty(Run(warningProof));
        rule = new KnownIssueRule { Id = "warning", PrtgRuleCode = "warning", PrtgThreshold = 20 };
        Assert.Equal(20, Assert.Single(Run(warningProof)).Magnitude);

        var flapProof = Proof((Start.AddHours(-2), "Down"), (Start.AddHours(-1), "Up"),
            (Start.AddHours(1), "Down"), (Start.AddHours(2), "Up"));
        rule = new KnownIssueRule { Id = "flapping", PrtgRuleCode = "flapping", PrtgThreshold = 2 };
        Assert.Empty(Run(flapProof));
        rule = new KnownIssueRule { Id = "flapping", PrtgRuleCode = "flapping", PrtgThreshold = 1 };
        Assert.Equal(1, Assert.Single(Run(flapProof)).Magnitude);
    }

    [Fact]
    public void 覆蓋缺口一tick不能跨日累算_變更世代亦不可跨()
    {
        // 缺口 1 tick
        var proofWithGap = new PrtgSensorTimelineEvidence();
        proofWithGap.Bind(10, 1, "core", "sensor", Start.AddDays(-1));
        proofWithGap.Coverage =
        [
            new(10, Start.AddDays(-1), Start.AddTicks(-1), proofWithGap.SourceGeneration, proofWithGap.ResourceGeneration),
            new(10, Start, Start.AddDays(1), proofWithGap.SourceGeneration, proofWithGap.ResourceGeneration)
        ];
        proofWithGap.States =
        [
            new(10, Start.AddMinutes(-15), "Down", proofWithGap.SourceGeneration, proofWithGap.ResourceGeneration),
            new(10, Start.AddMinutes(20), "Up", proofWithGap.SourceGeneration, proofWithGap.ResourceGeneration)
        ];
        Assert.Empty(Evaluate(proofWithGap, Day));

        // 先證明同世代資料會命中，再隔離前一段，避免空證據也能通過反例。
        var proofGenChange = Proof((Start.AddMinutes(-15), "Down"), (Start.AddMinutes(20), "Up"));
        proofGenChange.Coverage =
        [
            new(10, Start.AddDays(-1), Start, proofGenChange.SourceGeneration, proofGenChange.ResourceGeneration),
            new(10, Start, Start.AddDays(1), proofGenChange.SourceGeneration, proofGenChange.ResourceGeneration)
        ];
        Assert.Single(Evaluate(proofGenChange, Day));
        var prior = proofGenChange.Coverage[0];
        proofGenChange.Coverage[0] = prior with { ResourceGeneration = "other-resource" };
        Assert.Empty(Evaluate(proofGenChange, Day));
        proofGenChange.Coverage[0] = prior with { SourceGeneration = "other-source" };
        Assert.Empty(Evaluate(proofGenChange, Day));
    }
}
