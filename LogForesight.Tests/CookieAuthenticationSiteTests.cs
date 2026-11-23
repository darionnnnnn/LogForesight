using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 內建 Cookie Authentication 的站台層行為（docs/WEB-SPEC.md §6.1、§6.3）：每個案例起一個真實站台（Stub 登入、
/// 假時鐘、可重建），以 HTTP 往返驗證 cookie 屬性、逐請求驗證、時限、金鑰環持久化、舊 cookie 與未登入回應。
/// 站台設定走行程層級環境變數，所以標 <c>NLogGlobalConfig</c> 集合（關閉並行）。
/// </summary>
[Collection("NLogGlobalConfig")]
public class CookieAuthenticationSiteTests
{
    /// <summary>可重建的測試站台：<see cref="Restart"/> 以同一個資料根目錄重起（模擬重啟行程）</summary>
    private sealed class AuthSite : IDisposable
    {
        private readonly Dictionary<string, string?> _savedEnv = new();
        private readonly NLog.Config.LoggingConfiguration? _savedNLogConfig = NLog.LogManager.Configuration;
        private readonly string _baseAddress;
        private WebApplicationFactory<Program>? _factory;
        private HttpClient? _client;

        public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "lf-cookie-" + Guid.NewGuid().ToString("N"));

        /// <summary>整秒起算：cookie 內的登入起點以 Unix 秒記，起點帶小數會讓「剛好 24 小時」的邊界差不到一秒</summary>
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero));

        public IServiceProvider Services => _factory!.Services;

        public AuthSite(string baseAddress = "https://localhost/", string? pathBase = null, int? expireHours = null)
        {
            _baseAddress = baseAddress;
            Directory.CreateDirectory(DataRoot);
            try
            {
                SetEnv("Storage__Type", "Sqlite");
                SetEnv("Storage__DataRoot", DataRoot);
                SetEnv("Storage__ConnectionString", "");
                SetEnv("Auth__Provider", "Stub");
                SetEnv("Server__PathBase", pathBase ?? "");
                if (expireHours != null) SetEnv("Jwt__ExpireHours", expireHours.Value.ToString());
                Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void SetEnv(string key, string value)
        {
            if (!_savedEnv.ContainsKey(key)) _savedEnv[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        private void Start()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                });
            });
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri(_baseAddress)
            });
            _client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");
        }

        /// <summary>關站再以同一個資料根目錄開站（資料庫、金鑰環都留著，記憶體狀態全清）</summary>
        public void Restart(Action? whileStopped = null)
        {
            _client?.Dispose();
            _factory?.Dispose();
            WaitForNLogShutdown();
            whileStopped?.Invoke();
            Start();
        }

        public Task<HttpResponseMessage> Login(string account = "demo-admin", Action<HttpRequestMessage>? configure = null) =>
            Send(HttpMethod.Post, "api/auth/login", cookie: null, body: new { account, password = "" }, configure);

        public async Task<HttpResponseMessage> Send(
            HttpMethod method, string url, string? cookie, object? body = null, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (cookie != null) request.Headers.Add("Cookie", cookie);
            if (body != null) request.Content = JsonContent.Create(body);
            configure?.Invoke(request);
            // 回應內容讀完再回傳：呼叫端不必處理已釋放的請求／串流
            var response = await _client!.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            return response;
        }

        public Task<HttpResponseMessage> Me(string? cookie) => Send(HttpMethod.Get, "api/auth/me", cookie);

        public async Task<string> LoginCookie(string account = "demo-admin")
        {
            using var response = await Login(account);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AuthTestKit.ReadSessionCookiePair(response) ?? throw new InvalidOperationException("登入沒有回登入 cookie");
        }

        private static void WaitForNLogShutdown()
        {
            // 站台主程式結束時在 finally 呼叫 LogManager.Shutdown()（把全域設定清成 null），可能晚於 Dispose 返回才跑
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (NLog.LogManager.Configuration != null && DateTime.UtcNow < deadline) Thread.Sleep(20);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _factory?.Dispose();
            foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
            WaitForNLogShutdown();
            NLog.LogManager.Configuration = _savedNLogConfig;
            CryptoHelper.ResetForTests();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { /* SQLite 檔案可能仍被連線池占用，留在暫存目錄無妨 */ }
        }
    }

    private static string ErrorCode(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static async Task<string> Body(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static void AddUser(AuthSite site, string account, bool admin = false)
    {
        var users = site.Services.GetRequiredService<IUserStore>();
        var groups = site.Services.GetRequiredService<IUserGroupStore>();
        var group = admin
            ? groups.GetAll().First(g => g.Role == UserRole.Admin && g.Active)
            : groups.Upsert(new UserGroup { GroupName = "cookie-dept", Role = UserRole.User });
        users.Upsert(new WebUser { Account = account, DisplayName = account, Active = true, GroupIds = new List<long> { group.GroupId } });
    }

    // ── L1／L2：登入、逐請求驗證 ─────────────────────────────────────────────

    [Fact]
    public async Task 登入後帶cookie可讀me_沒帶cookie回401信封()
    {
        using var site = new AuthSite();

        var cookie = await site.LoginCookie();
        using var me = await site.Me(cookie);
        using var anonymous = await site.Me(null);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(ApiErrorCodes.AuthExpired, ErrorCode(await Body(anonymous)));
    }

    /// <summary>邊界兩側：同一個帳號停用前可用、停用後的下一個請求就 401，且回應清掉 cookie</summary>
    [Fact]
    public async Task 停用帳號_下一個請求生效且清cookie()
    {
        using var site = new AuthSite();
        AddUser(site, "cookie\\victim");
        var cookie = await site.LoginCookie("cookie\\victim");
        using (var before = await site.Me(cookie)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var users = site.Services.GetRequiredService<IUserStore>();
        var user = users.FindByAccount("cookie\\victim")!;
        users.Upsert(new WebUser { Account = user.Account, DisplayName = user.DisplayName, Active = false, GroupIds = user.GroupIds });

        using var after = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(ApiErrorCodes.AuthExpired, ErrorCode(await Body(after)));
        var deletion = AuthTestKit.ReadSetCookieLine(after);
        Assert.NotNull(deletion);
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 改群組_不需重登就拿到新能力_cookie續發後仍有效()
    {
        using var site = new AuthSite();
        AddUser(site, "cookie\\promoted");
        var cookie = await site.LoginCookie("cookie\\promoted");
        using (var before = await site.Me(cookie))
            Assert.DoesNotContain("Maintain", await Body(before));

        // 與 UserAdminService.SetUserGroups 相同的兩步：改成員、推進權限版本
        var users = site.Services.GetRequiredService<IUserStore>();
        var admin = site.Services.GetRequiredService<IUserGroupStore>().GetAll().First(g => g.Role == UserRole.Admin && g.Active);
        users.SetGroups(users.FindByAccount("cookie\\promoted")!.UserId, new[] { admin.GroupId });
        site.Services.GetRequiredService<PermissionVersionStamp>().Bump();

        using var after = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("Maintain", await Body(after));
        var renewed = AuthTestKit.ReadSessionCookiePair(after);
        Assert.NotNull(renewed);   // 就地更新後續發

        using var next = await site.Me(renewed);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Contains("Maintain", await Body(next));
        Assert.Null(AuthTestKit.ReadSessionCookiePair(next));   // 版本相符，不再續發
    }

    [Fact]
    public async Task 登出_所有裝置的既有cookie一併失效_重新登入的新cookie有效()
    {
        using var site = new AuthSite();
        var deviceA = await site.LoginCookie();
        var deviceB = await site.LoginCookie();

        using (var logout = await site.Send(HttpMethod.Post, "api/auth/logout", deviceA))
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using (var a = await site.Me(deviceA)) Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        using (var b = await site.Me(deviceB)) Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        var fresh = await site.LoginCookie();
        using (var c = await site.Me(fresh)) Assert.Equal(HttpStatusCode.OK, c.StatusCode);
    }

    // ── L3／L6：重啟後 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 登出後重建站台_舊cookie仍無效_未登出的cookie重啟後仍有效()
    {
        using var site = new AuthSite();
        var loggedOut = await site.LoginCookie();
        using (await site.Send(HttpMethod.Post, "api/auth/logout", loggedOut)) { }
        var stillIn = await site.LoginCookie();   // 登出之後才登入：版號是新的

        site.Restart();

        using (var old = await site.Me(loggedOut)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var alive = await site.Me(stillIn)) Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Fact]
    public async Task serverAdmin登出後重建站台_舊cookie仍無效()
    {
        using var site = new AuthSite();
        var cookie = await site.LoginCookie("svc-lfadmin");
        using (var me = await site.Me(cookie)) Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using (await site.Send(HttpMethod.Post, "api/auth/logout", cookie)) { }

        site.Restart();

        using var after = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>金鑰環存在資料目錄：重建站台後先前簽發的資料（cookie、分頁游標用的保護器）仍解得開</summary>
    [Fact]
    public async Task 金鑰環存到資料目錄_重建站台後保護過的資料仍可解開()
    {
        using var site = new AuthSite();
        var protector = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("lf-test-cursor");
        var cursor = protector.Protect("page=2");
        await site.LoginCookie();   // 確保金鑰環已產生

        var keyDirectory = DataProtectionKeyRing.KeyDirectory(site.DataRoot);
        Assert.NotEmpty(Directory.GetFiles(keyDirectory, "*.xml"));

        site.Restart();

        var after = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("lf-test-cursor");
        Assert.Equal("page=2", after.Unprotect(cursor));
    }

    /// <summary>
    /// 真實的 PRTG 分頁游標（<c>PrtgMonitoringController</c> 以 DataProtection 保護的 R01 游標）：
    /// 重建站台後，重啟前簽發的游標仍可解開、通過擁有者與逾期檢查，主機頁照常回 200（不是 InvalidCursor／InvalidToken）。
    /// 金鑰環遺失時同一個游標會被拒（對照組，證明上面的通過確實來自持久化的金鑰環）。
    /// </summary>
    [Fact]
    public async Task 重建站台後_重啟前簽發的真實PRTG分頁游標仍有效_金鑰環遺失時被拒()
    {
        using var site = new AuthSite();
        var cookie = await site.LoginCookie();
        using var first = await site.Send(HttpMethod.Get, "api/prtg/monitoring", cookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var data = JsonDocument.Parse(await Body(first)).RootElement.GetProperty("data");
        var hostCursor = data.GetProperty("hostCursor").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(hostCursor));
        var url = "api/prtg/monitoring/hosts?cursor=" + Uri.EscapeDataString(hostCursor);

        // 重啟前先確認這個游標本身有效（排除「游標一開始就不被接受」的假綠）
        using (var before = await site.Send(HttpMethod.Get, url, cookie))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        site.Restart();

        // 重啟後游標必須「解得開」：通過解密、擁有者與逾期檢查，之後才輪到目錄版本比對。
        // 重啟會讓部分版本戳前進，所以可能回 409 CatalogueChanged（游標被認得、要求重新載入目錄）；
        // 不能是 400 InvalidCursor／InvalidToken——那代表保護器解不開，也就是金鑰環沒有跨重啟持久化。
        using (var after = await site.Send(HttpMethod.Get, url, cookie))
        {
            var body = await Body(after);
            if (after.StatusCode != HttpStatusCode.OK)
            {
                Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
                Assert.Equal(ApiErrorCodes.CatalogueChanged, ErrorCode(body));
            }
        }

        // 對照組：站台關閉期間金鑰環遺失，同一個游標（與同一個 cookie 的登入）都失效
        site.Restart(() => Directory.Delete(DataProtectionKeyRing.KeyDirectory(site.DataRoot), recursive: true));
        var fresh = await site.LoginCookie();
        using var lost = await site.Send(HttpMethod.Get, url, fresh);
        Assert.Equal(HttpStatusCode.BadRequest, lost.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCursor, ErrorCode(await Body(lost)));
    }

    [Fact]
    public async Task 金鑰環遺失_全員重登但站台正常_不影響之後的登入()
    {
        using var site = new AuthSite();
        var cookie = await site.LoginCookie();
        // 站台關閉期間清掉金鑰環（模擬目錄遺失）再開站
        site.Restart(() => Directory.Delete(DataProtectionKeyRing.KeyDirectory(site.DataRoot), recursive: true));

        using (var old = await site.Me(cookie)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        var fresh = await site.LoginCookie();
        using (var again = await site.Me(fresh)) Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    // ── L4：時限 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 閒置未滿時數仍有效_超過一分鐘即登出()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();

        site.Clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1));
        using (var inside = await site.Me(cookie)) Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
    }

    [Fact]
    public async Task 閒置超過時數_登出()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();

        site.Clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));

        using var response = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>滑動延長：有活動就把到期往後推，原本第 8 小時到期的 cookie 因第 5 小時的活動而撐過第 8 小時</summary>
    [Fact]
    public async Task 有活動時到期延長()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();

        site.Clock.Advance(TimeSpan.FromHours(5));
        using var active = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var renewed = AuthTestKit.ReadSessionCookiePair(active);
        Assert.NotNull(renewed);   // 過半後的請求續發

        site.Clock.Advance(TimeSpan.FromHours(5));   // 自登入第 10 小時：舊 cookie 早已過期
        using var later = await site.Me(renewed);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    /// <summary>背景輪詢（X-Lf-Background）不滑動延長：只有背景請求時，閒置超過時數仍登出</summary>
    [Fact]
    public async Task 只有背景請求_不延長閒置時限_超過時數登出()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();

        for (var i = 0; i < 4; i++)
        {
            site.Clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
            using var poll = await site.Send(HttpMethod.Get, "api/auth/me", cookie,
                configure: r => r.Headers.Add("X-Lf-Background", "1"));
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            Assert.Null(AuthTestKit.ReadSessionCookiePair(poll));   // 不續發
        }

        site.Clock.Advance(TimeSpan.FromMinutes(10));   // 自登入第 8 小時多：背景請求沒有延長過
        using var after = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>
    /// 作業進度輪詢（以 PRTG 結構同步狀態為例，需 Maintain）：背景請求不延長閒置時限，時鐘推過時限後回 401，
    /// 重新登入後同一個端點可再取得。
    /// </summary>
    [Fact]
    public async Task 作業進度背景輪詢_閒置過時限回401_重新登入後恢復()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();
        const string url = "api/admin/settings/prtg-structure-sync/status";
        void Bg(HttpRequestMessage r) => r.Headers.Add("X-Lf-Background", "1");

        for (var i = 0; i < 4; i++)
        {
            site.Clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
            using var poll = await site.Send(HttpMethod.Get, url, cookie, configure: Bg);
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            Assert.Null(AuthTestKit.ReadSessionCookiePair(poll));
        }

        site.Clock.Advance(TimeSpan.FromMinutes(10));
        using (var expired = await site.Send(HttpMethod.Get, url, cookie, configure: Bg))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var fresh = await site.LoginCookie();
        using var again = await site.Send(HttpMethod.Get, url, fresh, configure: Bg);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>背景請求遇到權限版號變更：仍換主體並續發（必要的續發，不是活動延長）</summary>
    [Fact]
    public async Task 背景請求遇權限變更_仍換主體並續發()
    {
        using var site = new AuthSite();
        AddUser(site, "cookie\\bgpromoted");
        var cookie = await site.LoginCookie("cookie\\bgpromoted");
        var users = site.Services.GetRequiredService<IUserStore>();
        var admin = site.Services.GetRequiredService<IUserGroupStore>().GetAll().First(g => g.Role == UserRole.Admin && g.Active);
        users.SetGroups(users.FindByAccount("cookie\\bgpromoted")!.UserId, new[] { admin.GroupId });
        site.Services.GetRequiredService<PermissionVersionStamp>().Bump();

        using var after = await site.Send(HttpMethod.Get, "api/auth/me", cookie,
            configure: r => r.Headers.Add("X-Lf-Background", "1"));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("Maintain", await Body(after));
        Assert.NotNull(AuthTestKit.ReadSessionCookiePair(after));
    }

    [Fact]
    public async Task 連續活動超過24小時_仍須重新登入()
    {
        using var site = new AuthSite(expireHours: 8);
        var cookie = await site.LoginCookie();

        // 每 4 小時一次活動，每次都續發：第 24 小時整仍有效
        for (var hour = 4; hour <= 24; hour += 4)
        {
            site.Clock.Advance(TimeSpan.FromHours(4));
            using var response = await site.Me(cookie);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"第 {hour} 小時應仍有效，實際 {(int)response.StatusCode}");
            cookie = AuthTestKit.ReadSessionCookiePair(response) ?? cookie;
        }

        site.Clock.Advance(TimeSpan.FromHours(1));   // 第 25 小時
        using var over = await site.Me(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, over.StatusCode);
    }

    // ── L5：cookie 屬性 ─────────────────────────────────────────────────────

    [Fact]
    public async Task http請求_cookie不帶Secure且可登入()
    {
        using var site = new AuthSite(baseAddress: "http://localhost/");

        using var response = await site.Login();
        var line = AuthTestKit.ReadSetCookieLine(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(line);
        Assert.DoesNotContain("secure", line!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", line, StringComparison.OrdinalIgnoreCase);
        using var me = await site.Me(AuthTestKit.ReadSessionCookiePair(response));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task https請求_cookie帶Secure()
    {
        using var site = new AuthSite(baseAddress: "https://localhost/");

        using var response = await site.Login();

        Assert.Contains("secure", AuthTestKit.ReadSetCookieLine(response)!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>反向代理終止 TLS 後以 http 轉發：認 X-Forwarded-Proto，cookie 仍帶 Secure（續發也一樣）</summary>
    [Fact]
    public async Task 代理標示https的http請求_cookie帶Secure_續發也帶()
    {
        using var site = new AuthSite(baseAddress: "http://localhost/");

        using var login = await site.Login(configure: r => r.Headers.Add("X-Forwarded-Proto", "https"));
        Assert.Contains("secure", AuthTestKit.ReadSetCookieLine(login)!, StringComparison.OrdinalIgnoreCase);

        site.Clock.Advance(TimeSpan.FromHours(5));   // 過半：續發
        using var renewed = await site.Send(HttpMethod.Get, "api/auth/me", AuthTestKit.ReadSessionCookiePair(login),
            configure: r => r.Headers.Add("X-Forwarded-Proto", "https"));
        var line = AuthTestKit.ReadSetCookieLine(renewed);
        Assert.NotNull(line);
        Assert.Contains("secure", line!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/LogForesight", "/LogForesight")]
    public async Task cookie路徑跟隨站台掛載前綴(string? pathBase, string expectedPath)
    {
        using var site = new AuthSite(pathBase: pathBase);
        var prefix = pathBase == null ? "" : pathBase.TrimStart('/') + "/";

        using var response = await site.Send(HttpMethod.Post, prefix + "api/auth/login", null, new { account = "demo-admin", password = "" });
        var line = AuthTestKit.ReadSetCookieLine(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("path=" + expectedPath, line!, StringComparison.OrdinalIgnoreCase);
        if (pathBase != null)
            Assert.DoesNotContain("path=/;", line, StringComparison.OrdinalIgnoreCase);   // 不是根路徑
    }

    // ── L7：舊格式 cookie ───────────────────────────────────────────────────

    [Fact]
    public async Task 只帶舊名稱cookie_視為未登入且回應清掉它()
    {
        using var site = new AuthSite();

        using var response = await site.Me("lf_auth=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var deletion = AuthTestKit.ReadSetCookieLine(response, SessionCookie.LegacyCookieName);
        Assert.NotNull(deletion);
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>邊界另一側：新 cookie 有效時，舊 cookie 只被清除、不影響登入</summary>
    [Fact]
    public async Task 同時帶新舊cookie_新的有效_舊的被清除()
    {
        using var site = new AuthSite();
        var cookie = await site.LoginCookie();

        using var response = await site.Me(cookie + "; lf_auth=eyJhbGciOiJIUzI1NiJ9.old.sig");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(AuthTestKit.ReadSetCookieLine(response, SessionCookie.LegacyCookieName));
    }

    // ── L8：未登入的回應 ────────────────────────────────────────────────────

    [Fact]
    public async Task 未登入的頁面請求_轉登入頁並帶回跳網址()
    {
        using var site = new AuthSite();

        using var response = await site.Send(HttpMethod.Get, "records?riskLevels=high", null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/login?returnUrl=", location);
        Assert.Contains(Uri.EscapeDataString("/records?riskLevels=high"), location);
    }

    [Fact]
    public async Task 子應用下未登入的頁面請求_轉址帶掛載前綴()
    {
        using var site = new AuthSite(pathBase: "/LogForesight");

        using var response = await site.Send(HttpMethod.Get, "LogForesight/records", null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/LogForesight/login?returnUrl=%2Frecords", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task 未登入的API請求_回401信封而非轉址()
    {
        using var site = new AuthSite();

        using var response = await site.Send(HttpMethod.Get, "api/records", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(ApiErrorCodes.AuthExpired, ErrorCode(await Body(response)));
    }
}

/// <summary>金鑰環目錄的權限限縮（目錄內是登入 cookie 與分頁游標的金鑰）</summary>
public class DataProtectionKeyRingPermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-dp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Unix_新建目錄700_既有寬鬆目錄與檔案被收緊()
    {
        if (OperatingSystem.IsWindows()) return;
        var created = Path.Combine(_root, "new");
        DataProtectionKeyRing.Prepare(created);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(created));

        var existing = Path.Combine(_root, "existing");
        Directory.CreateDirectory(existing);
        File.SetUnixFileMode(existing, (UnixFileMode)0b111_101_101);   // 755
        var key = Path.Combine(existing, "key-1.xml");
        File.WriteAllText(key, "<key/>");
        File.SetUnixFileMode(key, (UnixFileMode)0b110_100_100);        // 644

        DataProtectionKeyRing.Prepare(existing);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(existing));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
    }

    [Fact]
    public void 準備失敗時輸出與記錄都帶目錄路徑與執行身分_原例外照擲()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // 需要「沒有寫入權限」的情境
        var parent = Path.Combine(_root, "locked");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var target = Path.Combine(parent, "dataprotection");
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataProtectionKeyRing.Prepare(target));
        }
        finally
        {
            Console.SetError(originalError);
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var text = captured.ToString();
        Assert.Contains(target, text);
        Assert.Contains(Environment.UserName, text);
    }

    [Fact]
    public void 失敗訊息含目錄與身分()
    {
        var message = DataProtectionKeyRing.FailureMessage("/data/dataprotection", "svc-logforesight");
        Assert.Contains("/data/dataprotection", message);
        Assert.Contains("svc-logforesight", message);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_目錄移除繼承_只留執行帳號與SYSTEM()
    {
        var path = Path.Combine(_root, "win");
        DataProtectionKeyRing.Prepare(path);

        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var identities = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value).ToList();
        Assert.Equal(2, identities.Count);
        Assert.Contains("S-1-5-18", identities);   // SYSTEM
    }

    [Fact]
    public void 重複準備同一目錄_冪等不擲例外()
    {
        var path = Path.Combine(_root, "again");
        DataProtectionKeyRing.Prepare(path);
        DataProtectionKeyRing.Prepare(path);
        Assert.True(Directory.Exists(path));
    }
}

/// <summary>背景輪詢必須帶背景標頭（否則會滑動延長閒置時限，前景分頁開著永不登出）</summary>
public class BackgroundPollingHeaderGuardTests
{
    private static string Js(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "js", relative));
    }

    [Theory]
    [InlineData("core/layout.js", "/api/run-activity")]
    [InlineData("core/badges.js", "/api/handlers/me/badge")]
    [InlineData("core/badges.js", "/api/work-orders/unread-count")]
    public void 輪詢呼叫帶background選項(string file, string endpoint)
    {
        var line = Js(file).Split('\n').Single(l => l.Contains($"api.get('{endpoint}'"));
        Assert.Contains("background: true", line);
    }

    /// <summary>無法靜態判定回呼的 pollWhile 呼叫點（"相對路徑:回呼文字"），附理由；目前沒有。</summary>
    private static readonly string[] Unresolvable = Array.Empty<string>();

    /// <summary>
    /// 規則：頁面開著就持續送、不需使用者再操作的輪詢一律標背景。掃每個 pollWhile( 的回呼：
    /// 箭頭函式本體內的 api.get，或回呼指到的同檔具名函式本體內的 api.get（箭頭只呼叫該函式並傳參時，
    /// 函式用 background 參數者呼叫端須傳 background: true），都必須帶背景。不發請求的回呼不檢查。
    /// </summary>
    internal static List<string> PollViolations(string file, string source)
    {
        var violations = new List<string>();
        var text = string.Join('\n', source.Split('\n').Select(l =>
            l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith("*") || l.TrimStart().StartsWith("/*") ? "" : l));
        foreach (Match m in Regex.Matches(text, @"(?<![\w$.])pollWhile\s*\("))
        {
            if (text[..m.Index].EndsWith("function ")) continue;
            var open = m.Index + m.Length - 1;
            var close = MatchClose(text, open);
            var args = text.Substring(open + 1, close - open - 1);
            var cb = FirstArg(args).Trim();
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var where = $"{file}:{line}";

            var arrow = cb.IndexOf("=>", StringComparison.Ordinal);
            if (Regex.IsMatch(cb, @"^[A-Za-z_$][\w$]*$"))
            {
                var body = FunctionBody(text, cb);
                if (body == null) { if (!Unresolvable.Contains($"{file}:{cb}")) violations.Add($"{where} 回呼 {cb} 找不到同檔定義"); continue; }
                CheckGets(body, allowParam: false, "", where, violations);
            }
            else if (arrow >= 0)
            {
                var body = cb[(arrow + 2)..];
                CheckGets(body, allowParam: false, "", where, violations);
                foreach (Match call in Regex.Matches(body, @"(?<![\w$.])([A-Za-z_$][\w$]*)\s*\("))
                {
                    var fb = FunctionBody(text, call.Groups[1].Value);
                    if (fb == null) continue;
                    var callArgs = body.Substring(call.Index + call.Length - 1,
                        MatchClose(body, call.Index + call.Length - 1) - (call.Index + call.Length - 1) + 1);
                    CheckGets(fb, allowParam: true, callArgs, $"{where}（{call.Groups[1].Value}）", violations);
                }
            }
            else if (!Unresolvable.Contains($"{file}:{cb}")) violations.Add($"{where} 無法判定回呼：{cb}");
        }
        return violations;
    }

    private static void CheckGets(string body, bool allowParam, string callArgs, string where, List<string> violations)
    {
        foreach (Match g in Regex.Matches(body, @"\bapi\.get\s*\("))
        {
            var open = g.Index + g.Length - 1;
            var args = body.Substring(open + 1, MatchClose(body, open) - open - 1);
            if (Regex.IsMatch(args, @"background\s*:\s*true")) continue;
            if (allowParam && Regex.IsMatch(args, @"[{,]\s*background\s*[},]") && Regex.IsMatch(callArgs, @"background\s*:\s*true")) continue;
            violations.Add($"{where} api.get 未帶 background: true：{Regex.Replace(args.Trim(), @"\s+", " ")}");
        }
    }

    private static int MatchClose(string t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Length; i++)
        {
            var c = t[i];
            if (c is '\'' or '"' or '`')
            {
                for (i++; i < t.Length && t[i] != c; i++) if (t[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') { depth--; if (depth == 0) return i; }
        }
        return t.Length - 1;
    }

    private static string FirstArg(string args)
    {
        var depth = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c is '\'' or '"' or '`')
            {
                for (i++; i < args.Length && args[i] != c; i++) if (args[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ',' && depth == 0) return args[..i];
        }
        return args;
    }

    /// <summary>同檔具名函式（function 宣告或 const 箭頭）的本體；找不到回 null</summary>
    private static string? FunctionBody(string text, string name)
    {
        var n = Regex.Escape(name);
        var m = Regex.Match(text, $@"(?:async\s+)?function\s+{n}\s*\(") is { Success: true } f ? f
            : Regex.Match(text, $@"(?:const|let)\s+{n}\s*=\s*(?:async\s*)?\(");
        if (!m.Success) return null;
        var paren = MatchClose(text, m.Index + m.Length - 1);
        var brace = text.IndexOf('{', paren);
        return brace < 0 ? null : text.Substring(brace, MatchClose(text, brace) - brace + 1);
    }

    [Fact]
    public void 所有pollWhile輪詢的請求都帶背景標頭()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        var root = Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "js");
        var all = new List<string>();
        foreach (var sub in new[] { "pages", "core" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, sub), "*.js", SearchOption.AllDirectories))
                all.AddRange(PollViolations(Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
        Assert.True(all.Count == 0, "輪詢請求沒標背景（會滑動延長閒置時限）：\n" + string.Join('\n', all));
    }

    [Fact]
    public void 守門判準_沒標的紅_標了的過_不發請求的不檢查()
    {
        Assert.Single(PollViolations("a.js", "pollWhile(async () => { await api.get('/x', { silent: true }); }, 1);"));
        Assert.Empty(PollViolations("a.js", "pollWhile(async () => { await api.get('/x', { silent: true, background: true }); }, 1);"));
        Assert.Single(PollViolations("a.js", "async function r() { await api.get('/x', { silent: true }); }\npollWhile(r, 1);"));
        Assert.Single(PollViolations("a.js", "async function r(o) { const background = o?.background === true; await api.get('/x', { silent: true, background }); }\npollWhile(() => r(), 1);"));
        Assert.Empty(PollViolations("a.js", "async function r(o) { const background = o?.background === true; await api.get('/x', { silent: true, background }); }\npollWhile(() => r({ background: true }), 1);"));
        Assert.Empty(PollViolations("a.js", "function tick() { el.textContent = 'x'; }\npollWhile(tick, 1000);"));
        Assert.Single(PollViolations("a.js", "pollWhile(someImported, 1);"));
    }
}

/// <summary>第 79 輪自我體檢的前端行為守門（來源比對；行為本身由人工與量測驗收）</summary>
public class SelfReviewFrontGuardTests
{
    private static string Js(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "js", relative));
    }

    /// <summary>全部成員都因問題簽章過長被略過時，不先顯示「已建立 0 張」成功提示，只留警告</summary>
    [Fact]
    public void 建單全部被略過時成功提示受nothingCreated把關()
    {
        var js = Js("pages/records.js");
        Assert.Matches(@"const nothingCreated = orders\.length === 0 && \(result\.overlongSkipped\?\.length \?\? 0\) > 0;", js);
        Assert.Matches(@"if \(!nothingCreated\) \{\s*toast\(`已建立 \$\{createdCount\} 張", js);
    }

    /// <summary>PRTG 測試連線帶入「擷取參數」頁籤兩欄時，與已存值不同要提示在該頁籤儲存</summary>
    [Fact]
    public void PRTG測試連線兩欄與已存值不同時提示到擷取參數頁籤儲存()
    {
        var js = Js("pages/prtg-admin.js");
        Assert.Contains("const certDiffers =", js);
        Assert.Contains("if (certDiffers) text += '；信任的憑證指紋與忽略憑證錯誤要在「擷取參數」頁籤儲存", js);
    }

    /// <summary>登入頁讀出被踢回的原因：只經 statusNote（textContent），不寫 innerHTML</summary>
    [Fact]
    public void 登入頁以statusNote顯示一次性原因()
    {
        var js = Js("pages/login.js");
        Assert.Contains("takeSessionNotice()", js);
        Assert.Contains("statusNote('info', message)", js);
        Assert.DoesNotContain("innerHTML", js);
    }
}
