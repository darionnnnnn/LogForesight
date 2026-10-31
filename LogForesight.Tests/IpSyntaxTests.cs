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
    [InlineData(" 1.2.3.4 ", true)]
    public void IsStrictIpv4(string? value, bool expected)
    {
        Assert.Equal(expected, IpSyntax.IsStrictIpv4(value));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("1.2.3.4")]
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

    /// <summary>自由輸入的位址只能走 IpSyntax；框架的 IPAddress.TryParse 會把簡寫當成合法 IPv4。</summary>
    [Fact]
    public void 守門_IPAddressTryParse只能出現在白名單檔()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IpSyntax.cs", "NetiqHostList.cs", "LoginThrottle.cs", "AppSettings.cs", "Program.cs", "PrtgProbeRunner.cs"
        };
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
                if (allowed.Contains(Path.GetFileName(file))) continue;
                if (File.ReadAllText(file).Contains("IPAddress.TryParse(", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file));
            }
        }
        Assert.True(offenders.Count == 0, "改用 IpSyntax.TryParse：" + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "LogForesight.Core"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到專案根目錄");
    }
}
