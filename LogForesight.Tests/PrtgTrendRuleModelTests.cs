using Xunit;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;

namespace LogForesight.Tests;

public sealed class PrtgTrendRuleModelTests
{
    [Fact]
    public void 三條Seed趨勢規則預設停用且通過RuleValidator()
    {
        var rules = KnownIssueSeed.CreateRules();
        var trendIds = new[]
        {
            "builtin-prtg-memory-used-trend",
            "builtin-prtg-cpu-load-trend",
            "builtin-prtg-ping-latency-trend"
        };

        foreach (var id in trendIds)
        {
            var rule = Assert.Single(rules, r => r.Id == id);
            Assert.False(rule.Enabled);
            Assert.Equal(0, rule.PrtgThreshold);
            Assert.NotNull(rule.PrtgTrendThresholds);
            Assert.True(rule.PrtgTrendThresholds.IsValid());
            Assert.Null(rule.PrtgDiskTrendThresholds);
            var expectedCategory = PrtgRuleCatalog.TrendSensorCategory(rule.PrtgRuleCode);
            Assert.Equal(expectedCategory, rule.PrtgSensorCategory);

            var outcome = RuleValidator.Validate(new List<KnownIssueRule> { rule });
            Assert.Empty(outcome.SkippedRules);
            Assert.Single(outcome.ValidRules);
        }
    }

    [Theory]
    [InlineData("builtin-prtg-memory-used-trend")]
    [InlineData("builtin-prtg-cpu-load-trend")]
    [InlineData("builtin-prtg-ping-latency-trend")]
    public void 趨勢規則各項錯誤案例皆被RuleValidator拒絕(string ruleId)
    {
        var baseRule = KnownIssueSeed.CreateRules().Single(r => r.Id == ruleId);

        // 1. PrtgThreshold != 0
        var badThreshold = Mutate(baseRule, nameof(KnownIssueRule.PrtgThreshold), 10);
        Assert.NotEmpty(RuleValidator.Validate(new List<KnownIssueRule> { badThreshold }).SkippedRules);

        // 2. 類別錯
        var badCategory = Mutate(baseRule, nameof(KnownIssueRule.PrtgSensorCategory), "hardware");
        Assert.NotEmpty(RuleValidator.Validate(new List<KnownIssueRule> { badCategory }).SkippedRules);

        // 3. 門檻 null
        var nullThresholds = Mutate(baseRule, nameof(KnownIssueRule.PrtgTrendThresholds), null);
        Assert.NotEmpty(RuleValidator.Validate(new List<KnownIssueRule> { nullThresholds }).SkippedRules);

        // 4. 門檻無效
        var invalidThresholds = Mutate(baseRule, nameof(KnownIssueRule.PrtgTrendThresholds),
            baseRule.PrtgTrendThresholds! with { WindowDays = 1, MinimumValidDays = 10 });
        Assert.NotEmpty(RuleValidator.Validate(new List<KnownIssueRule> { invalidThresholds }).SkippedRules);

        // 5. 同時填 PrtgDiskTrendThresholds
        var withDiskThresholds = Mutate(baseRule, nameof(KnownIssueRule.PrtgDiskTrendThresholds), PrtgDiskTrendThresholds.Provisional);
        Assert.NotEmpty(RuleValidator.Validate(new List<KnownIssueRule> { withDiskThresholds }).SkippedRules);
    }

    [Fact]
    public void Disk規則填PrtgTrendThresholds被拒()
    {
        var diskRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-disk-free-trend");
        var badDisk = Mutate(diskRule, nameof(KnownIssueRule.PrtgTrendThresholds), PrtgTrendThresholds.MemoryProvisional);
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { badDisk });
        Assert.NotEmpty(outcome.SkippedRules);
        Assert.Contains("PrtgTrendThresholds 僅三條趨勢規則可填", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void IsValueTrend_四真一假()
    {
        Assert.True(PrtgRuleCatalog.IsValueTrend(PrtgRuleEvaluator.RuleDiskFreeTrend));
        Assert.True(PrtgRuleCatalog.IsValueTrend(PrtgRuleEvaluator.RuleMemoryUsedTrend));
        Assert.True(PrtgRuleCatalog.IsValueTrend(PrtgRuleEvaluator.RuleCpuLoadTrend));
        Assert.True(PrtgRuleCatalog.IsValueTrend(PrtgRuleEvaluator.RulePingLatencyTrend));
        Assert.False(PrtgRuleCatalog.IsValueTrend(PrtgRuleEvaluator.RuleDown));
    }

    [Fact]
    public void TrendSensorCategory_對照正確()
    {
        Assert.Equal(PrtgSensorCategories.Disk, PrtgRuleCatalog.TrendSensorCategory(PrtgRuleEvaluator.RuleDiskFreeTrend));
        Assert.Equal(PrtgSensorCategories.Memory, PrtgRuleCatalog.TrendSensorCategory(PrtgRuleEvaluator.RuleMemoryUsedTrend));
        Assert.Equal(PrtgSensorCategories.Cpu, PrtgRuleCatalog.TrendSensorCategory(PrtgRuleEvaluator.RuleCpuLoadTrend));
        Assert.Equal(PrtgSensorCategories.Availability, PrtgRuleCatalog.TrendSensorCategory(PrtgRuleEvaluator.RulePingLatencyTrend));
        Assert.Null(PrtgRuleCatalog.TrendSensorCategory(PrtgRuleEvaluator.RuleDown));
        Assert.Null(PrtgRuleCatalog.TrendSensorCategory("unknown"));
    }

    [Fact]
    public void TrendFinding_Memory與Ping命中且語意不符或過期時回傳Null()
    {
        var completedDay = new DateOnly(2026, 10, 18);
        var rules = KnownIssueSeed.CreateRules();
        var memRule = rules.Single(r => r.Id == "builtin-prtg-memory-used-trend");
        var pingRule = rules.Single(r => r.Id == "builtin-prtg-ping-latency-trend");

        // Memory: 可用 % 每天下降 0.5（28 天：從 25.0% 降到 11.5%），轉成已用 % 後是上升（75.0% 升到 88.5%）
        var memDaily = new List<PrtgDailyValue>();
        for (var i = 27; i >= 0; i--)
        {
            var day = completedDay.AddDays(-i);
            var availPercent = 25.0 - (27 - i) * 0.5;
            memDaily.Add(new PrtgDailyValue(day, availPercent, 24));
        }

        var memSensor = new PrtgValueSensor(1001, 101, 11, "Memory Sensor", "SNMP Memory", PrtgSensorCategories.Memory);
        var memRow = new PrtgValueAssessmentRow(memSensor, PrtgValueSemantic.MemoryPercentFree, PrtgValueReason.Ready, 28, 28, completedDay, memDaily, 0);

        // 1. memory 命中且 detail 含「%」
        var memFinding = PrtgValueAssessment.TrendFinding(memRow, memRule, completedDay, out var memExplanation);
        Assert.NotNull(memFinding);
        Assert.Contains("%", memFinding!.Detail);
        Assert.Equal(PrtgRuleEvaluator.RuleMemoryUsedTrend, memFinding.RuleCode);
        Assert.Equal(PrtgSensorCategories.Memory, memFinding.SensorCategory);

        // 2. 把同一列的語意改成 CpuPercentUsed 但規則是 memory → null
        var mismatchedRow = memRow with { Semantic = PrtgValueSemantic.CpuPercentUsed };
        var mismatchedFinding = PrtgValueAssessment.TrendFinding(mismatchedRow, memRule, completedDay, out _);
        Assert.Null(mismatchedFinding);

        // 3. Ping: 28 天前段 21 天基準 20ms，近 7 天惡化升至 55~61ms
        var pingDaily = new List<PrtgDailyValue>();
        for (var i = 27; i >= 0; i--)
        {
            var day = completedDay.AddDays(-i);
            var val = i >= 7 ? 20.0 + (27 - i) * 0.1 : 55.0 + (6 - i) * 1.0;
            pingDaily.Add(new PrtgDailyValue(day, val, 24));
        }
        var pingSensor = new PrtgValueSensor(2001, 102, 12, "Ping Sensor", "Ping", PrtgSensorCategories.Availability);
        var pingRow = new PrtgValueAssessmentRow(pingSensor, PrtgValueSemantic.PingMs, PrtgValueReason.Ready, 28, 28, completedDay, pingDaily, 0);

        var pingFinding = PrtgValueAssessment.TrendFinding(pingRow, pingRule, completedDay, out var pingExplanation);
        Assert.NotNull(pingFinding);
        Assert.Contains(" ms", pingFinding!.Detail);
        Assert.Equal(PrtgRuleEvaluator.RulePingLatencyTrend, pingFinding.RuleCode);
        Assert.Equal(PrtgSensorCategories.Availability, pingFinding.SensorCategory);

        // 4. LatestDay 不是評估日 → null
        var outdatedFinding = PrtgValueAssessment.TrendFinding(memRow, memRule, completedDay.AddDays(1), out _);
        Assert.Null(outdatedFinding);
    }

    private static KnownIssueRule Mutate(KnownIssueRule rule, string property, object? value)
    {
        var clone = System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(
            System.Text.Json.JsonSerializer.Serialize(rule))!;
        typeof(KnownIssueRule).GetProperty(property)!.SetValue(clone, value);
        return clone;
    }
}
