using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// webdata 各 store 透過 EF blob／log 底層（SQLite）的往返驗證——證明「換底層、store 邏輯不變」
/// 對整份型（rules）、append-only（audit/import/batch）、複合型（handling）都成立。
/// </summary>
public class EfWebdataStoreTests
{
    // ── 整份型（EfJsonBlobStore）─────────────────────────────────────────────

    [Fact]
    public void 規則庫_EF往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new KnownIssueRuleStore(fx.Blob("rules"));

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
        new UserStore(fx.Blob("users")).Upsert(new WebUser { Account = "DOMAIN\\a", DisplayName = "甲" });

        // 另一個 store 實例讀同一個 DB key——資料在 DB 裡持久
        var reread = new UserStore(fx.Blob("users")).FindByAccount("domain\\a");
        Assert.NotNull(reread);
        Assert.Equal("甲", reread!.DisplayName);
    }

    [Fact]
    public void Sentinel_EF往返_密文原樣保存()
    {
        using var fx = new EfSqliteFixture();
        var store = new SentinelStore(fx.Blob("sentinels"));

        var saved = store.Upsert(new Sentinel
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc",
            PasswordEnc = CryptoHelper.Encrypt("hunter2")
        });

        var reread = new SentinelStore(fx.Blob("sentinels")).FindByName("s1");
        Assert.NotNull(reread);
        Assert.Equal(saved.SentinelId, reread!.SentinelId);
        Assert.Equal("hunter2", CryptoHelper.Decrypt(reread.PasswordEnc));
    }

    // ── append-only（EfJsonLogStore）─────────────────────────────────────────

    [Fact]
    public void 稽核_EF附加與查詢往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new AuditLogStore(fx.LogStore("audit"));

        store.Append(new AuditEntry { Action = "login", Account = "a", Result = AuditResult.Ok });
        store.Append(new AuditEntry { Action = "login_failed", Account = "b", Result = AuditResult.Denied });

        Assert.Equal(2, store.Query(new AuditQuery()).Total);
        Assert.Equal(1, store.Count(DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1), "login_failed"));
    }

    [Fact]
    public void 稽核_ID跨store實例遞增不重號()
    {
        using var fx = new EfSqliteFixture();
        new AuditLogStore(fx.LogStore("audit")).Append(new AuditEntry { Action = "x" });

        // 新 store 實例應從 DB 讀回 lastId，續號而非從 1 重來
        var store2 = new AuditLogStore(fx.LogStore("audit"));
        store2.Append(new AuditEntry { Action = "y" });

        var ids = store2.Query(new AuditQuery()).Items.Select(e => e.AuditId).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    // ── 複合型（blob 快照 + log 歷程）───────────────────────────────────────

    [Fact]
    public void 處理狀態_快照與歷程_EF往返()
    {
        using var fx = new EfSqliteFixture();
        var store = new RecordHandlingStore(fx.Blob("handling"), fx.LogStore("handling_log"));
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
        var store = new BatchRunStore(fx.LogStore("runs"), fx.LogStore("run_logs"));

        var runId = store.StartRun(new BatchRun { HostName = "SRV-01", StartedAt = DateTime.Now });
        store.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-01", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });

        // 同 RunId 取最後一列（結束覆蓋開始）
        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.NotNull(run!.FinishedAt);
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>
    /// 問題層級處理狀態的更新路徑（docs/archive/FEEDBACK-4-PLAN.md §7 附帶修復）：先前 Save() 更新既有列時
    /// 只抄 Status/ActorId/ActorAccount/Note/UpdatedAt，漏了 DueDate（與新增的 CaseId）——
    /// 對已標記過的問題再標「處理中＋預計完成日」，期限不會落盤，逾期判定因此對這類列失效。
    /// 這裡先標一次無期限的 in_progress，再標一次有期限，確認第二次的 DueDate/CaseId 真的更新到。
    /// </summary>
    [Fact]
    public void 問題處理狀態_更新既有列補上DueDate與CaseId()
    {
        using var fx = new EfSqliteFixture();
        var store = new IssueHandlingStore(fx.Blob("issue_handling"));
        var date = DateTime.Today;

        store.Save(new IssueHandling { HostName = "SRV-01", Date = date, IssueKey = "k1", Status = "in_progress", UpdatedAt = DateTime.Now });
        Assert.Null(store.GetForDay("SRV-01", date).Single().DueDate);

        var due = date.AddDays(7);
        store.Save(new IssueHandling { HostName = "SRV-01", Date = date, IssueKey = "k1", Status = "in_progress", DueDate = due, CaseId = "case-1", UpdatedAt = DateTime.Now });

        var updated = store.GetForDay("SRV-01", date).Single();
        Assert.Equal(due, updated.DueDate);
        Assert.Equal("case-1", updated.CaseId);
    }

    /// <summary>
    /// SaveMany（docs/archive/FEEDBACK-4-PLAN.md §0.5）：案件回溯關聯／狀態同步一次涉及多天，
    /// 一次呼叫要能新增、更新、清除三種情況混合處理，且只走一次 Mutate（不逐筆整份讀改寫）。
    /// </summary>
    [Fact]
    public void 問題處理狀態_SaveMany批次寫入新增更新與清除()
    {
        using var fx = new EfSqliteFixture();
        var store = new IssueHandlingStore(fx.Blob("issue_handling"));
        var d1 = DateTime.Today;
        var d2 = d1.AddDays(1);
        var d3 = d1.AddDays(2);

        // 預先寫一筆，後面用 SaveMany 更新它
        store.Save(new IssueHandling { HostName = "SRV-01", Date = d1, IssueKey = "k1", Status = "open", UpdatedAt = DateTime.Now });

        store.SaveMany(new[]
        {
            new IssueHandling { HostName = "SRV-01", Date = d1, IssueKey = "k1", Status = "in_progress", CaseId = "case-1", UpdatedAt = DateTime.Now },
            new IssueHandling { HostName = "SRV-01", Date = d2, IssueKey = "k1", Status = "in_progress", CaseId = "case-1", UpdatedAt = DateTime.Now },
            new IssueHandling { HostName = "SRV-01", Date = d3, IssueKey = "k1", Status = "", UpdatedAt = DateTime.Now }   // 空狀態＝清除（本來就沒有列，應為 no-op）
        });

        Assert.Equal("in_progress", store.GetForDay("SRV-01", d1).Single().Status);
        Assert.Equal("case-1", store.GetForDay("SRV-01", d1).Single().CaseId);
        Assert.Single(store.GetForDay("SRV-01", d2));
        Assert.Empty(store.GetForDay("SRV-01", d3));
        Assert.Equal(2, store.GetByCase("case-1").Count);
    }

    /// <summary>問題案件（docs/archive/FEEDBACK-4-PLAN.md §0）：GetOpen 只回進行中案件，結案後不再算「進行中」，
    /// 但仍查得到歷史紀錄（GetMany）——「上次誰處理的、怎麼結的」不因結案消失。</summary>
    [Fact]
    public void 問題案件_EF往返_進行中與結案語意()
    {
        using var fx = new EfSqliteFixture();
        var store = new IssueCaseStore(fx.Blob("issue_cases"));
        var caseId = Guid.NewGuid().ToString();

        store.Save(new IssueCase
        {
            CaseId = caseId, HostName = "SRV-01", IssueKey = "k1", IssueLabel = "Disk 153",
            Status = "in_progress", HandlerId = 42, FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "a", UpdatedAt = DateTime.Now
        });

        var open = store.GetOpen("SRV-01", "k1");
        Assert.NotNull(open);
        Assert.Equal(42, open!.HandlerId);
        Assert.Contains(store.GetOpenForHost("SRV-01"), c => c.CaseId == caseId);
        Assert.Contains(store.GetOpenByHandler(42), c => c.CaseId == caseId);

        // 結案：GetOpen 找不到了，但 Get/GetMany 仍能查到歷史
        open.Status = "resolved";
        open.ClosedAt = DateTime.Now;
        open.UpdatedAt = DateTime.Now;
        store.Save(open);

        Assert.Null(store.GetOpen("SRV-01", "k1"));
        Assert.Empty(store.GetOpenForHost("SRV-01"));
        Assert.NotNull(store.Get(caseId));
        Assert.Contains(store.GetMany(new[] { "SRV-01" }), c => c.CaseId == caseId && c.Status == "resolved");
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

    /// <summary>
    /// P0-4：<see cref="EfJsonBlobStore.Mutate"/> 撞上暫時性例外（含樂觀鎖衝突）時自動重試並成功落地。
    /// 驗證 P0-4 為相容 SqlServer <c>EnableRetryOnFailure</c> 加上的 execution strategy 包裝
    /// （<c>CreateExecutionStrategy().Execute(...)</c>，每次呼叫都用全新 DbContext）沒有破壞
    /// 既有「外層 for 迴圈依 <c>IsTransient</c> 判斷重試」的行為。
    ///
    /// 未在此以雙 context 模擬真實跨行程並發衝突：<see cref="EfSqliteFixture"/> 為了讓 in-memory DB
    /// 跨 context 保留內容，多個 context 共用同一條實體 SQLite 連線——這與正式環境「不同連線」的
    /// 並發語意不同（同連線同時只能有一個交易，兩個 context 的寫入會被序列化而非真正競態），
    /// 直接丟一個 DbUpdateException 更能精準且穩定地測到「IsTransient 判定＋重試」這條路徑。
    /// </summary>
    [Fact]
    public void Mutate_遇到暫時性例外時自動重試並成功落地()
    {
        using var fx = new EfSqliteFixture();
        var store = fx.Blob("transient-retry");

        var callCount = 0;
        var result = store.Mutate(content =>
        {
            callCount++;
            if (callCount == 1)
                throw new DbUpdateException("模擬暫時性衝突（IsTransient 應判定為可重試）");
            return ((content ?? "") + "v1", callCount);
        });

        Assert.Equal(2, callCount); // 第一次拋出、第二次重試才成功
        Assert.Equal(2, result);

        using var verify = fx.NewContext();
        Assert.Equal("v1", verify.Blobs.Single(b => b.BlobKey == "transient-retry").Content);
    }
}
