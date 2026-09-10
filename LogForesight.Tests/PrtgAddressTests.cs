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

    [Theory]
    [InlineData("srv.corp.local.", "srv.corp.local")]
    [InlineData("https://SRV.corp.local.:8443/", "srv.corp.local")]
    [InlineData(".", ".")]                       // 單一個點不剝成空字串
    [InlineData("srv.corp.local..", "srv.corp.local.")]   // 只剝一個
    public void HostToken_只去掉單一個結尾點_三層共用同一把鍵(string input, string expected)
    {
        Assert.Equal(expected, PrtgAddress.HostToken(input));
    }

    [Fact]
    public void Normalize_IP帶結尾點_視為該IP()
    {
        Assert.Equal("10.1.2.3", PrtgAddress.Normalize("10.1.2.3."));
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
    [InlineData("1.dc.corp.local")]        // 第一段全數字但有非 x 字母：真 FQDN
    [InlineData("0.pool.ntp.org")]
    [InlineData("123.example.com")]
    [InlineData("163.com")]                // 短標籤真網域
    [InlineData("104.com.tw")]
    [InlineData("1.dc.hq.tw")]
    [InlineData("10.2.3.4-old")]           // 有非 x 字母就放行：寧可多查一次
    [InlineData("SRV.CORP.LOCAL.")]        // root-qualified、大寫
    public void IsDnsCandidate_像主機名稱_回true(string token)
    {
        Assert.True(PrtgAddress.IsDnsCandidate(token));
    }

    [Theory]
    [InlineData("10.2xx.x.x")]          // PRTG 裝置 host 的佔位值：第一段全數字
    [InlineData("10.2.3.256")]          // 打壞的 IPv4：沒有字母
    [InlineData("10.2.3.4.5")]
    [InlineData("10.20.3x.4")]          // 第一段全數字且每段只有數字或 x
    [InlineData("192.168.1.100x")]
    [InlineData("10.2xxx.x.x")]
    [InlineData("10.2.3.4x")]
    [InlineData("10.2xx.x.x.")]         // 結尾點去掉後仍是壞 IPv4
    [InlineData(".a.b")]
    [InlineData("10..2.x")]
    [InlineData(".")]
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
