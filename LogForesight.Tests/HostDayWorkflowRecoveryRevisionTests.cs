using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class HostDayWorkflowRecoveryRevisionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-workflow-revision-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HostDayWorkflowRecoveryRevisionTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "records.sqlite")};Pooling=False"
        }, _directory);
    }

    [Fact]
    public void ReconcileAuthorityRecord_rejectsParentWhoseSqlWriteRevisionChangedAfterRead()
    {
        var day = new DateTime(2026, 10, 5);
        var record = new DailyAnalysisRecord
        {
            HostId = 91, Host = "revision-host", Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-20), AuditEventCount = 0
        };
        _backend.RecordStore().Append(record);
        var query = _backend.RecordStore();
        var page = query.QueryWorkflowRecoveryPage(day, day, 0, 10);
        var oldParent = Assert.Single(page.Records);
        var revision = page.CapturedWriteRevisions![oldParent.RecordId];

        using (var context = _backend.CreateContext())
        {
            var row = context.DailyRecords.Single(value => value.RecordId == oldParent.RecordId);
            row.HostName = "renamed-after-capture";
            context.SaveChanges();
        }

        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        Assert.False(workflow.ReconcileAuthorityRecord(oldParent, revision, prtgEnabled: false, aiEnabled: false));
        Assert.Null(workflow.Get(oldParent.HostId, oldParent.Date));
    }

    [Fact]
    public void ReconcileAuthorityRecord_preservesCurrentCaseReceiptAndAiInFlightForSameParent()
    {
        var day = new DateTime(2026, 10, 5);
        var record = new DailyAnalysisRecord
        {
            HostId = 92, Host = "same-parent", Date = day, RiskLevel = RiskLevels.Medium,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-20), AuditEventCount = 2, AiAnalyzed = false
        };
        _backend.RecordStore().Append(record);
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);
        var authoritative = Assert.Single(page.Records);
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(authoritative.HostId, authoritative.Host, authoritative.Date,
            authoritative.LatestNetiqAttemptAtUtc?.ToString("O"), authoritative.AuditEventCount,
            HostDayWorkflowFingerprint.ForParentRecord(authoritative), prtgEnabled: false, aiEnabled: true,
            parentRecordId: authoritative.RecordId);
        var aiVersion = workflow.BeginAi(authoritative.HostId, authoritative.Date);
        var deliveryVersion = workflow.CaptureDeliveryVersion(authoritative.HostId, authoritative.Date, authoritative.RecordId)!;
        workflow.RecordCaseResults(authoritative.HostId, authoritative.Date, ["issue-1"], ["issue-1"], [], deliveryVersion);
        var before = workflow.Get(authoritative.HostId, authoritative.Date)!;

        Assert.True(workflow.ReconcileAuthorityRecord(authoritative,
            page.CapturedWriteRevisions![authoritative.RecordId], prtgEnabled: false, aiEnabled: true));

        var after = workflow.Get(authoritative.HostId, authoritative.Date)!;
        Assert.Equal(WorkflowLegState.Running, after.Ai);
        Assert.Equal(aiVersion, after.AiInputVersion);
        Assert.Equal(before.CaseIntents, after.CaseIntents);
        Assert.Equal(before.CaseDeliveredIntents, after.CaseDeliveredIntents);
    }

    [Fact]
    public void OrphanRecovery_rechecksParentAfterStaleAbsenceSnapshotAndPreservesNewWorkflow()
    {
        var day = new DateTime(2026, 10, 5);
        var hostId = 93L;
        var absentSnapshot = _backend.RecordStore().ExistingHostDays([(hostId, day)]);
        Assert.Empty(absentSnapshot);

        var record = new DailyAnalysisRecord
        {
            HostId = hostId, Host = "orphan-race", Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10), AuditEventCount = 1
        };
        _backend.RecordStore().Append(record);
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);
        var current = Assert.Single(page.Records);
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        Assert.True(workflow.ReconcileAuthorityRecord(current,
            page.CapturedWriteRevisions![current.RecordId], prtgEnabled: false, aiEnabled: false));

        // This is the same stale-snapshot gap as ProcessOrphanPage: a parent appeared after
        // ExistingHostDays returned empty and before orphan invalidation began.
        Assert.False(workflow.ParentDeletedIfMissingOrOutsideRetention(hostId, day,
            day.AddDays(-3), day.AddDays(1)));
        var preserved = workflow.Get(hostId, day)!;
        Assert.Equal(current.RecordId, preserved.ParentRecordId);
        Assert.Equal(WorkflowLegState.Succeeded, preserved.Parent);
    }

    [Fact]
    public void OrphanRecovery_invalidatesWorkflowWhenParentIsActuallyAbsent()
    {
        var day = new DateTime(2026, 10, 5);
        var record = new DailyAnalysisRecord
        {
            HostId = 94, Host = "orphan-removed", Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10), AuditEventCount = 0
        };
        _backend.RecordStore().Append(record);
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, "parent", 0, "decision", false, false,
            parentRecordId: record.RecordId);
        using (var context = _backend.CreateContext())
        {
            var row = context.DailyRecords.Single(value => value.RecordId == record.RecordId);
            context.DailyRecords.Remove(row);
            context.SaveChanges();
        }

        Assert.True(workflow.ParentDeletedIfMissingOrOutsideRetention(record.HostId, day,
            day.AddDays(-3), day.AddDays(1)));
        var invalidated = workflow.Get(record.HostId, day)!;
        Assert.Null(invalidated.ParentRecordId);
        Assert.Equal(WorkflowLegState.Waiting, invalidated.Parent);
    }

    [Fact]
    public void Recovery_keepsPrtgLegWrittenByDailyEvaluation()
    {
        // 每日評估直接把 PRTG 腿寫成 Succeeded；恢復不再要求舊 manifest，不得把它退回 Waiting。
        var day = new DateTime(2026, 10, 5);
        var record = AppendAndReadParent(95, day, "prtg-evaluated");
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, record.LatestNetiqAttemptAtUtc?.ToString("O"),
            record.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(record), true, false,
            parentRecordId: record.RecordId);
        workflow.SetPrtg(record.HostId, day, WorkflowLegState.Succeeded, evidenceReady: true);
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);

        Assert.True(workflow.ReconcileAuthorityRecord(record,
            page.CapturedWriteRevisions![record.RecordId], prtgEnabled: true, aiEnabled: false));
        Assert.Equal(WorkflowLegState.Succeeded, workflow.Get(record.HostId, day)!.Prtg);
    }

    [Fact]
    public void Recovery_marksPrtgDisabledWhenSourceIsOff()
    {
        var day = new DateTime(2026, 10, 5);
        var record = AppendAndReadParent(96, day, "prtg-off");
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, record.LatestNetiqAttemptAtUtc?.ToString("O"),
            record.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(record), true, false,
            parentRecordId: record.RecordId);
        workflow.SetPrtg(record.HostId, day, WorkflowLegState.Succeeded, evidenceReady: true);
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);

        Assert.True(workflow.ReconcileAuthorityRecord(record,
            page.CapturedWriteRevisions![record.RecordId], prtgEnabled: false, aiEnabled: false));
        Assert.Equal(WorkflowLegState.Disabled, workflow.Get(record.HostId, day)!.Prtg);
    }

    private DailyAnalysisRecord AppendAndReadParent(long hostId, DateTime day, string host)
    {
        _backend.RecordStore().Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10), AuditEventCount = 0
        });
        return Assert.Single(_backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10).Records);
    }

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
