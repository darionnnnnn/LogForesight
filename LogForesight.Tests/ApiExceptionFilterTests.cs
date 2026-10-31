using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>錯誤碼登錄表、取消／逾時分流、today-focus 天數夾範圍。</summary>
[Collection("NLogGlobalConfig")]
public class ApiExceptionFilterTests
{
    private static ExceptionContext NewContext(Exception ex, bool aborted)
    {
        var cts = new CancellationTokenSource();
        if (aborted) cts.Cancel();
        var http = new DefaultHttpContext { RequestAborted = cts.Token };
        http.Request.Method = "GET";
        http.Request.Path = "/api/test";
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = ex };
    }

    /// <summary>掛 NLog 記憶體 target 抓 filter 的紀錄，結束後卸掉</summary>
    private static List<string> RunFilter(ExceptionContext ctx)
    {
        var target = new MemoryTarget("lf-api-ex-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target, typeof(ApiExceptionFilter).FullName);
        LogManager.ReconfigExistingLoggers();
        try
        {
            new ApiExceptionFilter().OnException(ctx);
            return target.Logs.ToList();
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }

    private static bool IsWarnOrAbove(string l) =>
        l.StartsWith("Warn|") || l.StartsWith("Error|") || l.StartsWith("Fatal|");

    [Fact]
    public void RequestAborted已取消_回499且不記Warn以上()
    {
        var ctx = NewContext(new OperationCanceledException(), aborted: true);
        var logs = RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(499, result.StatusCode);
        Assert.True(ctx.ExceptionHandled);
        Assert.DoesNotContain(logs, IsWarnOrAbove);
    }

    [Fact]
    public void 未取消的TaskCanceled_回503_upstream_timeout且只記Warn()
    {
        var ctx = NewContext(new TaskCanceledException("timeout"), aborted: false);
        var logs = RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(503, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.Equal("upstream_timeout", body.Error!.Code);
        Assert.Equal(1, logs.Count(l => l.StartsWith("Warn|")));
        Assert.DoesNotContain(logs, l => l.StartsWith("Error|"));
    }

    [Fact]
    public void DomainException_NotFound回404()
    {
        var ctx = NewContext(new DomainException(ApiErrorCodes.NotFound, "找不到"), aborted: false);
        RunFilter(ctx);
        Assert.Equal(404, Assert.IsType<ObjectResult>(ctx.Result).StatusCode);
    }

    [Fact]
    public void 一般例外回500並記Error()
    {
        var ctx = NewContext(new InvalidOperationException("boom"), aborted: false);
        var logs = RunFilter(ctx);
        Assert.Equal(500, Assert.IsType<ObjectResult>(ctx.Result).StatusCode);
        Assert.Contains(logs, l => l.StartsWith("Error|"));
    }

    [Fact]
    public void ApiErrorCodes每個const都有明確狀態碼對照()
    {
        var codes = typeof(ApiErrorCodes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(codes);
        foreach (var code in codes)
        {
            if (code == ApiErrorCodes.ValidationFailed) continue; // 表外預設 400
            Assert.True(ApiErrorCodes.Table.ContainsKey(code), $"錯誤碼 {code} 未登錄狀態碼");
        }
        Assert.Equal(400, ApiErrorCodes.StatusOf("validation_failed"));
        Assert.Equal(503, ApiErrorCodes.StatusOf("upstream_timeout"));
        Assert.Equal(400, ApiErrorCodes.StatusOf("totally_unknown"));
    }

    [Theory]
    [InlineData(null, DashboardController.DefaultDays)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(91, 90)]
    [InlineData(100000, 90)]
    [InlineData(30, 30)]
    public void ClampDays夾在1到90(int? input, int expected) =>
        Assert.Equal(expected, DashboardController.ClampDays(input));

    [Fact]
    public void 兩個控制器不再散落Clamp1_90與未夾的GetSummary()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LogForesight.Web", "Controllers", "Api"));
        var ai = File.ReadAllText(Path.Combine(dir, "AiController.cs"));
        var dash = File.ReadAllText(Path.Combine(dir, "DashboardController.cs"));
        Assert.DoesNotContain("GetSummary(days ??", ai);
        Assert.DoesNotContain("Math.Clamp(days ?? DefaultDays, 1, 90)", ai);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(dash, @"Math\.Clamp\([^)]*,\s*1,\s*90\)").Count);
    }
}
