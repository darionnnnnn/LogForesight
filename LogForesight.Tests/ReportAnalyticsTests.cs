using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表後端三組統計（第 75 輪批次 D）：分桶、處理成效、類型趨勢、前期趨勢。
/// 資料建置沿用 <see cref="ReportServiceTests"/>：串真正的 EfAnalysisRecordStore＋RecordRepository，
/// 三態推導與 <c>summary.handling</c> 走同一套 DayHandlingDerivation，所以可以拿兩個端點交叉比對。
/// </summary>
public class ReportBucketingTests
{
    private static string F(DateTime d) => d.ToString("yyyy-MM-dd");

    [Fact]
    public void 十四天按日()
    {
        var b = ReportBucketing.Build(new DateTime(2026, 3, 1), new DateTime(2026, 3, 14));
        Assert.Equal(14, b.Count);
        Assert.All(b, x => Assert.Equal(1, x.Days));
    }

    [Fact]
    public void 十五天按週_頭尾不足七天照實際天數()
    {
        // 2026-01-07 是週三：週三～週日 5 天、整週 7 天、週一～週三 3 天
        var b = ReportBucketing.Build(new DateTime(2026, 1, 7), new DateTime(2026, 1, 21));
        Assert.Equal(new[] { 5, 7, 3 }, b.Select(x => x.Days).ToArray());
        Assert.Equal("2026-01-07", F(b[0].From));
        Assert.Equal("2026-01-11", F(b[0].To));
        Assert.Equal(DayOfWeek.Monday, b[1].From.DayOfWeek);
        Assert.Equal("2026-01-21", F(b[2].To));
    }

    [Fact]
    public void 一百二十天按週_一百二十一天按月()
    {
        var weekly = ReportBucketing.Build(new DateTime(2026, 1, 1), new DateTime(2026, 4, 30));   // 120 天
        Assert.Equal(DayOfWeek.Thursday, weekly[0].From.DayOfWeek);
        Assert.Equal(4, weekly[0].Days);                                                          // 週四～週日
        Assert.Equal(120, weekly.Sum(x => x.Days));

        var monthly = ReportBucketing.Build(new DateTime(2026, 1, 1), new DateTime(2026, 5, 1));   // 121 天
        Assert.Equal(new[] { 31, 28, 31, 30, 1 }, monthly.Select(x => x.Days).ToArray());
    }

    [Fact]
    public void 跨年按月_頭尾截在期間內()
    {
        var b = ReportBucketing.Build(new DateTime(2025, 12, 20), new DateTime(2026, 4, 20));
        Assert.Equal(new[] { 12, 31, 28, 31, 20 }, b.Select(x => x.Days).ToArray());
        Assert.Equal("2025-12-31", F(b[0].To));
        Assert.Equal("2026-01-01", F(b[1].From));
    }

    [Fact]
    public void 按週_起日是週日首桶只有一天_週桶跨年_起訖顛倒自動交換()
    {
        // 2025-12-28 是週日：首桶只有它一天，第二桶 12-29（一）～ 01-04（日）跨年
        var from = new DateTime(2025, 12, 28);
        var to = new DateTime(2026, 1, 25);
        var b = ReportBucketing.Build(from, to);
        Assert.Equal(new[] { 1, 7, 7, 7, 7 }, b.Select(x => x.Days).ToArray());
        Assert.Equal(("2025-12-29", "2026-01-04"), (F(b[1].From), F(b[1].To)));
        Assert.Equal(1, ReportBucketing.IndexOf(b, new DateTime(2026, 1, 1)));

        Assert.Equal(b, ReportBucketing.Build(to, from));
    }

    [Fact]
    public void 一天期間只有一桶()
    {
        var b = Assert.Single(ReportBucketing.Build(new DateTime(2026, 6, 15), new DateTime(2026, 6, 15)));
        Assert.Equal(1, b.Days);
        Assert.Equal(0, ReportBucketing.IndexOf(new[] { b }, new DateTime(2026, 6, 15)));
        Assert.Equal(-1, ReportBucketing.IndexOf(new[] { b }, new DateTime(2026, 6, 16)));
    }
}

public class ReportAnalyticsTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly EfRecordHandlingStore _handlingStore;
    private readonly EfIssueHandlingStore _issueHandlingStore;
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _visibleSettings = new();
    private GapsDto _gapsResult = new();
    private int _gapsCalls;
    private GapsDto GapsProvider(DateTime? from, DateTime? to, int page) { _gapsCalls++; return _gapsResult; }

    // 期間 D0 ～ D0+29（全在過去，lastDataDay 完全由資料決定）
    private static readonly DateTime D0 = DateTime.Today.AddDays(-60);
    private static readonly DateTime PeriodTo = D0.AddDays(29);

    public ReportAnalyticsTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _handlingStore = new EfRecordHandlingStore(_fixture.NewContext, new EfJsonLogStore(_fixture.NewContext, "handling"));
        _issueHandlingStore = new EfIssueHandlingStore(_fixture.NewContext);
    }

    public void Dispose() => _fixture.Dispose();

    /// <summary>一組服務（共用同一組 store，可指定可見範圍、靜音排除、快取與計數）</summary>
    private sealed class Rig
    {
        public required ReportService Summary { get; init; }
        public required ReportEffectivenessService Effectiveness { get; init; }
        public required ReportCategoryTrendService CategoryTrend { get; init; }
        public required ReportServiceTests.CountingProxy<IRecordRepository> RepoCalls { get; init; }
        public required ReportServiceTests.CountingProxy<IIssueHandlingStore> IssueStoreCalls { get; init; }
    }

    private Rig MakeRig(IVisibilityService? visibility = null, IssueExclusion? exclusion = null, SummaryCache? cache = null, ICurrentUser? user = null)
    {
        visibility ??= new AlwaysVisibleService(_hosts);
        cache ??= new SummaryCache(new DataVersionStamp());
        var exclusions = new FixedIssueExclusionSource(exclusion ?? IssueExclusion.None);

        var (repoProxy, repoCounter) = ReportServiceTests.CountingProxy<IRecordRepository>.Wrap(
            new RecordRepository(_recordStore, _hosts, visibility, _visibleSettings));
        var (issueProxy, issueCounter) = ReportServiceTests.CountingProxy<IIssueHandlingStore>.Wrap(_issueHandlingStore);

        var progress = new HandlingProgressCalculator(issueProxy, _handlingStore, _caseStore, _settingsStore, exclusions);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var handling = new HandlingHistoryQueryService(
            _handlingStore, issueProxy, _caseStore, _hosts, _users, visibility, _settingsStore, repoProxy, progress, aggregates,
            new UserDisplayNameService(_settingsStore), exclusions);
        var ranking = new IssueRankingBuilder(aggregates, _hosts, exclusions);

        return new Rig
        {
            Summary = new ReportService(repoProxy, _hosts, visibility, handling, ranking, _settingsStore, aggregates, _visibleSettings,
                cache, exclusions, new SqlPerformanceMonitor()),
            Effectiveness = new ReportEffectivenessService(repoProxy, _hosts, visibility, handling, issueProxy, _visibleSettings, cache, exclusions, new FakeRuleStore(), _caseStore, GapsProvider,
                user ?? FakeCurrentUser.WithCapabilities(Capability.Assign)),
            CategoryTrend = new ReportCategoryTrendService(repoProxy, _hosts, visibility, _visibleSettings, cache, exclusions),
            RepoCalls = repoCounter,
            IssueStoreCalls = issueCounter
        };
    }

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High,
        IssueCategory category = IssueCategory.Other) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = LogEntryType.Error,
        Severity = severity, Category = category, Count = 1
    };

    private void Rec(WebHost host, DateTime date, string risk, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = risk, TopIssues = issues.ToList()
        });

    private void Mark(WebHost host, DateTime date, LogIssueSignature issue, string status) =>
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = date, IssueKey = IssueSignatureKey.For(issue), Status = status, UpdatedAt = DateTime.Now
        });

    /// <summary>把最後資料日推到期間尾（觀察期判定以所有可見主機的最後紀錄日為準）</summary>
    private void Filler(DateTime date)
    {
        var filler = AddHost("FILLER-" + Guid.NewGuid().ToString("N")[..6]);
        Rec(filler, date, "低");
    }

    // ── 處理成效：分桶與交叉比對 ───────────────────────────────────────────

    [Fact]
    public void 處理成效_每桶三數相加等於Total_且全期間與summary_handling一致()
    {
        var h1 = AddHost("H1");
        var h2 = AddHost("H2");
        // 30 天 × 兩台：已處理／處理中／未處理混合，另有中風險與低風險日
        for (var i = 0; i < 30; i++)
        {
            var date = D0.AddDays(i);
            var issue = Issue("disk", 7);
            Rec(h1, date, i % 5 == 4 ? "中" : "高", issue);
            Rec(h2, date, i % 7 == 6 ? "低" : "高", Issue("net", 8));
            if (i % 3 == 0) Mark(h1, date, issue, IssueHandlingStatuses.Resolved);
            else if (i % 3 == 1) Mark(h1, date, issue, IssueHandlingStatuses.InProgress);
            if (i % 4 == 0) Mark(h2, date, Issue("net", 8), IssueHandlingStatuses.WontFix);
        }

        var rig = MakeRig();
        var eff = rig.Effectiveness.Get(D0, PeriodTo);
        var summary = rig.Summary.GetSummary(D0, PeriodTo);

        Assert.All(eff.Buckets, b => Assert.Equal(b.Total, b.Resolved + b.InProgress + b.Open));
        Assert.Equal(summary.Handling.TotalCount, eff.Buckets.Sum(b => b.Total));
        Assert.Equal(summary.Handling.ResolvedCount, eff.Buckets.Sum(b => b.Resolved));
        Assert.Equal(summary.Handling.InProgressCount, eff.Buckets.Sum(b => b.InProgress));
        Assert.Equal(summary.Handling.OpenCount, eff.Buckets.Sum(b => b.Open));
        Assert.Equal(summary.Handling.OpenCount + summary.Handling.InProgressCount, eff.UnresolvedTotal);
        Assert.Equal(eff.UnresolvedTotal, eff.Aging.Sum(a => a.Count));
        Assert.True(eff.Buckets.Sum(b => b.Total) > 0 && eff.UnresolvedTotal > 0);
    }

    [Fact]
    public void 處理成效_週數依起日而定_桶涵蓋整段期間()
    {
        AddHost("H1");
        var rig = MakeRig();
        var eff = rig.Effectiveness.Get(D0, PeriodTo);
        Assert.Equal(30, eff.Buckets.Sum(b => b.Days));
        Assert.Equal(ReportBucketing.Build(D0, PeriodTo).Count, eff.Buckets.Count);
    }

    // ── 未結案三段（K3）────────────────────────────────────────────────────

    /// <summary>
    /// 三段的主機日相加＝未結案主機日：在處理人手上（處理中）＋待派（未處理、該主機有可派問題）＋不需處理（其餘未處理）。
    /// 另帶案件數與最舊天數、可派的問題×主機（不同單位，不參與相加）。
    /// </summary>
    [Fact]
    public void 未結案三段_主機日相加等於未結案_並帶案件數與最舊天數與可派問題主機()
    {
        var inHand = AddHost("IN-HAND");
        var pending = AddHost("PENDING");
        var notNeeded = AddHost("NOT-NEEDED");
        var done = AddHost("DONE");
        var issue = Issue("disk", 7);
        // 處理中兩個主機日、待派主機兩個未處理日、不需處理主機三個未處理日、已處理主機一日（不算未結案）
        foreach (var i in new[] { 0, 1 }) { Rec(inHand, D0.AddDays(i), "高", issue); Mark(inHand, D0.AddDays(i), issue, IssueHandlingStatuses.InProgress); }
        foreach (var i in new[] { 0, 1 }) Rec(pending, D0.AddDays(i), "高", issue);
        foreach (var i in new[] { 0, 1, 2 }) Rec(notNeeded, D0.AddDays(i), "高", issue);
        Rec(done, D0, "高", issue);
        Mark(done, D0, issue, IssueHandlingStatuses.Resolved);
        // 在處理人手上：兩個進行中案件（一個 10 天前建、一個 3 天前建）；另一台不可見的主機不算（本測試全可見，故兩件都算）
        _caseStore.Save(new IssueCase
        {
            CaseId = "c1", HostName = inHand.HostName, IssueKey = IssueSignatureKey.For(issue), Status = IssueHandlingStatuses.InProgress,
            WorkOrderId = 1, CreatedAt = DateTime.Today.AddDays(-10)
        });
        _caseStore.Save(new IssueCase
        {
            CaseId = "c2", HostName = pending.HostName, IssueKey = IssueSignatureKey.For(issue), Status = IssueHandlingStatuses.InProgress,
            WorkOrderId = 1, CreatedAt = DateTime.Today.AddDays(-3)
        });
        _gapsResult = new GapsDto { DispatchableIssueHosts = 5 };
        _gapsResult.DispatchableHostNames.Add("PENDING");

        var eff = MakeRig().Effectiveness.Get(D0, D0.AddDays(6));
        var u = eff.Unresolved;

        Assert.Equal(7, eff.UnresolvedTotal);
        Assert.Equal(2, u.InHandlers.HostDays);
        Assert.Equal(2, u.PendingDispatch.HostDays);
        Assert.Equal(3, u.NotNeeded.HostDays);
        // 對照式：三段主機日相加＝未結案主機日
        Assert.Equal(eff.UnresolvedTotal, u.InHandlers.HostDays + u.PendingDispatch.HostDays!.Value + u.NotNeeded.HostDays!.Value);
        Assert.Equal(2, u.InHandlers.Cases);
        Assert.Equal(10, u.InHandlers.OldestDays);
        Assert.Equal(5, u.PendingDispatch.IssueHosts);
        Assert.False(u.PendingDispatch.TooLarge);
    }

    /// <summary>邊界：沒有進行中案件時最舊天數是 null；沒有可派主機時全部未處理都是「不需處理」</summary>
    [Fact]
    public void 未結案三段_沒有案件也沒有可派問題()
    {
        var host = AddHost("H1");
        Rec(host, D0, "高", Issue("disk", 7));

        var eff = MakeRig().Effectiveness.Get(D0, D0.AddDays(6));
        var u = eff.Unresolved;

        Assert.Equal(0, u.InHandlers.Cases);
        Assert.Null(u.InHandlers.OldestDays);
        Assert.Equal(0, u.PendingDispatch.HostDays);
        Assert.Equal(1, u.NotNeeded.HostDays);
        Assert.Equal(eff.UnresolvedTotal, u.InHandlers.HostDays + u.PendingDispatch.HostDays!.Value + u.NotNeeded.HostDays!.Value);
    }

    /// <summary>待派試跑超過上限時拆不開：標 TooLarge，待派與不需處理的主機日為 null，處理中那段仍照算</summary>
    [Fact]
    public void 未結案三段_待派試跑超過上限時標TooLarge_不猜()
    {
        var host = AddHost("H1");
        var issue = Issue("disk", 7);
        Rec(host, D0, "高", issue);
        Mark(host, D0, issue, IssueHandlingStatuses.InProgress);
        Rec(host, D0.AddDays(1), "高", issue);
        _gapsResult = new GapsDto { TooLarge = true };

        var u = MakeRig().Effectiveness.Get(D0, D0.AddDays(6)).Unresolved;

        Assert.True(u.PendingDispatch.TooLarge);
        Assert.Null(u.PendingDispatch.HostDays);
        Assert.Null(u.NotNeeded.HostDays);
        Assert.Equal(1, u.InHandlers.HostDays);
    }

    /// <summary>
    /// 待派拆分的條件：檢視者具 Assign 且期間不超過待派預設窗口（7 天）才跑試跑；邊界兩側各一例，
    /// 沒跑時兩段主機日為 null、標 TooLarge，處理中那段照算。
    /// </summary>
    [Fact]
    public void 未結案三段_待派拆分只在具Assign且期間不超過預設窗口時跑試跑()
    {
        var host = AddHost("H1");
        var issue = Issue("disk", 7);
        Rec(host, D0, "高", issue);
        _gapsResult = new GapsDto { DispatchableIssueHosts = 1 };
        _gapsResult.DispatchableHostNames.Add("H1");

        // 具 Assign、期間剛好 7 天：跑試跑
        _gapsCalls = 0;
        var inWindow = MakeRig().Effectiveness.Get(D0, D0.AddDays(6)).Unresolved;
        Assert.Equal(1, _gapsCalls);
        Assert.False(inWindow.PendingDispatch.TooLarge);
        Assert.Equal(1, inWindow.PendingDispatch.HostDays);

        // 期間 8 天（超過一天）：不跑
        _gapsCalls = 0;
        var tooLong = MakeRig().Effectiveness.Get(D0, D0.AddDays(7)).Unresolved;
        Assert.Equal(0, _gapsCalls);
        Assert.True(tooLong.PendingDispatch.TooLarge);
        Assert.Null(tooLong.PendingDispatch.HostDays);
        Assert.Null(tooLong.NotNeeded.HostDays);

        // 沒有 Assign（期間在窗口內）：不跑
        _gapsCalls = 0;
        var noAssign = MakeRig(user: FakeCurrentUser.WithCapabilities(Capability.ViewAll)).Effectiveness.Get(D0, D0.AddDays(6)).Unresolved;
        Assert.Equal(0, _gapsCalls);
        Assert.True(noAssign.PendingDispatch.TooLarge);
    }

    /// <summary>快取鍵含檢視者是否具 Assign：同一期間、同一可見範圍，具 Assign 者的拆分不能端給沒有 Assign 的人</summary>
    [Fact]
    public void 未結案三段_快取不在有無Assign的檢視者之間共用()
    {
        var host = AddHost("H1");
        Rec(host, D0, "高", Issue("disk", 7));
        _gapsResult = new GapsDto { DispatchableIssueHosts = 1 };
        var cache = new SummaryCache(new DataVersionStamp());

        var assigner = MakeRig(cache: cache).Effectiveness.Get(D0, D0.AddDays(6)).Unresolved;
        var viewer = MakeRig(cache: cache, user: FakeCurrentUser.WithCapabilities(Capability.ViewAll)).Effectiveness.Get(D0, D0.AddDays(6)).Unresolved;

        Assert.False(assigner.PendingDispatch.TooLarge);
        Assert.True(viewer.PendingDispatch.TooLarge);
    }

    // ── 帳齡 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 帳齡邊界_第3_4_7_8_14_15_30_31天落在正確的段()
    {
        var asOf = PeriodTo;
        var days = new[] { 3, 4, 7, 8, 14, 15, 30, 31 };
        var host = AddHost("H1");
        Rec(host, asOf, "高", Issue("anchor", 1));   // AsOf 當天的未處理日（帳齡 0）
        // 31 天前在 D0（asOf-29）之前，期間要放大才放得下
        var from = asOf.AddDays(-40);
        foreach (var k in days)
        {
            var other = AddHost("H-" + k);
            Rec(other, asOf.AddDays(-k), "高", Issue("x", k));
        }

        var eff = MakeRig().Effectiveness.Get(from, asOf);

        Assert.Equal(asOf.ToString("yyyy-MM-dd"), eff.AsOf);
        // 0–3：asOf 當天與第 3 天；4–7：4、7；8–14：8、14；15–30：15、30；31+：31
        Assert.Equal(new[] { 2, 2, 2, 2, 1 }, eff.Aging.Select(a => a.Count).ToArray());
        Assert.Equal(new[] { "0–3", "4–7", "8–14", "15–30", "31+" }, eff.Aging.Select(a => a.Label).ToArray());
        Assert.Null(eff.Aging[4].MaxDays);
        Assert.Equal(9, eff.UnresolvedTotal);
    }

    [Fact]
    public void 帳齡_沒有未結案時五段皆為0()
    {
        var host = AddHost("H1");
        var issue = Issue("disk", 1);
        Rec(host, D0, "高", issue);
        Mark(host, D0, issue, IssueHandlingStatuses.Resolved);

        var eff = MakeRig().Effectiveness.Get(D0, PeriodTo);

        Assert.Equal(0, eff.UnresolvedTotal);
        Assert.Equal(5, eff.Aging.Count);
        Assert.All(eff.Aging, a => Assert.Equal(0, a.Count));
    }

    // ── 再發生 ───────────────────────────────────────────────────────────

    private static readonly DateTime ResolvedDay = D0.AddDays(5);

    private (WebHost Host, LogIssueSignature Issue) SeedResolved(string status = IssueHandlingStatuses.Resolved, string hostName = "H1")
    {
        var host = AddHost(hostName);
        var issue = Issue("disk", 7);
        Rec(host, ResolvedDay, "高", issue);
        Mark(host, ResolvedDay, issue, status);
        return (host, issue);
    }

    [Fact]
    public void 再發生_D後第14天出現算_第15天不算()
    {
        var (host, issue) = SeedResolved();
        Rec(host, ResolvedDay.AddDays(14), "中", issue);
        Filler(PeriodTo);
        var recurred = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((1, 0, 0), (recurred.Recurred, recurred.NotRecurred, recurred.Observing));
        Assert.Equal(1.0, recurred.Rate);
        var top = Assert.Single(recurred.TopRecurring);
        Assert.Equal("disk", top.Source);
        Assert.Equal((1, 1), (top.HostCount, top.RecurredCount));
    }

    [Fact]
    public void 再發生_第15天才出現不算再發生()
    {
        var (host, issue) = SeedResolved();
        Rec(host, ResolvedDay.AddDays(15), "中", issue);
        Filler(PeriodTo);
        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 1, 0), (r.Recurred, r.NotRecurred, r.Observing));
        Assert.Equal(0.0, r.Rate);
        Assert.Empty(r.TopRecurring);
    }

    [Theory]
    [InlineData(IssueHandlingStatuses.WontFix)]
    [InlineData(IssueHandlingStatuses.FalsePositive)]
    [InlineData(IssueHandlingStatuses.KnownNoise)]
    public void 再發生_不處理誤報已知雜訊不進分母(string status)
    {
        var (host, issue) = SeedResolved(status);
        Rec(host, ResolvedDay.AddDays(3), "中", issue);
        Filler(PeriodTo);
        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 0, 0), (r.Recurred, r.NotRecurred, r.Observing));
        Assert.Null(r.Rate);
    }

    [Fact]
    public void 再發生_觀察未滿且未再發生算觀察中_不進分母_Rate為null()
    {
        var host = AddHost("H1");
        var issue = Issue("disk", 7);
        var d = D0.AddDays(25);            // D+14 超過最後資料日（D0+29）
        Rec(host, d, "高", issue);
        Mark(host, d, issue, IssueHandlingStatuses.Resolved);
        Filler(PeriodTo);

        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 0, 1), (r.Recurred, r.NotRecurred, r.Observing));
        Assert.Null(r.Rate);
    }

    [Fact]
    public void 再發生_同組合多次已處理取最後一次()
    {
        var (host, issue) = SeedResolved();
        var later = ResolvedDay.AddDays(10);
        Rec(host, later, "高", issue);
        Mark(host, later, issue, IssueHandlingStatuses.Resolved);
        Rec(host, ResolvedDay.AddDays(4), "中", issue);   // 在第一次處理後 4 天出現，但最後一次是 +10
        Filler(PeriodTo);

        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 1, 0), (r.Recurred, r.NotRecurred, r.Observing));
    }

    [Fact]
    public void 再發生_別台主機的同一問題不算()
    {
        var (_, issue) = SeedResolved();
        var other = AddHost("H2");
        Rec(other, ResolvedDay.AddDays(3), "中", issue);
        Filler(PeriodTo);

        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 1, 0), (r.Recurred, r.NotRecurred, r.Observing));
    }

    [Fact]
    public void 再發生_D當天同一筆不算()
    {
        SeedResolved();   // D 當天本來就有這筆紀錄
        Filler(PeriodTo);
        var r = MakeRig().Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 1, 0), (r.Recurred, r.NotRecurred, r.Observing));
    }

    [Fact]
    public void 再發生_D之後再出現但當天被靜音不算()
    {
        var (host, issue) = SeedResolved();
        var again = ResolvedDay.AddDays(3);
        Rec(host, again, "中", issue);
        Filler(PeriodTo);
        var exclusion = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "disk", EventId = 7, Mutes = { new MuteInterval { From = again, To = again } } }
        }, DateTime.Today);

        var r = MakeRig(exclusion: exclusion).Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 1, 0), (r.Recurred, r.NotRecurred, r.Observing));
    }

    [Fact]
    public void 再發生_已處理那天就被靜音的問題不算一組()
    {
        // 整段期間靜音：之後的「又出現」永遠看不到，算進分母會一律落在「未再發生」把比例往下拉
        var (host, issue) = SeedResolved();
        Rec(host, ResolvedDay.AddDays(3), "中", issue);
        Filler(PeriodTo);
        var exclusion = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "disk", EventId = 7, Mutes = { new MuteInterval { From = D0, To = PeriodTo } } }
        }, DateTime.Today);

        var r = MakeRig(exclusion: exclusion).Effectiveness.Get(D0, PeriodTo).Recurrence;
        Assert.Equal((0, 0, 0), (r.Recurred, r.NotRecurred, r.Observing));
        Assert.Null(r.Rate);
    }

    [Fact]
    public void 再發生_查詢次數不隨組合數成長()
    {
        void Seed(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var host = AddHost("BULK-" + i);
                var issue = Issue("disk", 7);
                Rec(host, ResolvedDay, "高", issue);
                Mark(host, ResolvedDay, issue, IssueHandlingStatuses.Resolved);
                Rec(host, ResolvedDay.AddDays(2), "中", issue);
            }
        }

        Seed(0, 10);
        Filler(PeriodTo);
        var small = MakeRig();
        var smallResult = small.Effectiveness.Get(D0, PeriodTo);
        Assert.Equal(10, smallResult.Recurrence.Recurred);

        Seed(10, 1000);
        var large = MakeRig();
        var largeResult = large.Effectiveness.Get(D0, PeriodTo);
        Assert.Equal(1000, largeResult.Recurrence.Recurred);

        Assert.Equal(1, small.RepoCalls.Calls.GetValueOrDefault("QueryLightweight"));
        Assert.Equal(1, small.IssueStoreCalls.Calls.GetValueOrDefault("GetMany"));
        Assert.Equal(small.RepoCalls.Calls.GetValueOrDefault("QueryLightweight"), large.RepoCalls.Calls.GetValueOrDefault("QueryLightweight"));
        Assert.Equal(small.IssueStoreCalls.Calls.GetValueOrDefault("GetMany"), large.IssueStoreCalls.Calls.GetValueOrDefault("GetMany"));
    }

    // ── 類型趨勢 ─────────────────────────────────────────────────────────

    [Fact]
    public void 類型趨勢_問題日去重_Cells加總等於Total_依Total降冪()
    {
        var a = AddHost("A");
        var b = AddHost("B");
        var disk = Issue("disk", 1, IssueSeverity.Medium, IssueCategory.Storage);
        var auth = Issue("auth", 2, IssueSeverity.High, IssueCategory.Security);
        var from = D0;
        var to = D0.AddDays(9);   // 10 天，按日
        Rec(a, D0, "高", disk, auth, disk);              // 同主機同日同問題 disk 出現兩次，只算一次
        Rec(a, D0.AddDays(1), "中", disk);
        Rec(b, D0, "中", disk);

        var trend = MakeRig().CategoryTrend.Get(from, to);

        Assert.Equal(10, trend.Buckets.Count);
        Assert.Equal(new[] { "Storage", "Security" }, trend.Rows.Select(r => r.Category).ToArray());
        var storage = trend.Rows[0];
        Assert.Equal(3, storage.Total);
        Assert.Equal(storage.Total, storage.Cells.Sum());
        Assert.Equal(new[] { 2, 1 }, storage.Cells.Take(2).ToArray());
        Assert.Equal(2, trend.CellMax);
    }

    [Fact]
    public void 類型趨勢_被隱藏的嚴重度不計入_Total為0的類別不列()
    {
        var a = AddHost("A");
        Rec(a, D0, "高", Issue("disk", 1, IssueSeverity.Medium, IssueCategory.Storage), Issue("auth", 2, IssueSeverity.High, IssueCategory.Security));
        _visibleSettings.VisibleSeverities = new HashSet<string> { "High" };

        var trend = MakeRig().CategoryTrend.Get(D0, D0.AddDays(9));

        Assert.Equal("Security", Assert.Single(trend.Rows).Category);
    }

    [Fact]
    public void 類型趨勢_空期間回空Rows但桶仍完整()
    {
        AddHost("A");
        var trend = MakeRig().CategoryTrend.Get(D0, D0.AddDays(9));
        Assert.Empty(trend.Rows);
        Assert.Equal(0, trend.CellMax);
        Assert.Equal(10, trend.Buckets.Count);
    }

    // ── 前期趨勢 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("all")]
    [InlineData("unresolved")]
    public void 前期趨勢_長度與本期相同_DayIndex對齊(string scope)
    {
        var host = AddHost("H1");
        var from = DateTime.Today.AddDays(-20);
        var to = DateTime.Today.AddDays(-11);               // 10 天；前期 -30 ～ -21
        Rec(host, DateTime.Today.AddDays(-28), "高", Issue("disk", 1));
        Rec(host, DateTime.Today.AddDays(-25), "中", Issue("disk", 2));
        Rec(host, DateTime.Today.AddDays(-18), "高", Issue("disk", 3));

        var s = MakeRig().Summary.GetSummary(from, to, scope);

        Assert.Equal(s.Trend.Count, s.PreviousTrend.Count);
        Assert.Equal(10, s.PreviousTrend.Count);
        Assert.Equal(Enumerable.Range(0, 10), s.PreviousTrend.Select(p => p.DayIndex));
        var day2 = s.PreviousTrend[2];
        Assert.Equal(DateTime.Today.AddDays(-28).ToString("yyyy-MM-dd"), day2.Date);
        Assert.Equal((1, 0), (day2.HighRisk, day2.MediumRisk));
        Assert.Equal((0, 1), (s.PreviousTrend[5].HighRisk, s.PreviousTrend[5].MediumRisk));
        Assert.Equal(1, s.Trend[2].HighRisk);                 // 本期同索引：-18
    }

    [Fact]
    public void 前期趨勢_整段超出保留期回空陣列()
    {
        _settingsStore.Update(x => x.RetentionDays = 180);
        var s = MakeRig().Summary.GetSummary(DateTime.Today.AddDays(-180), DateTime.Today.AddDays(-179), null, "previous");
        Assert.True(s.ComparisonOutOfRetention);
        Assert.Empty(s.PreviousTrend);
        Assert.Equal(2, s.Trend.Count);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("unresolved")]
    public void 前期趨勢_部分超出保留期_早於保留線的日子為null_保留線當天起為數字(string scope)
    {
        _settingsStore.Update(x => x.RetentionDays = 180);
        var host = AddHost("H1");
        var threshold = DateTime.Today.AddDays(-180);
        // 前期起點早於保留線 3 天：前期 T-183 ～ T-174，本期 T-173 ～ T-164
        var from = threshold.AddDays(7);
        var to = from.AddDays(9);
        Rec(host, threshold, "高", Issue("disk", 1));                   // 保留線當天算有資料
        Rec(host, threshold.AddDays(2), "高", Issue("disk", 2));        // 其餘日子（含 +1）沒有高風險＝真的 0

        var s = MakeRig().Summary.GetSummary(from, to, scope, "previous");

        Assert.True(s.ComparisonPartiallyOutOfRetention);
        Assert.Equal(10, s.PreviousTrend.Count);
        Assert.All(s.PreviousTrend.Take(3), p => { Assert.Null(p.HighRisk); Assert.Null(p.MediumRisk); });
        Assert.All(s.PreviousTrend.Skip(3), p => { Assert.NotNull(p.HighRisk); Assert.NotNull(p.MediumRisk); });
        Assert.Equal(1, s.PreviousTrend[3].HighRisk);                   // 保留線當天
        Assert.Equal(0, s.PreviousTrend[4].HighRisk);                   // 真的沒有高風險
        Assert.Equal(1, s.PreviousTrend[5].HighRisk);
    }

    [Fact]
    public void 前期趨勢_完全在保留期內_沒有null()
    {
        _settingsStore.Update(x => x.RetentionDays = 180);
        AddHost("H1");
        var from = DateTime.Today.AddDays(-20);
        var s = MakeRig().Summary.GetSummary(from, from.AddDays(9), null, "previous");
        Assert.False(s.ComparisonPartiallyOutOfRetention);
        Assert.All(s.PreviousTrend, p => { Assert.NotNull(p.HighRisk); Assert.NotNull(p.MediumRisk); });
    }

    [Fact]
    public void 前期趨勢_序列化後早於保留線的日子是null而不是缺鍵或0()
    {
        _settingsStore.Update(x => x.RetentionDays = 180);
        AddHost("H1");
        var from = DateTime.Today.AddDays(-180).AddDays(7);
        var s = MakeRig().Summary.GetSummary(from, from.AddDays(9), null, "previous");

        var json = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var prev = doc.RootElement.GetProperty("previousTrend");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, prev[i].GetProperty("highRisk").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, prev[i].GetProperty("mediumRisk").ValueKind);
        }
        Assert.Equal(System.Text.Json.JsonValueKind.Number, prev[3].GetProperty("highRisk").ValueKind);
    }

    // ── 可見範圍與快取 ───────────────────────────────────────────────────

    /// <summary>A、B 兩台各有已處理後再發生的問題；B 的數量刻意較多，漏套範圍時數字明顯偏大</summary>
    private (WebHost A, WebHost B) SeedTwoHosts()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        var issueA = Issue("only-a", 1, IssueSeverity.High, IssueCategory.Storage);
        var issueB = Issue("only-b", 2, IssueSeverity.High, IssueCategory.Security);
        Rec(a, ResolvedDay, "高", issueA);
        Mark(a, ResolvedDay, issueA, IssueHandlingStatuses.Resolved);
        Rec(a, ResolvedDay.AddDays(2), "中", issueA);
        for (var i = 0; i < 3; i++)
        {
            var d = ResolvedDay.AddDays(i * 3);
            Rec(b, d, "高", issueB);
            Mark(b, d, issueB, IssueHandlingStatuses.Resolved);
        }
        Rec(b, ResolvedDay.AddDays(10), "中", issueB);
        Filler(PeriodTo);
        return (a, b);
    }

    [Fact]
    public void 可見範圍_三個端點都只算授權的主機()
    {
        var (a, _) = SeedTwoHosts();
        var all = MakeRig();
        var scoped = MakeRig(new HostScopedVisibility(_hosts, a.HostId));

        // 對照組：不限範圍時兩台都算
        var allEff = all.Effectiveness.Get(D0, PeriodTo);
        Assert.Contains(allEff.Recurrence.TopRecurring, t => t.Source == "only-b");

        var eff = scoped.Effectiveness.Get(D0, PeriodTo);
        Assert.DoesNotContain(eff.Recurrence.TopRecurring, t => t.Source == "only-b");
        Assert.Contains(eff.Recurrence.TopRecurring, t => t.Source == "only-a");
        Assert.Equal(1, eff.Recurrence.Recurred + eff.Recurrence.NotRecurred + eff.Recurrence.Observing);
        Assert.Equal(2, eff.Buckets.Sum(b => b.Total));            // A 的 2 個風險日（高＋中）

        var trend = scoped.CategoryTrend.Get(D0, PeriodTo);
        Assert.Equal("Storage", Assert.Single(trend.Rows).Category);
        Assert.Equal(2, trend.Rows[0].Total);

        var summary = scoped.Summary.GetSummary(D0, PeriodTo);
        Assert.Equal(2, summary.Handling.TotalCount);
    }

    [Fact]
    public void 快取_兩個不同可見範圍先後查同一期間_第二個不含第一個獨有的資料()
    {
        var (a, b) = SeedTwoHosts();
        var shared = new SummaryCache(new DataVersionStamp());
        var rigA = MakeRig(new HostScopedVisibility(_hosts, a.HostId), cache: shared);
        var rigB = MakeRig(new HostScopedVisibility(_hosts, b.HostId), cache: shared);

        // 先 A 後 B
        var effA = rigA.Effectiveness.Get(D0, PeriodTo);
        var effB = rigB.Effectiveness.Get(D0, PeriodTo);
        Assert.Contains(effA.Recurrence.TopRecurring, t => t.Source == "only-a");
        Assert.DoesNotContain(effB.Recurrence.TopRecurring, t => t.Source == "only-a");
        Assert.Contains(effB.Recurrence.TopRecurring, t => t.Source == "only-b");

        var trendA = rigA.CategoryTrend.Get(D0, PeriodTo);
        var trendB = rigB.CategoryTrend.Get(D0, PeriodTo);
        Assert.Equal("Storage", Assert.Single(trendA.Rows).Category);
        Assert.Equal("Security", Assert.Single(trendB.Rows).Category);

        var sumA = rigA.Summary.GetSummary(D0, PeriodTo);
        var sumB = rigB.Summary.GetSummary(D0, PeriodTo);
        Assert.Equal(2, sumA.Handling.TotalCount);
        Assert.Equal(4, sumB.Handling.TotalCount);
        Assert.Equal(sumA.Trend.Sum(t => t.HighRisk + t.MediumRisk), sumA.Handling.TotalCount);
    }
}
