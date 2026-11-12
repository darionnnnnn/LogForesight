using System.Text.Json;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 70 輪 F-a：報表「資料完整度」（缺漏主機日、DataThrough、Lagging、NoData）與主機排行的顯示名。
/// 計算一律以真實 SQLite 後端驗證；服務一律在種完資料後才建（別名索引與報表快取都不跨案例）。
/// </summary>
public class ReportCoverageTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();

    private static readonly DateTime Today = DateTime.Today;
    // 預設期間＝昨天往前 5 天（今天永遠還沒有資料，不放進預設期間）：d1 最早、d5 ＝ 昨天
    private static DateTime D(int n) => Today.AddDays(n - 6);   // D(1)=今天-5 … D(5)=今天-1
    private static readonly DateTime From = D(1);
    private static readonly DateTime To = D(5);

    public ReportCoverageTests() => _store = new EfAnalysisRecordStore(_fx.NewContext, "test");

    public void Dispose()
    {
        _fx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }

    private WebHost AddHost(string name, bool active = true, string? displayName = null) =>
        _hosts.Upsert(new WebHost { HostName = name, Active = active, DisplayName = displayName });

    private void Add(WebHost host, DateTime date, AnalysisLogSource source = AnalysisLogSource.Local,
        string risk = "低", bool incomplete = false) =>
        _store.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = risk,
            LogSource = source, DataIncomplete = incomplete
        });

    private void AddDays(WebHost host, params int[] days)
    {
        foreach (var d in days) Add(host, D(d));
    }

    private ReportService Service(ScheduleOptionsStore? schedule = null)
    {
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_store, _hosts, visibility, _severityVisibility);
        var handlingStore = new EfRecordHandlingStore(_fx.NewContext, new EfJsonLogStore(_fx.NewContext, "handling"));
        var issueHandling = new EfIssueHandlingStore(_fx.NewContext);
        var caseStore = new FakeIssueCaseStore();
        var progress = new HandlingProgressCalculator(issueHandling, handlingStore, caseStore, _settingsStore, new FixedIssueExclusionSource(IssueExclusion.None));
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var handling = new HandlingHistoryQueryService(
            handlingStore, issueHandling, caseStore, _hosts, new FakeUserStore(), visibility, _settingsStore, repository, progress, aggregates,
            new UserDisplayNameService(_settingsStore), new FixedIssueExclusionSource(IssueExclusion.None));
        var ranking = new IssueRankingBuilder(aggregates, _hosts, new FixedIssueExclusionSource(IssueExclusion.None));
        return new ReportService(repository, _hosts, visibility, handling, ranking, _settingsStore, aggregates, _severityVisibility,
            new SummaryCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None),
            new LogForesight.Core.Persistence.Sql.SqlPerformanceMonitor(), schedule);
    }

    private ReportCoverageDto Coverage(DateTime? from = null, DateTime? to = null, string? scope = null) =>
        Service().GetSummary(from ?? From, to ?? To, scope).Coverage;

    // ── 夜間：排程窗口還沒跑完，昨天的資料本來就不該有 ──────────────────

    /// <summary>窗口涵蓋「現在」（前 30 分到後 3 小時）：今天的實例還沒結束，最近結束的是昨天開跑那一個 → 應有資料到前天</summary>
    private ScheduleOptionsStore WindowCoveringNow()
    {
        var now = DateTime.Now;
        var store = new ScheduleOptionsStore(_fx.Blob("schedule_options"));
        store.Update(o =>
        {
            o.Enabled = true;
            o.Windows = new List<ScheduleWindow> { new() { Start = now.AddMinutes(-30).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") } };
        });
        return store;
    }

    [Fact]
    public void 窗口還沒跑完_昨天跑到一半的主機不算缺漏也不算落後()
    {
        if (DateTime.Now.AddMinutes(-30).Date != DateTime.Today) return;   // 剛過午夜：窗口起點落在昨天，此案的「今天的實例」不成立
        var a = AddHost("A"); var b = AddHost("B"); var c = AddHost("C");
        AddDays(a, 1, 2, 3, 4, 5); AddDays(b, 1, 2, 3, 4); AddDays(c, 1, 2, 3, 4);   // 昨天（D5）只有 A 跑完

        var night = Service(WindowCoveringNow()).GetSummary(From, To).Coverage;
        Assert.Equal(0, night.MissingHostDays);
        Assert.False(night.Lagging);

        // 沒有排程資訊（退回「昨天應有資料」）：B、C 昨天各算一個缺漏
        Assert.Equal(2, Coverage().MissingHostDays);
    }

    [Fact]
    public void 窗口還沒跑完_期間只有昨天一天_沒有應有資料的日子_回無資料()
    {
        if (DateTime.Now.AddMinutes(-30).Date != DateTime.Today) return;
        var a = AddHost("A");
        AddDays(a, 5);
        Assert.True(Service(WindowCoveringNow()).GetSummary(D(5), D(5)).Coverage.NoData);
    }

    [Fact]
    public void 同主機同日兩列_只算一天_缺漏不會被算少()
    {
        var a = AddHost("A");
        AddDays(a, 1, 2, 3, 4, 5);
        var b = AddHost("B");
        AddDays(b, 1, 5);
        // B 在 D1 另有一筆非 PRTG 紀錄（同日第二列）：天數仍是 2，缺 D2～D4 三天
        Add(b, D(1), AnalysisLogSource.Netiq);
        Assert.Equal(3, Coverage().MissingHostDays);
    }

    [Fact]
    public void 風險等級全部隱藏時_完整度仍照算_不變成無資料()
    {
        var a = AddHost("A");
        AddDays(a, 1, 2, 4, 5);
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string>();   // 全部隱藏
        var c = Coverage();
        Assert.False(c.NoData);
        Assert.Equal(1, c.MissingHostDays);
    }

    [Fact]
    public void 缺口紀錄不受風險等級顯示設定影響()
    {
        var a = AddHost("A");
        AddDays(a, 1, 2, 3, 4);
        Add(a, D(5), risk: "低", incomplete: true);
        var before = Coverage().MissingHostDays;
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { "高", "中" };   // 隱藏低風險日
        Assert.Equal(before, Coverage().MissingHostDays);
        Assert.Equal(1, before);
    }

    // ── 驗收 1～5：基本形狀 ────────────────────────────────────────

    [Fact]
    public void 三台主機五天每天都有紀錄_缺漏零且不落後()
    {
        foreach (var name in new[] { "A", "B", "C" }) AddDays(AddHost(name), 1, 2, 3, 4, 5);

        var c = Coverage();

        Assert.Equal(0, c.MissingHostDays);
        Assert.False(c.Lagging);
        Assert.False(c.NoData);
        Assert.Equal(D(5).ToString("yyyy-MM-dd"), c.DataThrough);
    }

    [Fact]
    public void 一台缺中間兩天_缺漏二()
    {
        AddDays(AddHost("A"), 1, 2, 3, 4, 5);
        AddDays(AddHost("B"), 1, 2, 3, 4, 5);
        AddDays(AddHost("C"), 1, 4, 5);

        Assert.Equal(2, Coverage().MissingHostDays);
    }

    [Fact]
    public void 某天三台全部沒紀錄_缺漏三()
    {
        foreach (var name in new[] { "A", "B", "C" }) AddDays(AddHost(name), 1, 2, 4, 5);

        Assert.Equal(3, Coverage().MissingHostDays);
    }

    [Fact]
    public void 期間最後兩天全體沒紀錄_不算缺漏且標示落後()
    {
        foreach (var name in new[] { "A", "B", "C" }) AddDays(AddHost(name), 1, 2, 3);

        var c = Coverage();

        Assert.Equal(0, c.MissingHostDays);                              // 最後兩天不是「缺漏」，是「還沒到」
        Assert.Equal(D(3).ToString("yyyy-MM-dd"), c.DataThrough);       // 倒數第 3 天
        Assert.True(c.Lagging);
    }

    [Fact]
    public void 期間終點是今天且資料到昨天_不落後()
    {
        foreach (var name in new[] { "A", "B" })
        {
            var host = AddHost(name);
            for (var i = 4; i >= 1; i--) Add(host, Today.AddDays(-i));   // 今天-4 … 昨天；今天沒有
        }

        var c = Coverage(Today.AddDays(-4), Today);

        Assert.False(c.Lagging);                                          // 以 to 比較會誤判成落後
        Assert.Equal(0, c.MissingHostDays);
        Assert.Equal(Today.AddDays(-1).ToString("yyyy-MM-dd"), c.DataThrough);
    }

    [Fact]
    public void 期間終點是今天且資料只到前天_落後()
    {
        var host = AddHost("A");
        for (var i = 4; i >= 2; i--) Add(host, Today.AddDays(-i));

        Assert.True(Coverage(Today.AddDays(-4), Today).Lagging);
    }

    // ── 驗收 6：起算日取有史以來第一筆 ─────────────────────────────

    [Fact]
    public void 期間第三天才第一次有紀錄的主機_前兩天不算缺漏_期間前就有紀錄的主機第一天缺才算()
    {
        AddDays(AddHost("NEW"), 3, 4, 5);                         // 從沒有紀錄 → 起算日是第 3 天
        var old = AddHost("OLD");
        Add(old, From.AddDays(-10));                               // 期間之前就在被分析
        AddDays(old, 2, 3, 4, 5);                                  // 期間第 1 天缺

        Assert.Equal(1, Coverage().MissingHostDays);
    }

    [Fact]
    public void 從沒有任何紀錄的主機不算缺漏()
    {
        AddDays(AddHost("A"), 1, 2, 3, 4, 5);
        AddHost("NEVER");

        Assert.Equal(0, Coverage().MissingHostDays);
    }

    // ── 驗收 7：停用、合併、別名 ───────────────────────────────────

    [Fact]
    public void 停用主機與已合併主機不算缺漏()
    {
        AddDays(AddHost("A"), 1, 2, 3, 4, 5);
        var inactive = AddHost("OFF", active: false);
        AddDays(inactive, 1);                                      // 有史以來有紀錄但之後都缺——若被算進來會是 4
        var mergedAlone = AddHost("MERGED");
        AddDays(mergedAlone, 1);
        var survivor = AddHost("S");
        AddDays(survivor, 1, 2, 3, 4, 5);
        _hosts.Merge(mergedAlone.HostId, survivor.HostId);

        Assert.Equal(0, Coverage().MissingHostDays);
    }

    [Fact]
    public void 存活主機缺的那天由別名補上就不算缺漏()
    {
        var survivor = AddHost("S");
        AddDays(survivor, 1, 2, 4, 5);                             // 缺第 3 天
        var alias = AddHost("OLD-NAME");
        AddDays(alias, 3);                                         // 別名在第 3 天有紀錄
        _hosts.Merge(alias.HostId, survivor.HostId);

        Assert.Equal(0, Coverage().MissingHostDays);
    }

    [Fact]
    public void 別名沒補到的那天仍算存活主機的缺漏()
    {
        var survivor = AddHost("S");
        AddDays(survivor, 1, 2, 4, 5);
        var alias = AddHost("OLD-NAME");
        Add(alias, From.AddDays(-3));                              // 別名的紀錄在期間之前，補不到期間內的缺口
        _hosts.Merge(alias.HostId, survivor.HostId);

        Assert.Equal(1, Coverage().MissingHostDays);
    }

    // ── 驗收 8、9 ──────────────────────────────────────────────────

    [Fact]
    public void 某天只有PRTG紀錄_算缺漏一()
    {
        var host = AddHost("A");
        AddDays(host, 1, 2, 4, 5);
        Add(host, D(3), AnalysisLogSource.Prtg);

        Assert.Equal(1, Coverage().MissingHostDays);
    }

    [Fact]
    public void 只有PRTG紀錄的主機_沒有日誌分析所以是NoData()
    {
        var host = AddHost("A");
        foreach (var d in new[] { 1, 2, 3, 4, 5 }) Add(host, D(d), AnalysisLogSource.Prtg);

        var c = Coverage();

        Assert.True(c.NoData);
        Assert.Null(c.DataThrough);
    }

    [Fact]
    public void 既有缺口紀錄兩筆加缺漏一_總數三()
    {
        var host = AddHost("A");
        Add(host, D(1));
        Add(host, D(2), incomplete: true);
        Add(host, D(3), incomplete: true);
        // 第 4 天沒有紀錄 → 缺漏 1
        Add(host, D(5));

        var summary = Service().GetSummary(From, To);

        Assert.Equal(2, summary.Kpi.CoverageGapDays);                    // 既有語意不變
        Assert.Equal(3, summary.Coverage.MissingHostDays);
    }

    // ── 驗收 10、11 ────────────────────────────────────────────────

    [Fact]
    public void 沒有可見主機_NoData()
    {
        var c = Coverage();

        Assert.True(c.NoData);
        Assert.Equal(0, c.MissingHostDays);
        Assert.False(c.Lagging);
        Assert.Null(c.DataThrough);
    }

    [Fact]
    public void 有主機但期間內無紀錄_NoData()
    {
        var host = AddHost("A");
        Add(host, From.AddDays(-20));                              // 有史以來有、期間內沒有

        var c = Coverage();

        Assert.True(c.NoData);
        Assert.Null(c.DataThrough);
        Assert.Equal(0, c.MissingHostDays);
    }

    [Fact]
    public void 只有停用主機_NoData()
    {
        AddDays(AddHost("OFF", active: false), 1, 2, 3, 4, 5);

        Assert.True(Coverage().NoData);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("open")]
    [InlineData("unassigned")]
    public void 顯示範圍非全部時Coverage與全部相同(string scope)
    {
        var a = AddHost("A");
        Add(a, D(1), risk: "高");
        Add(a, D(2), incomplete: true);                            // 低風險日的缺口：非全部範圍的 KPI 母體不含它
        Add(a, D(3), risk: "高", incomplete: true);
        // 第 4 天缺漏
        Add(a, D(5));
        var b = AddHost("B");
        AddDays(b, 1, 2, 3, 4, 5);

        var all = Coverage(scope: "all");
        var scoped = Coverage(scope: scope);

        Assert.Equal(1 + 2, all.MissingHostDays);                  // 缺漏 1 ＋ 缺口 2
        Assert.Equal(all.MissingHostDays, scoped.MissingHostDays);
        Assert.Equal(all.DataThrough, scoped.DataThrough);
        Assert.Equal(all.Lagging, scoped.Lagging);
        Assert.Equal(all.NoData, scoped.NoData);
    }

    [Fact]
    public void 介面預設實作回NoData狀態()
    {
        IIssueAggregateQuery fake = new FakeIssueAggregateQuery();

        var result = fake.AggregateReportCoverage(IssueExclusion.None, From, To, new long[] { 1 });

        Assert.True(result.NoData);
        Assert.Equal(0, result.MissingHostDays);
        Assert.Null(result.DataThrough);
    }

    // ── 驗收 12：查詢次數不隨主機數成長 ────────────────────────────

    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Commands { get; set; }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Commands++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    [Fact]
    public void 查詢次數不隨主機數成長_30台與300台相同()
    {
        var counter = new CommandCounter();
        using var probe = _fx.NewContext();
        var connection = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(probe.Database);
        var query = new EfIssueAggregateQuery(() => new LfDbContext(
            new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(counter).Options), _hosts);

        var ids = new List<long>();
        int CountFor(int hostCount)
        {
            while (ids.Count < hostCount)
            {
                var host = AddHost("H" + ids.Count);
                ids.Add(host.HostId);
                AddDays(host, 1, 2, 4, 5);                         // 每台缺第 3 天
            }
            counter.Commands = 0;
            var result = query.AggregateReportCoverage(IssueExclusion.None, From, To, ids.Take(hostCount).ToList());
            Assert.Equal(hostCount, result.MissingHostDays);       // 計算本身也要對（每台缺 1 天）
            return counter.Commands;
        }

        var at30 = CountFor(30);
        var at300 = CountFor(300);

        Assert.True(at30 > 0);
        Assert.Equal(at30, at300);
    }

    // ── 驗收 13：主機顯示名 ────────────────────────────────────────

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement RankingRow(string json, string hostName)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("hostRanking").EnumerateArray())
            if (row.GetProperty("hostName").GetString() == hostName) return row.Clone();
        throw new Xunit.Sdk.XunitException($"排行裡找不到 {hostName}");
    }

    [Theory]
    [InlineData("all")]
    [InlineData("unresolved")]
    public void 報表回應JSON的主機排行帶displayName(string scope)
    {
        var named = AddHost("10.0.0.5", displayName: "財務資料庫");
        var plain = AddHost("PLAIN");
        var blank = AddHost("BLANK", displayName: "   ");
        foreach (var host in new[] { named, plain, blank }) Add(host, D(5), risk: "高");

        var json = JsonSerializer.Serialize(ApiResponse<ReportSummaryDto>.Ok(Service().GetSummary(From, To, scope)), Web);

        Assert.Equal("財務資料庫", RankingRow(json, "10.0.0.5").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, RankingRow(json, "PLAIN").GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, RankingRow(json, "BLANK").GetProperty("displayName").ValueKind);
    }

    [Fact]
    public void 報表回應JSON帶coverage欄位()
    {
        AddDays(AddHost("A"), 1, 2, 4, 5);

        var json = JsonSerializer.Serialize(ApiResponse<ReportSummaryDto>.Ok(Service().GetSummary(From, To)), Web);

        using var doc = JsonDocument.Parse(json);
        var coverage = doc.RootElement.GetProperty("data").GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("missingHostDays").GetInt32());
        Assert.Equal(D(5).ToString("yyyy-MM-dd"), coverage.GetProperty("dataThrough").GetString());
        Assert.False(coverage.GetProperty("lagging").GetBoolean());
        Assert.False(coverage.GetProperty("noData").GetBoolean());
    }

    [Fact]
    public void 儀表板回應JSON同樣帶displayName_兩個組裝路徑都填()
    {
        var named = new WebHost { HostId = 7, HostName = "10.0.0.7", DisplayName = "備援主機" };
        var plain = new WebHost { HostId = 8, HostName = "PLAIN" };
        var aggregates = new List<HostRiskAggregate>
        {
            new() { HostId = 7, HighRiskDays = 1 }, new() { HostId = 8, HighRiskDays = 1 }
        };
        var byId = RecordStatsBuilder.BuildHostRanking(aggregates, new Dictionary<long, WebHost> { [7] = named, [8] = plain });

        var records = new List<DailyAnalysisRecord>
        {
            new() { HostId = 7, Host = "10.0.0.7", Date = Today, RiskLevel = "高" },
            new() { HostId = 8, Host = "PLAIN", Date = Today, RiskLevel = "高" },
            new() { HostId = 9, Host = "UNKNOWN", Date = Today, RiskLevel = "高" }   // 主機索引對不到
        };
        var byName = RecordStatsBuilder.BuildHostRanking(records, new Dictionary<string, WebHost>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.7"] = named, ["PLAIN"] = plain
        });

        foreach (var ranking in new[] { byId, byName })
        {
            var json = JsonSerializer.Serialize(ApiResponse<DashboardDto>.Ok(new DashboardDto { HostRanking = ranking }), Web);
            Assert.Equal("備援主機", RankingRow(json, "10.0.0.7").GetProperty("displayName").GetString());
            Assert.Equal(JsonValueKind.Null, RankingRow(json, "PLAIN").GetProperty("displayName").ValueKind);
        }
        Assert.Null(byName.Single(h => h.HostName == "UNKNOWN").DisplayName);
    }

    // ── SQL Server 翻譯守門 ────────────────────────────────────────

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 資料完整度兩條查詢_兩個後端都翻譯得出來且在SQL端分組(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var ids = new List<long> { 1, 2, 3 };

        var first = EfIssueAggregateQuery.BuildCoverageFirstQuery(ctx.DailyRecords, ids, 3).ToQueryString();
        var range = EfIssueAggregateQuery.BuildCoverageRangeQuery(ctx.DailyRecords, ids, 3, new DateTime(2026, 8, 1), new DateTime(2026, 8, 7)).ToQueryString();

        Assert.Contains("GROUP BY", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN(", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", range, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(", range, StringComparison.OrdinalIgnoreCase);
        // 天數要在 SQL 端去重（同主機同日多列只算一天）；缺口筆數是條件計數——只斷言 COUNT( 的話拿掉 Distinct 也會過
        Assert.Contains("COUNT(DISTINCT", range, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", range, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HAVING", range, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client", first + range, StringComparison.OrdinalIgnoreCase);
    }
}
