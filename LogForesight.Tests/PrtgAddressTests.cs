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
}
