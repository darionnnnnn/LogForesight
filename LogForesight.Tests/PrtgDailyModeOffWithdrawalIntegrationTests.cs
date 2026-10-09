using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    private const long ModeOffCpuSensorId = 81_104;
    private const long ModeOffMemorySensorId = 81_105;
    private const string ModeOffSourceGeneration = "b582b0e038e34b0697d74368fcd266dd";

    [Fact]
    public async Task DailyPipelineWithdrawsOnlyExactCpuModeOffFindingWhenProfileIsMissingAndRepeatsSafely()
    {
        var seeded = SeedDailyModeOffParent();
        Assert.False(_backend.PrtgStore().GetTrustedSamplingProfiles([ModeOffCpuSensorId]).ContainsKey(ModeOffCpuSensorId));
        Assert.True(new PrtgResourcePressureAuthorizationService(_backend).DisableFormalMode(
            HostId, ModeOffCpuSensorId, maintainAuthorized: true, DateTime.UtcNow));

        AssertModeOffSnapshot(seeded.CpuIdentity.Generation);
        var firstRun = await RunPipeline();
        Assert.Empty(firstRun.Registry.For(HostId, _completedDay));
        var firstParent = Assert.Single(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        Assert.True(!firstParent.TopIssues.Any(issue => issue.EventKey == seeded.CpuFinding.EventKey),
            "Owned Daily trace: " + string.Join("\n", firstRun.Lines));
        Assert.Contains(firstParent.TopIssues, issue => issue.EventKey == seeded.MemoryFinding.EventKey);
        Assert.Contains(firstParent.TopIssues, issue => issue.EventKey == seeded.ManualFinding.EventKey);
        Assert.Equal(AnalysisLogSource.Netiq, firstParent.LogSource);
        Assert.Equal(RiskLevels.Low, firstParent.PrtgBaselineRiskLevel);
        Assert.Equal("qualified NetIQ baseline", firstParent.PrtgBaselineRiskBasis);
        Assert.Equal(RiskLevels.Medium, firstParent.RiskLevel);
        Assert.Equal(RiskLevels.MoreSevere(firstParent.PrtgBaselineRiskLevel!,
            PrtgFindingMapper.RiskFromFindings(firstParent.TopIssues)), firstParent.RiskLevel);
        Assert.Equal(PrtgFindingMapper.RiskBasisFrom(firstParent.TopIssues), firstParent.RiskBasis);
        Assert.Null(firstParent.PrtgManifest);
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());

        var secondRun = await RunPipeline();
        Assert.Empty(secondRun.Registry.For(HostId, _completedDay));
        var repeatedParent = Assert.Single(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        Assert.DoesNotContain(repeatedParent.TopIssues, issue => issue.EventKey == seeded.CpuFinding.EventKey);
        Assert.Contains(repeatedParent.TopIssues, issue => issue.EventKey == seeded.MemoryFinding.EventKey);
        Assert.Contains(repeatedParent.TopIssues, issue => issue.EventKey == seeded.ManualFinding.EventKey);
        Assert.Equal(AnalysisLogSource.Netiq, repeatedParent.LogSource);
        Assert.Equal(RiskLevels.Low, repeatedParent.PrtgBaselineRiskLevel);
        Assert.Equal(RiskLevels.Medium, repeatedParent.RiskLevel);
        Assert.Equal(RiskLevels.MoreSevere(repeatedParent.PrtgBaselineRiskLevel!,
            PrtgFindingMapper.RiskFromFindings(repeatedParent.TopIssues)), repeatedParent.RiskLevel);
        Assert.Null(repeatedParent.PrtgManifest);
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
    }

    [Fact]
    public async Task DailyPipelineOldModeOffGenerationDoesNotWithdrawFindingForRotatedResourceGeneration()
    {
        var seeded = SeedDailyModeOffParent();
        Assert.True(new PrtgResourcePressureAuthorizationService(_backend).DisableFormalMode(
            HostId, ModeOffCpuSensorId, maintainAuthorized: true, DateTime.UtcNow));

        var rotated = _backend.PrtgStore().BindObservedResource(ModeOffCpuSensorId, HostId,
            ModeOffSourceGeneration, "cpu-resource-fingerprint-after-rotation");
        rotated = _backend.PrtgStore().SetObservedChannel(ModeOffCpuSensorId, ModeOffSourceGeneration,
            "cpu-channel-after-rotation|mode-off-v1", rotated.Generation);
        Assert.NotEqual(seeded.CpuIdentity.Generation, rotated.Generation);
        var cpuRule = new KnownIssueRuleStore(_backend.Blob("rules")).Load().Content.Rules
            .Single(rule => rule.Id == "builtin-prtg-resource-cpu-pressure");
        var rotatedCpuFinding = ModeOffFinding(cpuRule, ModeOffCpuSensorId, rotated.Generation,
            PrtgSensorCategories.Cpu);
        Assert.True(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .AttachPrtgFindings(HostId, _completedDay, [rotatedCpuFinding], new HashSet<string>(),
                out _, aiConfigured: false));

        AssertModeOffSnapshot(seeded.CpuIdentity.Generation);
        var run = await RunPipeline();
        Assert.Empty(run.Registry.For(HostId, _completedDay));
        var parent = Assert.Single(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        Assert.True(!parent.TopIssues.Any(issue => issue.EventKey == seeded.CpuFinding.EventKey),
            "Owned Daily trace: " + string.Join("\n", run.Lines));
        Assert.Contains(parent.TopIssues, issue => issue.EventKey == rotatedCpuFinding.EventKey);
        Assert.Contains(parent.TopIssues, issue => issue.EventKey == seeded.MemoryFinding.EventKey);
        Assert.Contains(parent.TopIssues, issue => issue.EventKey == seeded.ManualFinding.EventKey);
        Assert.Equal(RiskLevels.Medium, parent.RiskLevel);
        Assert.Equal(RiskLevels.MoreSevere(parent.PrtgBaselineRiskLevel!,
            PrtgFindingMapper.RiskFromFindings(parent.TopIssues)), parent.RiskLevel);
        Assert.Null(parent.PrtgManifest);
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
    }

    private void AssertModeOffSnapshot(string generation)
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        settings.PrtgEnabled = true; // RunPipeline applies these same settings before evaluation.
        settings.PrtgUrl = _url;
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        Assert.True(PrtgFormalEligibility.HostAllowed(_hosts.Get(HostId), settings, policy));
        var snapshot = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId))).ReadHostSnapshot(HostId);
        Assert.Contains(snapshot.Revocations, item => item.SensorObjid == ModeOffCpuSensorId &&
            item.SourceGeneration == ModeOffSourceGeneration && item.ResourceGeneration == generation);
    }

    private (PrtgResourceIdentity CpuIdentity, LogIssueSignature CpuFinding,
        LogIssueSignature MemoryFinding, LogIssueSignature ManualFinding) SeedDailyModeOffParent()
    {
        SeedDiskHistory(with28Days: true, descending: false);
        SetQualifiedNetIqBaseline();
        SeedTypedSemanticEvidence();

        var now = DateTime.UtcNow;
        var prtg = _backend.PrtgStore();
        prtg.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = ModeOffCpuSensorId, DeviceObjid = DeviceId,
                Name = "CPU pressure sensor", SensorType = "SNMP CPU Load", Category = PrtgSensorCategories.Cpu, Status = "Up" },
            new PrtgSensorRow { Objid = ModeOffMemorySensorId, DeviceObjid = DeviceId,
                Name = "Memory pressure sensor", SensorType = "SNMP Memory", Category = PrtgSensorCategories.Memory, Status = "Up" }
        }, now);
        prtg.ApplyAutoCategories(PrtgSensorTypeCategoryMap.ParseOverrides(
            new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgSensorTypeCategoryOverrides).Map);

        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy => policy.SensorIds = policy.SensorIds
            .Append(ModeOffCpuSensorId).Append(ModeOffMemorySensorId).Distinct().Order().ToList());

        var cpuIdentity = prtg.BindObservedResource(ModeOffCpuSensorId, HostId, ModeOffSourceGeneration,
            PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), "SNMP CPU Load", "cpu-creation", 0));
        cpuIdentity = prtg.SetObservedChannel(ModeOffCpuSensorId, ModeOffSourceGeneration,
            "cpu-channel|mode-off-v1", cpuIdentity.Generation);
        var memoryIdentity = prtg.BindObservedResource(ModeOffMemorySensorId, HostId, ModeOffSourceGeneration,
            PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), "SNMP Memory", "memory-creation", 0));
        memoryIdentity = prtg.SetObservedChannel(ModeOffMemorySensorId, ModeOffSourceGeneration,
            "memory-channel|mode-off-v1", memoryIdentity.Generation);

        var rules = KnownIssueSeed.CreateRules()
            .Where(rule => rule.Id is "builtin-prtg-disk-free-trend" or "builtin-prtg-resource-disk-pressure" or
                "builtin-prtg-resource-cpu-pressure" or "builtin-prtg-resource-memory-pressure")
            .Select(rule => rule.CloneForSeedOverwrite(enabled: true)).ToList();
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);

        var cpuRule = rules.Single(rule => rule.Id == "builtin-prtg-resource-cpu-pressure");
        var memoryRule = rules.Single(rule => rule.Id == "builtin-prtg-resource-memory-pressure");
        var cpuFinding = ModeOffFinding(cpuRule, ModeOffCpuSensorId, cpuIdentity.Generation,
            PrtgSensorCategories.Cpu);
        var memoryFinding = ModeOffFinding(memoryRule, ModeOffMemorySensorId, memoryIdentity.Generation,
            PrtgSensorCategories.Memory);
        cpuFinding.ElevatesDayRisk = true;
        var manualFinding = new LogIssueSignature
        {
            LogName = "System", Source = "manual-history", EventId = 41, EventKey = "manual:keep",
            Severity = IssueSeverity.Medium, Count = 1
        };

        Assert.True(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .AttachPrtgFindings(HostId, _completedDay, [cpuFinding, memoryFinding, manualFinding],
                new HashSet<string>(), out _, aiConfigured: false));

        var ruleFingerprint = PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(cpuRule);
        var modeStore = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(new PrtgResourcePressureModeGrant(
            HostId, ModeOffCpuSensorId, PrtgResourceFamily.Cpu, Hash("old-profile"),
            PrtgResourcePressureEvaluator.RulesVersion, ModeOffSourceGeneration, cpuIdentity.Generation,
            cpuIdentity.ChannelGeneration, cpuIdentity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "cpu-semantic-v1", "cpu-strategy-v1", cpuRule.Id, ruleFingerprint, Hash("old-trial"),
            MaintainAuthorized: true, FormalEnabled: true, UpdatedAtUtc: now,
            TrialExpiresAtUtc: now.AddHours(24), TrialConsumed: true)));
        return (cpuIdentity, cpuFinding, memoryFinding, manualFinding);
    }

    private void SetQualifiedNetIqBaseline()
    {
        using var context = _backend.CreateContext();
        var row = context.DailyRecords.Single(record => record.HostId == HostId &&
            record.RecordDate == _completedDay.Date);
        var parent = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)
            ?? throw new InvalidOperationException("Expected the seeded NetIQ parent record.");
        parent.RiskLevel = RiskLevels.Low;
        parent.RiskBasis = "qualified NetIQ baseline";
        parent.PrtgBaselineRiskLevel = RiskLevels.Low;
        parent.PrtgBaselineRiskBasis = "qualified NetIQ baseline";
        row.ContentJson = JsonSerializer.Serialize(parent);
        row.RiskLevel = RiskLevels.Low;
        context.SaveChanges();
    }

    private LogIssueSignature ModeOffFinding(KnownIssueRule rule, long sensorId, string resourceGeneration,
        string category) => PrtgFindingMapper.ToSignature(new PrtgFinding(DeviceId, sensorId,
            rule.PrtgRuleCode!, "pre-existing trusted resource pressure finding", 95, rule)
        {
            SourceGeneration = ModeOffSourceGeneration,
            ResourceGeneration = resourceGeneration,
            SensorCategory = category
        }, _completedDay, HostId);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
