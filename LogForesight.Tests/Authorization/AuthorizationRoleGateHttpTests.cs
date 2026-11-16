using System.Net;
using LogForesight.Web.Auth;
using LogForesight.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 角色閘門：全站每一條掛了 <c>[Permission(...)]</c>（類別層＋方法層）或 <c>[RequireViewAll]</c> 的 action，
/// 對「沒有所需能力」的身分一律擋下，且被擋的寫入類請求（POST／PUT／DELETE）沒有寫入任何資料。
///
/// 資料驅動：action 由 ASP.NET Core 自己列舉（<see cref="ActionCatalog"/>），標註由反射讀出；
/// 各身分的能力集合用站台真的 <see cref="RoleCapabilityMap"/> 算，測試裡沒有寫死的能力表。
/// 判準對兩個身分檢查——受限 A（User 角色：Handle、ConfirmPermission）與 serverAdmin（Maintain、ViewAudit、沒有 ViewAll）：
/// <list type="bullet">
/// <item>API：403（<c>PermissionFilter</c> 的信封；<c>RequireViewAll</c> 由 <c>ApiExceptionFilter</c> 轉成 403 信封）。</item>
/// <item>MVC 頁面：302 導向 <c>…/access-denied</c>。</item>
/// <item>被擋的 POST／PUT／DELETE：全域資料指紋（<see cref="AuthorizationDataProbe.Global"/>）前後逐字相同。</item>
/// </list>
/// 有能力的組合不在這裡驗行為（那是各功能自己的測試與讀取／寫入矩陣）。
/// </summary>
[Collection(AuthorizationHttpCollection.Name)]
public class AuthorizationRoleGateHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    private readonly AuthorizationDataProbe _probe;

    public AuthorizationRoleGateHttpTests(AuthorizationHttpFixture fx)
    {
        _fx = fx;
        _probe = new AuthorizationDataProbe(fx);
    }

    /// <summary>身分能力一律由站台的 RoleCapabilityMap 算出，並與夾具登入用的身分對帳</summary>
    private static IReadOnlySet<Capability> CapsOf(IdentityFx id) => id.Name switch
    {
        "A受限" => RoleCapabilityMap.For(LogForesight.Core.Models.UserRole.User),
        "serverAdmin" => RoleCapabilityMap.ForServerAdmin(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id.Name, "角色閘門只檢查受限 A 與 serverAdmin")
    };

    public static IReadOnlyList<ActionInfo> GatedActions() =>
        ActionCatalog.Actions.Where(a => a.IsAnnotated).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();

    [Fact]
    public void Gate_identities_match_the_role_capability_map()
    {
        foreach (var name in new[] { "A受限", "serverAdmin" })
            Assert.True(CapsOf(_fx.Identity(name)).SetEquals(_fx.Identity(name).Caps), $"夾具身分 {name} 的能力與 RoleCapabilityMap 不同");
    }

    [Fact]
    public async Task Every_annotated_action_blocks_identities_without_the_capability()
    {
        var problems = new List<string>();
        var checkedPairs = 0;
        var checkedWrites = 0;
        var skippedHasCapability = 0;

        foreach (var action in GatedActions())
        {
            foreach (var name in new[] { "A受限", "serverAdmin" })
            {
                var id = _fx.Identity(name);
                var caps = CapsOf(id);
                if (action.Passes(caps))
                {
                    skippedHasCapability++;
                    continue;
                }

                checkedPairs++;
                var url = ActionCatalog.BuildUrl(action);
                var method = new HttpMethod(action.Method);
                object? body = action.IsWrite ? new Dictionary<string, object>() : null;
                var content = ContentFor(action);
                var before = action.IsWrite ? _probe.Global() : null;

                var (status, text, location) = await _fx.SendAsync(id, method, url, body, content);

                if (action.IsPage)
                {
                    if (status != HttpStatusCode.Redirect || location == null || !location.EndsWith("/access-denied", StringComparison.Ordinal))
                        problems.Add($"{action.Key} 身分={name}：頁面應 302 導向 /access-denied，實際 {(int)status}（Location={location}）");
                }
                else if (status != HttpStatusCode.Forbidden)
                {
                    problems.Add($"{action.Key} 身分={name}：應 403，實際 {(int)status}　{Clip(text)}");
                }

                if (before != null)
                {
                    checkedWrites++;
                    if (_probe.Global() != before) problems.Add($"{action.Key} 身分={name}：被擋的請求仍改了資料（全域指紋不同）");
                }
            }
        }

        Assert.True(checkedPairs > 0 && checkedWrites > 0, "角色閘門沒有檢查到任何案例——列舉或標註讀取壞了");
        Assert.True(problems.Count == 0,
            $"角色閘門共檢查 {checkedPairs} 組（action×身分，其中寫入 {checkedWrites}），略過有能力者 {skippedHasCapability}。\n" +
            string.Join("\n", problems));
    }

    /// <summary>
    /// PRTG 範圍清除的三條端點刻意不掛 <c>[Permission]</c>（Maintain 被撤銷後，建立者仍要能讀到進度），
    /// 改在動作內以「作業建立者＋耐久授權」檢查。守門必須用**真實存在**的作業：不存在的編號任何人都回 400，
    /// 拿掉擁有者比對也不會紅。這裡先以管理者身分建一筆 preview 作業，他人（受限 A、serverAdmin）打這筆
    /// 必須 403、作業資料（狀態與版本）不變；管理者本人讀取則是 200（證明判準比得到資料）。
    /// </summary>
    [Fact]
    public async Task Self_authorized_purge_endpoints_reject_foreign_callers()
    {
        var backend = _fx.Services.GetRequiredService<StorageBackend>();
        var jobs = backend.PrtgStore().ScopePurgeJobs();
        var job = jobs.CreatePreview(_fx.Admin.UserId, 0, null, "authz-fingerprint",
            new HashSet<long> { 1L }, 1, DateTimeOffset.UtcNow);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/admin/settings/prtg-scope-purge/confirm", new { previewId = job.JobId }),
            (HttpMethod.Get, $"/api/admin/settings/prtg-scope-purge/jobs/{job.JobId}", null),
            (HttpMethod.Post, $"/api/admin/settings/prtg-scope-purge/jobs/{job.JobId}/resume", new { expectedRevision = job.Revision })
        };
        var problems = new List<string>();
        foreach (var name in new[] { "A受限", "serverAdmin" })
        {
            foreach (var (method, url, body) in calls)
            {
                var before = jobs.Get(job.JobId)!;
                var (status, text, _) = await _fx.SendAsync(_fx.Identity(name), method, url, body);
                if (status != HttpStatusCode.Forbidden) problems.Add($"{method} {url} 身分={name}：應 403，實際 {(int)status}　{Clip(text)}");
                var after = jobs.Get(job.JobId)!;
                if (after.Status != before.Status || after.Revision != before.Revision)
                    problems.Add($"{method} {url} 身分={name}：被擋的請求仍改了作業（{before.Status}/{before.Revision} → {after.Status}/{after.Revision}）");
            }
        }

        // 正向：擁有者本人讀得到，證明上面的 403 來自擁有者比對而不是作業不存在
        var (ownerStatus, ownerText, _) = await _fx.SendAsync(_fx.Admin, HttpMethod.Get,
            $"/api/admin/settings/prtg-scope-purge/jobs/{job.JobId}", null);
        if (ownerStatus != HttpStatusCode.OK || !ownerText.Contains(job.JobId, StringComparison.Ordinal))
            problems.Add($"作業擁有者（Admin）讀取應 200 且含作業編號，實際 {(int)ownerStatus}　{Clip(ownerText)}");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>[Consumes] 限定內容型別的端點（上傳檔案、分塊上傳）要送對的型別，否則在路由階段就 415，驗不到閘門</summary>
    private static HttpContent? ContentFor(ActionInfo action)
    {
        var type = action.Consumes.FirstOrDefault();
        if (type == null) return null;
        if (type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 0x41 }), "file", "x.csv" } };
        var bytes = new ByteArrayContent(new byte[] { 0x41 });
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return bytes;
    }

    private static string Clip(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
