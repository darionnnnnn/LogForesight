using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 待派頁「不再自動派工」清單與恢復（<see cref="DismissedIssueService"/>）：
/// 判定與閘門共用（<see cref="DispatchContext.DismissedKeys"/>）、欄位、分頁、可見範圍、恢復單筆／整個問題、稽核。
/// </summary>
public class DismissedIssueServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeHandlingStore _handling = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly RecordingAuditService _audit = new();

    private DismissedIssueService Service(params long[] visibleHostIds) => new(
        _cases, _handling, _hosts, _users, new FakeRuleStore(),
        new FixedVisibilityService(visibleHostIds.Length == 0 ? _hosts.GetAll().Select(h => h.HostId) : visibleHostIds),
        _audit, new UserDisplayNameService(_settings));

    private WebHost Host(string name) => _hosts.Upsert(new WebHost { HostName = name });

    private static string Key(string source, int eventId) => IssueSignatureKey.For("System", source, eventId, LogEntryType.Error);

    private IssueCase Case(string id, string host, string source, int eventId, string status, bool? blocks = null,
        DateTime? createdAt = null, DateTime? closedAt = null)
    {
        var closed = IssueHandlingStatuses.IsClosed(status);
        var c = new IssueCase
        {
            CaseId = id, HostName = host, IssueKey = Key(source, eventId), IssueLabel = $"{source} {eventId}", Status = status,
            CreatedAt = createdAt ?? Now.AddDays(-5), UpdatedAt = Now.AddDays(-2), FirstLinkedDate = Now.Date.AddDays(-6),
            LastLinkedDate = Now.Date.AddDays(-3), ClosedAt = closed ? closedAt ?? Now.AddDays(-2) : null,
            BlocksRedispatch = blocks, SourceName = source, EventId = eventId
        };
        _cases.Save(c);
        return c;
    }

    // ── 判定與閘門共用 ───────────────────────────────────────────────────────

    [Fact]
    public void 清單與DismissedKeys對同一組案件結果一致_空值_false_true_已處理各一件()
    {
        var host = Host("HOST-A");
        Case("c-null", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix, blocks: null);
        Case("c-false", host.HostName, "disk", 2, IssueHandlingStatuses.WontFix, blocks: false);
        Case("c-true", host.HostName, "disk", 3, IssueHandlingStatuses.FalsePositive, blocks: true);
        Case("c-resolved", host.HostName, "disk", 4, IssueHandlingStatuses.Resolved, blocks: null);

        var all = _cases.GetMany(new[] { host.HostName });
        var gateKeys = DispatchContext.DismissedKeys(all);
        var listed = Service().GetList(1).Groups.SelectMany(g => g.Rows).Select(r => r.IssueKey).ToHashSet(IssueSignatureKeyComparer.Instance);

        Assert.Equal(gateKeys.OrderBy(k => k), listed.OrderBy(k => k));
        Assert.Equal(new[] { Key("disk", 1), Key("disk", 3) }.OrderBy(k => k), listed.OrderBy(k => k));
    }

    [Fact]
    public void 最新案件已處理或進行中時舊的不處理案件不列出()
    {
        var host = Host("HOST-A");
        Case("old", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix, createdAt: Now.AddDays(-10));
        Case("new", host.HostName, "disk", 1, IssueHandlingStatuses.InProgress, createdAt: Now.AddDays(-1));

        Assert.Empty(Service().GetList(1).Groups);
        Assert.Empty(DispatchContext.DismissedKeys(_cases.GetMany(new[] { host.HostName })));
    }

    [Fact]
    public void 較新的已取消案件不解除閘門_取最新時略過Cancelled()
    {
        var host = Host("HOST-A");
        Case("dismissed", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix, createdAt: Now.AddDays(-10));
        // 管理者對被擋主機手動建單、之後取消：取消案件建立較晚，但不是處理人的判定
        var cancelled = Case("cancelled", host.HostName, "disk", 1, IssueHandlingStatuses.Open, createdAt: Now.AddDays(-1));
        cancelled.Cancelled = true;
        _cases.Save(cancelled);

        var all = _cases.GetMany(new[] { host.HostName });
        Assert.Equal("dismissed", Assert.Single(DispatchContext.DismissedCases(all)).CaseId);
        Assert.Contains(Key("disk", 1), DispatchContext.DismissedKeys(all));
        Assert.Single(Service().GetList(1).Groups.SelectMany(g => g.Rows));
    }

    [Fact]
    public void 已取消的不處理案件本身不構成閘門()
    {
        var host = Host("HOST-A");
        var c = Case("c", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix);
        c.Cancelled = true;
        _cases.Save(c);

        Assert.Empty(DispatchContext.DismissedKeys(_cases.GetMany(new[] { host.HostName })));
    }

    // ── 欄位、分組、分頁、可見範圍 ───────────────────────────────────────────

    [Fact]
    public void 每列帶主機_問題_先前結案狀態_結案的人與日期_並依問題分組()
    {
        var a = Host("HOST-A");
        var b = Host("HOST-B");
        var handler = _users.Upsert(new WebUser { Account = "h1", DisplayName = "王小明", Active = true });
        var closedAt = Now.AddDays(-2);
        var c = Case("c1", a.HostName, "disk", 153, IssueHandlingStatuses.KnownNoise, closedAt: closedAt);
        Case("c2", b.HostName, "disk", 153, IssueHandlingStatuses.WontFix);
        Case("c3", b.HostName, "ntfs", 55, IssueHandlingStatuses.FalsePositive);
        _handling.AppendLog(new RecordHandlingLog
        {
            HostName = a.HostName, Date = c.LastLinkedDate, Status = c.Status, IssueKey = c.IssueKey,
            ActorId = handler.UserId, ActorAccount = "h1", Action = HandlingActions.StatusChange, CreatedAt = closedAt
        });

        var dto = Service().GetList(1);

        Assert.Equal(2, dto.Total);
        Assert.Equal(2, dto.TotalHosts);
        var disk = dto.Groups[0];
        Assert.Equal("disk 153", disk.IssueLabel);
        Assert.Equal(2, disk.Hosts);
        var row = disk.Rows.Single(r => r.HostName == "HOST-A");
        Assert.Equal(IssueHandlingStatuses.KnownNoise, row.Status);
        Assert.Contains("王小明", row.ClosedBy);
        Assert.Equal(closedAt, row.ClosedAt);
        Assert.Equal(c.IssueKey, row.IssueKey);
        Assert.Null(disk.Rows.Single(r => r.HostName == "HOST-B").ClosedBy);
        Assert.Equal("ntfs 55", dto.Groups[1].IssueLabel);
    }

    [Fact]
    public void 分頁以問題組為單位_每頁50組()
    {
        var host = Host("HOST-A");
        for (var i = 1; i <= 51; i++) Case($"c{i}", host.HostName, "disk", i, IssueHandlingStatuses.WontFix);

        var page1 = Service().GetList(1);
        var page2 = Service().GetList(2);

        Assert.Equal(51, page1.Total);
        Assert.Equal(50, page1.PageSize);
        Assert.Equal(50, page1.Groups.Count);
        Assert.Single(page2.Groups);
        Assert.Equal(2, page2.Page);
        Assert.Empty(Service().GetList(3).Groups);
    }

    [Fact]
    public void 只列使用者看得到的主機()
    {
        var a = Host("HOST-A");
        var b = Host("HOST-B");
        Case("c1", a.HostName, "disk", 1, IssueHandlingStatuses.WontFix);
        Case("c2", b.HostName, "disk", 1, IssueHandlingStatuses.WontFix);

        var dto = Service(a.HostId).GetList(1);

        Assert.Equal(new[] { "HOST-A" }, dto.Groups.SelectMany(g => g.Rows).Select(r => r.HostName));
        Assert.Throws<DomainException>(() => Service(a.HostId).Restore("HOST-B", Key("disk", 1)));
        Assert.Throws<DomainException>(() => Service(a.HostId).RestoreIssue("disk", 99));
        Assert.Null(_cases.Get("c2")!.BlocksRedispatch);
    }

    // ── 恢復 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 恢復單筆只寫欄位為false_狀態與結案時間不動_同問題其他主機仍被擋()
    {
        var a = Host("HOST-A");
        var b = Host("HOST-B");
        var ca = Case("ca", a.HostName, "disk", 1, IssueHandlingStatuses.WontFix);
        Case("cb", b.HostName, "disk", 1, IssueHandlingStatuses.WontFix);
        var closedAt = ca.ClosedAt;

        var result = Service().Restore("HOST-A", Key("disk", 1));

        Assert.Equal(1, result.Restored);
        var saved = _cases.Get("ca")!;
        Assert.False(saved.BlocksRedispatch);
        Assert.Equal(IssueHandlingStatuses.WontFix, saved.Status);
        Assert.Equal(closedAt, saved.ClosedAt);
        Assert.Empty(DispatchContext.DismissedKeys(_cases.GetMany(new[] { "HOST-A" })));
        Assert.Contains(Key("disk", 1), DispatchContext.DismissedKeys(_cases.GetMany(new[] { "HOST-B" })));
        Assert.Equal(new[] { "HOST-B" }, Service().GetList(1).Groups.Single().Rows.Select(r => r.HostName));
    }

    [Fact]
    public void 恢復整個問題_該問題所有被擋主機一起恢復_其他問題不動()
    {
        var hosts = new[] { "HOST-A", "HOST-B", "HOST-C" }.Select(Host).ToList();
        foreach (var h in hosts) Case($"d-{h.HostName}", h.HostName, "disk", 1, IssueHandlingStatuses.WontFix);
        Case("n-A", "HOST-A", "ntfs", 2, IssueHandlingStatuses.KnownNoise);

        var result = Service().RestoreIssue("DISK", 1);

        Assert.Equal(3, result.Restored);
        foreach (var h in hosts)
            Assert.DoesNotContain(Key("disk", 1), DispatchContext.DismissedKeys(_cases.GetMany(new[] { h.HostName })));
        Assert.Contains(Key("ntfs", 2), DispatchContext.DismissedKeys(_cases.GetMany(new[] { "HOST-A" })));
        Assert.Equal("ntfs 2", Service().GetList(1).Groups.Single().IssueLabel);
    }

    [Fact]
    public void 恢復後處理人再回不處理是新案件_閘門重新生效()
    {
        var host = Host("HOST-A");
        Case("c1", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix, createdAt: Now.AddDays(-5));
        Service().Restore("HOST-A", Key("disk", 1));
        Assert.Empty(DispatchContext.DismissedKeys(_cases.GetMany(new[] { "HOST-A" })));

        // 之後再派工、處理人又回「不處理」：新案件的欄位是空值
        Case("c2", host.HostName, "disk", 1, IssueHandlingStatuses.WontFix, blocks: null, createdAt: Now.AddDays(-1));

        Assert.Contains(Key("disk", 1), DispatchContext.DismissedKeys(_cases.GetMany(new[] { "HOST-A" })));
        Assert.Single(Service().GetList(1).Groups.Single().Rows);
    }

    [Fact]
    public void 沒有被擋住的項目恢復擲NotFound()
    {
        var host = Host("HOST-A");
        Case("c1", host.HostName, "disk", 1, IssueHandlingStatuses.Resolved);

        var ex = Assert.Throws<DomainException>(() => Service().Restore("HOST-A", Key("disk", 1)));
        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
        Assert.Throws<DomainException>(() => Service().RestoreIssue("disk", 1));
        Assert.Empty(_audit.Entries);
    }

    // ── 稽核 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 恢復寫稽核_單筆摘要含主機與問題_整個問題含台數()
    {
        var a = Host("HOST-A");
        var b = Host("HOST-B");
        Case("ca", a.HostName, "disk", 153, IssueHandlingStatuses.WontFix);
        Case("cb", b.HostName, "disk", 153, IssueHandlingStatuses.WontFix);
        Case("cc", a.HostName, "ntfs", 55, IssueHandlingStatuses.WontFix);

        Service().Restore("HOST-A", Key("ntfs", 55));
        Service().RestoreIssue("disk", 153);

        Assert.Equal(2, _audit.Entries.Count);
        Assert.All(_audit.Entries, e => Assert.Equal(AuditActions.WorkOrderDismissRestore, e.Action));
        Assert.Contains("HOST-A", _audit.Entries[0].Summary);
        Assert.Contains("ntfs 55", _audit.Entries[0].Summary);
        Assert.Contains("disk 153", _audit.Entries[1].Summary);
        Assert.Contains("2 台", _audit.Entries[1].Summary);
    }
}
