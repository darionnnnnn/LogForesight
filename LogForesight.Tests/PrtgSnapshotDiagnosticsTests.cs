using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-prtg-diag-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgSnapshotDiagnosticsTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "diag.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private PrtgSnapshotDiagnosticsStore NewStore() => new(_backend.Blob("prtg_snapshot_diagnostics_v1"));
    private PrtgSnapshotDiagnosticsService NewReader() => new(NewStore(),
        (from, to) => _backend.PrtgStore().GetSnapshotValueCoverage(from, to));

    [Fact]
    public void 四次取樣達標而單次低coverage不達標()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var nextHour = hour.AddHours(1);
        var store = NewStore();
        for (var i = 0; i < 4; i++)
        {
            var at = hour.AddMinutes(i * 15);
            store.Record(at, "attempt", targets: 4);
            store.Record(at, "success", targets: 4);
        }
        store.Record(hour.AddMinutes(59), "persisted", sampled: 4);
        store.Record(nextHour.AddMinutes(5), "attempt", targets: 4);
        store.Record(nextHour.AddMinutes(5), "success", targets: 4);
        store.Record(nextHour.AddMinutes(59), "persisted", sampled: 1);
        _backend.PrtgStore().MergeSampledValues(new[]
        {
            new PrtgValueRow { SensorObjid = 1, PeriodStart = hour, Quality = "sampled", Coverage = 100 },
            new PrtgValueRow { SensorObjid = 2, PeriodStart = hour, Quality = "sampled", Coverage = 100 },
            new PrtgValueRow { SensorObjid = 3, PeriodStart = hour, Quality = "sampled", Coverage = 100 },
            new PrtgValueRow { SensorObjid = 4, PeriodStart = hour, Quality = "sampled", Coverage = 100 },
            new PrtgValueRow { SensorObjid = 1, PeriodStart = nextHour, Quality = "sampled", Coverage = 25 }
        });

        var rows = NewReader().ReadRecent(nextHour.AddHours(1), 2);
        Assert.Equal(4, rows[0].Attempts);
        Assert.Equal(4, rows[0].Successes);
        Assert.Equal(PrtgSnapshotHourState.ReportedWriteCountMetTarget, rows[0].State);
        Assert.Equal(4, rows[0].AvailableValues);
        Assert.Equal(1, rows[1].Attempts);
        Assert.Equal(1, rows[1].Successes);
        Assert.Equal(PrtgSnapshotHourState.Insufficient, rows[1].State);
        Assert.Equal(1, rows[1].AvailableValues);
        Assert.Equal(1, rows[1].SampledLowCoverageValues);
        Assert.Equal(0, rows[1].DatabaseUsableValues);
        Assert.Equal(4, rows[0].SampledUsableValues);
        Assert.Equal(4, rows[0].DatabaseUsableValues);
    }

    [Fact]
    public void 快照小時聚合查詢可翻譯至SqlServer()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var ctx = new LfDbContext(options);
        var sql = EfPrtgStore.BuildSnapshotValueCoverageQuery(ctx.PrtgValues,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 2)).ToQueryString();
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("period_start", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coverage", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 零目標不會判成健康()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        NewStore().Record(hour, "success", "no-targets", targets: 0);
        Assert.Equal(PrtgSnapshotHourState.NoTargets, NewReader().ReadRecent(hour.AddHours(1), 1).Single().State);
    }

    [Fact]
    public void 重啟跨到的不完整小時不會誤報無目標()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        NewStore().Record(hour.AddMinutes(42), "startup", "startup-partial-hour");
        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(PrtgSnapshotHourState.Unknown, row.State);
        Assert.Equal(0, row.Targets);
        Assert.Contains("startup-partial-hour", row.Reasons.Keys);
    }

    [Fact]
    public void 歷史Ok值覆蓋sampled時標示資料可用但不冒稱快照成功()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        NewStore().Record(hour, "attempt", targets: 1);
        _backend.PrtgStore().MergeSampledValues(new[] { new PrtgValueRow { SensorObjid = 7, PeriodStart = hour, Quality = "ok", Coverage = 100 } });
        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(PrtgSnapshotHourState.OkCovered, row.State);
        Assert.Equal(0, row.AvailableValues);
        Assert.Equal(0, row.Successes);
    }

    [Fact]
    public void 單次成功與持久化計數同時呈現但不宣稱逐顆覆蓋()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 4);
        store.Record(hour, "success", targets: 4);
        store.Record(hour, "persisted", sampled: 4);
        foreach (var id in Enumerable.Range(1, 4))
            _backend.PrtgStore().MergeSampledValues(new[]
                { new PrtgValueRow { SensorObjid = id, PeriodStart = hour, Quality = "ok", Coverage = 100 } });

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(PrtgSnapshotHourState.OkCovered, row.State);
        Assert.Equal(4, row.AvailableValues);
        Assert.Equal(4, row.DatabaseUsableValues);
    }

    [Fact]
    public void 四分之一目標有樣本時保留小時彙總計數()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 4);
        store.Record(hour, "success", targets: 4);
        store.Record(hour, "persisted", sampled: 1);
        _backend.PrtgStore().MergeSampledValues(new[]
            { new PrtgValueRow { SensorObjid = 1, PeriodStart = hour, Quality = "sampled", Coverage = 100 } });

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(PrtgSnapshotHourState.Insufficient, row.State);
        Assert.Equal(1, row.AvailableValues);
    }

    [Fact]
    public void 持久化計數達標且執行成功時回報未逐顆核實()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 3);
        store.Record(hour, "success", targets: 3);
        store.Record(hour, "persisted", sampled: 3);
        _backend.PrtgStore().MergeSampledValues(Enumerable.Range(1, 3).Select(id => new PrtgValueRow
            { SensorObjid = id, PeriodStart = hour, Quality = "sampled", Coverage = 100 }).ToArray());

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(3, row.AvailableValues);
        Assert.Equal(PrtgSnapshotHourState.ReportedWriteCountMetTarget, row.State);
    }

    [Fact]
    public void 重試累計可能重複計數且重啟後保留回報數()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 2);
        store.Record(hour, "success", targets: 2);
        store.Record(hour, "persisted", sampled: 2);
        store.Record(hour, "persisted", sampled: 2); // simulated retry after the write may already have committed
        _backend.PrtgStore().MergeSampledValues(new[] { new PrtgValueRow
            { SensorObjid = 11, PeriodStart = hour, Quality = "sampled", Coverage = 100 } });

        var afterRestart = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(4, afterRestart.AvailableValues);
        Assert.Equal(PrtgSnapshotHourState.Insufficient, afterRestart.State);
    }

    [Fact]
    public void 跳過或寫入失敗不得被超額回報數掩蓋()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 1);
        store.Record(hour, "success", targets: 1);
        store.Record(hour, "persisted", sampled: 2);
        store.Record(hour, "skip", "maintenance", targets: 1);
        var nextHour = hour.AddHours(1);
        store.Record(nextHour, "attempt", targets: 1);
        store.Record(nextHour, "success", targets: 1);
        store.Record(nextHour, "persisted", sampled: 2);
        store.Record(nextHour, "write-failure", "database-write-failed", targets: 1);

        var rows = NewReader().ReadRecent(nextHour.AddHours(1), 2);
        var skipped = rows.Single(row => row.Hour == hour);
        Assert.Equal(2, skipped.AvailableValues);
        Assert.Equal(1, skipped.Skips);
        Assert.Equal(0, skipped.WriteFailures);
        Assert.Equal(PrtgSnapshotHourState.Insufficient, skipped.State);
        var failed = rows.Single(row => row.Hour == nextHour);
        Assert.Equal(2, failed.AvailableValues);
        Assert.Equal(0, failed.Skips);
        Assert.Equal(1, failed.WriteFailures);
        Assert.Equal(PrtgSnapshotHourState.Insufficient, failed.State);
    }

    [Fact]
    public void 一百個目標的計數足量仍不證明逐顆覆蓋()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        for (var i = 0; i < 4; i++)
        {
            store.Record(hour.AddMinutes(i * 15), "attempt", targets: 100);
            store.Record(hour.AddMinutes(i * 15), "success", targets: 100);
        }
        store.Record(hour.AddMinutes(59), "persisted", sampled: 100);
        _backend.PrtgStore().MergeSampledValues(Enumerable.Range(1, 100).Select(id =>
            new PrtgValueRow { SensorObjid = id, PeriodStart = hour, Quality = "sampled", Coverage = 100 }).ToArray());

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(4, row.Successes);
        Assert.Equal(100, row.Targets);
        Assert.Equal(100, row.AvailableValues);
        Assert.Equal(PrtgSnapshotHourState.ReportedWriteCountMetTarget, row.State);
    }

    [Fact]
    public void 缺少統計的時段即使有歷史值也未知()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        _backend.PrtgStore().MergeSampledValues(new[]
            { new PrtgValueRow { SensorObjid = 1, PeriodStart = hour, Quality = "ok", Coverage = 100 } });

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(PrtgSnapshotHourState.Unknown, row.State);
        Assert.Equal(0, row.Targets);
    }

    [Fact]
    public void 大量無關感測器值不影響持久快照計數或宣稱健康()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 1);
        store.Record(hour, "success", targets: 1);
        store.Record(hour, "persisted", sampled: 1);
        var values = Enumerable.Range(1, 5000).Select(id => new PrtgValueRow
        {
            SensorObjid = id, PeriodStart = hour, Quality = "sampled", Coverage = 100
        }).ToArray();
        _backend.PrtgStore().MergeSampledValues(values);

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(1, row.AvailableValues);
        Assert.Equal(PrtgSnapshotHourState.ReportedWriteCountMetTarget, row.State);
    }

    [Fact]
    public void 重啟後讀回小時彙總而不依賴行程記憶體()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        NewStore().Record(hour, "skip", "夜間取數正在 PRTG 階段，暫停快照", targets: 3,
            reasonCode: PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase);
        NewStore().Record(hour.AddMinutes(1), "skip", "夜間取數正在 PRTG 階段，暫停快照", targets: 3,
            reasonCode: PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase);
        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(0, row.Attempts);
        Assert.Equal(0, row.Successes);
        Assert.Equal(1, row.Skips);
        Assert.Equal(3, row.Targets);
        Assert.Equal(1, row.Reasons["夜間取數正在 PRTG 階段，暫停快照"]);
        Assert.Equal(1, row.ReasonCodes[PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase]);
    }

    [Fact]
    public void 舊版中文互斥原因可遷移成穩定代碼並保留顯示文字()
    {
        const string legacy = """
            {"Hours":[{"Hour":"2026-09-23T10:00:00","Targets":3,"Attempts":0,"Successes":0,
            "Skips":1,"WriteFailures":0,"Sampled":0,"Reasons":{"夜間取數正在 PRTG 階段，暫停快照":1},
            "SkipReasons":["夜間取數正在 PRTG 階段，暫停快照"]}]}
            """;
        _backend.Blob("prtg_snapshot_diagnostics_v1").Mutate<bool>(_ => (legacy, true));

        var row = NewReader().ReadRecent(new DateTime(2026, 9, 23, 11, 0, 0), 1).Single();

        Assert.Equal(1, row.ReasonCodes[PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase]);
        Assert.Equal(1, row.Reasons["夜間取數正在 PRTG 階段，暫停快照"]);
    }

    [Fact]
    public void 穩定代碼略過每小時只計一次且診斷保留中文文案()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "skip", "結構同步執行中，暫停快照", targets: 2,
            reasonCode: PrtgSnapshotSkipReasonCodes.StructureSyncActive);
        store.Record(hour.AddMinutes(2), "skip", "結構同步執行中，暫停快照", targets: 2,
            reasonCode: PrtgSnapshotSkipReasonCodes.StructureSyncActive);

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();

        Assert.Equal(1, row.Skips);
        Assert.Equal(1, row.Reasons["結構同步執行中，暫停快照"]);
        Assert.Equal(1, row.ReasonCodes[PrtgSnapshotSkipReasonCodes.StructureSyncActive]);
    }

    [Fact]
    public void 寫入失敗與成功取樣分開記錄且不增加取數嘗試()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var store = NewStore();
        store.Record(hour, "attempt", targets: 4);
        store.Record(hour, "success", targets: 4);
        store.Record(hour, "write-failure", "database-write-failed", targets: 4, sampled: 4);

        var row = NewReader().ReadRecent(hour.AddHours(1), 1).Single();
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, row.Successes);
        Assert.Equal(1, row.WriteFailures);
        Assert.Equal(0, store.ReadHours().Single().Sampled);
        Assert.Equal(1, row.Reasons["database-write-failed"]);
    }

    [Fact]
    public void 延後至下一小時寫入的樣本歸屬原小時()
    {
        var hour = new DateTime(2026, 9, 23, 10, 0, 0);
        var service = CreateSnapshotService();
        var store = NewStore();
        store.Record(hour, "attempt", targets: 1);
        store.Record(hour, "success", targets: 1);
        service.Accumulator.Add(1, hour.AddMinutes(15), 42);
        service.Now = () => hour.AddHours(1).AddMinutes(1);

        var rows = service.Accumulator.DrainBefore(hour.AddHours(1), 1, hour.AddHours(1).AddMinutes(1));
        typeof(PrtgSnapshotHostedService).GetMethod("WriteSampledRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, new object[] { rows });

        var diagnostics = service.Diagnostics.ReadRecent(hour.AddHours(2), 2);
        Assert.Equal(1, diagnostics.Single(row => row.Hour == hour).AvailableValues);
        Assert.Equal(PrtgSnapshotHourState.ReportedWriteCountMetTarget, diagnostics.Single(row => row.Hour == hour).State);
        Assert.Equal(PrtgSnapshotHourState.Unknown, diagnostics.Single(row => row.Hour == hour.AddHours(1)).State);
    }

    private PrtgSnapshotHostedService CreateSnapshotService()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        var lifetime = new FakeHostApplicationLifetime();
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var scheduler = new SchedulerRunState();
        var syncState = new PrtgStructureSyncRunState();
        var backfillState = new PrtgBackfillRunState();
        var sync = new PrtgStructureSyncService(settings, _backend, syncState, scheduler, hostStore,
            new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)), backfillState,
            new FakeSentinelStore(), new DataVersionStamp(), lifetime);
        var probe = new PrtgProbeRunState();
        var backfill = new PrtgBackfillService(settings, _backend, backfillState, probe, hostStore, scheduler,
            syncState, new FakeSentinelStore(), sync);
        return new PrtgSnapshotHostedService(settings, _backend, scheduler, sync, backfill, hostStore,
            new FakeSentinelStore(), probe, lifetime);
    }
}
