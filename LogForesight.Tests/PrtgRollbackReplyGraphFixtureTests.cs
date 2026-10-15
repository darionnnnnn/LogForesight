using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    private const string RollbackReplyFixtureMarker = "lf-round54-native-reply-fixture-owner-v1";

    [Fact]
    public void RollbackGraphFixture_ExportsBeforeAndAfterSnapshotsFromNativeSqliteReply()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        var beforeParent = Parent();
        var beforeCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var beforeOrder = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Equal(beforeOrder.WorkOrderId, beforeCase.WorkOrderId);
        Assert.False(beforeCase.DaySyncPending);

        PersistRollbackStartupSafetySettings(setup.Settings);
        AssertRollbackStartupSafetySettings(setup.Settings);
        Assert.False(string.IsNullOrWhiteSpace(_backend.Blob("hosts").Read()));
        int sourceTopIssueRowCount;
        using (var sourceContext = _backend.CreateContext()) sourceTopIssueRowCount = sourceContext.TopIssues.Count();
        Assert.True(sourceTopIssueRowCount > 0);

        var evidenceRoot = Path.Combine(FindRepoRoot().FullName, "work", "round54", "rollback-native-reply-fixture");
        var root = Path.Combine(evidenceRoot, "fixture");
        var fixtureAlreadyExists = Directory.Exists(root);
        Directory.CreateDirectory(root);
        var markerPath = Path.Combine(root, "owner.json");
        var marker = new
        {
            schema = RollbackReplyFixtureMarker,
            fixture = "inline-resolved-reply-with-order-closure",
            source = "PrtgDiskFormalFlowTests + WorkOrderCoordinator.Reply on isolated EF SQLite",
            repositoryRelativeRoot = "work/round54/rollback-native-reply-fixture/fixture"
        };
        var markerJson = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        if (fixtureAlreadyExists && !File.Exists(markerPath))
            throw new InvalidOperationException("Native rollback fixture directory exists without its owner marker.");
        if (File.Exists(markerPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (existing.RootElement.GetProperty("schema").GetString() != RollbackReplyFixtureMarker ||
                existing.RootElement.GetProperty("fixture").GetString() != "inline-resolved-reply-with-order-closure")
                throw new InvalidOperationException("Native rollback fixture output is not owned by this test.");
        }
        else
        {
            File.WriteAllText(markerPath, markerJson);
        }

        var beforePath = Path.Combine(root, "before.db");
        var afterPath = Path.Combine(root, "after.db");
        ExportCheckpointedSnapshot(beforePath);

        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        var occurredAt = DateTime.Now;
        var reply = orders.Reply(beforeOrder.WorkOrderId, [beforeCase.CaseId], IssueHandlingStatuses.Resolved,
            "Native reply graph fixture", null,
            new WorkOrderActor { ActorId = beforeOrder.HandlerId, ActorAccount = "round54-fixture-handler", OccurredAt = occurredAt });
        Assert.Equal(1, reply.Cases);
        Assert.True(reply.WorkOrderClosed);

        var afterCase = Assert.Single(_backend.IssueCaseStore().GetMany(["DISK-HOST"]).Where(item => item.CaseId == beforeCase.CaseId));
        var afterOrder = _backend.WorkOrderStore().Get(beforeOrder.WorkOrderId)!;
        Assert.Equal(IssueHandlingStatuses.Resolved, afterCase.Status);
        Assert.Equal(occurredAt, afterCase.ClosedAt);
        Assert.Equal(occurredAt, afterOrder.LastReplyAt);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, afterOrder.ClosedReason);
        Assert.False(afterCase.DaySyncPending);

        ExportCheckpointedSnapshot(afterPath);
        var receipt = new
        {
            schema = "lf-round54-native-reply-fixture-v1",
            ownerMarker = RollbackReplyFixtureMarker,
            sourceTest = "PrtgDiskFormalFlowTests.RollbackGraphFixture_ExportsBeforeAndAfterSnapshotsFromNativeSqliteReply",
            sourceMethod = "WorkOrderCoordinator.Reply",
            status = IssueHandlingStatuses.Resolved,
            caseCount = 1,
            dayProjectionCount = _backend.IssueHandlingStore().GetByCase(beforeCase.CaseId).Count,
            eventActions = _backend.WorkOrderStore().ListEvents(beforeOrder.WorkOrderId).Select(item => item.Action).ToArray(),
            startupSafetyBlobsVerified = true,
            hostCatalogPersisted = true,
            sourceTopIssueRowCount,
            before = new { file = Path.GetFileName(beforePath), sha256 = Sha256(beforePath) },
            after = new { file = Path.GetFileName(afterPath), sha256 = Sha256(afterPath) },
            sourceParentFindingCount = beforeParent.TopIssues.Count,
            replyAt = occurredAt.ToString("O")
        };
        File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void RollbackGraphFixture_PendingReplyUsesNativeBatchConsumerAtPrivateTwoRowThreshold()
    {
        Assert.Equal(5000, IssueCaseCoordinator.CaseDayInlineRowLimit);
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);
        var beforeParent = Parent();
        var issueCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var order = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Equal(order.WorkOrderId, issueCase.WorkOrderId);

        // The native pending consumer takes eligible source days from persisted top-issue rows.
        // Keep this fixture at exactly three days so the internal threshold seam is genuinely tiny.
        var parentStore = _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" });
        var candidateDays = new[] { _completedDay.AddDays(-2), _completedDay.AddDays(-1) };
        foreach (var day in candidateDays)
        {
            var finding = CpuPressureSignature(day, setup.Profile);
            Assert.Equal(issueCase.IssueKey, IssueSignatureKey.For(finding));
            parentStore.Append(new DailyAnalysisRecord
            {
                LogSource = AnalysisLogSource.Netiq,
                LatestNetiqAttemptStatus = "success",
                AiAnalyzed = false,
                Date = day,
                HostId = HostId,
                Host = "DISK-HOST",
                RiskLevel = RiskLevels.Low,
                RiskBasis = "native pending reply fixture",
                TopIssues = [finding]
            });
        }
        using (var db = _backend.CreateContext())
            Assert.Equal(3, db.TopIssues.Count(row => row.HostId == HostId && row.SourceName == issueCase.SourceName && row.EventId == issueCase.EventId));

        var root = Path.Combine(FindRepoRoot().FullName, "work", "round54", "rollback-native-reply-fixture",
            "pending-threshold-2");
        var fixtureAlreadyExists = Directory.Exists(root);
        Directory.CreateDirectory(root);
        var markerPath = Path.Combine(root, "owner.json");
        const string pendingMarker = "lf-round54-native-pending-reply-owner-v1";
        var marker = new
        {
            schema = pendingMarker,
            fixture = "one-case-three-candidate-days-test-threshold-two",
            source = "WorkOrderCoordinator.Reply plus IssueCaseCoordinator.ApplyPendingCaseDays on isolated EF SQLite",
            repositoryRelativeRoot = "work/round54/rollback-native-reply-fixture/pending-threshold-2",
            productionInlineRowLimit = IssueCaseCoordinator.CaseDayInlineRowLimit,
            internalTestInlineRowLimit = 2
        };
        if (fixtureAlreadyExists && !File.Exists(markerPath))
            throw new InvalidOperationException("Native pending reply fixture directory exists without its owner marker.");
        if (File.Exists(markerPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (existing.RootElement.GetProperty("schema").GetString() != pendingMarker ||
                existing.RootElement.GetProperty("fixture").GetString() != "one-case-three-candidate-days-test-threshold-two")
                throw new InvalidOperationException("Native pending reply fixture output is not owned by this test.");
        }
        else
        {
            File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        }

        var beforePath = Path.Combine(root, "before.db");
        var pendingPath = Path.Combine(root, "pending-intent.db");
        var consumedPath = Path.Combine(root, "pending-consumed.db");
        ExportCheckpointedSnapshot(beforePath);
        var beforeReplyRows = _backend.IssueHandlingStore().GetByCase(issueCase.CaseId);
        Assert.Single(beforeReplyRows);
        Assert.Equal(_completedDay.Date, Assert.Single(beforeReplyRows).Date.Date);

        var ownerStore = new IssueOwnerStore(_backend.Blob("issue_owners"));
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, ownerStore);
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts,
            caseDayInlineRowLimit: 2);
        var occurredAt = DateTime.Now;
        var reply = orders.Reply(order.WorkOrderId, [issueCase.CaseId], IssueHandlingStatuses.Resolved,
            "Native pending reply graph fixture", null,
            new WorkOrderActor { ActorId = order.HandlerId, ActorAccount = "round54-pending-handler", OccurredAt = occurredAt });
        Assert.Equal(1, reply.Cases);
        Assert.False(reply.DaySync.Inline);
        Assert.Equal(3, reply.DaySync.Rows);
        Assert.True(reply.WorkOrderClosed);

        var pendingCase = _backend.IssueCaseStore().Get(issueCase.CaseId)!;
        Assert.True(pendingCase.DaySyncPending);
        Assert.NotNull(pendingCase.DaySyncIntent);
        Assert.Equal(issueCase.CaseId, pendingCase.CaseId);
        Assert.Equal(IssueHandlingStatuses.Resolved, pendingCase.Status);
        Assert.Equal("Native pending reply graph fixture", pendingCase.Note);
        Assert.Equal(occurredAt, pendingCase.DaySyncIntent!.OccurredAt);
        Assert.Equal(order.HandlerId, pendingCase.DaySyncIntent.ActorId);
        Assert.Equal("round54-pending-handler", pendingCase.DaySyncIntent.ActorAccount);
        Assert.Equal(occurredAt, pendingCase.ClosedAt);
        var pendingRows = _backend.IssueHandlingStore().GetByCase(issueCase.CaseId);
        Assert.Single(pendingRows);
        Assert.Equal(beforeReplyRows[0].Status, pendingRows[0].Status);
        Assert.Equal(beforeReplyRows[0].UpdatedAt, pendingRows[0].UpdatedAt);
        ExportCheckpointedSnapshot(pendingPath);

        var currentParent = Parent();
        Assert.Equal(beforeParent.RecordId, currentParent.RecordId);
        var pendingConsumer = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, ownerStore);
        Assert.Equal(1, pendingConsumer.ApplyPendingCaseDays(10));

        var completedCase = _backend.IssueCaseStore().Get(issueCase.CaseId)!;
        Assert.False(completedCase.DaySyncPending);
        Assert.Null(completedCase.DaySyncIntent);
        Assert.Equal(3, _backend.IssueHandlingStore().GetByCase(issueCase.CaseId).Count);
        Assert.All(_backend.IssueHandlingStore().GetByCase(issueCase.CaseId), row =>
        {
            Assert.Equal(IssueHandlingStatuses.Resolved, row.Status);
            Assert.Equal("Native pending reply graph fixture", row.Note);
            Assert.Equal(occurredAt, row.UpdatedAt);
            Assert.Equal(order.HandlerId, row.ActorId);
            Assert.Equal("round54-pending-handler", row.ActorAccount);
        });
        var logs = candidateDays.Append(_completedDay)
            .SelectMany(day => _backend.RecordHandlingStore().GetLogs("DISK-HOST", day))
            .Where(log => log.IssueKey == issueCase.IssueKey && log.Status == IssueHandlingStatuses.Resolved)
            .ToArray();
        Assert.Equal(3, logs.Length);
        Assert.All(logs, log =>
        {
            Assert.Null(log.CaseId);
            Assert.Null(log.HandlerId);
            Assert.Equal("case_sync", log.Action);
            Assert.Equal(order.HandlerId, log.ActorId);
            Assert.Equal("round54-pending-handler", log.ActorAccount);
            Assert.Equal(occurredAt, log.CreatedAt);
        });
        var finalOrder = _backend.WorkOrderStore().Get(order.WorkOrderId)!;
        Assert.Equal(occurredAt, finalOrder.LastReplyAt);
        Assert.Equal(occurredAt, finalOrder.ClosedAt);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, finalOrder.ClosedReason);
        var events = _backend.WorkOrderStore().ListEvents(order.WorkOrderId);
        var replyEvent = Assert.Single(events.Where(item => item.Action == WorkOrderEventActions.Replied));
        Assert.Equal(order.HandlerId, replyEvent.ActorId);
        Assert.Equal("round54-pending-handler", replyEvent.ActorAccount);
        Assert.Equal(0, replyEvent.MemberDelta);
        var closeEvent = Assert.Single(events.Where(item => item.Action == WorkOrderEventActions.Closed));
        Assert.Null(closeEvent.ActorId);
        Assert.Equal("(system)", closeEvent.ActorAccount);
        Assert.Equal(0, closeEvent.MemberDelta);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, closeEvent.Note);
        Assert.Equal(occurredAt, closeEvent.CreatedAt);
        ExportCheckpointedSnapshot(consumedPath);

        var receipt = new
        {
            schema = "lf-round54-native-pending-reply-fixture-v1",
            ownerMarker = pendingMarker,
            sourceTest = "PrtgDiskFormalFlowTests.RollbackGraphFixture_PendingReplyUsesNativeBatchConsumerAtPrivateTwoRowThreshold",
            producer = "WorkOrderCoordinator.Reply",
            consumer = "IssueCaseCoordinator.ApplyPendingCaseDays (the CaseDaySyncHostedService consumer)",
            caseCount = 1,
            candidateDayCount = 3,
            productionInlineRowLimit = IssueCaseCoordinator.CaseDayInlineRowLimit,
            internalTestInlineRowLimit = 2,
            replyDeferred = true,
            pendingIntentPersisted = true,
            consumerAppliedCaseCount = 1,
            projectedIssueHandlingRows = 3,
            projectedRecordHandlingLogs = 3,
            actorId = order.HandlerId,
            actorAccount = "round54-pending-handler",
            replyAt = occurredAt.ToString("O"),
            sourceParentFindingCount = beforeParent.TopIssues.Count,
            snapshots = new[]
            {
                new { file = Path.GetFileName(beforePath), sha256 = Sha256(beforePath) },
                new { file = Path.GetFileName(pendingPath), sha256 = Sha256(pendingPath) },
                new { file = Path.GetFileName(consumedPath), sha256 = Sha256(consumedPath) }
            }
        };
        File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ExportCheckpointedSnapshot(string destination)
    {
        var database = Path.Combine(_dir, "formal.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(database + suffix)) throw new InvalidOperationException("Native fixture database has an uncheckpointed SQLite sidecar.");
        File.Copy(database, destination, overwrite: true);
    }

    private void PersistRollbackStartupSafetySettings(ISystemSettingsStore settings)
    {
        settings.Update(value =>
        {
            value.MailEnabled = false;
            value.MailOnRunCompleted = false;
            value.MailDailyEnabled = false;
            value.MailWeeklyEnabled = false;
            value.MailUrgentEnabled = false;
            value.MailNotifyWorkOrders = false;
            value.AutoDispatchEnabled = false;
            value.PrtgEnabled = false;
            value.AiBaseUrl = string.Empty;
            value.AdAuthEnabled = false;
            value.AdServers.Clear();
        });
        new ScheduleOptionsStore(_backend.Blob("schedule_options")).Update(value =>
        {
            value.Enabled = false;
            value.LocalAnalysisEnabled = false;
        });
        new SentinelStore(_backend.Blob("sentinels")).Upsert(new Sentinel
        {
            Name = "round54-rollback-fixture-disabled",
            BaseUrl = "https://disabled.invalid",
            Active = false
        });
    }

    private void AssertRollbackStartupSafetySettings(ISystemSettingsStore settings)
    {
        var current = settings.Get();
        Assert.False(current.MailEnabled || current.MailOnRunCompleted || current.MailDailyEnabled ||
                     current.MailWeeklyEnabled || current.MailUrgentEnabled || current.MailNotifyWorkOrders);
        Assert.False(current.AutoDispatchEnabled || current.PrtgEnabled || current.AdAuthEnabled);
        Assert.Empty(current.AdServers);
        Assert.Equal(string.Empty, current.AiBaseUrl);
        var schedule = new ScheduleOptionsStore(_backend.Blob("schedule_options")).Get();
        Assert.False(schedule.Enabled);
        Assert.False(schedule.LocalAnalysisEnabled);
        var sentinels = new SentinelStore(_backend.Blob("sentinels")).GetAll();
        Assert.NotEmpty(sentinels);
        Assert.All(sentinels, sentinel => Assert.False(sentinel.Active));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static DirectoryInfo FindRepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "scripts", "round54", "rollback"))) return current;
        throw new DirectoryNotFoundException("Could not find the repository root for the owned rollback fixture.");
    }
}
