using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 F（CU-2）：不再自動派工清單的被擋案件改以資料庫端取候選。
/// 對照測試：資料庫端路徑（<see cref="EfIssueCaseStore.GetDismissalCandidates"/> ＋ <see cref="DispatchContext.DismissedCases"/>）
/// 與逐主機直接呼叫 <see cref="DispatchContext.DismissedCases"/> 在固定資料集上結果相同。
/// </summary>
public sealed class DismissedCandidatesTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 12, 20);
    private static readonly DateTime Closed = new(2026, 12, 1, 10, 0, 0);   // 生效日期（2026-11-25）之後

    private readonly EfSqliteFixture _fx = new();
    private readonly ITestOutputHelper _out;

    public DismissedCandidatesTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _fx.Dispose();

    private EfIssueCaseStore Store() => new(_fx.NewContext);

    private static string KeyOf(string source, int eventId) => IssueSignatureKey.For("System", source, eventId, LogEntryType.Error);

    private static IssueCase Case(string id, string host, string source, int eventId, string status, bool? blocks = null,
        DateTime? created = null, DateTime? closed = null, bool cancelled = false, DateTime? updated = null)
    {
        var closedAt = IssueHandlingStatuses.IsClosed(status) ? closed ?? Closed : (DateTime?)null;
        return new IssueCase
        {
            CaseId = id, HostName = host, IssueKey = KeyOf(source, eventId), IssueLabel = $"{source} {eventId}", Status = status,
            CreatedAt = created ?? Closed.AddDays(-3), UpdatedAt = updated ?? created ?? Closed.AddDays(-3), ClosedAt = closedAt,
            FirstLinkedDate = Closed.Date.AddDays(-3), LastLinkedDate = Closed.Date.AddDays(-3),
            BlocksRedispatch = blocks, Cancelled = cancelled, SourceName = source, EventId = eventId
        };
    }

    /// <summary>固定資料集：涵蓋規格列的每個情形（已取消、BlocksRedispatch=false、同主機多件、滿 30 天、生效日前）</summary>
    private static List<IssueCase> Dataset() => new()
    {
        Case("a1", "HOST-A", "disk", 1, IssueHandlingStatuses.WontFix),                                      // 擋
        Case("a2", "HOST-A", "disk", 2, IssueHandlingStatuses.WontFix, blocks: false),                       // 被恢復：不擋
        Case("a3", "HOST-A", "disk", 3, IssueHandlingStatuses.FalsePositive, blocks: true),                  // 擋
        Case("a4", "HOST-A", "disk", 4, IssueHandlingStatuses.Resolved),                                     // 已處理：不是不處理類
        // 同主機同問題多件：較舊的不處理、較新的進行中 → 不擋
        Case("b-old", "HOST-B", "disk", 1, IssueHandlingStatuses.WontFix, created: Closed.AddDays(-20)),
        Case("b-new", "HOST-B", "disk", 1, IssueHandlingStatuses.InProgress, created: Closed.AddDays(-1)),
        // 較新的已取消案件不解除閘門
        Case("c-old", "HOST-C", "disk", 1, IssueHandlingStatuses.WontFix, created: Closed.AddDays(-20)),
        Case("c-cancel", "HOST-C", "disk", 1, IssueHandlingStatuses.Open, created: Closed.AddDays(-1), cancelled: true),
        // 較新的不處理蓋過較舊的不處理（同鍵取最新）
        Case("d-old", "HOST-D", "disk", 1, IssueHandlingStatuses.WontFix, created: Closed.AddDays(-20)),
        Case("d-new", "HOST-D", "disk", 1, IssueHandlingStatuses.KnownNoise, created: Closed.AddDays(-5)),
        // 已取消的不處理案件本身不構成閘門
        Case("e1", "HOST-E", "disk", 1, IssueHandlingStatuses.WontFix, cancelled: true),
        // 生效日期前結案：上線前判定，永久擋（即使很久以前）
        Case("f1", "HOST-F", "disk", 1, IssueHandlingStatuses.WontFix, closed: new DateTime(2026, 11, 1), created: new DateTime(2026, 10, 28)),
        // 平手：建立與更新時間相同，以案件編號小者為最新
        Case("g-1", "HOST-G", "disk", 1, IssueHandlingStatuses.WontFix),
        Case("g-2", "HOST-G", "disk", 1, IssueHandlingStatuses.InProgress),
        // 大小寫不同的主機名：同一台
        Case("h1", "host-h", "disk", 1, IssueHandlingStatuses.WontFix),
        Case("h2", "HOST-H", "disk", 2, IssueHandlingStatuses.WontFix)
    };

    private static List<string> ReferenceIds(IEnumerable<IssueCase> all, DateTime today, IssueRuleLookup rules) =>
        all.GroupBy(c => HostNameKey.Of(c.HostName))
            .SelectMany(g => DispatchContext.DismissedCases(g, today, rules))
            .Select(c => c.CaseId).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static List<string> DbSideIds(EfIssueCaseStore store, IEnumerable<string> hosts, DateTime today, IssueRuleLookup rules) =>
        store.GetDismissalCandidates(hosts)
            .GroupBy(c => HostNameKey.Of(c.HostName))
            .SelectMany(g => DispatchContext.DismissedCases(g, today, rules))
            .Select(c => c.CaseId).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void 資料庫端候選加解除過濾_與逐主機DismissedCases結果相同()
    {
        var data = Dataset();
        var store = Store();
        store.SaveMany(data);
        var hosts = data.Select(c => c.HostName).Distinct().ToList();

        // 低嚴重度規則（永久擋）與高嚴重度規則（滿 30 天解除）、滿 30 天前後各一天，都必須與參考實作一致
        foreach (var (today, rules) in new[]
                 {
                     (Today, TestRules.None), (Today, TestRules.AllHigh),
                     (Closed.Date.AddDays(30), TestRules.AllHigh), (Closed.Date.AddDays(31), TestRules.AllHigh)
                 })
        {
            Assert.Equal(ReferenceIds(data, today, rules), DbSideIds(store, hosts, today, rules));
        }

        // 低嚴重度：a1、a3、d-new、f1、g-1、h1、h2、c-old 都擋；明確指出幾個邊界案件
        var ids = DbSideIds(store, hosts, Today, TestRules.None);
        Assert.Contains("a1", ids);
        Assert.Contains("a3", ids);
        Assert.DoesNotContain("a2", ids);          // BlocksRedispatch=false
        Assert.DoesNotContain("a4", ids);          // 已處理
        Assert.DoesNotContain("b-old", ids);       // 同鍵較新的進行中案件
        Assert.Contains("c-old", ids);             // 較新的是已取消案件
        Assert.DoesNotContain("d-old", ids);       // 同鍵較新的不處理案件取代
        Assert.Contains("d-new", ids);
        Assert.DoesNotContain("e1", ids);          // 已取消
        Assert.Contains("f1", ids);                // 生效日前
        Assert.Contains("g-1", ids);               // 平手（建立、更新時間相同）：案件編號小者 g-1 視為最新，它是不處理 → 擋
    }

    [Fact]
    public void 候選集合只含被擋的案件_不是主機全部案件()
    {
        var data = Dataset();
        var store = Store();
        store.SaveMany(data);

        var candidates = store.GetDismissalCandidates(data.Select(c => c.HostName).Distinct().ToList());

        Assert.True(candidates.Count < data.Count);
        Assert.All(candidates, c =>
        {
            Assert.False(c.Cancelled);
            Assert.NotEqual(false, c.BlocksRedispatch);
            Assert.Contains(c.Status, IssueHandlingStatuses.Dismissals);
        });
        Assert.Empty(store.GetDismissalCandidates(Array.Empty<string>()));
    }

    [Fact]
    public void 超過一批的主機數也完整_分批邊界()
    {
        var store = Store();
        var cases = Enumerable.Range(0, 1203).Select(i => Case($"x{i:D5}", $"BATCH-{i:D5}", "disk", 1, IssueHandlingStatuses.WontFix)).ToList();
        store.SaveMany(cases);

        var got = store.GetDismissalCandidates(cases.Select(c => c.HostName).ToList());

        Assert.Equal(1203, got.Count);
    }

    [Fact]
    public void 資料庫端候選查詢在SQLServer與SQLite都翻得出()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var sqlServer = new LfDbContext(options);
        using var sqlite = _fx.NewContext();
        var keys = new[] { "host-a", "host-b" };

        foreach (var sql in new[]
                 {
                     EfIssueCaseStore.DismissalCandidates(sqlServer, keys).ToQueryString(),
                     EfIssueCaseStore.DismissalCandidates(sqlite, keys).ToQueryString()
                 })
        {
            Assert.Contains("EXISTS (", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blocks_redispatch", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("host_name_key", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 服務層：清單與計數同一個查詢 ───────────────────────────────────────

    private DismissedIssueService ServiceOver(EfIssueCaseStore store, FakeHostStore hosts)
    {
        var settings = new FakeSystemSettingsStore();
        return new DismissedIssueService(store, new FakeHandlingStore(), hosts, new FakeUserStore(), new FakeRuleStore(),
            new FixedVisibilityService(hosts.GetAll().Select(h => h.HostId)), new RecordingAuditService(),
            new UserDisplayNameService(settings), new FixedLocalClock(Today));
    }

    [Fact]
    public void 計數與清單同一個查詢_N個問題_M台()
    {
        var data = Dataset();
        var store = Store();
        store.SaveMany(data);
        var hosts = new FakeHostStore();
        foreach (var name in data.Select(c => HostNameKey.Of(c.HostName)).Distinct()) hosts.Upsert(new WebHost { HostName = name });
        // 主機名以 host_name_key 比對：資料集的 "host-h" 與 "HOST-H" 是同一台
        var service = ServiceOver(store, hosts);

        var list = service.GetList(1);
        var count = service.GetCount();

        Assert.Equal(list.Total, count.Issues);
        Assert.Equal(list.TotalHosts, count.Hosts);
        Assert.True(count.Issues > 0 && count.Hosts > 0);
    }

    [Fact]
    public void 沒有任何被擋住時計數為0()
    {
        var store = Store();
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "H1" });
        store.Save(Case("r1", "H1", "disk", 1, IssueHandlingStatuses.Resolved));

        var count = ServiceOver(store, hosts).GetCount();

        Assert.Equal((0, 0), (count.Issues, count.Hosts));
    }

    // ── 規模 ────────────────────────────────────────────────────────────────

    [ScaleFact]
    public void 規模_2000台不再自動派工清單與計數在1秒內()
    {
        var store = Store();
        var hosts = new FakeHostStore();
        var cases = new List<IssueCase>();
        for (var i = 0; i < 2000; i++)
        {
            var host = $"SRV-{i:D4}";
            hosts.Upsert(new WebHost { HostName = host });
            // 每台 25 件歷史案件（已處理為主），其中每台 2 件是被擋住的不處理
            for (var k = 0; k < 25; k++)
                cases.Add(Case($"s{i:D4}-{k:D2}", host, "src" + (k % 12), 100 + k,
                    k < 2 ? IssueHandlingStatuses.WontFix : IssueHandlingStatuses.Resolved, created: Closed.AddDays(-k)));
        }
        store.SaveMany(cases);
        var service = ServiceOver(store, hosts);
        service.GetCount();   // 暖機（JIT、連線）

        var sw = Stopwatch.StartNew();
        var list = service.GetList(1);
        var count = service.GetCount();
        sw.Stop();

        _out.WriteLine($"2000 台清單＋計數：{sw.ElapsedMilliseconds} ms；{count.Issues} 個問題、{count.Hosts} 台");
        Assert.Equal(2000, count.Hosts);
        Assert.Equal(list.Total, count.Issues);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"耗時 {sw.ElapsedMilliseconds} ms");
    }
}
