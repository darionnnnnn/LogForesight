using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態自整份 blob 遷入真表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3、
/// 修復規劃 docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三）。
///
/// **這是全案唯一會動到既有資料的一步**，四條約束逐一釘住：
///   1. 中斷後可安全重來——每一份包在單一交易內，被砍就整份回滾、表仍為空。
///   2. 完成與否是**被寫下的事實**（HandlingMigrationState），不是從「表空不空」反推。
///      體檢 S-1：全表判斷在中斷後會誤判成「已搬完」而**靜默丟資料**。
///   3. 搬完不刪 blob，遷移狀態即為它的失效標記。
///   4. 解析失敗顯性失敗，不靜默當空。
///
/// 另外釘住「不在啟動路徑上」：建立 StorageBackend **不會**自己搬資料（G1）。
/// </summary>
public class HandlingBlobMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-mig-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public HandlingBlobMigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "m.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private StorageBackend NewBackend() => new(
        new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={_dbPath}" }, _dir);

    /// <summary>把舊格式的整份 JSON 直接塞進 lf_blobs，模擬升級前的既有資料庫</summary>
    private static void SeedBlob<T>(StorageBackend backend, string key, List<T> items) =>
        backend.Blob(key).Mutate<object?>(_ => (JsonSerializer.Serialize(items, LfJsonOptions.Pretty), null));

    private static IssueHandling Handling(string host, DateTime date, string key, string status) => new()
    {
        HostName = host, Date = date, IssueKey = key, Status = status,
        ActorId = 1, ActorAccount = "admin", Note = "舊資料", UpdatedAt = DateTime.Now
    };

    private static IssueCase Case(string caseId, string host) => new()
    {
        CaseId = caseId, HostName = host, IssueKey = "k1", IssueLabel = "Disk 153",
        Status = IssueHandlingStatuses.InProgress, HandlerId = 42,
        FirstLinkedDate = DateTime.Today.AddDays(-3), LastLinkedDate = DateTime.Today,
        CreatedAt = DateTime.Now, CreatedByAccount = "admin", UpdatedAt = DateTime.Now
    };

    /// <summary>
    /// 造出一個**升級前**的資料庫：三份處理狀態在 blob 裡，且**沒有遷移狀態標記**
    /// ——舊版本根本不知道有這個 key，那正是真實升級情境的形狀。
    /// 回傳重新建立的後端＝模擬新版本的第一次啟動（只做判定，不搬）。
    /// </summary>
    private StorageBackend SeedLegacyDatabase(DateTime date)
    {
        var seeding = NewBackend();
        SeedBlob(seeding, "issue_handling", new List<IssueHandling>
        {
            Handling("SRV-01", date, "k1", "resolved"),
            Handling("SRV-02", date, "k2", "wont_fix")
        });
        SeedBlob(seeding, "issue_cases", new List<IssueCase> { Case("c1", "SRV-01") });
        SeedBlob(seeding, "record_handling", new List<RecordHandling>
        {
            new() { HostName = "SRV-01", Date = date, Status = "in_progress", HandlerId = 7, UpdatedAt = DateTime.Now }
        });

        // 清掉建立 seeding 後端時寫下的遷移狀態：那一次的判定看到的是「還沒有 blob」，
        // 因而標成 completed。真實的升級前資料庫沒有這個標記，清空即等於還原成 Unknown
        seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));

        return NewBackend();
    }

    // ── G1：不在啟動路徑上 ───────────────────────────────────────────────────

    /// <summary>
    /// 建立後端**不會**自己搬資料——搬移是分鐘級的工作，掛在啟動路徑上會撞
    /// Windows 服務 30 秒的啟動逾時（體檢 G1）。啟動時只做毫秒級的判定。
    /// </summary>
    [Fact]
    public void 建立後端不會搬資料_只標記為待搬移()
    {
        var date = DateTime.Today;
        var backend = SeedLegacyDatabase(date);

        Assert.Equal(HandlingMigrationState.Pending, backend.HandlingMigrator.State.State);
        Assert.Empty(backend.IssueHandlingStore().GetForDay("SRV-01", date));
    }

    /// <summary>待搬移期間處理狀態必須是唯讀的——寫入會落進一個馬上要被覆蓋的表</summary>
    [Fact]
    public void 未搬移完成前_應擋下處理狀態的寫入()
    {
        var backend = SeedLegacyDatabase(DateTime.Today);

        Assert.True(backend.HandlingMigrator.State.ShouldBlockWrites);
    }

    // ── 搬移本身 ────────────────────────────────────────────────────────────

    [Fact]
    public void 搬移後_三份資料都在真表且狀態為完成()
    {
        var date = DateTime.Today;
        var backend = SeedLegacyDatabase(date);

        backend.HandlingMigrator.Run(CancellationToken.None);

        Assert.Equal(2, backend.IssueHandlingStore().GetMany(new[] { "SRV-01", "SRV-02" }, date, date).Count);
        Assert.Equal("resolved", backend.IssueHandlingStore().GetForDay("SRV-01", date).Single().Status);
        Assert.NotNull(backend.IssueCaseStore().GetOpen("SRV-01", "k1"));
        Assert.Equal(7, backend.RecordHandlingStore().Get("SRV-01", date)!.HandlerId);

        var state = backend.HandlingMigrator.State;
        Assert.Equal(HandlingMigrationState.Completed, state.State);
        Assert.True(state.AllDone);
        Assert.False(state.ShouldBlockWrites);
        Assert.Equal(2, state.IssueHandlingRows);
        Assert.Equal(1, state.IssueCasesRows);
        Assert.Equal(1, state.RecordHandlingRows);
    }

    [Fact]
    public void 遷移前夜間分析已寫入真表_舊blob的處理狀態仍然全部搬進來()
    {
        // 遷移閘門只擋 HTTP 寫入，但這三張表還有另一個寫入端：AnalysisOrchestrator 把三個
        // 真表 store 交給 IssueCaseCoordinator，夜間分析每個主機日都會寫。重入保護若寫成
        // 「表裡有資料就整批跳過」，排程搶先寫一列就會讓整份舊處理狀態永遠不被搬。
        var date = DateTime.Today;
        var backend = SeedLegacyDatabase(date);

        backend.IssueHandlingStore().SaveMany(new[] { Handling("SRV-99", date, "k9", "open") });
        backend.RecordHandlingStore().Save(new RecordHandling
        {
            HostName = "SRV-99",
            Date = date,
            Status = "open",
            HandlerId = 1,
            UpdatedAt = DateTime.Now
        });

        backend.HandlingMigrator.Run(CancellationToken.None);

        // 舊 blob 的兩筆 issue_handling 與一筆 record_handling 都要在
        Assert.Equal("resolved", backend.IssueHandlingStore().GetForDay("SRV-01", date).Single().Status);
        Assert.Equal("wont_fix", backend.IssueHandlingStore().GetForDay("SRV-02", date).Single().Status);
        Assert.Equal(7, backend.RecordHandlingStore().Get("SRV-01", date)!.HandlerId);
        // 分析先寫入的那一筆不受影響
        Assert.Equal("open", backend.IssueHandlingStore().GetForDay("SRV-99", date).Single().Status);
    }

    /// <summary>搬完不刪 blob——真的出事時資料還在原地，不必從備份還原</summary>
    [Fact]
    public void 搬移後_舊blob保留未刪()
    {
        var backend = SeedLegacyDatabase(DateTime.Today);
        backend.HandlingMigrator.Run(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(backend.Blob("issue_handling").Read()));
        Assert.False(string.IsNullOrWhiteSpace(backend.Blob("issue_cases").Read()));
        Assert.False(string.IsNullOrWhiteSpace(backend.Blob("record_handling").Read()));
    }

    [Fact]
    public void 重複執行_不重複匯入()
    {
        var date = DateTime.Today;
        var backend = SeedLegacyDatabase(date);

        backend.HandlingMigrator.Run(CancellationToken.None);
        backend.HandlingMigrator.Run(CancellationToken.None);   // 再跑一次

        Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-01", date));
    }

    /// <summary>
    /// 重啟後不再搬：狀態已是 completed，`Evaluate()` 直接返回，
    /// 也不會因為「blob 還在」而重新標記為 pending（S-2：blob 已是唯讀備份）。
    /// </summary>
    [Fact]
    public void 搬移完成後重啟_不再標記為待搬移()
    {
        var date = DateTime.Today;
        SeedLegacyDatabase(date).HandlingMigrator.Run(CancellationToken.None);

        var restarted = NewBackend();

        Assert.Equal(HandlingMigrationState.Completed, restarted.HandlingMigrator.State.State);
        Assert.False(restarted.HandlingMigrator.State.ShouldBlockWrites);
    }

    /// <summary>遷移之後在新表寫入的資料，不會被下一次啟動用舊 blob 蓋掉</summary>
    [Fact]
    public void 遷移後的新寫入_不被舊blob覆蓋()
    {
        var date = DateTime.Today;
        var migrated = SeedLegacyDatabase(date);
        migrated.HandlingMigrator.Run(CancellationToken.None);
        migrated.IssueHandlingStore().Save(Handling("SRV-01", date, "k1", "wont_fix"));

        var restarted = NewBackend();
        restarted.HandlingMigrator.Run(CancellationToken.None);

        Assert.Equal("wont_fix", restarted.IssueHandlingStore().GetForDay("SRV-01", date).Single().Status);
    }

    // ── 全新安裝 ────────────────────────────────────────────────────────────

    /// <summary>沒有任何舊 blob＝全新安裝：直接標完成，寫入不該被擋</summary>
    [Fact]
    public void 全新安裝_直接標記完成且不擋寫入()
    {
        var backend = NewBackend();

        Assert.Equal(HandlingMigrationState.Completed, backend.HandlingMigrator.State.State);
        Assert.False(backend.HandlingMigrator.State.ShouldBlockWrites);
        Assert.Empty(backend.IssueHandlingStore().GetForDay("SRV-01", DateTime.Today));
    }

    // ── 失敗處理 ────────────────────────────────────────────────────────────

    /// <summary>
    /// blob 損毀時**顯性失敗**而不是靜默當空：處理狀態靜默消失會讓整站看起來
    /// 「所有問題都沒人處理過」，比失敗難查得多。失敗後狀態退回 pending
    /// （不是 completed）——寫入**繼續被擋**，不會落進半搬完的表。
    /// </summary>
    [Fact]
    public void blob損毀_搬移失敗且維持唯讀()
    {
        var seeding = NewBackend();
        seeding.Blob("issue_handling").Mutate<object?>(_ => ("{ 這不是合法的 JSON", null));
        seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));   // 見 SeedLegacyDatabase

        var backend = NewBackend();
        var ex = Assert.Throws<InvalidOperationException>(() => backend.HandlingMigrator.Run(CancellationToken.None));

        Assert.Contains("issue_handling", ex.Message);
        Assert.Contains("資料未被修改", ex.Message);

        var state = backend.HandlingMigrator.State;
        Assert.Equal(HandlingMigrationState.Pending, state.State);
        Assert.True(state.ShouldBlockWrites);
        Assert.False(string.IsNullOrWhiteSpace(state.LastError));
    }

    /// <summary>
    /// 取消（站台關閉）時不標記完成——下次啟動要能接續。
    /// 這是體檢 S-1 的核心：舊實作以「目標表非空」判斷，中斷後會誤判成已搬完而丟資料。
    /// </summary>
    [Fact]
    public void 取消時不標記完成_下次啟動可接續()
    {
        var date = DateTime.Today;
        var backend = SeedLegacyDatabase(date);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        backend.HandlingMigrator.Run(cts.Token);

        Assert.NotEqual(HandlingMigrationState.Completed, backend.HandlingMigrator.State.State);
        Assert.True(backend.HandlingMigrator.State.ShouldBlockWrites);

        // 接續：重新執行應該把三份都搬完
        backend.HandlingMigrator.Run(CancellationToken.None);
        Assert.Equal(HandlingMigrationState.Completed, backend.HandlingMigrator.State.State);
        Assert.Equal(2, backend.IssueHandlingStore().GetMany(new[] { "SRV-01", "SRV-02" }, date, date).Count);
    }
}
