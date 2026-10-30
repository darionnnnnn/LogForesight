using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public class DisplaySettingsControllerTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public DisplaySettingsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Get() 一被呼叫就計數並擲例外：顯示設定端點不得走會統計報告用量的完整設定。</summary>
    private sealed class CountingSettingsService : ISystemSettingsService
    {
        public int GetCalls { get; private set; }
        public SystemSettingsDto Get() { GetCalls++; throw new InvalidOperationException("顯示設定端點不應呼叫完整 Get()"); }
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void 顯示設定的郵件布林由輕量設定庫讀取_不呼叫完整設定Get(bool mailEnabled, bool notifyWorkOrders, bool expected)
    {
        var store = new SystemSettingsStore(_backend.Blob("system_settings"));
        store.Update(s => { s.MailEnabled = mailEnabled; s.MailNotifyWorkOrders = notifyWorkOrders; });
        var service = new CountingSettingsService();
        var controller = new DisplaySettingsController(service, store);

        var result = controller.Get();

        Assert.True(result.Success);
        Assert.Equal(expected, result.Data!.MailWorkOrdersEnabled);
        Assert.Equal(0, service.GetCalls);
    }
}
