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
