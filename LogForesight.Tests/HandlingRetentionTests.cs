using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態三表與處理歷程的保留天數（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4／G4）。
///
/// 改動前這四個對象**從來不會被清理**：`lf_daily_records` 有 RetentionDays（預設 120），
/// 但它的附屬處理狀態沒有——分析紀錄被清掉之後，處理狀態變成永遠讀不到的孤兒，
/// 卻繼續佔空間並拖慢範圍查詢；`handling_log` 在 6000 台環境是千萬列級且無上限成長。
///
/// 這組測試釘住的不只是「有刪到」，更重要的是**哪些不該被刪**：
///   - 保留期內的狀態；
///   - **進行中的案件不論多舊**——它代表「還沒處理完」，用日期清掉等於幫忙藏爛帳；
///   - 處理歷程用的是稽核的保留天數，不是分析紀錄的。
/// </summary>
public class HandlingRetentionTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private EfIssueHandlingStore IssueHandlings() => new(_fixture.NewContext);
    private EfIssueCaseStore Cases() => new(_fixture.NewContext);
    private EfRecordHandlingStore RecordHandlings() => new(_fixture.NewContext, _fixture.LogStore("handling_log"));

    private static IssueHandling Handling(string host, int daysAgo, string issueKey = "System|Svc|1|1") => new()
    {
        HostName = host,
        Date = DateTime.Today.AddDays(-daysAgo),
        IssueKey = issueKey,
        Status = IssueHandlingStatuses.KnownNoise,
        ActorAccount = "admin",
        UpdatedAt = DateTime.Now
    };

    // ── lf_issue_handling ───────────────────────────────────────────────────

    [Fact]
    public void 問題處理狀態_清掉超過保留天數的_保留期內不動()
    {
        var store = IssueHandlings();
        store.Save(Handling("SRV-A", daysAgo: 200));
        store.Save(Handling("SRV-B", daysAgo: 150));
        store.Save(Handling("SRV-C", daysAgo: 10));

        var removed = store.Prune(retentionDays: 120);

        Assert.Equal(2, removed);
        var left = store.GetMany(new[] { "SRV-A", "SRV-B", "SRV-C" }, DateTime.Today.AddDays(-365), DateTime.Today);
        Assert.Equal("SRV-C", Assert.Single(left).HostName);
    }

    [Fact]
    public void 問題處理狀態_超過單次上限的留待下次_且可續跑刪光()
    {
        var store = IssueHandlings();
        for (var i = 0; i < 6; i++) store.Save(Handling($"SRV-{i}", daysAgo: 200));

        Assert.Equal(4, store.Prune(retentionDays: 120, maxRows: 4, batchSize: 3));
        Assert.Equal(2, store.Prune(retentionDays: 120, maxRows: 4, batchSize: 3));
        Assert.Equal(0, store.Prune(retentionDays: 120, maxRows: 4, batchSize: 3));
    }

    // ── lf_record_handling ──────────────────────────────────────────────────

    [Fact]
    public void 日處理狀態_清掉超過保留天數的_保留期內不動()
    {
        var store = RecordHandlings();
        foreach (var (host, daysAgo) in new[] { ("SRV-A", 200), ("SRV-B", 10) })
        {
            store.Save(new RecordHandling
            {
                HostName = host, Date = DateTime.Today.AddDays(-daysAgo),
                Status = HandlingStatuses.Resolved, UpdatedAt = DateTime.Now
            });
        }

        Assert.Equal(1, store.Prune(retentionDays: 120));
        Assert.Null(store.Get("SRV-A", DateTime.Today.AddDays(-200)));
        Assert.NotNull(store.Get("SRV-B", DateTime.Today.AddDays(-10)));
    }

    // ── lf_issue_cases ──────────────────────────────────────────────────────

    private IssueCase Case(string caseId, string host, string status, DateTime? closedAt, int firstLinkedDaysAgo) => new()
    {
        CaseId = caseId,
        HostName = host,
        IssueKey = "System|Svc|1|1",
        IssueLabel = "Svc 1",
        Status = status,
        ClosedAt = closedAt,
        FirstLinkedDate = DateTime.Today.AddDays(-firstLinkedDaysAgo),
        LastLinkedDate = DateTime.Today.AddDays(-firstLinkedDaysAgo),
        CreatedAt = DateTime.Now.AddDays(-firstLinkedDaysAgo),
        CreatedByAccount = "admin"
    };

    /// <summary>
    /// 判準是**結案時間**，不是事件日期。一個掛了兩年沒人動的進行中案件正是最該被看見的那種，
    /// 依事件日期清掉等於幫忙把爛帳藏起來。
    /// </summary>
    [Fact]
    public void 案件清理_只刪已結案且結案很久的_進行中的不論多舊都留著()
    {
        var store = Cases();
        store.Save(Case("old-closed", "SRV-A", IssueHandlingStatuses.Resolved,
            closedAt: DateTime.Now.AddDays(-200), firstLinkedDaysAgo: 300));
        store.Save(Case("recent-closed", "SRV-B", IssueHandlingStatuses.Resolved,
            closedAt: DateTime.Now.AddDays(-10), firstLinkedDaysAgo: 300));
        store.Save(Case("ancient-open", "SRV-C", IssueHandlingStatuses.InProgress,
            closedAt: null, firstLinkedDaysAgo: 900));

        var removed = store.Prune(retentionDays: 120);

        Assert.Equal(1, removed);
        Assert.Null(store.Get("old-closed"));
        Assert.NotNull(store.Get("recent-closed"));
        // 兩年半前開的案件、至今未結案——這正是清理絕對不能碰的那一種
        Assert.NotNull(store.Get("ancient-open"));
    }

    [Fact]
    public void 案件清理_超過單次上限的留待下次_且可續跑刪光()
    {
        var store = Cases();
        for (var i = 0; i < 5; i++)
        {
            store.Save(Case($"c{i}", $"SRV-{i}", IssueHandlingStatuses.Resolved,
                closedAt: DateTime.Now.AddDays(-200), firstLinkedDaysAgo: 300));
        }

        Assert.Equal(3, store.Prune(retentionDays: 120, maxRows: 3, batchSize: 2));
        Assert.Equal(2, store.Prune(retentionDays: 120, maxRows: 3, batchSize: 2));
        Assert.Equal(0, store.Prune(retentionDays: 120, maxRows: 3, batchSize: 2));
    }

    private IssueCase DismissCase(string caseId, string host, string status, int closedDaysAgo, int createdDaysAgo,
        bool? blocks = null, bool cancelled = false, string issueKey = "System|Svc|1|1") => new()
    {
        CaseId = caseId, HostName = host, IssueKey = issueKey, IssueLabel = "Svc 1", Status = status,
        ClosedAt = DateTime.Now.AddDays(-closedDaysAgo),
        FirstLinkedDate = DateTime.Today.AddDays(-createdDaysAgo), LastLinkedDate = DateTime.Today.AddDays(-createdDaysAgo),
        CreatedAt = DateTime.Now.AddDays(-createdDaysAgo), UpdatedAt = DateTime.Now.AddDays(-closedDaysAgo),
        CreatedByAccount = "admin", BlocksRedispatch = blocks, Cancelled = cancelled
    };

    /// <summary>處理人判定的不處理類維持不過期：清除若刪掉最新那筆，閘門就找不到它、問題會重新派工，也從恢復清單消失。</summary>
    [Fact]
    public void 案件清理_該鍵最新的不處理案件保留_被較新案件取代的舊案件與BlocksRedispatch為false的照刪()
    {
        var store = Cases();
        // 最新的不處理類（blocks 為 null＝預設擋）：保留
        store.Save(DismissCase("latest-wontfix", "SRV-A", IssueHandlingStatuses.WontFix, closedDaysAgo: 200, createdDaysAgo: 300));
        store.Save(DismissCase("latest-noise", "SRV-A2", IssueHandlingStatuses.KnownNoise, closedDaysAgo: 200, createdDaysAgo: 300, blocks: true));
        // 同鍵有較新案件（已處理）：較舊的不處理案件已不影響閘門，照刪；較新的已處理案件也過期照刪
        store.Save(DismissCase("superseded-old", "SRV-B", IssueHandlingStatuses.WontFix, closedDaysAgo: 250, createdDaysAgo: 400));
        store.Save(DismissCase("superseded-new", "SRV-B", IssueHandlingStatuses.Resolved, closedDaysAgo: 200, createdDaysAgo: 300));
        // 明確標記不擋再派工：照刪
        store.Save(DismissCase("not-blocking", "SRV-C", IssueHandlingStatuses.WontFix, closedDaysAgo: 200, createdDaysAgo: 300, blocks: false));
        // 已處理類（一般結案）：照刪
        store.Save(DismissCase("resolved", "SRV-D", IssueHandlingStatuses.Resolved, closedDaysAgo: 200, createdDaysAgo: 300));

        var removed = store.Prune(retentionDays: 120);

        Assert.Equal(4, removed);
        Assert.NotNull(store.Get("latest-wontfix"));
        Assert.NotNull(store.Get("latest-noise"));
        Assert.Null(store.Get("superseded-old"));
        Assert.Null(store.Get("superseded-new"));
        Assert.Null(store.Get("not-blocking"));
        Assert.Null(store.Get("resolved"));
    }

    [Fact]
    public void 案件清理_保留的不處理案件經分批與上限仍不被刪_且計數不含它()
    {
        var store = Cases();
        store.Save(DismissCase("keep", "SRV-K", IssueHandlingStatuses.FalsePositive, closedDaysAgo: 200, createdDaysAgo: 300));
        for (var i = 0; i < 3; i++)
            store.Save(DismissCase($"gone{i}", $"SRV-{i}", IssueHandlingStatuses.Resolved, closedDaysAgo: 200, createdDaysAgo: 300));

        Assert.Equal(3, store.Prune(retentionDays: 120, maxRows: 10, batchSize: 2));
        Assert.Equal(0, store.Prune(retentionDays: 120, maxRows: 10, batchSize: 2));
        Assert.NotNull(store.Get("keep"));
    }

    [Fact]
    public void 案件清理_保留判定在SQLServer與SQLite都翻成EXISTS子查詢()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var sqlServer = new LfDbContext(options);
        using var sqlite = _fixture.NewContext();

        foreach (var sql in new[]
                 {
                     EfIssueCaseStore.Expired(sqlServer, DateTime.Today).ToQueryString(),
                     EfIssueCaseStore.Expired(sqlite, DateTime.Today).ToQueryString()
                 })
        {
            // 子查詢翻成 EXISTS（外層以 NOT 包住保留條件）
            Assert.Contains("EXISTS (", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("closed_at", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── handling_log（G4）───────────────────────────────────────────────────

    private void BackdateLogs(DateTime createdAt)
    {
        using var ctx = _fixture.NewContext();
        foreach (var row in ctx.LogLines.Where(l => l.LogKey == "handling_log").ToList())
            row.CreatedAt = createdAt;
        ctx.SaveChanges();
    }

    [Fact]
    public void 處理歷程_清掉超過保留天數的()
    {
        var store = RecordHandlings();
        for (var i = 0; i < 3; i++)
        {
            store.AppendLog(new RecordHandlingLog
            {
                HostName = "SRV-A", Date = DateTime.Today.AddDays(-500),
                Action = "標記", ActorAccount = "admin", CreatedAt = DateTime.Now.AddDays(-800)
            });
        }
        BackdateLogs(DateTime.Today.AddDays(-800));

        // 稽核的保留天數（730）之外 → 清掉
        Assert.Equal(3, store.PruneLogs(retentionDays: 730));
        Assert.Empty(store.GetLogs("SRV-A", DateTime.Today.AddDays(-500)));
    }

    /// <summary>
    /// 歷程是追責證據，用的是稽核的保留天數（730）而不是執行歷程的（90）——
    /// 用後者的話，證據會比被追究的事件更早消失。
    /// </summary>
    [Fact]
    public void 處理歷程_稽核保留期內的不動()
    {
        var store = RecordHandlings();
        store.AppendLog(new RecordHandlingLog
        {
            HostName = "SRV-A", Date = DateTime.Today.AddDays(-200),
            Action = "標記", ActorAccount = "admin", CreatedAt = DateTime.Now.AddDays(-200)
        });
        BackdateLogs(DateTime.Today.AddDays(-200));

        Assert.Equal(0, store.PruneLogs(retentionDays: 730));
        Assert.Single(store.GetLogs("SRV-A", DateTime.Today.AddDays(-200)));
    }

    /// <summary>
    /// 清掉最舊的歷程不能讓續號從 1 重來——重號會讓同一天的歷程排序錯亂。
    /// 續號起點讀的是**最後**幾行，所以從最舊的一端刪是安全的，這裡把它釘住。
    /// </summary>
    [Fact]
    public void 處理歷程_清理後續號不重來()
    {
        var store = RecordHandlings();
        var day = DateTime.Today.AddDays(-10);
        for (var i = 0; i < 3; i++)
        {
            store.AppendLog(new RecordHandlingLog
            {
                HostName = "SRV-A", Date = day, Action = $"舊-{i}",
                ActorAccount = "admin", CreatedAt = DateTime.Now.AddDays(-800)
            });
        }
        BackdateLogs(DateTime.Today.AddDays(-800));

        // 新一筆留在保留期內
        store.AppendLog(new RecordHandlingLog
        {
            HostName = "SRV-A", Date = day, Action = "新", ActorAccount = "admin", CreatedAt = DateTime.Now
        });

        Assert.Equal(3, store.PruneLogs(retentionDays: 730));

        // 清理後由**另一個 store 實例**續寫：模擬站台重啟，續號起點必須自倖存的最後一行接下去
        var reopened = RecordHandlings();
        reopened.AppendLog(new RecordHandlingLog
        {
            HostName = "SRV-A", Date = day, Action = "重啟後", ActorAccount = "admin", CreatedAt = DateTime.Now
        });

        var logs = reopened.GetLogs("SRV-A", day);
        Assert.Equal(new[] { "新", "重啟後" }, logs.Select(l => l.Action));
        Assert.Equal(logs.Select(l => l.LogId).Distinct().Count(), logs.Count);
    }
}
