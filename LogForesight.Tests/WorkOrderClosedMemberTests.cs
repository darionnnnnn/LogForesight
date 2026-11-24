using LogForesight.Core.Models;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Xunit;
using static LogForesight.Tests.ClosedMemberWorld;

namespace LogForesight.Tests;

/// <summary>
/// 階段 A-1：人工交辦與協調器在建案前排除「已結案」與「不再打擾」的成員。
/// 本檔只斷言既有的公開面（受影響台數、案件是否被建出），所以在未修正的程式上也能編譯並確認會紅。
/// </summary>
public class WorkOrderClosedMemberTests : IDisposable
{
    private readonly ClosedMemberWorld W = new();

    public void Dispose() => W.Dispose();

    [Fact]
    public void 預覽與建立_已結案與不再打擾的主機不建案_只有未處理的那台被寫入()
    {
        W.AddHost("HOST-OPEN");
        W.AddHost("HOST-CLOSED");
        W.AddHost("HOST-DISMISSED");
        W.CloseDay("HOST-CLOSED");
        W.Dismiss("HOST-DISMISSED");
        var handler = W.AddHandler();
        var before = W.TotalCases();

        var preview = W.Service.Preview(Single(handler.UserId));
        var result = W.Service.Create(Single(handler.UserId));

        Assert.Equal(1, preview.AffectedHosts);
        Assert.Equal(1, preview.AffectedMembers);
        var order = Assert.Single(result.Orders);
        Assert.Equal(1, order.NewCases);
        Assert.Equal(before + 1, W.TotalCases());
        Assert.NotNull(W.OpenCase("HOST-OPEN"));
        Assert.Null(W.OpenCase("HOST-CLOSED"));
        Assert.Null(W.OpenCase("HOST-DISMISSED"));
    }

    [Fact]
    public void 預覽_只有嚴重度預設不處理且沒有逐日標記的主機_不被當成已結案排除()
    {
        W.AddHost("HOST-OPEN");
        W.AddHost("HOST-LOW", Disk(IssueSeverity.Low));
        var handler = W.AddHandler();

        var preview = W.Service.Preview(Single(handler.UserId));

        Assert.Equal(2, preview.AffectedHosts);
        Assert.Equal(2, preview.AffectedMembers);
    }

    [Fact]
    public void 協調器保底_候選日全已結案且不要求重開_不建案件也不建單()
    {
        W.AddHost("HOST-CLOSED");
        W.CloseDay("HOST-CLOSED");
        var handler = W.AddHandler();

        var outcome = W.Coordinator.Create(NewRequest(handler.UserId, Member("HOST-CLOSED")));

        Assert.Equal(0, outcome.WorkOrderId);
        Assert.Equal(0, outcome.NewCases);
        Assert.Equal(0, W.TotalCases());
        Assert.Empty(W.OrderStore.All);
    }

    [Fact]
    public void 協調器保底_同一批只略過已結案的成員_其餘照常建案()
    {
        W.AddHost("HOST-OPEN");
        W.AddHost("HOST-CLOSED");
        W.CloseDay("HOST-CLOSED");
        var handler = W.AddHandler();

        var outcome = W.Coordinator.Create(NewRequest(handler.UserId, Member("HOST-OPEN"), Member("HOST-CLOSED")));

        Assert.Equal(1, outcome.NewCases);
        Assert.NotNull(W.OpenCase("HOST-OPEN"));
        Assert.Null(W.OpenCase("HOST-CLOSED"));
    }

    // ── 以下斷言新增的欄位與旗標 ─────────────────────────────────────────────

    [Fact]
    public void 預覽與建立_回報已結案與不再打擾的略過台數_每台帶原因與結案資訊_數字一致()
    {
        W.AddHost("HOST-OPEN");
        var closed = W.AddHost("HOST-CLOSED");
        var dismissed = W.AddHost("HOST-DISMISSED");
        W.CloseDay("HOST-CLOSED", IssueHandlingStatuses.Resolved, "DOMAIN\\closer");
        W.Dismiss("HOST-DISMISSED");
        var handler = W.AddHandler();

        var preview = W.Service.Preview(Single(handler.UserId));
        var result = W.Service.Create(Single(handler.UserId));

        Assert.Equal(1, preview.ClosedSkippedHosts);
        Assert.Equal(1, preview.DismissedSkippedHosts);
        Assert.Equal(1, result.ClosedSkippedHosts);
        Assert.Equal(1, result.DismissedSkippedHosts);

        var closedHost = Assert.Single(preview.Hosts, h => h.HostId == closed.HostId);
        Assert.Equal("closed", closedHost.ClosedReason);
        Assert.Equal(IssueHandlingStatuses.Resolved, closedHost.ClosedStatus);
        Assert.Equal("DOMAIN\\closer", closedHost.ClosedByAccount);
        Assert.NotNull(closedHost.ClosedAt);
        Assert.Null(closedHost.AllocatedHandlerId);
        Assert.False(closedHost.NoiseExcluded);

        var dismissedHost = Assert.Single(preview.Hosts, h => h.HostId == dismissed.HostId);
        Assert.Equal("dismissed", dismissedHost.ClosedReason);
        Assert.Equal(IssueHandlingStatuses.WontFix, dismissedHost.ClosedStatus);

        Assert.All(preview.Hosts.Where(h => h.HostName == "HOST-OPEN"), h => Assert.Null(h.ClosedReason));
    }

    [Fact]
    public void 重新交辦_勾選的已結案與不再打擾主機照常建案_觸發日改回處理中_舊案件恢復並留稽核()
    {
        W.AddHost("HOST-OPEN");
        var closed = W.AddHost("HOST-CLOSED");
        var dismissed = W.AddHost("HOST-DISMISSED");
        W.CloseDay("HOST-CLOSED");
        var blocked = W.Dismiss("HOST-DISMISSED");
        var handler = W.AddHandler();
        var request = Single(handler.UserId);
        request.ReopenHostIds = new List<long> { closed.HostId, dismissed.HostId };

        var preview = W.Service.Preview(request);
        // 預覽不寫任何資料：不建案、不恢復、不留稽核
        Assert.Equal(3, preview.AffectedHosts);
        Assert.Equal(0, preview.ClosedSkippedHosts);
        Assert.Equal(0, preview.DismissedSkippedHosts);
        Assert.Null(W.CaseStore.Get(blocked.CaseId)!.BlocksRedispatch);
        Assert.Equal(1, W.TotalCases());   // 只有那件舊的不再打擾案件
        Assert.Empty(W.Audit.Entries);

        var result = W.Service.Create(request);

        Assert.Equal(3, Assert.Single(result.Orders).NewCases);
        Assert.NotNull(W.OpenCase("HOST-OPEN"));
        var reopenedCase = W.OpenCase("HOST-CLOSED");
        Assert.NotNull(reopenedCase);
        var row = Assert.Single(W.IssueHandlingStore.GetForDay("HOST-CLOSED", Yesterday));
        Assert.Equal(IssueHandlingStatuses.InProgress, row.Status);
        Assert.Equal(reopenedCase!.CaseId, row.CaseId);
        Assert.NotNull(W.OpenCase("HOST-DISMISSED"));
        Assert.False(W.CaseStore.Get(blocked.CaseId)!.BlocksRedispatch);
        var restore = Assert.Single(W.Audit.Entries, e => e.Action == AuditActions.WorkOrderDismissRestore);
        Assert.Contains("HOST-DISMISSED", restore.DetailJson);
    }

    [Fact]
    public void 協調器保底_略過清單帶出已結案成員_全部都是已結案時不建單()
    {
        W.AddHost("HOST-CLOSED");
        W.CloseDay("HOST-CLOSED");
        var handler = W.AddHandler();

        var outcome = W.Coordinator.Create(NewRequest(handler.UserId, Member("HOST-CLOSED")));

        var skipped = Assert.Single(outcome.SkippedClosed);
        Assert.Equal("HOST-CLOSED", skipped.HostName);
        var preview = W.Coordinator.PreviewMembers(new List<WorkOrderMember> { Member("HOST-CLOSED") }, "disk", 153, handler.UserId);
        Assert.Equal(0, preview.NewCases);
        Assert.Single(preview.Closed);
    }

    [Fact]
    public void 協調器_要求重開的成員照常建案_觸發日覆寫已結案的列()
    {
        W.AddHost("HOST-CLOSED");
        W.CloseDay("HOST-CLOSED");
        var handler = W.AddHandler();
        var member = Member("HOST-CLOSED");
        member.Reopen = true;

        var outcome = W.Coordinator.Create(NewRequest(handler.UserId, member));

        Assert.Equal(1, outcome.NewCases);
        Assert.Empty(outcome.SkippedClosed);
        var row = Assert.Single(W.IssueHandlingStore.GetForDay("HOST-CLOSED", Yesterday));
        Assert.Equal(IssueHandlingStatuses.InProgress, row.Status);
        Assert.Equal(W.OpenCase("HOST-CLOSED")!.CaseId, row.CaseId);
    }

    [Fact]
    public void 協調器_候選日一天已結案一天未處理_建案只寫未處理那天_已結案那天的列不被改寫()
    {
        var host = W.AddHost("HOST-MIXED");
        var earlier = Yesterday.AddDays(-1);
        W.RecordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = earlier, RiskLevel = "高",
            Headline = "HOST-MIXED", TopIssues = new List<LogIssueSignature> { Disk() }
        });
        W.CloseDay("HOST-MIXED");   // 只有昨天已結案，前天未處理
        var closedBefore = Assert.Single(W.IssueHandlingStore.GetForDay("HOST-MIXED", Yesterday));
        var handler = W.AddHandler();

        var outcome = W.Coordinator.Create(NewRequest(handler.UserId, Member("HOST-MIXED")));

        Assert.Equal(1, outcome.NewCases);
        Assert.Empty(outcome.SkippedClosed);
        var created = W.OpenCase("HOST-MIXED");
        Assert.NotNull(created);
        var closedAfter = Assert.Single(W.IssueHandlingStore.GetForDay("HOST-MIXED", Yesterday));
        Assert.Equal(IssueHandlingStatuses.Resolved, closedAfter.Status);
        Assert.Equal(closedBefore.CaseId, closedAfter.CaseId);
        Assert.NotEqual(created!.CaseId, closedAfter.CaseId);
        var openDay = Assert.Single(W.IssueHandlingStore.GetForDay("HOST-MIXED", earlier));
        Assert.Equal(IssueHandlingStatuses.InProgress, openDay.Status);
        Assert.Equal(created.CaseId, openDay.CaseId);
    }

    private static WorkOrderMember Member(string hostName) => new()
    {
        HostName = hostName, IssueKey = KeyOf(Disk()), IssueLabel = "disk 153", TriggerDate = Yesterday
    };

    private static WorkOrderCreateRequest NewRequest(long handlerId, params WorkOrderMember[] members) => new()
    {
        Source = "disk", EventId = 153, IssueLabel = "disk 153", HandlerId = handlerId,
        Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts,
        Members = members.ToList(), Actor = new WorkOrderActor { ActorId = 1, ActorAccount = "admin", OccurredAt = DateTime.Now }
    };
}
