using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 全站路由／授權快照守門：拆分或搬動控制器時，路由、HTTP method、<c>[Permission]</c> 能力集合與
/// <c>[AllowAnonymous]</c> 必須與基準檔完全一致（控制器類別名只做資訊輸出，拆檔時本來就會變）。
/// 路由由 ASP.NET Core 自己的 <see cref="IActionDescriptorCollectionProvider"/> 列舉，不手寫反射組路由。
/// 基準檔更新：設定環境變數 LF_UPDATE_ROUTE_SNAPSHOT=1 後執行本測試（預設不改寫）。
/// </summary>
public class ControllerRouteSnapshotTests
{
    private const string UpdateEnvVar = "LF_UPDATE_ROUTE_SNAPSHOT";

    /// <summary>
    /// class 層與部分（或全部）action 不掛 <c>[Permission]</c> 的控制器白名單與理由。這些控制器靠 FallbackPolicy（登入即可）、Service 層可見範圍過濾，或 action 內自行檢查授權。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PermissionExempt = new Dictionary<string, string>
    {
        ["AuthController"] = "登入／登出：login、options 匿名，其餘僅需登入",
        ["MeController"] = "目前使用者自身資料：FallbackPolicy 要求登入即可",
        ["PrtgScopePurgeJobsController"] = "action 內自行檢查：Confirm／Get／Resume 皆驗作業擁有者並呼叫 PrtgScopePurgeAuthorizationService 的 Check／CheckRead（刻意不掛 Maintain，撤權後擁有者仍可讀進度）",
        ["PagesController"] = "頁面殼：只回傳 View、不帶資料，資料由 API 層授權",
        ["AiController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾；寫入類 action 另有自己的 [Permission]",
        ["DashboardController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["RunActivityController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["HostDetailController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["ReportsController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["DisplaySettingsController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["HandlersController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["HandlingController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["HealthController"] = "健康檢查：health 匿名、freshness 僅需登入",
        ["HelpController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["HostsController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["RecordsController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
        ["WorkOrderViewController"] = "讀取類 action 登入即可（FallbackPolicy），可見範圍由 Service 層 IVisibilityService 過濾",
    };

    private sealed record Row(string Method, string Route, string Caps, bool Anonymous, string Controller)
    {
        public string Comparable => $"{Method}\t{Route}\t{Caps}\t{(Anonymous ? "anonymous" : "-")}";
        public string Line => $"{Comparable}\t{Controller}";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string SnapshotPath() =>
        Path.Combine(FindRepoRoot(), "LogForesight.Tests", "Fixtures", "controller-route-snapshot.txt");

    private static IReadOnlyList<ControllerActionDescriptor> LoadActions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(SettingsController).Assembly);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
    }

    private static string CapabilitiesOf(IEnumerable<PermissionAttribute> attrs)
    {
        // 同一範圍可疊多個屬性（AND 語意）；單一屬性內的多個能力為 OR
        var parts = attrs
            .Select(a => string.Join("|", ((Capability[])a.Arguments![0]).Select(c => c.ToString()).OrderBy(s => s, StringComparer.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        return parts.Count == 0 ? "-" : string.Join("&", parts);
    }

    private static string CapsOf(ControllerActionDescriptor d)
    {
        var cls = d.ControllerTypeInfo.GetCustomAttributes<PermissionAttribute>(inherit: true);
        var act = d.MethodInfo.GetCustomAttributes<PermissionAttribute>(inherit: true);
        return $"C={CapabilitiesOf(cls)};A={CapabilitiesOf(act)}";
    }

    private static IReadOnlyList<Row> BuildRows()
    {
        var rows = new List<Row>();
        foreach (var d in LoadActions())
        {
            var route = "/" + (d.AttributeRouteInfo?.Template ?? $"{{conventional:{d.ControllerName}/{d.ActionName}}}").TrimStart('/');
            var methods = d.EndpointMetadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (methods.Count == 0) methods.Add("ANY");
            var anon = d.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            foreach (var m in methods)
            {
                rows.Add(new Row(m.ToUpperInvariant(), route, CapsOf(d), anon, d.ControllerTypeInfo.Name));
            }
        }

        return rows.OrderBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
            .ThenBy(r => r.Controller, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void 全站路由與授權快照與基準檔一致()
    {
        var rows = BuildRows();
        Assert.NotEmpty(rows);
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", rows.Select(r => r.Line)) + "\n", new UTF8Encoding(false));
        }

        Assert.True(File.Exists(path), $"找不到基準檔 {path}；設定 {UpdateEnvVar}=1 重新產生");
        // 控制器名（第 5 欄）只供資訊，不比對
        var baseline = File.ReadAllText(path).Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => string.Join("\t", l.Split('\t').Take(4)))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var current = rows.Select(r => r.Comparable).OrderBy(s => s, StringComparer.Ordinal).ToList();

        // 多重集合差異：重複列（同 method 同路由）也要抓得到
        var removed = Subtract(baseline, current);
        var added = Subtract(current, baseline);
        var diff = new StringBuilder();
        diff.AppendLine("路由／授權快照與基準檔不一致（格式：METHOD、路由、能力集合 C=class層;A=action層、匿名）。");
        diff.AppendLine("基準檔有、現況沒有：");
        foreach (var s in removed) diff.AppendLine("  - " + s);
        diff.AppendLine("現況有、基準檔沒有：");
        foreach (var s in added) diff.AppendLine("  + " + s);
        Assert.True(removed.Count == 0 && added.Count == 0, diff.ToString());
    }

    private static List<string> Subtract(IEnumerable<string> left, IEnumerable<string> right)
    {
        var counts = right.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var s in left)
        {
            if (counts.TryGetValue(s, out var n) && n > 0) counts[s] = n - 1;
            else result.Add(s);
        }

        return result;
    }

    [Fact]
    public void 每個控制器都有授權宣告()
    {
        var offenders = new List<string>();
        foreach (var group in LoadActions().GroupBy(d => d.ControllerTypeInfo))
        {
            var type = group.Key;
            if (type.GetCustomAttributes<PermissionAttribute>(inherit: true).Any()) continue;
            if (group.All(d => d.MethodInfo.GetCustomAttributes<PermissionAttribute>(inherit: true).Any())) continue;
            if (PermissionExempt.ContainsKey(type.Name)) continue;
            offenders.Add(type.Name);
        }

        Assert.True(offenders.Count == 0,
            "下列控制器沒有 [Permission]（class 層或每個 action），也不在白名單：" + string.Join("、", offenders));

        // 白名單不得過期：列在白名單的控制器必須真的存在
        var existing = typeof(SettingsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)).Select(t => t.Name).ToHashSet();
        foreach (var name in PermissionExempt.Keys)
        {
            Assert.True(existing.Contains(name), $"白名單的 {name} 已不存在，請移除");
        }
    }

    [Fact]
    public void 一檔一控制器且檔名等於類別名()
    {
        var dir = Path.Combine(FindRepoRoot(), "LogForesight.Web", "Controllers");
        var pattern = new Regex(@"^\s*(?:public|internal)\s+(?:(?:sealed|abstract|partial|static)\s+)*class\s+(\w+)[^\r\n{]*?:\s*(?:ControllerBase|Controller)\b",
            RegexOptions.Multiline);
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var declared = pattern.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Distinct().ToList();
            if (declared.Count == 0) continue;
            if (declared.Count != 1 || declared[0] != name)
            {
                problems.Add($"{Path.GetRelativePath(dir, file)}：宣告 {string.Join("、", declared)}");
            }
        }

        Assert.True(problems.Count == 0, "控制器檔必須恰宣告一個控制器且類別名＝檔名：\n" + string.Join("\n", problems));
    }
}
