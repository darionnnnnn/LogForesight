using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表類型趨勢依日期分段載入（第 78 輪批次 5c）：分段後的回應必須與「整期一次載入」的原算法逐欄相同。
/// 對照組 <see cref="Reference"/> 是原算法的逐字副本（一次 QueryLightweight 整期），與分段實作各自獨立。
/// 不使用全域累計計數：每個案例用自己的計數替身與資料庫。
/// </summary>
public class ReportCategoryTrendSegmentTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsService _settings = new();
    private static readonly DateTime Anchor = DateTime.Today.AddDays(-300);

    public ReportCategoryTrendSegmentTests() => _store = new EfAnalysisRecordStore(_fixture.NewContext, "test");

    public void Dispose() => _fixture.Dispose();

    /// <summary>記錄每次 QueryLightweight 回傳筆數的替身</summary>
    public class SizeProxy : System.Reflection.DispatchProxy
    {
        public IRecordRepository Target { get; set; } = null!;
        public List<int> Sizes { get; } = new();

        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
        {
            try
            {
                var result = m!.Invoke(Target, args);
                if (m.Name == "QueryLightweight" && result is List<DailyAnalysisRecord> list) Sizes.Add(list.Count);
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private (IRecordRepository Repo, SizeProxy Sizes) MakeRepo(IVisibilityService? visibility = null)
    {
        var real = new RecordRepository(_store, _hosts, visibility ?? new AlwaysVisibleService(_hosts), _settings);
        var proxy = DispatchProxy_Create(real);
        return (proxy.Repo, proxy.Sizes);
    }

    private static (IRecordRepository Repo, SizeProxy Sizes) DispatchProxy_Create(IRecordRepository target)
    {
        var p = System.Reflection.DispatchProxy.Create<IRecordRepository, SizeProxy>();
        var sizes = (SizeProxy)(object)p;
        sizes.Target = target;
        return (p, sizes);
    }

    private ReportCategoryTrendService MakeService(IRecordRepository repo, IssueExclusion exclusion, IVisibilityService? visibility = null) =>
        new(repo, _hosts, visibility ?? new AlwaysVisibleService(_hosts), _settings,
            new SummaryCache(new DataVersionStamp()), new FixedIssueExclusionSource(exclusion));

    private static readonly IssueCategory[] Categories =
        { IssueCategory.Storage, IssueCategory.Security, IssueCategory.Other, IssueCategory.Service };

    private static LogIssueSignature Issue(string source, int id, IssueSeverity sev, IssueCategory cat) => new()
    {
        LogName = "System", Source = source, EventId = id, EntryType = LogEntryType.Error,
        Severity = sev, Category = cat, Count = 1
    };

    /// <summary>決定性假資料：多台主機、逐日、混合風險與嚴重度、同紀錄內重複問題、合併主機同日重複</summary>
    private void Seed(int days, bool withMerged)
    {
        var rnd = new Random(78);
        var hosts = Enumerable.Range(0, 5).Select(i => TestData.AddHost(_hosts, "H" + i)).ToList();
        WebHost? old = null;
        if (withMerged)
        {
            old = TestData.AddHost(_hosts, "H0-OLD");
            old.Active = false;
            old.MergedInto = hosts[0].HostId;
        }
        var risks = new[] { "高", "中", "低" };
        var sevs = new[] { IssueSeverity.High, IssueSeverity.Medium, IssueSeverity.Low };
        for (var d = 0; d < days; d++)
        {
            var date = Anchor.AddDays(d);
            foreach (var h in hosts.Concat(old != null && d % 2 == 0 ? new[] { old } : Array.Empty<WebHost>()))
            {
                if (rnd.Next(4) == 0) continue;
                var issues = new List<LogIssueSignature>();
                for (var k = 0; k < rnd.Next(1, 5); k++)
                {
                    var n = rnd.Next(6);
                    var issue = Issue("src" + n, n, sevs[rnd.Next(3)], Categories[n % Categories.Length]);
                    issues.Add(issue);
                    if (rnd.Next(3) == 0) issues.Add(issue);   // 同紀錄內重複
                }
                _store.Append(new DailyAnalysisRecord
                {
                    HostId = h.HostId, Host = h.HostName, Date = date, RiskLevel = risks[rnd.Next(3)], TopIssues = issues
                });
            }
        }
    }

    /// <summary>原算法逐字副本：整期一次載入</summary>
    private ReportCategoryTrendDto Reference(IRecordRepository repo, DateTime from, DateTime to, IssueExclusion exclusion)
    {
        var buckets = ReportBucketing.Build(from, to);
        var dto = new ReportCategoryTrendDto
        {
            From = ReportBucketing.Fmt(from), To = ReportBucketing.Fmt(to), Buckets = ReportBucketing.ToDtos(buckets)
        };
        var (_, _, nothingVisible) = RecordRepository.ResolveVisibility(_settings.GetVisibleDayRiskLevels(), _settings.GetVisibleSeverities());
        if (nothingVisible) return dto;
        var records = repo.QueryLightweight(new RecordQueryFilter { From = from, To = to });
        if (records.Count == 0) return dto;
        var lookup = new HostLookup(_hosts.GetAll());
        var seen = new HashSet<(string, string, DateTime)>();
        var cells = new Dictionary<IssueCategory, (int[] Counts, IssueSeverity?[] Max)>();
        foreach (var record in records)
        {
            var date = record.Date.Date;
            var bucket = ReportBucketing.IndexOf(buckets, date);
            if (bucket < 0) continue;
            var host = (lookup.For(record)?.HostName ?? record.Host).ToUpperInvariant();
            foreach (var issue in record.TopIssues)
            {
                if (exclusion.IsMuted(issue.Source, issue.EventId, date)) continue;
                var key = IssueSignatureKey.For(issue.LogName, issue.Source, issue.EventId, issue.EntryType, issue.EventKey);
                if (!seen.Add((host, key.ToUpperInvariant(), date))) continue;
                if (!cells.TryGetValue(issue.Category, out var cell))
                {
                    cell = (new int[buckets.Count], new IssueSeverity?[buckets.Count]);
                    cells[issue.Category] = cell;
                }
                cell.Counts[bucket]++;
                if (cell.Max[bucket] is not { } cur || issue.Severity > cur) cell.Max[bucket] = issue.Severity;
            }
        }
        dto.Rows = cells.Select(kv => new ReportCategoryTrendRowDto
            {
                Category = kv.Key.ToString(), Total = kv.Value.Counts.Sum(), Cells = kv.Value.Counts.ToList(),
                MaxSeverityPerCell = kv.Value.Max.Select(s => s?.ToString()).ToList()
            })
            .Where(r => r.Total > 0).OrderByDescending(r => r.Total).ThenBy(r => r.Category, StringComparer.Ordinal).ToList();
        dto.CellMax = dto.Rows.Count == 0 ? 0 : dto.Rows.Max(r => r.Cells.Max());
        return dto;
    }

    private static void AssertSame(ReportCategoryTrendDto expected, ReportCategoryTrendDto actual)
    {
        Assert.Equal(expected.From, actual.From);
        Assert.Equal(expected.To, actual.To);
        Assert.Equal(expected.CellMax, actual.CellMax);
        Assert.Equal(expected.Buckets.Count, actual.Buckets.Count);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (var i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i].Category, actual.Rows[i].Category);
            Assert.Equal(expected.Rows[i].Total, actual.Rows[i].Total);
            Assert.Equal(expected.Rows[i].Cells, actual.Rows[i].Cells);
            Assert.Equal(expected.Rows[i].MaxSeverityPerCell, actual.Rows[i].MaxSeverityPerCell);
        }
    }

    private static IssueExclusion Mutes(DateTime from, params (string Source, int Id, int StartOffset, int EndOffset)[] spans) =>
        IssueExclusion.From(spans.Select(s => new IssueProfile
        {
            SourceName = s.Source, EventId = s.Id,
            Mutes = new List<MuteInterval> { new() { From = from.AddDays(s.StartOffset), To = from.AddDays(s.EndOffset) } }
        }), DateTime.Today);

    [Theory]
    [InlineData(30, false)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(37, true)]     // 不是 7 的倍數
    [InlineData(100, false)]
    public void 類型趨勢分段_各種期間長度與合併主機_與整期一次載入逐欄相同(int days, bool merged)
    {
        Seed(days, merged);
        var (repo, _) = MakeRepo();
        var from = Anchor;
        var to = Anchor.AddDays(days - 1);
        var exclusion = Mutes(from, ("src1", 1, 6, 8), ("src2", 2, 13, 14), ("src3", 3, 20, 21));
        AssertSame(Reference(repo, from, to, exclusion), MakeService(repo, exclusion).Get(from, to));
    }

    [Fact]
    public void 類型趨勢分段_跨月期間與靜音區間跨段落邊界()
    {
        Seed(120, true);
        var (repo, _) = MakeRepo();
        // 從某月 20 日起，確保跨月；靜音區間橫跨第 7／8 天與第 14／15 天的邊界
        var from = new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(1).AddDays(19);
        if (from < Anchor) from = Anchor;
        var to = from.AddDays(60);
        var exclusion = Mutes(from, ("src0", 0, 5, 9), ("src4", 4, 13, 16), ("src5", 5, 0, 70));
        AssertSame(Reference(repo, from, to, exclusion), MakeService(repo, exclusion).Get(from, to));
    }

    [Fact]
    public void 類型趨勢分段_同一主機問題日期在不同紀錄重複出現只算一次()
    {
        var a = TestData.AddHost(_hosts, "A");
        var old = TestData.AddHost(_hosts, "A-OLD");
        old.Active = false;
        old.MergedInto = a.HostId;
        var issue = Issue("disk", 1, IssueSeverity.High, IssueCategory.Storage);
        // 第 7 天（段落首日）與第 6 天（前一段末日）各由存活主機與舊識別各寫一筆
        foreach (var offset in new[] { 6, 7, 13, 14 })
            foreach (var h in new[] { a, old })
                _store.Append(new DailyAnalysisRecord
                {
                    HostId = h.HostId, Host = h.HostName, Date = Anchor.AddDays(offset), RiskLevel = "高", TopIssues = { issue }
                });
        var (repo, _) = MakeRepo();
        var to = Anchor.AddDays(20);
        var actual = MakeService(repo, IssueExclusion.None).Get(Anchor, to);
        AssertSame(Reference(repo, Anchor, to, IssueExclusion.None), actual);
        Assert.Equal(4, Assert.Single(actual.Rows).Total);
    }

    [Fact]
    public void 類型趨勢分段_可見範圍受限與嚴重度全站隱藏()
    {
        Seed(60, true);
        var first = _hosts.GetAll().First(h => h.HostName == "H0");
        var scoped = new HostScopedVisibility(_hosts, first.HostId);
        var (repo, _) = MakeRepo(scoped);
        var to = Anchor.AddDays(59);
        AssertSame(Reference(repo, Anchor, to, IssueExclusion.None), MakeService(repo, IssueExclusion.None, scoped).Get(Anchor, to));

        _settings.VisibleSeverities = new HashSet<string> { "High" };
        AssertSame(Reference(repo, Anchor, to, IssueExclusion.None), MakeService(repo, IssueExclusion.None, scoped).Get(Anchor, to));

        _settings.VisibleSeverities = new HashSet<string>();   // 全隱藏
        var hidden = MakeService(repo, IssueExclusion.None, scoped).Get(Anchor, to);
        AssertSame(Reference(repo, Anchor, to, IssueExclusion.None), hidden);
        Assert.Empty(hidden.Rows);
    }

    [Theory]
    [InlineData("高")]
    [InlineData("高", "中")]
    public void 類型趨勢分段_日風險顯示設定只開部分等級(params string[] levels)
    {
        Seed(50, false);
        _settings.VisibleDayRiskLevels = new HashSet<string>(levels);
        var (repo, _) = MakeRepo();
        var to = Anchor.AddDays(49);
        var actual = MakeService(repo, IssueExclusion.None).Get(Anchor, to);
        AssertSame(Reference(repo, Anchor, to, IssueExclusion.None), actual);
        Assert.NotEmpty(actual.Rows);
    }

    [Fact]
    public void 類型趨勢分段_一百八十天單次載入筆數降為約一段的量()
    {
        Seed(180, false);
        var (repo, sizes) = MakeRepo();
        var to = Anchor.AddDays(179);
        MakeService(repo, IssueExclusion.None).Get(Anchor, to);
        var whole = Reference(repo, Anchor, to, IssueExclusion.None);   // 同一替身再跑一次整期，取得整期筆數
        Assert.NotEmpty(whole.Rows);

        var segmentSizes = sizes.Sizes.Take(sizes.Sizes.Count - 1).ToList();   // 最後一筆是 Reference 的整期載入
        var wholeCount = sizes.Sizes[^1];
        Assert.True(segmentSizes.Count >= 180 / 7, "應分成多段載入");
        // 每段最多 7 天 × 5 台；整期約 180 × 4 台以上
        Assert.True(segmentSizes.Max() <= 7 * 5, $"單段筆數 {segmentSizes.Max()}");
        Assert.True(wholeCount > segmentSizes.Max() * 10, $"整期 {wholeCount} 應遠大於單段 {segmentSizes.Max()}");
        Assert.Equal(wholeCount, segmentSizes.Sum());
    }

    [Fact]
    public void 類型趨勢分段_快取鍵含可見主機與靜音token()
    {
        Seed(30, false);
        var cache = new SummaryCache(new DataVersionStamp());
        var a = _hosts.GetAll().First(h => h.HostName == "H0");
        var b = _hosts.GetAll().First(h => h.HostName == "H1");
        var visA = new HostScopedVisibility(_hosts, a.HostId);
        var visB = new HostScopedVisibility(_hosts, b.HostId);
        var (repoA, _) = MakeRepo(visA);
        var (repoB, _) = MakeRepo(visB);
        var to = Anchor.AddDays(29);
        var svcA = new ReportCategoryTrendService(repoA, _hosts, visA, _settings, cache, new FixedIssueExclusionSource(IssueExclusion.None));
        var svcB = new ReportCategoryTrendService(repoB, _hosts, visB, _settings, cache, new FixedIssueExclusionSource(IssueExclusion.None));
        var ra = svcA.Get(Anchor, to);
        var rb = svcB.Get(Anchor, to);
        AssertSame(Reference(repoA, Anchor, to, IssueExclusion.None), ra);
        AssertSame(Reference(repoB, Anchor, to, IssueExclusion.None), rb);
        Assert.NotEqual(ra.Rows.Sum(r => r.Total), rb.Rows.Sum(r => r.Total));
    }
}
