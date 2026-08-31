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
}
