using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 高嚴重度「不再打擾」滿 30 天解除一次（<see cref="DismissalRelease"/>）：判定只在
/// <see cref="DispatchContext.DismissedCases"/>，保留清除、清單剩餘天數、新案件的「先前判定不處理」推導都共用同一個函式。
/// 日期一律用固定日期（生效日期 2026-11-25 之後），不依賴真實的今天。
/// </summary>
public sealed class DismissalReleaseTests : IDisposable
{
    private const string Host = "SRV-01";

    /// <summary>處理人回覆不處理的結案時間（生效日期之後）</summary>
    private static readonly DateTime Closed = new(2026, 12, 1, 10, 0, 0);

    private static string KeyOf(string source = "disk", int eventId = 153) =>
        IssueSignatureKey.For("System", source, eventId, LogEntryType.Error);

    private static IssueCase Case(string id, DateTime closedAt, bool? blocks = null, string status = IssueHandlingStatuses.WontFix,
        DateTime? createdAt = null, string? key = null, bool cancelled = false) => new()
    {
        CaseId = id, HostName = Host, IssueKey = key ?? KeyOf(), IssueLabel = "disk 153", Status = status,
        CreatedAt = createdAt ?? closedAt.AddDays(-3), UpdatedAt = closedAt, ClosedAt = closedAt,
        FirstLinkedDate = closedAt.Date.AddDays(-3), LastLinkedDate = closedAt.Date.AddDays(-3),
        BlocksRedispatch = blocks, Cancelled = cancelled, SourceName = "disk", EventId = 153
    };

    private static bool Blocked(IEnumerable<IssueCase> cases, DateTime today, IssueRuleLookup rules) =>
        DispatchContext.DismissedKeys(cases, today, rules).Contains(KeyOf());

    // ── 邊界：第 30 天仍擋、第 31 天不擋（日曆日差）─────────────────────────────

    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(60, false)]
    public void 處理人回覆的高嚴重度_第30天擋_第31天不擋(int daysAfter, bool blocked)
    {
        var cases = new[] { Case("c1", Closed) };

        Assert.Equal(blocked, Blocked(cases, Closed.Date.AddDays(daysAfter), TestRules.AllHigh));
    }

    [Fact]
    public void 天數以日曆日計_結案當天深夜與隔天凌晨差一天()
    {
        // 12-01 23:59 結案：12-31 00:00 是日曆日差 30 天（仍擋）；隔天 01-01 是 31 天（解除）
        var cases = new[] { Case("c1", new DateTime(2026, 12, 1, 23, 59, 0)) };

        Assert.True(Blocked(cases, new DateTime(2026, 12, 31, 0, 0, 0), TestRules.AllHigh));
        Assert.False(Blocked(cases, new DateTime(2027, 1, 1, 0, 0, 0), TestRules.AllHigh));
    }

    [Fact]
    public void 管理員明確勾選的高嚴重度_第31天仍擋()
    {
        var cases = new[] { Case("c1", Closed, blocks: true) };

        Assert.True(Blocked(cases, Closed.Date.AddDays(31), TestRules.AllHigh));
        Assert.True(Blocked(cases, Closed.Date.AddDays(400), TestRules.AllHigh));
    }

    [Fact]
    public void 中嚴重度_第31天仍擋()
    {
        var cases = new[] { Case("c1", Closed) };

        Assert.True(Blocked(cases, Closed.Date.AddDays(31), TestRules.AllMedium));
    }

    /// <summary>突變參考：拿掉「嚴重度高或重大」條件，這一組（同一天）就會一起變成不擋而紅</summary>
    [Fact]
    public void 嚴重度條件是解除的必要條件_同一天高解除_中擋住()
    {
        var cases = new[] { Case("c1", Closed) };
        var day31 = Closed.Date.AddDays(31);

        Assert.False(Blocked(cases, day31, TestRules.AllHigh));
        Assert.True(Blocked(cases, day31, TestRules.AllMedium));
        Assert.True(Blocked(cases, day31, TestRules.None));
    }

    [Fact]
    public void 嚴重度中但帶重大旗標_第31天不擋()
    {
        var cases = new[] { Case("c1", Closed) };

        Assert.False(Blocked(cases, Closed.Date.AddDays(31), TestRules.MediumMajor));
        Assert.True(Blocked(cases, Closed.Date.AddDays(30), TestRules.MediumMajor));
    }

    [Fact]
    public void 規則不存在_視為非高嚴重度_第31天仍擋()
    {
        var cases = new[] { Case("c1", Closed) };

        Assert.True(Blocked(cases, Closed.Date.AddDays(31), TestRules.None));
    }

    [Fact]
    public void 舊資料的Critical視為高_第31天解除()
    {
        var cases = new[] { Case("c1", Closed) };
        IssueRuleLookup critical = _ => new IssueRuleFacts(IssueSeverity.Critical, false);

        Assert.False(Blocked(cases, Closed.Date.AddDays(31), critical));
    }

    [Fact]
    public void 生效日期前結案的高嚴重度_第31天仍擋_生效當天結案的照樣解除()
    {
        var before = new[] { Case("old", new DateTime(2026, 11, 24, 23, 59, 0)) };
        var onDay = new[] { Case("new", new DateTime(2026, 11, 25, 0, 0, 0)) };

        Assert.True(Blocked(before, new DateTime(2027, 3, 1), TestRules.AllHigh));
        Assert.False(Blocked(onDay, new DateTime(2026, 11, 25).AddDays(31), TestRules.AllHigh));
    }

    [Fact]
    public void 解除只看最新一件_已取消的案件不影響()
    {
        // 舊的不處理（已滿 30 天）＋較新的已取消案件：取消不是處理人的結論，最新有效案件仍是那件舊的，已解除
        var cases = new[]
        {
            Case("old", Closed),
            Case("cancelled", Closed.AddDays(40), createdAt: Closed.AddDays(35), cancelled: true)
        };

        Assert.False(Blocked(cases, Closed.Date.AddDays(45), TestRules.AllHigh));
    }

    // ── 解除一次、可重新計 ─────────────────────────────────────────────────────

    [Fact]
    public void 解除後新案件再回不處理_自新案件結案日重新計30天()
    {
        var first = Case("first", Closed);                                           // 12-01 結案
        var secondClosed = Closed.Date.AddDays(40).AddHours(10);                     // 01-10 又回不處理
        var second = Case("second", secondClosed, createdAt: Closed.Date.AddDays(32));
        var cases = new[] { first, second };

        // 第一件滿 30 天後、新案件還沒結案以前沒有新案件可比：只有第一件，第 31 天解除
        Assert.False(Blocked(new[] { first }, Closed.Date.AddDays(31), TestRules.AllHigh));
        // 新案件結案後：自 01-10 起算，第 30 天（02-09）擋、第 31 天（02-10）不擋，與第一件的日期無關
        Assert.True(Blocked(cases, secondClosed.Date.AddDays(1), TestRules.AllHigh));
        Assert.True(Blocked(cases, secondClosed.Date.AddDays(30), TestRules.AllHigh));
        Assert.False(Blocked(cases, secondClosed.Date.AddDays(31), TestRules.AllHigh));
    }

    [Fact]
    public void ReleasedCases是被放行的補集_與DismissedCases不重疊()
    {
        var cases = new[] { Case("c1", Closed) };
        var day31 = Closed.Date.AddDays(31);

        Assert.Empty(DispatchContext.DismissedCases(cases, day31, TestRules.AllHigh));
        Assert.Equal("c1", Assert.Single(DispatchContext.ReleasedCases(cases, day31, TestRules.AllHigh)).CaseId);
        Assert.Single(DispatchContext.DismissedCases(cases, day31, TestRules.AllMedium));
        Assert.Empty(DispatchContext.ReleasedCases(cases, day31, TestRules.AllMedium));
    }

    // ── 清單：剩餘天數、上線前判定、永久 ───────────────────────────────────────

    [Fact]
    public void 剩餘天數_第30天剩1天_中嚴重度與管理員勾選沒有剩餘天數()
    {
        var c = Case("c1", Closed);

        Assert.Equal(31, DismissalRelease.RemainingDays(c, Closed.Date, TestRules.AllHigh));
        Assert.Equal(1, DismissalRelease.RemainingDays(c, Closed.Date.AddDays(30), TestRules.AllHigh));
        Assert.Null(DismissalRelease.RemainingDays(c, Closed.Date, TestRules.AllMedium));
        Assert.Null(DismissalRelease.RemainingDays(Case("c2", Closed, blocks: true), Closed.Date, TestRules.AllHigh));
    }

    [Fact]
    public void 上線前判定只標高嚴重度且沒有被管理員勾選的舊案件()
    {
        var pre = Case("pre", new DateTime(2026, 11, 10));

        Assert.True(DismissalRelease.IsPreLaunch(pre, TestRules.AllHigh));
        Assert.Null(DismissalRelease.RemainingDays(pre, new DateTime(2026, 11, 20), TestRules.AllHigh));
        Assert.False(DismissalRelease.IsPreLaunch(pre, TestRules.AllMedium));
        Assert.False(DismissalRelease.IsPreLaunch(Case("pre-blocked", new DateTime(2026, 11, 10), blocks: true), TestRules.AllHigh));
        Assert.False(DismissalRelease.IsPreLaunch(Case("post", Closed), TestRules.AllHigh));
    }

    // ── 新案件的「先前判定不處理」推導（與閘門同一個解除條件）──────────────────

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void 新案件_上一件不處理且距結案超過30天才標先前判定不處理(int createdDaysAfterClose, bool marked)
    {
        var previous = Case("prev", Closed);
        var target = new IssueCase
        {
            CaseId = "new", HostName = Host, IssueKey = KeyOf(), Status = IssueHandlingStatuses.InProgress,
            CreatedAt = Closed.Date.AddDays(createdDaysAfterClose).AddHours(2), SourceName = "disk", EventId = 153
        };

        var result = DismissalRelease.PriorDismissalsOf(new[] { target }, new[] { previous }, TestRules.AllHigh);

        Assert.Equal(marked, result.ContainsKey("new"));
        if (marked) Assert.Equal("prev", result["new"].CaseId);
    }

    [Fact]
    public void 新案件_管理員勾選或中嚴重度或上一件是已處理_都不標()
    {
        IssueCase Target() => new()
        {
            CaseId = "new", HostName = Host, IssueKey = KeyOf(), Status = IssueHandlingStatuses.InProgress,
            CreatedAt = Closed.Date.AddDays(40), SourceName = "disk", EventId = 153
        };

        Assert.Empty(DismissalRelease.PriorDismissalsOf(new[] { Target() }, new[] { Case("p", Closed, blocks: true) }, TestRules.AllHigh));
        Assert.Empty(DismissalRelease.PriorDismissalsOf(new[] { Target() }, new[] { Case("p", Closed) }, TestRules.AllMedium));
        Assert.Empty(DismissalRelease.PriorDismissalsOf(new[] { Target() },
            new[] { Case("p", Closed, status: IssueHandlingStatuses.Resolved) }, TestRules.AllHigh));
        // 上一件被取消：不是處理人的結論，不算
        Assert.Empty(DismissalRelease.PriorDismissalsOf(new[] { Target() }, new[] { Case("p", Closed, cancelled: true) }, TestRules.AllHigh));
    }

    // ── 派工脈絡：閘門與系統事件的判定來源 ─────────────────────────────────────

    [Fact]
    public void 派工脈絡_第30天IsDismissed_第31天放行且IsReleased為真()
    {
        var cases = new FakeIssueCaseStore();
        cases.Save(Case("c1", Closed));
        var orders = new FakeWorkOrderStore(cases);

        DispatchContext Ctx(int days) => DispatchContext.Build(
            new DispatchCandidatePool(), new FakeIssueOwnerStore(), orders, cases, new FakeNoiseMarkStore(), new SystemSettings(),
            Closed.Date.AddDays(days), TestRules.AllHigh);

        var day30 = Ctx(30);
        Assert.True(day30.IsDismissed(Host, KeyOf()));
        Assert.False(day30.IsReleased(Host, KeyOf()));

        var day31 = Ctx(31);
        Assert.False(day31.IsDismissed(Host, KeyOf()));
        Assert.True(day31.IsReleased(Host, KeyOf()));
    }

    // ── 保留清除與派工閘門用同一個函式 ─────────────────────────────────────────

    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void 保留清除_已解除的過期案件被清掉_仍擋的保留()
    {
        var today = new DateTime(2027, 1, 15);
        var store = new EfIssueCaseStore(_fixture.NewContext, new FixedLocalClock(today));
        // 滿 30 天已解除（12-01 結案，距今 45 天）→ 清掉
        store.Save(Case("released", Closed, key: KeyOf("disk", 1)));
        // 同樣滿 30 天，但管理員明確勾選 → 永久，保留
        store.Save(Case("admin-blocked", Closed, blocks: true, key: KeyOf("disk", 2)));
        // 12-20 結案，距今 26 天、還沒滿 30 天 → 仍擋，保留
        store.Save(Case("within30", new DateTime(2026, 12, 20), key: KeyOf("disk", 3)));
        // 中嚴重度：永久，保留
        store.Save(Case("medium", Closed, key: KeyOf("disk", 4)));
        // 生效日期前結案的高嚴重度：不自動解除，保留
        store.Save(Case("prelaunch", new DateTime(2026, 11, 10), key: KeyOf("disk", 5)));

        IssueRuleLookup rules = key => key == KeyOf("disk", 4) ? new IssueRuleFacts(IssueSeverity.Medium, false)
                                                              : new IssueRuleFacts(IssueSeverity.High, false);

        // retentionDays=1：除了解除與否的判定，其餘全部都已超過保留期
        var removed = store.Prune(retentionDays: 1, rules);

        Assert.Equal(1, removed);
        Assert.Null(store.Get("released"));
        foreach (var kept in new[] { "admin-blocked", "within30", "medium", "prelaunch" })
            Assert.NotNull(store.Get(kept));
    }

    [Fact]
    public void 保留清除與派工閘門對同一組案件的結論一致()
    {
        var today = new DateTime(2027, 1, 15);
        var store = new EfIssueCaseStore(_fixture.NewContext, new FixedLocalClock(today));
        var cases = new[]
        {
            Case("a", Closed, key: KeyOf("disk", 1)),
            Case("b", Closed, blocks: true, key: KeyOf("disk", 2)),
            Case("c", new DateTime(2026, 12, 20), key: KeyOf("disk", 3))
        };
        foreach (var c in cases) store.Save(c);

        var gateBlocked = DispatchContext.DismissedCases(cases, today, TestRules.AllHigh).Select(c => c.CaseId).ToHashSet();
        store.Prune(retentionDays: 1, TestRules.AllHigh);
        var kept = cases.Where(c => store.Get(c.CaseId) != null).Select(c => c.CaseId).ToHashSet();

        // 清除之後還在的，正好是閘門仍擋的（兩邊用同一個 DismissalRelease.IsReleased）
        Assert.Equal(gateBlocked, kept);
    }

    [Fact]
    public void 保留清除的已解除清單在SQLServer與SQLite都翻得出查詢()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var sqlServer = new LfDbContext(options);
        using var sqlite = _fixture.NewContext();
        var released = new[] { "released-1", "released-2" };

        foreach (var sql in new[]
                 {
                     EfIssueCaseStore.Expired(sqlServer, DateTime.Today, released).ToQueryString(),
                     EfIssueCaseStore.Expired(sqlite, DateTime.Today, released).ToQueryString()
                 })
        {
            Assert.Contains("case_id", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EXISTS (", sql, StringComparison.OrdinalIgnoreCase);
        }

        // 解除判定的預篩查詢：兩個後端都翻得出，且帶 blocks_redispatch 與結案日條件
        foreach (var sql in new[]
                 {
                     EfIssueCaseStore.ReleaseCandidates(sqlServer, DateTime.Today).ToQueryString(),
                     EfIssueCaseStore.ReleaseCandidates(sqlite, DateTime.Today).ToQueryString()
                 })
        {
            Assert.Contains("blocks_redispatch", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("closed_at", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
