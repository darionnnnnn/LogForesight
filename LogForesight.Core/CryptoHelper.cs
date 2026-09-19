using System.Security.Cryptography;
using System.Text;
using NLog;

namespace LogForesight.Core;

/// <summary>
/// 密文欄位的加解密（用途：<see cref="Sentinel.PasswordEnc"/>、<see cref="SystemSettings.AiApiKeyEnc"/>、
/// SMTP 密碼與 PRTG 三種憑證）。
///
/// 金鑰來源優先序（解析一次後快取）：
///   1. 環境變數 <c>LF_CRYPTO_KEY</c>（base64，解碼後需恰為 32 bytes；設錯 fail-fast）；
///   2. 站台專屬金鑰檔（<see cref="UseKeyFile"/> 指定，啟動時由 Web 的 CryptoKeyBootstrapper 產生；
///      預設在 DataRoot\keys\lf-crypto.key，格式同環境變數，設錯同樣 fail-fast）；
///   3. 程式內嵌金鑰（只剩沒呼叫 <see cref="UseKeyFile"/> 的情境，例如單元測試；記一次 WARN）。
///
/// **防護邊界誠實聲明**：金鑰檔與 DB 分開存放，防的是「只拿到 DB 備份」——拿得到主機本身
/// （可讀金鑰檔或環境變數）的人依然解得開。內嵌金鑰等於公開，只為 v1 舊密文過渡保留。
///
/// 密文格式：
///   - <c>enc:v2:</c>＋base64(nonce(12)‖tag(16)‖ciphertext)，AES-256-GCM。<see cref="Encrypt"/> 一律產生 v2；
///     解密只用現用金鑰，驗證失敗必擲 <see cref="CryptographicException"/>（不會像 CBC 那樣錯金鑰偶爾回亂碼）。
///   - <c>enc:v1:</c>＋base64(IV(16)‖ciphertext)，AES-256-CBC 舊格式：現用金鑰解不開時退回內嵌金鑰再試
///     （金鑰輪替過渡期）。啟動時 CryptoKeyBootstrapper 會把 v1 重寫成 v2（<see cref="NeedsRewrap"/>）。
/// </summary>
public static class CryptoHelper
{
    private const string PrefixV1 = "enc:v1:";
    private const string PrefixV2 = "enc:v2:";
    private const string EnvVarName = "LF_CRYPTO_KEY";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // 內嵌金鑰（fallback）：隨機產生的 32 bytes（AES-256）。v1 舊密文過渡期仍需要，不可移除。
    private static readonly byte[] EmbeddedKey = Convert.FromBase64String(
        "aXEQsH/zY6lrvkc/pJZDYwa8oAaiOwInIZWou5VlfWo=");

    private static readonly object StateLock = new();
    private static string? _keyFilePath;
    private static byte[]? _cachedKey;
    private static string _cachedSource = "embedded";
    private static bool _embeddedWarned;
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);
    private static volatile bool _decryptFailureSeen;

    /// <summary>曾有密文解不開（金鑰不符或密文損毀）；供健康頁顯示。</summary>
    public static bool DecryptFailureSeen => _decryptFailureSeen;

    /// <summary>現用金鑰來源：<c>env</c>／<c>file</c>／<c>embedded</c>。</summary>
    public static string KeySource
    {
        get
        {
            lock (StateLock)
            {
                EnsureResolved();
                return _cachedSource;
            }
        }
    }

    /// <summary>現用金鑰指紋：SHA-256(金鑰) 前 16 個十六進位字元（小寫），供啟動時比對 DB 記錄的金鑰是否相符。</summary>
    public static string KeyFingerprint => Convert.ToHexString(SHA256.HashData(CurrentKey()))[..16].ToLowerInvariant();

    /// <summary>設定金鑰檔路徑（啟動時呼叫；再次呼叫以最後一次為準）。會清除金鑰快取。</summary>
    public static void UseKeyFile(string path)
    {
        lock (StateLock)
        {
            _keyFilePath = path;
            _cachedKey = null;
        }
    }

    /// <summary>測試用：清除金鑰檔設定、快取與失敗旗標，回到「未呼叫 UseKeyFile」的初始狀態。</summary>
    internal static void ResetForTests()
    {
        lock (StateLock)
        {
            _keyFilePath = null;
            _cachedKey = null;
            _cachedSource = "embedded";
            _embeddedWarned = false;
            ReportedFailures.Clear();
            _decryptFailureSeen = false;
        }
    }

    /// <summary>加密明碼，回傳 <c>enc:v2:</c> 密文。空字串／null 回空字串（無密碼不必加密）。</summary>
    public static string Encrypt(string? plaintext) => EncryptWith(CurrentKey(), plaintext);

    /// <summary>
    /// 解密 <see cref="Encrypt"/> 產生的密文（v1／v2）。不是本 Helper 格式的值擲 <see cref="InvalidOperationException"/>；
    /// 金鑰不對擲 <see cref="CryptographicException"/>。正式碼請用 <see cref="TryDecrypt"/>。
    /// </summary>
    public static string Decrypt(string value) => DecryptWith(CurrentKey(), value);

    /// <summary>
    /// 不擲例外的解密：非密文（含 null／空字串）回 true 且原樣（null 回空字串）；解密失敗回 false、
    /// <paramref name="plaintext"/> 為空字串，並設 <see cref="DecryptFailureSeen"/>、每筆密文只記一次 Error。
    /// </summary>
    public static bool TryDecrypt(string? value, out string plaintext)
    {
        if (!IsEncrypted(value))
        {
            plaintext = value ?? "";
            return true;
        }

        try
        {
            plaintext = Decrypt(value!);
            return true;
        }
        catch (Exception ex)
        {
            plaintext = "";
            RecordFailure(value!, ex);
            return false;
        }
    }

    /// <summary>值是否已是本 Helper 產生的密文格式（v1 或 v2）</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith(PrefixV2, StringComparison.Ordinal) || value.StartsWith(PrefixV1, StringComparison.Ordinal));

    /// <summary>值是 v1（CBC）舊密文、應重寫成 v2 時回 true。</summary>
    public static bool NeedsRewrap(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(PrefixV1, StringComparison.Ordinal);

    private static void RecordFailure(string value, Exception ex)
    {
        _decryptFailureSeen = true;
        var tag = value.Length > 16 ? value[..16] : value;
        bool first;
        lock (StateLock) first = ReportedFailures.Add(tag);
        if (first)
            Log.Error("[Crypto] 密文無法解密（{0}），此欄視為未設定。常見原因：金鑰檔與資料庫不相符，" +
                      "請還原正確的金鑰檔或到設定頁重新輸入密碼。", ex.GetType().Name);
    }

    private static byte[] CurrentKey()
    {
        lock (StateLock)
        {
            EnsureResolved();
            return _cachedKey!;
        }
    }

    // 呼叫端須持有 StateLock
    private static void EnsureResolved()
    {
        if (_cachedKey != null) return;

        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            _cachedKey = ResolveKey(env);
            _cachedSource = "env";
            return;
        }

        if (_keyFilePath != null && File.Exists(_keyFilePath))
        {
            string content;
            try
            {
                content = File.ReadAllText(_keyFilePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 金鑰檔產生時權限限縮為「建立它的帳號＋Administrators」：先以管理員手動啟動、之後改用
                // 服務帳號執行時會走到這裡。不退回內嵌金鑰——已用這把金鑰加密的密碼會全部解不開
                throw new InvalidOperationException(
                    $"無法讀取金鑰檔「{_keyFilePath}」：站台執行帳號沒有讀取權限。金鑰檔由第一次啟動站台的帳號建立並限縮權限；" +
                    "請以系統管理員身分在該檔案的「內容 > 安全性」加入站台執行帳號（IIS 應用程式集區身分或 Windows 服務帳號）的讀取權限後重新啟動。", ex);
            }
            _cachedKey = ParseKeyFile(content, _keyFilePath);
            _cachedSource = "file";
            return;
        }

        if (!_embeddedWarned)
        {
            _embeddedWarned = true;
            Log.Warn("[Crypto] 環境變數 {0} 未設定且沒有金鑰檔，沿用程式內嵌金鑰（不建議，見 CryptoHelper 類別註解）。",
                EnvVarName);
        }
        _cachedKey = EmbeddedKey;
        _cachedSource = "embedded";
    }

    /// <summary>
    /// 由環境變數值解析出實際生效金鑰的純函數（不直接讀 <see cref="Environment"/>）。
    /// null/空白＝未設定，回內嵌金鑰；格式錯誤或長度不對一律 fail-fast。
    /// </summary>
    internal static byte[] ResolveKey(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return EmbeddedKey;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(envValue);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"環境變數 {EnvVarName} 不是合法的 base64 字串。", ex);
        }

        if (key.Length != 32)
            throw new InvalidOperationException(
                $"環境變數 {EnvVarName} 解碼後長度為 {key.Length} bytes，AES-256 金鑰需恰為 32 bytes。");

        return key;
    }

    /// <summary>解析金鑰檔內容（base64、32 bytes）的純函數；格式錯誤 fail-fast。</summary>
    internal static byte[] ParseKeyFile(string content, string path)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(content.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"金鑰檔 {path} 內容不是合法的 base64 字串。", ex);
        }

        if (key.Length != 32)
            throw new InvalidOperationException(
                $"金鑰檔 {path} 解碼後長度為 {key.Length} bytes，AES-256 金鑰需恰為 32 bytes。");

        return key;
    }

    /// <summary>內嵌金鑰（internal 供測試產生 v1 舊密文）</summary>
    internal static byte[] EmbeddedKeyForTests => EmbeddedKey;

    /// <summary>v2（AES-256-GCM）加密實作（internal 供測試以任意金鑰直接驗證）</summary>
    internal static string EncryptWith(byte[] key, string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var combined = new byte[NonceSize + TagSize + plainBytes.Length];
        var nonce = combined.AsSpan(0, NonceSize);
        var tag = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        return PrefixV2 + Convert.ToBase64String(combined);
    }

    /// <summary>v1（AES-256-CBC）舊格式加密，只供測試產生舊密文</summary>
    internal static string EncryptV1With(byte[] key, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var combined = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        return PrefixV1 + Convert.ToBase64String(combined);
    }

    /// <summary>解密實作（internal 供測試以任意「現用金鑰」直接驗證；v1 含退回內嵌金鑰的 fallback）</summary>
    internal static string DecryptWith(byte[] key, string value)
    {
        if (value.StartsWith(PrefixV2, StringComparison.Ordinal))
        {
            var data = Convert.FromBase64String(value[PrefixV2.Length..]);
            if (data.Length < NonceSize + TagSize)
                throw new CryptographicException("enc:v2 密文長度不足。");

            var plain = new byte[data.Length - NonceSize - TagSize];
            using var gcm = new AesGcm(key, TagSize);
            // 驗證失敗擲 AuthenticationTagMismatchException（CryptographicException 子類），不退回其他金鑰
            gcm.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }

        if (!value.StartsWith(PrefixV1, StringComparison.Ordinal))
            throw new InvalidOperationException("值不是 CryptoHelper 加密的密文（缺少 enc:v1:／enc:v2: 前綴）。");

        var combined = Convert.FromBase64String(value[PrefixV1.Length..]);
        var iv = combined[..16];
        var cipherBytes = combined[16..];

        try
        {
            return DecryptRaw(key, iv, cipherBytes);
        }
        catch (CryptographicException) when (!key.SequenceEqual(EmbeddedKey))
        {
            // 金鑰輪替過渡期：現用金鑰解不開，可能是這筆密文還是內嵌金鑰時代寫入的，退回內嵌金鑰再試一次
            return DecryptRaw(EmbeddedKey, iv, cipherBytes);
        }
    }

    private static string DecryptRaw(byte[] key, byte[] iv, byte[] cipherBytes)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
