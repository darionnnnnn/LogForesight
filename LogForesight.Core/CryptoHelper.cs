using System.Security.Cryptography;
using System.Text;
using NLog;

namespace LogForesight;

/// <summary>
/// 密文欄位的加解密（用途：<see cref="Sentinel.PasswordEnc"/>、<see cref="SystemSettings.AiApiKeyEnc"/>）。
///
/// AES-256-CBC。金鑰來源（docs/archive/HISTORY.md P0-5，取代 docs/archive/HISTORY.md
/// 定案 3 原本「金鑰內嵌於程式」的做法）：優先讀環境變數 <c>LF_CRYPTO_KEY</c>（base64，解碼後
/// 需恰為 32 bytes）；未設定時沿用內嵌金鑰並記一次 WARN。批次與 Web 需設定**同一把**機器層級
/// 環境變數才能互相讀懂對方寫入的密文。
///
/// **防護邊界誠實聲明**：這道防線防的是「DB 外洩但主機沒破」——金鑰仍在同一台主機上（環境變數或
/// 內嵌），拿得到主機本身（或程式反組譯結果）的人依然解得開。內網維運工具的威脅模型下已足夠。
///
/// **金鑰輪替**：<see cref="Decrypt"/> 現用金鑰解不開時會退回內嵌金鑰再試一次，讓「剛設定
/// LF_CRYPTO_KEY、DB 裡還是舊金鑰時代密文」的過渡期不中斷；任何一次重新加密（管理頁存檔）
/// 就會換成現用金鑰的密文。<see cref="Encrypt"/> 一律只用現用金鑰。
///
/// 密文固定帶 <c>enc:v1:</c> 前綴：一來讓呼叫端能分辨欄位是否已加密（<see cref="IsEncrypted"/>），
/// 二來未來換演算法版本時新舊格式並存過渡有辨識依據（金鑰輪替不算演算法變更，不换前綴）。
/// </summary>
public static class CryptoHelper
{
    private const string Prefix = "enc:v1:";
    private const string EnvVarName = "LF_CRYPTO_KEY";

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // 內嵌金鑰（fallback）：隨機產生的 32 bytes（AES-256）。見上方類別註解的防護邊界說明。
    private static readonly byte[] EmbeddedKey = Convert.FromBase64String(
        "aXEQsH/zY6lrvkc/pJZDYwa8oAaiOwInIZWou5VlfWo=");

    /// <summary>加密明碼，回傳帶 <c>enc:v1:</c> 前綴的密文。空字串／null 原樣回傳（無密碼不必加密）。</summary>
    public static string Encrypt(string? plaintext) => EncryptWith(CurrentKey(), plaintext);

    /// <summary>
    /// 解密 <see cref="Encrypt"/> 產生的密文。不是本 Helper 格式的值會擲例外——呼叫端應先用
    /// <see cref="IsEncrypted"/> 判斷。現用金鑰解不開時退回內嵌金鑰再試一次（見類別註解的金鑰輪替說明）。
    /// </summary>
    public static string Decrypt(string value) => DecryptWith(CurrentKey(), value);

    /// <summary>值是否已是本 Helper 產生的密文格式</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] CurrentKey() => ResolveKey(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// 由環境變數值解析出實際生效金鑰的純函數（不直接讀 <see cref="Environment"/>）——
    /// 測試藉此直接驗證各種輸入，不必碰真的環境變數（多個測試類別可能並行，碰真環境變數會互相干擾）。
    /// null/空白＝未設定，沿用內嵌金鑰並記 WARN；格式錯誤或長度不對一律 fail-fast，
    /// 不讓「設定了但設錯」被誤判成「沒設定」而悄悄退回內嵌金鑰。
    /// </summary>
    internal static byte[] ResolveKey(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue))
        {
            Log.Warn("[Crypto] 環境變數 {EnvVar} 未設定，沿用程式內嵌金鑰（正式環境建議設定，" +
                     "見 CryptoHelper 類別註解的防護邊界說明）。", EnvVarName);
            return EmbeddedKey;
        }

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

    /// <summary>加密實作（internal 供測試以任意金鑰直接驗證加解密與 fallback 行為）</summary>
    internal static string EncryptWith(byte[] key, string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // IV 不是機密，跟密文存在一起即可（解密時原樣取回）
        var combined = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    /// <summary>解密實作（internal 供測試以任意「現用金鑰」直接驗證，含解不開時退回內嵌金鑰的 fallback）</summary>
    internal static string DecryptWith(byte[] key, string value)
    {
        if (!IsEncrypted(value))
            throw new InvalidOperationException("值不是 CryptoHelper 加密的密文（缺少 enc:v1: 前綴）。");

        var combined = Convert.FromBase64String(value[Prefix.Length..]);
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
