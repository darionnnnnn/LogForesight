using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="MigrationGateMiddleware"/> 的「處理狀態搬移」分支（權限異動分支由 PermissionChangeMigrationTests 負責）：
/// 搬移未完成時，處理狀態保護前綴下的寫入回 503 與 service_unavailable；讀取與非保護路徑照常放行；
/// 搬移完成後不再擋。狀態用真的 StorageBackend 造：有舊 blob 的資料庫＝Pending，全新資料庫＝Completed。
/// </summary>
public sealed class MigrationGateMiddlewareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-gate-" + Guid.NewGuid().ToString("N"));

    public MigrationGateMiddlewareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private StorageBackend NewBackend(string name) => new(
        new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, name + ".db")}" }, _dir);

    /// <summary>造一個處理狀態「搬移中（Pending）」的後端：先放舊格式 blob，再清掉遷移標記後重建後端。</summary>
    private StorageBackend NewMigratingBackend()
    {
        var seeding = NewBackend("migrating");
        seeding.Blob("issue_handling").Mutate<object?>(_ => (JsonSerializer.Serialize(new List<IssueHandling>
        {
            new() { HostName = "SRV-01", Date = DateTime.Today, IssueKey = "k1", Status = "resolved", ActorId = 1, ActorAccount = "admin", UpdatedAt = DateTime.Now }
        }, LfJsonOptions.Pretty), null));
        seeding.Blob("handling_migration").Mutate<object?>(_ => (string.Empty, null));
        var backend = NewBackend("migrating");
        Assert.True(backend.HandlingMigrator.State.ShouldBlockWrites, "前置條件：處理狀態搬移應處於未完成");
        return backend;
    }

    private static async Task<(bool Reached, DefaultHttpContext Context)> InvokeGate(string method, string path, StorageBackend backend)
    {
        var reached = false;
        var middleware = new MigrationGateMiddleware(_ => { reached = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context, backend);
        return (reached, context);
    }

    [Theory]
    [InlineData("/api/handling/batch-assign")]
    [InlineData("/api/records/2026-09-01/handling/k1")]
    public async Task 處理狀態搬移中_保護路徑的POST回503且錯誤碼為service_unavailable(string path)
    {
        var backend = NewMigratingBackend();

        var (reached, context) = await InvokeGate("POST", path, backend);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(context.Response.Body);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("service_unavailable", error.GetProperty("code").GetString());
        Assert.Equal(ApiErrorCodes.ServiceUnavailable, error.GetProperty("code").GetString());
        Assert.Contains("處理狀態", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task 處理狀態搬移中_同一保護路徑的讀取方法放行(string method)
    {
        var backend = NewMigratingBackend();

        var (reached, context) = await InvokeGate(method, "/api/handling/summary", backend);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/hosts")]
    [InlineData("/api/handlingx/other")]
    [InlineData("/api/auth/login")]
    public async Task 處理狀態搬移中_非保護路徑的POST放行(string path)
    {
        var backend = NewMigratingBackend();

        var (reached, context) = await InvokeGate("POST", path, backend);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task 處理狀態搬移完成後_保護路徑的POST放行()
    {
        var backend = NewBackend("fresh");
        Assert.False(backend.HandlingMigrator.State.ShouldBlockWrites, "前置條件：全新資料庫沒有舊資料，搬移狀態應為完成");

        var (reached, _) = await InvokeGate("POST", "/api/handling/batch-assign", backend);

        Assert.True(reached);
    }
}
