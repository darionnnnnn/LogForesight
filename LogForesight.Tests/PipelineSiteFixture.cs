using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogForesight.Tests;

/// <summary>讓 <see cref="PipelineSiteFixture"/> 能在驗證中介層擲例外的替身（只在測試設了標記時擲）。</summary>
internal sealed class ThrowingAuthenticationSchemeProvider : AuthenticationSchemeProvider
{
    /// <summary>非 null 時，下一個請求經過驗證中介層會擲出帶此字串的例外</summary>
    internal static string? ThrowMarker;

    public ThrowingAuthenticationSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }

    public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync()
    {
        // 只擲一次：例外處理器改派到錯誤頁時會重跑整條管線，不消耗標記的話第二次也會擲，測不到第一次處理的結果
        if (Interlocked.Exchange(ref ThrowMarker, null) is { } marker) throw new InvalidOperationException(marker);
        return base.GetDefaultAuthenticateSchemeAsync();
    }
}

/// <summary>讓登入驗證卡住，用來把登入並行上限占滿（測試不靠真實 LDAP）。</summary>
internal sealed class BlockingAuthenticationProvider : IAuthenticationProvider
{
    internal static readonly ManualResetEventSlim Gate = new(initialState: true);
    internal static int Entered;

    public string Name => "Blocking";
    public bool RequiresPassword => false;

    public CredentialCheckResult Verify(string account, string? password)
    {
        Interlocked.Increment(ref Entered);
        Gate.Wait(TimeSpan.FromSeconds(60));
        return CredentialCheckResult.Fail("測試替身一律拒絕");
    }
}

/// <summary>
/// 起「真的 Kestrel」的整站（<see cref="WebApplicationFactory{TEntryPoint}"/> 的 UseKestrel）：
/// TestServer 沒有實作請求本文大小的功能介面，413 只有在 Kestrel 上才會真的發生。
/// 與授權矩陣的 fixture 同樣以環境變數帶入站台設定，因此獨立成一個停用並行的集合，結束時還原全域狀態。
/// </summary>
public sealed class PipelineSiteFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataRoot;
    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly NLog.Config.LoggingConfiguration? _savedNLogConfig;
    public HttpClient Client { get; }

    public PipelineSiteFixture()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "lf-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        _savedNLogConfig = NLog.LogManager.Configuration;
        try
        {
            SetEnv("Storage__Type", "Sqlite");
            SetEnv("Storage__DataRoot", _dataRoot);
            SetEnv("Storage__ConnectionString", "");
            SetEnv("Auth__Provider", "Stub");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IAuthenticationSchemeProvider>();
                    services.AddSingleton<IAuthenticationSchemeProvider, ThrowingAuthenticationSchemeProvider>();
                    services.RemoveAll<IAuthenticationProvider>();
                    services.AddSingleton<IAuthenticationProvider, BlockingAuthenticationProvider>();
                });
            });
            _factory.UseKestrel(0);
            _factory.StartServer();
            var address = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
            Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri(address) });
            Client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void SetEnv(string key, string value)
    {
        _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        BlockingAuthenticationProvider.Gate.Set();
        ThrowingAuthenticationSchemeProvider.ThrowMarker = null;
        Client?.Dispose();
        _factory?.Dispose();
        foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
        // 與授權矩陣 fixture 相同：站台結束時會清掉全域 NLog 設定，等它清完再還原
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (NLog.LogManager.Configuration != null && DateTime.UtcNow < deadline) Thread.Sleep(20);
        NLog.LogManager.Configuration = _savedNLogConfig;
        CryptoHelper.ResetForTests();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* SQLite 檔案可能仍被連線池占用 */ }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PipelineSiteCollection : ICollectionFixture<PipelineSiteFixture>
{
    public const string Name = "PipelineSite";
}

/// <summary>
/// 第 79 輪 B1：真的 Kestrel 上，本文過大、媒體類型不符、middleware 內擲例外都拿到信封；
/// G15：登入並行上限（含佇列）占滿時，新的登入請求回 429 信封。
/// </summary>
[Collection(PipelineSiteCollection.Name)]
public class PipelineSiteHttpTests
{
    private readonly PipelineSiteFixture _fx;
    public PipelineSiteHttpTests(PipelineSiteFixture fx) => _fx = fx;

    private static (bool Success, string? Code, string? Message) ReadEnvelope(string body)
    {
        Assert.True(body.TrimStart().StartsWith('{'), "回應本文不是信封：[" + body[..Math.Min(500, body.Length)] + "]");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("success", out _) && root.TryGetProperty("error", out _), "回應不是信封（缺 success／error）：" + body[..Math.Min(500, body.Length)]);
        var error = root.GetProperty("error");
        return (root.GetProperty("success").GetBoolean(), error.GetProperty("code").GetString(), error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 本文超過端點上限_回413信封()
    {
        // 登入端點上限是 4 KB；本文 64 KB，Kestrel 在讀取時擲 BadHttpRequestException(413)
        var oversized = new StringContent("{\"account\":\"" + new string('a', 64 * 1024) + "\",\"password\":\"x\"}", Encoding.UTF8, "application/json");

        using var response = await _fx.Client.PostAsync("/api/auth/login", oversized);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var (success, code, _) = ReadEnvelope(body);
        Assert.False(success);
        Assert.Equal(ApiErrorCodes.PayloadTooLarge, code);
    }

    [Fact]
    public async Task 不支援的內容型別_回415信封()
    {
        using var content = new StringContent("account=a&password=b", Encoding.UTF8, "text/plain");

        using var response = await _fx.Client.PostAsync("/api/auth/login", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var (success, code, _) = ReadEnvelope(body);
        Assert.False(success);
        Assert.Equal(ApiErrorCodes.UnsupportedMediaType, code);
    }

    [Fact]
    public async Task 中介層內擲例外_整條管線回信封_本文不含例外原文()
    {
        const string marker = "ZZ-MIDDLEWARE-SECRET lf_internal /var/data/path";
        ThrowingAuthenticationSchemeProvider.ThrowMarker = marker;
        try
        {
            using var response = await _fx.Client.GetAsync("/api/auth/options");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(body.Contains("ZZ-MIDDLEWARE-SECRET"), body[..Math.Min(3000, body.Length)]);
            var (success, code, _) = ReadEnvelope(body);
            Assert.False(success);
            Assert.Equal(ApiErrorCodes.ServerError, code);
        }
        finally
        {
            ThrowingAuthenticationSchemeProvider.ThrowMarker = null;
        }

        // 標記清掉後同一條路徑恢復正常，證明上面的 500 確實來自擲出的例外
        using var ok = await _fx.Client.GetAsync("/api/auth/options");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task 登入並行上限與佇列占滿_新的登入回429信封()
    {
        var capacity = LoginRateLimit.ConcurrentLogins + LoginRateLimit.QueuedLogins;
        BlockingAuthenticationProvider.Gate.Reset();
        Interlocked.Exchange(ref BlockingAuthenticationProvider.Entered, 0);
        var inflight = new List<Task<HttpResponseMessage>>();
        try
        {
            // 同時送出 並行數＋佇列數＋1 個登入：前兩者各自卡住或排隊，多出來的那一個只能被拒絕
            for (var i = 0; i <= capacity; i++)
                inflight.Add(_fx.Client.PostAsJsonAsyncCompat("/api/auth/login", $"{{\"account\":\"fill{i}\",\"password\":\"x\"}}"));

            // 卡住的請求在 Gate 放開前不會完成，所以第一個完成的只會是被拒絕的那個
            var first = await Task.WhenAny(inflight).WaitAsync(TimeSpan.FromSeconds(30));
            var rejected = await first;
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            var settle = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref BlockingAuthenticationProvider.Entered) < LoginRateLimit.ConcurrentLogins && DateTime.UtcNow < settle)
                await Task.Delay(25);
            Assert.Equal(LoginRateLimit.ConcurrentLogins, Volatile.Read(ref BlockingAuthenticationProvider.Entered));
            var (success, code, message) = ReadEnvelope(await rejected.Content.ReadAsStringAsync());
            Assert.False(success);
            Assert.Equal(ApiErrorCodes.RateLimited, code);
            Assert.Contains("登入請求過多", message);   // 並行上限的訊息，不是帳號節流的「登入嘗試次數過多」
        }
        finally
        {
            BlockingAuthenticationProvider.Gate.Set();
            foreach (var task in inflight) { try { (await task).Dispose(); } catch (HttpRequestException) { } }
        }
    }
}

internal static class HttpClientJsonExtensions
{
    internal static Task<HttpResponseMessage> PostAsJsonAsyncCompat(this HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
}
