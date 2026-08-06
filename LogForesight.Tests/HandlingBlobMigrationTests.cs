using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態自整份 blob 遷入真表（docs/SCALE-ISSUE-FIRST-PLAN.md P3）。
///
/// **這是全案唯一會動到既有資料的一步**，三條約束逐一釘住：
///   1. 冪等可重跑——搬過就不再搬，站台重啟不會重複匯入。
///   2. 失敗不破壞舊資料——搬完**不刪** blob，舊資料留在原地當備份。
///   3. 不靜默丟資料——解析失敗直接拋，讓啟動失敗而不是「安靜地少了一半處理狀態」。
///      後者要好幾天後才會有人發現，而那時新舊資料已經混在一起。
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

    [Fact]
    public void 首次啟動_三份blob全部遷入真表()
    {
        var date = DateTime.Today;

        // 第一次建立後端只是為了寫入舊格式資料（此時表是空的，遷移不做事）
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

        // 第二次建立後端＝模擬升級後的啟動，遷移在此發生
        var backend = NewBackend();

        Assert.Equal(2, backend.IssueHandlingStore().GetMany(new[] { "SRV-01", "SRV-02" }, date, date).Count);
        Assert.Equal("resolved", backend.IssueHandlingStore().GetForDay("SRV-01", date).Single().Status);
        Assert.NotNull(backend.IssueCaseStore().GetOpen("SRV-01", "k1"));
        Assert.Equal(7, backend.RecordHandlingStore().Get("SRV-01", date)!.HandlerId);
    }

    /// <summary>搬完不刪 blob——真的出事時資料還在原地，不必從備份還原</summary>
    [Fact]
    public void 遷移後_舊blob保留未刪()
    {
        var date = DateTime.Today;
        var seeding = NewBackend();
        SeedBlob(seeding, "issue_handling", new List<IssueHandling> { Handling("SRV-01", date, "k1", "resolved") });

        var backend = NewBackend();

        Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-01", date));
        Assert.False(string.IsNullOrWhiteSpace(backend.Blob("issue_handling").Read()));
    }

    /// <summary>
    /// 冪等：第二次啟動時目標表已非空，不再匯入。若不擋，每次重啟都會把 blob 再搬一次，
    /// 而唯一索引會讓第二次直接失敗——站台從此起不來。
    /// </summary>
    [Fact]
    public void 重複啟動_不重複匯入()
    {
        var date = DateTime.Today;
        var seeding = NewBackend();
        SeedBlob(seeding, "issue_handling", new List<IssueHandling> { Handling("SRV-01", date, "k1", "resolved") });

        NewBackend();   // 第一次遷移
        var backend = NewBackend();   // 第二次啟動：不該再搬

        Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-01", date));
    }

    /// <summary>
    /// 遷移之後在新表寫入的資料，不會被下一次啟動用舊 blob 蓋掉——
    /// 「表非空即跳過」這條規則保護的正是這件事（blob 停留在遷移當下的內容，不再更新）。
    /// </summary>
    [Fact]
    public void 遷移後的新寫入_不被舊blob覆蓋()
    {
        var date = DateTime.Today;
        var seeding = NewBackend();
        SeedBlob(seeding, "issue_handling", new List<IssueHandling> { Handling("SRV-01", date, "k1", "resolved") });

        var migrated = NewBackend();
        migrated.IssueHandlingStore().Save(Handling("SRV-01", date, "k1", "wont_fix"));

        var restarted = NewBackend();

        Assert.Equal("wont_fix", restarted.IssueHandlingStore().GetForDay("SRV-01", date).Single().Status);
    }

    /// <summary>
    /// blob 損毀時**啟動失敗**而不是靜默當空。處理狀態靜默消失會讓整站看起來
    /// 「所有問題都沒人處理過」，比啟動報錯難查得多——與 JsonBlobCollection 的既有取捨一致。
    /// </summary>
    [Fact]
    public void blob損毀_啟動直接失敗而非靜默丟資料()
    {
        var seeding = NewBackend();
        seeding.Blob("issue_handling").Mutate<object?>(_ => ("{ 這不是合法的 JSON", null));

        var ex = Assert.Throws<InvalidOperationException>(() => NewBackend());

        Assert.Contains("issue_handling", ex.Message);
        Assert.Contains("資料未被修改", ex.Message);
    }

    [Fact]
    public void 空blob_不影響啟動()
    {
        // 全新環境沒有任何舊 blob——遷移應該安靜地什麼都不做
        var backend = NewBackend();

        Assert.Empty(backend.IssueHandlingStore().GetForDay("SRV-01", DateTime.Today));
        Assert.Empty(backend.IssueCaseStore().GetOpenByHandler(1));
    }
}
