using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgAddressTests
{
    [Fact]
    public void Normalize_IP加port_去掉port回傳IP()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize("10.1.2.3:8080"));
    }

    [Fact]
    public void Normalize_https加port加斜線_去掉scheme與port回傳IP()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize("https://10.1.2.3:443/"));
    }

    [Fact]
    public void Normalize_http加路徑_去掉scheme與路徑回傳IP()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize("http://10.1.2.3/api/table.json"));
    }

    [Fact]
    public void Normalize_前導零與前後空白_正規化後消除前導零()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize(" 010.001.002.003 "));
    }

    [Fact]
    public void Normalize_純IP_原樣回傳()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize("10.1.2.3"));
    }

    [Fact]
    public void Normalize_IPv6中括號加port_取出中括號內容()
    {
        Assert.Equal("fe80::1", PrtgAddress.Normalize("[fe80::1]:80"));
    }

    [Fact]
    public void Normalize_裸IPv6_loopback_不拆冒號()
    {
        Assert.Equal("::1", PrtgAddress.Normalize("::1"));
    }

    [Fact]
    public void Normalize_裸IPv6_link_local_不拆冒號()
    {
        Assert.Equal("fe80::1", PrtgAddress.Normalize("fe80::1"));
    }

    [Fact]
    public void Normalize_主機名稱_回傳null()
    {
        Assert.Null(PrtgAddress.Normalize("prtg.local"));
    }

    [Fact]
    public void Normalize_主機名稱加port_回傳null()
    {
        Assert.Null(PrtgAddress.Normalize("prtg.local:8080"));
    }

    [Fact]
    public void Normalize_空字串_回傳null()
    {
        Assert.Null(PrtgAddress.Normalize(""));
    }

    [Fact]
    public void Normalize_純空白_回傳null()
    {
        Assert.Null(PrtgAddress.Normalize("   "));
    }

    [Fact]
    public void Normalize_null_回傳null()
    {
        Assert.Null(PrtgAddress.Normalize(null));
    }

    [Theory]
    [InlineData("srv-a.example.local")]
    [InlineData("srv-a")]
    [InlineData("a-b.c")]
    [InlineData("netiq.corp.local")]
    [InlineData("srv_01.corp")]
    [InlineData("1and1.example.com")]
    [InlineData("1.dc.corp.local")]        // 第一段全數字但有超過 3 字的段：真 FQDN
    [InlineData("0.pool.ntp.org")]
    [InlineData("123.example.com")]
    [InlineData("srv.corp.local.")]        // root-qualified
    public void IsDnsCandidate_像主機名稱_回true(string token)
    {
        Assert.True(PrtgAddress.IsDnsCandidate(token));
    }

    [Theory]
    [InlineData("10.2xx.x.x")]          // PRTG 裝置 host 的佔位值：第一段全數字
    [InlineData("10.2.3.256")]          // 打壞的 IPv4：沒有字母
    [InlineData("10.2.3.4.5")]
    [InlineData("10.20.3x.4")]          // 第一段全數字且每段 ≤ 3 字
    [InlineData("10.2xx.x.x.")]         // 結尾點去掉後仍是壞 IPv4
    [InlineData("srv.corp.local..")]    // 兩個結尾點：去掉一個後仍有空 label
    [InlineData("10.2.3.4 (old)")]      // 含空白與括號
    [InlineData("srv a")]
    [InlineData("-bad.host")]
    [InlineData("bad-.host")]
    [InlineData("a..b")]
    [InlineData("fe80::1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsDnsCandidate_不像主機名稱或壞掉的IPv4_回false(string? token)
    {
        Assert.False(PrtgAddress.IsDnsCandidate(token));
    }
}
