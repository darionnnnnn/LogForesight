using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.SqlClient;

const long FullN6Rows = 64_800_000;
var targets = new[] { 50, 500, 2_001, 15_000 };
var started = DateTimeOffset.UtcNow;
var runId = Guid.NewGuid().ToString("N");
var sourceCommit = await GetSourceCommitAsync();
var instanceName = $"lf_e07_{runId[..12]}";
var instance = $"(localdb)\\{instanceName}";
var ownerRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "evidence", $"sql-compat-{runId}"));
var resultPath = Path.Combine(ownerRoot, "sql-compat-result.json");
var localDbExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server", "130", "Tools", "Binn", "SqlLocalDB.exe");
var createdInstance = false;
var createdDatabases = new List<string>();
var targetResults = new List<object>();
string? edition = null;
int? engineEdition = null;
var fullShapeDenied = false;
var cleanupSucceeded = false;

try
{
    if (Directory.Exists(ownerRoot)) throw new InvalidOperationException("Refusing to reuse an existing evidence root.");
    if (!File.Exists(localDbExe)) throw new FileNotFoundException("Existing SqlLocalDB.exe was not found; no installer or download is used.", localDbExe);
    Directory.CreateDirectory(ownerRoot);
    await RunLocalDb(localDbExe, "create", instanceName, "15.0");
    createdInstance = true;
    await RunLocalDb(localDbExe, "start", instanceName);

    var masterConnectionString = SqlConnectionString(instance, "master");
    await using (var master = new SqlConnection(masterConnectionString))
    {
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandTimeout = 20;
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), CONVERT(int, SERVERPROPERTY('EngineEdition'))";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("SQL Server did not report provider edition.");
        edition = reader.GetString(0);
        engineEdition = reader.GetInt32(1);
    }

    // This workload's documented full N6 corpus is 64.8 million typed hourly rows.
    // EngineEdition 4 is SQL Server Express; deny that shape before creating any user database.
    fullShapeDenied = engineEdition == 4;
    if (!fullShapeDenied) throw new InvalidOperationException("Expected the owned LocalDB Express instance to report EngineEdition=4; full-shape denial was not demonstrated.");

    foreach (var target in targets)
    {
        var databaseName = $"lf_e07_{runId[..12]}_{target}";
        createdDatabases.Add(databaseName);
        await CreateDatabase(masterConnectionString, databaseName);
        var dataRoot = Path.Combine(ownerRoot, $"data-{target}");
        Directory.CreateDirectory(dataRoot);
        var backend = new StorageBackend(new StorageSettings
        {
            Type = "SqlServer",
            ConnectionString = SqlConnectionString(instance, databaseName)
        }, dataRoot);
        var store = backend.PrtgStore();
        var now = DateTime.Now;
        var deviceCount = (target + 4) / 5;
        var devices = Enumerable.Range(1, deviceCount).Select(id => new PrtgDeviceRow
        {
            Objid = id,
            Name = $"e07-sql-host-{target}-{id:D5}",
            GroupPath = "/e07/sql-compat/synthetic",
            Ip = $"10.{(id >> 16) & 255}.{(id >> 8) & 255}.{(id & 254) + 1}",
            Tags = "e07.synthetic.sql-compat",
            Status = PrtgSensorStatuses.Up,
            SyncedAt = now,
            CreatedAt = now
        }).ToArray();
        var sensors = Enumerable.Range(0, target).Select(i => new PrtgSensorRow
        {
            Objid = 70_000_001L + i,
            DeviceObjid = 1 + i / 5,
            Name = $"e07-sql-sensor-{target}-{i + 1:D5}",
            SensorType = "e07-synthetic-hourly",
            Unit = "percent",
            Status = PrtgSensorStatuses.Up,
            Category = "e07-synthetic",
            CategorySource = "auto",
            SyncedAt = now,
            CreatedAt = now
        }).ToArray();
        if (store.UpsertDevices(devices, now) != deviceCount || store.UpsertSensors(sensors, now) != target)
            throw new InvalidOperationException($"Catalog write count mismatch for {target} target sensors.");

        var period = DateTime.SpecifyKind(DateTime.Today.AddDays(-1), DateTimeKind.Unspecified);
        var rows = sensors.Select((sensor, i) => CreateTypedValue(sensor.Objid, period, i, now)).ToArray();
        var writeTimer = Stopwatch.StartNew();
        if (store.MergeSampledValues(rows) != target) throw new InvalidOperationException($"Typed sample write count mismatch for {target}.");
        writeTimer.Stop();
        var readTimer = Stopwatch.StartNew();
        var readRows = store.GetValues(period, period.AddDays(1));
        readTimer.Stop();
        if (readRows.Count != target || readRows.Any(r => r.TrustVersion != 1 || string.IsNullOrWhiteSpace(r.TrustedProof)))
            throw new InvalidOperationException($"Provider read/proof verification failed for {target}.");

        // A production snapshot checkpoint is filesystem-backed, while its database identity
        // is persisted through StorageBackend.Blob; verify the SQL provider side plus restart read.
        var endpoint = PrtgSnapshotJournal.Endpoint("https://synthetic.invalid/e07-sql-compat");
        using (var journal = new PrtgSnapshotJournal(backend))
        {
            journal.AcquireOwnership();
            if (journal.Load(endpoint, now) != null) throw new InvalidOperationException("Fresh owned journal unexpectedly contained state.");
            var checkpoint = new PrtgSnapshotAccumulator.CheckpointRow(period, sensors[0].Objid, 12.5, 1, 12.5, 12.5);
            journal.Save(endpoint, [checkpoint], [], now);
        }
        using (var restartedJournal = new PrtgSnapshotJournal(backend))
        {
            restartedJournal.AcquireOwnership();
            if (restartedJournal.Load(endpoint, now) is not { Accumulator.Count: 1 })
                throw new InvalidOperationException("A new journal instance did not recover the durable checkpoint.");
        }

        var queueNow = DateTime.UtcNow;
        var queueDevice = deviceCount + 10_000L;
        var queueSensor = new PrtgSensorRow
        {
            Objid = 90_000_000L + target,
            DeviceObjid = queueDevice,
            Name = $"e07-lease-sensor-{target}",
            SensorType = "e07-lease",
            Status = PrtgSensorStatuses.Up,
            SyncedAt = now,
            CreatedAt = now
        };
        var queueItem = new PrtgRecentStateChangeQueueItem(queueDevice, DateTime.Today.AddDays(-1), DateTime.Today,
            $"e07-source-{runId}", $"e07-scope-{target}", queueNow, queueNow, 0, null, null, null);
        if (store.UpsertSensorsAndEnqueueRecentStateChanges([queueSensor], now, queueItem) != 1)
            throw new InvalidOperationException("Could not enqueue the SQL recent-state lease fixture.");
        var lease = store.ClaimRecentStateChanges($"e07-owner-a-{runId}", queueNow.AddSeconds(1), TimeSpan.FromMinutes(1), 1).SingleOrDefault();
        if (lease == null || store.ClaimRecentStateChanges($"e07-owner-b-{runId}", queueNow.AddSeconds(2), TimeSpan.FromMinutes(1), 1).Count != 0)
            throw new InvalidOperationException("SQL lease did not exclude a competing owner.");
        if (!store.AcknowledgeRecentStateChange(lease)) throw new InvalidOperationException("SQL lease acknowledgement failed.");

        var runStore = new BatchRunStore(backend.LogStore("e07_sql_batch_runs_" + runId), backend.LogStore("e07_sql_batch_run_logs_" + runId));
        var run = new BatchRun { HostName = "e07-sql-compat", StartedAt = now, AppVersion = "e07-private", Args = $"compat:{target}", Trigger = "console" };
        var runIdValue = runStore.StartRun(run);
        runStore.AppendLog(new BatchRunLog { RunId = runIdValue, LoggedAt = now, Level = "Info", Logger = "e07", Message = "synthetic provider compatibility" });
        run.FinishedAt = now.AddSeconds(1);
        run.ExitCode = 0;
        runStore.FinishRun(run);
        var runRead = runStore.GetRun(runIdValue);
        var runLogs = runStore.GetLogs(runIdValue);
        if (runIdValue <= 0 || runRead?.FinishedAt == null || runLogs.Count != 1 || runLogs[0].RunId != runIdValue)
            throw new InvalidOperationException("SQL-backed RunId or log read did not round-trip.");
        var restartedRunStore = new BatchRunStore(backend.LogStore("e07_sql_batch_runs_" + runId), backend.LogStore("e07_sql_batch_run_logs_" + runId));
        var secondRun = new BatchRun { HostName = "e07-sql-compat", StartedAt = now.AddSeconds(2), AppVersion = "e07-private", Args = $"compat-reopen:{target}", Trigger = "console" };
        var secondRunId = restartedRunStore.StartRun(secondRun);
        secondRun.FinishedAt = now.AddSeconds(3);
        secondRun.ExitCode = 0;
        restartedRunStore.FinishRun(secondRun);
        if (secondRunId <= runIdValue || restartedRunStore.GetRun(secondRunId)?.FinishedAt == null)
            throw new InvalidOperationException("SQL monotonic RunId did not continue across store-instance restart.");

        var rollbackEvidence = await VerifyRollback(masterConnectionString, databaseName, store, now);
        var oldCreatedAt = DateTime.Today.AddDays(-181);
        var recentCreatedAt = DateTime.Today.AddDays(-179);
        var pruneSensor = 99_000_000L + target;
        if (store.MergeSampledValues([CreateTypedValue(pruneSensor, period.AddHours(-1), target + 1, oldCreatedAt),
                CreateTypedValue(pruneSensor + 1, period.AddHours(-2), target + 2, recentCreatedAt)]) != 2)
            throw new InvalidOperationException("Could not seed SQL prune boundary rows.");
        var pruned = store.Prune(180);
        using (var context = backend.CreateContext())
        {
            var oldRemaining = context.PrtgValues.Count(v => v.SensorObjid == pruneSensor);
            var recentRemaining = context.PrtgValues.Count(v => v.SensorObjid == pruneSensor + 1);
            if (oldRemaining != 0 || recentRemaining != 1) throw new InvalidOperationException("SQL production Prune(180) boundary mismatch.");
        }
        targetResults.Add(new
        {
            targetSensorCount = target,
            targetDeviceCount = deviceCount,
            syntheticTypedRowsWritten = rows.Length,
            persistedTypedRowsRead = readRows.Count,
            writeMilliseconds = writeTimer.Elapsed.TotalMilliseconds,
            queryMilliseconds = readTimer.Elapsed.TotalMilliseconds,
            queryShape = "production EfPrtgStore.GetValues(period, period+1 day), ordered provider query; execution plan not captured by this compatibility slice",
            journal = new { backendDatabaseIdBlob = true, filesystemCheckpointSavedAndReloaded = true },
            lease = new { acquiredByOneOwner = true, competingOwnerExcluded = true, acknowledged = true },
            runId = new { first = runIdValue, secondAfterStoreReopen = secondRunId, monotonicPositive = secondRunId > runIdValue, completedRunRead = runRead.FinishedAt.HasValue, logCount = runLogs.Count },
            rollback = new { fault = "named CHECK constraint rejects the second attempted sensor row", expectedSqlErrorNumber = 547, actualSqlErrorNumber = rollbackEvidence.ErrorNumber, sqlExceptionErrorNumbers = rollbackEvidence.ErrorNumbers, constraintMatched = true, productionMergeRolledBackAllAttemptedRows = true },
            prune180 = new { returnedDeletedCount = pruned, old181DaySentinelDeleted = true, recent179DaySentinelRetained = true },
            datasetLabel = "synthetic provider/storage compatibility only; not native evidence"
        });

        await DropDatabase(masterConnectionString, databaseName);
        createdDatabases.Remove(databaseName);
    }

    await RunLocalDb(localDbExe, "stop", instanceName, "-k");
    await RunLocalDb(localDbExe, "delete", instanceName);
    createdInstance = false;
    cleanupSucceeded = createdDatabases.Count == 0 && !createdInstance;
    var result = new
    {
        schema = "LogForesight.SqlServerM3CompatibilityResult",
        schemaVersion = 1,
        runId,
        startedAtUtc = started,
        completedAtUtc = DateTimeOffset.UtcNow,
        sourceCommit,
        sourceHashes = new Dictionary<string, string>
        {
            ["LogForesight.Core/Persistence/StorageBackend.cs"] = HashSource("LogForesight.Core/Persistence/StorageBackend.cs"),
            ["LogForesight.Core/Persistence/Sql/EfPrtgStore.cs"] = HashSource("LogForesight.Core/Persistence/Sql/EfPrtgStore.cs"),
            ["LogForesight.Core/Persistence/Sql/LfDbContext.cs"] = HashSource("LogForesight.Core/Persistence/Sql/LfDbContext.cs"),
            ["LogForesight.Core/Persistence/BatchRunStore.cs"] = HashSource("LogForesight.Core/Persistence/BatchRunStore.cs"),
            ["LogForesight.Web/Services/PrtgSnapshotJournal.cs"] = HashSource("LogForesight.Web/Services/PrtgSnapshotJournal.cs"),
            ["scripts/prtg-workload/sqlserver-compat/Program.cs"] = HashSource("scripts/prtg-workload/sqlserver-compat/Program.cs")
        },
        provider = new { instance, edition, engineEdition },
        fixedTargets = targets,
        datasets = targetResults,
        fullShapePreflight = new { n6HourlyRows = FullN6Rows, deniedBeforeUserDatabaseCreation = fullShapeDenied, reason = "owned provider reported Express EngineEdition=4; SQL M3 compatibility work does not claim or attempt the 64.8M-row N6 shape" },
        syntheticOnly = true,
        sqlM3CompatibilityChecksPassed = targetResults.Count == targets.Length,
        n6FullCapacityAccepted = false,
        n6RetentionAccepted = false,
        sqliteFullWorkloadSubstituted = false,
        cleanupSucceeded,
        databaseNamesWereUniqueToRun = true
    };
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptionsHolder.Options));
    Console.WriteLine($"result={resultPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED run={runId} type={ex.GetType().Name}; details omitted from console to avoid provider secrets.");
    return 2;
}
finally
{
    if (createdDatabases.Count > 0)
    {
        var masterCs = SqlConnectionString(instance, "master");
        foreach (var database in createdDatabases.ToArray())
        {
            try { await DropDatabase(masterCs, database); createdDatabases.Remove(database); }
            catch { }
        }
    }
    if (createdInstance)
    {
        try { await RunLocalDb(localDbExe, "stop", instanceName, "-k"); } catch { }
        try { await RunLocalDb(localDbExe, "delete", instanceName); } catch { }
    }
}

static async Task<(int ErrorNumber, int[] ErrorNumbers)> VerifyRollback(string masterConnectionString, string databaseName, EfPrtgStore store, DateTime createdAt)
{
    var constraintName = "CK_e07_abort_sample_insert";
    await using (var connection = new SqlConnection(SqlConnectionStringFromMaster(masterConnectionString, databaseName)))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE [dbo].[lf_prtg_values] WITH CHECK ADD CONSTRAINT [{constraintName}] CHECK ([sensor_objid] <> 98000002)";
        await command.ExecuteNonQueryAsync();
    }
    try
    {
        var beforeIds = new[] { 98_000_001L, 98_000_002L };
        var rollbackRows = beforeIds.Select((id, index) => CreateTypedValue(id, DateTime.Today.AddDays(-2).AddHours(index), 120_000 + index, createdAt)).ToArray();
        int actualErrorNumber;
        int[] sqlErrorNumbers;
        try
        {
            store.MergeSampledValues(rollbackRows);
            throw new InvalidOperationException("SQL failure CHECK constraint did not abort production batch write.");
        }
        catch (Exception ex)
        {
            var sqlException = FindSqlException(ex);
            sqlErrorNumbers = sqlException?.Errors.Cast<SqlError>().Select(error => error.Number).ToArray() ?? [];
            var constraintSourceMatched = sqlException?.Errors.Cast<SqlError>().Any(error =>
                error.Number == 547 && error.Message.Contains(constraintName, StringComparison.Ordinal)) == true;
            if (sqlException?.Number != 547 || !constraintSourceMatched)
                throw new InvalidOperationException($"Rollback probe did not fail from the expected named CHECK constraint. Diagnostic: {SafeSqlFailureDiagnostic(ex)}");
            actualErrorNumber = sqlException.Number;
        }
        using var verify = new SqlConnection(SqlConnectionStringFromMaster(masterConnectionString, databaseName));
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT_BIG(*) FROM lf_prtg_values WHERE sensor_objid IN (98000001,98000002)";
        var rowsAfter = Convert.ToInt64(await count.ExecuteScalarAsync());
        if (rowsAfter != 0) throw new InvalidOperationException("SQL provider transaction retained rows after CHECK constraint failure.");
        return (actualErrorNumber, sqlErrorNumbers);
    }
    finally
    {
        await using var connection = new SqlConnection(SqlConnectionStringFromMaster(masterConnectionString, databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE [dbo].[lf_prtg_values] DROP CONSTRAINT IF EXISTS [{constraintName}]";
        await command.ExecuteNonQueryAsync();
    }
}

static SqlException? FindSqlException(Exception exception)
{
    for (Exception? current = exception; current != null; current = current.InnerException)
        if (current is SqlException sqlException) return sqlException;
    return null;
}

static string SafeSqlFailureDiagnostic(Exception exception)
{
    var stages = new List<string>();
    for (Exception? current = exception; current != null; current = current.InnerException)
    {
        if (current is SqlException sqlException)
            stages.Add($"{current.GetType().FullName}[{string.Join(',', sqlException.Errors.Cast<SqlError>().Select(error => error.Number))}]");
        else stages.Add(current.GetType().FullName ?? current.GetType().Name);
    }
    return string.Join(" -> ", stages);
}

static PrtgValueRow CreateTypedValue(long sensorId, DateTime period, int seed, DateTime createdAt)
{
    period = DateTime.SpecifyKind(period, DateTimeKind.Unspecified);
    var utc = DateTime.SpecifyKind(period, DateTimeKind.Utc);
    var value = 25 + seed % 50;
    var slots = Enumerable.Range(0, 4).Select(slot =>
    {
        var measuredAt = utc.AddMinutes(slot * 15);
        var physicalHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"e07|{sensorId}|{period:O}|{slot}")))[..32];
        return new PrtgTrustedSampleSlot(slot, physicalHash, measuredAt, measuredAt.AddSeconds(5), value);
    }).ToArray();
    var effective = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);
    var proof = PrtgTrustedSampleProof.From(new PrtgTrustedSample(sensorId, value, "e07-synthetic-sql-source-v1",
        $"resource-{sensorId}", $"channel-{sensorId}", "generation-1", "synthetic-semantic-v1", "synthetic-strategy-v1",
        15, effective, slots[0].MeasuredAt, slots[0].ReceivedAt, TimeSpan.FromMinutes(15),
        PrtgTrustedSampleQuality.Good, slots[0].PhysicalIdHash, "UTC", "UTC"), slots);
    return new PrtgValueRow
    {
        SensorObjid = sensorId,
        PeriodStart = period,
        AvgValue = value,
        MinValue = value,
        MaxValue = value,
        Coverage = 100,
        Quality = PrtgDataQuality.Sampled,
        CreatedAt = createdAt,
        TrustVersion = 1,
        TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
    };
}

static async Task CreateDatabase(string masterConnectionString, string databaseName)
{
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"CREATE DATABASE [{databaseName}]";
    await command.ExecuteNonQueryAsync();
}

static async Task DropDatabase(string masterConnectionString, string databaseName)
{
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"IF DB_ID(N'{databaseName.Replace("'", "''")}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
    await command.ExecuteNonQueryAsync();
}

static string SqlConnectionString(string instance, string database) => new SqlConnectionStringBuilder
{
    DataSource = instance,
    InitialCatalog = database,
    IntegratedSecurity = true,
    TrustServerCertificate = true,
    ConnectTimeout = 20,
    ApplicationName = "LogForesight-E07-SqlCompat-" + Environment.ProcessId
}.ConnectionString;

static string SqlConnectionStringFromMaster(string masterConnectionString, string databaseName) =>
    new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = databaseName }.ConnectionString;

static string HashSource(string relativePath)
{
    var repoRoot = RepoRoot();
    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repoRoot, relativePath))));
}

static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

static async Task<string> GetSourceCommitAsync()
{
    var start = new ProcessStartInfo("git")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = RepoRoot()
    };
    start.ArgumentList.Add("rev-parse");
    start.ArgumentList.Add("HEAD");
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not identify source worktree revision.");
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    var commit = output.Trim();
    if (process.ExitCode != 0 || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException("Could not identify source worktree revision.");
    return commit;
}

static async Task RunLocalDb(string exe, params string[] arguments)
{
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var command = Process.Start(start) ?? throw new InvalidOperationException("Could not start existing SqlLocalDB.exe.");
    var stdout = await command.StandardOutput.ReadToEndAsync();
    var stderr = await command.StandardError.ReadToEndAsync();
    await command.WaitForExitAsync();
    if (command.ExitCode != 0) throw new InvalidOperationException($"SqlLocalDB operation failed ({arguments[0]}): {stderr.Trim()} {stdout.Trim()}");
}

static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
