using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/HISTORY.md S8：「設定快照 → 重建客戶端」共用工具。
/// 取代 WebAiService 原本互動／對話情境各自寫一份的 lock+快照比對邏輯；
/// #9 的 AD 動態驗證是第三個使用點，快照形狀與 WebAiService 的 (BaseUrl, KeyEnc) 不同
/// （AD 是伺服器清單＋SearchBase／Filter），因此泛型化為任意快照型別——這裡直接測泛型工具本身，
/// 「未設定回 null」的判斷交給 factory 自行決定，不預設任何快照形狀。
/// </summary>
public class SettingsBoundClientTests
{
    [Fact]
    public void Factory回null時_Get也回null_不快取()
    {
        var factoryCalls = 0;
        var client = new SettingsBoundClient<string, object>(snapshot =>
        {
            factoryCalls++;
            return string.IsNullOrWhiteSpace(snapshot) ? null : new object();
        });

        var result = client.Get("");

        Assert.Null(result);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void 快照相同時回快取實例_不重建()
    {
        var factoryCalls = 0;
        var client = new SettingsBoundClient<(string Url, string Key), object>(_ => { factoryCalls++; return new object(); });

        var first = client.Get(("http://host", "enc-key"));
        var second = client.Get(("http://host", "enc-key"));

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Theory]
    [InlineData("http://host-a", "enc-key", "http://host-b", "enc-key")]
    [InlineData("http://host", "enc-key-1", "http://host", "enc-key-2")]
    public void 快照變動時重建(string url1, string key1, string url2, string key2)
    {
        var factoryCalls = 0;
        var client = new SettingsBoundClient<(string, string), object>(_ => { factoryCalls++; return new object(); });

        var first = client.Get((url1, key1));
        var second = client.Get((url2, key2));

        Assert.NotSame(first, second);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void Factory收到目前傳入的快照()
    {
        (string Url, string Key)? received = null;
        var client = new SettingsBoundClient<(string, string), object>(snapshot =>
        {
            received = snapshot;
            return new object();
        });

        client.Get(("http://host", "enc-key"));

        Assert.Equal(("http://host", "enc-key"), received);
    }

    /// <summary>非 ValueTuple 的快照形狀（例如 AD 用的伺服器清單陣列）也要能正確比對——
    /// 用陣列內容相等（SequenceEqual 包一層 record）驗證，不侷限於 WebAiService 的 (string,string)</summary>
    [Fact]
    public void 快照可為任意型別_record包裝的清單也能正確比對()
    {
        var factoryCalls = 0;
        var client = new SettingsBoundClient<AdSnapshot, object>(_ => { factoryCalls++; return new object(); });

        var first = client.Get(new AdSnapshot(new List<string> { "dc1", "dc2" }));
        var second = client.Get(new AdSnapshot(new List<string> { "dc1", "dc2" }));
        var third = client.Get(new AdSnapshot(new List<string> { "dc3" }));

        Assert.Same(first, second);
        Assert.NotSame(second, third);
        Assert.Equal(2, factoryCalls);
    }

    private sealed record AdSnapshot(List<string> Servers)
    {
        public bool Equals(AdSnapshot? other) => other != null && Servers.SequenceEqual(other.Servers);
        public override int GetHashCode() => Servers.Count;
    }
}
