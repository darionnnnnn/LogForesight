using LogForesight.Web.Models;

namespace LogForesight.Web.Middleware;

/// <summary>
/// 處理狀態搬移期間擋下**寫入**（docs/SCALE-FIX-PLAN-2026-08-06.md §三-d）。
///
/// **為什麼需要**：遷移改成背景執行之後，站台在搬移進行中就已經可以服務請求。
/// 若此時使用者標記了一個問題，那筆資料會寫進**還沒搬完**的表——
/// 而遷移的「整份 AddRange」隨後會撞上唯一索引，或更糟：兩份資料並存且互不相通。
///
/// **為什麼只擋寫、不擋讀**：升級後第一件事通常是有人登入看畫面。整站 503 會讓人
/// 以為升級失敗；只擋處理狀態的寫入，讀到的內容仍然正確（只是還不完整），
/// 而唯一會產生「新舊兩份資料」的路徑被關死。
///
/// **註冊位置**：必須在 <c>UseAuthorization</c> 之後（才知道是不是 API 請求、
/// 也才不會擋到登入），在 <c>CsrfHeaderMiddleware</c> 之前（先回明確的 503，
/// 不要讓使用者收到看似「請求來源驗證失敗」的誤導訊息）。
/// </summary>
public class MigrationGateMiddleware
{
    /// <summary>受保護的路徑前綴：處理狀態相關的寫入入口</summary>
    private static readonly string[] GuardedPrefixes =
    {
        "/api/handling",        // 跨主機批次指派／統一標記／回覆狀態
        "/api/records"          // 風險日詳情的日層級與問題層級標記（.../handling/...）
    };

    private readonly RequestDelegate _next;

    public MigrationGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, StorageBackend backend)
    {
        if (RequiresGate(context))
        {
            var state = backend.HandlingMigrator.State;
            if (state.ShouldBlockWrites)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    ApiResponse<object>.Fail(ApiErrorCodes.Conflict, DescribeState(state)));
                return;
            }
        }

        await _next(context);
    }

    /// <summary>只擋處理狀態相關路徑的非 GET 請求——查詢一律放行</summary>
    private static bool RequiresGate(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            return false;
        }

        return GuardedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix));
    }

    /// <summary>
    /// 訊息要說得出「現在是什麼狀況、要不要等」——只回「服務暫時無法使用」的話，
    /// 使用者會以為升級壞了而去重啟服務，那正是最不該做的事（會中斷搬移）。
    /// </summary>
    private static string DescribeState(HandlingMigrationState state)
    {
        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            return "處理狀態的資料搬移失敗，目前為唯讀模式，請聯繫系統管理員。" +
                   $"（原因：{state.LastError}）";
        }

        var done = new[] { state.IssueHandlingDone, state.IssueCasesDone, state.RecordHandlingDone }.Count(x => x);
        return $"處理狀態的資料正在搬移中（已完成 {done}/3），此期間無法變更處理狀態，請稍候再試。" +
               "查詢與檢視不受影響。";
    }
}
