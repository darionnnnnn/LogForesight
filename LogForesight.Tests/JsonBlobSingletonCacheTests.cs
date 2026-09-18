using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
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
/// 回饋四十五輪 B3：資料版本戳的失效白名單（<see cref="DataVersionStampPolicy"/>）。
/// 走的是真正掛在管線上的那個方法，不另抄一份判定。
/// </summary>
public class DataVersionStampPolicyTests
{
    private static (HttpContext Context, DataVersionStamp Stamp) Request(string method, string path, int status = 200)
    {
        var services = new ServiceCollection();
        var stamp = new DataVersionStamp();
        services.AddSingleton(stamp);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.StatusCode = status;
        return (context, stamp);
    }

    /// <summary>驗收 6：白名單內的成功非 GET 請求不推進版本戳</summary>
    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/help/ask")]
    [InlineData("/api/ai/chat")]
    public void 白名單端點_不推進版本戳(string path)
    {
        var (context, stamp) = Request("POST", path);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(0, stamp.Current);
    }

    /// <summary>驗收 6 反例：會改分析資料的端點（處理狀態寫入）必須推進</summary>
    [Theory]
    [InlineData("PUT", "/api/records/12/2026-09-16/handling")]
    [InlineData("PUT", "/api/handling/issue-cases/assign")]
    [InlineData("POST", "/api/work-orders/12/reply")]
    public void 處理狀態寫入_推進版本戳(string method, string path)
    {
        var (context, stamp) = Request(method, path);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(1, stamp.Current);
    }

    /// <summary>驗收 6：白名單是排除法——未知（將來新增）的路徑預設仍推進</summary>
    [Fact]
    public void 未知路徑_預設推進版本戳()
    {
        var (context, stamp) = Request("POST", "/api/something-brand-new");

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(1, stamp.Current);
    }

    /// <summary>GET 與失敗回應一律不推進（既有行為，不得被白名單改動連帶破壞）</summary>
    [Theory]
    [InlineData("GET", "/api/records", 200)]
    [InlineData("HEAD", "/api/records", 200)]
    [InlineData("POST", "/api/work-orders/12/reply", 400)]
    [InlineData("POST", "/api/work-orders/12/reply", 500)]
    public void GET與失敗回應_不推進版本戳(string method, string path, int status)
    {
        var (context, stamp) = Request(method, path, status);

        DataVersionStampPolicy.BumpIfNeeded(context);

        Assert.Equal(0, stamp.Current);
    }
}
