using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 樂觀鎖衝突的端到端配線（docs/archive/SCALE-FIX-PLAN-2026-08-06.md D3）。
///
/// 三張處理狀態表以 <c>UpdatedAt</c> 當並發權杖，衝突**偵測得到**
/// （<c>DbUpdateConcurrencyException</c>），但在 D3 之前沒有人攔——
/// <c>ApiExceptionFilter</c> 只認 <c>DomainException</c>，其餘一律 500＋通用訊息。
/// 多人同時操作是正常情境不是故障，使用者該看到的是「被誰搶先了、重新整理再試」。
///
/// 這組測試釘住配線的兩端：
///   1. 權杖真的會攔下「讀取之後被別人改過」的寫入（EF 層）；
///   2. <see cref="ConcurrentUpdateException"/> 經 <see cref="ApiExceptionFilter"/>
///      對應成 409＋可讀訊息（Web 層）。
/// 中間的轉換（store 的 try/catch）兩端各自驗證後只剩四行純轉發。
/// </summary>
public class ConcurrentUpdateTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// 規劃 D3 的驗收情境：兩個 DbContext 讀同一列、各自修改、依序 SaveChanges——
    /// 第二個必須撞 DbUpdateConcurrencyException，而不是靜默地後寫蓋前寫（更新遺失）。
    /// </summary>
    [Fact]
    public void 並發權杖_後寫的一方撞衝突而不是蓋掉先寫的()
    {
        var day = DateTime.Today;
        using (var seed = _fixture.NewContext())
        {
            seed.RecordHandlings.Add(new RecordHandlingRow
            {
                HostName = "SRV-A", HostNameKey = "SRV-A", RecordDate = day,
                Status = HandlingStatuses.Open, UpdatedAt = DateTime.Now.AddMinutes(-5)
            });
            seed.SaveChanges();
        }

        using var ctx1 = _fixture.NewContext();
        using var ctx2 = _fixture.NewContext();
        var row1 = ctx1.RecordHandlings.Single(h => h.HostNameKey == "SRV-A");
        var row2 = ctx2.RecordHandlings.Single(h => h.HostNameKey == "SRV-A");

        row1.Status = HandlingStatuses.InProgress;
        row1.UpdatedAt = DateTime.Now;
        ctx1.SaveChanges();   // 先寫的成功

        row2.Status = HandlingStatuses.Resolved;
        row2.UpdatedAt = DateTime.Now.AddSeconds(1);
        Assert.Throws<DbUpdateConcurrencyException>(() => ctx2.SaveChanges());
    }

    [Fact]
    public void 衝突例外_經ApiExceptionFilter對應成409與可讀訊息()
    {
        var exception = new ConcurrentUpdateException(
            "SRV-A 2026-08-05 的處理狀態", new Exception("inner"));

        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>())
        { Exception = exception };

        new ApiExceptionFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);

        var envelope = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.Equal(ApiErrorCodes.Conflict, envelope.Error!.Code);
        // 訊息要指得出「哪一筆」並告訴使用者怎麼辦——這是 409 與通用 500 的全部差別
        Assert.Contains("SRV-A 2026-08-05", envelope.Error.Message);
        Assert.Contains("重新整理", envelope.Error.Message);
        Assert.True(context.ExceptionHandled);
    }
}
