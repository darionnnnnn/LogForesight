using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪 B7：PRTG 診斷匯出把「逾時」與「使用者取消」分開處理。
/// 逾時是匯出自己的期限到了（請求並未中止）：沒開始串流回 503；使用者斷線才是 499。
/// </summary>
public sealed class PrtgExportCancellationSemanticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-export-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgExportCancellationSemanticsTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")};Pooling=False"
        }, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class UnusedCapacity : IPrtgTransferCapacityProvider
    {
        public PrtgTransferCapacitySnapshot Capture() => throw new NotSupportedException("期限／取消在取得容量之前就會結束");
    }

    private static HttpContext NewContext(CancellationToken requestAborted = default)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.RequestAborted = requestAborted;
        http.Response.Body = new MemoryStream();
        return http;
    }

    /// <summary>
    /// 匯出一開始會呼叫 captureBinding：測試在這裡睡過期限再擲 OperationCanceledException，
    /// 模擬「期限到後，某個操作以取消例外結束，但請求本身沒有被中止」。
    /// </summary>
    private Task Run(HttpContext http, TimeSpan? deadline) =>
        new PrtgDiagnosticExportActionResult(_backend, new UnusedCapacity(),
            () =>
            {
                if (deadline is { } limit) Thread.Sleep(limit + TimeSpan.FromSeconds(3));   // 期限計時器在執行緒集區上觸發，全量並行時可能延遲，所以留足餘裕
                throw new OperationCanceledException();
            },
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 3), new RecordingAuditService(), deadline)
            .ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));

    [Fact]
    public async Task 匯出期限到但請求未被中止_回503而不是當成使用者取消()
    {
        var http = NewContext();   // RequestAborted 沒有取消

        await Run(http, deadline: TimeSpan.FromMilliseconds(30));

        Assert.False(http.RequestAborted.IsCancellationRequested);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
    }

    [Fact]
    public async Task 使用者中止請求_回499()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var http = NewContext(aborted.Token);

        await Run(http, deadline: null);

        Assert.Equal(499, http.Response.StatusCode);
    }
}
