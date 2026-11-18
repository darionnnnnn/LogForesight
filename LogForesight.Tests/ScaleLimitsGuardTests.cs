using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規模上限只在 <see cref="LogForesight.Core.Models.ScaleLimits"/> 一處（第 77 輪 E-2）：
/// 監看範圍的控制器、store、匯出、校準、主機全選與前端都不得再寫自己的數字——放寬一處、其他處沒跟上，
/// 就是「頁面收得下、store 卻拒絕」這種互相矛盾。
/// </summary>
public sealed class ScaleLimitsGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Theory]
    [InlineData("LogForesight.Web/Controllers/Api/PrtgMonitoringController.cs")]
    [InlineData("LogForesight.Core/Persistence/Sql/EfPrtgStore.cs")]
    [InlineData("LogForesight.Core/Service/PrtgDiagnosticExportSource.cs")]
    [InlineData("LogForesight.Core/Persistence/Sql/PrtgSqlServerIdScope.cs")]
    [InlineData("LogForesight.Web/Services/HostAdminService.cs")]
    public void 上限使用處沒有自己的數字(string file)
    {
        var code = Regex.Replace(File.ReadAllText(Path.Combine(Root(), file)), @"//.*|/\*.*?\*/", "", RegexOptions.Singleline);
        // 3000／15000／30000 與 2000（主機全選舊值）以任何寫法出現都算
        Assert.DoesNotMatch(new Regex(@"(?<![\d_.])(3[,_]?000|15[,_]?000|30[,_]?000|2[,_]?000)(?![\d_])"), code);
    }

    [Fact]
    public void 前端監看範圍選取上限讀自後端()
    {
        var js = File.ReadAllText(Path.Combine(Root(), "LogForesight.Web/wwwroot/js/pages/prtg-admin.js"));
        Assert.Contains("monitoring?.maxHosts", js);
        Assert.Contains("monitoring?.maxSensors", js);
        Assert.DoesNotMatch(new Regex(@"isHost \? \d"), js);
        Assert.DoesNotContain("3,000", js);
        Assert.DoesNotContain("15000", js);
    }
}
