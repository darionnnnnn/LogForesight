using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgHostnameCandidatesTests
{
    [Theory]
    [InlineData("U(O-stkomsdb1)216.45.101", null, new[] { "o-stkomsdb1", "stkomsdb1" })]
    [InlineData("W(VM-GFYQ-AIS002)_216.11.10", null, new[] { "vm-gfyq-ais002" })]
    [InlineData("U(L-TP-Sentinel-SRVS05)216.7.117", null, new[] { "l-tp-sentinel-srvs05", "tp-sentinel-srvs05" })]
    public void Extract_三種現場名稱產生命中候選(string deviceName, string? hostField, string[] expected)
    {
        var result = PrtgHostnameCandidates.Extract(deviceName, hostField);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Extract_無括號時使用整個名稱()
    {
        var result = PrtgHostnameCandidates.Extract("SRV-APP01", null);
        Assert.Equal(new[] { "srv-app01" }, result);
    }

    [Theory]
    [InlineData("Device (first) (second)", new[] { "first" })]
    [InlineData("Device () (non-empty) ()", new[] { "non-empty" })]
    public void Extract_多對括號時取第一對非空(string deviceName, string[] expected)
    {
        var result = PrtgHostnameCandidates.Extract(deviceName, null);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Device ()")]
    [InlineData("Device (   )")]
    [InlineData("()")]
    public void Extract_空括號時無候選a(string deviceName)
    {
        var result = PrtgHostnameCandidates.Extract(deviceName, null);
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_VM開頭不去前綴()
    {
        var result = PrtgHostnameCandidates.Extract("W(VM-GFYQ-AIS002)", null);
        Assert.Equal(new[] { "vm-gfyq-ais002" }, result);
    }

    [Theory]
    [InlineData("U(O-stkomsdb1)", new[] { "o-stkomsdb1", "stkomsdb1" })]
    [InlineData("U(A-SERVER)", new[] { "a-server", "server" })]
    [InlineData("U(z-database)", new[] { "z-database", "database" })]
    public void Extract_單字母前綴去除(string deviceName, string[] expected)
    {
        var result = PrtgHostnameCandidates.Extract(deviceName, null);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1:8080")]
    [InlineData("http://172.16.0.1/")]
    public void Extract_host欄為IP不列入(string hostField)
    {
        var result = PrtgHostnameCandidates.Extract("Device", hostField);
        Assert.Equal(new[] { "device" }, result);

        var resultNoDevice = PrtgHostnameCandidates.Extract(null, hostField);
        Assert.Empty(resultNoDevice);
    }

    [Fact]
    public void Extract_host欄為FQDN時正規化並加入()
    {
        var result = PrtgHostnameCandidates.Extract("Device", "srv01.corp.local");
        Assert.Equal(new[] { "device", "srv01" }, result);

        var resultOnlyHost = PrtgHostnameCandidates.Extract(null, "srv01.corp.local");
        Assert.Equal(new[] { "srv01" }, resultOnlyHost);
    }

    [Theory]
    [InlineData("http://SRV01.corp.local:8080/api/v1", "srv01")]
    [InlineData("https://Web02.domain.com:443", "web02")]
    [InlineData("SRV-DB.internal", "srv-db")]
    [InlineData("host-name", "host-name")]
    [InlineData("192.168.1.1", null)]
    [InlineData("10.0.0.1:8080", null)]
    [InlineData("http://192.168.1.1/", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ShortName_去Scheme與Port與域名與大小寫且IP回null(string? input, string? expected)
    {
        var result = PrtgHostnameCandidates.ShortName(input);
        Assert.Equal(expected, result);
    }
}
