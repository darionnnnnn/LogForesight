using LogForesight.Web.Filters;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋四十五輪 B3：<see cref="JsonBlobSingleton{T}"/> 的版本探測快取。
///
/// 計數方式不是自訂替身，而是**真的去數 SQL**：<see cref="EfJsonBlobStore"/> 是 sealed、
/// 無介面，唯一能誠實分辨「讀內容」與「探測版本」的位置就是它送出的查詢本身
/// （讀內容的查詢會選 Content 欄，探測版本的只選 Version 欄）。
/// </summary>
public class JsonBlobSingletonCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = new();

    public JsonBlobSingletonCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
        _sql.Clear();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .LogTo(line => { lock (_sql) _sql.Add(line); },
                new[] { Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted })
            .Options);

    private EfJsonBlobStore Blob(string key) => new(NewContext, key);

    /// <summary>讀取整份內容的查詢次數（含 Read 與 ReadWithVersion，兩者都會選出 content 欄）</summary>
    private int ContentReads()
    {
        lock (_sql) return _sql.Count(s => s.Contains("SELECT") && s.Contains("\"content\""));
    }

    /// <summary>只探測版本的查詢次數（ReadVersion 只選 version 欄、不碰 content）</summary>
    private int VersionProbes()
    {
        lock (_sql)
            return _sql.Count(s => s.Contains("SELECT") && s.Contains("\"version\"") && !s.Contains("\"content\""));
    }

    /// <summary>驗收 1：連續兩次 Get，內容只讀一次；版本探測允許多次</summary>
    [Fact]
    public void 開啟快取_連續兩次Get_只讀一次內容()
    {
        var store = new SystemSettingsStore(Blob("system_settings"));
        store.Update(s => s.RawEventRetentionDays = 33);
        _sql.Clear();

        var first = store.Get();
        var second = store.Get();

        Assert.Equal(33, first.RawEventRetentionDays);
        Assert.Equal(33, second.RawEventRetentionDays);
        Assert.Equal(1, ContentReads());
        Assert.True(VersionProbes() >= 2, $"版本探測應每次都做，實際 {VersionProbes()} 次");
    }

    /// <summary>
    /// 驗收 2（副本反例）：呼叫端改了 Get() 回傳的物件後，下一次 Get() 不受影響。
    /// 這是本階段的核心風險——若快取共用同一個實例，SystemSettingsService 的讀→改→寫
    /// 前後快照會變成同一個物件。
    /// </summary>
    [Fact]
    public void 開啟快取_呼叫端修改回傳物件_不影響下一次Get()
    {
        var store = new SystemSettingsStore(Blob("system_settings"));
        store.Update(s => s.RawEventRetentionDays = 33);

        // 先讀一次把快取填起來——之後每一次 Get 都走命中路徑，命中路徑共用實例才驗得到
        store.Get();

        var first = store.Get();
        first.RawEventRetentionDays = 999;

        var second = store.Get();

        Assert.Equal(33, second.RawEventRetentionDays);
        Assert.NotSame(first, second);
    }

    /// <summary>驗收 3：Update 推進版本，下一次 Get 讀到新值</summary>
    [Fact]
    public void 開啟快取_Update後Get讀到新值()
    {
        var store = new SystemSettingsStore(Blob("system_settings"));
        store.Update(s => s.RawEventRetentionDays = 33);
        Assert.Equal(33, store.Get().RawEventRetentionDays);

        store.Update(s => s.RawEventRetentionDays = 44);

        Assert.Equal(44, store.Get().RawEventRetentionDays);
    }

    /// <summary>驗收 4：未開啟快取的子類行為完全不變——兩次 Get 都去讀內容、完全不探測版本</summary>
    [Fact]
    public void 未開啟快取的子類_每次Get都讀DB()
    {
        var store = new ScheduleOptionsStore(Blob("schedule_options"));
        store.Update(o => o.Enabled = true);
        _sql.Clear();

        store.Get();
        store.Get();

        Assert.Equal(2, ContentReads());
        Assert.Equal(0, VersionProbes());
    }
}

/// <summary>
/// 資料版本戳的推進判定（<see cref="DataVersionStampPolicy"/>）：排除法由端點宣告
/// <see cref="SkipDataVersionBumpAttribute"/>，中介層只看端點中繼資料，不比對路徑字串。
/// 走的是真正掛在管線上的那個方法，不另抄一份判定。
/// </summary>
public class DataVersionStampPolicyTests
{
    private static (HttpContext Context, DataVersionStamp Stamp) Request(string method, string path, int status = 200, bool skip = false)
    {
        var services = new ServiceCollection();
        var stamp = new DataVersionStamp();
        services.AddSingleton(stamp);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.StatusCode = status;
        var metadata = skip
            ? new EndpointMetadataCollection(new SkipDataVersionBumpAttribute())
            : new EndpointMetadataCollection();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, path));
        return (context, stamp);
    }

    /// <summary>端點貼了 [SkipDataVersionBump]：成功的非 GET 請求也不推進版本戳（路徑長什麼樣都無關）</summary>
    [Theory]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/work-orders/15/seen/")]
    [InlineData("PUT", "/api/anything/else")]
    public void 有標記的端點_不推進版本戳(string method, string path)
    {
        var (context, stamp) = Request(method, path, skip: true);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(0, stamp.Current);
    }

    /// <summary>排除法：沒有標記的成功非 GET 一律推進，就算路徑長得像舊的尾碼清單（/candidates、/seen、login）</summary>
    [Theory]
    [InlineData("PUT", "/api/records/12/2026-09-16/handling")]
    [InlineData("PUT", "/api/handling/issue-cases/assign")]
    [InlineData("POST", "/api/work-orders/12/reply")]
    [InlineData("POST", "/api/work-orders/15/return")]
    [InlineData("POST", "/api/something-brand-new")]
    [InlineData("POST", "/api/something/candidates")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("PUT", "/api/prtg/monitoring")]
    public void 無標記的端點_推進版本戳(string method, string path)
    {
        var (context, stamp) = Request(method, path);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(1, stamp.Current);
    }

    /// <summary>沒有端點中繼資料（例如未匹配到端點的請求）也走預設：推進</summary>
    [Fact]
    public void 無端點時_預設推進版本戳()
    {
        var (context, stamp) = Request("POST", "/api/x");
        context.SetEndpoint(null);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(1, stamp.Current);
    }

    /// <summary>GET／HEAD 與失敗回應一律不推進（無論有無標記）</summary>
    [Theory]
    [InlineData("GET", "/api/records", 200, false)]
    [InlineData("HEAD", "/api/records", 200, false)]
    [InlineData("POST", "/api/work-orders/12/reply", 400, false)]
    [InlineData("POST", "/api/work-orders/12/reply", 500, false)]
    [InlineData("POST", "/api/work-orders/12/reply", 400, true)]
    public void GET與失敗回應_不推進版本戳(string method, string path, int status, bool skip)
    {
        var (context, stamp) = Request(method, path, status, skip);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(0, stamp.Current);
    }

    /// <summary>
    /// 不改變分析資料的端點必須貼 [SkipDataVersionBump]：原白名單、唯讀 POST 尾碼、診斷匯入搬運，
    /// 加上唯讀 POST 的預覽／試算／連線測試。以 MVC 實際列舉的端點中繼資料判定（與執行期同一份）。
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("POST", "/api/help/ask")]
    [InlineData("POST", "/api/ai/chat")]
    [InlineData("POST", "/api/work-orders/candidates")]
    [InlineData("POST", "/api/records/{hostId:long}/{date}/handling/assign/preview")]
    [InlineData("POST", "/api/work-orders/{id:long}/seen")]
    [InlineData("POST", "/api/admin/settings/prtg-import-transfers")]
    [InlineData("POST", "/api/admin/settings/prtg-import-transfers/{transferId:guid}/abandon")]
    [InlineData("PUT", "/api/admin/settings/prtg-import-transfers/{transferId:guid}/chunks/{ordinal:int}")]
    [InlineData("POST", "/api/admin/settings/prtg-import-transfers/{transferId:guid}/complete")]
    [InlineData("POST", "/api/ai/tidy-note")]
    [InlineData("POST", "/api/rules/validate")]
    [InlineData("POST", "/api/rules/disk-trend-preview")]
    [InlineData("POST", "/api/admin/settings/ad-test")]
    [InlineData("POST", "/api/admin/settings/mail-test")]
    [InlineData("POST", "/api/admin/settings/prtg-test")]
    [InlineData("POST", "/api/admin/sentinels/test-connection")]
    [InlineData("POST", "/api/work-orders/preview")]
    [InlineData("POST", "/api/admin/host-groups/{groupId:long}/members/preview")]
    [InlineData("POST", "/api/admin/settings/prtg-backfill/selected/preview")]
    [InlineData("POST", "/api/imports/{kind}/preview")]
    [InlineData("POST", "/api/prtg/monitoring/sensors")]
    [InlineData("POST", "/api/prtg/monitoring/estimate")]
    public void 唯讀與免推進端點_都貼了SkipDataVersionBump(string method, string route)
    {
        var matches = EndpointsByRoute().Where(d => d.Method == method && d.Route == route).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, d => Assert.True(d.Skip, $"{method} {route} 應貼 [SkipDataVersionBump]"));
    }

    /// <summary>反例：會寫分析資料或設定的端點不得貼標記（灰區與真寫入端點）</summary>
    [Theory]
    [InlineData("PUT", "/api/prtg/monitoring")]
    [InlineData("POST", "/api/work-orders/{id:long}/reply")]
    [InlineData("PUT", "/api/records/{hostId:long}/{date}/handling")]
    [InlineData("POST", "/api/health/freshness-ack")]
    public void 會寫資料的端點_沒有貼SkipDataVersionBump(string method, string route)
    {
        var matches = EndpointsByRoute().Where(d => d.Method == method && d.Route == route).ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, d => Assert.False(d.Skip, $"{method} {route} 不應貼 [SkipDataVersionBump]"));
    }

    /// <summary>舊的路徑清單機制不得殘留：Program.cs 的判定只看端點中繼資料，PRTG 監看範圍儲存也不再手動推進</summary>
    [Fact]
    public void 舊的路徑清單與手動Bump已移除()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Program.cs"));
        foreach (var legacy in new[] { "ReadOnlyPostSuffixes", "EndsWith(\"/candidates\"", "\"/api/auth/login\"", "\"/api/help/ask\"",
                     "Whitelist", "prtg-import-transfers" })
            Assert.DoesNotContain(legacy, program);
        var monitoring = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Controllers", "Api", "PrtgMonitoringController.cs"));
        Assert.DoesNotContain(".Bump()", monitoring);
    }

    private static IReadOnlyList<(string Method, string Route, bool Skip)> EndpointsByRoute()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(SkipDataVersionBumpAttribute).Assembly);
        using var provider = services.BuildServiceProvider();
        var result = new List<(string, string, bool)>();
        foreach (var d in provider.GetRequiredService<IActionDescriptorCollectionProvider>()
                     .ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var route = "/" + (d.AttributeRouteInfo?.Template ?? "").TrimStart('/');
            var skip = d.EndpointMetadata.OfType<SkipDataVersionBumpAttribute>().Any();
            foreach (var method in d.EndpointMetadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).Distinct())
                result.Add((method.ToUpperInvariant(), route, skip));
        }

        return result;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }
}
