using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Joins the current-version formal mode producer, supplement receiver, durable case/order
/// handoff, and parent deletion boundary using the existing isolated SQLite formal fixture.
/// SMTP, AI execution, and the browser role journey remain separate N4/N5 acceptance lanes.
/// </summary>
public sealed partial class PrtgDiskFormalFlowTests
{
    [Fact]
    public void JoinedFormalWorkflow_FiveReplaysKeepOneCaseAndHumanReply_DeletedParentCannotRepublish()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);

        // The real worker evaluates trusted same-host-day evidence and hands the finding to case dispatch.
        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);
        var originalParent = Parent();
        var finding = Assert.Single(originalParent.TopIssues.Where(PrtgFindingMapper.IsPrtg));
        var originalEventKey = finding.EventKey;
        var originalIssueKey = IssueSignatureKey.For(finding);
        var riskReport = Assert.IsType<ReportContent>(_backend.ReportStore().Read(
            new HostKey { HostId = HostId, HostName = "DISK-HOST" }, _completedDay, ReportKinds.DailyRisk));
        Assert.Contains(Assert.Single(finding.SampleMessages), riskReport.Content);
        Assert.Contains($"sha256:{HostDayWorkflowFingerprint.PrtgInputFingerprint(originalParent)}", riskReport.Content);
        Assert.Contains("未重新查詢 NetIQ", riskReport.Content);
        var originalCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var originalCaseId = originalCase.CaseId;
        var originalOrder = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        var originalOrderId = originalOrder.WorkOrderId;
        Assert.Equal(originalIssueKey, originalCase.IssueKey);

        // An actual human reply is persisted through WorkOrderCoordinator before replaying.
        var ownerStore = new IssueOwnerStore(_backend.Blob("issue_owners"));
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, ownerStore);
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        var replyAt = DateTime.Now;
        var reply = orders.Reply(originalOrderId, [originalCaseId], IssueHandlingStatuses.InProgress,
            "Human reply retained across PRTG replay", null,
            new WorkOrderActor { ActorId = originalOrder.HandlerId, ActorAccount = "round53-handler", OccurredAt = replyAt });
        Assert.Equal(1, reply.Cases);

        var replay = SupplementReplayForMode();
        var workflow = WorkflowForMode();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(attempt == 0 ? 1 : 0, replay.RunBatch(workflow: workflow));

        var caseAfterRetries = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var orderAfterRetries = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Equal(originalCaseId, caseAfterRetries.CaseId);
        Assert.Equal(originalOrderId, caseAfterRetries.WorkOrderId);
        Assert.Equal(originalEventKey, caseAfterRetries.PrtgEvidence?.EventKey);
        Assert.Equal(IssueHandlingStatuses.InProgress, caseAfterRetries.Status);
        Assert.Equal(replyAt, orderAfterRetries.LastReplyAt);
        Assert.Contains(_backend.IssueHandlingStore().GetByCase(originalCaseId), item =>
            item.Note == "Human reply retained across PRTG replay");
        Assert.Single(workflow.Get(HostId, _completedDay)!.FormalCaseStartClaims);

        // Remove the exact eligible parent, then replay repeatedly. No formal finding/case may be
        // recreated from the orphan observation, and the already accepted human work stays intact.
        Assert.Equal(1, _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .DeleteDays([_completedDay]));
        Assert.Empty(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(0, replay.RunBatch(workflow: workflow));

        using var db = _backend.CreateContext();
        Assert.Empty(db.TopIssues);
        Assert.Single(db.IssueCases);
        Assert.Single(db.WorkOrders);
        Assert.Equal(originalCaseId, Assert.Single(db.IssueCases).CaseId);
        Assert.Equal(originalOrderId, Assert.Single(db.WorkOrders).WorkOrderId);
        Assert.Equal(replyAt, Assert.Single(db.WorkOrders).LastReplyAt);
        Assert.Contains(_backend.IssueHandlingStore().GetByCase(originalCaseId), item =>
            item.Note == "Human reply retained across PRTG replay");
        Assert.Equal("waiting-netiq", Assert.Single(db.PrtgObservations).SupplementStatus);
    }
}
