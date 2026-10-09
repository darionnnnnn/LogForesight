using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgPartialWorkflowContractTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-partial-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime day = DateTime.Today.AddDays(-1);
    private HostDayWorkflowService Workflow() => new(new HostDayWorkflowStore(new StorageBackend(new StorageSettings { Type = "Sqlite" }, root)));
    private static string Hash(string text) => SourceEvidence.Fingerprint(text);

    [Fact]
    public void QualifiedPartialAdvancesVersionRejectsOldAiAndClearsWholeReadinessDeadline()
    {
        var workflow = Workflow();
        workflow.ParentSucceeded(7, "partial", day, "run", 0, Hash("parent"), true, true);
        workflow.SetPrtg(7, day, WorkflowLegState.Succeeded, true, Hash("complete"));
        var original = workflow.Get(7, day)!;
        Assert.NotNull(original.SupplementDueAt);
        var oldAi = workflow.BeginAi(7, day);
        workflow.SetPrtg(7, day, WorkflowLegState.Degraded, false, Hash("partial"), failure: "silent-proof-not-ready");
        var state = Workflow().Get(7, day)!;
        Assert.Equal(original.DecisionVersion + 1, state.DecisionVersion);
        Assert.Equal(WorkflowLegState.Degraded, state.Prtg);
        Assert.Null(state.PrtgEvidenceReadyAt);
        Assert.Null(state.SupplementDueAt);
        Assert.Equal("silent-proof-not-ready", state.PrtgFailure);
        Assert.False(workflow.CompleteAi(7, day, oldAi, true));
        workflow.SetPrtg(7, day, WorkflowLegState.Degraded, false, Hash("partial"), failure: "silent-proof-not-ready");
        Assert.Equal(state.DecisionVersion, workflow.Get(7, day)!.DecisionVersion);
    }

    [Fact]
    public void PartialRequiresFindingMappingAndExplicitBoundedReasonsWhileZeroFindingRequiresComplete()
    {
        var manifest = new PrtgDecisionManifest
        {
            HostMappingFingerprint = Hash("mapping"), Outcome = "partial",
            WaitReasonCodes = ["silent-proof-not-ready"]
        };
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, true));
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, false));
        manifest.Outcome = "complete";
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, false));
        manifest.WaitReasonCodes.Clear();
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, false));
        manifest.HostMappingFingerprint = "";
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, true));
        manifest.HostMappingFingerprint = Hash("mapping");
        manifest.Outcome = "partial";
        manifest.WaitReasonCodes = ["reason", "reason"];
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, true));
        manifest.WaitReasonCodes = ["bad\nreason"];
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, true));
        manifest.WaitReasonCodes = Enumerable.Range(0, 17).Select(i => "reason-" + i).ToList();
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgOutcome(manifest, true));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
