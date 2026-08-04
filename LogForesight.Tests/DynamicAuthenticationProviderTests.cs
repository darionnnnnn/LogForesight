using LogForesight.Web.Auth;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/HISTORY.md #9：DynamicAuthenticationProvider 的切換邏輯。
/// LdapService 本體依賴 DirectoryEntry／實際網路 bind，難以單元測試（也不該測，見計畫書）；
/// 這裡只測「AD 開關開/關、伺服器清單空/非空」時是否正確路由到 AD 路徑或 fallback，
/// 不觸發任何實際網路連線——用空密碼讓 LdapCredentialVerifier 在碰網路前就短路回傳，
/// 藉此證明「確實進了 AD 路徑」而非委派給 fallback。
/// </summary>
public class DynamicAuthenticationProviderTests
{
    private readonly FakeSystemSettingsStore _store = new();
    private readonly FakeAuthenticationProvider _fallback = new();

    private DynamicAuthenticationProvider Create() => new(_store, _fallback);

    [Fact]
    public void AD停用時_Verify委派給fallback()
    {
        _store.Update(s => s.AdAuthEnabled = false);
        var provider = Create();

        var result = provider.Verify("user", "any-password");

        Assert.Equal(1, _fallback.VerifyCalls);
        Assert.Same(_fallback.ResultToReturn, result);
    }

    [Fact]
    public void AD啟用但伺服器清單為空時_Verify委派給fallback()
    {
        _store.Update(s => { s.AdAuthEnabled = true; s.AdServers = new List<string>(); });
        var provider = Create();

        provider.Verify("user", "any-password");

        Assert.Equal(1, _fallback.VerifyCalls);
    }

    [Fact]
    public void AD啟用且伺服器非空時_Verify不委派給fallback_改走AD路徑()
    {
        _store.Update(s =>
        {
            s.AdAuthEnabled = true;
            s.AdServers = new List<string> { "LDAP://fake-dc.invalid" };
        });
        var provider = Create();

        // 空密碼讓 LdapCredentialVerifier 在碰網路前就短路回傳「密碼為空」，
        // 藉此驗證路由到了 AD 路徑，而不需要真的連線到任何 AD 伺服器
        var result = provider.Verify("user", null);

        Assert.Equal(0, _fallback.VerifyCalls);
        Assert.False(result.Success);
        Assert.Equal("密碼為空", result.FailureReason);
    }

    [Fact]
    public void RequiresPassword_AD停用時委派給fallback()
    {
        _fallback.RequiresPasswordValue = false;
        _store.Update(s => s.AdAuthEnabled = false);

        Assert.False(Create().RequiresPassword);
    }

    [Fact]
    public void RequiresPassword_AD啟用且有伺服器時一律true_不論fallback為何()
    {
        _fallback.RequiresPasswordValue = false;   // 即使底層 fallback 是 Stub（不驗密碼）
        _store.Update(s =>
        {
            s.AdAuthEnabled = true;
            s.AdServers = new List<string> { "LDAP://fake-dc.invalid" };
        });

        Assert.True(Create().RequiresPassword);
    }

    [Fact]
    public void RequiresPassword_AD啟用但伺服器清單為空時委派給fallback()
    {
        _fallback.RequiresPasswordValue = false;
        _store.Update(s => { s.AdAuthEnabled = true; s.AdServers = new List<string>(); });

        Assert.False(Create().RequiresPassword);
    }

    [Fact]
    public void Name_反映目前生效的驗證方式()
    {
        _fallback.NameValue = "Stub";
        _store.Update(s => s.AdAuthEnabled = false);
        Assert.Equal("Stub", Create().Name);

        _store.Update(s =>
        {
            s.AdAuthEnabled = true;
            s.AdServers = new List<string> { "LDAP://fake-dc.invalid" };
        });
        Assert.Contains("AD", Create().Name);
    }

    /// <summary>設定變更即生效（S8 快照重建），不必重啟站台——關掉 AD 後下一次 Verify 立刻改走 fallback</summary>
    [Fact]
    public void 設定關閉後下一次呼叫立刻改走fallback_不必重建Provider()
    {
        _store.Update(s =>
        {
            s.AdAuthEnabled = true;
            s.AdServers = new List<string> { "LDAP://fake-dc.invalid" };
        });
        var provider = Create();
        provider.Verify("user", null);
        Assert.Equal(0, _fallback.VerifyCalls);

        _store.Update(s => s.AdAuthEnabled = false);
        provider.Verify("user", "any-password");

        Assert.Equal(1, _fallback.VerifyCalls);
    }
}

// FakeAuthenticationProvider 已搬到 TestDoubles\CurrentUserFake.cs。
