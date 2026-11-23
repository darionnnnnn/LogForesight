using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>單獨保留清除：失敗不記今天已完成；30 分鐘後才重試、當天最多重試 3 次（含首次共 4 次）、跨日歸零。</summary>
public class RetentionRetryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 23, 13, 0, 0);

    [Fact]
    public void 首次執行允許()
        => Assert.True(RetentionRetryGate.ShouldRun(T0, 0, null, false));

    [Fact]
    public void 成功後當天不再跑()
        => Assert.False(RetentionRetryGate.ShouldRun(T0.AddHours(5), 0, null, true));

    [Fact]
    public void 失敗後29分鐘不跑_30分鐘跑()
    {
        Assert.False(RetentionRetryGate.ShouldRun(T0.AddMinutes(29), 1, T0, false));
        Assert.True(RetentionRetryGate.ShouldRun(T0.AddMinutes(30), 1, T0, false));
    }

    [Fact]
    public void 重試次數邊界_第三次重試可跑_第四次不可()
    {
        // 已失敗 3 次（首次＋2 次重試）：還能再重試第 3 次
        Assert.True(RetentionRetryGate.ShouldRun(T0.AddHours(1), 3, T0, false));
        // 已失敗 4 次（首次＋3 次重試）：用完
        Assert.False(RetentionRetryGate.ShouldRun(T0.AddHours(9), 4, T0, false));
    }

    [Fact]
    public void 狀態_首次失敗不算完成_用完次數當天不再跑_隔天重新計()
    {
        var st = new RetentionRetryState();
        var now = T0;
        Assert.True(st.ShouldRun(now));
        for (var i = 0; i < 4; i++)
        {
            Assert.True(st.ShouldRun(now), $"第 {i + 1} 次應可執行");
            st.RecordFailure(now);
            Assert.False(st.ShouldRun(now.AddMinutes(29)));
            now = now.AddMinutes(30);
        }
        Assert.False(st.ShouldRun(now.AddHours(3)));
        Assert.True(st.ShouldRun(T0.AddDays(1)));
    }

    [Fact]
    public void 狀態_成功後當天不再跑_隔天再跑()
    {
        var st = new RetentionRetryState();
        st.RecordFailure(T0);
        st.RecordSuccess(T0.AddMinutes(30));
        Assert.False(st.ShouldRun(T0.AddHours(6)));
        Assert.True(st.ShouldRun(T0.AddDays(1)));
    }

    // ── 排程服務實際使用它 ──
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-retention-retry-" + Guid.NewGuid());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private SchedulerHostedService Create(Func<DateTime> clock, Func<int, RetentionRunResult> runner, Action onCall)
    {
        Directory.CreateDirectory(_dir);
        var web = new WebAppSettings
        {
            Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")}" }
        };
        var backend = new StorageBackend(web.Storage, web.Storage.ResolveDataRoot());
        var calls = 0;
        return new SchedulerHostedService(
            web, new ScheduleOptionsStore(backend.Blob("schedule_options")),
            new SystemSettingsStore(backend.Blob("system_settings")),
            new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
            new SchedulerRunState(), null!, null!, null!,
            new NamedMutexGate(@"Local\LF-retention-retry-" + Guid.NewGuid()),
            null!, new FakeLifetime(), new DataVersionStamp())
        {
            Clock = clock,
            RetentionOnlyRunner = (_, _, _) => { onCall(); return Task.FromResult(runner(++calls)); }
        };
    }

    [Theory]
    [InlineData(false)] // 回失敗結果
    [InlineData(true)]  // 擲例外
    public async Task 排程服務_清除失敗不把今天記成完成_30分鐘後重試(bool throws)
    {
        var now = new DateTime(2026, 11, 23, 13, 0, 0);
        var calls = 0;
        var svc = Create(() => now, n =>
        {
            if (n == 1) return throws ? throw new InvalidOperationException("boom") : new RetentionRunResult(new[] { "歷史紀錄" });
            return new RetentionRunResult(Array.Empty<string>());
        }, () => calls++);

        await svc.RunRetentionIfDueAsync();
        Assert.Equal(1, calls);

        now = now.AddMinutes(29);
        await svc.RunRetentionIfDueAsync();
        Assert.Equal(1, calls); // 29 分鐘：不重打

        now = now.AddMinutes(1);
        await svc.RunRetentionIfDueAsync();
        Assert.Equal(2, calls); // 30 分鐘：重試，這次成功

        now = now.AddHours(3);
        await svc.RunRetentionIfDueAsync();
        Assert.Equal(2, calls); // 成功後當天不再跑
    }

    [Fact]
    public async Task 排程服務_一直失敗_當天只執行4次_隔天再來()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0);
        var calls = 0;
        var svc = Create(() => now, _ => new RetentionRunResult(new[] { "x" }), () => calls++);
        for (var i = 0; i < 20; i++)
        {
            await svc.RunRetentionIfDueAsync();
            now = now.AddMinutes(30);
            if (now.Date != new DateTime(2026, 11, 23)) break;
        }
        Assert.Equal(4, calls);

        now = new DateTime(2026, 11, 24, 12, 0, 0);
        await svc.RunRetentionIfDueAsync();
        Assert.Equal(5, calls);
    }
}
