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
    settings.Validate(builder.Environment.IsProduction());
    builder.Services.AddSingleton(settings);

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
    app.UseStaticFiles();
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
