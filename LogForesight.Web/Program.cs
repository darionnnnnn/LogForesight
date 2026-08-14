using System.Text;
using LogForesight.Web;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Filters;
using LogForesight.Web.Middleware;
using LogForesight.Web.Services;
using NLog;
using NLog.Web;

// console 輸出一律 UTF-8（docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §6）：本專案的
// console 訊息全是繁體中文，而 Windows 的主控台預設代碼頁是系統地區設定（繁中為 950），
// 與讀取端（Rider 執行視窗、CI log 收集器）的預期編碼不一致時整片變亂碼。
// 在這裡釘死成 UTF-8，輸出端就不再取決於誰來啟動這個行程。
//
// 兩個刻意：
//   1. UTF8Encoding(false)＝不寫 BOM——寫了的話部分終端會把 BOM 顯示成開頭的怪字元；
//   2. 吞掉 IOException——以 Windows 服務身分執行時沒有附掛主控台，設定這個屬性會擲例外。
//      沒有主控台就沒有畫面可亂，這件事失敗不該讓服務起不來（診斷 log 走 nlog 檔案目標，
//      不受影響）。nlog.config 的 Console target 走 Console.Out，自然跟著這裡的設定，
//      不另外標 encoding——同一件事寫兩個地方，日後只改一處就會分歧。
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch (IOException)
{
    // 無主控台（Windows 服務）——維持預設，不影響任何功能
}

if (args.Length > 0 && args[0] == "--hash-password")
{
    return HashPasswordCommand.Run();
}

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // 以 Windows 服務執行（docs/archive/HISTORY.md P1-3）：只在真的被服務控制管理器啟動時
    // 才切換生命週期管理，一般用 `dotnet run`／console 啟動不受影響
    builder.Host.UseWindowsService();

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // ── 組態：全部集中在強型別的 WebAppSettings（§5）────────────────────────
    var settings = builder.Configuration.Get<WebAppSettings>() ?? new WebAppSettings();

    // §12（回饋第九輪）：AI 進階參數（含 ExtraRequestFields）已自 appsettings 移入 DB 設定頁，
    // 原本為了修 ConfigurationBinder 綁不出 Dictionary<string, JsonElement> 而存在的
    // AiExtraFieldsLoader workaround 隨之退場——DB 存的是 JSON 文字，由
    // RuntimeSettingsResolver 解析，沒有 binder 的型別問題。

    // DataRoot 未明確指定時，StorageSettings.ResolveDataRoot() 退回 AppContext.BaseDirectory
    // （本站台自己的輸出目錄）——console 批次專案已隨 Phase 5 退場（docs/archive/WEB-SCHEDULER-PLAN.md
    // §1.5），Web 排程／立即執行是現在唯一的分析執行途徑，資料本來就該落在 Web 自己的目錄下，
    // 不需要再另外推算「批次輸出目錄」。開發者若要讀別處的資料，在設定檔明確填 DataRoot 即可。
    settings.Validate(builder.Environment.IsProduction());
    builder.Services.AddSingleton(settings);

    // 資料根目錄健檢（誠實申報，「沒告警 ≠ 沒問題」的原則）：
    // DataRoot 存在（Validate 已檢查）但底下沒有該儲存後端的資料足跡，最常見的成因是舊部署
    // 升級時 Storage:DataRoot 還指著升級前批次獨立部署時的資料目錄（console 已隨 Phase 5 退場，
    // docs/archive/WEB-SCHEDULER-PLAN.md §1.5），或是換過機器/目錄但設定沒跟著改。
    // 那正是「規則維護頁報『載入規則失敗』、儀表板一片空白」的來源。
    // 只有「Sqlite 用預設連線」時才在 DataRoot 底下有檔案足跡可查（.db 落點）；
    // Sqlite 自訂 ConnectionString 與 SqlServer 的可用性由 StorageBackend 建構時連線 fail-fast 把關，這裡不重複檢查。
    // 刻意不 fail-fast：排程還沒首次執行過是合法狀態；但要顯性提示，而不是讓人對著空白畫面猜。
    var dataRoot = settings.Storage.ResolveDataRoot();
    var expectedDataFiles = settings.Storage.Type == "Sqlite" && string.IsNullOrWhiteSpace(settings.Storage.ConnectionString)
        ? new[] { "logforesight.db" }
        : Array.Empty<string>();   // SqlServer／自訂連線的 Sqlite：足跡不在 DataRoot，交給 DB 連線把關
    if (expectedDataFiles.Length > 0 &&
        !expectedDataFiles.Any(f => File.Exists(Path.Combine(dataRoot, f))))
    {
        var names = string.Join(" / ", expectedDataFiles);
        logger.Warn("資料根目錄 {0} 底下找不到 {1}。若排程/立即執行已跑過至少一次，" +
            "代表 Storage:DataRoot 指錯目錄，儀表板與問題查詢會因此空白" +
            "（規則維護頁不受影響——Web 啟動會自行初始化規則庫，見下方「啟動時的資料準備」）。", dataRoot, names);
        Console.Error.WriteLine($"⚠ 資料根目錄「{dataRoot}」底下找不到 {names}；" +
            "若排程/立即執行已跑過，請確認 Storage:DataRoot 指向正確的資料目錄。");
    }

    // ── DI（§4.3）─────────────────────────────────────────────────────────────
    builder.Services.AddStorage(settings);
    builder.Services.AddLogForesightAuth(settings);
    builder.Services.AddLogForesightServices();

    builder.Services.AddControllersWithViews(options =>
    {
        // 例外處理單點化：Controller 與 Service 都不必寫 try-catch 樣板（§7.2）
        options.Filters.Add<ApiExceptionFilter>();
    });

    // DTO 驗證失敗也要回統一信封，否則前端拿不到欄位訊息（§7.2）
    builder.Services.AddEnvelopeModelValidation();

    var app = builder.Build();

    // ── 啟動時的資料準備 ──────────────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        identity.EnsureSeedGroups();

        // 開箱測試管理員（§1）：僅測試模式（Provider=Stub）且非 Production 才 seed——Stub 免密碼，
        // 建一個 admin 成員即可直接登入測全站，補足「只能以最小權限的 serverAdmin 登入」的落差。
        // Production 用 Stub 啟動會被 Validate 擋下，這裡的環境判斷是第二道保險。
        if (string.Equals(settings.Auth.Provider, "Stub", StringComparison.OrdinalIgnoreCase)
            && !app.Environment.IsProduction())
        {
            identity.SeedTestAdmin("demo-admin", "測試管理員");
        }

        if (identity.HasNoAdmins())
        {
            logger.Warn("目前沒有任何 admin 群組成員。請以 serverAdmin 帳號（{0}）登入後指派。",
                settings.Auth.ServerAdmin.Account);
        }

        // Sentinel 一律由 Web「系統管理 > NetIQ 維護」頁維護（docs/archive/HISTORY.md 定案 1），
        // appsettings.json 不再提供種子——全新環境部署後直接在維護頁新增伺服器。
        var sentinelStore = scope.ServiceProvider.GetRequiredService<ISentinelStore>();

        // SentinelId 回填（定案 4）：一次性遷移，冪等，見 SentinelIdBackfiller 的類別註解
        var hostStore = scope.ServiceProvider.GetRequiredService<IHostStore>();
        var backfill = SentinelIdBackfiller.Run(hostStore, sentinelStore);
        if (backfill.BackfilledCount > 0)
            logger.Info("已回填 {0} 台主機的 SentinelId（{1} 台對不到現存 Sentinel，維持待歸屬）。",
                backfill.BackfilledCount, backfill.UnresolvedCount);

        // 規則庫初始化（docs/archive/FEEDBACK-5-PLAN.md §10）：rules blob 原本只有批次的
        // RuleBootstrapper 會初始化，全新環境（批次從未執行過）Web 開站即假設「批次至少
        // 跑過一次」，規則維護頁因此對著不存在的 blob 直接拋例外。這裡冪等補上——
        // 已存在只載入不覆寫，不存在才寫入內建種子；用 LoadContent 而非 Run，因為 Web
        // 不需要（也不該）連帶初始化 KnownIssueCatalog 的全域分類狀態，那是批次分析時才用得到的。
        // 失敗不擋站台啟動：規則頁在極端情況（DB 寫入失敗）仍會顯示原本的錯誤，其餘頁面不受影響。
        try
        {
            var ruleStore = scope.ServiceProvider.GetRequiredService<IKnownIssueRuleStore>();
            RuleBootstrapper.LoadContent(ruleStore);

            // 原廠種子鏡像同步（與批次 Program.cs 同一份邏輯）：「回復預設」需要一份使用者
            // 碰不到的原始內容才比較得出差異，全新環境沒有這份鏡像會導致該功能無法使用。
            var ruleSeedStore = scope.ServiceProvider.GetRequiredService<IRuleSeedStore>();
            ruleSeedStore.Sync(KnownIssueSeed.CreateRules(), KnownIssueSeed.Version);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "規則庫初始化失敗（不影響其餘頁面，規則維護頁本次可能無法使用）：{0}", ex.Message);
        }
    }

    // ── 管線 ─────────────────────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles(new StaticFileOptions
    {
        // no-cache＝「可以快取，但每次用前要帶 ETag 回來驗證」（不是不快取）。
        // 為什麼必要：頁面模組（js/pages/*）經 asp-append-version 有版本參數，但它們
        // import 的共用模組（js/core/*）網址沒有版本——瀏覽器啟發式快取會把舊版
        // core 模組留到天荒地老，core 檔案一改（例如新增 export），舊快取的 core 配上
        // 新版頁面模組直接 import 失敗、整頁掛掉，除非使用者知道要硬重整。
        // 改動前後檔案未變時仍是 304，多的只是一次極輕的驗證往返。
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    });
    app.UseRouting();

    app.UseAuthentication();
    app.UseMiddleware<ActiveUserMiddleware>();   // 停用帳號即時生效（§6.3），必須在驗證之後
    app.UseAuthorization();
    // 處理狀態搬移中擋下寫入（docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三-d）。
    // 位置有要求：在 UseAuthorization 之後（不擋登入），在 CsrfHeaderMiddleware 之前
    //（先回明確的「搬移中」，不要讓使用者收到看似「請求來源驗證失敗」的誤導訊息）
    app.UseMiddleware<MigrationGateMiddleware>();
    app.UseMiddleware<CsrfHeaderMiddleware>();   // 非 GET 的 API 需帶 X-Requested-By（§6.4）

    app.MapControllers();

    logger.Info("LogForesight.Web 啟動：驗證方式 {0}，資料根目錄 {1}",
        settings.Auth.Provider, settings.Storage.ResolveDataRoot());

    app.Run();
    return 0;
}
catch (Exception ex)
{
    // 啟動階段的失敗（設定不合格、資料目錄不存在）必須留下明確訊息，
    // 否則站台起不來時只會看到一個沒有上下文的錯誤頁
    logger.Fatal(ex, "LogForesight.Web 啟動失敗");
    Console.Error.WriteLine("啟動失敗：" + ex.Message);
    return 1;
}
finally
{
    LogManager.Shutdown();
}
