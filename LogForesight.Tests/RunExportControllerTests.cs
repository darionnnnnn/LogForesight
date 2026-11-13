using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>GET /api/runs/export 的控制器行為（權限、單一併發、天數夾限、檔名與稽核、失敗與取消）</summary>
public class RunExportControllerTests : IDisposable
{
    private readonly RunExportHarness _h = new();
    private readonly StubRunExportService _stub = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public void Dispose() => _h.Dispose();

    private RunsController Create(IRunExportService? export = null, IAuditService? audit = null, CancellationToken ct = default)
    {
        var controller = new RunsController(_h.Monitor, _settings, export ?? _stub, audit ?? _audit)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = ct } }
        };
        return controller;
    }

    private static RunExportResult Result(int days = 14, string content = "{\"ok\":true}") =>
        new(System.Text.Encoding.UTF8.GetBytes(content), days, 3, 2, 1, 0, new Dictionary<string, int> { ["host"] = 2 });

    [Fact]
    public void serverAdmin回403_有檢視全部權限的Dev放行()
    {
        var controller = Create();
        ViewAllGuard.AssertForbidden(controller, nameof(RunsController.Export), FakeCurrentUser.ServerAdmin());
        ViewAllGuard.AssertAllowed(controller, nameof(RunsController.Export),
            FakeCurrentUser.WithCapabilities(Capability.ViewAll, Capability.DevMonitor));
        // 類別層的能力判定與其他 runs 端點相同
        Assert.NotEmpty(typeof(RunsController).GetCustomAttributes(typeof(PermissionAttribute), true));
    }

    [Fact]
    public void Dev取得200_application_json_檔名含日期與天數_內容原樣()
    {
        _stub.Handler = (d, _) => Result(d);
        var file = Assert.IsType<FileContentResult>(Create().Export(7));

        Assert.Equal("application/json", file.ContentType);
        Assert.Equal($"run-diagnostics-{DateTime.Today:yyyyMMdd}-7d.json", file.FileDownloadName);
        Assert.Equal("{\"ok\":true}", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Theory]
    [InlineData(9999, 90, 90)]
    [InlineData(9999, 120, 120)]
    [InlineData(0, 120, 1)]
    [InlineData(-5, 120, 1)]
    [InlineData(30, 120, 30)]
    public void days被夾在1到上限之間_上限同畫面為max90與保留天數(int requested, int retention, int expected)
    {
        _settings.Update(s => s.RunLogRetentionDays = retention);
        _stub.Handler = (d, _) => Result(d);

        var file = Assert.IsType<FileContentResult>(Create().Export(requested));

        Assert.Equal(new[] { expected }, _stub.Calls);
        Assert.Contains($"-{expected}d.json", file.FileDownloadName);
    }

    [Fact]
    public void 稽核一筆_不含內容_只有天數組數趟數位元組數與代號數()
    {
        _stub.Handler = (d, _) => Result(d, "{\"secret-looking\":\"SRV-ORIGINAL\"}");
        Create().Export(14);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.RunDiagnosticsExport, entry.Action);
        Assert.Contains("\"Days\":14", entry.DetailJson);
        Assert.Contains("\"SignalGroups\":2", entry.DetailJson);
        Assert.Contains("\"Runs\":3", entry.DetailJson);
        Assert.Contains("\"Bytes\":", entry.DetailJson);
        Assert.Contains("\"host\":2", entry.DetailJson);
        Assert.DoesNotContain("SRV-ORIGINAL", entry.DetailJson + entry.Summary);
        Assert.True(AuditQueryService.ActionNamesForTest.ContainsKey(AuditActions.RunDiagnosticsExport));
    }

    [Fact]
    public void 稽核寫入失敗不中斷匯出()
    {
        var file = Create(audit: new ThrowingAudit()).Export(14);
        Assert.IsType<FileContentResult>(file);
    }

    private sealed class ThrowingAudit : IAuditService
    {
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) => throw new InvalidOperationException("稽核壞了");

        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    [Fact]
    public void 去識別化失敗_沒有檔案內容_沒有稽核_信號量釋放_例外交給filter轉500()
    {
        var failure = new RunExportDeidentificationException(new Dictionary<string, int> { ["dictionary"] = 2 });
        _stub.Handler = (_, _) => throw failure;

        var ex = Assert.Throws<RunExportDeidentificationException>(() => Create().Export(14));
        Assert.Same(failure, ex);
        Assert.Empty(_audit.Entries);

        // filter 轉成 500 export_deidentification_failed，回應是錯誤信封而不是檔案
        var httpContext = new DefaultHttpContext();
        var context = new ExceptionContext(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>())
        {
            Exception = ex
        };
        new ApiExceptionFilter().OnException(context);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(ApiErrorCodes.ExportDeidentificationFailed, Assert.IsType<ApiResponse<object>>(result.Value).Error!.Code);

        // 失敗後閘門已釋放，下一次可以成功
        _stub.Handler = (d, _) => Result(d);
        Assert.IsType<FileContentResult>(Create().Export(14));
    }

    [Fact]
    public void 真實服務去識別化失敗時沒有檔案_端對端()
    {
        _h.AddHost("SRV-LEAK");
        var run = _h.AddRun();
        _h.AddLog(run, "Error", "Orchestrator", "LEAKME SRV-LEAK");
        var service = _h.Service(masker: () => new LeakingMasker(_h.Factory.Create()));

        Assert.Throws<RunExportDeidentificationException>(() => Create(service).Export(14));
        Assert.Empty(_audit.Entries);
    }

    private sealed class LeakingMasker : IRunExportMasker
    {
        private readonly RunExportDeidentifier _inner;
        public LeakingMasker(RunExportDeidentifier inner) => _inner = inner;
        public string Mask(string? text) => text != null && text.Contains("LEAKME") ? text : _inner.Mask(text);
        public string? MaskHost(string? hostText) => _inner.MaskHost(hostText);
        public string MaskStack(string? stack) => _inner.MaskStack(stack);
        public IReadOnlyDictionary<string, int> TokenCounts => _inner.TokenCounts;

        public void EnsureNoResidual(string json, IEnumerable<string>? allowedLiterals = null, bool scanPropertyNames = true) =>
            _inner.EnsureNoResidual(json, allowedLiterals, scanPropertyNames);
    }

    [Fact]
    public void 同時第二個匯出回503_capture_busy_第一個完成後閘門釋放()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _stub.Handler = (d, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            return Result(d);
        };

        var first = Task.Run(() => Create().Export(14));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            var second = Assert.IsType<ObjectResult>(Create(new StubRunExportService()).Export(14));
            Assert.Equal(503, second.StatusCode);
            var body = Assert.IsAssignableFrom<ApiResponse<object>>(second.Value);
            Assert.Equal(ApiErrorCodes.CaptureBusy, body.Error!.Code);
            Assert.Contains("診斷資料匯出", body.Error!.Message);
            Assert.DoesNotContain("校準", body.Error!.Message);
        }
        finally
        {
            release.Set();
        }

        Assert.IsType<FileContentResult>(first.GetAwaiter().GetResult());
        _stub.Handler = (d, _) => Result(d);
        Assert.IsType<FileContentResult>(Create().Export(14));
    }

    [Fact]
    public void 取消_不寫稽核_信號量釋放_例外由filter轉499()
    {
        using var cts = new CancellationTokenSource();
        _stub.Handler = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Result();
        };
        Assert.Throws<OperationCanceledException>(() => Create(ct: cts.Token).Export(14));
        Assert.Empty(_audit.Entries);

        // 服務回來之後才發現已取消，一樣不寫稽核
        using var cts2 = new CancellationTokenSource();
        _stub.Handler = (d, _) =>
        {
            cts2.Cancel();
            return Result(d);
        };
        Assert.Throws<OperationCanceledException>(() => Create(ct: cts2.Token).Export(14));
        Assert.Empty(_audit.Entries);

        _stub.Handler = (d, _) => Result(d);
        Assert.IsType<FileContentResult>(Create().Export(14));
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public void 容量超過的DomainException原樣穿出_交給filter轉413()
    {
        _stub.Handler = (_, _) => throw new DomainException(ApiErrorCodes.CapacityExceeded, "太大");
        var ex = Assert.Throws<DomainException>(() => Create().Export(14));
        Assert.Equal(413, ApiErrorCodes.StatusOf(ex.Code));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 真實服務端對端_回傳的檔案可解析且稽核不含原值()
    {
        _h.AddHost("SRV-REAL-E2E", "10.9.9.9");
        var run = _h.AddRun("SRV-REAL-E2E");
        _h.AddLog(run, "Error", "Orchestrator", "SRV-REAL-E2E 10.9.9.9 失敗");

        var file = Assert.IsType<FileContentResult>(Create(_h.Service()).Export(14));

        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.Equal(RunExportService.SchemaVersion, doc.RootElement.GetProperty("meta").GetProperty("schemaVersion").GetInt32());
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.DoesNotContain("SRV-REAL-E2E", text);
        Assert.DoesNotContain("10.9.9.9", text);
        var entry = Assert.Single(_audit.Entries);
        Assert.DoesNotContain("SRV-REAL-E2E", entry.DetailJson);
    }
}
