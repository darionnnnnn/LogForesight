using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel 密碼欄位的加解密（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 3）。</summary>
public class CryptoHelperTests
{
    [Fact]
    public void 加密後解密_取回原始明碼()
    {
        var cipher = CryptoHelper.Encrypt("my-secret-password");

        Assert.NotEqual("my-secret-password", cipher);
        Assert.Equal("my-secret-password", CryptoHelper.Decrypt(cipher));
    }

    [Fact]
    public void 密文帶有辨識前綴()
    {
        var cipher = CryptoHelper.Encrypt("x");

        Assert.StartsWith("enc:v1:", cipher);
        Assert.True(CryptoHelper.IsEncrypted(cipher));
    }

    [Fact]
    public void 明碼字串不被誤判為已加密()
    {
        Assert.False(CryptoHelper.IsEncrypted("plain-text-password"));
        Assert.False(CryptoHelper.IsEncrypted(null));
        Assert.False(CryptoHelper.IsEncrypted(""));
    }

    [Fact]
    public void 同樣明碼兩次加密_密文不同()
    {
        // 每次加密用新的隨機 IV，密文不該重複（否則同密碼的兩筆資料一眼就能看出相同）
        var a = CryptoHelper.Encrypt("same-password");
        var b = CryptoHelper.Encrypt("same-password");

        Assert.NotEqual(a, b);
        Assert.Equal("same-password", CryptoHelper.Decrypt(a));
        Assert.Equal("same-password", CryptoHelper.Decrypt(b));
    }

    [Fact]
    public void 空字串原樣回傳_不加密()
    {
        Assert.Equal("", CryptoHelper.Encrypt(""));
        Assert.Equal("", CryptoHelper.Encrypt(null));
    }

    [Fact]
    public void 解密非本格式的字串_擲例外()
    {
        Assert.Throws<InvalidOperationException>(() => CryptoHelper.Decrypt("not-encrypted"));
    }
}

/// <summary>
/// P0-5：金鑰來源改環境變數 LF_CRYPTO_KEY（未設定時 fallback 內嵌金鑰），
/// 以及解密端的雙金鑰 fallback（金鑰輪替過渡期）。
///
/// 一律測 <see cref="CryptoHelper.ResolveKey"/>／<see cref="CryptoHelper.EncryptWith"/>／
/// <see cref="CryptoHelper.DecryptWith"/> 這幾個 internal 純函數，不碰真的環境變數——
/// xUnit 預設不同測試類別可能並行執行，真改 process 環境變數會讓測試互相干擾。
/// </summary>
public class CryptoHelperKeyResolutionTests
{
    // 與 CryptoHelper 內嵌金鑰不同的另一把合法 32-byte AES-256 金鑰，測試「換了金鑰」情境用
    // （用固定的位元組陣列而非手打 base64——手數 32 bytes 的 base64 字元數容易數錯）
    private static readonly byte[] OtherKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void 環境變數未設定_回內嵌金鑰()
    {
        var key = CryptoHelper.ResolveKey(null);

        // 內嵌金鑰是加解密沿用至今、既有密文都解得開的那把——用一段已知密文往返驗證身分，
        // 比直接比對 byte[] 內容更貼近「這把金鑰實際上就是內嵌金鑰」這件事本身
        var cipher = CryptoHelper.EncryptWith(key, "known-plaintext");
        Assert.Equal("known-plaintext", CryptoHelper.Decrypt(cipher));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 環境變數空白_回內嵌金鑰(string envValue)
    {
        var key = CryptoHelper.ResolveKey(envValue);
        var cipher = CryptoHelper.EncryptWith(key, "known-plaintext");

        Assert.Equal("known-plaintext", CryptoHelper.Decrypt(cipher));
    }

    [Fact]
    public void 環境變數不是合法base64_擲例外()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CryptoHelper.ResolveKey("!!!not-base64!!!"));
        Assert.Contains("LF_CRYPTO_KEY", ex.Message);
    }

    [Theory]
    [InlineData(16)] // AES-128 長度，不是本系統要求的 32 bytes
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    public void 環境變數長度不是32bytes_擲例外(int byteLength)
    {
        var wrongLengthKey = Convert.ToBase64String(new byte[byteLength]);

        var ex = Assert.Throws<InvalidOperationException>(() => CryptoHelper.ResolveKey(wrongLengthKey));
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void 環境變數為合法32byte金鑰_採用該金鑰()
    {
        var validKey = Convert.ToBase64String(OtherKey);

        var resolved = CryptoHelper.ResolveKey(validKey);

        Assert.Equal(OtherKey, resolved);
    }

    [Fact]
    public void 以指定金鑰加密後解密_取回原始明碼()
    {
        var cipher = CryptoHelper.EncryptWith(OtherKey, "rotated-key-secret");

        Assert.Equal("rotated-key-secret", CryptoHelper.DecryptWith(OtherKey, cipher));
    }

    /// <summary>
    /// 金鑰輪替過渡期的核心行為：換了 LF_CRYPTO_KEY 之後，DB 裡舊金鑰（內嵌金鑰）時代寫入的
    /// 密文仍要解得開——不能因為換了金鑰就讓既有的 Sentinel 密碼／AI 金鑰全部變成打不開的密文。
    /// </summary>
    [Fact]
    public void 現用金鑰解不開時_退回內嵌金鑰再試()
    {
        // 用「內嵌金鑰時代」的方式加密（CryptoHelper.Encrypt 在測試環境未設定 LF_CRYPTO_KEY 時
        // 本來就走內嵌金鑰，這裡直接呼叫公開 API 即等同模擬舊密文）
        var legacyCipher = CryptoHelper.Encrypt("legacy-secret");

        // 假設現在換了金鑰（OtherKey）：DecryptWith 用新金鑰解不開，應自動退回內嵌金鑰解密成功
        var result = CryptoHelper.DecryptWith(OtherKey, legacyCipher);

        Assert.Equal("legacy-secret", result);
    }

    /// <summary>兩把金鑰都解不開時（密文本身損毀／根本不是這系統加密的）仍要如實拋出，不能吞掉</summary>
    [Fact]
    public void 兩把金鑰都解不開時_仍拋出例外()
    {
        var thirdKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        var cipherWithThirdKey = CryptoHelper.EncryptWith(thirdKey, "x");

        Assert.ThrowsAny<Exception>(() => CryptoHelper.DecryptWith(OtherKey, cipherWithThirdKey));
    }
}
