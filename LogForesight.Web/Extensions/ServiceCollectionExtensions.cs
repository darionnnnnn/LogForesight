using System.Text;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Import;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace LogForesight.Web.Extensions;

/// <summary>
/// DI 註冊（docs/WEB-SPEC.md §4.3）。抽成擴充方法讓 Program.cs 保持薄——
/// Program.cs 應該一眼看得出「這個應用由哪幾塊組成」，而不是三百行註冊碼。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Core 的儲存後端。依 Storage.Type 建立，Web 的其他部分對後端無感知</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services, WebAppSettings settings)
    {
        var storage = settings.Storage;
        var dataRoot = storage.ResolveDataRoot();

        // Singleton：JSONL 實作內部自行處理檔案鎖，共用一個實例才能保證行程內的寫入互斥
        services.AddSingleton<IUserStore>(_ => StorageFactory.CreateUserStore(storage, dataRoot));
        services.AddSingleton<IUserGroupStore>(_ => StorageFactory.CreateUserGroupStore(storage, dataRoot));
        services.AddSingleton<IHostStore>(_ => StorageFactory.CreateHostStore(storage, dataRoot));
        services.AddSingleton<IHostGroupStore>(_ => StorageFactory.CreateHostGroupStore(storage, dataRoot));
        services.AddSingleton<IGroupAccessStore>(_ => StorageFactory.CreateGroupAccessStore(storage, dataRoot));
        services.AddSingleton<IAuditLogStore>(_ => StorageFactory.CreateAuditLogStore(storage, dataRoot));
        services.AddSingleton<IImportLogStore>(_ => StorageFactory.CreateImportLogStore(storage, dataRoot));

        // 分析紀錄與報告全文：批次寫、Web 讀
        services.AddSingleton<IAnalysisRecordQuery>(_ => StorageFactory.CreateRecordQuery(storage, dataRoot));
        services.AddSingleton<IReportReader>(_ => StorageFactory.CreateReportReader(storage, dataRoot));

        // 寫入面：處理狀態（Web 寫）、權限異動（批次寫異動、Web 寫確認）
        services.AddSingleton<IRecordHandlingStore>(_ => StorageFactory.CreateHandlingStore(storage, dataRoot));
        services.AddSingleton<IIssueHandlingStore>(_ => StorageFactory.CreateIssueHandlingStore(storage, dataRoot));
        services.AddSingleton<IPermissionChangeStore>(_ => StorageFactory.CreatePermissionChangeStore(storage, dataRoot));

        // 規則維護與執行監控
        services.AddSingleton<IKnownIssueRuleStore>(_ =>
            StorageFactory.CreateRuleStore(storage, Path.Combine(dataRoot, "rules.json")));
        services.AddSingleton<IRuleSeedStore>(_ => StorageFactory.CreateRuleSeedStore(storage, dataRoot));
        services.AddSingleton<ISuppressionStore>(_ =>
            StorageFactory.CreateSuppressionStore(storage, Path.Combine(dataRoot, "suppressions.json")));
        services.AddSingleton<IBatchRunStore>(_ => StorageFactory.CreateBatchRunStore(storage, dataRoot));

        return services;
    }

    /// <summary>驗證與授權：JWT（存於 HttpOnly Cookie）＋ 能力解析</summary>
    public static IServiceCollection AddLogForesightAuth(this IServiceCollection services, WebAppSettings settings)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<ServerAdminAuthenticator>();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // 驗證方式可抽換（開放封閉）：換 Provider 不影響登入流程的其餘部分
        services.AddSingleton<IAuthenticationProvider>(sp => settings.Auth.Provider.ToLowerInvariant() switch
        {
            "ldap" => new LdapAuthenticationProvider(settings),
            "stub" => new StubAuthenticationProvider(),
            _ => throw new InvalidOperationException(
                $"未知的 Auth:Provider「{settings.Auth.Provider}」，可用值為 Stub 或 Ldap。")
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // 關閉 claim 名稱映射：預設行為會把 unique_name 之類的短名稱改寫成
                // WS-Federation 的長 URI，造成「寫進去的 claim 名字」與「讀出來的名字」不同。
                // 關掉之後 JwtTokenService 寫什麼名字就讀什麼名字，不需要記憶對照關係。
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = JwtTokenService.DisplayNameClaim,
                    RoleClaimType = JwtTokenService.CapabilityClaim,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = settings.Jwt.Issuer,
                    ValidAudience = settings.Jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Jwt.SecretKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };

                options.Events = new JwtBearerEvents
                {
                    // token 放在 HttpOnly Cookie 而不是 Authorization 標頭：
                    // 前端 JS 讀不到 token，XSS 就偷不走它（§6.1）
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Cookies.TryGetValue(settings.Jwt.CookieName, out var token))
                            context.Token = token;
                        return Task.CompletedTask;
                    },

                    // 未驗證時 API 與頁面要有不同待遇：
                    // API 回 401 讓前端攔截後導頁；頁面直接 302，使用者不會先看到空殼再被踢出去
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();

                        if (context.Request.Path.StartsWithSegments("/api"))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(
                                ApiErrorCodes.AuthExpired, "登入已逾期，請重新登入。"));
                        }
                        else
                        {
                            var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
                            context.Response.Redirect($"/login?returnUrl={returnUrl}");
                        }
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            // **預設全部端點都要求已登入**，只有明確標註 [AllowAnonymous] 的例外（登入頁、登入 API）。
            // 反過來做（預設匿名、需要保護的才標 [Authorize]）的話，任何人新增 Controller 時
            // 忘了標註就是一個對外開放的端點——而且不會有任何錯誤提示，測試也照樣通過。
            // 安全的預設值要讓「漏掉」變成拒絕存取，不是變成公開。
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    /// <summary>
    /// 讓 DTO 的 DataAnnotations 驗證失敗也回統一信封（docs/WEB-SPEC.md §7.2）。
    ///
    /// [ApiController] 預設會把模型驗證失敗轉成 RFC 9110 的 ProblemDetails，
    /// 那個形狀與本專案的信封不同——前端 api.js 解析不到 error.message，
    /// 使用者看到的會是通用的「系統發生未預期的錯誤」，而不是
    /// 「請輸入來源比對字串」這種真正有用的欄位訊息。
    /// </summary>
    public static IServiceCollection AddEnvelopeModelValidation(this IServiceCollection services)
    {
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var messages = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .SelectMany(entry => entry.Value!.Errors.Select(error => error.ErrorMessage))
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct()
                    .ToList();

                var message = messages.Count > 0
                    ? string.Join("；", messages)
                    : "輸入內容不合格，請確認必填欄位與格式。";

                return new BadRequestObjectResult(
                    ApiResponse<object>.Fail(ApiErrorCodes.ValidationFailed, message));
            };
        });

        return services;
    }

    /// <summary>業務層與資料存取層</summary>
    public static IServiceCollection AddLogForesightServices(this IServiceCollection services)
    {
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IVisibilityService, VisibilityService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IHostAdminService, HostAdminService>();
        services.AddScoped<INetiqHostService, NetiqHostService>();
        services.AddScoped<IGroupAdminService, GroupAdminService>();

        // Sentinel 名單唯讀取自批次 appsettings.json；Singleton＋依檔案時間快取，
        // 不必每次請求都重讀解析（改設定也不需要重啟 Web）
        services.AddSingleton<INetiqServerCatalog, NetiqServerCatalog>();

        // 查詢面：Repository 負責主機識別展開與可見範圍強制套用
        services.AddScoped<IRecordRepository, RecordRepository>();
        services.AddScoped<IRecordQueryService, RecordQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReportService, ReportService>();

        // 寫入面
        services.AddScoped<IHandlingService, HandlingService>();
        services.AddScoped<IPermissionChangeService, PermissionChangeService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();

        // 規則維護與執行監控
        services.AddScoped<IRuleAdminService, RuleAdminService>();
        services.AddScoped<IRunMonitorService, RunMonitorService>();

        // CSV 匯入：每種類型一個 ICsvImporter 實作，ImportService 依 Kind 解析。
        // 新增第四種匯入時只要多註冊一個實作，流程與 Controller 都不必改（OCP）
        services.AddScoped<ICsvImporter, UserCsvImporter>();
        services.AddScoped<ICsvImporter, HostCsvImporter>();
        services.AddScoped<ICsvImporter, GroupAccessCsvImporter>();
        services.AddScoped<ICsvImporter, OwnerCsvImporter>();
        services.AddScoped<IImportService, ImportService>();

        return services;
    }
}
