using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// webdata 各 store 透過 EF blob／log 底層（SQLite）的往返驗證——證明「換底層、store 邏輯不變」
/// 對整份型（rules）、append-only（audit/import/batch）、複合型（handling）都成立。
/// </summary>
public class EfWebdataStoreTests
{
    // ── 整份型（IJsonBlobStore）─────────────────────────────────────────────

    [Fact]
    public void 規則庫_EF往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new JsonKnownIssueRuleStore(fx.Blob("rules"));

        Assert.False(store.Exists);
        store.Save(new RuleFileContent
        {
            SeedVersion = 3,
            Rules = new List<KnownIssueRule> { new() { Id = "r1", SourcePattern = "disk", EventIds = new[] { 153 } } }
        });

        Assert.True(store.Exists);
        var loaded = store.Load();
        Assert.True(loaded.Success);
        Assert.Equal(3, loaded.Content!.SeedVersion);
        Assert.Equal("r1", loaded.Content.Rules.Single().Id);
    }

    [Fact]
    public void 使用者_EF往返_跨store實例持久()
    {
        using var fx = new EfSqliteFixture();
        new JsonUserStore(fx.Blob("users")).Upsert(new WebUser { Account = "DOMAIN\\a", DisplayName = "甲" });

        // 另一個 store 實例讀同一個 DB key——資料在 DB 裡持久
        var reread = new JsonUserStore(fx.Blob("users")).FindByAccount("domain\\a");
        Assert.NotNull(reread);
        Assert.Equal("甲", reread!.DisplayName);
    }

    // ── append-only（IJsonLogStore）─────────────────────────────────────────

    [Fact]
    public void 稽核_EF附加與查詢往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new JsonAuditLogStore(fx.LogStore("audit"));

        store.Append(new AuditEntry { Action = "login", Account = "a", Result = AuditResult.Ok });
        store.Append(new AuditEntry { Action = "login_failed", Account = "b", Result = AuditResult.Denied });

        Assert.Equal(2, store.Query(new AuditQuery()).Total);
        Assert.Equal(1, store.Count(DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1), "login_failed"));
    }

    [Fact]
    public void 稽核_ID跨store實例遞增不重號()
    {
        using var fx = new EfSqliteFixture();
        new JsonAuditLogStore(fx.LogStore("audit")).Append(new AuditEntry { Action = "x" });

        // 新 store 實例應從 DB 讀回 lastId，續號而非從 1 重來
        var store2 = new JsonAuditLogStore(fx.LogStore("audit"));
        store2.Append(new AuditEntry { Action = "y" });

        var ids = store2.Query(new AuditQuery()).Items.Select(e => e.AuditId).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    // ── 複合型（blob 快照 + log 歷程）───────────────────────────────────────

    [Fact]
    public void 處理狀態_快照與歷程_EF往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new JsonRecordHandlingStore(fx.Blob("handling"), fx.LogStore("handling_log"));
        var date = DateTime.Today;

        store.Save(new RecordHandling { HostName = "SRV-01", Date = date, Status = "in_progress" });
        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Status = "in_progress", Action = "status" });

        Assert.Equal("in_progress", store.Get("SRV-01", date)!.Status);
        Assert.Single(store.GetLogs("SRV-01", date));
    }

    [Fact]
    public void 執行紀錄_開始結束回填_EF往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new JsonBatchRunStore(fx.LogStore("runs"), fx.LogStore("run_logs"));

        var runId = store.StartRun(new BatchRun { HostName = "SRV-01", StartedAt = DateTime.Now });
        store.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-01", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });

        // 同 RunId 取最後一列（結束覆蓋開始）
        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.NotNull(run!.FinishedAt);
        Assert.Equal(0, run.ExitCode);
    }
}
