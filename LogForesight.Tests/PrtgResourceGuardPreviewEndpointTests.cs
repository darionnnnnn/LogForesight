using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public class PrtgResourceGuardPreviewEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly RecordingAuditService _audit = new();
    private readonly SettingsController _controller;

    public PrtgResourceGuardPreviewEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-guard-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);

        _controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: _backend);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "test-admin"),
            new Claim(JwtTokenService.AccountClaim, "test-admin")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new();
        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public bool SetPrtgEnabled(bool enabled) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task 預覽不要求PrtgEnabled與PrtgResourceGuardEnabled_兩者皆false但憑證齊備仍回得出結果()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var json = "{\"prtg-version\":\"23.4\",\"treesize\":1,\"sensors\":[{\"objid\":1001,\"device\":\"TestHost\",\"sensor\":\"CPU Load\",\"status\":\"Up\",\"lastvalue\":\"15 %\"}]}";
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var store = new SystemSettingsStore(_backend.Blob("system_settings"));
        store.Update(s =>
        {
            s.PrtgEnabled = false;
            s.PrtgResourceGuardEnabled = false;
            s.PrtgUrl = $"http://127.0.0.1:{port}";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("my-token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgResourceGuardSensorObjids = new List<string> { "1001" };
        });

        var res = await _controller.PreviewPrtgResourceGuard(CancellationToken.None);
        await serverTask;

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.True(res.Data.Success);
        Assert.Null(res.Data.ErrorMessage);
        Assert.Equal("override", res.Data.Source);
        Assert.Single(res.Data.Sensors);
        Assert.Equal(1001, res.Data.Sensors[0].Objid);
        Assert.Equal("TestHost", res.Data.Sensors[0].Device);
        Assert.Equal("CPU Load", res.Data.Sensors[0].Sensor);
        Assert.Equal(15.0, res.Data.Sensors[0].Percentage);
    }

    [Fact]
    public async Task 憑證未設定時回錯誤說明而非擲例外()
    {
        var store = new SystemSettingsStore(_backend.Blob("system_settings"));
        store.Update(s =>
        {
            s.PrtgUrl = "";
            s.PrtgApiTokenEnc = "";
            s.PrtgPasswordEnc = "";
            s.PrtgPasshashEnc = "";
        });

        var res = await _controller.PreviewPrtgResourceGuard(CancellationToken.None);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.False(res.Data.Success);
        Assert.False(string.IsNullOrWhiteSpace(res.Data.ErrorMessage));
        Assert.Contains("尚未設定", res.Data.ErrorMessage);
        Assert.Empty(res.Data.Sensors);
    }

    [Fact]
    public async Task 覆寫清單時Source為override_留空時為auto()
    {
        var store = new SystemSettingsStore(_backend.Blob("system_settings"));

        // 1. 覆寫清單有值
        store.Update(s =>
        {
            s.PrtgUrl = "";
            s.PrtgResourceGuardSensorObjids = new List<string> { "9001", "9002" };
        });

        var resOverride = await _controller.PreviewPrtgResourceGuard(CancellationToken.None);
        Assert.True(resOverride.Success);
        Assert.NotNull(resOverride.Data);
        Assert.Equal("override", resOverride.Data.Source);

        // 2. 留空（自動偵測）
        store.Update(s =>
        {
            s.PrtgUrl = "";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var resAuto = await _controller.PreviewPrtgResourceGuard(CancellationToken.None);
        Assert.True(resAuto.Success);
        Assert.NotNull(resAuto.Data);
        Assert.Equal("auto", resAuto.Data.Source);
    }

    /// <summary>
    /// 鏡像是空的（PRTG 剛啟用、還沒同步過）時改為直接查 PRTG；查不通就退回鏡像並標明來源
    /// （docs/PRTG-SPEC.md §12）。靜默退回會讓使用者以為「PRTG 上真的沒有這些裝置」。
    /// </summary>
    [Fact]
    public async Task 鏡像為空時改查PRTG_查不通則退回鏡像並標明來源()
    {
        var store = new SystemSettingsStore(_backend.Blob("system_settings"));
        store.Update(s =>
        {
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var res = await _controller.PreviewPrtgResourceGuard(CancellationToken.None);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal("mirror-fallback", res.Data!.Source);
        Assert.Contains(res.Data.Warnings, w => w.Contains("直接查詢 PRTG 失敗"));
    }
}
