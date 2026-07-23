using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Filters;
using LogForesight.Web.Middleware;
using LogForesight.Web.Services;
using NLog;
using NLog.Web;

// ── 命令列工具：--hash-password ───────────────────────────────────────────────
// serverAdmin 的密碼以 PBKDF2 雜湊存放（docs/WEB-SPEC.md §6.2），這裡提供產生雜湊的方式。
// 輪替 SOP：跑這個指令 → 把輸出貼進 appsettings.json 的 Auth:ServerAdmin:PasswordHash → 重啟站台。
if (args.Length > 0 && args[0] == "--hash-password")
{
    Console.Write("請輸入要雜湊的密碼：");
    var password = ReadPasswordMasked();
    Console.WriteLine();

    if (string.IsNullOrWhiteSpace(password))
    {
        Console.WriteLine("密碼不可為空。");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("請將下面這行填入 appsettings.json 的 Auth:ServerAdmin:PasswordHash：");
    Console.WriteLine();
    Console.WriteLine(PasswordHasher.Hash(password));
    Console.WriteLine();
    return 0;
}

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // ── 組態：全部集中在強型別的 WebAppSettings（§5）────────────────────────
    var settings = builder.Configuration.Get<WebAppSettings>() ?? new WebAppSettings();

    // 開發／測試環境：DataRoot 未明確指定時，改用「相對於本站台輸出目錄推算出的批次輸出目錄」，
    // 不再寫死絕對路徑（見 appsettings.Development.json 的說明）。這樣不綁使用者名稱，
    // 也會自動跟著 Debug/Release 與 TFM 變動。開發者若在設定檔明確填了 DataRoot，則尊重其值、不推算。
    if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(settings.Storage.DataRoot))
    {
        var computed = TryResolveSiblingBatchDataRoot();
        if (computed is not null)
        {
            settings.Storage.DataRoot = computed;
            logger.Info("開發環境自動推算批次資料根目錄（Storage.DataRoot）：{0}", computed);
        }
    }

    settings.Validate(builder.Environment.IsProduction());
    builder.Services.AddSingleton(settings);

    // 資料根目錄健檢（誠實申報，沿用批次端「沒告警 ≠ 沒問題」的原則）：
    // DataRoot 存在（Validate 已檢查）但底下既無 rules.json 也無 history.txt，最常見的成因是
    // Storage:DataRoot 指錯了——指到 Web 自己的執行檔目錄、而不是批次 LogForesight.exe 的資料目錄。
    // 那正是「規則維護頁報『載入規則失敗，檔案不存在』、儀表板一片空白」的來源。
    // 刻意不 fail-fast：批次還沒首次執行是合法狀態；但要顯性提示，而不是讓人對著空白畫面猜。
    var dataRoot = settings.Storage.ResolveDataRoot();
    if (!File.Exists(Path.Combine(dataRoot, "rules.json")) &&
        !File.Exists(Path.Combine(dataRoot, "history.txt")))
    {
        logger.Warn("資料根目錄 {0} 底下找不到 rules.json 或 history.txt。若批次 LogForesight.exe 已執行過，" +
            "代表 Storage:DataRoot 指錯目錄（應指向批次的資料目錄），規則頁與儀表板會因此空白。", dataRoot);
        Console.Error.WriteLine($"⚠ 資料根目錄「{dataRoot}」底下找不到 rules.json / history.txt；" +
            "若批次已執行過，請確認 Storage:DataRoot 指向批次 LogForesight.exe 的資料目錄。");
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
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        identity.EnsureSeedGroups();

        if (identity.HasNoAdmins())
        {
            logger.Warn("目前沒有任何 admin 群組成員。請以 serverAdmin 帳號（{0}）登入後指派。",
                settings.Auth.ServerAdmin.Account);
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

/// <summary>
/// 開發／測試環境用：從本站台的輸出目錄推算同一個 repo 內批次 LogForesight.exe 的輸出目錄，
/// 取代 appsettings.Development.json 中原本寫死的絕對路徑。
///
/// 兩個專案的輸出結構相同（{repo}\{專案}\bin\{Config}\{TFM}），差別只在專案資料夾名稱，
/// 所以把本站台輸出目錄尾端的 bin\{Config}\{TFM} 原樣接到批次專案資料夾（LogForesight）下即可——
/// 自動跟著 Debug/Release 與 TFM 變動，且不含任何使用者相關的絕對路徑。
/// （"LogForesight" 是批次專案的資料夾名稱，屬 repo 結構常數，非使用者路徑。）
///
/// 推算不出（目錄結構非預期）時回 null，交由呼叫端維持原值、後續 Validate 顯性報錯，
/// 不猜一個可能錯的路徑蓋掉設定。
/// </summary>
static string? TryResolveSiblingBatchDataRoot()
{
    var webBinTfm = new DirectoryInfo(AppContext.BaseDirectory); // …\LogForesight.Web\bin\{Config}\{TFM}
    var webProjectDir = webBinTfm.Parent?.Parent?.Parent;        // …\LogForesight.Web
    var repoRoot = webProjectDir?.Parent;                        // repo 根目錄
    if (webProjectDir is null || repoRoot is null) return null;

    // bin\{Config}\{TFM}——本站台與批次相同，原樣沿用即可跟著建置設定與 TFM 變動
    var tail = Path.GetRelativePath(webProjectDir.FullName, webBinTfm.FullName);
    return Path.Combine(repoRoot.FullName, "LogForesight", tail);
}

/// <summary>讀取密碼但不回顯（避免密碼留在畫面與終端機的捲動紀錄裡）</summary>
static string ReadPasswordMasked()
{
    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;

        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    return password.ToString();
}
