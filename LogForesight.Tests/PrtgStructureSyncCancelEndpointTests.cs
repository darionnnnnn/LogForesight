using System.Security.Claims;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 中止結構同步的端點（docs/PRTG-SPEC.md §5a）。這條路徑在大型環境要跑數十分鐘，
/// 沒有它時唯一的中止方式是重啟站台，而重啟會讓當晚的取數也一起中斷。
/// </summary>
public class PrtgStructureSyncCancelEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly RecordingAuditService _audit = new();
    private readonly SystemSettingsStore _settingsStore;

    public PrtgStructureSyncCancelEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-sync-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PrtgStructureSyncService CreateSyncService() =>
        new(_settingsStore, _backend, new PrtgStructureSyncRunState(), new SchedulerRunState(),
            new HostStore(_backend.Blob("hosts")),
            new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)));

    private SettingsController CreateController(PrtgStructureSyncService? sync)
    {
        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            prtgStructureSync: sync,
            backend: _backend);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-admin"),
                new Claim(JwtTokenService.AccountClaim, "test-admin")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new();
        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private void EnablePrtg() => _settingsStore.Update(s =>
    {
        s.PrtgEnabled = true;
        s.PrtgUrl = "https://prtg.example";
        s.PrtgAuthMode = PrtgAuthModes.Token;
        s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
        s.PrtgTimeoutSeconds = 5;
    });

    [Fact]
    public void 沒有進行中的同步時回409()
    {
        var controller = CreateController(CreateSyncService());

        // 「沒東西可停」是狀態衝突而非輸入錯誤——與 start 被互斥擋下時同一種語意
        var ex = Assert.Throws<DomainException>(() => controller.CancelPrtgStructureSync());
        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 執行中時中止並寫稽核()
    {
        EnablePrtg();
        var sync = CreateSyncService();
        Assert.True(sync.TryStart(out _, out _));
        var controller = CreateController(sync);

        var res = controller.CancelPrtgStructureSync();

        Assert.True(res.Success);
        Assert.Contains(_audit.Entries, e => e.Action == AuditActions.PrtgStructureSyncCancel);
    }

    [Fact]
    public void 同步服務未接上時回輸入錯誤而非500()
    {
        var controller = CreateController(null);

        var ex = Assert.Throws<DomainException>(() => controller.CancelPrtgStructureSync());
        Assert.NotEqual(ApiErrorCodes.Conflict, ex.Code);
    }
}
