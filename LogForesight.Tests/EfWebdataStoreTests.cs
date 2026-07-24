using LogForesight;
using LogForesight.Sql;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public void Sentinel_EF往返_密文原樣保存()
    {
        using var fx = new EfSqliteFixture();
        var store = new JsonSentinelStore(fx.Blob("sentinels"));

        var saved = store.Upsert(new Sentinel
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc",
            PasswordEnc = CryptoHelper.Encrypt("hunter2")
        });

        var reread = new JsonSentinelStore(fx.Blob("sentinels")).FindByName("s1");
        Assert.NotNull(reread);
        Assert.Equal(saved.SentinelId, reread!.SentinelId);
        Assert.Equal("hunter2", CryptoHelper.Decrypt(reread.PasswordEnc));
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

    // ── 並發保護（lf_blobs.UpdatedAt 為 ConcurrencyToken）──────────────────────

    /// <summary>
    /// 補齊 JSONL 檔案時代跨程序鎖檔要防的事故：兩個行程各自讀到舊內容，
    /// 後寫的整份蓋掉先寫的。UpdatedAt 設為 ConcurrencyToken 後，帶著過期原始值的寫入
    /// 必須被擋下（拋 DbUpdateConcurrencyException），而不是靜默覆蓋掉搶先寫入的內容。
    /// </summary>
    [Fact]
    public void Blob並發衝突_過期寫入被擋下_不遺失搶先寫入的內容()
    {
        using var fx = new EfSqliteFixture();

        using (var seed = fx.NewContext())
        {
            seed.Blobs.Add(new BlobRow { BlobKey = "race", Content = "v0", UpdatedAt = DateTime.Now });
            seed.SaveChanges();
        }

        // ctxA 讀到 v0（帶著 v0 的 UpdatedAt 當原始並發權杖），尚未寫回
        using var ctxA = fx.NewContext();
        var rowA = ctxA.Blobs.Single(b => b.BlobKey == "race");

        // ctxB 搶先讀改寫，UpdatedAt 前進成功落地
        using (var ctxB = fx.NewContext())
        {
            var rowB = ctxB.Blobs.Single(b => b.BlobKey == "race");
            rowB.Content = "v1";
            rowB.UpdatedAt = DateTime.Now.AddSeconds(1);
            ctxB.SaveChanges();
        }

        // ctxA 此時才要寫回：帶的仍是 v0 的原始 UpdatedAt，與 DB 現況（v1 的 UpdatedAt）不符
        rowA.Content = "v0-from-A（不該落地）";
        Assert.Throws<DbUpdateConcurrencyException>(() => ctxA.SaveChanges());

        using var verify = fx.NewContext();
        Assert.Equal("v1", verify.Blobs.Single(b => b.BlobKey == "race").Content);
    }
}
