using System.Text.Json;
using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 資料搬運／遷移器搬入既有資料時的欄位長度處理（第 79 輪批次 D6）。
/// 兩支遷移器（權限異動、處理狀態）都經 <c>SaveChanges</c> 寫入，所以由 <c>LfDbContext.PrepareForSave</c> 涵蓋：
/// 非鍵欄超長截斷後照常搬入、鍵欄（含索引欄）超長的那一列不搬並出聲，其餘列與遷移狀態不受影響。
/// 沒有任何遷移器使用 bulk copy 或原生指令寫入，所以這裡不需要另一個截斷出口。
/// </summary>
public sealed class MigratorTruncationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-migtrunc-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public MigratorTruncationTests()
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

    private static void SeedBlob<T>(StorageBackend backend, string key, List<T> items) =>
        backend.Blob(key).Mutate<object?>(_ => (JsonSerializer.Serialize(items, LfJsonOptions.Pretty), null));

    // ── 權限異動：舊 JSONL → lf_permission_changes ──

    private static PermissionChangeRecord OldPermission(string id, string host, string changeType, string source) => new()
    {
        ChangeId = id, HostName = host, DetectedAt = new DateTime(2026, 8, 1, 10, 0, 0), Target = "Administrators",
        ChangeType = changeType, Before = "b", After = "a", AlertText = "x", Source = source, Category = ""
    };

    private StorageBackend SeedPermissionLegacy(params PermissionChangeRecord[] records)
    {
        var seeding = NewBackend();
        foreach (var r in records)
            seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(r, LfJsonOptions.Pretty));
        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));
        return NewBackend();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 權限異動遷移_非鍵欄超過上限時截斷後搬入_剛好等於上限不動(int over)
    {
        var changeTypeMax = ColumnClamp.For<PermissionChangeRow>(nameof(PermissionChangeRow.ChangeType))!.Value;
        var sourceMax = ColumnClamp.For<PermissionChangeRow>(nameof(PermissionChangeRow.Source))!.Value;
        var backend = SeedPermissionLegacy(OldPermission("c1", "SRV-01", new string('t', changeTypeMax + over), new string('s', sourceMax + over)));

        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        Assert.Equal(PermissionChangeMigrationState.Completed, backend.PermissionChangeMigrator.State.State);
        Assert.Null(backend.PermissionChangeMigrator.State.LastError);
        var row = backend.PermissionChanges().Get("c1");
        Assert.NotNull(row);
        Assert.Equal(changeTypeMax, row!.ChangeType.Length);
        Assert.Equal(sourceMax, row.Source.Length);
    }

    [Fact]
    public void 權限異動遷移_主機名稱鍵超過上限的那一列不搬_其餘列照搬且遷移完成()
    {
        var hostMax = ColumnClamp.For<PermissionChangeRow>(nameof(PermissionChangeRow.HostNameKey))!.Value;
        var backend = SeedPermissionLegacy(
            OldPermission("ok", "SRV-01", "成員新增", "本機監控"),
            OldPermission("edge", new string('h', hostMax), "成員新增", "本機監控"),      // 剛好等於上限：照搬
            OldPermission("long", new string('h', hostMax + 1), "成員新增", "本機監控")); // 超過上限：不搬

        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        Assert.Equal(PermissionChangeMigrationState.Completed, backend.PermissionChangeMigrator.State.State);
        Assert.NotNull(backend.PermissionChanges().Get("ok"));
        Assert.NotNull(backend.PermissionChanges().Get("edge"));
        Assert.Null(backend.PermissionChanges().Get("long"));
    }

    // ── 處理狀態：舊 blob → lf_issue_cases／lf_issue_handling ──

    private static IssueCase OldCase(string label) => new()
    {
        CaseId = "c1", HostName = "SRV-01", IssueKey = "k1", IssueLabel = label,
        Status = IssueHandlingStatuses.InProgress, HandlerId = 42,
        FirstLinkedDate = DateTime.Today.AddDays(-3), LastLinkedDate = DateTime.Today,
        CreatedAt = DateTime.Now, CreatedByAccount = "admin", UpdatedAt = DateTime.Now
    };

    private static IssueHandling OldHandling(string host, DateTime date, string key) => new()
    {
        HostName = host, Date = date, IssueKey = key, Status = "resolved",
        ActorId = 1, ActorAccount = "admin", Note = "舊資料", UpdatedAt = DateTime.Now
    };

    private StorageBackend SeedHandlingLegacy(DateTime date, string issueLabel, params IssueHandling[] handlings)
    {
        var seeding = NewBackend();
        SeedBlob(seeding, "issue_handling", handlings.ToList());
        SeedBlob(seeding, "issue_cases", new List<IssueCase> { OldCase(issueLabel) });
        SeedBlob(seeding, "record_handling", new List<RecordHandling>());
        seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));
        return NewBackend();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 處理狀態遷移_案件說明超過上限時截斷後搬入_剛好等於上限不動(int over)
    {
        var max = ColumnClamp.For<IssueCaseRow>(nameof(IssueCaseRow.IssueLabel))!.Value;
        var date = DateTime.Today;
        var backend = SeedHandlingLegacy(date, new string('l', max + over), OldHandling("SRV-01", date, "k1"));

        backend.HandlingMigrator.Run(CancellationToken.None);

        Assert.Equal(HandlingMigrationState.Completed, backend.HandlingMigrator.State.State);
        Assert.Equal(max, backend.IssueCaseStore().GetOpen("SRV-01", "k1")!.IssueLabel.Length);
    }

    [Fact]
    public void 處理狀態遷移_問題鍵超過上限的處理列不搬_其餘列照搬且遷移完成()
    {
        var keyMax = ColumnClamp.For<IssueHandlingRow>(nameof(IssueHandlingRow.IssueKey))!.Value;
        var date = DateTime.Today;
        var backend = SeedHandlingLegacy(date, "Disk 153",
            OldHandling("SRV-01", date, "k1"),
            OldHandling("SRV-02", date, new string('k', keyMax)),         // 剛好等於上限：照搬
            OldHandling("SRV-03", date, new string('k', keyMax + 1)));    // 超過上限：不搬

        backend.HandlingMigrator.Run(CancellationToken.None);

        Assert.Equal(HandlingMigrationState.Completed, backend.HandlingMigrator.State.State);
        Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-01", date));
        Assert.Single(backend.IssueHandlingStore().GetForDay("SRV-02", date));
        Assert.Empty(backend.IssueHandlingStore().GetForDay("SRV-03", date));
    }
}
