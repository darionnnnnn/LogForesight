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
        // 先剝區塊註解（可跨行），再逐行剝 // 註解；兩者不能合成一條 Singleline，否則 //.* 會從第一個註解吃到檔尾、守門恆綠
        var code = Regex.Replace(File.ReadAllText(Path.Combine(Root(), file)), @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^
]*", "");
        // 語意不同的獨立數字：近期狀態佇列容量（以裝置計）、診斷匯出數值分頁大小
        code = Regex.Replace(code, @"const int (RecentStateChangeQueueCapacity|ValuePageSize) = [\d_]+;", "");
        Assert.True(code.Length > 1000, $"{file} 剝掉註解後只剩 {code.Length} 字元，剝除規則有誤");
        // 3000／15000／30000 與 2000（主機全選舊值）以任何寫法出現都算
        Assert.DoesNotMatch(new Regex(@"(?<![\d_.])(3[,_]?000|15[,_]?000|30[,_]?000|2[,_]?000)(?![\d_])"), code);
    }

    /// <summary>頁面提示的上限數字由前端依 API 回傳填入，view 不得寫死</summary>
    [Fact]
    public void 監看範圍頁面提示沒有寫死上限()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "LogForesight.Web/Views/Pages/Prtg.cshtml"));
        Assert.DoesNotMatch(new Regex(@"(?<![\d_.])(3[,_]?000|15[,_]?000|10[,_]?000)(?![\d_])\s*(台|顆)"), view);
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
