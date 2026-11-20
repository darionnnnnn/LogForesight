using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門：驗證已改用內建 Cookie Authentication（第 79 輪 L），套件清單不得再出現 JwtBearer。
/// 範圍是中央套件版本檔與三個專案檔；掃描函式以反例字串證明「出現就會紅」。
/// </summary>
public class PackageReferenceGuardTests
{
    private const string ForbiddenPackage = "Microsoft.AspNetCore.Authentication.JwtBearer";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static readonly string[] ManifestFiles =
    [
        "Directory.Packages.props",
        "LogForesight.Core/LogForesight.Core.csproj",
        "LogForesight.Web/LogForesight.Web.csproj",
        "LogForesight.Tests/LogForesight.Tests.csproj"
    ];

    /// <summary>內容中提到禁用套件的行（不分大小寫；註解行也算，避免以註解殘留當成「已移除」）。</summary>
    private static List<string> ForbiddenLines(string content) =>
        content.Split('\n').Where(l => l.Contains(ForbiddenPackage, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim()).ToList();

    [Fact]
    public void 套件清單與三個專案檔不含JwtBearer()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var relative in ManifestFiles)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"找不到 {relative}，守門範圍失準");
            offenders.AddRange(ForbiddenLines(File.ReadAllText(path)).Select(l => $"{relative}：{l}"));
        }
        Assert.True(offenders.Count == 0, "套件清單仍出現 JwtBearer：" + string.Join("；", offenders));
    }

    [Theory]
    [InlineData("<PackageVersion Include=\"Microsoft.AspNetCore.Authentication.JwtBearer\" Version=\"10.0.0\" />")]
    [InlineData("<PackageReference Include=\"microsoft.aspnetcore.authentication.jwtbearer\" />")]
    [InlineData("<!-- <PackageReference Include=\"Microsoft.AspNetCore.Authentication.JwtBearer\" /> -->")]
    public void 反例_含JwtBearer的內容會被守門抓到(string content) =>
        Assert.Single(ForbiddenLines("<Project>\n  " + content + "\n  <PackageReference Include=\"NLog\" />\n</Project>"));

    [Fact]
    public void 反例的對照組_不含JwtBearer的內容不會被抓()
    {
        Assert.Empty(ForbiddenLines("<PackageReference Include=\"Microsoft.AspNetCore.Authentication.Cookies\" />"));
    }
}
