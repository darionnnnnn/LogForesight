using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 5a：問題聚合查詢不把逐日原始列拉回記憶體。
///
/// 原則是「不改運作邏輯」：每個入口的每個欄位與改動前相同，只減少讀回列數與查詢數。
/// 因此對帳一律對「記憶體內重做一次舊演算法」的預言機（<see cref="Oracle"/>）逐欄比對，
/// 成本面用 <see cref="SqlCommandRecorder"/> 的讀取呼叫次數與指令數（與機器快慢無關）。
/// 不使用任何全域累計計數，同一個測試類別可與其他類別並行。
/// </summary>
public sealed class IssueAggregateBatch5aTests : IDisposable
{
    private static readonly DateTime End = new(2026, 8, 28);

    private readonly ITestOutputHelper _output;
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly SqlCommandRecorder _recorder = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public IssueAggregateBatch5aTests(ITestOutputHelper output)
    {
        _output = output;
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    // ── 建構與資料 ────────────────────────────────────────────

    private LfDbContext Recording() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_fx.NewContext().Database.GetDbConnection()).AddInterceptors(_recorder).Options);

    private EfIssueAggregateQuery Query(bool ready = true, bool recording = true) =>
        new(recording ? Recording : _fx.NewContext, _hosts, null, () => ready);

    private static LogIssueSignature Issue(
        string source, int eventId, IssueSeverity severity = IssueSeverity.Medium, int count = 1,
        string? knownIssue = null, string eventKey = "", string logName = "System",
        IssueCategory category = IssueCategory.Other) => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = LogEntryType.Warning,
            Category = category, Severity = severity, Count = count, KnownIssue = knownIssue, EventKey = eventKey
        };

    private void Add(long hostId, DateTime date, params LogIssueSignature[] issues) =>
        Add(hostId, date, RiskLevels.High, issues);

    private void Add(long hostId, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = $"H{hostId}", Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList()
        });

    private List<long> SeedHosts(int hostCount, int days)
    {
        var ids = new List<long>();
        for (var h = 0; h < hostCount; h++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"SRV-{h:D3}", Active = true });
            ids.Add(host.HostId);
            for (var d = 0; d < days; d++)
                Add(host.HostId, End.AddDays(-d),
                    Issue("disk", 153, IssueSeverity.Medium, 2), Issue("cpu", 100, IssueSeverity.Low),
                    Issue("net", 404, IssueSeverity.High));
        }
        return ids;
    }

    private static readonly (string Source, int EventId)[] ThreeIssues = { ("disk", 153), ("cpu", 100), ("net", 404) };

    // ── 預言機：記憶體內重做舊演算法（讀整張表、逐列判定）──────────

    private IEnumerable<TopIssueRow> OracleRows(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null, IReadOnlySet<string>? riskLevels = null,
        Func<TopIssueRow, bool>? extra = null)
    {
        using var ctx = _fx.NewContext();
        var index = new HostAliasIndex(_hosts.GetAll());
        var ranks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);
        HashSet<long>? expanded = null;
        if (hostIds != null)
        {
            expanded = new HashSet<long>();
            foreach (var id in hostIds)
            {
                var aliases = index.Aliases(id);
                if (aliases.Count == 0) expanded.Add(id);
                foreach (var a in aliases) expanded.Add(a.HostId);
            }
        }
        var records = ctx.DailyRecords.AsNoTracking().Select(r => new { r.RecordId, r.RiskLevel }).ToList()
            .ToDictionary(r => r.RecordId, r => r.RiskLevel);
        return ctx.TopIssues.AsNoTracking().ToList().Where(x =>
            x.RecordDate >= from.Date && x.RecordDate <= to.Date
            && (ranks == null || ranks.Contains(x.SeverityRank))
            && (expanded == null || expanded.Contains(x.HostId))
            && (riskLevels == null || (records.TryGetValue(x.RecordId, out var lv) && riskLevels.Contains(lv)))
            && (extra == null || extra(x))).ToList();
    }

    private (string Key, DateTime LastSeen, int Rank, string? Known, long Host)[] Oracle(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? visibleSeverities = null,
        IReadOnlySet<string>? riskLevels = null, Func<TopIssueRow, bool>? extra = null)
    {
        var index = new HostAliasIndex(_hosts.GetAll());
        var wanted = issues.Select(i => (WorkOrderIssueKey.SourceKeyOf(i.Source), i.EventId)).ToHashSet();
        long Surv(long id) => index.Surviving(id)?.HostId ?? id;
        return OracleRows(from, to, hostIds, visibleSeverities, riskLevels, extra)
            .Where(x => wanted.Contains((x.SourceKey ?? WorkOrderIssueKey.SourceKeyOf(x.SourceName), x.EventId)))
            .GroupBy(x => (Host: Surv(x.HostId), x.LogName, SourceKey: x.SourceKey ?? WorkOrderIssueKey.SourceKeyOf(x.SourceName),
                x.EventId, x.EntryType, x.EventKey))
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.RecordDate).ThenByDescending(x => x.SeverityRank).First();
                return (
                    IssueSignatureKey.For(g.Key.LogName, g.Select(x => x.SourceName).Min() ?? g.Key.SourceKey, g.Key.EventId,
                        (LogEntryType)g.Key.EntryType, g.Key.EventKey),
                    latest.RecordDate, latest.SeverityRank, latest.KnownIssue, g.Key.Host);
            })
            .OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item5)
            .ToArray();
    }

    private static (string Key, DateTime LastSeen, int Rank, string? Known, long Host)[] Shape(IEnumerable<HostIssueOccurrence> rows) =>
        rows.Select(o => (o.IssueKey, o.LastSeen, o.SeverityRank, o.KnownIssue, o.HostId))
            .OrderBy(x => x.IssueKey, StringComparer.Ordinal).ThenBy(x => x.HostId)
            .ToArray();

    /// <summary>主機 1、2 沒有合併；3 併入 4。資料在合併前後兩個識別都有。</summary>
    private (long A, long B, long Tomb, long Surv) SeedMergeScenario(int days = 6)
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var surv = _hosts.Upsert(new WebHost { HostName = "SURV" });
        var tomb = _hosts.Upsert(new WebHost { HostName = "TOMB", MergedInto = surv.HostId, Active = false });
        for (var d = 0; d < days; d++)
        {
            Add(a.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.Low, 1, "k-a"), Issue("cpu", 100));
            Add(b.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.Medium, 2, "k-b"));
            if (d % 2 == 0) Add(tomb.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.High, 3, "k-tomb"));
            if (d % 2 == 1) Add(surv.HostId, End.AddDays(-d), Issue("disk", 153, IssueSeverity.Low, 1, "k-surv"));
        }
        return (a.HostId, b.HostId, tomb.HostId, surv.HostId);
    }

    private static readonly (string, int)[] DiskCpu = { ("disk", 153), ("cpu", 100) };

    // ── A1：新舊結果逐欄相同 ───────────────────────────────────

    [Fact]
    public void A1_無合併主機_新路徑與舊演算法逐欄相同()
    {
        SeedHosts(5, 6);
        var from = End.AddDays(-5);

        var actual = Shape(Query().LatestOccurrences(IssueExclusion.None, ThreeIssues, from, End, null));

        Assert.Equal(Oracle(ThreeIssues, from, End, null), actual);
        Assert.Equal(15, actual.Length);
    }

    [Fact]
    public void A1_有合併主機_合併前後各有資料_仍與舊演算法相同()
    {
        SeedMergeScenario();
        var from = End.AddDays(-5);

        var actual = Shape(Query().LatestOccurrences(IssueExclusion.None, DiskCpu, from, End, null));

        Assert.Equal(Oracle(DiskCpu, from, End, null), actual);
        // 合併前後兩個識別併成存活主機一筆（取真正最近的一次，不是各自留一筆）
        Assert.Single(actual.Where(x => x.Key.StartsWith("System|disk|153|") && x.Host == _hosts.GetAll().Single(h => h.HostName == "SURV").HostId));
    }

    [Fact]
    public void A1_來源鍵未就緒_走舊路徑且結果相同()
    {
        SeedHosts(4, 5);
        var from = End.AddDays(-4);

        var actual = Shape(Query(ready: false).LatestOccurrences(IssueExclusion.None, ThreeIssues, from, End, null));

        Assert.Equal(Oracle(ThreeIssues, from, End, null), actual);
    }

    [Fact]
    public void A1_可見範圍受限_結果相同()
    {
        var ids = SeedHosts(6, 5);
        var from = End.AddDays(-4);
        var visible = new[] { ids[1], ids[3] };

        var actual = Shape(Query().LatestOccurrences(IssueExclusion.None, ThreeIssues, from, End, visible));

        Assert.Equal(Oracle(ThreeIssues, from, End, visible), actual);
        Assert.Equal(new[] { ids[1], ids[3] }, actual.Select(x => x.Host).Distinct().OrderBy(x => x));
    }

    [Fact]
    public void A1_只限部分主機且那些主機沒有合併_走新路徑結果相同()
    {
        var m = SeedMergeScenario(days: 10);
        var from = End.AddDays(-9);
        var visible = new[] { m.A, m.B };

        var actual = Shape(Query().LatestOccurrences(IssueExclusion.None, DiskCpu, from, End, visible));

        Assert.Equal(Oracle(DiskCpu, from, End, visible), actual);
        Assert.DoesNotContain(actual, x => x.Host == m.Surv || x.Host == m.Tomb);
    }

    // ── A2：讀回列數不隨天數成長 ───────────────────────────────

    private long LatestReadCalls(int days)
    {
        using var env = new IssueAggregateBatch5aTests(_output);
        env.SeedHosts(8, days);
        var query = env.Query();
        var (_, reads) = env._recorder.Measure(() =>
            Assert.Equal(24, query.LatestOccurrences(IssueExclusion.None, ThreeIssues, End.AddDays(-(days - 1)), End, null).Count));
        return reads;
    }

    [Fact]
    public void A2_無合併主機_LatestOccurrences讀回列數不隨天數成長()
    {
        var week = LatestReadCalls(7);
        var month = LatestReadCalls(28);
        _output.WriteLine($"LatestOccurrences ReadCalls: 7 天={week}、28 天={month}");

        Assert.True((double)month / week < 1.3, $"7 天 {week}、28 天 {month}，讀回列數不應隨天數成長");
    }

    [Fact]
    public void A2_有合併主機在範圍內_仍走舊路徑_讀回列數隨天數成長()
    {
        // 反向對照：條件不成立時不動舊行為（也證明「走哪條路」的判斷確實生效）
        long Measure(int days)
        {
            using var env = new IssueAggregateBatch5aTests(_output);
            env.SeedMergeScenario(days);
            var query = env.Query();
            return env._recorder.Measure(() => query.LatestOccurrences(IssueExclusion.None, DiskCpu, End.AddDays(-(days - 1)), End, null)).ReadCalls;
        }
        Assert.True(Measure(28) > Measure(7) * 2);
    }

    // ── A3：同事件編號分屬兩來源 ───────────────────────────────

    [Fact]
    public void A3_同事件編號分屬兩來源_只回傳被要求的那個_來源名為前綴也一樣()
    {
        var h = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(h.HostId, End, Issue("disk", 153), Issue("diskfoo", 153), Issue("DISK", 7));

        var only = Query().LatestOccurrences(IssueExclusion.None, new[] { ("disk", 153) }, End, End, null);
        var prefix = Query().LatestOccurrences(IssueExclusion.None, new[] { ("diskfoo", 153) }, End, End, null);

        Assert.Equal("System|disk|153|2", Assert.Single(only).IssueKey);
        Assert.Equal("System|diskfoo|153|2", Assert.Single(prefix).IssueKey);
    }

    [Fact]
    public void A3_要求兩個來源各自不同事件編號_不交叉誤配()
    {
        var h = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(h.HostId, End, Issue("disk", 153), Issue("disk", 7), Issue("cpu", 153), Issue("cpu", 7));

        var result = Query().LatestOccurrences(IssueExclusion.None, new[] { ("disk", 153), ("cpu", 7) }, End, End, null);

        Assert.Equal(new[] { "System|cpu|7|2", "System|disk|153|2" }, result.Select(o => o.IssueKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── A4：出現點與同日取最高嚴重度 ───────────────────────────

    [Fact]
    public void A4_同主機同規則兩顆PRTG感測器仍是兩個出現點()
    {
        var h = _hosts.Upsert(new WebHost { HostName = "A" });
        var s1 = Issue("PRTG", 0, eventKey: "prtg:down:1001", logName: "PRTG");
        var s2 = Issue("PRTG", 0, eventKey: "prtg:down:1002", logName: "PRTG");
        Add(h.HostId, End.AddDays(-2), s1, s2);
        Add(h.HostId, End, s1, s2);

        var result = Query().LatestOccurrences(IssueExclusion.None, new[] { ("PRTG", 0) }, End.AddDays(-2), End, null);

        Assert.Equal(
            new[] { IssueSignatureKey.For(s1), IssueSignatureKey.For(s2) }.OrderBy(k => k, StringComparer.Ordinal),
            result.Select(o => o.IssueKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(result, o => Assert.Equal(End, o.LastSeen));
    }

    [Fact]
    public void A4_同出現點最近一天有兩列_取嚴重度較高者與其KnownIssue()
    {
        var h = _hosts.Upsert(new WebHost { HostName = "A" });
        // 同一個出現點（主機、LogName、來源、事件、EntryType、EventKey）同一天兩列：
        // 走 lf_top_issues 直接寫入，才能構造出同鍵同日的兩列
        Add(h.HostId, End.AddDays(-3), Issue("disk", 153, IssueSeverity.High, 1, "old-high"));
        Add(h.HostId, End, Issue("disk", 153, IssueSeverity.Low, 1, "today-low"));
        using (var ctx = _fx.NewContext())
        {
            var template = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate == End).Single();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = template.RecordId, SourceName = template.SourceName, SourceKey = template.SourceKey,
                EventId = template.EventId, Category = template.Category, SeverityRank = (int)IssueSeverity.High,
                HostId = template.HostId, RecordDate = End, EventCount = 1, LogName = template.LogName,
                EntryType = template.EntryType, KnownIssue = "today-high", EventKey = template.EventKey
            });
            ctx.SaveChanges();
        }

        var occurrence = Assert.Single(Query().LatestOccurrences(IssueExclusion.None, new[] { ("disk", 153) }, End.AddDays(-3), End, null));

        Assert.Equal(End, occurrence.LastSeen);
        Assert.Equal((int)IssueSeverity.High, occurrence.SeverityRank);
        Assert.Equal("today-high", occurrence.KnownIssue);
    }

    // ── A5：三個母體條件在新路徑都生效 ──────────────────────────

    [Fact]
    public void A5_靜音排除_可見嚴重度_日風險等級_在新路徑都生效()
    {
        var h1 = _hosts.Upsert(new WebHost { HostName = "A" });
        var h2 = _hosts.Upsert(new WebHost { HostName = "B" });
        Add(h1.HostId, End.AddDays(-2), RiskLevels.High, Issue("disk", 153, IssueSeverity.High), Issue("cron", 7, IssueSeverity.Medium));
        Add(h1.HostId, End, RiskLevels.Low, Issue("disk", 153, IssueSeverity.High), Issue("cron", 7, IssueSeverity.Medium));
        Add(h2.HostId, End, RiskLevels.High, Issue("disk", 153, IssueSeverity.Low), Issue("cron", 7, IssueSeverity.Low));
        var issues = new[] { ("disk", 153), ("cron", 7) };
        var from = End.AddDays(-2);

        // 靜音：cron/7 整段靜音
        var mute = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "cron", EventId = 7, Mutes = { new MuteInterval { From = from, To = End } } }
        }, End);
        var muted = Query().LatestOccurrences(mute, issues, from, End, null);
        Assert.DoesNotContain(muted, o => o.IssueKey.Contains("|cron|"));
        Assert.Equal(Shape(Query(ready: false).LatestOccurrences(mute, issues, from, End, null)), Shape(muted));

        // 可見嚴重度：只看 High → 只剩 h1 的 disk
        var high = new HashSet<IssueSeverity> { IssueSeverity.High };
        var sev = Query().LatestOccurrences(IssueExclusion.None, issues, from, End, null, high);
        Assert.Equal(Oracle(issues, from, End, null, high), Shape(sev));
        Assert.All(sev, o => Assert.Equal((int)IssueSeverity.High, o.SeverityRank));
        Assert.Single(sev);

        // 日風險等級：只看 High 的日 → h1 的最近一次是 8/26，不是 8/28
        var riskHigh = new HashSet<string> { RiskLevels.High };
        var risk = Query().LatestOccurrences(IssueExclusion.None, issues, from, End, null, null, riskHigh);
        Assert.Equal(Oracle(issues, from, End, null, null, riskHigh), Shape(risk));
        Assert.Equal(End.AddDays(-2), risk.Single(o => o.HostId == h1.HostId && o.IssueKey.Contains("|disk|")).LastSeen);
        // 空集合＝零結果（不是不限制）
        Assert.Empty(Query().LatestOccurrences(IssueExclusion.None, issues, from, End, null, null, new HashSet<string>()));
    }

    // ── A6：Aggregate 的主機數／主機日數 ────────────────────────

    private void AssertAggregateMatchesOracle(IReadOnlyCollection<long>? hostIds, DateTime from)
    {
        var index = new HostAliasIndex(_hosts.GetAll());
        long Surv(long id) => index.Surviving(id)?.HostId ?? id;
        var expected = OracleRows(from, End, hostIds)
            .GroupBy(x => (x.SourceKey!, x.EventId))
            .ToDictionary(g => g.Key, g => (
                Hosts: g.Select(x => Surv(x.HostId)).Distinct().Count(),
                Days: g.Select(x => (Surv(x.HostId), x.RecordDate)).Distinct().Count()));

        var actual = Query().Aggregate(IssueExclusion.None, from, End, hostIds);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var a in actual)
        {
            var e = expected[(WorkOrderIssueKey.SourceKeyOf(a.Source), a.EventId)];
            Assert.Equal(e.Hosts, a.HostCount);
            Assert.Equal(e.Days, a.DayCount);
        }
    }

    [Fact]
    public void A6_Aggregate的主機數與主機日數_含合併主機_與舊演算法逐欄相同()
    {
        var m = SeedMergeScenario(8);
        var from = End.AddDays(-7);

        AssertAggregateMatchesOracle(null, from);
        AssertAggregateMatchesOracle(new[] { m.A, m.Surv }, from);   // 存活主機展開別名
        var disk = Query().Aggregate(IssueExclusion.None, from, End, null).Single(a => a.Source == "disk");
        Assert.Equal(3, disk.HostCount);   // A、B、存活（合併前後同一台）
    }

    [Fact]
    public void A6_Aggregate_整批拉回由兩次降為一次()
    {
        SeedHosts(6, 5);
        var query = Query();
        query.Aggregate(IssueExclusion.None, End.AddDays(-4), End, null);   // 暖機（別名索引、EF 初始化）

        _recorder.Clear();
        query.Aggregate(IssueExclusion.None, End.AddDays(-4), End, null);

        // 改動前 5 道（主查詢、主機數、主機日數、簽章、類別）；主機數與主機日數併成同一道後為 4 道
        Assert.Equal(4, _recorder.Selects().Count);
    }

    // ── A7：5-3 前一期只算用到的欄位 ────────────────────────────

    [Fact]
    public void A7_排行與郵件摘要_前期欄位與舊做法相同_前一期查詢數下降()
    {
        var m = SeedMergeScenario(20);
        // 只在前期出現的老問題、只在本期出現的新問題
        var from = End.AddDays(-6);
        Add(m.A, End.AddDays(-10), Issue("oldonly", 9));
        Add(m.B, End.AddDays(-9), Issue("oldonly", 9));
        Add(m.A, End, Issue("brandnew", 11, IssueSeverity.High));
        var aggregates = Query();

        var ranking = new IssueRankingBuilder(aggregates, _hosts, new FixedIssueExclusionSource(IssueExclusion.None))
            .Build(from, End, null, totalHosts: 4);

        // 對帳：以舊做法（完整 Aggregate）算出的前期欄位
        var periodDays = 7;
        var prevTo = from.AddDays(-1);
        var previous = Query(recording: false).Aggregate(IssueExclusion.None, prevTo.AddDays(-periodDays + 1), prevTo, null)
            .ToDictionary(a => IssueProfile.KeyOf(a.Source, a.EventId));
        Assert.NotEmpty(ranking);
        foreach (var row in ranking)
        {
            previous.TryGetValue(IssueProfile.KeyOf(row.Source, row.EventId), out var prev);
            Assert.Equal(prev?.HostCount ?? 0, row.PreviousHostCount);
            Assert.Equal((int)Math.Min(prev?.TotalCount ?? 0, int.MaxValue), row.PreviousTotalCount);
            Assert.Equal(prev == null, row.IsNew);
        }
        Assert.True(ranking.Single(r => r.Source == "brandnew").IsNew);
        Assert.False(ranking.Single(r => r.Source == "disk").IsNew);

        // 郵件摘要每列的前期主機數與分區
        var digest = new MailIssueDigest(aggregates, new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings),
            _settings, new FixedIssueExclusionSource(IssueExclusion.None));
        var rows = digest.Build(from, End, null);
        foreach (var row in rows)
        {
            previous.TryGetValue(IssueProfile.KeyOf(row.Source, row.EventId), out var prev);
            Assert.Equal(prev?.HostCount ?? 0, row.PreviousHostCount);
            if (prev == null) Assert.Equal(MailIssueBucket.New, row.Bucket);
        }
        Assert.Contains(rows, r => r.Source == "brandnew" && r.Bucket == MailIssueBucket.New);
    }

    // ── A8：四個入口數字一致 ────────────────────────────────────

    private RecordListQueryService NewListService(EfIssueAggregateQuery aggregates)
    {
        var visibility = new AlwaysVisibleService(_hosts);
        var severity = new FakeSystemSettingsService();
        var handling = new FakeHandlingStore();
        var users = new FakeUserStore();
        var repository = new RecordRepository(_records, _hosts, visibility, severity);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings);
        return new RecordListQueryService(
            repository, _hosts, users, handling, _issueHandlings, _cases, _settings, severity,
            visibility, aggregates, statusResolver, new UserDisplayNameService(_settings),
            new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));
    }

    [Fact]
    public void A8_四個入口的數字一致()
    {
        SeedHosts(7, 9);
        var from = End.AddDays(-6);
        var aggregates = Query();

        var ranking = new IssueRankingBuilder(aggregates, _hosts, new FixedIssueExclusionSource(IssueExclusion.None))
            .Build(from, End, null, totalHosts: 7);
        var search = NewListService(aggregates).SearchByIssue(new RecordSearchRequest { From = from, To = End });
        var latest = aggregates.LatestOccurrences(IssueExclusion.None, ThreeIssues, from, End, null);
        var digest = new MailIssueDigest(aggregates, new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings),
            _settings, new FixedIssueExclusionSource(IssueExclusion.None)).Build(from, End, null);

        foreach (var (source, eventId) in ThreeIssues)
        {
            var r = ranking.Single(x => x.Source == source && x.EventId == eventId);
            var s = search.Items.Single(x => x.Source == source && x.EventId == eventId);
            Assert.Equal(7, r.HostCount);
            Assert.Equal(r.HostCount, s.HostCount);
            Assert.Equal(r.DayCount, s.DayCount);
            Assert.Equal(r.TotalCount, s.TotalCount);
            Assert.Equal(7, latest.Count(o => o.IssueKey.Contains($"|{source}|")));
        }
        Assert.Equal(7, search.DistinctHostCount);
        Assert.All(digest, row => Assert.Equal(7, row.HostCount));
    }

    // ── A9：兩個後端都翻譯得出來（SQL Server 只驗 EF 翻譯，未實連）───────

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void A9_查詢端取最近一次的兩道查詢_兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= End.AddDays(-6) && x.RecordDate <= End);

        var (latest, names) = EfIssueAggregateQuery.BuildLatestOccurrenceQueries(q, new HashSet<string> { "DISK", "CPU" });

        var latestSql = latest.ToQueryString();
        Assert.Contains("MAX(", latestSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", latestSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JOIN", latestSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISTINCT", names.ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── A0：四個入口的基線量測（改動前後的數字寫進回報）──────────

    [Theory]
    [InlineData(10, 7)]
    [InlineData(10, 28)]
    [InlineData(40, 7)]
    [InlineData(40, 28)]
    public void A0_四個入口的讀回列數與指令數(int hostCount, int days)
    {
        SeedHosts(hostCount, days * 2);   // 本期＋前期各 days 天
        var from = End.AddDays(-(days - 1));
        var aggregates = Query();
        var exclusions = new FixedIssueExclusionSource(IssueExclusion.None);
        var resolver = new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings);
        var list = NewListService(aggregates);
        var ranking = new IssueRankingBuilder(aggregates, _hosts, exclusions);
        var digest = new MailIssueDigest(aggregates, resolver, _settings, exclusions);

        // 暖機：別名索引、EF 模型初始化不計入
        ranking.Build(from, End, null, hostCount);
        list.SearchByIssue(new RecordSearchRequest { From = from, To = End });
        digest.Build(from, End, null);

        var rank = _recorder.Measure(() => ranking.Build(from, End, null, hostCount));
        var first = _recorder.Measure(() => list.SearchByIssue(new RecordSearchRequest { From = from, To = End }));
        // 待派試跑（WorkOrderBoardService.RunTrial）對資料層做的兩個呼叫：全問題聚合＋出現點
        var trial = _recorder.Measure(() =>
        {
            var agg = aggregates.Aggregate(IssueExclusion.None, from, End, null, null, null);
            aggregates.LatestOccurrences(IssueExclusion.None, agg.Select(a => (a.Source, a.EventId)).ToList(), from, End, null, null, null);
        });
        var mail = _recorder.Measure(() => digest.Build(from, End, null));

        _output.WriteLine($"A0 主機={hostCount} 天={days} | 排行 指令={rank.Commands} 讀取={rank.ReadCalls}" +
                          $" | 依問題第一頁 指令={first.Commands} 讀取={first.ReadCalls}" +
                          $" | 待派試跑 指令={trial.Commands} 讀取={trial.ReadCalls}" +
                          $" | 郵件摘要 指令={mail.Commands} 讀取={mail.ReadCalls}");
        Assert.True(rank.Commands > 0 && first.Commands > 0 && trial.Commands > 0 && mail.Commands > 0);
    }
}
