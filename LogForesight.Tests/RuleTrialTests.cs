using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規則試算（F19-2）：草稿規則套在近 30 天既有問題子列上，現在→試算的「命中主機日」與「會被派工的（主機, 問題）」。
/// 驗收重點：門檻調高時，試算結果＝以新規則實際重新分析（<see cref="KnownIssueCatalog.Classify"/>）同一份資料的結果；
/// 門檻調低時標示為下限；試算前後資料庫無變更。
/// 規則表是行程內的靜態狀態（<see cref="KnownIssueCatalog.Initialize"/>），與其他碰它的測試序列化。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class RuleTrialTests : IDisposable
{
    private const string RuleId = "custom-trial";
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeRuleStore _rules = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly List<(WebHost Host, int DaysAgo, int Count)> _seeded = new();
    private readonly List<KnownIssueRule> _originalCatalog = KnownIssueCatalog.Rules;

    public RuleTrialTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
        _rules.Content = new RuleFileContent { SeedVersion = 1, Rules = new List<KnownIssueRule> { Rule(threshold: 3, IssueSeverity.Medium, elevates: false) } };
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(_originalCatalog);
        _fx.Dispose();
    }

    private static KnownIssueRule Rule(int threshold, IssueSeverity severity, bool elevates, bool enabled = true) => new()
    {
        Id = RuleId, Origin = "custom", Enabled = enabled, SourcePattern = "TrialApp", EventIds = new[] { 7001 },
        Category = IssueCategory.Service, Severity = severity, ElevatesDayRisk = elevates, Description = "試算用規則", CountThreshold = threshold
    };

    private static SaveRuleRequest Draft(int threshold, string severity = "Medium", bool elevates = false, bool enabled = true) => new()
    {
        Id = RuleId, Enabled = enabled, SourcePattern = "TrialApp", EventIds = new List<int> { 7001 }, Category = "Service",
        Severity = severity, ElevatesDayRisk = elevates, Description = "試算用規則", CountThreshold = threshold,
        PlainExplanation = "x", Impact = "x", LikelyCauses = new List<string> { "x" }, NextSteps = new List<string> { "x" }
    };

    /// <summary>種一份固定資料：每筆是（主機, 幾天前, 當日次數）。次數取 1、2、3、5、9 讓門檻 2～10 各有邊界</summary>
    private void SeedData()
    {
        var a = TestData.AddHost(_hosts, "HOST-A");
        var b = TestData.AddHost(_hosts, "HOST-B");
        var c = TestData.AddHost(_hosts, "HOST-C");
        foreach (var (host, daysAgo, count) in new[]
        {
            (a, 1, 1), (a, 2, 2), (a, 3, 3), (a, 4, 5), (a, 5, 9),
            (b, 1, 3), (b, 2, 3), (b, 6, 5),
            (c, 1, 9), (c, 40, 9)   // 40 天前在試算窗口外
        })
        {
            _seeded.Add((host, daysAgo, count));
            _records.Append(new DailyAnalysisRecord
            {
                HostId = host.HostId, Host = host.HostName, Date = DateTime.Today.AddDays(-daysAgo), RiskLevel = "中",
                TopIssues = new List<LogIssueSignature>
                {
                    new() { LogName = "Application", Source = "TrialApp", EventId = 7001, EntryType = LogEntryType.Error, Count = count, Severity = IssueSeverity.Medium },
                    // 不相干的問題：不得被算進來
                    new() { LogName = "System", Source = "Other", EventId = 1, EntryType = LogEntryType.Error, Count = 99, Severity = IssueSeverity.High }
                }
            });
        }
    }

    private RuleAdminService Service() =>
        new(_rules, new FakeRuleSeedStore(), new FakeSuppressionStore(), new FakeUserStore(), FakeCurrentUser.WithCapabilities(Capability.Maintain),
            new RecordingAuditService(), new FakeHostGroupStore(), _hosts, new EfIssueAggregateQuery(_fx.NewContext, _hosts),
            systemSettings: _settings, visibility: new FixedVisibilityService(_hosts.GetAll().Select(h => h.HostId)));

    /// <summary>「實際重新分析」：把規則表換成新規則，逐列用正式的 Classify 判定，算同樣兩個數字</summary>
    private (int HitHostDays, int DispatchPairs) Reanalyze(KnownIssueRule rule)
    {
        KnownIssueCatalog.Initialize(new List<KnownIssueRule> { rule });
        var dispatchable = _settings.Get().ParseUnhandledSeverities();
        var hitDays = new HashSet<(long, DateTime)>();
        var pairs = new HashSet<long>();
        foreach (var (host, daysAgo, count) in _seeded.Where(s => s.DaysAgo <= 29))
        {
            var sig = new LogIssueSignature { LogName = "Application", Source = "TrialApp", EventId = 7001, EntryType = LogEntryType.Error, Count = count };
            KnownIssueCatalog.Classify(sig);
            if (sig.RuleId != rule.Id) continue;
            if (!sig.BelowThreshold) hitDays.Add((host.HostId, DateTime.Today.AddDays(-daysAgo)));
            if (dispatchable.Contains(sig.Severity)) pairs.Add(host.HostId);
        }
        return (hitDays.Count, pairs.Count);
    }

    [Theory]
    [InlineData(4)]    // 調高：命中日變少
    [InlineData(5)]    // 邊界：恰等於一筆的次數仍算達門檻
    [InlineData(6)]
    [InlineData(10)]   // 高到沒有任何一天達標
    public void 門檻調高_試算結果等於以新規則實際重新分析同一份資料(int threshold)
    {
        SeedData();
        var trial = Service().TrialRule(Draft(threshold));
        var actual = Reanalyze(Rule(threshold, IssueSeverity.Medium, elevates: false));

        Assert.Equal(actual.HitHostDays, trial.HitHostDaysAfter);
        Assert.Equal(actual.DispatchPairs, trial.DispatchPairsAfter);
        Assert.False(trial.IsLowerBound, "調高不是下限估計");
        // 「現在」＝目前儲存的規則（門檻 3）也要等於實際分析
        var current = Reanalyze(Rule(3, IssueSeverity.Medium, elevates: false));
        Assert.Equal(current.HitHostDays, trial.HitHostDaysBefore);
        Assert.Equal(current.DispatchPairs, trial.DispatchPairsBefore);
    }

    [Fact]
    public void 已知數字_門檻3到5_命中主機日從6變3_窗口外與不相干問題不算()
    {
        SeedData();
        var trial = Service().TrialRule(Draft(5));
        // 窗口內達 3 的（A:3、5、9＝3 天；B:3、3、5＝3 天；C:9＝1 天）＝7；達 5 的（A:5、9；B:5；C:9）＝4
        Assert.Equal(7, trial.HitHostDaysBefore);
        Assert.Equal(4, trial.HitHostDaysAfter);
        Assert.Equal(30, trial.WindowDays);
    }

    [Fact]
    public void 沒有可見主機_回無法試算並寫明原因_有可見主機時照常試算()
    {
        SeedData();
        var noHosts = new RuleAdminService(_rules, new FakeRuleSeedStore(), new FakeSuppressionStore(), new FakeUserStore(),
            FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(), new FakeHostGroupStore(), _hosts,
            new EfIssueAggregateQuery(_fx.NewContext, _hosts), systemSettings: _settings, visibility: new FixedVisibilityService(Array.Empty<long>()));

        var empty = noHosts.TrialRule(Draft(5));
        Assert.False(empty.Supported);
        Assert.Equal("目前帳號沒有可見主機，無法試算。", empty.UnsupportedReason);

        Assert.True(Service().TrialRule(Draft(5)).Supported);   // 邊界另一側：有可見主機照常算
    }

    [Fact]
    public void 門檻調低_標示為下限估計_調高或不變不是()
    {
        SeedData();
        Assert.True(Service().TrialRule(Draft(2)).IsLowerBound);
        Assert.False(Service().TrialRule(Draft(3)).IsLowerBound);   // 不變：邊界另一側
        Assert.False(Service().TrialRule(Draft(4)).IsLowerBound);
    }

    [Fact]
    public void 嚴重度與重大旗標_影響會被派工的數量與高風險日()
    {
        SeedData();
        // 預設未處理嚴重度＝High＋Medium：把 Medium 降到 Low，未達門檻再降也是 Low → 沒有任何一組會被派工
        var lowered = Service().TrialRule(Draft(3, "Low"));
        Assert.Equal(3, lowered.DispatchPairsBefore);   // A、B、C 三台都有達 Medium 的列
        Assert.Equal(0, lowered.DispatchPairsAfter);
        Assert.False(lowered.IsLowerBound);

        // 調高到 High 且打開重大旗標：高風險日數出現，並標示為下限
        var raised = Service().TrialRule(Draft(3, "High", elevates: true));
        Assert.Equal(0, raised.ElevatedHostDaysBefore);
        Assert.Equal(7, raised.ElevatedHostDaysAfter);
        Assert.True(raised.IsLowerBound);

        // 把派工嚴重度集合改成只剩 High：Medium 的規則「現在」就派不出去
        _settings.Update(s => s.UnhandledSeverities = new List<string> { "High" });
        var highOnly = Service().TrialRule(Draft(3, "High"));
        Assert.Equal(0, highOnly.DispatchPairsBefore);
        Assert.True(highOnly.DispatchPairsAfter > 0);
    }

    [Fact]
    public void 停用規則_試算後命中為0_啟用既有停用規則標示下限()
    {
        SeedData();
        var off = Service().TrialRule(Draft(3, enabled: false));
        Assert.Equal(7, off.HitHostDaysBefore);
        Assert.Equal(0, off.HitHostDaysAfter);
        Assert.Equal(0, off.DispatchPairsAfter);

        _rules.Content.Rules[0] = Rule(3, IssueSeverity.Medium, false, enabled: false);
        var on = Service().TrialRule(Draft(3));
        Assert.Equal(0, on.HitHostDaysBefore);
        Assert.Equal(7, on.HitHostDaysAfter);
        Assert.True(on.IsLowerBound);
    }

    [Fact]
    public void 新規則_現在為0且標示下限_被先排的同條件規則遮蔽時命中為0_PRTG規則不支援()
    {
        SeedData();
        var draft = Draft(3);
        draft.Id = "custom-new";
        // 現行規則表已有同來源同事件的 custom-trial 排在前面：新規則接在最後，永遠輪不到（與正式分析的先到先得一致）
        var shadowed = Service().TrialRule(draft);
        Assert.True(shadowed.IsNewRule);
        Assert.Equal(0, shadowed.HitHostDaysAfter);

        // 先把前面那條停用，新規則才接得到命中
        _rules.Content.Rules[0] = Rule(3, IssueSeverity.Medium, false, enabled: false);
        var trial = Service().TrialRule(draft);
        Assert.Equal(0, trial.HitHostDaysBefore);
        Assert.Equal(7, trial.HitHostDaysAfter);
        Assert.True(trial.IsLowerBound);

        var prtg = Draft(3);
        prtg.Id = "custom-prtg-new";   // 平台一經建立不可變更：要用新規則才建得出 prtg 草稿
        prtg.Platform = "prtg";
        prtg.PrtgRuleCode = "down";
        prtg.PrtgThreshold = 5;
        var unsupported = Service().TrialRule(prtg);
        Assert.False(unsupported.Supported);
        Assert.False(string.IsNullOrWhiteSpace(unsupported.UnsupportedReason));
    }

    [Fact]
    public void 試算前後資料庫無變更_規則檔也不動()
    {
        SeedData();
        var before = Snapshot();
        var ruleBefore = _rules.Content.Rules.Single();
        Service().TrialRule(Draft(10));
        Service().TrialRule(Draft(1, "High", elevates: true));
        Assert.Equal(before, Snapshot());
        Assert.Same(ruleBefore, _rules.Content.Rules.Single());
        Assert.Equal(3, _rules.Content.Rules.Single().CountThreshold);
    }

    private string Snapshot()
    {
        using var ctx = _fx.NewContext();
        var records = ctx.DailyRecords.AsNoTracking().Count();
        var issues = ctx.TopIssues.AsNoTracking().OrderBy(x => x.IssueId).Select(x => $"{x.IssueId}:{x.HostId}:{x.RecordDate:yyyyMMdd}:{x.EventCount}:{x.SeverityRank}").ToList();
        return $"{records}|{string.Join(",", issues)}";
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void RuleTrialRows的查詢兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var f = DateTime.Today.AddDays(-29);
        // 與 RuleTrialRows 同一個出口（RuleTrialQuery），不另寫一份條件；含「靜音排除」與「可見主機」兩個條件
        var sql = EfIssueAggregateQuery.RuleTrialQuery(ctx, IssueExclusion.None, sourceKeyReady: true, new[] { 7001 }, f, DateTime.Today, new long[] { 1, 2 }).ToQueryString();
        Assert.Contains("7001", sql);
        Assert.Contains("event_count", sql, StringComparison.OrdinalIgnoreCase);
    }
}
