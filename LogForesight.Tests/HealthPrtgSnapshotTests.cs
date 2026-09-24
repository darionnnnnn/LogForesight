using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

public sealed class HealthPrtgSnapshotTests
{
    [Fact]
    public void 儲存層故障時詳情仍回傳Down且不讀取PRTG設定()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-health-prtg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbDir = Path.Combine(dir, "db");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "health.db");
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" }, dir);
            var freshness = new ScheduleFreshnessService(
                new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(backend.Blob("schedule_options")));
            var settings = new ThrowOnReadSystemSettingsStore();
            var mail = new MailNotificationService(
                settings, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(backend.Blob("mail_notify_state")), freshness);
            var service = new HealthService(backend, new SchedulerRunState(), backend.TopIssueBackfiller(), mail, freshness);
            _ = backend.HandlingMigrator.State;
            _ = backend.PermissionChangeMigrator.State;

            // 所有共用儲存與服務都先由有效資料庫完成初始化，之後才讓健康探測遇到儲存故障。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            Directory.CreateDirectory(dbPath);

            var result = service.GetDetail();

            Assert.Equal(HealthStatuses.Down, result.Status);
            Assert.False(result.StorageOk);
            Assert.NotNull(result.StorageError);
            Assert.Equal(string.Empty, result.MigrationState);
            Assert.Equal(string.Empty, result.PermissionChangeMigrationState);
            Assert.Null(result.ScheduleFreshness?.LastSuccessAt);
            Assert.Equal(0, settings.ReadCount);
            Assert.Null(result.PrtgSnapshot);
            Assert.Null(result.PrtgFreshness);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class ThrowOnReadSystemSettingsStore : ISystemSettingsStore
    {
        public int ReadCount { get; private set; }

        public SystemSettings Get()
        {
            ReadCount++;
            throw new InvalidOperationException("儲存層故障時不應讀取 PRTG 設定");
        }

        public SystemSettings Update(Action<SystemSettings> mutation) =>
            throw new InvalidOperationException("測試不應更新系統設定");
    }

    private static PrtgSnapshotHourDiagnostic Hour(DateTime hour, PrtgSnapshotHourState state,
        int targets = 4, int availableValues = 1, int attempts = 4, int successes = 1,
        int skips = 0, int writeFailures = 0, IReadOnlyDictionary<string, int>? reasons = null,
        IReadOnlyDictionary<string, int>? reasonCodes = null) =>
        new(hour, state, targets, availableValues, attempts, successes, skips, writeFailures,
            reasons ?? new Dictionary<string, int>())
        { ReasonCodes = reasonCodes ?? new Dictionary<string, int>() };

    [Fact]
    public void 停用與無目標不形成系統警告()
    {
        var disabled = HealthService.BuildPrtgSnapshotHealth(false, Array.Empty<PrtgSnapshotHourDiagnostic>());
        var noTargets = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.NoTargets, 0),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.NoTargets, 0)
        });

        Assert.Equal("disabled", disabled.State);
        Assert.False(disabled.Warning);
        Assert.Equal("no-targets", noTargets.State);
        Assert.False(noTargets.Warning);
    }

    [Fact]
    public void 空診斷資料回傳未知且不形成系統警告()
    {
        var recent = Array.Empty<PrtgSnapshotHourDiagnostic>();

        var enabled = HealthService.BuildPrtgSnapshotHealth(true, recent);
        var disabled = HealthService.BuildPrtgSnapshotHealth(false, recent);

        Assert.Equal("unknown", enabled.State);
        Assert.Equal("尚無完整小時診斷資料。", enabled.Message);
        Assert.Empty(enabled.RecentHours);
        Assert.False(enabled.Warning);
        Assert.Equal("disabled", disabled.State);
        Assert.False(disabled.Warning);
    }

    [Fact]
    public void 連續兩小時缺診斷不會誤判無目標()
    {
        var result = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Unknown, targets: 0),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Unknown, targets: 0)
        });

        Assert.Equal("unknown", result.State);
        Assert.False(result.Warning);
    }

    [Fact]
    public void 曾有目標後連續兩小時缺診斷會警告()
    {
        var result = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-3), PrtgSnapshotHourState.Healthy),
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Unknown, targets: 0),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Unknown, targets: 0)
        });

        Assert.True(result.Warning);
        Assert.Equal(2, result.ConsecutiveAnomalousHours);
    }

    [Fact]
    public void 啟動部分小時不計入連續異常()
    {
        var result = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Insufficient),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Unknown,
                reasons: new Dictionary<string, int> { ["startup-partial-hour"] = 1 })
        });

        Assert.False(result.Warning);
        Assert.Equal(0, result.ConsecutiveAnomalousHours);
    }

    [Fact]
    public void 只有連續兩個完整小時異常才警告()
    {
        var one = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Healthy),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Insufficient)
        });
        var two = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Insufficient),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Insufficient, writeFailures: 1)
        });

        Assert.False(one.Warning);
        Assert.Equal(1, one.ConsecutiveAnomalousHours);
        Assert.True(two.Warning);
        Assert.Equal(2, two.ConsecutiveAnomalousHours);
    }

    [Theory]
    [InlineData(PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase, "夜間取數正在 PRTG 階段，暫停快照")]
    [InlineData(PrtgSnapshotSkipReasonCodes.StructureSyncActive, "結構同步執行中，暫停快照")]
    [InlineData(PrtgSnapshotSkipReasonCodes.BackfillActive, "歷史回填執行中，暫停快照")]
    [InlineData(PrtgSnapshotSkipReasonCodes.MaintenanceWindow, "維護時段暫停快照")]
    [InlineData(PrtgSnapshotSkipReasonCodes.MaintenanceConfirmed, "已確認維護暫停快照")]
    public void 已知穩定互斥代碼的連續跳過不列為異常(string reasonCode, string displayText)
    {
        var hours = new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Insufficient, skips: 1,
                reasons: new Dictionary<string, int> { [displayText] = 1 }, reasonCodes: new Dictionary<string, int> { [reasonCode] = 1 }),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Insufficient, skips: 1,
                reasons: new Dictionary<string, int> { [displayText] = 1 }, reasonCodes: new Dictionary<string, int> { [reasonCode] = 1 })
        };
        var result = HealthService.BuildPrtgSnapshotHealth(true, hours);

        Assert.Equal("paused", result.State);
        Assert.False(result.Warning);
    }

    [Fact]
    public void 中文原因文字本身不再抑制健康警告()
    {
        var hours = new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.Insufficient, skips: 1,
                reasons: new Dictionary<string, int> { ["夜間取數正在 PRTG 階段，暫停快照"] = 1 }),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.Insufficient, skips: 1,
                reasons: new Dictionary<string, int> { ["夜間取數正在 PRTG 階段，暫停快照"] = 1 })
        };

        var result = HealthService.BuildPrtgSnapshotHealth(true, hours);

        Assert.Equal("insufficient", result.State);
        Assert.True(result.Warning);
    }

    [Fact]
    public void 寫入回報達標顯示未逐顆核實且不警告()
    {
        var result = HealthService.BuildPrtgSnapshotHealth(true, new[]
        {
            Hour(DateTime.Today.AddHours(-2), PrtgSnapshotHourState.ReportedWriteCountMetTarget,
                availableValues: 4),
            Hour(DateTime.Today.AddHours(-1), PrtgSnapshotHourState.ReportedWriteCountMetTarget,
                availableValues: 5, targets: 4)
        });

        Assert.Equal("reported-write-count-met-target", result.State);
        Assert.False(result.Warning);
        Assert.Equal(0, result.ConsecutiveAnomalousHours);
        Assert.Contains("回報寫入量達目標，逐顆覆蓋未證明", result.Message);
        Assert.Contains("重試或覆寫", result.Message);
    }
}
