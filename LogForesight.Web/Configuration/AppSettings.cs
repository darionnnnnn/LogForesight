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
    /// <summary>儲存後端。型別取自 Core，與批次 exe 共用同一份欄位定義</summary>
    public StorageSettings Storage { get; set; } = new();

    public JwtSettings Jwt { get; set; } = new();

    public AuthSettings Auth { get; set; } = new();

    public ImportSettings Import { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    /// <summary>
    /// 啟動時的組態驗證：不合格直接拋例外中止啟動（fail fast），不讓站台帶病執行。
    /// 沿用批次端「設定錯誤要顯性化」的原則——設定寫錯時最糟的結果是「看起來正常但行為不對」，
    /// 例如 SecretKey 空白會讓 JWT 簽章失效、DataRoot 指錯會讓整站看起來沒有任何資料。
    /// </summary>
    public void Validate(bool isProduction)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Jwt.SecretKey))
            errors.Add("Jwt:SecretKey 未設定（正式環境請用環境變數 Jwt__SecretKey 或 user-secrets 提供，不要進版控）。");
        else if (System.Text.Encoding.UTF8.GetByteCount(Jwt.SecretKey) < 32)
            errors.Add("Jwt:SecretKey 長度不足：HMAC-SHA256 簽章金鑰至少需要 32 bytes。");

        if (Jwt.ExpireHours <= 0)
            errors.Add("Jwt:ExpireHours 必須大於 0。");

        var dataRoot = Storage.ResolveDataRoot();
        if (!Directory.Exists(dataRoot))
            errors.Add($"Storage:DataRoot 不存在：{dataRoot}（應指向批次 LogForesight.exe 的執行檔目錄）。");

        if (string.IsNullOrWhiteSpace(Auth.ServerAdmin.Account))
            errors.Add("Auth:ServerAdmin:Account 未設定（本地救援帳號，用於指派 admin 群組成員）。");

        if (string.IsNullOrWhiteSpace(Auth.ServerAdmin.PasswordHash))
            errors.Add("Auth:ServerAdmin:PasswordHash 未設定（以 LogForesight.Web.exe --hash-password 產生）。");

        // Stub 不驗密碼，只要知道帳號就能登入。測試環境刻意允許（已評估接受），
        // 但絕不能跟著設定檔一起被帶上正式環境——這道欄杆防的是部署時的疏忽，不是測試期的使用。
        if (isProduction && string.Equals(Auth.Provider, "Stub", StringComparison.OrdinalIgnoreCase))
            errors.Add("正式環境不允許 Auth:Provider=Stub（Stub 不驗證密碼）。請改用 Ldap。");

        if (errors.Count > 0)
            throw new InvalidOperationException("appsettings.json 設定不合格：" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
    }
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
    /// <summary>驗證方式："Stub"（開發用，不驗密碼）或 "Ldap"（AD 帳密驗證）</summary>
    public string Provider { get; set; } = "Stub";

    public ServerAdminSettings ServerAdmin { get; set; } = new();

    public LdapSettings Ldap { get; set; } = new();
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

public class LdapSettings
{
    /// <summary>AD 網域名稱（如 corp.local）；Provider=Ldap 時必填</summary>
    public string Domain { get; set; } = "";
}

public class ImportSettings
{
    public int MaxFileSizeKb { get; set; } = 2048;

    public int MaxRows { get; set; } = 5000;
}

public class UiSettings
{
    public int DefaultPageSize { get; set; } = 50;

    public int DashboardDefaultDays { get; set; } = 7;

    public int RunMatrixDays { get; set; } = 14;
}
