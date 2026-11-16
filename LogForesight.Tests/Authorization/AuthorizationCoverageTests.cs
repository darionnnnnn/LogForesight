using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 授權涵蓋守門：站台每一條 API／頁面 action，都必須**出現在讀取矩陣、寫入矩陣、帶有能力閘門標註（由角色閘門測試涵蓋），
/// 或登錄在附理由的豁免清單**（<see cref="AuthorizationCoverageLists"/>）。
/// 新增端點沒登錄就紅，逼著寫的人當下決定「這條怎麼測授權」；清單裡的端點已不存在也紅，
/// 避免清單與現況漂移（作法比照 <c>ControllerRouteSnapshotTests</c> 的白名單）。
/// 列舉由 ASP.NET Core 自己的 <see cref="IActionDescriptorCollectionProvider"/> 完成，不手寫反射組路由。
/// </summary>
public class AuthorizationCoverageTests
{
    private static IReadOnlySet<string> LoadActionKeys() =>
        ActionCatalog.Actions.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 掛了能力閘門（[Permission]／[RequireViewAll]）<b>且受限 A 一定被擋</b>的 action：由角色閘門測試資料驅動地涵蓋，不必逐條登錄。
    /// 角色閘門只拿受限 A 與 serverAdmin 打；A 本來就有的能力（Handle、ConfirmPermission）所標註的端點 A 能通過，
    /// 角色閘門根本打不到，必須另外出現在讀取矩陣、寫入矩陣或豁免清單——不能因為有標註就算涵蓋。
    /// </summary>
    private static IReadOnlySet<string> AnnotatedKeys()
    {
        var aCaps = RoleCapabilityMap.For(UserRole.User);
        return ActionCatalog.Actions.Where(a => a.IsAnnotated && !a.Passes(aCaps)).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>三份明確登錄的清單（互斥）；A 一定被擋的標註 action 另由角色閘門涵蓋，可與讀取／寫入矩陣重疊</summary>
    private static readonly IReadOnlyList<(string Name, IReadOnlyCollection<string> Keys)> Lists = new (string, IReadOnlyCollection<string>)[]
    {
        ("讀取矩陣（AuthorizationReadMatrix.Rows）", ReadMatrix.Rows.Select(r => r.Key).ToList()),
        ("寫入矩陣（AuthorizationWriteMatrix.Rows）", WriteMatrix.Rows.Select(r => r.Key).ToList()),
        ("豁免（AuthorizationCoverageLists.Exempt）", AuthorizationCoverageLists.Exempt.Keys.ToList())
    };

    [Fact]
    public void Every_action_is_registered_in_exactly_one_list()
    {
        var actions = LoadActionKeys();
        var registered = Lists.SelectMany(l => l.Keys).Concat(AnnotatedKeys()).ToHashSet(StringComparer.Ordinal);

        var missing = actions.Where(a => !registered.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "下列端點沒有登錄到授權涵蓋清單——它沒有會擋下受限 A 的 [Permission]／[RequireViewAll]（A 能通過的標註角色閘門打不到），" +
            "請加進讀取矩陣（AuthorizationReadMatrix）、寫入矩陣（AuthorizationWriteMatrix），或豁免清單（附理由）：\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Lists_contain_no_stale_or_duplicate_entries()
    {
        var actions = LoadActionKeys();
        var problems = new List<string>();

        foreach (var (name, keys) in Lists)
        {
            foreach (var stale in keys.Where(k => !actions.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                problems.Add($"{name}：「{stale}」已不存在於站台（改名或刪除了端點？請同步清單）");

            foreach (var dup in keys.GroupBy(k => k).Where(g => g.Count() > 1))
                problems.Add($"{name}：「{dup.Key}」重複登錄");
        }

        for (var i = 0; i < Lists.Count; i++)
            for (var j = i + 1; j < Lists.Count; j++)
                foreach (var both in Lists[i].Keys.Intersect(Lists[j].Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
                    problems.Add($"「{both}」同時登錄在 {Lists[i].Name} 與 {Lists[j].Name}（每條端點只能屬於一份清單）");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Exempt_entries_all_carry_a_reason()
    {
        var blank = AuthorizationCoverageLists.Exempt.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(blank.Count == 0, "豁免清單的下列端點沒有附理由：" + string.Join("、", blank));
    }

    [Fact]
    public void Read_matrix_rows_are_get_endpoints_with_unique_keys()
    {
        var dup = ReadMatrix.Rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "讀取矩陣有重複列：" + string.Join("、", dup));
        Assert.All(ReadMatrix.Rows, r => Assert.Equal("GET", r.Method));
    }
}
