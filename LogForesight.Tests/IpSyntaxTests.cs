using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

public class IpSyntaxTests
{
    [Theory]
    [InlineData("10.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("12345", false)]
    [InlineData("0", false)]
    [InlineData("0x7f.1", false)]
    [InlineData("0x7f.0.0.1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3.a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1.2.3.4", true)]
    [InlineData("010.001.002.003", true)]
    [InlineData(" 1.2.3.4 ", false)]
    [InlineData("1.2.3.4\t", false)]
    public void IsStrictIpv4(string? value, bool expected)
    {
        Assert.Equal(expected, IpSyntax.IsStrictIpv4(value));
    }

    /// <summary>Sentinel 存的是標準寫法：前導零的清單值要先標準化再進子句與事件歸戶，否則永遠查無資料。</summary>
    [Theory]
    [InlineData("010.001.002.003", "10.1.2.3")]
    [InlineData("1.2.3.04", "1.2.3.4")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("10.1", null)]
    [InlineData(" 1.2.3.4", null)]
    [InlineData(null, null)]
    public void CanonicalIpv4(string? value, string? expected)
    {
        Assert.Equal(expected, IpSyntax.CanonicalIpv4(value));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("1.2.3.4")]
    [InlineData(" 1.2.3.4 ")]
    public void TryParse_合法位址_成功(string value)
    {
        Assert.True(IpSyntax.TryParse(value, out var address));
        Assert.NotNull(address);
    }

    [Theory]
    [InlineData("10.1")]
    [InlineData("12345")]
    [InlineData("0x7f.1")]
    [InlineData(null)]
    public void TryParse_簡寫或空值_失敗(string? value)
    {
        Assert.False(IpSyntax.TryParse(value, out var address));
        Assert.Null(address);
    }

    /// <summary>
    /// 自由輸入的位址只能走 IpSyntax。禁用由編譯器以符號比對執行（根目錄 BannedSymbols.txt，RS0030 視為錯誤）；
    /// 這裡只守住清單本身沒被刪改。
    /// </summary>
    [Theory]
    [InlineData("M:System.Net.IPAddress.TryParse(System.String,System.Net.IPAddress@)")]
    [InlineData("M:System.Net.IPAddress.TryParse(System.ReadOnlySpan{System.Char},System.Net.IPAddress@)")]
    [InlineData("M:System.Net.IPAddress.Parse(System.String)")]
    [InlineData("M:System.Net.IPAddress.Parse(System.ReadOnlySpan{System.Char})")]
    [InlineData("T:System.Diagnostics.EventLogEntryType")]
    public void 禁用清單涵蓋框架的IP解析與Windows專屬事件型別(string symbol)
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), "BannedSymbols.txt"));
        Assert.Contains(lines, line => line.Split(';')[0] == symbol);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "LogForesight.Core"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到專案根目錄");
    }
}
