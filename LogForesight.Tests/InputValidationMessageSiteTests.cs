using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LogForesight.Web.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 輸入驗證訊息與預設輸入上限的站台層行為（第 80 輪批次 E）：真實站台（Stub 登入）以 HTTP 往返驗證
/// 「被擋時使用者看到什麼」。站台設定走行程層級環境變數，所以標 <c>NLogGlobalConfig</c> 集合（關閉並行）。
/// </summary>
[Collection("NLogGlobalConfig")]
public class InputValidationMessageSiteTests
{
    private sealed class Site : IDisposable
    {
        private readonly Dictionary<string, string?> _savedEnv = new();
        private readonly NLog.Config.LoggingConfiguration? _savedNLogConfig = NLog.LogManager.Configuration;
        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "lf-valmsg-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        public HttpClient Client { get; }
        public string Cookie { get; private set; } = "";

        public Site()
        {
            Directory.CreateDirectory(_dataRoot);
            try
            {
                foreach (var (k, v) in new Dictionary<string, string>
                {
                    ["Storage__Type"] = "Sqlite", ["Storage__DataRoot"] = _dataRoot,
                    ["Storage__ConnectionString"] = "", ["Auth__Provider"] = "Stub", ["Server__PathBase"] = ""
                })
                {
                    _savedEnv[k] = Environment.GetEnvironmentVariable(k);
                    Environment.SetEnvironmentVariable(k, v);
                }
                _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
                    builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
                });
                Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost/")
                });
                Client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public async Task LoginAsync()
        {
            using var response = await Client.PostAsJsonAsync("api/auth/login", new { account = "demo-admin", password = "" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Cookie = AuthTestKit.ReadSessionCookiePair(response) ?? throw new InvalidOperationException("登入沒有回登入 cookie");
        }

        public async Task<(HttpStatusCode Status, string Code, string Message)> PostAsync(string url, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("Cookie", Cookie);
            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!text.TrimStart().StartsWith('{')) return (response.StatusCode, "", text);
            using var doc = JsonDocument.Parse(text);
            var error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            return (response.StatusCode,
                error.ValueKind == JsonValueKind.Object ? error.GetProperty("code").GetString() ?? "" : "",
                error.ValueKind == JsonValueKind.Object ? error.GetProperty("message").GetString() ?? "" : "");
        }

        public void Dispose()
        {
            Client?.Dispose();
            _factory?.Dispose();
            foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (NLog.LogManager.Configuration != null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            NLog.LogManager.Configuration = _savedNLogConfig;
            CryptoHelper.ResetForTests();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* 連線池可能仍占著檔案 */ }
        }
    }

    private static object RuleBody(int countThreshold, int sourcePatternLength) => new
    {
        id = "valmsg-rule", enabled = true, platform = "windows",
        sourcePattern = new string('s', sourcePatternLength),
        eventIds = new[] { 4625 }, severity = "Medium", category = "Security",
        description = "驗證訊息測試", countThreshold
    };

    // ── E1：沒寫 ErrorMessage 的 [Range]／[StringLength] ────────────────────────

    [Fact]
    public async Task Range未寫訊息_超界回中文訊息含允許範圍_不含英文框架字樣與屬性名()
    {
        using var site = new Site();
        await site.LoginAsync();

        var (status, code, message) = await site.PostAsync("api/rules/validate", RuleBody(countThreshold: 100001, sourcePatternLength: 10));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", code);
        Assert.DoesNotContain("The field", message);
        Assert.DoesNotContain("CountThreshold", message);
        Assert.Contains("1", message);
        Assert.Contains("100000", message);
        Assert.Matches("[一-鿿]", message);
    }

    [Fact]
    public async Task StringLength未寫訊息_超界回中文訊息含上限_不含英文框架字樣與屬性名()
    {
        using var site = new Site();
        await site.LoginAsync();

        var (status, _, message) = await site.PostAsync("api/rules/validate", RuleBody(countThreshold: 1, sourcePatternLength: 256));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain("The field", message);
        Assert.DoesNotContain("SourcePattern", message);
        Assert.Contains("255", message);
        Assert.Matches("[一-鿿]", message);
    }

    [Fact]
    public async Task 邊界_剛好等於上限不出現驗證錯誤_上限加一被擋()
    {
        using var site = new Site();
        await site.LoginAsync();

        var atLimit = await site.PostAsync("api/rules/validate", RuleBody(countThreshold: 100000, sourcePatternLength: 255));
        Assert.True(atLimit.Status != HttpStatusCode.BadRequest, atLimit.Message);

        var overRange = await site.PostAsync("api/rules/validate", RuleBody(countThreshold: 100001, sourcePatternLength: 255));
        var overLength = await site.PostAsync("api/rules/validate", RuleBody(countThreshold: 100000, sourcePatternLength: 256));
        Assert.Equal(HttpStatusCode.BadRequest, overRange.Status);
        Assert.Equal(HttpStatusCode.BadRequest, overLength.Status);
    }

    [Fact]
    public async Task 已手寫ErrorMessage的屬性_訊息維持原樣()
    {
        using var site = new Site();
        await site.LoginAsync();
        var body = new
        {
            id = "valmsg-rule", enabled = true, platform = "windows", sourcePattern = "s", eventIds = new[] { 4625 },
            severity = "Medium", category = "Security", description = "", countThreshold = 1
        };

        var (status, _, message) = await site.PostAsync("api/rules/validate", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("請輸入規則說明", message);   // [Required(ErrorMessage = "請輸入規則說明")]
    }

    // ── E2：PRTG 診斷匯入建立請求的預設輸入上限 ───────────────────────────────

    [Fact]
    public async Task PackageSha256超過預設上限_回400中文訊息_剛好等於上限則不被預設上限擋()
    {
        using var site = new Site();
        await site.LoginAsync();
        object Body(int len) => new { transferId = Guid.NewGuid(), declaredBytes = 10, chunkCount = 1, packageSha256 = new string('a', len) };

        var over = await site.PostAsync("api/admin/settings/prtg-import-transfers", Body(DefaultInputLimitsFilter.DefaultMaxStringLength + 1));
        var at = await site.PostAsync("api/admin/settings/prtg-import-transfers", Body(DefaultInputLimitsFilter.DefaultMaxStringLength));

        Assert.True(over.Status == HttpStatusCode.BadRequest, over.Status + " " + over.Message);
        Assert.Contains("PackageSha256", over.Message);
        Assert.Contains("內容過長", over.Message);
        Assert.DoesNotContain("內容過長", at.Message);
    }
}

/// <summary>驗證屬性的中文預設訊息涵蓋與請求本文型別位置守門（第 80 輪批次 E）</summary>
public class InputValidationCoverageGuardTests
{
    private static IEnumerable<Type> WebModelTypes() =>
        typeof(LogForesight.Web.Models.ApiResponse).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("LogForesight.Web.Models", StringComparison.Ordinal) == true);

    private static IEnumerable<(string Where, System.ComponentModel.DataAnnotations.ValidationAttribute Attribute)> AllAttributes()
    {
        foreach (var type in WebModelTypes())
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var a in p.GetCustomAttributes<System.ComponentModel.DataAnnotations.ValidationAttribute>(true))
                    yield return ($"{type.Name}.{p.Name}", a);
            foreach (var c in type.GetConstructors())
                foreach (var prm in c.GetParameters())
                    foreach (var a in prm.GetCustomAttributes<System.ComponentModel.DataAnnotations.ValidationAttribute>(true))
                        yield return ($"{type.Name}({prm.Name})", a);
        }
    }

    [Fact]
    public void 沒寫ErrorMessage的驗證屬性型別_全在中文訊息涵蓋表內()
    {
        var uncovered = AllAttributes()
            .Where(x => string.IsNullOrEmpty(x.Attribute.ErrorMessage) && x.Attribute.ErrorMessageResourceType == null)
            .Where(x => !ChineseModelBindingMessages.CoveredAttributeTypes.Contains(x.Attribute.GetType()))
            .Select(x => $"{x.Where}[{x.Attribute.GetType().Name}]")
            .ToList();
        Assert.True(uncovered.Count == 0, "以下驗證屬性沒有中文預設訊息，請補進 ChineseModelBindingMessages 涵蓋表或手寫 ErrorMessage：" + string.Join("、", uncovered));
    }

    [Fact]
    public void 涵蓋表的訊息不帶欄位名佔位字_或僅在有Display時帶()
    {
        // 抽樣各型別：沒有 [Display] 時產生的訊息不得含 {0}
        foreach (var type in ChineseModelBindingMessages.CoveredAttributeTypes)
        {
            var message = ChineseModelBindingMessages.DefaultMessageFor(
                type == typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute) ? new System.ComponentModel.DataAnnotations.StringLengthAttribute(5)
                : type == typeof(System.ComponentModel.DataAnnotations.RangeAttribute) ? new System.ComponentModel.DataAnnotations.RangeAttribute(1, 2)
                : type == typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute) ? new System.ComponentModel.DataAnnotations.MaxLengthAttribute(5)
                : type == typeof(System.ComponentModel.DataAnnotations.MinLengthAttribute) ? new System.ComponentModel.DataAnnotations.MinLengthAttribute(1)
                : type == typeof(System.ComponentModel.DataAnnotations.LengthAttribute) ? new System.ComponentModel.DataAnnotations.LengthAttribute(1, 5)
                : type == typeof(System.ComponentModel.DataAnnotations.EmailAddressAttribute) ? new System.ComponentModel.DataAnnotations.EmailAddressAttribute()
                : type == typeof(System.ComponentModel.DataAnnotations.RegularExpressionAttribute) ? new System.ComponentModel.DataAnnotations.RegularExpressionAttribute("a")
                : new System.ComponentModel.DataAnnotations.RequiredAttribute(),
                typeof(string), displayName: null);
            Assert.NotNull(message);
            if (type != typeof(System.ComponentModel.DataAnnotations.RequiredAttribute))
                Assert.DoesNotContain("{0}", message);
        }
    }

    // ── E2：請求本文型別一律在 LogForesight.Web.Models* ───────────────────────

    /// <summary>直接以集合或字串當本文的 action 不經 DefaultInputLimitsFilter，列白名單並附理由（目前無）</summary>
    private static readonly HashSet<string> BodyTypeAllowList = new();

    [Fact]
    public void 控制器action的本文參數型別_全在Models命名空間下()
    {
        var offenders = new List<string>();
        var controllers = typeof(LogForesight.Web.Controllers.Api.PrtgTransferController).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        foreach (var controller in controllers)
        {
            var apiController = controller.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ApiControllerAttribute>(true) != null;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.GetCustomAttributes(true).Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)) continue;
                foreach (var prm in method.GetParameters())
                {
                    var isBody = prm.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromBodyAttribute>() != null;
                    if (!isBody && apiController && !HasExplicitSource(prm) && IsComplex(prm.ParameterType)
                        && !IsRouteParameter(method, prm)) isBody = true;
                    if (!isBody) continue;
                    var type = prm.ParameterType;
                    if (type.Namespace?.StartsWith("LogForesight.Web.Models", StringComparison.Ordinal) == true) continue;
                    var key = $"{controller.Name}.{method.Name}({type.Name})";
                    if (!BodyTypeAllowList.Contains(key)) offenders.Add(key);
                }
            }
        }
        Assert.True(offenders.Count == 0, "請求本文型別不在 LogForesight.Web.Models* 下，DefaultInputLimitsFilter 不會套預設上限：" + string.Join("、", offenders));
    }

    private static bool HasExplicitSource(ParameterInfo prm) =>
        prm.GetCustomAttributes(true).Any(a => a is Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata);

    private static bool IsRouteParameter(MethodInfo method, ParameterInfo prm)
    {
        var templates = method.GetCustomAttributes(true).OfType<Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider>()
            .Select(a => a.Template).Where(t => t != null);
        return templates.Any(t => t!.Contains("{" + prm.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsComplex(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(CancellationToken)
            || t == typeof(DateOnly) || t == typeof(TimeOnly)) return false;
        if (t.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true) return false;
        return true;
    }
}
