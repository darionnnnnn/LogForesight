using System.Net;
using System.Text;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Middleware;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// CSP 違規回報端點（<see cref="CspReportController"/>）與 CSRF 標頭中介層對該路徑的精確放行。
/// 控制器直接以 DefaultHttpContext 驅動；紀錄以 NLog MemoryTarget 收集（logger 名固定）。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class CspReportControllerTests : IDisposable
{
    private readonly MemoryTarget _target = new("lf-csp-report-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
    private readonly LoggingConfiguration _config;

    public CspReportControllerTests()
    {
        _config = LogManager.Configuration ??= new LoggingConfiguration();
        _config.AddTarget(_target);
        _config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, _target, CspReportController.LoggerName);
        LogManager.ReconfigExistingLoggers();
    }

    public void Dispose()
    {
        _config.RemoveTarget(_target.Name);
        _config.LoggingRules.Where(r => r.Targets.Contains(_target)).ToList().ForEach(r => _config.LoggingRules.Remove(r));
        LogManager.ReconfigExistingLoggers();
    }

    private const string Legacy =
        """{"csp-report":{"document-uri":"https://lf/x","violated-directive":"script-src-elem","blocked-uri":"https://evil/a.js","original-policy":"default-src 'self'"}}""";

    private static CspReportController NewController(CspReportIpThrottle? ip = null, CspReportDedupThrottle? dedup = null)
        => new(ip ?? new CspReportIpThrottle(), dedup ?? new CspReportDedupThrottle());

    private static async Task<int> Post(CspReportController controller, byte[] body, string contentType, string? remoteIp = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        if (remoteIp != null) context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var result = await controller.Report();
        return result switch
        {
            StatusCodeResult s => s.StatusCode,
            _ => throw new InvalidOperationException("未預期的結果型別 " + result.GetType().Name)
        };
    }

    private static Task<int> Post(CspReportController controller, string body, string contentType, string? remoteIp = "10.0.0.1")
        => Post(controller, Encoding.UTF8.GetBytes(body), contentType, remoteIp);

    private List<string> Logs => _target.Logs.ToList();

    [Fact]
    public async Task csp_report舊式格式_204並記錄三個欄位()
    {
        Assert.Equal(204, await Post(NewController(), Legacy, "application/csp-report"));

        var line = Assert.Single(Logs);
        Assert.StartsWith("Info|", line);
        Assert.Contains("https://lf/x", line);
        Assert.Contains("script-src-elem", line);
        Assert.Contains("https://evil/a.js", line);
        Assert.Contains("default-src 'self'", line);
    }

    [Fact]
    public async Task reports_json陣列格式_204並記錄三個欄位()
    {
        const string body = """
            [{"type":"deprecation","body":{"id":"x"}},
             {"type":"csp-violation","body":{"documentURL":"https://lf/y","effectiveDirective":"img-src","blockedURL":"https://evil/p.png","originalPolicy":"default-src 'self'"}}]
            """;

        Assert.Equal(204, await Post(NewController(), body, "application/reports+json"));

        var line = Assert.Single(Logs);
        Assert.Contains("https://lf/y", line);
        Assert.Contains("img-src", line);
        Assert.Contains("https://evil/p.png", line);
    }

    [Fact]
    public async Task application_json兩種形狀都可解析()
    {
        var controller = NewController();
        Assert.Equal(204, await Post(controller, Legacy, "application/json"));
        const string reporting = """[{"type":"csp-violation","body":{"documentURL":"d","effectiveDirective":"font-src","blockedURL":"https://evil/f.woff","originalPolicy":"p"}}]""";
        Assert.Equal(204, await Post(controller, reporting, "application/json"));

        Assert.Equal(2, Logs.Count);
        Assert.Contains(Logs, l => l.Contains("script-src-elem"));
        Assert.Contains(Logs, l => l.Contains("font-src"));
    }

    [Fact]
    public async Task 本文超過8192_bytes回413且零紀錄()
    {
        var body = Legacy.Replace("https://evil/a.js", new string('a', 9000));

        Assert.Equal(413, await Post(NewController(), body, "application/csp-report"));
        Assert.Empty(Logs);
    }

    [Fact]
    public async Task 剛好8192_bytes不被擋()
    {
        var pad = 8192 - Encoding.UTF8.GetByteCount(Legacy);
        var body = Legacy.Replace("https://lf/x", "https://lf/x" + new string('x', pad));
        Assert.Equal(8192, Encoding.UTF8.GetByteCount(body));

        Assert.Equal(204, await Post(NewController(), body, "application/csp-report"));
        Assert.Single(Logs);
    }

    [Fact]
    public async Task 同IP第11筆回429且不記錄()
    {
        var controller = NewController();
        for (var i = 0; i < 10; i++)
            Assert.Equal(204, await Post(controller, Legacy.Replace("a.js", $"a{i}.js"), "application/csp-report"));
        var before = Logs.Count;

        Assert.Equal(429, await Post(controller, Legacy.Replace("a.js", "a99.js"), "application/csp-report"));
        Assert.Equal(before, Logs.Count);
        // 不同 IP 不受影響
        Assert.Equal(204, await Post(controller, Legacy.Replace("a.js", "b.js"), "application/csp-report", "10.0.0.2"));
    }

    [Fact]
    public async Task 同指令同blocked_uri第2筆回204但不記錄()
    {
        var controller = NewController();
        Assert.Equal(204, await Post(controller, Legacy, "application/csp-report"));
        Assert.Equal(204, await Post(controller, Legacy, "application/csp-report"));

        Assert.Single(Logs);
    }

    [Fact]
    public async Task 欄位含換行與控制字元時記錄內容只剩空白且各欄截512字()
    {
        var body = Legacy.Replace("https://evil/a.js", "https://evil/\\r\\nFAKE\\t\\u0085X").Replace("https://lf/x", new string('d', 700));

        Assert.Equal(204, await Post(NewController(), body, "application/csp-report"));

        var line = Assert.Single(Logs);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("https://evil/  FAKE  X", line);
        Assert.Contains(new string('d', 512), line);
        Assert.DoesNotContain(new string('d', 513), line);
    }

    [Theory]
    [InlineData("not json", "application/csp-report")]
    [InlineData("{}", "application/json")]
    [InlineData("[1,2]", "application/reports+json")]
    [InlineData("", "application/json")]
    public async Task 無法解析回204且零紀錄(string body, string contentType)
    {
        Assert.Equal(204, await Post(NewController(), body, contentType));
        Assert.Empty(Logs);
    }

    [Fact]
    public async Task 遠端位址為null時以unknown計入節流()
    {
        var ip = new CspReportIpThrottle();
        var controller = NewController(ip);

        Assert.Equal(204, await Post(controller, Legacy, "application/csp-report", remoteIp: null));

        // 已用掉 1 次：再用 9 次仍在額度內，第 10 次（總第 11 次）被拒
        for (var i = 0; i < 9; i++) Assert.True(ip.TryAcquire("unknown", DateTime.UtcNow));
        Assert.False(ip.TryAcquire("unknown", DateTime.UtcNow));
    }

    // ---- CsrfHeaderMiddleware：只精確放行 POST /api/csp-report ----

    private static async Task<(bool NextCalled, int Status)> RunCsrf(string method, string path)
    {
        var called = false;
        var middleware = new CsrfHeaderMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return (called, context.Response.StatusCode);
    }

    [Fact]
    public async Task Csrf_POST_csp_report無標頭放行()
    {
        var (called, _) = await RunCsrf("POST", "/api/csp-report");
        Assert.True(called);
    }

    [Theory]
    [InlineData("POST", "/api/csp-report-x")]
    [InlineData("POST", "/api/csp-report/extra")]
    [InlineData("POST", "/api/other")]
    [InlineData("DELETE", "/api/csp-report")]
    [InlineData("PUT", "/api/csp-report")]
    public async Task Csrf_其他非GET_API無標頭仍被擋(string method, string path)
    {
        var (called, status) = await RunCsrf(method, path);
        Assert.False(called);
        Assert.Equal(400, status);
    }
}

