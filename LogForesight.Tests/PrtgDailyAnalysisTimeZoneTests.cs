using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    [Fact]
    public void DailyTrend不授予EvidenceAsOfUtc之後才收到的可信槽()
    {
        SeedDiskHistory(with28Days: true, descending: true, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        var service = new PrtgDiskAssessmentService(_backend.PrtgStore(), _hosts,
            new SystemSettingsStore(_backend.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        var before = Assert.Single(service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]).Rows);
        Assert.Equal(PrtgValueReadinessStatus.Ready, before.Readiness.Status);
        Assert.Equal(28, before.Readiness.UsableDays);

        Assert.True(new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])
            .TryGetValue(SensorId, out var profile));
        var periodStart = _completedDay.AddHours(15); // UTC analysis hour ending at Taipei Local D+1 midnight.
        var pending = PrtgResourceFixture.TrustedDiskHour(SensorId, periodStart, profile!, 48d - 27 * 1.1);
        var originalProof = PrtgTrustedSampleProof.Deserialize(pending.TrustedProof!);
        Assert.Equal(4, originalProof.Slots.Count);
        Assert.True(originalProof.IsStructurallyValid());
        var asOfUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        Assert.All(originalProof.Slots, slot => Assert.True(slot.ReceivedAt < asOfUtc));
        var lateReceivedAtUtc = asOfUtc.AddMinutes(5);
        var originalLastSlot = Assert.Single(originalProof.Slots.Where(slot => slot.Slot == 3));
        Assert.Equal(DateTime.SpecifyKind(periodStart.AddMinutes(45), DateTimeKind.Utc), originalLastSlot.MeasuredAt);
        Assert.Equal(TimeSpan.FromMinutes(20), lateReceivedAtUtc - originalLastSlot.MeasuredAt);
        Assert.True(lateReceivedAtUtc - originalLastSlot.MeasuredAt <= TimeSpan.FromMinutes(30.5));
        var lateProof = originalProof with
        {
            Slots = originalProof.Slots.Select(slot => slot.Slot == 3
                ? slot with { ReceivedAt = lateReceivedAtUtc }
                : slot).ToArray()
        };
        Assert.True(lateProof.IsStructurallyValid());
        Assert.Equal(originalLastSlot with { ReceivedAt = lateReceivedAtUtc },
            Assert.Single(lateProof.Slots.Where(slot => slot.Slot == 3)));
        pending.TrustedProof = PrtgTrustedSampleProof.Serialize(lateProof);
        _backend.PrtgStore().MergeSampledValues([pending]);

        var after = Assert.Single(service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]).Rows);
        Assert.Equal(before.Readiness.UsableHours - 1, after.Readiness.UsableHours);
        Assert.NotEqual(before.EvidenceFingerprint, after.EvidenceFingerprint);
    }

    [Fact]
    public void 前一分析日尾端落在Local父日的可信小時納入SQL趨勢與證據()
    {
        SeedDiskHistory(with28Days: true, descending: true, excludeOutsideParentDay: true,
            omitPriorAnalysisDayHoursBelongingToParentDay: true);
        SeedTypedSemanticEvidence();
        var service = new PrtgDiskAssessmentService(_backend.PrtgStore(), _hosts,
            new SystemSettingsStore(_backend.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        var before = Assert.Single(service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]).Rows);

        var insideParentDay = Enumerable.Range(0, 24)
            .Select(hour => _completedDay.AddDays(-1).AddHours(hour))
            .Where(period => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(period, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date)
            .ToArray();
        Assert.NotEmpty(insideParentDay);
        Assert.True(new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])
            .TryGetValue(SensorId, out var profile));
        var trustedHours = insideParentDay.Select(period => PrtgResourceFixture.TrustedDiskHour(
            SensorId, period, profile!, 0)).ToArray();
        _backend.PrtgStore().MergeSampledValues(trustedHours);

        var after = Assert.Single(service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]).Rows);
        Assert.Equal(PrtgValueReadinessStatus.Ready, after.Readiness.Status);
        Assert.Equal(before.Readiness.UsableHours + insideParentDay.Length, after.Readiness.UsableHours);
        Assert.NotEqual(before.EvidenceFingerprint, after.EvidenceFingerprint);
        Assert.NotNull(after.Decision.Finding);
    }

    [Fact]
    public void 無效可信AnalysisTimeZone明確回報Unknown且不產生趨勢finding()
    {
        SeedDiskHistory(with28Days: true, descending: true);
        SeedTypedSemanticEvidence();
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
            policy.AnalysisTimeZoneId = "invalid/unknown-zone");
        var service = new PrtgDiskAssessmentService(_backend.PrtgStore(), _hosts,
            new SystemSettingsStore(_backend.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));

        var row = Assert.Single(service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]).Rows);
        Assert.Equal(PrtgValueReadinessStatus.Unknown, row.Readiness.Status);
        Assert.Contains("Unknown", row.Readiness.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(row.Decision.Finding);
    }

    [Fact]
    public async Task Daily磁碟趨勢不採用Local父日截止後的分析牆鐘樣本()
    {
        SeedDiskHistory(with28Days: true, descending: true, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();

        var service = new PrtgDiskAssessmentService(_backend.PrtgStore(), _hosts,
            new SystemSettingsStore(_backend.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        var baseline = service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]);
        var baselineRow = Assert.Single(baseline.Rows);
        Assert.NotNull(baselineRow.Decision.Finding);

        var firstRun = await RunPipeline();
        var firstFinding = Assert.Single(firstRun.Registry.For(HostId, _completedDay));
        var recordStore = _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" });
        var firstRecord = Assert.Single(recordStore.ReadRecent(_completedDay, 1));

        // Analysis UTC 的 16:00–24:00 wall hours 已落在 Taipei/Local 父日次日。
        // 後到的可信樣本不屬於父日 D，不能改動已完成父日的來源結果。
        Assert.True(new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])
            .TryGetValue(SensorId, out var currentProfile));
        var localParentDayEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var outsideHours = Enumerable.Range(0, 24)
            .Select(hour => _completedDay.AddHours(hour))
            .Where(period => DateTime.SpecifyKind(period, DateTimeKind.Utc) >= localParentDayEndUtc)
            .ToArray();
        Assert.NotEmpty(outsideHours);
        var outsideParentDay = outsideHours
            .Select(period => PrtgResourceFixture.TrustedDiskHour(SensorId, period, currentProfile!, 0))
            .ToArray();
        _backend.PrtgStore().MergeSampledValues(outsideParentDay);

        var afterChange = service.Assess(DateOnly.FromDateTime(_completedDay), DiskRule(enabled: true),
            PrtgDiskDecisionMode.Formal, 100, 0, [HostId], [SensorId]);
        var afterChangeRow = Assert.Single(afterChange.Rows);
        Assert.Equal(baselineRow.EvidenceFingerprint, afterChangeRow.EvidenceFingerprint);

        var secondRun = await RunPipeline();
        var secondFinding = Assert.Single(secondRun.Registry.For(HostId, _completedDay));
        var secondRecord = Assert.Single(recordStore.ReadRecent(_completedDay, 1));
        Assert.Equal(firstFinding.EventKey, secondFinding.EventKey);
        Assert.Equal(firstFinding.PrtgDisplayLabel, secondFinding.PrtgDisplayLabel);
        Assert.Equal(firstRecord.RiskLevel, secondRecord.RiskLevel);
        Assert.Equal(firstRecord.PrtgManifest?.FindingFingerprint, secondRecord.PrtgManifest?.FindingFingerprint);
    }
}
