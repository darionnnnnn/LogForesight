using System.Reflection;
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

namespace LogForesight.Tests.Authorization;

/// <summary>站台上的一條 action：鍵、HTTP 方法、路由樣板，以及它宣告的授權標註（類別層＋方法層）。</summary>
public sealed record ActionInfo
{
    /// <summary>「方法 /路由樣板」，與路由快照基準檔、讀取矩陣的鍵格式相同</summary>
    public required string Key { get; init; }
    public required string Method { get; init; }
    public required string Route { get; init; }

    /// <summary>
    /// 每個 <c>[Permission(...)]</c>（類別層與方法層各自、可多個）是一組「任一即可」的能力；
    /// <c>PermissionFilter</c> 每個標註各跑一次，所以組與組之間是「都要滿足」。
    /// </summary>
    public required IReadOnlyList<Capability[]> PermissionGroups { get; init; }

    /// <summary>有 <c>[RequireViewAll]</c>（類別層或方法層）</summary>
    public bool RequireViewAll { get; init; }

    public bool AllowAnonymous { get; init; }

    /// <summary>沒有預設值、不在路由裡的簡單型別參數（會從查詢字串綁定）：缺了會在 <c>RequireViewAll</c> 這類 action filter 之前就被模型驗證回 400</summary>
    public IReadOnlyList<(string Name, Type Type)> RequiredQuery { get; init; } = Array.Empty<(string, Type)>();

    /// <summary>[Consumes] 宣告的內容型別（空＝JSON）；內容型別不符時請求根本進不了 action（415），閘門測試要送對的型別才驗得到</summary>
    public IReadOnlyList<string> Consumes { get; init; } = Array.Empty<string>();

    /// <summary>非 /api 的 MVC 頁面（沒有能力時是導向 /access-denied，不是 403 信封）</summary>
    public bool IsPage => !Route.StartsWith("/api/", StringComparison.Ordinal);

    public bool IsWrite => Method is "POST" or "PUT" or "DELETE" or "PATCH";

    /// <summary>有任何能力閘門（Permission 或 RequireViewAll）</summary>
    public bool IsAnnotated => PermissionGroups.Count > 0 || RequireViewAll;

    /// <summary>身分是否通過所有能力閘門（ViewAll 標註＝一組只含 ViewAll 的要求）</summary>
    public bool Passes(IReadOnlySet<Capability> caps) =>
        PermissionGroups.All(g => g.Any(caps.Contains)) && (!RequireViewAll || caps.Contains(Capability.ViewAll));

    public override string ToString() => Key;
}

/// <summary>
/// 站台 action 的列舉，由 ASP.NET Core 的 <see cref="IActionDescriptorCollectionProvider"/> 完成
/// （不手寫反射組路由），並讀出每條 action 的授權標註。涵蓋守門、角色閘門與匿名端點守門共用這一份。
/// </summary>
public static class ActionCatalog
{
    private static readonly Lazy<IReadOnlyList<ActionInfo>> All = new(Load);

    public static IReadOnlyList<ActionInfo> Actions => All.Value;

    private static IReadOnlyList<ActionInfo> Load()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddControllersWithViews().AddApplicationPart(typeof(SettingsController).Assembly);
        using var provider = services.BuildServiceProvider();

        var result = new List<ActionInfo>();
        foreach (var d in provider.GetRequiredService<IActionDescriptorCollectionProvider>()
                     .ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var route = "/" + (d.AttributeRouteInfo?.Template ?? $"{{conventional:{d.ControllerName}/{d.ActionName}}}").TrimStart('/');
            var methods = d.EndpointMetadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (methods.Count == 0) methods.Add("ANY");

            var groups = new List<Capability[]>();
            foreach (var attr in d.ControllerTypeInfo.GetCustomAttributes<PermissionAttribute>(inherit: true)
                         .Concat(d.MethodInfo.GetCustomAttributes<PermissionAttribute>(inherit: true)))
                groups.Add((Capability[])attr.Arguments![0]!);

            var viewAll = d.ControllerTypeInfo.GetCustomAttribute<RequireViewAllAttribute>(inherit: true) != null
                          || d.MethodInfo.GetCustomAttribute<RequireViewAllAttribute>(inherit: true) != null;
            var anonymous = d.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            // 含 ApiController 由 IFormFile／[FromForm] 參數推論出的 multipart/form-data（它出現在 filter 描述而不是端點中繼資料）
            var consumes = d.FilterDescriptors.Select(f => f.Filter).OfType<ConsumesAttribute>()
                .SelectMany(c => c.ContentTypes.Select(t => t.ToString())).ToList();
            var routeNames = Placeholder.Matches(route).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var query = d.Parameters.OfType<ControllerParameterDescriptor>()
                .Where(p => !routeNames.Contains(p.Name) && !p.ParameterInfo.HasDefaultValue
                            && p.BindingInfo?.BindingSource != Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body
                            && p.BindingInfo?.BindingSource != Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Services
                            && p.BindingInfo?.BindingSource != Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Form
                            && p.BindingInfo?.BindingSource != Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Special
                            && IsSimple(p.ParameterType))
                .Select(p => (p.Name, p.ParameterType)).ToList();

            foreach (var m in methods)
            {
                var method = m.ToUpperInvariant();
                result.Add(new ActionInfo
                {
                    Key = $"{method} {route}",
                    Method = method,
                    Route = route,
                    PermissionGroups = groups,
                    RequireViewAll = viewAll,
                    AllowAnonymous = anonymous,
                    RequiredQuery = query,
                    Consumes = consumes
                });
            }
        }

        return result;
    }

    private static bool IsSimple(Type t) =>
        (Nullable.GetUnderlyingType(t) ?? t) is { } u && (u.IsPrimitive || u == typeof(string) || u == typeof(DateTime) || u == typeof(Guid) || u == typeof(decimal));

    private static readonly Regex Placeholder = new(@"\{(\w+)(?::(\w+))?\}", RegexOptions.Compiled);

    public static string BuildUrl(ActionInfo action)
    {
        var url = BuildUrl(action.Route);
        if (action.RequiredQuery.Count == 0) return url;
        return url + "?" + string.Join("&", action.RequiredQuery.Select(q =>
            $"{q.Name}={QueryValue(Nullable.GetUnderlyingType(q.Type) ?? q.Type)}"));
    }

    private static string QueryValue(Type t) =>
        t == typeof(string) ? "x"
        : t == typeof(bool) ? "false"
        : t == typeof(DateTime) ? AuthorizationHttpFixture.DayText
        : t == typeof(Guid) ? Guid.NewGuid().ToString()
        : "1";

    /// <summary>把路由樣板的占位符代入合法的值（數字 1、日期＝夾具的風險日、guid、其餘字串 x）</summary>
    public static string BuildUrl(string route) => Placeholder.Replace(route, m =>
    {
        var name = m.Groups[1].Value;
        var constraint = m.Groups[2].Value;
        if (name == "date") return AuthorizationHttpFixture.DayText;
        return constraint switch
        {
            "long" or "int" => "1",
            "guid" => Guid.NewGuid().ToString(),
            _ => "x"
        };
    });
}
