using System.Runtime;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 67 輪 A-1：記憶體快照單一讀取點、快取統計、健康頁詳情的記憶體區塊。
/// 快照絕不能讓健康頁失敗，因此重點是「委派擲例外時降級為 null」與「兩條 GetDetail 分支都帶 Memory」。
/// </summary>
public class MemorySnapshotTests
{
    // ── 1. Capture ───────────────────────────────────────────────────────

    [Fact]
    public void Capture_數值欄位皆不為負且IsServerGc與執行期一致()
    {
        var s = MemorySnapshot.Capture();

        foreach (var v in new long?[]
                 {
                     s.WorkingSetBytes, s.PeakWorkingSetBytes, s.PrivateBytes, s.PeakPagedBytes, s.ManagedHeapBytes,
                     s.GcHeapSizeBytes, s.GcCommittedBytes, s.GcFragmentedBytes, s.LohSizeBytes, s.HardLimitBytes,
                     s.GcConserveMemory, s.GcHeapHardLimitPercent, s.SqliteHeapBytes, s.SqliteHeapHighwaterBytes
                 })
        {
            if (v is { } x) Assert.True(x >= 0, $"欄位值 {x} 不應為負");
        }
        Assert.True(s.Gen2Collections >= 0);
        Assert.Equal(GCSettings.IsServerGC, s.IsServerGc);
        Assert.NotNull(s.WorkingSetBytes);
        Assert.NotNull(s.ManagedHeapBytes);
        Assert.Empty(s.Caches);
    }

    [Fact]
    public void Capture_委派擲例外時對應欄位為null且不擲例外()
    {
        var s = MemorySnapshot.Capture(
            sqliteHeap: () => throw new InvalidOperationException("provider 未初始化"),
            nlogRuleCount: () => throw new InvalidOperationException("nlog"),
            caches: () => throw new InvalidOperationException("cache"));

        Assert.Null(s.SqliteHeapBytes);
        Assert.Null(s.SqliteHeapHighwaterBytes);
        Assert.Null(s.NlogRuleCount);
        Assert.Empty(s.Caches);
        Assert.NotNull(s.WorkingSetBytes);
    }

    [Fact]
    public void Capture_委派正常時值原樣帶出()
    {
        var s = MemorySnapshot.Capture(
            () => (10, 20), () => 7,
            () => new[] { new CacheStatisticsSnapshot("A", 1, 2, 3, 4, 5, 6) });

        Assert.Equal(10, s.SqliteHeapBytes);
        Assert.Equal(20, s.SqliteHeapHighwaterBytes);
        Assert.Equal(7, s.NlogRuleCount);
        Assert.Equal("A", Assert.Single(s.Caches).Name);
    }

    // ── 2. HardLimitUsageRatio ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void HardLimitUsageRatio_上限未知或為零回null(long? limit)
    {
        var s = new MemorySnapshot { ManagedHeapBytes = 85, HardLimitBytes = limit };
        Assert.Null(s.HardLimitUsageRatio);
    }

    [Fact]
    public void HardLimitUsageRatio_託管堆除以上限()
    {
        var s = new MemorySnapshot { ManagedHeapBytes = 85, HardLimitBytes = 100 };
        Assert.Equal(0.85, s.HardLimitUsageRatio);
        Assert.Null(new MemorySnapshot { ManagedHeapBytes = null, HardLimitBytes = 100 }.HardLimitUsageRatio);
    }

    // ── 3. StorageBackend.TryReadSqliteHeap ──────────────────────────────

    [Fact]
    public void StorageBackend_Sqlite開過連線後堆高水位大於零()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-memsnap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "m.db")}" }, dir);
            _ = backend.Blob("__probe__").Read();   // 確實開過連線

            Assert.True(backend.IsSqlite);
            var (used, highwater) = backend.TryReadSqliteHeap();
            Assert.NotNull(used);
            Assert.NotNull(highwater);
            Assert.True(highwater > 0, $"高水位 {highwater} 應大於 0");
            Assert.True(highwater >= used);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── 4. 快取統計 ──────────────────────────────────────────────────────

    private static DateTime Now => new(2026, 11, 9, 10, 0, 0);

    [Fact]
    public void IssueRankingCache_統計含條目元素命中未命中()
    {
        var cache = new IssueRankingCache(new DataVersionStamp(), () => Now);
        cache.Set("a", new List<IssueRankingDto> { new(), new() });
        cache.Set("b", new List<IssueRankingDto> { new() });
        cache.Set("c", new List<IssueRankingDto> { new(), new(), new() });

        Assert.NotNull(cache.TryGet("a"));
        Assert.Null(cache.TryGet("missing"));

        var st = cache.GetStatistics();
        Assert.Equal("IssueRankingCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(6, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(1, st.Misses);
        Assert.Equal(0, st.Evictions);
        Assert.Equal(0, st.OversizeRejected);
    }

    [Fact]
    public void IssueRankingCache_逾時讀取算未命中_滿了逐出計入Evictions()
    {
        var now = Now;
        var cache = new IssueRankingCache(new DataVersionStamp(), () => now);
        cache.Set("old", new List<IssueRankingDto>());
        now = now.AddSeconds(IssueRankingCache.TtlSeconds + 1);
        Assert.Null(cache.TryGet("old"));
        Assert.Equal(1, cache.GetStatistics().Misses);
        Assert.Equal(0, cache.GetStatistics().Entries);

        for (var i = 0; i < 257; i++) cache.Set($"k{i}", new List<IssueRankingDto>());
        var st = cache.GetStatistics();
        Assert.Equal(256, st.Entries);
        Assert.Equal(1, st.Evictions);
    }

    [Fact]
    public void ActionableSnapshotCache_統計含元素加總()
    {
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), () => Now);
        cache.Set("a", new List<ResolvedOccurrence> { default!, default! });
        cache.Set("b", new List<ResolvedOccurrence> { default! });
        cache.Set("c", new List<ResolvedOccurrence> { default!, default!, default! });

        Assert.NotNull(cache.TryGet("b"));
        Assert.Null(cache.TryGet("missing"));

        var st = cache.GetStatistics();
        Assert.Equal("ActionableSnapshotCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(6, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(1, st.Misses);
    }

    [Fact]
    public void SummaryCache_統計()
    {
        var cache = new SummaryCache(new DataVersionStamp(), () => Now);
        for (var i = 0; i < 3; i++) cache.GetOrAdd($"k{i}", () => new object());   // 3 次未命中
        cache.GetOrAdd("k0", () => new object());                                  // 1 次命中

        var st = cache.GetStatistics();
        Assert.Equal("SummaryCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(3, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(3, st.Misses);
    }

    [Fact]
    public void NextUnhandledSequenceCache_統計含元素加總()
    {
        var cache = new NextUnhandledSequenceCache(new DataVersionStamp(), () => Now);
        cache.GetOrAdd("a", () => new List<(long, string)> { (1, "d"), (2, "d") });
        cache.GetOrAdd("b", () => new List<(long, string)> { (1, "d") });
        cache.GetOrAdd("c", () => new List<(long, string)> { (1, "d"), (2, "d"), (3, "d") });
        cache.GetOrAdd("a", () => new List<(long, string)>());                       // 命中

        var st = cache.GetStatistics();
        Assert.Equal("NextUnhandledSequenceCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(6, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(3, st.Misses);
    }

    [Fact]
    public void IssueOwnedHostIdsCache_統計含主機數加總()
    {
        var cache = new IssueOwnedHostIdsCache(new DataVersionStamp(), () => Now);
        cache.GetOrAdd(1, 30, () => new HashSet<long> { 1, 2 });
        cache.GetOrAdd(2, 30, () => new HashSet<long> { 1 });
        cache.GetOrAdd(3, 30, () => new HashSet<long> { 1, 2, 3 });
        cache.GetOrAdd(1, 30, () => new HashSet<long>());                            // 命中

        var st = cache.GetStatistics();
        Assert.Equal("IssueOwnedHostIdsCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(6, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(3, st.Misses);
    }

    [Fact]
    public void IssueOwnedHostIdsCache_版本推進整批清空計入逐出()
    {
        var stamp = new DataVersionStamp();
        var cache = new IssueOwnedHostIdsCache(stamp, () => Now);
        cache.GetOrAdd(1, 30, () => new HashSet<long> { 1 });
        cache.GetOrAdd(2, 30, () => new HashSet<long> { 1 });
        stamp.Bump();
        cache.GetOrAdd(1, 30, () => new HashSet<long> { 1 });

        var st = cache.GetStatistics();
        Assert.Equal(2, st.Evictions);
        Assert.Equal(1, st.Entries);
    }

    [Fact]
    public void PrtgDeviceIndexCache_統計()
    {
        var cache = new PrtgDeviceIndexCache(() => Now);
        IReadOnlyList<LogForesight.Core.Persistence.Sql.PrtgDeviceRow> Load() =>
            new[] { new LogForesight.Core.Persistence.Sql.PrtgDeviceRow { Objid = 1, Ip = "10.0.0.1" } };
        for (var i = 0; i < 3; i++) cache.GetOrAdd($"k{i}", Load);                   // 3 次未命中
        cache.GetOrAdd("k1", Load);                                                  // 命中

        var st = cache.GetStatistics();
        Assert.Equal("PrtgDeviceIndexCache", st.Name);
        Assert.Equal(3, st.Entries);
        Assert.Equal(3, st.Elements);
        Assert.Equal(1, st.Hits);
        Assert.Equal(3, st.Misses);
    }

    // ── 5. 健康頁 ────────────────────────────────────────────────────────

    [Collection("CryptoKeyState")]
    public sealed class HealthMemoryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-healthmem-" + Guid.NewGuid().ToString("N"));
        private readonly StorageBackend _backend;

        public HealthMemoryTests()
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

        private ScheduleFreshnessService NewFreshness() => new(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(_backend.Blob("schedule_options")));

        private HealthService NewService(IEnumerable<ICacheStatistics>? caches = null)
        {
            var freshness = NewFreshness();
            var mail = new MailNotificationService(
                new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness,
                new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
                new FakeIssueCaseStore(), new FakeRuleStore());
            return new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mail, freshness,
                null, null, caches);
        }

        private static Func<MemorySnapshot> Fixed(long? heap, long? limit) =>
            () => new MemorySnapshot { CapturedAt = DateTime.Now, ManagedHeapBytes = heap, HardLimitBytes = limit };

        [Fact]
        public void 用量達八十五趴為degraded且MemoryWarning為真()
        {
            var svc = NewService();
            svc.MemoryCaptureOverride = Fixed(85, 100);

            var dto = svc.GetDetail();

            Assert.Equal(HealthStatuses.Degraded, dto.Status);
            Assert.True(dto.MemoryWarning);
            Assert.NotNull(dto.Memory);
            Assert.Equal(85.0, dto.Memory!.HardLimitUsagePercent);
        }

        [Fact]
        public void 用量八十四趴不警告()
        {
            var svc = NewService();
            svc.MemoryCaptureOverride = Fixed(84, 100);

            var dto = svc.GetDetail();

            Assert.False(dto.MemoryWarning);
            Assert.Equal(HealthStatuses.Ok, dto.Status);
        }

        [Fact]
        public void 上限未知時不degraded也不擲例外()
        {
            var svc = NewService();
            svc.MemoryCaptureOverride = Fixed(1_000_000, null);

            var dto = svc.GetDetail();

            Assert.False(dto.MemoryWarning);
            Assert.Equal(HealthStatuses.Ok, dto.Status);
            Assert.Null(dto.Memory!.HardLimitUsagePercent);
        }

        [Fact]
        public void 正式路徑帶出快取統計與SQLite堆()
        {
            var cache = new SummaryCache(new DataVersionStamp());
            cache.GetOrAdd("k", () => new object());
            var svc = NewService(new ICacheStatistics[] { cache });

            var dto = svc.GetDetail();

            Assert.NotNull(dto.Memory);
            var c = Assert.Single(dto.Memory!.Caches);
            Assert.Equal("SummaryCache", c.Name);
            Assert.Equal(1, c.Entries);
            Assert.NotNull(dto.Memory.WorkingSetBytes);
        }

        [Fact]
        public void 儲存探測失敗分支也帶Memory()
        {
            var svc = NewService();
            svc.MemoryCaptureOverride = Fixed(1, 100);

            // 與 HealthPrtgSnapshotTests 同法：關池後把 db 檔換成同名目錄，探測必然失敗
            _ = _backend.HandlingMigrator.State;
            _ = _backend.PermissionChangeMigrator.State;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dbPath = Path.Combine(_dir, "h.db");
            File.Delete(dbPath);
            Directory.CreateDirectory(dbPath);

            var dto = svc.GetDetail();

            Assert.Equal(HealthStatuses.Down, dto.Status);
            Assert.False(dto.StorageOk);
            Assert.NotNull(dto.Memory);
            Assert.False(dto.MemoryWarning);
        }
    }

    // ── 6. JSON 往返 ─────────────────────────────────────────────────────

    [Fact]
    public void MemorySnapshotDto_JSON往返欄位相等()
    {
        var dto = new MemorySnapshotDto
        {
            CapturedAt = new DateTime(2026, 11, 9, 10, 0, 0),
            WorkingSetBytes = 1, PeakWorkingSetBytes = 2, PrivateBytes = 3, PeakPagedBytes = 4,
            ManagedHeapBytes = 5, GcHeapSizeBytes = 6, GcCommittedBytes = 7, GcFragmentedBytes = 8,
            LohSizeBytes = 9, HardLimitBytes = 10, Gen2Collections = 11, IsServerGc = true,
            GcConserveMemory = 12, GcHeapHardLimitPercent = 13, SqliteHeapBytes = 14, SqliteHeapHighwaterBytes = 15,
            NlogRuleCount = 16, HardLimitUsagePercent = 85.5,
            Caches = { new CacheStatisticsDto { Name = "X", Entries = 1, Elements = 2, Hits = 3, Misses = 4, Evictions = 5, OversizeRejected = 6 } }
        };

        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<MemorySnapshotDto>(json)!;

        Assert.Equal(JsonSerializer.Serialize(dto), JsonSerializer.Serialize(back));
        Assert.Equal(dto.HardLimitUsagePercent, back.HardLimitUsagePercent);
        Assert.Equal(dto.NlogRuleCount, back.NlogRuleCount);
        var c = Assert.Single(back.Caches);
        Assert.Equal(6, c.OversizeRejected);
        Assert.Equal("X", c.Name);
    }
}
