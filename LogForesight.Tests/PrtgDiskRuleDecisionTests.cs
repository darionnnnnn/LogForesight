using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskRuleDecisionTests
{
    private static readonly DateOnly End = new(2026, 9, 20);
    private const long Sensor = 12345;
    private const long Device = 678;
    private const int Host = 42;

    private static PrtgDiskTrendDay[] Falling() => Enumerable.Range(0, 30)
        .Select(i => new PrtgDiskTrendDay(End.AddDays(i - 29), 45 - i)).ToArray();

    private static KnownIssueRule Rule(bool enabled = true, string code = "disk_free_trend", PrtgDiskTrendThresholds? thresholds = null) => new()
    {
        Id = "test-disk-trend", Enabled = enabled, Platform = "prtg", PrtgRuleCode = code,
        PrtgSensorCategory = PrtgSensorCategories.Disk, PrtgDiskTrendThresholds = thresholds ?? PrtgDiskTrendThresholds.Provisional,
        Category = IssueCategory.Storage, Severity = IssueSeverity.High, ElevatesDayRisk = true,
        Description = "磁碟空間持續下降"
    };

    private static PrtgDiskRuleDecisionInput Input(
        string? category = PrtgSensorCategories.Disk,
        long host = Host,
        PrtgValueReadinessResult? readiness = null,
        PrtgDiskSemanticEvidenceValidity? validity = null,
        IReadOnlyCollection<PrtgDiskTrendDay>? series = null,
        KnownIssueRule? rule = null,
        bool omitRule = false,
        PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Formal) => new(
            Sensor, Device, host, category,
            readiness ?? new(Sensor, PrtgValueReadinessStatus.Ready, 28, 28, 336, 12,
                End.ToDateTime(TimeOnly.MinValue), true, "ready"),
            validity ?? new() { IsValid = true, Evidence = Evidence() },
            series ?? Falling(), End, omitRule ? null : rule ?? Rule(), mode);

    private static PrtgDiskSemanticEvidence Evidence() => new(
        Sensor, Device, Host, "SNMP Disk Free", "channel-1", "Free Space", "Percent", 1, "decreasing",
        PrtgDiskSemanticEvidenceSource.Manual, 1, "Verified percent free", DateTime.UtcNow, "v1");

    [Theory]
    [InlineData("availability", PrtgDiskDecisionExclusion.NotDisk)]
    [InlineData("", PrtgDiskDecisionExclusion.NotDisk)]
    public void Rejects_non_disk_categories(string? category, PrtgDiskDecisionExclusion expected) =>
        Assert.Equal(expected, PrtgDiskRuleDecision.Evaluate(Input(category: category)).Exclusion);

    [Fact]
    public void Rejects_unmapped_host() =>
        Assert.Equal(PrtgDiskDecisionExclusion.HostUnmapped,
            PrtgDiskRuleDecision.Evaluate(Input(host: 0)).Exclusion);

    [Theory]
    [InlineData(PrtgValueReadinessStatus.InsufficientData, true, PrtgDiskDecisionExclusion.ReadinessNotReady)]
    [InlineData(PrtgValueReadinessStatus.Unknown, true, PrtgDiskDecisionExclusion.ReadinessNotReady)]
    [InlineData(PrtgValueReadinessStatus.Ready, false, PrtgDiskDecisionExclusion.SemanticNotReady)]
    public void Rejects_readiness_gates(PrtgValueReadinessStatus status, bool semanticReady, PrtgDiskDecisionExclusion expected)
    {
        var readiness = new PrtgValueReadinessResult(Sensor, status, 28, 28, 336, 12, null, semanticReady, "test");
        Assert.Equal(expected, PrtgDiskRuleDecision.Evaluate(Input(readiness: readiness)).Exclusion);
    }

    [Fact]
    public void Rejects_invalid_or_mismatched_semantic_evidence()
    {
        var invalid = new PrtgDiskSemanticEvidenceValidity { IsValid = false, InvalidReason = "stale" };
        Assert.Equal(PrtgDiskDecisionExclusion.EvidenceInvalid,
            PrtgDiskRuleDecision.Evaluate(Input(validity: invalid)).Exclusion);
        var mismatched = new PrtgDiskSemanticEvidenceValidity { IsValid = true, Evidence = Evidence() with { HostId = 99 } };
        Assert.Equal(PrtgDiskDecisionExclusion.EvidenceInvalid,
            PrtgDiskRuleDecision.Evaluate(Input(validity: mismatched)).Exclusion);
    }

    [Fact]
    public void Rejects_missing_or_wrong_rule_and_missing_threshold()
    {
        Assert.Equal(PrtgDiskDecisionExclusion.RuleMissing,
            PrtgDiskRuleDecision.Evaluate(Input(omitRule: true)).Exclusion);
        Assert.Equal(PrtgDiskDecisionExclusion.RuleMismatch,
            PrtgDiskRuleDecision.Evaluate(Input(rule: Rule(code: "warning"))).Exclusion);
        var noThreshold = new KnownIssueRule
        {
            Id = "missing-threshold", Platform = "prtg", PrtgRuleCode = "disk_free_trend",
            PrtgSensorCategory = PrtgSensorCategories.Disk
        };
        Assert.Equal(PrtgDiskDecisionExclusion.RuleMismatch,
            PrtgDiskRuleDecision.Evaluate(Input(rule: noThreshold)).Exclusion);
        Assert.Equal(PrtgDiskDecisionExclusion.RulePlatformMismatch,
            PrtgDiskRuleDecision.Evaluate(Input(rule: WithPlatformForTest(Rule(), "windows"))).Exclusion);
    }

    [Fact]
    public void Disabled_rule_is_previewable_but_not_formal()
    {
        var preview = PrtgDiskRuleDecision.Evaluate(Input(rule: Rule(false), mode: PrtgDiskDecisionMode.Preview));
        Assert.True(preview.WouldHit);
        Assert.Null(preview.Finding);
        Assert.Equal(PrtgDiskDecisionExclusion.RuleDisabled,
            PrtgDiskRuleDecision.Evaluate(Input(rule: Rule(false))).Exclusion);
    }

    [Fact]
    public void Formal_hit_creates_finding_with_stable_sensor_signature_and_completed_day_cap()
    {
        var data = Falling().Append(new PrtgDiskTrendDay(End.AddDays(1), 0)).ToArray();
        var result = PrtgDiskRuleDecision.Evaluate(Input(series: data));
        Assert.True(result.WouldHit);
        var finding = Assert.IsType<PrtgFinding>(result.Finding);
        Assert.Equal(Sensor, finding.SensorObjid);
        Assert.Equal(Device, finding.DeviceObjid);
        Assert.Equal("disk_free_trend", finding.RuleCode);
        Assert.False(finding.Acknowledged);
        Assert.Contains("有效日 30 日", finding.Detail);
        Assert.Contains("336 小時", finding.Detail);
        Assert.Contains("資料截至 2026-09-20", finding.Detail);
        Assert.DoesNotContain("2026-09-21", finding.Detail);
        var signature = PrtgFindingMapper.ToSignature(finding, End.ToDateTime(TimeOnly.MinValue));
        Assert.Equal($"prtg:disk_free_trend:{Sensor}", signature.EventKey);
    }

    [Fact]
    public void Nonhit_and_insufficient_series_have_structured_exclusions()
    {
        var flat = Falling().Select(x => x with { AvailablePercent = 10 }).ToArray();
        var noHit = PrtgDiskRuleDecision.Evaluate(Input(series: flat));
        Assert.Equal(PrtgDiskDecisionExclusion.TrendNoHit, noHit.Exclusion);
        Assert.Null(noHit.Finding);
        var shortSeries = Falling().Take(6).ToArray();
        var insufficient = PrtgDiskRuleDecision.Evaluate(Input(series: shortSeries));
        Assert.Equal(PrtgDiskDecisionExclusion.TrendInsufficientData, insufficient.Exclusion);
        Assert.NotNull(insufficient.Trend);
    }

    [Theory]
    [InlineData(PrtgDiskDecisionMode.Formal)]
    [InlineData(PrtgDiskDecisionMode.Preview)]
    public void Stale_hit_never_creates_finding_and_returns_as_of_reason(PrtgDiskDecisionMode mode)
    {
        var stale = Falling().Select(x => x with { Day = x.Day.AddDays(-1) }).ToArray();
        var result = PrtgDiskRuleDecision.Evaluate(Input(series: stale, mode: mode));
        Assert.Equal(PrtgDiskDecisionExclusion.StaleDataAsOf, result.Exclusion);
        Assert.True(result.WouldHit);
        Assert.Contains("不等於指定完成日", result.Reason);
        Assert.Null(result.Finding);
    }

    private static KnownIssueRule WithPlatformForTest(KnownIssueRule source, string platform) => new()
    {
        Id = source.Id, Enabled = source.Enabled, Platform = platform, PrtgRuleCode = source.PrtgRuleCode,
        PrtgSensorCategory = source.PrtgSensorCategory, PrtgDiskTrendThresholds = source.PrtgDiskTrendThresholds,
        Category = source.Category, Severity = source.Severity, ElevatesDayRisk = source.ElevatesDayRisk,
        Description = source.Description
    };
}
