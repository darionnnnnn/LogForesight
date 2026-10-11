using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public sealed class SchedulePrtgOperationalStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-schedule-prtg-status-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;

    public SchedulePrtgOperationalStatusTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_dir, "schedule.db")}"
        }, _dir);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DevMonitorScheduleStatus_ReturnsNormalizedPrtgOperationsWithoutConnectionSecrets()
    {
        _settings.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgValueFetchScope = "invalid-scope";
            settings.PrtgFetchStrategy = "invalid-strategy";
            settings.PrtgBackfillDays = 400;
            settings.PrtgUrl = "https://private-prtg.example.invalid";
            settings.PrtgApiTokenEnc = "sentinel-api-token";
            settings.PrtgPasswordEnc = "sentinel-password";
        });

        var status = CreateController().GetStatus();

        Assert.True(status.Success);
        Assert.NotNull(status.Data);
        Assert.True(status.Data.PrtgEnabled);
        Assert.Equal(PrtgValueFetchScope.Triggered, status.Data.PrtgValueFetchScope);
        Assert.Equal(PrtgFetchStrategy.Conservative, status.Data.PrtgFetchStrategy);
        Assert.Equal(365, status.Data.PrtgBackfillDays);

        var json = JsonSerializer.Serialize(status.Data);
        Assert.DoesNotContain("private-prtg.example.invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-api-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-password", json, StringComparison.Ordinal);
    }

    private ScheduleController CreateController() => new(
        new ScheduleOptionsStore(_backend.Blob("schedule_options")),
        scheduler: null!,
        runState: new SchedulerRunState(),
        hosts: null!,
        sentinels: null!,
        audit: null!,
        currentUser: null!,
        users: null!,
        records: null!,
        settingsStore: _settings,
        userDisplayNames: null!,
        aiScheduler: null!,
        aiRunState: new AiAnalysisRunState());
}
