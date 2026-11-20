using System.Net;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace LogForesight.Tests;

public class LoginThrottleTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 10, 0, 0);
    private static readonly IPAddress Ip1 = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Ip2 = IPAddress.Parse("10.0.0.2");

    [Fact]
    public void 同帳號9次失敗不擋_第10次後擋5分鐘_6分鐘後解除()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 9; i++) t.RecordFailure("Alice", Ip1, T0.AddSeconds(i));
        Assert.False(t.IsBlocked("alice", Ip1, T0.AddSeconds(9), out _));

        t.RecordFailure(" ALICE ", Ip1, T0.AddSeconds(10));
        Assert.True(t.IsBlocked("alice", Ip2, T0.AddSeconds(10), out var retry));
        Assert.InRange(retry.TotalMinutes, 4.9, 5.0);

        Assert.False(t.IsBlocked("alice", Ip2, T0.AddSeconds(10).AddMinutes(6), out _));
    }

    [Fact]
    public void 窗口外的失敗不累計()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 9; i++) t.RecordFailure("bob", null, T0);
        t.RecordFailure("bob", null, T0.AddMinutes(6));
        Assert.False(t.IsBlocked("bob", null, T0.AddMinutes(6), out _));
    }

    [Fact]
    public void 同IP50個不同帳號各失敗1次_IP被擋_換IP同帳號不擋()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 50; i++) t.RecordFailure($"user{i}", Ip1, T0);

        Assert.True(t.IsBlocked("someone-else", Ip1, T0, out _));
        Assert.False(t.IsBlocked("user0", Ip2, T0, out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void 迴路位址不計入IP維度_帳號維度照常(string loopback)
    {
        var t = new LoginThrottle();
        var ip = IPAddress.Parse(loopback);
        for (var i = 0; i < 100; i++) t.RecordFailure($"user{i}", ip, T0);

        Assert.False(t.IsBlocked("fresh", ip, T0, out _));
        Assert.DoesNotContain(t.GetBlocked(T0), e => e.Kind == LoginThrottle.KindIp);

        for (var i = 0; i < 10; i++) t.RecordFailure("carol", ip, T0);
        Assert.True(t.IsBlocked("carol", ip, T0, out _));
    }

    [Fact]
    public void 成功登入後該帳號計數歸零()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 9; i++) t.RecordFailure("dave", null, T0);
        t.RecordSuccess("DAVE");
        t.RecordFailure("dave", null, T0);
        Assert.False(t.IsBlocked("dave", null, T0, out _));
    }

    [Fact]
    public void Clear後立即不擋_帳號與IP皆可()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 10; i++) t.RecordFailure("erin", null, T0);
        for (var i = 0; i < 50; i++) t.RecordFailure($"u{i}", Ip1, T0);
        Assert.Equal(2,t.GetBlocked(T0).Count(e => e.Key is "erin" or "10.0.0.1"));

        Assert.True(t.Clear("ERIN"));
        Assert.False(t.IsBlocked("erin", null, T0, out _));
        Assert.True(t.Clear("10.0.0.1"));
        Assert.False(t.IsBlocked("x", Ip1, T0, out _));
        Assert.False(t.Clear("nobody"));
    }

    [Fact]
    public void 過期資料會被清掉_總鍵數不超過上限()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxKeys + 500; i++) t.RecordFailure($"spray{i}", null, T0.AddMilliseconds(i));
        // 最舊的被移除後，最新的仍在：再補 9 次就會擋（被移除的鍵不影響）
        for (var i = 0; i < 9; i++) t.RecordFailure($"spray{LoginThrottle.MaxKeys + 499}", null, T0.AddSeconds(30));
        Assert.True(t.IsBlocked($"spray{LoginThrottle.MaxKeys + 499}", null, T0.AddSeconds(30), out _));
        // 窗口過後全部清空
        Assert.Empty(t.GetBlocked(T0.AddHours(1)));
    }

    // ── Controller ──────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly LoginThrottle Throttle = new();
        public readonly RecordingAuditService Audit = new();
        public readonly FakeAuthenticationProvider Provider = new() { ResultToReturn = CredentialCheckResult.Fail("bad") };
        public readonly WebAppSettings Settings = new();
        public readonly RecordingSessionSigner Signer = new();

        public Harness()
        {
            Settings.Auth.ServerAdmin.Account = "serverAdmin";
        }

        public AuthController Controller(IPAddress? ip)
        {
            var users = new FakeUserStore();
            var groups = new FakeUserGroupStore();
            var hosts = new FakeHostStore();
            var identity = new IdentityService(users, groups, hosts, Provider,
                new ServerAdminAuthenticator(Settings), Audit, new UserCapabilityResolver(groups, hosts));
            var controller = new AuthController(identity, Signer, Provider,
                FakeCurrentUser.Anonymous(), Audit, Settings,
                new UserDisplayNameService(new FakeSystemSettingsStore()), Throttle);
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = ip;
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }
    }

    private static int? StatusOf(ActionResult<LogForesight.Web.Models.ApiResponse<CurrentUserDto>> r) =>
        (r.Result as IStatusCodeActionResult)?.StatusCode;

    [Fact]
    public async Task 被擋時回429且不呼叫身分驗證_同一次暫停連擋3次只寫1筆稽核()
    {
        var h = new Harness();
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, StatusOf(await h.Controller(Ip1).Login(new LoginRequest { Account = "frank", Password = "x" })));
        Assert.Equal(10, h.Provider.VerifyCalls);

        for (var i = 0; i < 3; i++)
        {
            var r = await h.Controller(Ip1).Login(new LoginRequest { Account = "frank", Password = "x" });
            Assert.Equal(429, StatusOf(r));
            var body = (LogForesight.Web.Models.ApiResponse<CurrentUserDto>)((ObjectResult)r.Result!).Value!;
            Assert.StartsWith("登入嘗試次數過多，請於 ", body.Error!.Message);
        }

        Assert.Equal(10, h.Provider.VerifyCalls);
        Assert.Single(h.Audit.Entries, e => e.Action == AuditActions.LoginThrottled);
        Assert.Empty(h.Signer.SignedIn);   // 失敗與被擋的登入都不建立登入 cookie
    }

    [Fact]
    public async Task IP已被擋時_serverAdmin仍會進到身分驗證()
    {
        var h = new Harness();
        h.Provider.RequiresPasswordValue = false; // Stub 模式：serverAdmin 免密碼即成功
        for (var i = 0; i < 50; i++) h.Throttle.RecordFailure($"u{i}", Ip1, DateTime.Now);
        Assert.True(h.Throttle.IsBlocked("anyone", Ip1, DateTime.Now, out _));

        var blocked = await h.Controller(Ip1).Login(new LoginRequest { Account = "anyone", Password = "x" });
        Assert.Equal(429, StatusOf(blocked));
        Assert.Equal(0, h.Provider.VerifyCalls);

        var r = await h.Controller(Ip1).Login(new LoginRequest { Account = "SERVERADMIN", Password = "" });
        Assert.Equal(200, StatusOf(r));
        Assert.Single(h.Signer.SignedIn, i => i.IsServerAdmin);   // 成功才建立登入 cookie；被擋與失敗都沒有
        Assert.Contains(h.Audit.Entries, e => e.Action == AuditActions.Login && e.Account == "SERVERADMIN");
    }
}

/// <summary>
/// 第 79 輪 G10：失敗計數是原子操作。N 個執行緒以 Barrier 同時起跑記錄失敗，門檻判定必須與依序記錄完全相同——
/// 少一次不該擋、多一次必擋；計數若有遺失（非原子的讀改寫），剛好等於門檻的那一輪會漏擋。
/// 各輪都用固定時間點，封鎖時點因此可精確斷言。
/// </summary>
public class LoginThrottleConcurrencyTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 10, 0, 0);
    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.7");

    private static void RunConcurrently(int count, Action<int> work)
    {
        using var barrier = new Barrier(count);
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try { barrier.SignalAndWait(); work(i); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(LoginThrottle.AccountThreshold - 1, false)]
    [InlineData(LoginThrottle.AccountThreshold, true)]
    public void 同帳號並行失敗_剛好達門檻才擋_不因競爭少算或多算(int concurrent, bool blocked)
    {
        for (var round = 0; round < 30; round++)
        {
            var throttle = new LoginThrottle();

            RunConcurrently(concurrent, _ => throttle.RecordFailure("alice", null, T0));

            Assert.Equal(blocked, throttle.IsBlocked("alice", null, T0, out _));
            if (blocked)
            {
                var entry = Assert.Single(throttle.GetBlocked(T0));
                Assert.Equal(LoginThrottle.KindAccount, entry.Kind);
                Assert.Equal(T0 + LoginThrottle.BlockDuration, entry.BlockedUntil);   // 封鎖時點正確
            }
        }
    }

    [Theory]
    [InlineData(LoginThrottle.IpThreshold - 1, false)]
    [InlineData(LoginThrottle.IpThreshold, true)]
    public void 同IP不同帳號並行失敗_剛好達門檻才擋(int concurrent, bool blocked)
    {
        for (var round = 0; round < 20; round++)
        {
            var throttle = new LoginThrottle();

            RunConcurrently(concurrent, i => throttle.RecordFailure($"user{i}", Ip, T0));

            Assert.Equal(blocked, throttle.IsBlocked("someone-else", Ip, T0, out _));
        }
    }

    [Fact]
    public void 同帳號並行一百次失敗_只擋一次且封鎖時點是第一次達門檻的時間()
    {
        var throttle = new LoginThrottle();

        RunConcurrently(100, _ => throttle.RecordFailure("bob", null, T0));

        var entry = Assert.Single(throttle.GetBlocked(T0));
        Assert.Equal("bob", entry.Key);
        Assert.Equal(T0 + LoginThrottle.BlockDuration, entry.BlockedUntil);
        // 暫停期間的失敗不延長暫停：暫停結束後第一筆失敗重新計數，不會一次就再擋
        var after = T0 + LoginThrottle.BlockDuration + TimeSpan.FromSeconds(1);
        Assert.False(throttle.IsBlocked("bob", null, after, out _));
        throttle.RecordFailure("bob", null, after);
        Assert.False(throttle.IsBlocked("bob", null, after, out _));
    }
}
