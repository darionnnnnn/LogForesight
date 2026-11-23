using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 遷移失敗訊息的出口整合斷言：三個遷移器失敗時存進狀態的 LastError、以及 MigrationGateMiddleware
/// 放進回應本文的「原因」，都不得帶出例外原文（資料表名、路徑、SQL）。
/// 純函式 <see cref="MigrationFailureText.For"/> 另由 ErrorOutputBoundaryTests 覆蓋；
/// 這裡守的是「三個呼叫端真的都經過它」——有人把任一處改回 <c>ex.Message</c>，對應案例就紅。
/// </summary>
public sealed class MigrationFailureBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-migfail-" + Guid.NewGuid().ToString("N"));
    private const string Marker = "MARKER-table=lf_x";

    public MigrationFailureBoundaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private StorageBackend NewBackend(string name = "m") => new(
        new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, name + ".db")}" }, _dir);

    private static void AssertSanitized(string? lastError, string rawMessage)
    {
        Assert.False(string.IsNullOrWhiteSpace(lastError));
        Assert.DoesNotContain("MARKER", lastError);
        Assert.DoesNotContain(rawMessage, lastError);
        Assert.Contains(nameof(InvalidOperationException), lastError);
    }

    /// <summary>損毀的處理狀態 blob：遷移器自擲含資料表名與「資料未被修改」說明的 InvalidOperationException。</summary>
    private StorageBackend NewCorruptHandlingBackend(string name)
    {
        var seeding = NewBackend(name);
        seeding.Blob("issue_handling").Mutate<object?>(_ => ("{ 這不是合法的 JSON", null));
        seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));
        return NewBackend(name);
    }

    [Fact]
    public void 處理狀態遷移失敗_LastError不含例外原文()
    {
        var backend = NewCorruptHandlingBackend("handling");
        var ex = Assert.Throws<InvalidOperationException>(() => backend.HandlingMigrator.Run(CancellationToken.None));

        Assert.Contains("issue_handling", ex.Message);   // 前置：原文確實帶資料表名，下面才有意義
        AssertSanitized(backend.HandlingMigrator.State.LastError, ex.Message);
        Assert.DoesNotContain("issue_handling", backend.HandlingMigrator.State.LastError!);
    }

    [Fact]
    public void 權限異動遷移失敗_LastError不含例外原文()
    {
        var seeding = NewBackend("perm");
        seeding.LogStore("perm_changes").AppendLine("{ 這不是合法的 JSON 內容");
        seeding.Blob(PermissionChangeMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));
        var backend = NewBackend("perm");

        var ex = Assert.Throws<InvalidOperationException>(() => backend.PermissionChangeMigrator.Run(CancellationToken.None));

        Assert.Contains("perm_changes", ex.Message);
        AssertSanitized(backend.PermissionChangeMigrator.State.LastError, ex.Message);
        Assert.DoesNotContain("perm_changes", backend.PermissionChangeMigrator.State.LastError!);
    }

    [Fact]
    public void 報告檔遷移失敗_LastError不含例外原文()
    {
        var backend = NewBackend("report");
        // 建後端時的判定看到沒有 export 目錄、已標成完成；清空還原成升級前的未知狀態才會真的跑
        backend.Blob(ReportFileMigrator.StateBlobKey).Mutate<object?>(_ => (string.Empty, null));
        var stateStore = new ReportMigrationStateStore(backend.Blob(ReportFileMigrator.StateBlobKey));
        var migrator = new ReportFileMigrator(
            () => throw new InvalidOperationException(Marker), stateStore, _dir);

        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Run(CancellationToken.None));

        Assert.Equal(Marker, ex.Message);
        AssertSanitized(migrator.State.LastError, Marker);
    }

    [Fact]
    public async Task 遷移閘門回應本文的原因不含例外原文()
    {
        var backend = NewCorruptHandlingBackend("gate");
        var ex = Assert.Throws<InvalidOperationException>(() => backend.HandlingMigrator.Run(CancellationToken.None));

        var middleware = new MigrationGateMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/handling/batch-assign";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context, backend);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(context.Response.Body);
        var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("原因：", message);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.DoesNotContain(ex.Message, message);
        Assert.DoesNotContain("issue_handling", message);
    }
}
