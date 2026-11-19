using LogForesight.Core;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 79 輪批次 G8：站台模式下沒有可用的站台金鑰時拒絕加密，不靜默改用公開的內嵌金鑰</summary>
[Collection("CryptoKeyState")]
public class CryptoSiteKeyUnavailableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-sitekey-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(CryptoHelper.EnvVarName);

    public CryptoSiteKeyUnavailableTests()
    {
        Environment.SetEnvironmentVariable(CryptoHelper.EnvVarName, null);
        CryptoHelper.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CryptoHelper.EnvVarName, _savedEnv);
        CryptoHelper.ResetForTests();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void 站台模式且金鑰檔不存在_加密被拒並列入健康原因()
    {
        CryptoHelper.UseKeyFile(Path.Combine(_dir, "missing.key"));
        Assert.True(CryptoHelper.SiteKeyUnavailable);
        Assert.Throws<CryptoKeyUnavailableException>(() => CryptoHelper.Encrypt("secret"));
        Assert.Equal("", CryptoHelper.Encrypt(""));   // 空值不需要金鑰，照舊
        var reasons = HealthAttention.Evaluate(new HealthAttentionInput { CryptoKeyUnavailable = CryptoHelper.SiteKeyUnavailable }, DateTime.Now);
        Assert.Contains(reasons, r => r.Key == HealthAttentionKeys.Crypto);
    }

    [Fact]
    public void 站台模式且金鑰檔存在_照常加密()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "site.key");
        File.WriteAllText(path, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        CryptoHelper.UseKeyFile(path);
        Assert.False(CryptoHelper.SiteKeyUnavailable);
        Assert.NotEqual("secret", CryptoHelper.Encrypt("secret"));
    }

    [Fact]
    public void 未設定金鑰檔路徑的非站台情境_維持內嵌金鑰行為()
    {
        Assert.False(CryptoHelper.SiteKeyUnavailable);
        Assert.NotEqual("secret", CryptoHelper.Encrypt("secret"));
    }
}
