using LogForesight.Web.Auth;

namespace LogForesight.Web.Configuration;

/// <summary>
/// Web 的強型別組態根（docs/WEB-SPEC.md §5）。
///
/// 規則：**程式中不直接讀 IConfiguration**，一律建構式注入本類別取值
/// （<c>settings.Jwt.ExpireHours</c>）。組態鍵名只存在於這個檔案，改名時編譯器會抓到，
/// 不會有魔法字串漏網。
///
/// 類別名為 WebAppSettings 而非 AppSettings：Core 已有批次用的 <see cref="LogForesight.AppSettings"/>，
/// 只差大小寫的兩個型別對讀程式的人是陷阱。
/// </summary>
public class WebAppSettings
{
    /// <summary>
    /// 已提交進版控、公開已知的測試值（appsettings.json 的預設 SecretKey／PasswordHash）。
    /// Production 帶著這組值啟動＝所有 JWT 都可被外人偽造、serverAdmin 密碼形同公開——
    /// 這道清單只擋「忘了用環境變數覆寫」的疏忽，不是額外的機密保護層。
    /// **未來再提交任何測試用金鑰／雜湊，記得同步加進這裡。**
    /// </summary>
    private static readonly HashSet<string> KnownDevSecrets = new(StringComparer.Ordinal)
    {
        "bge6V8SsJu6GRuYYyVniemo/XhAJ4J3kis3sqSDai5gluW9wmA2Vnd4opTbXBTxo", // Jwt:SecretKey（appsettings.json）
        "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M=" // Auth:ServerAdmin:PasswordHash（appsettings.json，明文＝LogForesight-dev）
    };

    /// <summary>儲存後端。型別取自 Core，與批次 exe 共用同一份欄位定義</summary>
    public StorageSettings Storage { get; set; } = new();

    public JwtSettings Jwt { get; set; } = new();

    public AuthSettings Auth { get; set; } = new();

    public ServerSettings Server { get; set; } = new();

    // §12（回饋第九輪）：Ai／Permissions／Analysis／Import／Ui／Netiq 區段已自 appsettings.json 退役。
    // 前四者的唯一事實來源改為 DB「系統管理 > 設定」頁（見 SystemSettings ＋
    // RuntimeSettingsResolver.ApplySystemSettingsOverrides）；Ui 的兩個值改為程式常數
    // （DashboardController.DefaultDays／RulesController.DefaultRunMatrixDays），
    // DefaultPageSize 盤點後無任何消費端而直接移除（「有設定無行為」是本專案紅線）。
    // 本檔只保留「站台還沒起來、DB 還沒連上之前就必須知道」的啟動與安全前提。

    /// <summary>
    /// 啟動時的組態驗證：不合格直接拋例外中止啟動（fail fast），不讓站台帶病執行。
    /// 沿用批次端「設定錯誤要顯性化」的原則——設定寫錯時最糟的結果是「看起來正常但行為不對」，
    /// 例如 SecretKey 空白會讓 JWT 簽章失效、DataRoot 指錯會讓整站看起來沒有任何資料。
    /// </summary>
    /// <param name="strict">
    /// 是否擋下出廠公開值與 Stub。呼叫端傳「環境不是 Development」：只有 Development 放行，
    /// 環境名稱設成 Staging／Test 之類的值時一律從嚴，不讓欄杆因為名稱不是字面上的 Production 而靜默失效。
    /// </param>
    /// <param name="environmentName">目前環境名稱，寫進錯誤訊息讓部署者知道站台以為自己跑在哪個環境</param>
    public void Validate(bool strict, string environmentName)
    {
        var errors = new List<string>();
        var envPrefix = $"目前環境為「{environmentName}」：";
        const string devHint = "若這是開發／展示用站台，請把環境變數 ASPNETCORE_ENVIRONMENT 設為 Development" +
                               "（Windows 服務：設為機器層級環境變數後重啟服務；IIS：在 web.config 的 aspNetCore 元素下加 environmentVariable）。";

        if (!Storage.IsValidType)
            errors.Add($"Storage:Type「{Storage.Type}」不受支援，僅允許 " +
                       $"{string.Join(" / ", StorageSettings.ValidTypes)}。");

        if (string.IsNullOrWhiteSpace(Jwt.SecretKey))
            errors.Add("Jwt:SecretKey 未設定（正式環境請用環境變數 Jwt__SecretKey 或 user-secrets 提供，不要進版控）。");
        else if (System.Text.Encoding.UTF8.GetByteCount(Jwt.SecretKey) < 32)
            errors.Add("Jwt:SecretKey 長度不足：HMAC-SHA256 簽章金鑰至少需要 32 bytes。");
        else if (strict && KnownDevSecrets.Contains(Jwt.SecretKey))
            errors.Add(envPrefix + "Jwt:SecretKey 是已提交進版控的公開測試值，正式環境不可沿用（請以環境變數 Jwt__SecretKey 設定另一組隨機字串）。" + devHint);

        if (Jwt.ExpireHours <= 0)
            errors.Add("Jwt:ExpireHours 必須大於 0。");

        var dataRoot = Storage.ResolveDataRoot();
        if (!Directory.Exists(dataRoot))
            errors.Add($"Storage:DataRoot 不存在：{dataRoot}（應指向資料所在的目錄；留空則預設為本站台的執行檔目錄）。");

        if (string.IsNullOrWhiteSpace(Auth.ServerAdmin.Account))
            errors.Add("Auth:ServerAdmin:Account 未設定（本地救援帳號，用於指派 admin 群組成員）。");

        if (string.IsNullOrWhiteSpace(Auth.ServerAdmin.PasswordHash))
            errors.Add("Auth:ServerAdmin:PasswordHash 未設定（以 LogForesight.Web.exe --hash-password 產生）。");
        else if (!PasswordHasher.IsValidHashFormat(Auth.ServerAdmin.PasswordHash))
            errors.Add("Auth:ServerAdmin:PasswordHash 格式不正確（不可直接填寫明文密碼，請使用 LogForesight.Web.exe --hash-password 產生 PBKDF2 雜湊）。");
        else if (strict && KnownDevSecrets.Contains(Auth.ServerAdmin.PasswordHash))
            errors.Add(envPrefix + "Auth:ServerAdmin:PasswordHash 是已提交進版控的公開測試值，正式環境不可沿用（請以環境變數 Auth__ServerAdmin__PasswordHash 設定 --hash-password 產生的新雜湊）。" + devHint);

        // Stub 不驗密碼，只要知道帳號就能登入。測試環境刻意允許（已評估接受），
        // 但絕不能跟著設定檔一起被帶上正式環境——這道欄杆防的是部署時的疏忽，不是測試期的使用。
        if (strict && string.Equals(Auth.Provider, "Stub", StringComparison.OrdinalIgnoreCase))
            errors.Add(envPrefix + "正式環境不允許 Auth:Provider=Stub（Stub 不驗證密碼）。請改用 Ad，" +
                       "並以 serverAdmin 登入後至「系統管理 > 設定」頁啟用 AD 驗證。" + devHint);

        // NetIQ 離線示範資料（§13）改由 DB「NetIQ 維護」頁的 UseOfflineDemoData 開關控制（僅非
        // Production 生效），appsettings 不再有 Netiq:DiscoveryClient，這裡因此不需要對應的啟動驗證。

        foreach (var proxy in Server.TrustedProxies)
        {
            if (!System.Net.IPAddress.TryParse((proxy ?? "").Trim(), out _))
                errors.Add($"Server:TrustedProxies 含有不是 IP 位址的值「{proxy}」（只能填反向代理的 IP，例如 10.0.0.5）。");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("appsettings.json 設定不合格：" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
    }
}

public class ServerSettings
{
    /// <summary>
    /// 站台前面有反向代理（ARR、nginx）時，填代理的 IP；會用 X-Forwarded-For 取得真實來源 IP
    /// （登入節流的 IP 維度靠它）。空＝不處理轉送標頭。
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();
}

public class JwtSettings
{
    public string Issuer { get; set; } = "LogForesight";

    public string Audience { get; set; } = "LogForesight.Web";

    /// <summary>HMAC-SHA256 簽章金鑰（≥32 bytes）。**不進版控**：正式環境用環境變數 Jwt__SecretKey 覆寫</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Token 效期（小時）。不做 refresh token——內網工具，過期重新登入即可</summary>
    public int ExpireHours { get; set; } = 8;

    /// <summary>存放 JWT 的 Cookie 名稱（HttpOnly + Secure + SameSite=Strict，前端 JS 讀不到）</summary>
    public string CookieName { get; set; } = "lf_auth";
}

public class AuthSettings
{
    /// <summary>
    /// 驗證方式（§12 起值域縮為兩個）：
    /// <c>"Ad"</c>（預設，正式環境用）——AD 驗證的設定**唯一來源是「系統管理 > 設定」頁**
    /// （<see cref="SystemSettings.AdAuthEnabled"/>／<c>AdServers</c>），尚未設定前只有 serverAdmin
    /// 進得來（它不經任何 Provider，正是為此存在）；
    /// <c>"Stub"</c>（測試用，不驗密碼）——Production 啟動會被 Validate 擋下。
    /// 舊值 <c>"Ldap"</c> 視同 <c>"Ad"</c>（appsettings 的 Auth:Ldap:Domain 已於 §12 退役）。
    /// </summary>
    public string Provider { get; set; } = "Ad";

    public ServerAdminSettings ServerAdmin { get; set; } = new();
}

/// <summary>
/// 本地救援/引導帳號（docs/WEB-SPEC.md §6.2）。用途是指派 admin 群組成員，
/// 以及 AD 停擺時的救援入口——所以它不存在於 lf_users、不依賴任何 Provider。
/// 能力刻意只有 Maintain + ViewAudit，不含業務資料檢視。
/// </summary>
public class ServerAdminSettings
{
    public string Account { get; set; } = "";

    /// <summary>PBKDF2 雜湊（--hash-password 產生）。不存明文：設定檔會進備份與複本，明文會跟著擴散</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>連續失敗幾次後鎖定。本地帳號不受 AD 帳戶鎖定原則保護，必須自帶防暴力破解</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;
}


