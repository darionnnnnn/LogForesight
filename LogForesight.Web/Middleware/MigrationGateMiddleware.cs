using LogForesight.Web.Models;

namespace LogForesight.Web.Middleware;

/// <summary>
/// 資料搬移期間擋下**寫入**（docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三-d）。
/// 目前管兩件事：處理狀態搬移（/api/handling、/api/records）與權限異動搬移
/// （/api/permission-changes），兩者各看各的遷移狀態。
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
    private static readonly string[] HandlingGuardedPrefixes =
    {
        "/api/handling",        // 跨主機批次指派／統一標記／回覆狀態
        "/api/records"          // 風險日詳情的日層級與問題層級標記（.../handling/...）
    };

    private const string PermissionChangeGuardedPrefix = "/api/permission-changes";

    private readonly RequestDelegate _next;

    public MigrationGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, StorageBackend backend)
    {
        if (!IsReadOnlyMethod(context.Request.Method))
        {
            if (context.Request.Path.StartsWithSegments(PermissionChangeGuardedPrefix))
            {
                var permState = backend.PermissionChangeMigrator.State;
                if (permState.ShouldBlockWrites)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(
                        ApiResponse<object>.Fail(ApiErrorCodes.Conflict, DescribePermissionChangeState(permState)));
                    return;
                }
            }
            else if (HandlingGuardedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
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
        }

        await _next(context);
    }

    /// <summary>非 GET／HEAD／OPTIONS 請求才需要檢查遷移閘門——查詢一律放行</summary>
    private static bool IsReadOnlyMethod(string method) =>
        HttpMethods.IsGet(method) ||
        HttpMethods.IsHead(method) ||
        HttpMethods.IsOptions(method);

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

    private static string DescribePermissionChangeState(PermissionChangeMigrationState state)
    {
        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            return "權限異動的資料搬移失敗，目前為唯讀模式，請聯繫系統管理員。" +
                   $"（原因：{state.LastError}）";
        }

        return "權限異動的資料正在搬移中，此期間無法確認權限異動，請稍候再試。" +
               "查詢與檢視不受影響。";
    }
}
