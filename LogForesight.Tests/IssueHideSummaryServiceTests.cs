using LogForesight.Core.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 N（F16-4）：問題負責與靜音頁的「不顯示設定總覽」。四個來源（靜音與結論、抑制、規則、不再打擾）
/// 各有與沒有的組合；數字與各原頁面一致（同一資料集比對）。規則走內建種子（disk 153＝高嚴重度、Time-Service 29＝中），時鐘固定。
/// </summary>
public sealed class IssueHideSummaryServiceTests
{
    private static readonly DateTime Closed = new(2026, 12, 1, 10, 0, 0);
    private static readonly DateTime Today = Closed.Date.AddDays(10);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeHandlingStore _handling = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeIssueAggregateQuery _aggregates = new();

    private DismissedIssueService Dismissed(DateTime today) => new(
        _cases, _handling, _hosts, _users, new FakeRuleStore(),
        new FixedVisibilityService(_hosts.GetAll().Select(h => h.HostId)),
        new RecordingAuditService(), new UserDisplayNameService(_settings), new FixedLocalClock(today));

    private IssueHideSummaryService Service(DateTime? today = null)
    {
        var t = today ?? Today;
        return new IssueHideSummaryService(_owners, _suppressions, new FakeRuleStore(), _aggregates, Dismissed(t), new FixedLocalClock(t));
    }

    private void Blocked(string id, string host, string source, int eventId, DateTime closedAt)
    {
        _hosts.Upsert(new WebHost { HostName = host });
        _cases.Save(new IssueCase
        {
            CaseId = id, HostName = host, IssueKey = IssueSignatureKey.For("System", source, eventId, LogEntryType.Error),
            IssueLabel = $"{source} {eventId}", Status = IssueHandlingStatuses.WontFix,
            CreatedAt = closedAt.AddDays(-3), UpdatedAt = closedAt, ClosedAt = closedAt,
            FirstLinkedDate = closedAt.Date.AddDays(-3), LastLinkedDate = closedAt.Date.AddDays(-3),
            SourceName = source, EventId = eventId
        });
    }

    private void Mute(string source, int eventId, DateTime from, DateTime to) =>
        _owners.Update(source, eventId, p => p.Mutes.Add(new MuteInterval { From = from, To = to, Reason = "維護", ByAccount = "admin" }));

    private static string RuleIdOf(string source, int eventId) =>
        KnownIssueCatalog.FindRuleByAggregateKey(KnownIssueCatalog.ResolveRules(new FakeRuleStore()), source, eventId)!.Id;

    private void Suppress(string ruleId, string scope, DateTime? expires = null, string host = "") =>
        _suppressions.SaveAll(_suppressions.LoadAll().Append(new RuleSuppression
        {
            RuleId = ruleId, TargetType = SuppressionTargetTypes.Rule, Scope = scope, Host = host, Reason = "雜訊", ExpiresAt = expires
        }).ToList());

    private void Recent(string source, int eventId) =>
        _aggregates.Result.Add(new IssueAggregate { Source = source, EventId = eventId, HostCount = 1, LastSeen = Today.AddDays(-1) });

    [Fact]
    public void 四個來源都沒有設定_清單是空的_也不查聚合()
    {
        Assert.Empty(Service().GetSummaries());
        Assert.Equal(0, _aggregates.AggregateCallCount);
    }

    [Fact]
    public void 只有靜音_列出迄日與原因_其他三欄是null()
    {
        Mute("disk", 153, Today.AddDays(-2), Today.AddDays(5));

        var row = Assert.Single(Service().GetSummaries());
        Assert.Equal(Today.AddDays(5), row.Mute!.To);
        Assert.Equal("維護", row.Mute.Reason);
        Assert.Null(row.Conclusion);
        Assert.Null(row.Suppression);
        Assert.Null(row.Dismissed);
    }

    [Fact]
    public void 靜音邊界_迄日當天仍算_隔天不算()
    {
        Mute("disk", 153, Today.AddDays(-5), Today);
        Assert.Single(Service().GetSummaries());
        Assert.Empty(Service(Today.AddDays(1)).GetSummaries());
    }

    [Fact]
    public void 只有機房結論_列出狀態文字與自動套用()
    {
        _owners.Update("disk", 153, p => { p.ConclusionStatus = IssueHandlingStatuses.KnownNoise; p.ConclusionNote = "已知"; p.AutoApply = true; });

        var row = Assert.Single(Service().GetSummaries());
        Assert.Equal(IssueHandlingStatuses.KnownNoise, row.Conclusion!.Status);
        Assert.Equal(HandlingTextHelpers.IssueStatusText(IssueHandlingStatuses.KnownNoise), row.Conclusion.StatusText);
        Assert.True(row.Conclusion.AutoApply);
        Assert.Null(row.Mute);
    }

    [Fact]
    public void 只有負責人_沒有任何不顯示設定_不列出()
    {
        _owners.Update("disk", 153, p => p.OwnerUserIds = new List<long> { 1 });
        Assert.Empty(Service().GetSummaries());
    }

    [Fact]
    public void 規則抑制_以近期出現的問題反查_依範圍計數_到期的不算()
    {
        var ruleId = RuleIdOf("disk", 153);
        Recent("disk", 153);
        Suppress(ruleId, SuppressionScopes.Site);
        Suppress(ruleId, SuppressionScopes.Group);
        Suppress(ruleId, SuppressionScopes.Host, host: "H1");
        Suppress(ruleId, SuppressionScopes.Host, host: "H2");
        Suppress(ruleId, SuppressionScopes.Host, expires: Today.AddDays(-1), host: "H3");   // 已到期：不算

        var row = Assert.Single(Service().GetSummaries());
        Assert.Equal((4, 1, 1, 2), (row.Suppression!.Total, row.Suppression.SiteCount, row.Suppression.GroupCount, row.Suppression.HostCount));
        Assert.Equal(ruleId, row.Suppression.RuleId);
        Assert.Equal(1, _aggregates.AggregateCallCount);
    }

    [Fact]
    public void 抑制到期邊界_到期時刻之前算_到期當下不算()
    {
        var ruleId = RuleIdOf("disk", 153);
        Recent("disk", 153);
        Suppress(ruleId, SuppressionScopes.Site, expires: Today.AddDays(1));
        Assert.Single(Service().GetSummaries());
        Assert.Empty(Service(Today.AddDays(1)).GetSummaries());
    }

    [Fact]
    public void 規則型抑制_問題近期沒出現過_不在總覽_也不是別的問題的抑制()
    {
        Suppress(RuleIdOf("disk", 153), SuppressionScopes.Site);
        Recent("Time-Service", 29);   // 近期只有別條規則的問題

        Assert.Empty(Service().GetSummaries());
    }

    [Fact]
    public void 簽章抑制_由簽章鍵取得來源與事件編號_不必查聚合()
    {
        _suppressions.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Signature, Scope = SuppressionScopes.Host, Host = "H1",
                SignatureKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Error), TargetLabel = "disk", Reason = "x"
            }
        });

        var row = Assert.Single(Service().GetSummaries());
        Assert.Equal("disk", row.SourceName);
        Assert.Equal(153, row.EventId);
        Assert.Equal(1, row.Suppression!.HostCount);
        Assert.Equal(0, _aggregates.AggregateCallCount);
    }

    [Fact]
    public void 不再打擾_台數與可恢復台數_和待派頁清單同一資料集一致()
    {
        Blocked("high1", "HOST-1", "disk", 153, Closed);                          // 高嚴重度、處理人回覆：會恢復，剩 21 天
        Blocked("high2", "HOST-2", "disk", 153, Closed.AddDays(5));               // 剩 26 天
        Blocked("pre", "HOST-3", "disk", 153, new DateTime(2026, 11, 10));        // 上線前判定：永久
        Blocked("mid", "HOST-4", "Time-Service", 29, Closed);                     // 中等嚴重度：永久

        var summaries = Service().GetSummaries();
        var list = Dismissed(Today).GetList(1);

        var disk = summaries.Single(r => r.SourceName == "disk");
        Assert.Equal(list.Groups.Single(g => g.Source == "disk").Hosts, disk.Dismissed!.Hosts);
        Assert.Equal(3, disk.Dismissed.Hosts);
        Assert.Equal(2, disk.Dismissed.ReleasingHosts);
        Assert.Equal(21, disk.Dismissed.NextReleaseInDays);

        var time = summaries.Single(r => r.SourceName == "Time-Service");
        Assert.Equal(list.Groups.Single(g => g.Source == "Time-Service").Hosts, time.Dismissed!.Hosts);
        Assert.Equal(0, time.Dismissed.ReleasingHosts);
        Assert.Null(time.Dismissed.NextReleaseInDays);

        Assert.Equal(Dismissed(Today).GetCount().Issues, summaries.Count(r => r.Dismissed != null));
    }

    [Fact]
    public void 四種設定並存_同一個問題只有一列_大小寫不同的來源視為同一個問題()
    {
        Mute("DISK", 153, Today.AddDays(-1), Today.AddDays(3));
        _owners.Update("DISK", 153, p => p.ConclusionStatus = IssueHandlingStatuses.WontFix);
        Blocked("high1", "HOST-1", "disk", 153, Closed);
        Recent("disk", 153);
        Suppress(RuleIdOf("disk", 153), SuppressionScopes.Group);

        var row = Assert.Single(Service().GetSummaries());
        Assert.NotNull(row.Mute);
        Assert.NotNull(row.Conclusion);
        Assert.NotNull(row.Suppression);
        Assert.NotNull(row.Dismissed);
    }

    [Fact]
    public void 靜音中問題數與靜音中頁籤的計數相同()
    {
        Mute("disk", 153, Today.AddDays(-1), Today.AddDays(3));
        Mute("Time-Service", 29, Today.AddDays(-1), Today.AddDays(3));
        Mute("other", 1, Today.AddDays(-9), Today.AddDays(-2));   // 已過期

        var rows = Service().GetSummaries();
        var admin = new IssueOwnerAdminService(_owners, _aggregates, _users, new RecordingAuditService(), FakeCurrentUser.ForAccount("a"),
            new UserDisplayNameService(_settings), new FakeWorkOrderStore(_cases), null!, TestPermissionStamps.Shared, new FakeRuleStore(),
            clock: new FixedLocalClock(Today));
        Assert.Equal(admin.CountMuted().Count, rows.Count(r => r.Mute != null));
    }
}
