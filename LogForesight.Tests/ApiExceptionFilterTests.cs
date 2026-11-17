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
    public void 協調器規則例外_回400驗證錯誤與原中文訊息_不記Error()
    {
        var ctx = NewContext(new LogForesight.Core.Persistence.WorkOrderRuleException("交辦單 1 已結案。"), aborted: false);
        var logs = RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.Error!.Code);
        Assert.Equal("交辦單 1 已結案。", body.Error.Message);
        Assert.DoesNotContain(logs, l => l.StartsWith("Error|") || l.StartsWith("Fatal|"));
    }

    [Fact]
    public void 框架層InvalidOperationException仍是500固定說明_原文不外洩()
    {
        var ctx = NewContext(new InvalidOperationException("Sequence contains no elements"), aborted: false);
        RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.DoesNotContain("Sequence", Assert.IsType<ApiResponse<object>>(result.Value).Error!.Message);
    }

    [Fact]
    public void NetiqDiscoveryException回400_PrtgScopeConflictException回409()
    {
        var discovery = NewContext(new LogForesight.Web.Services.NetiqDiscoveryException("網段格式不正確。"), aborted: false);
        RunFilter(discovery);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(discovery.Result).StatusCode);

        var conflict = NewContext(new LogForesight.Core.Persistence.Sql.PrtgScopeConflictException(), aborted: false);
        RunFilter(conflict);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(conflict.Result).StatusCode);
    }

    /// <summary>
    /// 守門：<see cref="UserFacingMessage"/> 登錄的專屬例外型別，過濾器都要有對應的 arm（否則訊息雖標為可給使用者看，
    /// 沒被服務層攔下時會變成 500 固定說明）。目前沒有例外理由；真有不需要對應的型別要在這裡明列並寫理由。
    /// </summary>
    [Fact]
    public void UserFacingMessage登錄的型別在ApiExceptionFilter都有對應()
    {
        var registered = (IEnumerable<Type>)typeof(UserFacingMessage)
            .GetField("RegisteredTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        var filterSource = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Filters", "ApiExceptionFilter.cs"));
        var exempt = new Dictionary<Type, string>();

        var missing = registered
            .Where(t => !exempt.ContainsKey(t) && !HasSwitchArm(filterSource, t.Name))
            .Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, "以下已登錄為使用者可見的例外型別在 ApiExceptionFilter 沒有對應 arm：\n" + string.Join("\n", missing));
    }

    /// <summary>過濾器 switch 的型別 pattern arm：「型別名 變數 =&gt;」。先剝掉註解，註解裡提到型別名不算有對應。</summary>
    private static bool HasSwitchArm(string source, string typeName)
    {
        var code = System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w])" + System.Text.RegularExpressions.Regex.Escape(typeName) + @"\s+\w+\s*(when\s[^=]*)?=>");
    }

    [Fact]
    public void Switch對應判定_只在註解出現型別名時比不到_真正的arm比得到()
    {
        const string onlyComment = "// FooException foo => (400)\n/// <see cref=\"FooException\"/> FooException \nvar x = 1;";
        Assert.False(HasSwitchArm(onlyComment, "FooException"));
        const string realArm = "context.Exception switch\n{\n    LogForesight.Core.FooException foo => (400, \"x\", foo.Message),\n    _ => (500, \"y\", \"z\")\n};";
        Assert.True(HasSwitchArm(realArm, "FooException"));
        const string withWhen = "OperationCanceledException when ok => (1)";
        Assert.True(HasSwitchArm("BarException bar when ok => (1)", "BarException"));
        Assert.False(HasSwitchArm(withWhen, "FooException"));
    }

    [Fact]
    public void RequestAborted已取消_回499且不記Warn以上()
    {
        var ctx = NewContext(new OperationCanceledException(), aborted: true);
        var logs = RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(499, result.StatusCode);
        Assert.Equal(ApiErrorCodes.RequestCancelled, Assert.IsType<ApiResponse<object>>(result.Value).Error!.Code);
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
    public void PrtgScopePurgeBusyException_回409_conflict_與原訊息()
    {
        var ctx = NewContext(new LogForesight.Core.Persistence.PrtgScopePurgeBusyException("x"), aborted: false);
        RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(409, result.StatusCode);
        var error = Assert.IsType<ApiResponse<object>>(result.Value).Error!;
        Assert.Equal("conflict", error.Code);
        Assert.Equal("x", error.Message);
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

    [Theory]
    [InlineData("transfer_binding_changed", 403)]
    [InlineData("transfer_context_changed", 409)]
    [InlineData("chunk_shape_invalid", 400)]
    [InlineData("transfer_not_found", 404)]
    [InlineData("transfer_store_busy", 503)]
    public void PRTG診斷傳輸儲存例外_狀態碼與錯誤碼取自拋出點(string code, int httpStatus)
    {
        var ctx = NewContext(new LogForesight.Core.Persistence.Sql.PrtgTransferStoreException(code, "傳輸錯誤訊息", httpStatus), aborted: false);

        RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(httpStatus, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(body.Success);
        Assert.Equal(code, body.Error!.Code);
        Assert.Equal("傳輸錯誤訊息", body.Error.Message);
        Assert.True(ctx.ExceptionHandled);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.IO.InvalidDataException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(System.IO.EndOfStreamException))]
    public void 通用型別例外不被全域對照_仍為500通用訊息(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "內部細節不得外洩")!;
        var ctx = NewContext(ex, aborted: false);

        RunFilter(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(500, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.Equal(ApiErrorCodes.ServerError, body.Error!.Code);
        Assert.DoesNotContain("內部細節", body.Error.Message);
    }
}
