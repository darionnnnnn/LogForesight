using System.Text;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Import;
using LogForesight.Web.Services.Mail;
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

        // Singleton：全站共用同一個 StorageBackend（DbContext 工廠與 schema 確認只做一次）
        services.AddSingleton(_ => new StorageBackend(storage, dataRoot));

        services.AddSingleton<IUserStore>(sp => new UserStore(sp.GetRequiredService<StorageBackend>().Blob("users")));
        services.AddSingleton<IUserGroupStore>(sp => new UserGroupStore(sp.GetRequiredService<StorageBackend>().Blob("user_groups")));
        services.AddSingleton<IHostStore>(sp => new HostStore(sp.GetRequiredService<StorageBackend>().Blob("hosts")));
        services.AddSingleton<IHostGroupStore>(sp => new HostGroupStore(sp.GetRequiredService<StorageBackend>().Blob("host_groups")));
        services.AddSingleton<IGroupAccessStore>(sp => new GroupAccessStore(sp.GetRequiredService<StorageBackend>().Blob("group_access")));
        services.AddSingleton<AuditLogStore>(sp => new AuditLogStore(sp.GetRequiredService<StorageBackend>().LogStore("audit")));
        services.AddSingleton<IImportLogStore>(sp => new ImportLogStore(sp.GetRequiredService<StorageBackend>().LogStore("import_logs")));
        services.AddSingleton<ISentinelStore>(sp => new SentinelStore(sp.GetRequiredService<StorageBackend>().Blob("sentinels")));
        services.AddSingleton<IIssueOwnerStore>(sp => new IssueOwnerStore(sp.GetRequiredService<StorageBackend>().Blob("issue_owners")));
        services.AddSingleton<SetupWizardStateStore>(sp => new SetupWizardStateStore(sp.GetRequiredService<StorageBackend>().Blob("setup_wizard_state")));
        services.AddSingleton<ISystemSettingsStore>(sp => new SystemSettingsStore(sp.GetRequiredService<StorageBackend>().Blob("system_settings")));
        services.AddSingleton<NetiqOptionsStore>(sp => new NetiqOptionsStore(sp.GetRequiredService<StorageBackend>().Blob("netiq_options")));

        // 分析紀錄與報告全文：批次寫、Web 讀
        services.AddSingleton<IAnalysisRecordQuery>(sp => sp.GetRequiredService<StorageBackend>().RecordStore());
        services.AddSingleton<IReportReader>(_ => new FileReportReader(dataRoot));

        // 寫入面：處理狀態（Web 寫）、權限異動（批次寫異動、Web 寫確認）
        //
        // 這三個自 P3 起走**真表**而非整份 blob（docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 B）：
        // 它們會隨「主機數 × 天數」成長，6000 台 × 90 天下 issue_handling 的整份序列化
        // 會撞上 .NET 的 2 GB 單一物件上限。介面不變，呼叫端零修改。
        services.AddSingleton<IRecordHandlingStore>(sp => sp.GetRequiredService<StorageBackend>().RecordHandlingStore());
        services.AddSingleton<IIssueHandlingStore>(sp => sp.GetRequiredService<StorageBackend>().IssueHandlingStore());
        services.AddSingleton<IIssueCaseStore>(sp => sp.GetRequiredService<StorageBackend>().IssueCaseStore());

        // 問題聚合（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／根因 C）：一句 GROUP BY 取代
        // 「撈回整段期間的紀錄再於記憶體 GroupBy」
        services.AddSingleton<IIssueAggregateQuery>(sp =>
            sp.GetRequiredService<StorageBackend>().IssueAggregateQuery(sp.GetRequiredService<IHostStore>()));
        services.AddSingleton<TopIssueBackfiller>(sp => sp.GetRequiredService<StorageBackend>().TopIssueBackfiller());
        services.AddSingleton<INoiseMarkStore>(sp => new NoiseMarkStore(sp.GetRequiredService<StorageBackend>().Blob("noise_marks")));
        services.AddSingleton<AiCacheStore>(sp => new AiCacheStore(sp.GetRequiredService<StorageBackend>().Blob("ai_cache")));
        services.AddSingleton<PermissionChangeStore>(sp =>
        {
            var backend = sp.GetRequiredService<StorageBackend>();
            return new PermissionChangeStore(backend.LogStore("perm_changes"), backend.Blob("perm_confirms"));
        });

        // 規則維護與執行監控
        services.AddSingleton<IKnownIssueRuleStore>(sp => new KnownIssueRuleStore(sp.GetRequiredService<StorageBackend>().Blob("rules")));
        services.AddSingleton<IRuleSeedStore>(sp => new RuleSeedStore(sp.GetRequiredService<StorageBackend>().Blob("rule_seeds")));
        services.AddSingleton<ISuppressionStore>(sp => new SuppressionStore(sp.GetRequiredService<StorageBackend>().Blob("suppressions")));
        services.AddSingleton<BatchRunStore>(sp =>
        {
            var backend = sp.GetRequiredService<StorageBackend>();
            return new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
        });

        // 排程設定（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）
        services.AddSingleton<ScheduleOptionsStore>(sp => new ScheduleOptionsStore(sp.GetRequiredService<StorageBackend>().Blob("schedule_options")));

        // 郵件通知寄送狀態（回饋十五輪批次D）：每日/每週摘要的「上次寄送日」＋緊急通知的去重鍵
        services.AddSingleton<MailNotifyStateStore>(sp => new MailNotifyStateStore(sp.GetRequiredService<StorageBackend>().Blob("mail_notify_state")));

        // 風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）：批次寫、Web（AI 對話）讀
        services.AddSingleton<IRiskyEventStore>(sp => sp.GetRequiredService<StorageBackend>().RiskyEventStore());

        return services;
    }

    /// <summary>驗證與授權：JWT（存於 HttpOnly Cookie）＋ 能力解析</summary>
    public static IServiceCollection AddLogForesightAuth(this IServiceCollection services, WebAppSettings settings)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<ServerAdminAuthenticator>();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // 驗證方式可抽換（開放封閉）：換 Provider 不影響登入流程的其餘部分。
        // DynamicAuthenticationProvider（docs/archive/HISTORY.md #9）包一層：DB 設定的
        // AdAuthEnabled 開啟時走設定頁的 AD 設定，否則落到下面的 fallback。
        // §12：AD 驗證的唯一事實來源是設定頁——appsettings 的 Auth:Ldap:Domain 與
        // LdapAuthenticationProvider 已退役，"Ad"（含舊值 "Ldap"）的 fallback 改為
        // UnconfiguredAdAuthenticationProvider（明講「AD 尚未設定，請以 serverAdmin 登入後設定」）。
        services.AddSingleton<IAuthenticationProvider>(sp =>
        {
            IAuthenticationProvider fallback = settings.Auth.Provider.ToLowerInvariant() switch
            {
                "ad" or "ldap" => new UnconfiguredAdAuthenticationProvider(),
                "stub" => new StubAuthenticationProvider(),
                _ => throw new InvalidOperationException(
                    $"未知的 Auth:Provider「{settings.Auth.Provider}」，可用值為 Ad 或 Stub。")
            };

            return new DynamicAuthenticationProvider(sp.GetRequiredService<ISystemSettingsStore>(), fallback);
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
        // 能力解析的單一事實來源（體檢 H1／H3）：登入、使用者詳細頁、指派前檢查共用同一份規則
        services.AddScoped<UserCapabilityResolver>();
        services.AddScoped<IdentityService>();
        services.AddScoped<IVisibilityService, VisibilityService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<HostAdminService>();
        services.AddScoped<INetiqHostService, NetiqHostService>();
        services.AddScoped<GroupAdminService>();
        services.AddScoped<IssueOwnerAdminService>();
        services.AddScoped<SetupReadinessService>();

        // Sentinel 名單改由 Web 維護（docs/archive/HISTORY.md 定案 1），讀寫都經 ISentinelStore
        services.AddSingleton<INetiqServerCatalog, NetiqServerCatalog>();
        services.AddScoped<SentinelAdminService>();

        // 全站系統設定（未處理等級門檻／AI 位址／補充留存天數）
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddScoped<NetiqOptionsService>();

        // 版面的品牌三元素（docs/archive/FEEDBACK-10-PLAN.md §1）：主版面與登入頁伺服器端渲染時注入，
        // View 只看得到品牌欄位，不必碰整份設定或 Persistence 層
        services.AddScoped<IBrandProvider, BrandProvider>();

        // NetIQ 主動探索（§13，回饋第九輪）：**預設真連線**（SentinelRestDirectoryClient，走
        // SentinelClient 的網段範圍掃描，docs/NETIQ-API-REFERENCE.md §3.4）。離線示範資料改由
        // 「NetIQ 維護」頁的 NetiqOptions.UseOfflineDemoData 開關控制，且僅非 Production 生效——
        // 執行期解析（讀 DB 開關＋環境），不再由啟動時的 appsettings 決定。
        services.AddScoped<INetiqDirectoryClient>(sp =>
            UseStubNetiqClient(
                sp.GetRequiredService<IWebHostEnvironment>().IsProduction(),
                sp.GetRequiredService<NetiqOptionsStore>().Get().UseOfflineDemoData)
                ? new StubNetiqDirectoryClient()
                : new SentinelRestDirectoryClient(sp.GetRequiredService<NetiqOptionsStore>()));
        services.AddScoped<NetiqDiscoveryService>();

        // AI 加值層（純加值、失敗靜默降級）。WebAiService 讀批次 appsettings 的 AI 位址、
        // 建互動情境的短逾時客戶端；Singleton 讓快取與 HttpClient 共用一份
        services.AddSingleton<IWebAiService, WebAiService>();
        services.AddScoped<AiInsightService>();

        // 操作說明書（回饋十五輪批次E）：內容編譯進組件、執行期間不變，Singleton＋Lazy 延後載入
        services.AddSingleton<HelpContentService>();
        services.AddScoped<HelpQaService>();

        // 詢問 AI 現場取數（docs/archive/FEEDBACK-4-PLAN.md §5）：Singleton——併發旗標與 10 分鐘快取
        // 要全站共用同一份，不能隨請求範圍各自持有
        services.AddSingleton<ISentinelEventFetcher, SentinelEventFetchService>();

        // 風險 log 暫存優先、Sentinel 即時查詢 fallback
        services.AddScoped<RiskyEventLookupService>();

        // 查詢面：Repository 負責主機識別展開與可見範圍強制套用
        services.AddScoped<IRecordRepository, RecordRepository>();
        services.AddScoped<RecordListQueryService>();
        services.AddScoped<RecordDetailQueryService>();
        // 問題排行的共用投影（P4）：儀表板與報表共用，兩頁數字必然一致
        services.AddScoped<IssueRankingBuilder>();
        // 批次載入處理狀態＋逐筆判定的共用骨架（回饋十九輪批次D）：
        // IssueHandlingRollupQuery／IssueTodoQuery 共用，避免各自重寫一份樣板碼。
        // OccurrenceStatusResolver 註冊為 Singleton（回饋十九輪批次H）——它自己的四個相依
        // （IHostStore／IIssueHandlingStore／IIssueCaseStore／ISystemSettingsStore）本來就全是
        // Singleton，往下調整生命週期沒有 captive dependency 疑慮；這樣批次H 的
        // MailIssueDigest（Singleton，見下方郵件通知區塊）才能直接注入它，重用同一套
        // 「批次撈 handling/case → 逐筆判定」邏輯，不必為 Singleton 情境另外複製一份判定規則。
        // 既有的 Scoped 呼叫端（DashboardService／ReportService 等）依賴 Singleton 服務永遠合法，
        // 不受影響。
        services.AddSingleton<OccurrenceStatusResolver>();
        services.AddScoped<IssueHandlingRollupQuery>();
        services.AddScoped<IssueTodoQuery>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();

        // 健康檢查（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）：Singleton——它只讀 StorageBackend
        // 與 SchedulerRunState 兩個既有的行程內單例，沒有請求範圍狀態
        services.AddSingleton<HealthService>();

        // 寫入面：IssueCaseCoordinator 依賴的四個 store 全是 Singleton（docs/archive/FEEDBACK-4-PLAN.md §0），
        // 本身也可以是 Singleton——沒有請求範圍狀態
        services.AddSingleton<IssueCaseCoordinator>();

        // 處理狀態（原 HandlingService，依關注點拆為日層級／問題層級／查詢三個服務，
        // 共用 HandlingProgressCalculator 推導進度）
        services.AddScoped<HandlingProgressCalculator>();
        services.AddScoped<DayHandlingCommandService>();
        services.AddScoped<IssueHandlingCommandService>();
        services.AddScoped<HandlingHistoryQueryService>();

        services.AddScoped<PermissionChangeService>();
        services.AddScoped<AuditQueryService>();

        // 規則維護與執行監控
        services.AddScoped<RuleAdminService>();
        services.AddScoped<RunMonitorService>();

        // 郵件通知（回饋十五輪批次D）：Singleton——SchedulerHostedService 是 Singleton 背景服務，
        // 直接建構子注入而非每次觸發解析 Scope，MailNotificationService 的相依（Store 們）
        // 本來就全是 Singleton，沒有 captive dependency 疑慮。
        services.AddSingleton<ISmtpMailSender, SystemNetSmtpMailSender>();
        // 郵件問題優先摘要（回饋十九輪批次H1）：同樣 Singleton-safe，相依的 IIssueAggregateQuery／
        // OccurrenceStatusResolver 皆已是 Singleton
        services.AddSingleton<MailIssueDigest>();
        services.AddSingleton<MailNotificationService>();

        // 排程引擎（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）：SchedulerRunState 是行程內單例狀態
        // （執行中/觸發來源/最新進度，供狀態與停止 API 讀取）；SchedulerHostedService 本身也註冊
        // 為單例並讓 IHostedService 直接引用同一個實例——ScheduleController 需要呼叫它的
        // TriggerRunAsync（手動觸發），不能只當背景服務、必須也能被其他地方解析取得。
        // AnalysisOrchestrator／NamedMutexGate 皆無狀態依賴，改由 DI 注入而非各自 new——
        // 讓 SchedulerHostedService 的執行路徑可用測試替身注入驗證。
        services.AddSingleton<AnalysisOrchestrator>();
        services.AddSingleton<NamedMutexGate>();
        services.AddSingleton<SchedulerRunState>();
        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());

        // 處理狀態自 blob 搬進真表（docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三）：
        // 同樣不能掛在啟動路徑上，且搬完之前由 MigrationGateMiddleware 擋住寫入。
        // **註冊在回填之前**——遷移未完成時處理狀態是唯讀的，要優先解除
        services.AddHostedService<HandlingMigrationHostedService>();

        // lf_top_issues 聚合欄的背景回填（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4）：
        // 掛在啟動路徑上會讓 Windows 服務啟動逾時（§8.2 E3），所以走背景服務
        services.AddHostedService<TopIssueBackfillHostedService>();

        // lf_daily_records 抽出欄的背景回填（回饋十九輪批次B），骨架與時機同上
        services.AddSingleton<DailyRecordBackfiller>(sp => sp.GetRequiredService<StorageBackend>().DailyRecordBackfiller());
        services.AddHostedService<DailyRecordBackfillHostedService>();

        // NetIQ API 診斷（probe，docs/archive/WEB-SCHEDULER-PLAN.md §1.4.11）：狀態單例本身就是
        // 併發 1 的 gate，刻意與上面的 SchedulerRunState 分開——不與排程/手動分析共用
        services.AddSingleton<NetiqProbeRunState>();
        services.AddSingleton<NetiqProbeService>();

        // CSV 匯入：每種類型一個 ICsvImporter 實作，ImportService 依 Kind 解析。
        // **§2a（回饋第十一輪）起只剩負責人一種**——使用者／主機／群組授權三種已退役
        // （主機主要來源是 NetIQ 掃描匯入，其餘動線見 docs/archive/FEEDBACK-11-PLAN.md §2a 對照表）。
        // 未註冊的 Kind 由 ImportService.Resolve 回「不支援的匯入類型」，舊網址打進來不會 500。
        services.AddScoped<ICsvImporter, OwnerCsvImporter>();
        services.AddScoped<ImportService>();

        return services;
    }

    /// <summary>
    /// NetIQ 主動探索要不要用 Stub（§13）：僅「非 Production 且開關開啟」才用離線示範資料，
    /// Production 一律真連線（假資料不得上正式）。抽成純函數方便單獨測試邊界，不必起完整 DI 容器。
    /// </summary>
    internal static bool UseStubNetiqClient(bool isProduction, bool useOfflineDemoData) =>
        !isProduction && useOfflineDemoData;
}
