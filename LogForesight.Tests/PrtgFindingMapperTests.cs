using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgFindingMapperTests
{
    private static readonly DateTime TestDay = new(2026, 9, 1);

    [Fact]
    public void ToSignature_四種RuleCode映射正確()
    {
        var downFinding = new PrtgFinding(1001, 2001, PrtgRuleEvaluator.RuleDown, "Sensor down", 60);
        var flappingFinding = new PrtgFinding(1001, 2002, PrtgRuleEvaluator.RuleFlapping, "Sensor flapping", 6);
        var warningFinding = new PrtgFinding(1001, 2003, PrtgRuleEvaluator.RuleWarning, "Sensor warning", 250);
        var silentFinding = new PrtgFinding(1002, null, PrtgRuleEvaluator.RuleSilent, "Device silent", 1);

        var downSig = PrtgFindingMapper.ToSignature(downFinding, TestDay);
        var flapSig = PrtgFindingMapper.ToSignature(flappingFinding, TestDay);
        var warnSig = PrtgFindingMapper.ToSignature(warningFinding, TestDay);
        var silentSig = PrtgFindingMapper.ToSignature(silentFinding, TestDay);

        // down
        Assert.Equal(IssueCategory.Service, downSig.Category);
        Assert.Equal(IssueSeverity.High, downSig.Severity);
        Assert.True(downSig.ElevatesDayRisk);
        Assert.Equal("監控 sensor 持續無回應，可能是服務或主機失聯", downSig.KnownIssue);
        Assert.Equal("prtg-down", downSig.RuleId);

        // flapping
        Assert.Equal(IssueCategory.Service, flapSig.Category);
        Assert.Equal(IssueSeverity.Medium, flapSig.Severity);
        Assert.False(flapSig.ElevatesDayRisk);
        Assert.Equal("監控 sensor 狀態頻繁震盪，可能是網路不穩或服務反覆重啟", flapSig.KnownIssue);
        Assert.Equal("prtg-flapping", flapSig.RuleId);

        // warning
        Assert.Equal(IssueCategory.Resource, warnSig.Category);
        Assert.Equal(IssueSeverity.Medium, warnSig.Severity);
        Assert.False(warnSig.ElevatesDayRisk);
        Assert.Equal("監控 sensor 長時間處於警告狀態，資源可能接近上限", warnSig.KnownIssue);
        Assert.Equal("prtg-warning", warnSig.RuleId);

        // silent
        Assert.Equal(IssueCategory.Service, silentSig.Category);
        Assert.Equal(IssueSeverity.Medium, silentSig.Severity);
        Assert.False(silentSig.ElevatesDayRisk);
        Assert.Equal("該 device 的全部 sensor 皆無狀態，監控本身可能已失效", silentSig.KnownIssue);
        Assert.Equal("prtg-silent", silentSig.RuleId);
    }

    [Fact]
    public void ToSignature_EventKey格式為冒號分隔且絕不含管線符號()
    {
        var sensorFinding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60);
        var deviceFinding = new PrtgFinding(1002, null, "silent", "Device silent", 1);

        var sensorSig = PrtgFindingMapper.ToSignature(sensorFinding, TestDay);
        var deviceSig = PrtgFindingMapper.ToSignature(deviceFinding, TestDay);

        Assert.Equal("prtg:down:2001", sensorSig.EventKey);
        Assert.Equal("prtg:silent:1002", deviceSig.EventKey);

        Assert.DoesNotContain("|", sensorSig.EventKey);
        Assert.DoesNotContain("|", deviceSig.EventKey);

        // 驗證 IssueSignatureKey 組合與解析相容性
        var issueKey = IssueSignatureKey.For(sensorSig);
        Assert.Equal("PRTG|PRTG|0|2|prtg:down:2001", issueKey);
        var parsed = IssueSignatureKey.TryParseSignature(issueKey);
        Assert.NotNull(parsed);
        Assert.Equal("PRTG", parsed.Value.Source);
        Assert.Equal(0, parsed.Value.EventId);
    }

    [Fact]
    public void ToSignature_固定欄位與範例訊息正確()
    {
        var finding = new PrtgFinding(1001, 2001, "down", "Ping sensor down for 60m", 60);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal("PRTG", sig.LogName);
        Assert.Equal("PRTG", sig.Source);
        Assert.Equal(0, sig.EventId);
        Assert.Equal(EventLogEntryType.Warning, sig.EntryType);
        Assert.Equal(1, sig.Count);
        Assert.Equal("00:00", sig.FirstSeen);
        Assert.Equal("23:59", sig.LastSeen);
        Assert.Single(sig.SampleMessages, "Ping sensor down for 60m");
        Assert.Equal(1, sig.DistinctMessageCount);
    }

    [Fact]
    public void ToSignature_未知RuleCode走預設Other()
    {
        var finding = new PrtgFinding(1001, 2001, "custom_rule", "Custom detail", 1);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal(IssueCategory.Other, sig.Category);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Null(sig.KnownIssue);
        Assert.Equal("prtg-custom_rule", sig.RuleId);
        Assert.Equal("prtg:custom_rule:2001", sig.EventKey);
    }

    [Theory]
    [InlineData(PrtgRuleEvaluator.RuleDown, "builtin-prtg-down")]
    [InlineData(PrtgRuleEvaluator.RuleFlapping, "builtin-prtg-flapping")]
    [InlineData(PrtgRuleEvaluator.RuleWarning, "builtin-prtg-warning")]
    [InlineData(PrtgRuleEvaluator.RuleSilent, "builtin-prtg-silent")]
    public void ToSignature_分類嚴重度與風險升級旗標與KnownIssueSeed一致(string ruleCode, string seedRuleId)
    {
        var finding = new PrtgFinding(1001, 2001, ruleCode, "Detail", 1);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        var seedRule = KnownIssueSeed.CreateRules().Single(r => r.Id == seedRuleId);

        Assert.Equal(seedRule.Category, sig.Category);
        Assert.Equal(seedRule.Severity, sig.Severity);
        Assert.Equal(seedRule.ElevatesDayRisk, sig.ElevatesDayRisk);
    }

    [Fact]
    public void PrtgRuleThresholds_預設值與KnownIssueSeed門檻一致()
    {
        var defaultThresholds = new PrtgRuleThresholds();
        var seedRules = KnownIssueSeed.CreateRules().ToDictionary(r => r.Id);

        Assert.Equal(seedRules["builtin-prtg-down"].PrtgThreshold, defaultThresholds.DownMinutes);
        Assert.Equal(seedRules["builtin-prtg-flapping"].PrtgThreshold, defaultThresholds.FlapCount);
        Assert.Equal(seedRules["builtin-prtg-warning"].PrtgThreshold, defaultThresholds.WarningMinutes);
    }
}
