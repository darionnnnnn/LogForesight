namespace LogForesight.Web.Auth;

/// <summary>憑證驗證的結果。失敗原因只寫入診斷 log 與稽核，不回傳給前端（避免帳號列舉）</summary>
public record CredentialCheckResult(bool Success, string? FailureReason = null)
{
    public static readonly CredentialCheckResult Ok = new(true);

    public static CredentialCheckResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// 憑證驗證的抽象（docs/WEB-SPEC.md §6.2）。**只回答「這組帳密是不是本人」**，
/// 不查使用者資料、不判斷是否停用、不算能力——那些是 IdentityService 的職責（SRP）。
///
/// 這層抽象存在的理由：驗證方式會換（測試期 Stub → 正式 AD LDAP），
/// 但登入流程的其餘部分（查使用者、算能力、簽 JWT、寫稽核）完全不變。
/// 換 Provider 時開放封閉原則生效——新增實作、不改既有流程。
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>供診斷 log 與 /api/auth/me 顯示的名稱</summary>
    string Name { get; }

    /// <summary>
    /// 此 Provider 是否驗密碼。Ldap=true（登入頁密碼欄必填）；Stub=false（後端一律通過、不比對密碼，
    /// 登入頁密碼欄仍顯示但改為選填）。也一併決定 serverAdmin 救援帳號在此模式是否驗密碼。
    /// </summary>
    bool RequiresPassword { get; }

    CredentialCheckResult Verify(string account, string? password);
}

/// <summary>
/// 開發/前期測試用：不驗證密碼，任何密碼（含空白）都通過。
///
/// 使用者是否存在、是否停用由 IdentityService 檢查，所以實際效果是
/// 「lf_users 裡有這個帳號且未停用就能登入」。已知且已接受的風險
/// （測試環境不含核心重要主機，2026-07-21 決策）；WebAppSettings.Validate 會擋下
/// 帶著這個 Provider 上正式環境的部署。
/// </summary>
public class StubAuthenticationProvider : IAuthenticationProvider
{
    public string Name => "Stub";

    public bool RequiresPassword => false;

    public CredentialCheckResult Verify(string account, string? password) => CredentialCheckResult.Ok;
}
