using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 待派頁「不再自動派工」清單的恢復標示（<see cref="DismissedIssueService"/>）：N 天後恢復派工、上線前判定、永久三種。
/// 規則走內建種子（disk 153＝高嚴重度、Time-Service 29＝中，見 KnownIssueSeed），時鐘固定在生效日期之後。
/// </summary>
public sealed class DismissedIssueReleaseListTests
{
    private static readonly DateTime Closed = new(2026, 12, 1, 10, 0, 0);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeHandlingStore _handling = new();
    private readonly FakeSystemSettingsStore _settings = new();

    private DismissedIssueService Service(DateTime today) => new(
        _cases, _handling, _hosts, _users, new FakeRuleStore(),
        new FixedVisibilityService(_hosts.GetAll().Select(h => h.HostId)),
        new RecordingAuditService(), new UserDisplayNameService(_settings), new FixedLocalClock(today));

    private void Case(string id, string host, string source, int eventId, DateTime closedAt, bool? blocks = null)
    {
        _hosts.Upsert(new WebHost { HostName = host });
        _cases.Save(new IssueCase
        {
            CaseId = id, HostName = host, IssueKey = IssueSignatureKey.For("System", source, eventId, LogEntryType.Error),
            IssueLabel = $"{source} {eventId}", Status = IssueHandlingStatuses.WontFix,
            CreatedAt = closedAt.AddDays(-3), UpdatedAt = closedAt, ClosedAt = closedAt,
            FirstLinkedDate = closedAt.Date.AddDays(-3), LastLinkedDate = closedAt.Date.AddDays(-3),
            BlocksRedispatch = blocks, SourceName = source, EventId = eventId
        });
    }

    private static DismissedRowDto RowOf(DismissedListDto dto, string host, string label) =>
        dto.Groups.Single(g => g.IssueLabel == label).Rows.Single(r => r.HostName == host);

    [Fact]
    public void 清單三種標示_高嚴重度有剩餘天數_上線前判定_其餘永久()
    {
        Case("high", "HOST-HIGH", "disk", 153, Closed);                                          // 高嚴重度、處理人回覆
        Case("pre", "HOST-PRE", "disk", 153, new DateTime(2026, 11, 10));                        // 上線前判定
        Case("admin", "HOST-ADMIN", "disk", 153, Closed, blocks: true);                          // 管理員明確勾選
        Case("medium", "HOST-MID", "Time-Service", 29, Closed);                                          // 中等以下嚴重度

        var dto = Service(Closed.Date.AddDays(10)).GetList(1);

        Assert.Equal(21, RowOf(dto, "HOST-HIGH", "disk EventId 153").ReleaseInDays);
        Assert.False(RowOf(dto, "HOST-HIGH", "disk EventId 153").PreLaunch);

        var pre = RowOf(dto, "HOST-PRE", "disk EventId 153");
        Assert.True(pre.PreLaunch);
        Assert.Null(pre.ReleaseInDays);

        var admin = RowOf(dto, "HOST-ADMIN", "disk EventId 153");
        Assert.Null(admin.ReleaseInDays);
        Assert.False(admin.PreLaunch);

        var medium = RowOf(dto, "HOST-MID", "Time-Service EventId 29");
        Assert.Null(medium.ReleaseInDays);
        Assert.False(medium.PreLaunch);
    }

    [Fact]
    public void 第30天剩1天_第31天不再列出()
    {
        Case("high", "HOST-A", "disk", 153, Closed);

        Assert.Equal(1, RowOf(Service(Closed.Date.AddDays(30)).GetList(1), "HOST-A", "disk EventId 153").ReleaseInDays);
        Assert.Empty(Service(Closed.Date.AddDays(31)).GetList(1).Groups);
    }
}
