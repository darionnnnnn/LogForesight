using System.Security.Cryptography;

namespace LogForesight.Web.Auth;

/// <summary>
/// serverAdmin 密碼的 PBKDF2 雜湊與驗證（docs/WEB-SPEC.md §6.2）。
///
/// 為什麼不存明文：appsettings.json 會進備份、複本、可能被複製到測試機——
/// 明文密碼會跟著擴散到你追蹤不到的地方。雜湊只能驗證、不能反推，
/// 檔案外流時攻擊者拿到的不是可直接使用的憑證。
///
/// 格式：<c>PBKDF2$&lt;iterations&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c>（自帶參數，
/// 未來調高迭代次數時舊雜湊仍可驗證，不需要強迫所有人同時換密碼）。
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 210_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>驗證密碼。雜湊字串格式不正確時回 false（不拋例外——設定錯誤不該讓登入端點噴 500）</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash)) return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // 固定時間比對：一般的位元組比較會在第一個不同的位元組就返回，
        // 執行時間會洩漏「猜對了前幾個位元組」的資訊
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
