using System.Reflection;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>權限異動 API 的能力標註守門：讀取端點與寫入端點都要 ConfirmPermission。</summary>
public class PermissionChangesAuthorizationTests
{
    private static readonly Type Controller = typeof(PermissionChangesController);

    private static bool RequiresConfirmPermission(MemberInfo m) =>
        m.GetCustomAttributes<PermissionAttribute>(true).Any(a =>
            a.Arguments is { Length: > 0 } && a.Arguments[0] is Capability[] caps && caps.Contains(Capability.ConfirmPermission));

    [Fact]
    public void 所有HttpGet端點都要求ConfirmPermission()
    {
        var gets = Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpGetAttribute>().Any()).ToList();
        Assert.Equal(4, gets.Count);
        foreach (var m in gets)
            Assert.True(RequiresConfirmPermission(m) || RequiresConfirmPermission(Controller), m.Name);
    }

    [Fact]
    public void 寫入端點維持原本的ConfirmPermission標註()
    {
        var writes = Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a is not HttpGetAttribute))
            .Select(m => m.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Confirm", "ConfirmBatch" }, writes);
        foreach (var name in writes)
            Assert.True(RequiresConfirmPermission(Controller.GetMethod(name)!), name);
    }
}
