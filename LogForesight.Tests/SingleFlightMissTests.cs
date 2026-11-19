using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// H2：健康整體狀態（導覽告示）與 AI 待補數的未命中經 <see cref="SingleFlight"/>——
/// 同時進來的請求只跑一次計算（以計數替身確認），不各打一輪完整診斷／一支 7～15 秒的慢查詢。
/// </summary>
public sealed class SingleFlightMissTests
{
    private const int Requests = 20;

    [Fact]
    public async Task AI待補數_二十個並發請求冷快取只查詢一次()
    {
        var state = new AiAnalysisRunState();
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int Fetch()
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return 42;
        }

        var tasks = Enumerable.Range(0, Requests)
            .Select(_ => Task.Factory.StartNew(() => state.GetPendingAiCount(Fetch), TaskCreationOptions.LongRunning))
            .ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        release.Set();
        var counts = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(counts, c => Assert.Equal(42, c));
        // 合併結束後結果已進快取：之後的請求直接命中，不再查詢
        Assert.Equal(42, state.GetPendingAiCount(() => throw new InvalidOperationException("應命中快取")));
    }

    [Fact]
    public void AI待補數_合併期間快取被失效_結果不寫回()
    {
        var state = new AiAnalysisRunState();
        Assert.Equal(5, state.GetPendingAiCount(() => { state.InvalidatePendingAiCache(); return 5; }));

        var calls = 0;
        Assert.Equal(6, state.GetPendingAiCount(() => { calls++; return 6; }));
        Assert.Equal(1, calls);   // 上一份是失效後才回來的過期結果，沒有寫回，所以這次要重查
    }

    [Collection("CryptoKeyState")]
    public sealed class HealthAttention : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-healthflight-" + Guid.NewGuid().ToString("N"));
        private readonly StorageBackend _backend;

        public HealthAttention()
        {
            CryptoHelper.ResetForTests();
            CryptoKeyBootstrapper.KeyMismatch = false;
            Directory.CreateDirectory(_dir);
            _backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        /// <summary>每次完整診斷都會讀一次快取統計：用它當「GetDetail 跑了幾次」的計數替身，並可擋住診斷讓請求疊起來。</summary>
        private sealed class CountingCache : ICacheStatistics
        {
            public int Calls;
            public ManualResetEventSlim Entered { get; } = new();
            public ManualResetEventSlim Release { get; } = new();

            public CacheStatisticsSnapshot GetStatistics()
            {
                Interlocked.Increment(ref Calls);
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
                return new CacheStatisticsSnapshot("Counting", 0, 0, 0, 0, 0, 0);
            }
        }

        private HealthService NewService(ICacheStatistics cache)
        {
            var freshness = new ScheduleFreshnessService(
                new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_backend.Blob("schedule_options")));
            var mail = new MailNotificationService(
                new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness,
                new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
                new FakeIssueCaseStore(), new FakeRuleStore());
            return new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mail, freshness,
                null, null, new[] { cache });
        }

        [Fact]
        public async Task 二十個並發請求冷快取只跑一次完整診斷()
        {
            var cache = new CountingCache();
            var svc = NewService(cache);

            var tasks = Enumerable.Range(0, Requests)
                .Select(_ => Task.Factory.StartNew(() => svc.GetAttention(), TaskCreationOptions.LongRunning))
                .ToArray();
            Assert.True(cache.Entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(300);
            cache.Release.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, cache.Calls);
            Assert.All(results, r => Assert.Equal(results[0].Status, r.Status));
        }

        [Fact]
        public void 三十秒存活邊界_命中快取不再診斷_過期後重新診斷()
        {
            var cache = new CountingCache();
            cache.Release.Set();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero));
            var freshness = new ScheduleFreshnessService(
                new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_backend.Blob("schedule_options")));
            var mail = new MailNotificationService(
                new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness,
                new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
                new FakeIssueCaseStore(), new FakeRuleStore());
            var svc = new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mail, freshness,
                null, null, new[] { (ICacheStatistics)cache }, clock: clock);

            svc.GetAttention();
            clock.Advance(TimeSpan.FromSeconds(29.9));
            svc.GetAttention();
            Assert.Equal(1, cache.Calls);

            clock.Advance(TimeSpan.FromSeconds(0.1));
            svc.GetAttention();
            Assert.Equal(2, cache.Calls);
        }

        private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
        {
            private DateTimeOffset _now = start;
            public void Advance(TimeSpan span) => _now += span;
            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
