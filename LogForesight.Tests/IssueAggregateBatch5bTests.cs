using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 5b（5-4／5-5）：依日期視角與報表風險統計改成查詢端分組／計數。
///
/// 原則是「不改運作邏輯」：每個入口的每個欄位與改動前相同，只減少讀回列數。
/// 對帳一律對「記憶體內重做一次舊演算法」的預言機逐欄比對（預言機在改動前的程式上先通過），
/// 成本面用 <see cref="SqlCommandRecorder"/> 的讀取呼叫次數（與機器快慢無關）。
/// 不使用任何全域累計計數，同一個測試類別可與其他類別並行。
/// </summary>
public sealed class IssueAggregateBatch5bTests : IDisposable
{
    private static readonly DateTime End = new(2026, 8, 28);

    private readonly ITestOutputHelper _output;
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly SqlCommandRecorder _recorder = new();

    public IssueAggregateBatch5bTests(ITestOutputHelper output)
    {
        _output = output;
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    // ── 建構與資料 ────────────────────────────────────────────

    private LfDbContext Recording() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_fx.NewContext().Database.GetDbConnection()).AddInterceptors(_recorder).Options);

    private EfIssueAggregateQuery Query(bool recording = false) =>
        new(recording ? Recording : _fx.NewContext, _hosts, null, () => true);

    private static LogIssueSignature Issue(
        string source, int eventId, IssueCategory category, IssueSeverity severity, int count = 1) => new()
        {
            LogName = "System", Source = source, EventId = eventId, EntryType = LogEntryType.Warning,
            Category = category, Severity = severity, Count = count
        };

    private static LogIssueSignature Disk() => Issue("disk", 153, IssueCategory.Hardware, IssueSeverity.High, 2);
    private static LogIssueSignature Sec() => Issue("sec", 4625, IssueCategory.Security, IssueSeverity.Medium, 3);
    private static LogIssueSignature Cfg() => Issue("cfg", 10016, IssueCategory.Config, IssueSeverity.Low, 1);

    private void Add(long hostId, DateTime date, string risk, bool correlation = false,
        bool dataIncomplete = false, bool? securityAvailable = null, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = _hosts.GetAll().First(h => h.HostId == hostId).HostName, Date = date, RiskLevel = risk,
            CorrelationAlerts = correlation ? new List<string> { "x" } : new List<string>(),
            DataIncomplete = dataIncomplete, SecurityLogAvailable = securityAvailable,
            TopIssues = issues.ToList()
        });

    /// <summary>
    /// 無合併主機的標準資料：每台主機每天一列；風險等級、關聯旗標、缺資料、安全記錄可用與否都輪替，
    /// 每天三個類別的（最高嚴重度, 問題數）互不相同（Hardware 9 筆、Security 4 筆、Config 2 筆）。
    /// </summary>
    private List<long> SeedPlain(int hostCount = 6, int days = 5)
    {
        var ids = new List<long>();
        for (var h = 0; h < hostCount; h++)
            ids.Add(_hosts.Upsert(new WebHost { HostName = $"SRV-{h:D3}", Active = true }).HostId);
        for (var h = 0; h < hostCount; h++)
            for (var d = 0; d < days; d++)
            {
                var issues = new List<LogIssueSignature> { Disk() };
                if (h < 4) issues.Add(Sec());
                if (h < 2) issues.Add(Cfg());
                // 同一類別內混合嚴重度（Hardware 有 High 也有 Low）：類別的最高嚴重度要取最大值
                if (h % 2 == 1) issues.Add(Issue("disk2", 154, IssueCategory.Hardware, IssueSeverity.Low));
                var risk = (h + d) % 3 == 0 ? RiskLevels.High : (h + d) % 3 == 1 ? RiskLevels.Medium : RiskLevels.Low;
                Add(ids[h], End.AddDays(-d), risk, correlation: (h + d) % 4 == 0,
                    dataIncomplete: (h + d) % 5 == 0,
                    securityAvailable: (h + d) % 7 == 0 ? false : (h + d) % 7 == 1 ? null : true,
                    issues: issues.ToArray());
            }
        return ids;
    }

    // ── 預言機：記憶體內重做舊演算法 ──────────────────────────

    private sealed record DayOracle(
        DateTime Date, int High, int Medium, int Low, int Correlation, int HostCount,
        List<(string Category, int Rank, int Count)> Categories);

    private HashSet<long>? Expand(IReadOnlyCollection<long>? hostIds)
    {
        if (hostIds == null) return null;
        var index = new HostAliasIndex(_hosts.GetAll());
        var expanded = new HashSet<long>();
        foreach (var id in hostIds)
        {
            var aliases = index.Aliases(id);
            if (aliases.Count == 0) expanded.Add(id);
            foreach (var a in aliases) expanded.Add(a.HostId);
        }
        return expanded;
    }

    private long Surviving(long hostId)
    {
        var index = new HostAliasIndex(_hosts.GetAll());
        return index.Surviving(hostId)?.HostId ?? hostId;
    }

    private List<DayOracle> OracleByDate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        using var ctx = _fx.NewContext();
        var expanded = Expand(hostIds);
        var ranks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);
        var issuesByRecord = ctx.TopIssues.AsNoTracking().ToList().ToLookup(x => x.RecordId);

        bool Exists(DailyRecordRow r)
        {
            var own = issuesByRecord[r.RecordId].ToList();
            if (categories is { Count: > 0 } && !own.Any(t => categories.Select(c => c.ToString()).Contains(t.Category))) return false;
            if (eventId.HasValue && !own.Any(t => t.EventId == eventId.Value)) return false;
            if (!string.IsNullOrWhiteSpace(source) && !own.Any(t => t.SourceName.ToUpperInvariant() == source.ToUpperInvariant())) return false;
            if (minSeverity.HasValue && !own.Any(t => t.SeverityRank >= (int)minSeverity.Value)) return false;
            return true;
        }

        var rows = ctx.DailyRecords.AsNoTracking().ToList()
            .Where(r => r.RecordDate >= from.Date && r.RecordDate <= to.Date
                && (expanded == null || expanded.Contains(r.HostId))
                && (riskLevels == null || riskLevels.Contains(r.RiskLevel))
                && Exists(r))
            .GroupBy(r => (Host: Surviving(r.HostId), r.RecordDate))
            .Select(g => g.OrderByDescending(x => RiskLevels.Rank(x.RiskLevel)).First())
            .ToList();

        var allowed = riskLevels != null ? rows.Select(r => r.RecordId).ToHashSet() : null;
        var issues = ctx.TopIssues.AsNoTracking().ToList().Where(x =>
            x.RecordDate >= from.Date && x.RecordDate <= to.Date
            && (expanded == null || expanded.Contains(x.HostId))
            && (allowed == null || allowed.Contains(x.RecordId))
            && (ranks == null || ranks.Contains(x.SeverityRank))).ToList();
        var catsByDate = issues.GroupBy(x => x.RecordDate).ToDictionary(g => g.Key, g => g
            .GroupBy(x => x.Category)
            .Select(c => (Category: c.Key, Rank: LegacySeverityRank.Normalize(c.Max(x => x.SeverityRank)), Count: c.Count()))
            .OrderByDescending(c => c.Rank).ThenByDescending(c => c.Count).ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList());

        return rows.GroupBy(r => r.RecordDate).OrderBy(g => g.Key).Select(g => new DayOracle(
            g.Key,
            g.Count(x => x.RiskLevel == RiskLevels.High), g.Count(x => x.RiskLevel == RiskLevels.Medium),
            g.Count(x => x.RiskLevel == RiskLevels.Low), g.Count(x => x.HasCorrelation), g.Count(),
            catsByDate.TryGetValue(g.Key, out var cats) ? cats : new())).ToList();
    }

    /// <summary>
    /// 與預言機逐欄比對。日期順序舊版沒有 ORDER BY（未定義），以日期為鍵比對；
    /// 風險類型清單依（最高嚴重度, 問題數）降冪，同分的先後未定義，因此比對「順序上的（嚴重度, 問題數）序列」與集合。
    /// </summary>
    private static void AssertSameAsOracle(List<DayOracle> expected, List<DateRiskAggregate> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var e in expected)
        {
            var a = Assert.Single(actual, x => x.Date == e.Date);
            Assert.Equal(e.High, a.HighRiskHosts);
            Assert.Equal(e.Medium, a.MediumRiskHosts);
            Assert.Equal(e.Low, a.LowRiskHosts);
            Assert.Equal(e.Correlation, a.CorrelationHosts);
            Assert.Equal(e.HostCount, a.HostCount);
            Assert.Equal(e.Categories.Select(c => c.Category).OrderBy(x => x, StringComparer.Ordinal),
                a.Categories.OrderBy(x => x, StringComparer.Ordinal));
            var lookup = e.Categories.ToDictionary(c => c.Category);
            Assert.Equal(e.Categories.Select(c => (c.Rank, c.Count)),
                a.Categories.Select(c => (lookup[c].Rank, lookup[c].Count)));
        }
    }

    private static readonly IReadOnlySet<string> OnlyHigh = new HashSet<string> { RiskLevels.High };
    private static readonly IReadOnlySet<string> HighMedium = new HashSet<string> { RiskLevels.High, RiskLevels.Medium };

    /// <summary>A1 用的參數組合：無篩選、風險等級、類別、事件、來源、最低嚴重度、嚴重度全站隱藏與組合。</summary>
    public static IEnumerable<object?[]> ByDateCases() => new List<object?[]>
    {
        new object?[] { "無篩選", null, null, null, null, null, null },
        new object?[] { "只看高", OnlyHigh, null, null, null, null, null },
        new object?[] { "高與中", HighMedium, null, null, null, null, null },
        new object?[] { "類別Security", null, new HashSet<IssueCategory> { IssueCategory.Security }, null, null, null, null },
        new object?[] { "事件10016", null, null, 10016, null, null, null },
        new object?[] { "來源SEC", null, null, null, "sec", null, null },
        new object?[] { "最低嚴重度High", null, null, null, null, IssueSeverity.High, null },
        new object?[] { "嚴重度全站隱藏Low", null, null, null, null, null, new HashSet<IssueSeverity> { IssueSeverity.High, IssueSeverity.Medium } },
        new object?[] { "只看高且類別且隱藏", OnlyHigh, new HashSet<IssueCategory> { IssueCategory.Hardware, IssueCategory.Security }, null, null, null, new HashSet<IssueSeverity> { IssueSeverity.High } },
    };

    // ── A1：依日期視角 ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(ByDateCases))]
    public void A1_依日期_無合併主機_與預言機逐欄相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueCategory>? cats, int? eventId, string? source,
        IssueSeverity? minSeverity, IReadOnlySet<IssueSeverity>? visible)
    {
        SeedPlain();
        var from = End.AddDays(-4);

        var actual = Query().AggregateByDate(IssueExclusion.None, from, End, null, risks, cats, eventId, source, minSeverity, visible);
        var expected = OracleByDate(from, End, null, risks, cats, eventId, source, minSeverity, visible);

        Assert.NotEmpty(expected);
        AssertSameAsOracle(expected, actual);
        _output.WriteLine($"{name}：{actual.Count} 天");
    }

    [Theory]
    [MemberData(nameof(ByDateCases))]
    public void A1_依日期_指定可見主機範圍_與預言機逐欄相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueCategory>? cats, int? eventId, string? source,
        IssueSeverity? minSeverity, IReadOnlySet<IssueSeverity>? visible)
    {
        var ids = SeedPlain();
        var scope = ids.Take(3).ToList();
        var from = End.AddDays(-4);

        var actual = Query().AggregateByDate(IssueExclusion.None, from, End, scope, risks, cats, eventId, source, minSeverity, visible);
        var expected = OracleByDate(from, End, scope, risks, cats, eventId, source, minSeverity, visible);

        AssertSameAsOracle(expected, actual);
        _output.WriteLine(name);
    }

    [Fact]
    public void A1_依日期_空的可見範圍_零結果()
    {
        SeedPlain();
        Assert.Empty(Query().AggregateByDate(IssueExclusion.None, End.AddDays(-4), End, Array.Empty<long>()));
    }

    /// <summary>合併主機：同一天合併前後兩個識別各有一列，取風險等級較高的那一列，只算一台。</summary>
    private (long Survivor, long Merged, long Other) SeedMergedSameDay()
    {
        var survivor = _hosts.Upsert(new WebHost { HostName = "B" });
        var merged = _hosts.Upsert(new WebHost { HostName = "A" });
        var other = _hosts.Upsert(new WebHost { HostName = "C" });
        _hosts.Merge(merged.HostId, survivor.HostId);
        var d1 = End.AddDays(-1);
        // 同一天：合併前識別（A）中風險、存活識別（B）高風險——取高
        Add(merged.HostId, d1, RiskLevels.Medium, correlation: true, issues: new[] { Sec() });
        Add(survivor.HostId, d1, RiskLevels.High, correlation: false, issues: new[] { Disk(), Cfg() });
        // 另一天：只有合併前識別有紀錄
        Add(merged.HostId, End.AddDays(-2), RiskLevels.Low, issues: new[] { Cfg() });
        Add(other.HostId, d1, RiskLevels.Low, correlation: true, issues: new[] { Sec() });
        Add(other.HostId, End.AddDays(-2), RiskLevels.High, issues: new[] { Disk() });
        return (survivor.HostId, merged.HostId, other.HostId);
    }

    [Theory]
    [MemberData(nameof(ByDateCases))]
    public void A1_依日期_合併主機同日兩識別_取較高風險_與預言機逐欄相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueCategory>? cats, int? eventId, string? source,
        IssueSeverity? minSeverity, IReadOnlySet<IssueSeverity>? visible)
    {
        SeedMergedSameDay();
        var from = End.AddDays(-4);

        var actual = Query().AggregateByDate(IssueExclusion.None, from, End, null, risks, cats, eventId, source, minSeverity, visible);
        var expected = OracleByDate(from, End, null, risks, cats, eventId, source, minSeverity, visible);

        AssertSameAsOracle(expected, actual);
        _output.WriteLine(name);
    }

    [Fact]
    public void A1_依日期_合併主機同日兩識別_具體數字()
    {
        var (survivor, _, _) = SeedMergedSameDay();
        var d1 = End.AddDays(-1);

        var rows = Query().AggregateByDate(IssueExclusion.None, End.AddDays(-4), End, null);
        var day = Assert.Single(rows, r => r.Date == d1);

        // 兩個識別同一天只算一台（取高），加上 C：2 台、1 高 1 低
        Assert.Equal(2, day.HostCount);
        Assert.Equal(1, day.HighRiskHosts);
        Assert.Equal(0, day.MediumRiskHosts);
        Assert.Equal(1, day.LowRiskHosts);

        // 範圍只含存活主機時，仍會展開合併前的識別，結果相同
        var scoped = Query().AggregateByDate(IssueExclusion.None, End.AddDays(-4), End, new[] { survivor });
        var scopedDay = Assert.Single(scoped, r => r.Date == d1);
        Assert.Equal(1, scopedDay.HostCount);
        Assert.Equal(1, scopedDay.HighRiskHosts);
    }

    /// <summary>範圍裡沒有被合併的主機（合併發生在範圍外）：結果仍與預言機相同。</summary>
    [Theory]
    [MemberData(nameof(ByDateCases))]
    public void A1_依日期_合併主機在範圍外_與預言機逐欄相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueCategory>? cats, int? eventId, string? source,
        IssueSeverity? minSeverity, IReadOnlySet<IssueSeverity>? visible)
    {
        var (_, _, other) = SeedMergedSameDay();
        var from = End.AddDays(-4);
        var scope = new[] { other };

        var actual = Query().AggregateByDate(IssueExclusion.None, from, End, scope, risks, cats, eventId, source, minSeverity, visible);
        var expected = OracleByDate(from, End, scope, risks, cats, eventId, source, minSeverity, visible);

        AssertSameAsOracle(expected, actual);
        _output.WriteLine(name);
    }

    /// <summary>
    /// 沒有合併主機、但資料庫裡同一識別同一天有兩列（資料不乾淨，不強求）：舊版依（存活主機, 日期）去重取較高風險，
    /// 新版不得因為走查詢端分組而把同一台算兩次。
    /// </summary>
    [Fact]
    public void A1_依日期_同一識別同一天兩列_仍去重取較高風險()
    {
        var ids = SeedPlain(hostCount: 3, days: 3);
        var day = End.AddDays(-1);
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.AsNoTracking().First(r => r.HostId == ids[0] && r.RecordDate == day);
            var copy = new DailyRecordRow
            {
                HostId = row.HostId, HostName = row.HostName, RecordDate = row.RecordDate,
                RiskLevel = row.RiskLevel == RiskLevels.High ? RiskLevels.Low : RiskLevels.High,
                HasCorrelation = !row.HasCorrelation, ContentJson = row.ContentJson, CreatedAt = row.CreatedAt
            };
            ctx.DailyRecords.Add(copy);
            ctx.SaveChanges();
        }

        foreach (var risks in new IReadOnlySet<string>?[] { null, OnlyHigh })
        {
            var actual = Query().AggregateByDate(IssueExclusion.None, End.AddDays(-4), End, null, risks);
            var expected = OracleByDate(End.AddDays(-4), End, null, risks);
            AssertSameAsOracle(expected, actual);
            if (risks == null) Assert.Equal(3, Assert.Single(actual, r => r.Date == day).HostCount);
        }
    }

    // ── A2：報表風險統計 ─────────────────────────────────────

    private sealed record KpiOracle(int Issues, int High, int Medium, int Affected, int Gap);

    private KpiOracle OracleKpi(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels, IReadOnlySet<IssueSeverity>? visibleSeverities)
    {
        using var ctx = _fx.NewContext();
        var expanded = Expand(hostIds);
        var ranks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);
        var rows = ctx.DailyRecords.AsNoTracking().ToList().Where(r =>
            r.RecordDate >= from.Date && r.RecordDate <= to.Date
            && (expanded == null || expanded.Contains(r.HostId))
            && (riskLevels == null || riskLevels.Contains(r.RiskLevel))).ToList();
        var allowed = rows.Select(r => r.RecordId).ToHashSet();
        var issues = ctx.TopIssues.AsNoTracking().ToList().Count(x =>
            x.RecordDate >= from.Date && x.RecordDate <= to.Date
            && (expanded == null || expanded.Contains(x.HostId))
            && (riskLevels == null || allowed.Contains(x.RecordId))
            && (ranks == null || ranks.Contains(x.SeverityRank)));
        return new KpiOracle(
            issues,
            rows.Count(r => r.RiskLevel == RiskLevels.High), rows.Count(r => r.RiskLevel == RiskLevels.Medium),
            rows.Where(r => r.RiskLevel == RiskLevels.High || r.RiskLevel == RiskLevels.Medium)
                .Select(r => Surviving(r.HostId)).Distinct().Count(),
            rows.Count(r => r.DataIncomplete || r.SecurityLogAvailable == false));
    }

    private static void AssertKpi(KpiOracle expected, ReportKpiAggregate actual)
    {
        Assert.Equal(expected.Issues, actual.TotalIssues);
        Assert.Equal(expected.High, actual.HighRiskDays);
        Assert.Equal(expected.Medium, actual.MediumRiskDays);
        Assert.Equal(expected.Affected, actual.AffectedHosts);
        Assert.Equal(expected.Gap, actual.CoverageGapDays);
    }

    public static IEnumerable<object?[]> KpiCases() => new List<object?[]>
    {
        new object?[] { "無篩選", null, null },
        new object?[] { "只看高", OnlyHigh, null },
        new object?[] { "高與中且隱藏Low", HighMedium, new HashSet<IssueSeverity> { IssueSeverity.High, IssueSeverity.Medium } },
        new object?[] { "只隱藏", null, new HashSet<IssueSeverity> { IssueSeverity.High } },
    };

    [Theory]
    [MemberData(nameof(KpiCases))]
    public void A2_報表統計_單期與兩期_無合併_與預言機相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueSeverity>? visible)
    {
        var ids = SeedPlain(hostCount: 6, days: 12);
        var from = End.AddDays(-4);
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-4);

        foreach (var scope in new IReadOnlyCollection<long>?[] { null, ids.Take(3).ToList() })
        {
            var single = Query().AggregateReportKpi(IssueExclusion.None, from, End, scope, risks, visible);
            AssertKpi(OracleKpi(from, End, scope, risks, visible), single);

            var (cur, prev) = Query().AggregateReportKpiPair(IssueExclusion.None, from, End, prevFrom, prevTo, scope, risks, visible);
            AssertKpi(OracleKpi(from, End, scope, risks, visible), cur);
            AssertKpi(OracleKpi(prevFrom, prevTo, scope, risks, visible), prev);
            Assert.Equal(Query().AggregateReportKpi(IssueExclusion.None, prevFrom, prevTo, scope, risks, visible), prev);
        }
        _output.WriteLine(name);
    }

    [Theory]
    [MemberData(nameof(KpiCases))]
    public void A2_報表統計_合併主機_與預言機相同(
        string name, IReadOnlySet<string>? risks, IReadOnlySet<IssueSeverity>? visible)
    {
        var (survivor, _, other) = SeedMergedSameDay();
        var from = End.AddDays(-4);
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-4);
        // 前期也放資料，且含缺資料／安全記錄不可用
        Add(survivor, prevTo, RiskLevels.High, dataIncomplete: true, issues: new[] { Disk() });
        Add(other, prevTo, RiskLevels.Medium, securityAvailable: false, issues: new[] { Sec() });

        foreach (var scope in new IReadOnlyCollection<long>?[] { null, new[] { survivor }, new[] { other } })
        {
            AssertKpi(OracleKpi(from, End, scope, risks, visible),
                Query().AggregateReportKpi(IssueExclusion.None, from, End, scope, risks, visible));
            var (cur, prev) = Query().AggregateReportKpiPair(IssueExclusion.None, from, End, prevFrom, prevTo, scope, risks, visible);
            AssertKpi(OracleKpi(from, End, scope, risks, visible), cur);
            AssertKpi(OracleKpi(prevFrom, prevTo, scope, risks, visible), prev);
        }
        _output.WriteLine(name);
    }

    [Fact]
    public void A2_報表統計_空資料與空範圍()
    {
        SeedPlain(hostCount: 2, days: 2);
        var far = End.AddYears(-3);
        Assert.Equal(new ReportKpiAggregate(0, 0, 0, 0, 0),
            Query().AggregateReportKpi(IssueExclusion.None, far, far.AddDays(3), null, null, null));
        var (a, b) = Query().AggregateReportKpiPair(IssueExclusion.None, far, far.AddDays(3), far.AddDays(-9), far.AddDays(-5), null, null, null);
        Assert.Equal(new ReportKpiAggregate(0, 0, 0, 0, 0), a);
        Assert.Equal(new ReportKpiAggregate(0, 0, 0, 0, 0), b);
    }

    // ── 成本面：讀回列數 ─────────────────────────────────────

    [Fact]
    public void A1_依日期_無合併主機_讀回列數不隨主機數成長()
    {
        SeedPlain(hostCount: 10, days: 3);
        var from = End.AddDays(-2);
        var query = Query(recording: true);
        query.AggregateByDate(IssueExclusion.None, from, End, null);   // 暖機

        foreach (var risks in new IReadOnlySet<string>?[] { null, HighMedium })
        {
            _hosts.GetAll();   // 主機清單在記憶體，不經 SQL
            var small = _recorder.Measure(() => query.AggregateByDate(IssueExclusion.None, from, End, null, risks));
            var smallRows = _recorder.ReadCalls;
            AddHosts(10, 30, days: 3);
            var large = _recorder.Measure(() => query.AggregateByDate(IssueExclusion.None, from, End, null, risks));
            _output.WriteLine($"risks={(risks == null ? "-" : "高中")}：10 台 {small}，40 台 {large}");

            Assert.Equal(small.ReadCalls, large.ReadCalls);
            Assert.Equal(small.Commands, large.Commands);
            Assert.True(smallRows <= 20, $"讀回列數應只與天數／類別數有關，實際 {smallRows}");
            RemoveExtraHosts(10);
        }
    }

    /// <summary>對照：範圍內有被合併的主機時走原本路徑，讀回列數隨主機數成長（證明上面的量測分得出兩條路徑）。</summary>
    [Fact]
    public void A1_依日期_有合併主機_走原路徑_讀回列數隨主機數成長()
    {
        SeedPlain(hostCount: 10, days: 3);
        var a = _hosts.Upsert(new WebHost { HostName = "MERGED-A" });
        var b = _hosts.Upsert(new WebHost { HostName = "MERGED-B" });
        _hosts.Merge(a.HostId, b.HostId);
        var from = End.AddDays(-2);
        var query = Query(recording: true);
        query.AggregateByDate(IssueExclusion.None, from, End, null);

        var small = _recorder.Measure(() => query.AggregateByDate(IssueExclusion.None, from, End, null));
        AddHosts(10, 30, days: 3);
        var large = _recorder.Measure(() => query.AggregateByDate(IssueExclusion.None, from, End, null));

        Assert.True(large.ReadCalls > small.ReadCalls + 60, $"10 台 {small.ReadCalls}，40 台 {large.ReadCalls}");
    }

    private readonly List<long> _extra = new();

    private void AddHosts(int firstIndex, int count, int days)
    {
        for (var h = firstIndex; h < firstIndex + count; h++)
        {
            var id = _hosts.Upsert(new WebHost { HostName = $"SRV-{h:D3}", Active = true }).HostId;
            _extra.Add(id);
            for (var d = 0; d < days; d++)
                Add(id, End.AddDays(-d), (h + d) % 3 == 0 ? RiskLevels.High : RiskLevels.Low,
                    issues: new[] { Disk(), Sec() });
        }
    }

    private void RemoveExtraHosts(int keep)
    {
        using var ctx = _fx.NewContext();
        foreach (var id in _extra)
        {
            ctx.TopIssues.Where(t => t.HostId == id).ExecuteDelete();
            ctx.DailyRecords.Where(r => r.HostId == id).ExecuteDelete();
        }
        _extra.Clear();
        Assert.True(keep > 0);
    }

    [Fact]
    public void A2_報表統計_讀回列數不隨天數成長()
    {
        var ids = SeedPlain(hostCount: 5, days: 6);
        var query = Query(recording: true);
        var from = End.AddDays(-2);
        query.AggregateReportKpi(IssueExclusion.None, from, End, null, null, null);   // 暖機

        var singleSmall = _recorder.Measure(() => query.AggregateReportKpi(IssueExclusion.None, from, End, null, null, null));
        var pairSmall = _recorder.Measure(() => query.AggregateReportKpiPair(
            IssueExclusion.None, from, End, from.AddDays(-3), from.AddDays(-1), null, null, null));

        // 同樣 5 台主機，各補 34 天（拉寬後的本期與前期都涵蓋；每台在任一 3 天內都有高或中風險日）
        for (var h = 0; h < ids.Count; h++)
            for (var d = 6; d < 40; d++)
                Add(ids[h], End.AddDays(-d), RiskLevels.High, issues: new[] { Disk() });
        var wideFrom = End.AddDays(-14);
        var singleLarge = _recorder.Measure(() => query.AggregateReportKpi(IssueExclusion.None, wideFrom, End, null, null, null));
        var pairLarge = _recorder.Measure(() => query.AggregateReportKpiPair(
            IssueExclusion.None, wideFrom, End, wideFrom.AddDays(-15), wideFrom.AddDays(-1), null, null, null));

        _output.WriteLine($"單期 {singleSmall.ReadCalls}→{singleLarge.ReadCalls}，兩期 {pairSmall.ReadCalls}→{pairLarge.ReadCalls}");
        Assert.Equal(singleSmall.ReadCalls, singleLarge.ReadCalls);
        Assert.Equal(pairSmall.ReadCalls, pairLarge.ReadCalls);
        Assert.True(singleLarge.ReadCalls <= 20);
    }

    // ── A7：兩個後端都翻譯得出來（SQL Server 只驗 EF 翻譯，未實連）───

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void A7_新查詢兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var days = ctx.DailyRecords.AsNoTracking().Where(r => r.RecordDate >= End.AddDays(-6) && r.RecordDate <= End);
        var issues = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= End.AddDays(-6) && x.RecordDate <= End);

        var byDay = EfIssueAggregateQuery.BuildDayRiskCountsQuery(days).ToQueryString();
        var byCategory = EfIssueAggregateQuery.BuildCategoryDayQuery(issues).ToQueryString();
        Assert.Contains("GROUP BY", byDay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(", byDay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", byCategory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(", byCategory, StringComparison.OrdinalIgnoreCase);

        // 允許的紀錄編號是子查詢（IN (SELECT …)），不是物化後的參數集合
        var allowed = days.Where(r => r.RiskLevel == RiskLevels.High).Select(r => r.RecordId);
        var withSubquery = issues.Where(x => allowed.Contains(x.RecordId));
        Assert.Contains("SELECT", EfIssueAggregateQuery.BuildCategoryDayQuery(withSubquery).ToQueryString()
            .Split("WHERE", 2)[1], StringComparison.OrdinalIgnoreCase);

        // 重複主機日偵測與風險日計數也要能翻譯（執行時才呼叫，ToQueryString 驗翻譯）
        var dupQuery = days.GroupBy(r => new { r.HostId, r.RecordDate }).Where(g => g.Count() > 1).Select(g => 1);
        Assert.Contains("HAVING", dupQuery.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        var kpi = days.GroupBy(_ => 1).Select(g => new
        {
            High = g.Count(r => r.RiskLevel == RiskLevels.High),
            Gap = g.Count(r => r.DataIncomplete || r.SecurityLogAvailable == false)
        });
        Assert.Contains("COUNT(", kpi.ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }
}
