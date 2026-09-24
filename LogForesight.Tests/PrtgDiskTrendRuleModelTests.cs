using System.Reflection;
using Xunit;
using LogForesight.Core.Analysis;
using LogForesight.Core.Service;
using LogForesight.Core.Models;

namespace LogForesight.Tests;

public sealed class PrtgDiskTrendRuleModelTests
{
    [Fact]
    public void Seed新增停用磁碟趨勢規則並使用獨立暫定門檻()
    {
        Assert.Equal(8, KnownIssueSeed.Version);
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-disk-free-trend");

        Assert.False(rule.Enabled);
        Assert.Equal(PrtgRuleEvaluator.RuleDiskFreeTrend, rule.PrtgRuleCode);
        Assert.Equal(0, rule.PrtgThreshold);
        Assert.Equal(PrtgSensorCategories.Disk, rule.PrtgSensorCategory);
        Assert.Equal(PrtgDiskTrendThresholds.Provisional, rule.PrtgDiskTrendThresholds);
        Assert.Empty(RuleValidator.Validate(new() { rule }).SkippedRules);
    }

    [Theory]
    [InlineData("LowWaterPercent", "NaN")]
    [InlineData("LowWaterPercent", "101")]
    [InlineData("MinimumDeclinePercentagePointsPerDay", "0")]
    [InlineData("MaximumDaysToDepletion", "Infinity")]
    [InlineData("MinimumValidDays", "1")]
    [InlineData("RecentWindowDays", "800")]
    [InlineData("MinimumDecliningDayRatio", "0")]
    public void Validator拒絕非有限或越界的磁碟趨勢門檻(string field, string value)
    {
        var parsed = value switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, _ => double.Parse(value) };
        var rule = MutateThreshold(field, parsed);

        Assert.NotEmpty(RuleValidator.Validate(new() { rule }).SkippedRules);
    }

    [Fact]
    public void 磁碟趨勢規則必須使用專用門檻與disk分類且不能混用狀態門檻()
    {
        var seed = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-disk-free-trend");
        AssertInvalid(seed, "PrtgThreshold", 1);
        AssertInvalid(seed, "PrtgSensorCategory", "hardware");
        AssertInvalid(seed, "PrtgDiskTrendThresholds", null);

        var stateRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-warning-disk");
        AssertInvalid(stateRule, "PrtgDiskTrendThresholds", PrtgDiskTrendThresholds.Provisional);
    }

    [Fact]
    public void 匯入內容比較會追蹤趨勢門檻並保留使用者Enabled()
    {
        var seed = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-disk-free-trend");
        var customized = Mutate(seed, "PrtgDiskTrendThresholds",
            PrtgDiskTrendThresholds.Provisional with { LowWaterPercent = 18 });
        customized = Mutate(customized, "Enabled", true);
        customized = Mutate(customized, "ModifiedBy", 42L);

        var preservePlan = RuleImportPlanner.BuildPlan(new() { customized }, new() { seed }, overwriteBuiltin: false);
        Assert.Equal(RuleImportAction.SkippedModifiedBuiltin, Assert.Single(preservePlan.Items).Action);

        var overwritePlan = RuleImportPlanner.BuildPlan(new() { customized }, new() { seed }, overwriteBuiltin: true);
        var restored = Assert.Single(overwritePlan.ResultingRules);
        Assert.True(restored.Enabled);
        Assert.Equal(PrtgDiskTrendThresholds.Provisional, restored.PrtgDiskTrendThresholds);
    }

    private static void AssertInvalid(KnownIssueRule rule, string property, object? value) =>
        Assert.NotEmpty(RuleValidator.Validate(new() { Mutate(rule, property, value) }).SkippedRules);

    private static KnownIssueRule MutateThreshold(string field, double value)
    {
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-disk-free-trend");
        var thresholds = rule.PrtgDiskTrendThresholds! with { };
        return Mutate(rule, nameof(KnownIssueRule.PrtgDiskTrendThresholds), Mutate(thresholds, field, value));
    }

    private static KnownIssueRule Mutate(KnownIssueRule rule, string property, object? value)
    {
        var clone = Deserialize(rule);
        typeof(KnownIssueRule).GetProperty(property)!.SetValue(clone, value);
        return clone;
    }

    private static PrtgDiskTrendThresholds Mutate(PrtgDiskTrendThresholds thresholds, string property, object value)
    {
        var clone = thresholds with { };
        var targetType = typeof(PrtgDiskTrendThresholds).GetProperty(property)!.PropertyType;
        typeof(PrtgDiskTrendThresholds).GetProperty(property)!.SetValue(clone, Convert.ChangeType(value, targetType));
        return clone;
    }

    private static KnownIssueRule Deserialize(KnownIssueRule rule) =>
        System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(System.Text.Json.JsonSerializer.Serialize(rule))!;
}
