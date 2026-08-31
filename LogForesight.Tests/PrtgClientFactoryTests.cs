using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgClientFactory 的單元測試（PRTG 第 2 輪批次A）：
/// 工廠建立客戶端、認證憑證檢查、明文與密文相容性處理。
/// </summary>
public class PrtgClientFactoryTests
{
    [Fact]
    public void Create_token模式帶入apitoken()
    {
        var settings = new SystemSettings
        {
            PrtgEnabled = true,
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("my-api-token")
        };

        Assert.True(PrtgClientFactory.HasUsableCredentials(settings));

        using var client = PrtgClientFactory.Create(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public void HasUsableCredentials_token模式無token時為false()
    {
        var settingsEmpty = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = ""
        };
        var settingsWhitespace = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = "   "
        };
        var settingsNull = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = null!
        };

        Assert.False(PrtgClientFactory.HasUsableCredentials(settingsEmpty));
        Assert.False(PrtgClientFactory.HasUsableCredentials(settingsWhitespace));
        Assert.False(PrtgClientFactory.HasUsableCredentials(settingsNull));
    }

    [Fact]
    public void HasUsableCredentials_帳密模式缺帳號或缺密碼時為false()
    {
        var noUsername = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "",
            PrtgPasswordEnc = CryptoHelper.Encrypt("secret")
        };
        var noPassword = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "admin",
            PrtgPasswordEnc = ""
        };
        var bothEmpty = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "  ",
            PrtgPasswordEnc = null!
        };
        var bothPresent = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "admin",
            PrtgPasswordEnc = CryptoHelper.Encrypt("secret")
        };

        Assert.False(PrtgClientFactory.HasUsableCredentials(noUsername));
        Assert.False(PrtgClientFactory.HasUsableCredentials(noPassword));
        Assert.False(PrtgClientFactory.HasUsableCredentials(bothEmpty));
        Assert.True(PrtgClientFactory.HasUsableCredentials(bothPresent));
    }

    [Fact]
    public void Create_密文與明文憑證都能處理()
    {
        // 驗證 IsEncrypted 守衛有效，即使資料庫/blob 內是明文也不會因 Decrypt 擲出例外
        var settingsPlainText = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = "plain-text-token"
        };

        var settingsEncrypted = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("secret-token")
        };

        using var clientPlain = PrtgClientFactory.Create(settingsPlainText);
        Assert.NotNull(clientPlain);

        using var clientEnc = PrtgClientFactory.Create(settingsEncrypted);
        Assert.NotNull(clientEnc);

        // 密碼模式下明文與密文也能處理
        var settingsPassPlain = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "admin",
            PrtgPasswordEnc = "plain-password"
        };
        using var clientPassPlain = PrtgClientFactory.Create(settingsPassPlain);
        Assert.NotNull(clientPassPlain);
    }

    [Fact]
    public void ResolveCredentials_解密真的發生且分支沒有接反()
    {
        // 工廠的主要職責是「依模式解密憑證」——只斷言 Create 不擲例外擋不住
        // 「漏掉 Decrypt 把密文當明文送出」或「token/password 分支接反」這兩種回歸
        var tokenSettings = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("plain-token-123"),
            PrtgPasswordEnc = CryptoHelper.Encrypt("should-not-be-used")
        };
        var (token, username, password) = PrtgClientFactory.ResolveCredentials(tokenSettings);
        Assert.Equal("plain-token-123", token);
        Assert.Equal("", username);
        Assert.Equal("", password);

        var passwordSettings = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "operator",
            PrtgPasswordEnc = CryptoHelper.Encrypt("plain-pass-456"),
            PrtgApiTokenEnc = CryptoHelper.Encrypt("should-not-be-used")
        };
        var (token2, username2, password2) = PrtgClientFactory.ResolveCredentials(passwordSettings);
        Assert.Equal("", token2);
        Assert.Equal("operator", username2);
        Assert.Equal("plain-pass-456", password2);

        // 明文相容（IsEncrypted 守衛）：blob 被手動編輯成明文時原樣使用、不擲例外
        var plainSettings = new SystemSettings
        {
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = "raw-plain-token"
        };
        Assert.Equal("raw-plain-token", PrtgClientFactory.ResolveCredentials(plainSettings).Token);
    }

    [Fact]
    public void Create_認證方式有真的傳進client()
    {
        // `Assert.NotNull(client)` 對不可為 null 的回傳型別是恆真斷言，證明不了任何事。
        // 這裡改用一個真的會因 authMode 而異的行為：帳密模式缺帳號時建構期就擲例外，
        // token 模式則不會——若工廠把 authMode 傳錯或漏傳，這條就會紅。
        var passwordModeNoUser = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Password,
            PrtgUsername = "",
            PrtgPasswordEnc = CryptoHelper.Encrypt("pw")
        };
        Assert.Throws<PrtgClientException>(() => PrtgClientFactory.Create(passwordModeNoUser));

        var tokenModeNoUser = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgUsername = "",
            PrtgApiTokenEnc = CryptoHelper.Encrypt("tk")
        };
        using var client = PrtgClientFactory.Create(tokenModeNoUser);
        Assert.NotNull(client);
    }

    [Fact]
    public void Create_逾時與SSL選項取自設定()
    {
        // 證明設定值真的有流進 client，而不是用了預設值
        var settings = new SystemSettings
        {
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = CryptoHelper.Encrypt("tk"),
            PrtgTimeoutSeconds = 123,
            PrtgIgnoreSslErrors = true
        };

        using var client = PrtgClientFactory.Create(settings);

        Assert.Equal(TimeSpan.FromSeconds(123), client.Timeout);
        var handler = Assert.IsType<SocketsHttpHandler>(client.Handler);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
    }
}
