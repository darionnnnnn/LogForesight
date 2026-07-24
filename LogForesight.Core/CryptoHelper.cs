using System.Security.Cryptography;
using System.Text;

namespace LogForesight;

/// <summary>
/// 密文欄位的加解密（目前唯一用途：<see cref="Sentinel.PasswordEnc"/>）。
///
/// AES-256-CBC，金鑰內嵌於程式（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 3）。
/// **防護邊界誠實聲明**：這道防線防的是「直接翻資料庫看到明碼」，不防「拿得到這支程式
/// （或其反組譯結果）的人」——金鑰與密文都在同一份可執行檔的掌控範圍內，本質是混淆而非
/// 真正保密。內網維運工具的威脅模型下已足夠；若日後需要更高強度，金鑰應改放環境變數
/// （批次與 Web 兩邊同時設定），屆時本檔的介面不必變。
///
/// 密文固定帶 <c>enc:v1:</c> 前綴：一來讓呼叫端能分辨欄位是否已加密（<see cref="IsEncrypted"/>），
/// 二來未來換演算法/金鑰版本時新舊格式並存過渡有辨識依據。
/// </summary>
public static class CryptoHelper
{
    private const string Prefix = "enc:v1:";

    // 內嵌金鑰：隨機產生的 32 bytes（AES-256）。見上方類別註解的防護邊界說明。
    private static readonly byte[] Key = Convert.FromBase64String(
        "aXEQsH/zY6lrvkc/pJZDYwa8oAaiOwInIZWou5VlfWo=");

    /// <summary>加密明碼，回傳帶 <c>enc:v1:</c> 前綴的密文。空字串／null 原樣回傳（無密碼不必加密）。</summary>
    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";

        using var aes = Aes.Create();
        aes.Key = Key;
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

    /// <summary>解密 <see cref="Encrypt"/> 產生的密文。不是本 Helper 格式的值會擲例外——呼叫端應先用 <see cref="IsEncrypted"/> 判斷。</summary>
    public static string Decrypt(string value)
    {
        if (!IsEncrypted(value))
            throw new InvalidOperationException("值不是 CryptoHelper 加密的密文（缺少 enc:v1: 前綴）。");

        var combined = Convert.FromBase64String(value[Prefix.Length..]);

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = combined[..16];

        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = combined[16..];
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>值是否已是本 Helper 產生的密文格式</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}
