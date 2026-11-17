using LogForesight.Web.Auth;
using LogForesight.Core.Models;
using LogForesight.Web.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 F1：「我的交辦」導覽條件是處理能力（Handle）。能力一律由真實的 <see cref="RoleCapabilityMap"/>
/// 與 <see cref="UserCapabilityResolver"/> 算出（含只靠主機負責人取得的隱含 User 角色），再交給 production 的 layout.js 判斷；
/// 前端用的 capabilities 就是 /api/auth/me 回的同一份。
/// </summary>
public sealed class RoleNavigationBehaviorTests
{
    private static string ScriptDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return Path.Combine(dir!.FullName, "LogForesight.Tests", "JsBehavior");
    }

    private static string NavMyWork(IEnumerable<Capability> caps, bool serverAdmin = false)
    {
        var dir = ScriptDir();
        var r = NodeRuntime.RunOrFail(
            new[] { Path.Combine(dir, "role-nav.behavior.mjs"), "navMyWork", string.Join(',', caps), serverAdmin ? "1" : "0" },
            dir, timeout: TimeSpan.FromSeconds(60), label: "navMyWork");
        Assert.True(r.ExitCode == 0, r.Stdout + r.Stderr);
        return r.Stdout.Contains("my-work=visible") ? "visible" : "hidden";
    }

    private static IReadOnlySet<Capability> OwnerOnlyCapabilities()
    {
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "HOST-A", Active = true, OwnerUserIds = new List<long> { 5 } });
        var resolver = new UserCapabilityResolver(new FakeUserGroupStore(), hosts);
        return resolver.Resolve(new WebUser { UserId = 5, Account = "owner" });
    }

    /// <summary>案例預設可不帶參數執行（登錄守門用）；能力組合的案例在下面的 Fact 以真實角色算出再帶入</summary>
    [NodeTheory]
    [InlineData("role-nav.behavior.mjs", "navMyWork")]
    [InlineData("role-nav.behavior.mjs", "noHandleExplained")]
    public void role_nav腳本的案例可執行(string scriptName, string caseName)
    {
        var dir = ScriptDir();
        var r = NodeRuntime.RunOrFail(new[] { Path.Combine(dir, scriptName), caseName }, dir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, r.Stdout + r.Stderr);
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    [NodeFact]
    public void F1a_Manager與Dev的導覽沒有我的交辦()
    {
        Assert.Equal("hidden", NavMyWork(RoleCapabilityMap.For(UserRole.Manager)));
        Assert.Equal("hidden", NavMyWork(RoleCapabilityMap.For(UserRole.Dev)));
    }

    [NodeFact]
    public void F1b_只靠主機負責人_一般處理人_admin都有我的交辦()
    {
        var owner = OwnerOnlyCapabilities();
        Assert.Contains(Capability.Handle, owner);   // 隱含 User 角色讓前端拿得到 Handle
        Assert.Equal("visible", NavMyWork(owner));
        Assert.Equal("visible", NavMyWork(RoleCapabilityMap.For(UserRole.User)));
        Assert.Equal("visible", NavMyWork(RoleCapabilityMap.For(UserRole.Admin)));
        // serverAdmin 沒有對應的使用者，沿用既有隱藏
        Assert.Equal("hidden", NavMyWork(RoleCapabilityMap.ForServerAdmin(), serverAdmin: true));
    }

    [NodeFact]
    public void F1c_沒有處理能力的人開自己的頁_出現原因說明()
    {
        var dir = ScriptDir();
        var r = NodeRuntime.RunOrFail(new[] { Path.Combine(dir, "role-nav.behavior.mjs"), "noHandleExplained" }, dir,
            timeout: TimeSpan.FromSeconds(60), label: "noHandleExplained");
        Assert.True(r.ExitCode == 0, r.Stdout + r.Stderr);
        Assert.Contains("PASS noHandleExplained", r.Stdout);

        // load() 的接線：自己的頁且沒有 Handle 就走說明，且在管理者看別人頁的判斷（canViewHandlerPage）之前
        var js = File.ReadAllText(Path.Combine(ScriptDir(), "..", "..", "LogForesight.Web", "wwwroot", "js", "pages", "handler-detail.js"));
        var guard = js.IndexOf("isSelf && !hasCapability(user, 'Handle')", StringComparison.Ordinal);
        var viewCheck = js.IndexOf("if (!canViewHandlerPage(user, userId))", StringComparison.Ordinal);
        Assert.True(guard > 0 && viewCheck > guard);
        Assert.Contains("renderNoHandleCapability(ordersEl)", js);
    }
}
