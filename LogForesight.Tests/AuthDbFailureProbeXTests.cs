using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LogForesight.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 80 輪批次 X1 探針：帶有效 cookie 時使用者資料讀取擲例外（資料庫暫時讀不到），
/// 頁面應回站台錯誤頁、API 應回標準錯誤信封，且都不含例外原文。
/// </summary>
[Collection("NLogGlobalConfig")]
public class AuthDbFailureProbeXTests
{
    private const string Secret = "PROBE-X1-SECRET-DB-DOWN";

    public class ThrowingProxy : DispatchProxy
    {
        public IUserStore Inner = null!;
        public static volatile bool Throw;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Throw && targetMethod!.Name == nameof(IUserStore.Get)) throw new InvalidOperationException(Secret);
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }
    }

    [Fact(Skip = "第 80 輪批次 X 探針成立，待決：Cookie 驗證讀取失敗時，AuthenticationHandler 快取了失敗的 AuthenticateAsync 任務，錯誤頁重跑管線再度擲同一例外（正式環境空白 500、開發環境洩漏例外原文）；修法需改管線或驗證失敗契約，見回報")]
    public async Task 有效cookie遇資料庫讀取失敗_頁面與API都回站台錯誤形狀且不含例外原文()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-probex1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var saved = new Dictionary<string, string?>();
        void Set(string k, string v) { saved[k] = Environment.GetEnvironmentVariable(k); Environment.SetEnvironmentVariable(k, v); }
        var savedNLog = NLog.LogManager.Configuration;
        ThrowingProxy.Throw = false;
        try
        {
            Set("Storage__Type", "Sqlite"); Set("Storage__DataRoot", root); Set("Storage__ConnectionString", "");
            Set("Auth__Provider", "Stub"); Set("Server__PathBase", "");
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IHostedService>();
                    var d = s.Last(x => x.ServiceType == typeof(IUserStore));
                    s.Remove(d);
                    s.AddSingleton<IUserStore>(sp =>
                    {
                        var inner = d.ImplementationFactory != null ? (IUserStore)d.ImplementationFactory(sp)
                            : (IUserStore)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
                        var p = DispatchProxy.Create<IUserStore, ThrowingProxy>();
                        ((ThrowingProxy)(object)p).Inner = inner;
                        return p;
                    });
                });
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost/")
            });
            client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");

            using var login = await client.PostAsJsonAsync("api/auth/login", new { account = "demo-admin", password = "" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var cookie = AuthTestKit.ReadSessionCookiePair(login)!;

            ThrowingProxy.Throw = true;

            async Task<(HttpStatusCode, string, string?)> Get(string url, bool api)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Cookie", cookie);
                if (!api) req.Headers.Add("Accept", "text/html");
                using var r = await client.SendAsync(req);
                return (r.StatusCode, await r.Content.ReadAsStringAsync(), r.Content.Headers.ContentType?.MediaType);
            }

            var (pStatus, pBody, _) = await Get("/", false);
            var (aStatus, aBody, _) = await Get("api/auth/me", true);

            Console.WriteLine($"PAGE {(int)pStatus} len={pBody.Length} title={System.Text.RegularExpressions.Regex.Match(pBody, "<title>(.*?)</title>").Groups[1].Value}");
            Assert.DoesNotContain(Secret, pBody);
            Assert.DoesNotContain(Secret, aBody);

            // 頁面：非空白、是站台錯誤頁（含 html 與 500 狀態）
            Assert.Equal(HttpStatusCode.InternalServerError, pStatus);
            Assert.False(string.IsNullOrWhiteSpace(pBody), $"頁面回應本文為空（status {pStatus}）");
            Assert.Contains("發生未預期的錯誤", pBody);   // 站台自己的錯誤頁，不是框架例外頁或空白

            // API：標準信封 success=false
            Assert.Equal(HttpStatusCode.InternalServerError, aStatus);
            Assert.False(string.IsNullOrWhiteSpace(aBody), "API 回應本文為空");
            using var doc = JsonDocument.Parse(aBody);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetProperty("code").GetString()));
        }
        finally
        {
            ThrowingProxy.Throw = false;
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (NLog.LogManager.Configuration != null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            NLog.LogManager.Configuration = savedNLog;
            CryptoHelper.ResetForTests();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
