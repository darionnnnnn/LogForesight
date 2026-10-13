using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class BatchRunIdentityAllocationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"logforesight-run-ids-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public BatchRunIdentityAllocationTests()
    {
        _connectionString = $"Data Source={_databasePath};Pooling=False";
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connectionString).Options);

    private EfJsonLogStore NewLogStore(string key) => new(NewContext, key);

    private BatchRunStore NewStore() =>
        new(new EfJsonLogStore(NewContext, "batch_runs"), new EfJsonLogStore(NewContext, "batch_run_logs"));

    [Fact]
    public async Task IndependentStoresAllocateDistinctIdsAndKeepBothRunHistories()
    {
        // Construct both stores before either allocates, reproducing independent max-ID snapshots.
        var first = NewStore();
        var second = NewStore();

        var ids = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var store = i % 2 == 0 ? first : second;
            var startedAt = DateTime.Now;
            var run = new BatchRun
            {
                HostName = $"host-{i}",
                StartedAt = startedAt,
                FinishedAt = startedAt.AddSeconds(1),
                ExitCode = 0
            };
            var runId = store.StartRun(run);
            store.AppendLog(new BatchRunLog { RunId = runId, Level = "Info", Logger = "test", Message = $"run-{i}" });
            return (RunId: runId, LogId: store.GetLogs(runId).Single().LogId);
        })));

        Assert.Equal(ids.Length, ids.Select(x => x.RunId).Distinct().Count());
        Assert.Equal(ids.Length, ids.Select(x => x.LogId).Distinct().Count());
        Assert.Equal(ids.Length, first.GetRecentRuns(1, null).Count);
    }

    [Fact]
    public void RestartAllocatesAfterUnfinishedRunAndPreservesItsIdentity()
    {
        var first = NewStore();
        var unfinishedId = first.StartRun(new BatchRun
        {
            HostName = "unfinished",
            StartedAt = DateTime.Now
        });

        var restarted = NewStore();
        var nextId = restarted.StartRun(new BatchRun
        {
            HostName = "after-restart",
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now,
            ExitCode = 0
        });

        Assert.True(nextId > unfinishedId);
        var runs = restarted.GetRecentRuns(1, null);
        Assert.Equal(2, runs.Count);
        Assert.Null(runs.Single(run => run.RunId == unfinishedId).FinishedAt);
        Assert.Equal("after-restart", runs.Single(run => run.RunId == nextId).HostName);
    }

    [Fact]
    public void ReservationSurvivesPruningAndUnwrittenReservationGap()
    {
        var initial = NewStore();
        var unfinishedId = initial.StartRun(new BatchRun { HostName = "will-prune", StartedAt = DateTime.Now });

        // Simulate an allocated ID whose following append failed, then prune run history.
        var unusedId = NewLogStore("batch_runs").ReserveMonotonicId(unfinishedId);
        NewLogStore("batch_runs").Prune(DateTime.MaxValue);

        var afterPrune = NewStore();
        var nextId = afterPrune.StartRun(new BatchRun { HostName = "after-prune", StartedAt = DateTime.Now });

        Assert.True(unusedId > unfinishedId);
        Assert.True(nextId > unusedId);
        Assert.Equal("after-prune", Assert.Single(afterPrune.GetRecentRuns(1, null)).HostName);
    }

    [Fact]
    public void LegacyHighRunIdIsAdvancedInOneReservation()
    {
        const long legacyId = 8_000_000_000_000_000_000;
        var seed = NewStore();
        seed.FinishRun(new BatchRun
        {
            RunId = legacyId,
            HostName = "legacy",
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now,
            ExitCode = 0
        });

        var nextId = NewStore().StartRun(new BatchRun { HostName = "next", StartedAt = DateTime.Now });

        Assert.Equal(legacyId + 1, nextId);
    }

    [Theory]
    [InlineData("broken", typeof(FormatException))]
    [InlineData("9223372036854775807", typeof(OverflowException))]
    [InlineData("00000000000000000000", typeof(InvalidDataException))]
    public void InvalidOrExhaustedCounterCannotAllocateOrAppend(string value, Type errorType)
    {
        using (var context = NewContext())
        {
            context.Blobs.Add(new BlobRow { BlobKey = "log_id_allocator:batch_runs", Content = value, Version = 1 });
            context.SaveChanges();
        }
        var store = NewStore();
        var error = Record.Exception(() => store.StartRun(new BatchRun { HostName = "rejected", StartedAt = DateTime.Now }));
        Assert.NotNull(error);
        Assert.Equal(errorType, error.GetType());
        using var check = NewContext();
        Assert.Empty(check.LogLines);
        Assert.Equal(value, check.Blobs.Single().Content);
        Assert.Equal(1, check.Blobs.Single().Version);
    }

    [Fact]
    public void LegacyIdsBehindCorruptTailFailClosedUntilSafeCounterExists()
    {
        using (var context = NewContext())
        {
            for (var id = 1; id <= 100; id++)
                context.LogLines.Add(new LogLineRow
                {
                    LogKey = "batch_runs",
                    Line = System.Text.Json.JsonSerializer.Serialize(new BatchRun { RunId = id, HostName = $"legacy-{id}" }, LfJsonOptions.Compact),
                    CreatedAt = DateTime.Now
                });
            for (var i = 0; i < BatchRunStore.IdProbeLines; i++)
            {
                context.LogLines.Add(new LogLineRow { LogKey = "batch_runs", Line = "{bad", CreatedAt = DateTime.Now });
                context.LogLines.Add(new LogLineRow { LogKey = "batch_run_logs", Line = "{bad", CreatedAt = DateTime.Now });
            }
            context.SaveChanges();
        }

        var storeWithoutCounter = NewStore();
        var error = Record.Exception(() => storeWithoutCounter.StartRun(new BatchRun { HostName = "must-not-reuse", StartedAt = DateTime.Now }));
        Assert.IsType<InvalidDataException>(error);
        using (var check = NewContext())
        {
            Assert.Equal(100 + BatchRunStore.IdProbeLines,
                check.LogLines.Count(line => line.LogKey == "batch_runs"));
            Assert.Equal(BatchRunStore.IdProbeLines, check.LogLines.Count(line => line.LogKey == "batch_run_logs"));
            Assert.Empty(check.Blobs);
        }

        // Durable counters are authoritative for both run and diagnostic IDs even when their
        // legacy tails are unreadable.
        using (var context = NewContext())
        {
            context.Blobs.Add(new BlobRow { BlobKey = "log_id_allocator:batch_runs", Content = "100", Version = 1 });
            context.Blobs.Add(new BlobRow { BlobKey = "log_id_allocator:batch_run_logs", Content = "12", Version = 1 });
            context.SaveChanges();
        }
        var resumed = NewStore();
        var next = resumed.StartRun(new BatchRun { HostName = "safe-counter", StartedAt = DateTime.Now });
        Assert.Equal(101, next);
        var log = new BatchRunLog { RunId = next, Level = "Info", Logger = "test", Message = "safe counter" };
        resumed.AppendLog(log);
        Assert.Equal(13, log.LogId);
    }

    [Fact]
    public void NonAllocatorLogConsumersFailClosedOnEntirelyUnreadableTail()
    {
        using (var context = NewContext())
        {
            foreach (var key in new[] { "audit", "import_logs", "record_handling_log" })
                for (var i = 0; i < BatchRunStore.IdProbeLines; i++)
                    context.LogLines.Add(new LogLineRow { LogKey = key, Line = "{broken", CreatedAt = DateTime.Now });
            context.SaveChanges();
        }

        var audit = new AuditLogStore(NewLogStore("audit"));
        var import = new ImportLogStore(NewLogStore("import_logs"));
        Assert.Throws<InvalidDataException>(() => audit.Append(new AuditEntry()));
        Assert.Throws<InvalidDataException>(() => import.Append(new ImportLogEntry()));
        var probe = NewLogStore("record_handling_log");
        var handling = new EfRecordHandlingStore(NewContext, probe);
        Assert.Throws<InvalidDataException>(() => handling.AppendLog(new RecordHandlingLog()));
    }
}
