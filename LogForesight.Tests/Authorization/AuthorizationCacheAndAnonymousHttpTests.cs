using System.Net;
using Microsoft.AspNetCore.Authorization;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 兩件跨端點的授權守門：
/// <list type="number">
/// <item>快取分鍵（F-5）：儀表板與報表彙總有跨請求快取，鍵必須含可見主機集合——Admin 先查（快取被填滿全部主機）後，
/// 受限者用同參數查，不得拿到別人的快取內容；同一人再查一次要與第一次相同（命中自己的快取）。</item>
/// <item>匿名端點守門（F-6）：<c>[AllowAnonymous]</c> 的 action 恰為登錄的 6 條，其餘都被
/// <c>FallbackPolicy</c> 擋在驗證之外。多一條就紅並列出名稱。</item>
/// </list>
/// </summary>
[Collection(AuthorizationHttpCollection.Name)]
public class AuthorizationCacheAndAnonymousHttpTests
{
    private static readonly string[] ExpectedAnonymous =
    {
        "POST /api/auth/login",
        "GET /api/auth/options",
        "POST /api/csp-report",
        "GET /api/health",
        "GET /login",
        "GET /error"
    };

    private readonly AuthorizationHttpFixture _fx;

    public AuthorizationCacheAndAnonymousHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    public static IEnumerable<object[]> SummaryUrls()
    {
        var from = AuthorizationHttpFixture.Day.AddDays(-14).ToString("yyyy-MM-dd");
        var to = AuthorizationHttpFixture.DayText;
        // 第二個參數：回應是否只帶彙總數字、沒有哨兵（類型趨勢）——這種端點改以 "total":（每台可見主機 2 個問題日）比對可見範圍
        yield return new object[] { "/api/dashboard/summary", false };
        yield return new object[] { $"/api/reports/summary?from={from}&to={to}", false };
        yield return new object[] { $"/api/reports/effectiveness?from={from}&to={to}", false };
        yield return new object[] { $"/api/reports/category-trend?from={from}&to={to}", true };
    }

    [Theory]
    [MemberData(nameof(SummaryUrls))]
    public async Task Summary_cache_is_keyed_by_visible_scope(string url, bool totalsOnly)
    {
        // 回應帶哨兵的端點：逐台比哨兵；只帶彙總數字的端點：比 "total":N（N＝可見主機數 × 2 個問題日），
        // 快取鍵少了可見範圍時，受限者會拿到 Admin 的 6、Admin 會拿到受限者的 2
        void ExpectVisible(string body, int[] visible, string who)
        {
            if (totalsOnly)
            {
                Assert.True(body.Contains($"\"total\":{visible.Length * 2}", StringComparison.Ordinal),
                    $"{who} 的回應應含 \"total\":{visible.Length * 2}（快取鍵缺少可見範圍？）：{url}\n{body}");
                return;
            }
            foreach (var n in new[] { 1, 2, 3, 4 })
            {
                if (visible.Contains(n)) Assert.True(Sentinels.ContainsAny(body, n), $"{who} 的回應應含主機 {n}：{url}");
                else Assert.False(Sentinels.ContainsAny(body, n), $"{who} 拿到未授權主機 {n} 的資料（快取鍵缺少可見範圍？）：{url}");
            }
        }

        // 1. Admin 先查：回應含主機 1～3（快取被「全部主機」的內容填滿）
        var (adminStatus, adminBody) = await _fx.GetAsync(_fx.Admin, url);
        Assert.Equal(HttpStatusCode.OK, adminStatus);
        ExpectVisible(adminBody, new[] { 1, 2, 3 }, "Admin");

        // 2. 受限 A 立刻用同參數查：只能看到主機 1，不得拿到 Admin 的快取
        var (aStatus, aFirst) = await _fx.GetAsync(_fx.A, url);
        Assert.Equal(HttpStatusCode.OK, aStatus);
        ExpectVisible(aFirst, new[] { 1 }, "A");

        // 3. 同一人再查：與第一次相同，且 SummaryCache 的命中數真的增加（回應相同不代表走了快取——
        //    快取壞掉時每次重算也會得到同樣內容，所以必須用命中統計證明第二次是命中自己那一格）
        var cache = _fx.Services.GetRequiredService<SummaryCache>();
        var hitsBefore = cache.GetStatistics().Hits;
        var (_, aSecond) = await _fx.GetAsync(_fx.A, url);
        Assert.Equal(aFirst, aSecond);
        Assert.True(cache.GetStatistics().Hits > hitsBefore, $"A 第二次查詢沒有命中 SummaryCache（命中數 {hitsBefore} → {cache.GetStatistics().Hits}）：{url}");

        // 4. 另一個受限者 B（負責主機 2）：只看到主機 2，不得拿到 A 的快取
        var (_, bBody) = await _fx.GetAsync(_fx.B, url);
        ExpectVisible(bBody, new[] { 2 }, "B");

        // 5. Admin 再查：不被受限者的快取汙染（仍含主機 1～3）
        var (_, adminAgain) = await _fx.GetAsync(_fx.Admin, url);
        ExpectVisible(adminAgain, new[] { 1, 2, 3 }, "Admin（再查）");
    }

    [Fact]
    public void Only_the_registered_actions_allow_anonymous()
    {
        var actual = ActionCatalog.Actions.Where(a => a.AllowAnonymous).Select(a => a.Key)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var expected = ExpectedAnonymous.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var extra = actual.Except(expected).ToList();
        var missing = expected.Except(actual).ToList();
        Assert.True(extra.Count == 0 && missing.Count == 0,
            (extra.Count > 0 ? "多了這些 [AllowAnonymous] 端點（匿名者可直接呼叫，請確認是否刻意）：\n  " + string.Join("\n  ", extra) + "\n" : "") +
            (missing.Count > 0 ? "登錄的匿名端點不見了（改名或移除了標註？）：\n  " + string.Join("\n  ", missing) : ""));
    }

    [Fact]
    public void Fallback_policy_requires_an_authenticated_user()
    {
        var options = _fx.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }
}
