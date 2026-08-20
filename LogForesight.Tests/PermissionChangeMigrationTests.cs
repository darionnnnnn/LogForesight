using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動自舊格式（JSONL log 與 blob）搬移至真表 lf_permission_changes 的單元測試。
/// 驗證：單一交易、重入保護、去重、類別重算、帳號提取、確認狀態附著、孤兒確認跳過、
/// 失敗處理、舊資料保留、MigrationGateMiddleware 攔截非 GET 請求等。
/// </summary>
public class PermissionChangeMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-perm-mig-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public PermissionChangeMigrationTests()
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

    /// <summary>造出升級前的既有資料庫</summary>
    private StorageBackend SeedLegacyDatabase()
    {
        var seeding = NewBackend();

        var record1 = new PermissionChangeRecord
        {
            ChangeId = "c1",
            HostName = "SRV-01",
            DetectedAt = new DateTime(2026, 8, 1, 10, 0, 0),
            Target = "本機 Administrators 群組",
            ChangeType = "成員新增",
            Before = "（不在群組中）",
            After = "DOMAIN\\alice",
            AlertText = "【提權】本機 Administrators 群組新增成員：DOMAIN\\alice",
            Source = "本機監控",
            Category = "", // 舊格式沒有 category
            IsPrivilegedTarget = false // 舊格式沒有此旗標
        };

        var record2 = new PermissionChangeRecord
        {
            ChangeId = "c2",
            HostName = "SRV-02",
            DetectedAt = new DateTime(2026, 8, 2, 11, 0, 0),
            Target = "D:\\Confidential",
            ChangeType = "權限新增（ACL 規則）",
            Before = "（無此規則）",
            After = "DOMAIN\\bob｜Allow｜FullControl｜明確設定",
            AlertText = "【權限新增】D:\\Confidential：DOMAIN\\bob｜Allow｜FullControl｜明確設定",
            Source = "本機監控",
            Category = ""
        };

        var record3 = new PermissionChangeRecord
        {
            ChangeId = "c3",
            HostName = "DC-01",
            DetectedAt = new DateTime(2026, 8, 3, 14, 0, 0),
            Target = "Security Group A",
            ChangeType = "成員移除",
            Before = "DOMAIN\\charlie",
            After = "（已移出群組）",
            AlertText = "【權限變更】本機 Administrators 群組移除成員：DOMAIN\\charlie\r\nSubject:\r\n  Account Name: admin_user",
            Source = "NetIQ 事件",
            EventId = 4729,
            Category = ""
        };

        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record1, LfJsonOptions.Pretty));
        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record2, LfJsonOptions.Pretty));
        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record3, LfJsonOptions.Pretty));

        SeedBlob(seeding, "perm_confirms", new List<PermissionChangeConfirmation>
        {
            new()
            {
                ChangeId = "c1",
                Status = PermissionConfirmStatuses.Authorized,
                ConfirmedBy = 99,
                ConfirmedByAccount = "sec_auditor",
                ConfirmedAt = new DateTime(2026, 8, 4, 9, 0, 0),
                Note = "核准變更"
            }
        });

        // 清除 seeding 產生的狀態以還原成升級前狀態
        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));

        return NewBackend();
    }

    [Fact]
    public void 舊JSONL與確認blob遷移後_新表的列數與內容正確且確認狀態正確附著()
    {
        var backend = SeedLegacyDatabase();
        var migrator = backend.PermissionChangeMigrator;

        migrator.Run(CancellationToken.None);

        var state = migrator.State;
        Assert.Equal(PermissionChangeMigrationState.Completed, state.State);
        Assert.Equal(3, state.MigratedRows);
        Assert.False(state.ShouldBlockWrites);
        Assert.Null(state.LastError);

        var c1 = backend.PermissionChanges().Get("c1");
        Assert.NotNull(c1);
        Assert.Equal("SRV-01", c1.HostName);
        Assert.Equal(PermissionCategory.GroupMember, c1.Category);
        Assert.True(c1.IsPrivilegedTarget);
        Assert.Equal("DOMAIN\\alice", c1.TargetAccount);

        var confirms = backend.PermissionChanges().GetConfirmations(new[] { "c1", "c2", "c3" });
        Assert.Equal(3, confirms.Count);

        var confirm1 = confirms.Single(c => c.ChangeId == "c1");
        Assert.Equal(PermissionConfirmStatuses.Authorized, confirm1.Status);
        Assert.Equal(99, confirm1.ConfirmedBy);
        Assert.Equal("sec_auditor", confirm1.ConfirmedByAccount);
        Assert.Equal("核准變更", confirm1.Note);

        var confirm2 = confirms.Single(c => c.ChangeId == "c2");
        Assert.Equal(PermissionConfirmStatuses.Pending, confirm2.Status);
        Assert.Null(confirm2.ConfirmedBy);

        var confirm3 = confirms.Single(c => c.ChangeId == "c3");
        Assert.Equal(PermissionConfirmStatuses.Pending, confirm3.Status);
    }

    [Fact]
    public void 遷移時重算類別_舊資料category為空_遷移後依change_type重算且非空()
    {
        var seeding = NewBackend();

        var changeTypes = new[]
        {
            ("成員新增", null, PermissionCategory.GroupMember),
            ("成員移除", null, PermissionCategory.GroupMember),
            ("權限新增（ACL 規則）", null, PermissionCategory.FolderAcl),
            ("權限移除（ACL 規則）", null, PermissionCategory.FolderAcl),
            ("權限變更", (int?)4670, PermissionCategory.FolderAcl),
            ("擁有者變更", null, PermissionCategory.OwnerChange),
            ("無法存取", null, PermissionCategory.FolderAccess),
            ("恢復可存取", null, PermissionCategory.FolderAccess),
            ("稽核政策變更", (int?)4719, PermissionCategory.AuditPolicy),
            ("權限異動（彙總）", null, PermissionCategory.Summary),
            ("自訂其他類型", null, PermissionCategory.Other)
        };

        for (var i = 0; i < changeTypes.Length; i++)
        {
            var (changeType, eventId, _) = changeTypes[i];
            var record = new PermissionChangeRecord
            {
                ChangeId = $"type-test-{i}",
                HostName = "SRV-TEST",
                DetectedAt = DateTime.Today,
                Target = changeType == "成員新增" ? "Administrators" : "Target",
                ChangeType = changeType,
                EventId = eventId,
                Category = "" // 舊資料無 category
            };
            seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record, LfJsonOptions.Pretty));
        }

        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));

        var backend = NewBackend();
        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        for (var i = 0; i < changeTypes.Length; i++)
        {
            var (changeType, _, expectedCat) = changeTypes[i];
            var item = backend.PermissionChanges().Get($"type-test-{i}");
            Assert.NotNull(item);
            Assert.False(string.IsNullOrWhiteSpace(item.Category));
            Assert.Equal(expectedCat, item.Category);
        }
    }

    [Fact]
    public void 重複執行遷移_列數不變且不產生重複列()
    {
        var backend = SeedLegacyDatabase();
        var migrator = backend.PermissionChangeMigrator;

        migrator.Run(CancellationToken.None);
        var firstCount = backend.PermissionChanges().Query(null, null, 100).Count;
        Assert.Equal(3, firstCount);

        migrator.Run(CancellationToken.None); // 第二次執行
        var secondCount = backend.PermissionChanges().Query(null, null, 100).Count;
        Assert.Equal(3, secondCount);
    }

    [Fact]
    public void 遷移前背景分析已寫入新表_舊資料仍然全部搬進來()
    {
        // 遷移閘門只擋得住 HTTP 寫入，但權限異動的寫入端還有背景排程的分析流程。
        // 重入保護若寫成「表裡有資料就整批跳過」，夜間分析先寫進一列，舊待辦就會永久消失。
        var backend = SeedLegacyDatabase();

        backend.PermissionChanges().AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "analysis-written",
                HostName = "SRV-99",
                DetectedAt = new DateTime(2026, 8, 20, 2, 0, 0),
                Target = "本機 Administrators 群組",
                ChangeType = "成員新增"
            }
        });

        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        var store = backend.PermissionChanges();
        Assert.Equal(4, store.Query(null, null, 100).Count);
        Assert.NotNull(store.Get("c1"));
        Assert.NotNull(store.Get("c2"));
        Assert.NotNull(store.Get("c3"));
        Assert.NotNull(store.Get("analysis-written"));
    }

    [Fact]
    public void 遷移中途失敗_狀態退回待處理且LastError有值()
    {
        var seeding = NewBackend();
        seeding.LogStore("perm_changes").AppendLine("{ 這不是合法的 JSON 內容");
        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));

        var backend = NewBackend();
        var migrator = backend.PermissionChangeMigrator;

        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Run(CancellationToken.None));
        Assert.Contains("perm_changes", ex.Message);

        var state = migrator.State;
        Assert.Equal(PermissionChangeMigrationState.Pending, state.State);
        Assert.True(state.ShouldBlockWrites);
        Assert.False(string.IsNullOrWhiteSpace(state.LastError));
    }

    [Fact]
    public void 孤兒確認列_blob有但log無對應異動_遷移不失敗且記警告略過()
    {
        var seeding = NewBackend();
        var record = new PermissionChangeRecord
        {
            ChangeId = "valid-c1",
            HostName = "SRV-01",
            DetectedAt = DateTime.Today,
            Target = "Test",
            ChangeType = "成員新增"
        };
        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record, LfJsonOptions.Pretty));

        SeedBlob(seeding, "perm_confirms", new List<PermissionChangeConfirmation>
        {
            new() { ChangeId = "valid-c1", Status = PermissionConfirmStatuses.Authorized, ConfirmedByAccount = "user1" },
            new() { ChangeId = "orphan-c999", Status = PermissionConfirmStatuses.Suspicious, ConfirmedByAccount = "user2" }
        });

        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));

        var backend = NewBackend();
        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        Assert.Equal(PermissionChangeMigrationState.Completed, backend.PermissionChangeMigrator.State.State);
        var rows = backend.PermissionChanges().Query(null, null, 100);
        Assert.Single(rows);
        Assert.Equal("valid-c1", rows[0].ChangeId);
    }

    [Fact]
    public void 沒有舊資料時_Evaluate直接標成完成()
    {
        var backend = NewBackend();
        var migrator = backend.PermissionChangeMigrator;

        migrator.Evaluate();

        Assert.Equal(PermissionChangeMigrationState.Completed, migrator.State.State);
        Assert.False(migrator.State.ShouldBlockWrites);
        Assert.NotNull(migrator.State.CompletedAt);
    }

    [Fact]
    public void 重複changeId在舊資料中出現時_保留最後一筆()
    {
        var seeding = NewBackend();

        var record1 = new PermissionChangeRecord
        {
            ChangeId = "dup-01",
            HostName = "SRV-01",
            DetectedAt = new DateTime(2026, 8, 1),
            Target = "Target1",
            ChangeType = "成員新增",
            AlertText = "First Alert"
        };

        var record2 = new PermissionChangeRecord
        {
            ChangeId = "dup-01",
            HostName = "SRV-01",
            DetectedAt = new DateTime(2026, 8, 2),
            Target = "Target2",
            ChangeType = "成員新增",
            AlertText = "Second Alert (Latest)"
        };

        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record1, LfJsonOptions.Pretty));
        seeding.LogStore("perm_changes").AppendLine(JsonSerializer.Serialize(record2, LfJsonOptions.Pretty));
        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));

        var backend = NewBackend();
        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        var rows = backend.PermissionChanges().Query(null, null, 100);
        Assert.Single(rows);
        Assert.Equal("dup-01", rows[0].ChangeId);
        Assert.Equal("Second Alert (Latest)", rows[0].AlertText);
    }

    [Fact]
    public async Task 遷移未完成時_非GET請求回503_GET請求放行()
    {
        var backend = SeedLegacyDatabase();
        // 遷移狀態目前為 Pending
        Assert.True(backend.PermissionChangeMigrator.State.ShouldBlockWrites);

        // 1. POST /api/permission-changes 應被擋下回 503
        var (reachedPost, contextPost) = await InvokeGate("POST", "/api/permission-changes/confirm", backend);
        Assert.False(reachedPost);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, contextPost.Response.StatusCode);

        contextPost.Response.Body.Position = 0;
        using var docPost = JsonDocument.Parse(contextPost.Response.Body);
        Assert.False(docPost.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ApiErrorCodes.Conflict, docPost.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("權限異動", docPost.RootElement.GetProperty("error").GetProperty("message").GetString());

        // 2. GET /api/permission-changes 應放行
        var (reachedGet, contextGet) = await InvokeGate("GET", "/api/permission-changes", backend);
        Assert.True(reachedGet);
        Assert.Equal(StatusCodes.Status200OK, contextGet.Response.StatusCode);

        // 3. HEAD /api/permission-changes 應放行
        var (reachedHead, _) = await InvokeGate("HEAD", "/api/permission-changes", backend);
        Assert.True(reachedHead);
    }

    [Fact]
    public void 搬移完成後重啟_不再標記為待搬移()
    {
        var backend = SeedLegacyDatabase();
        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        var restarted = NewBackend();
        Assert.Equal(PermissionChangeMigrationState.Completed, restarted.PermissionChangeMigrator.State.State);
        Assert.False(restarted.PermissionChangeMigrator.State.ShouldBlockWrites);
    }

    [Fact]
    public void 搬移後_舊log與舊blob保留未刪()
    {
        var backend = SeedLegacyDatabase();
        backend.PermissionChangeMigrator.Run(CancellationToken.None);

        Assert.Equal(3, backend.LogStore("perm_changes").ReadLines().Count);
        Assert.False(string.IsNullOrWhiteSpace(backend.Blob("perm_confirms").Read()));
    }

    private static async Task<(bool Reached, DefaultHttpContext Context)> InvokeGate(
        string method, string path, StorageBackend backend)
    {
        var reached = false;
        var middleware = new MigrationGateMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, backend);
        return (reached, context);
    }
}
