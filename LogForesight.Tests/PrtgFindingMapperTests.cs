using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgFindingMapperTests
{
    private static readonly DateTime TestDay = new(2026, 9, 1);
    private static readonly Dictionary<string, KnownIssueRule> SeedRules =
        KnownIssueSeed.CreateRules().ToDictionary(r => r.Id);

    [Fact]
    public void ToSignature_四種RuleCode映射正確()
    {
        var downRule = SeedRules["builtin-prtg-down"];
        var flapRule = SeedRules["builtin-prtg-flapping"];
        var warnRule = SeedRules["builtin-prtg-warning"];
        var silentRule = SeedRules["builtin-prtg-silent"];

        var downFinding = new PrtgFinding(1001, 2001, PrtgRuleEvaluator.RuleDown, "Sensor down", 60, downRule);
        var flappingFinding = new PrtgFinding(1001, 2002, PrtgRuleEvaluator.RuleFlapping, "Sensor flapping", 6, flapRule);
        var warningFinding = new PrtgFinding(1001, 2003, PrtgRuleEvaluator.RuleWarning, "Sensor warning", 250, warnRule);
        var silentFinding = new PrtgFinding(1002, null, PrtgRuleEvaluator.RuleSilent, "Device silent", 1, silentRule);

        var downSig = PrtgFindingMapper.ToSignature(downFinding, TestDay);
        var flapSig = PrtgFindingMapper.ToSignature(flappingFinding, TestDay);
        var warnSig = PrtgFindingMapper.ToSignature(warningFinding, TestDay);
        var silentSig = PrtgFindingMapper.ToSignature(silentFinding, TestDay);

        // down
        Assert.Equal(IssueCategory.Service, downSig.Category);
        Assert.Equal(IssueSeverity.High, downSig.Severity);
        // seed v7：不限分類的 down 不再是「重大」，只有連通性分類的 down 會拉高日風險
        Assert.False(downSig.ElevatesDayRisk);
        Assert.Equal(downRule.Description, downSig.KnownIssue);
        Assert.Equal("builtin-prtg-down", downSig.RuleId);
        Assert.Equal("PRTG:down", downSig.Source);

        // flapping
        Assert.Equal(IssueCategory.Service, flapSig.Category);
        Assert.Equal(IssueSeverity.Medium, flapSig.Severity);
        Assert.False(flapSig.ElevatesDayRisk);
        Assert.Equal(flapRule.Description, flapSig.KnownIssue);
        Assert.Equal("builtin-prtg-flapping", flapSig.RuleId);
        Assert.Equal("PRTG:flapping", flapSig.Source);

        // warning
        Assert.Equal(IssueCategory.Resource, warnSig.Category);
        Assert.Equal(IssueSeverity.Medium, warnSig.Severity);
        Assert.False(warnSig.ElevatesDayRisk);
        Assert.Equal(warnRule.Description, warnSig.KnownIssue);
        Assert.Equal("builtin-prtg-warning", warnSig.RuleId);
        Assert.Equal("PRTG:warning", warnSig.Source);

        // silent
        Assert.Equal(IssueCategory.Service, silentSig.Category);
        Assert.Equal(IssueSeverity.Medium, silentSig.Severity);
        Assert.False(silentSig.ElevatesDayRisk);
        Assert.Equal(silentRule.Description, silentSig.KnownIssue);
        Assert.Equal("builtin-prtg-silent", silentSig.RuleId);
        Assert.Equal("PRTG:silent", silentSig.Source);
    }

    [Fact]
    public void ToSignature_EventKey格式為冒號分隔且絕不含管線符號()
    {
        var downRule = SeedRules["builtin-prtg-down"];
        var silentRule = SeedRules["builtin-prtg-silent"];

        var sensorFinding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, downRule);
        var deviceFinding = new PrtgFinding(1002, null, "silent", "Device silent", 1, silentRule);

        var sensorSig = PrtgFindingMapper.ToSignature(sensorFinding, TestDay);
        var deviceSig = PrtgFindingMapper.ToSignature(deviceFinding, TestDay);

        Assert.Equal("prtg:down:2001", sensorSig.EventKey);
        Assert.Equal("prtg:silent:1002", deviceSig.EventKey);

        Assert.DoesNotContain("|", sensorSig.EventKey);
        Assert.DoesNotContain("|", deviceSig.EventKey);

        // 驗證 IssueSignatureKey 組合與解析相容性
        var issueKey = IssueSignatureKey.For(sensorSig);
        Assert.Equal("PRTG|PRTG:down|0|2|prtg:down:2001", issueKey);
        var parsed = IssueSignatureKey.TryParseSignature(issueKey);
        Assert.NotNull(parsed);
        Assert.Equal("PRTG:down", parsed.Value.Source);
        Assert.Equal(0, parsed.Value.EventId);
    }

    [Fact]
    public void ToSignature_固定欄位與範例訊息正確()
    {
        var downRule = SeedRules["builtin-prtg-down"];
        var finding = new PrtgFinding(1001, 2001, "down", "Ping sensor down for 60m", 60, downRule);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal("PRTG", sig.LogName);
        Assert.Equal("PRTG:down", sig.Source);
        Assert.Equal(0, sig.EventId);
        Assert.Equal(EventLogEntryType.Warning, sig.EntryType);
        Assert.Equal(1, sig.Count);
        Assert.Equal("00:00", sig.FirstSeen);
        Assert.Equal("23:59", sig.LastSeen);
        Assert.Single(sig.SampleMessages, "Ping sensor down for 60m");
        Assert.Equal(1, sig.DistinctMessageCount);
    }

    [Fact]
    public void ToSignature_自訂規則對象欄位映射正確()
    {
        var customRule = new KnownIssueRule
        {
            Id = "custom-rule",
            PrtgRuleCode = "custom_rule",
            Category = IssueCategory.Other,
            Severity = IssueSeverity.Medium,
            ElevatesDayRisk = false,
            Description = "Custom description"
        };
        var finding = new PrtgFinding(1001, 2001, "custom_rule", "Custom detail", 1, customRule);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal(IssueCategory.Other, sig.Category);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal("Custom description", sig.KnownIssue);
        Assert.Equal("custom-rule", sig.RuleId);
        Assert.Equal("prtg:custom_rule:2001", sig.EventKey);
        Assert.Equal("PRTG:custom_rule", sig.Source);
    }

    [Theory]
    [InlineData(PrtgRuleEvaluator.RuleDown, "builtin-prtg-down")]
    [InlineData(PrtgRuleEvaluator.RuleFlapping, "builtin-prtg-flapping")]
    [InlineData(PrtgRuleEvaluator.RuleWarning, "builtin-prtg-warning")]
    [InlineData(PrtgRuleEvaluator.RuleSilent, "builtin-prtg-silent")]
    public void ToSignature_分類嚴重度與風險升級旗標與KnownIssueSeed一致(string ruleCode, string seedRuleId)
    {
        var seedRule = KnownIssueSeed.CreateRules().Single(r => r.Id == seedRuleId);
        var finding = new PrtgFinding(1001, 2001, ruleCode, "Detail", 1, seedRule);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal(seedRule.Category, sig.Category);
        Assert.Equal(seedRule.Severity, sig.Severity);
        Assert.Equal(seedRule.ElevatesDayRisk, sig.ElevatesDayRisk);
    }

    [Fact]
    public void PrtgRuleCatalog_預設值與KnownIssueSeed門檻一致()
    {
        var seedRules = KnownIssueSeed.CreateRules().ToDictionary(r => r.Id);

        Assert.Equal(PrtgRuleCatalog.DefaultDownMinutes, seedRules["builtin-prtg-down"].PrtgThreshold);
        Assert.Equal(PrtgRuleCatalog.DefaultFlapCount, seedRules["builtin-prtg-flapping"].PrtgThreshold);
        Assert.Equal(PrtgRuleCatalog.DefaultWarningMinutes, seedRules["builtin-prtg-warning"].PrtgThreshold);
    }

    [Fact]
    public void ToSignature_規則物件屬性覆寫分類嚴重度與ElevatesDayRisk()
    {
        var customDown = new KnownIssueRule
        {
            Id = "custom-down-medium",
            PrtgRuleCode = "down",
            Category = IssueCategory.Service,
            Severity = IssueSeverity.Medium,
            ElevatesDayRisk = false,
            Description = "Down but medium"
        };
        var finding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, customDown);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal("custom-down-medium", sig.RuleId);
    }

    [Fact]
    public void ToSignature_Acknowledged為true時簽章ElevatesDayRisk降為false()
    {
        var downRule = new KnownIssueRule
        {
            Id = "down-elevates",
            PrtgRuleCode = "down",
            Category = IssueCategory.Service,
            Severity = IssueSeverity.High,
            ElevatesDayRisk = true,
            Description = "Down rule"
        };
        var ackFinding = new PrtgFinding(1001, 2001, "down", "Detail 已於 PRTG 確認", 90, downRule, Acknowledged: true);
        var ackSig = PrtgFindingMapper.ToSignature(ackFinding, TestDay);
        Assert.False(ackSig.ElevatesDayRisk);

        var unackFinding = new PrtgFinding(1001, 2001, "down", "Detail", 90, downRule, Acknowledged: false);
        var unackSig = PrtgFindingMapper.ToSignature(unackFinding, TestDay);
        Assert.True(unackSig.ElevatesDayRisk);
    }

    [Fact]
    public void IsPrtg_只認LogName且Source正確()
    {
        var downRule = new KnownIssueRule
        {
            Id = "builtin-down",
            PrtgRuleCode = "down",
            Description = "Down"
        };
        var finding = new PrtgFinding(1001, 2001, "down", "Detail", 60, downRule);
        var sig = PrtgFindingMapper.ToSignature(finding, TestDay);

        Assert.Equal("PRTG:down", sig.Source);
        Assert.True(PrtgFindingMapper.IsPrtg(sig));

        var nonPrtg = new LogIssueSignature
        {
            LogName = "System",
            Source = "PRTG:down"
        };
        Assert.False(PrtgFindingMapper.IsPrtg(nonPrtg));
    }

    [Fact]
    public void ToSignature_帶出sensor分類_device層silent為null()
    {
        var sensorFinding = new PrtgFinding(1001, 2001, "warning", "Disk warning", 300, SeedRules["builtin-prtg-warning-disk"])
        {
            SensorCategory = PrtgSensorCategories.Disk
        };
        var deviceFinding = new PrtgFinding(1002, null, "silent", "Device silent", 1, SeedRules["builtin-prtg-silent"]);

        Assert.Equal(PrtgSensorCategories.Disk, PrtgFindingMapper.ToSignature(sensorFinding, TestDay).PrtgSensorCategory);
        Assert.Null(PrtgFindingMapper.ToSignature(deviceFinding, TestDay).PrtgSensorCategory);
    }
}
