using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

const long FixedRows = 15_000L * 180 * 24;
const int HostCount = 3_000;
const int SensorCount = 15_000;
const int HoursPerSensor = 180 * 24;
const int BatchSize = 100;

try
{
    var argsMap = ParseArgs(args);
    var mode = Required(argsMap, "mode");
    var root = Path.GetFullPath(Required(argsMap, "root"));
    if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
        throw new ArgumentException("Output must be on a local fixed volume.");
    for (var ancestor = new DirectoryInfo(root).Parent; ancestor is not null; ancestor = ancestor.Parent)
        if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Output ancestors must not be reparse points.");
    var resume = argsMap.ContainsKey("resume");
    var adoptLegacy = argsMap.ContainsKey("adopt-legacy");
    var simulateCrashAfterCommit = argsMap.ContainsKey("simulate-crash-after-db-commit");
    var controlledStopAfterBatches = argsMap.TryGetValue("stop-after-batches", out var stopAfterText)
        ? int.Parse(stopAfterText, System.Globalization.CultureInfo.InvariantCulture) : 0;
    if (controlledStopAfterBatches < 0) throw new ArgumentOutOfRangeException("stop-after-batches");
    if (resume && adoptLegacy || simulateCrashAfterCommit && (resume || adoptLegacy)) throw new ArgumentException("Invalid combination of resume/adoption/interruption controls.");
    if (Directory.Exists(root) && !resume && !adoptLegacy || !Directory.Exists(root) && (resume || adoptLegacy))
        throw new ArgumentException(resume || adoptLegacy
            ? "--resume/--adopt-legacy requires an existing owned run directory."
            : "Use a new, dedicated output directory; pass --resume only for an owned run.");
    if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Existing run root must not be a reparse point.");
    var resultPath = Path.Combine(root, "n6-high-water-result.json");
    if (File.Exists(resultPath)) throw new InvalidOperationException($"Refusing to overwrite prior result: {resultPath}");

    if (mode != "n6") throw new ArgumentException("--mode must be n6.");
    var provider = argsMap.GetValueOrDefault("provider", "sqlite").ToLowerInvariant();
    if (provider is not ("sqlite" or "sqlserver")) throw new ArgumentException("--provider must be sqlite or sqlserver.");
    var requestedRows = long.Parse(Required(argsMap, "rows"), System.Globalization.CultureInfo.InvariantCulture);
    if (requestedRows is < 1 or > FixedRows) throw new ArgumentOutOfRangeException("rows", $"Must be 1..{FixedRows}; the fixed full shape is {FixedRows}.");
    var sourceFingerprint = GetSourceFingerprint();
    if (adoptLegacy)
    {
        if (provider != "sqlite")
            throw new InvalidOperationException("Legacy adoption is restricted to SQLite runs.");
        AdoptLegacyRun(root, requestedRows, Required(argsMap, "source-root"), Required(argsMap, "source-log"), sourceFingerprint);
        return 0;
    }
    if (resume && provider != "sqlite") throw new InvalidOperationException("Resume is supported only for an owned SQLite run.");
    Directory.CreateDirectory(root);
    var manifestPath = Path.Combine(root, "n6-run-manifest.json");
    var checkpointPath = Path.Combine(root, "n6-checkpoint.json");
    var databasePath = Path.Combine(root, $"n6-{provider}-{Guid.NewGuid():N}.db");
    RunManifest manifest;
    RunCheckpoint? priorCheckpoint = null;
    if (resume)
    {
        manifest = ReadAndValidateManifest(manifestPath, root, provider, requestedRows, sourceFingerprint);
        priorCheckpoint = ReadAndValidateCheckpoint(checkpointPath, manifest);
        databasePath = manifest.DatabasePath;
        var recoveredRows = ValidateOwnedDatabase(databasePath, manifest, priorCheckpoint.CommittedRows);
        if (recoveredRows != priorCheckpoint.CommittedRows)
        {
            AuditOwnedRowRange(databasePath, manifest, priorCheckpoint.CommittedRows, recoveredRows);
            UpdateOwnedDatabaseCheckpoint(databasePath, manifest, recoveredRows);
            priorCheckpoint.CommittedRows = recoveredRows;
            priorCheckpoint.CheckpointSha256 = HashCheckpoint(priorCheckpoint);
            WriteJsonAtomic(checkpointPath, priorCheckpoint);
        }
    }
    else
    {
        manifest = CreateManifest(root, databasePath, provider, requestedRows, sourceFingerprint);
        WriteJsonAtomic(manifestPath, manifest);
    }
    string connectionString;
    if (provider == "sqlite")
    {
        connectionString = $"Data Source={databasePath};Pooling=False;Cache=Shared";
        var volume = new DriveInfo(Path.GetPathRoot(root)!);
        var space = volume.AvailableFreeSpace;
        // Keep a database copy for backup/restore staging and leave >=25% of the
        // total volume free afterwards. This preflight is not a capacity verdict.
        // Frozen M2 projection is 73,518,727,168 bytes. A separately generated
        // current four-slot 100k pilot used 127,053,824 bytes, projecting to
        // 82,330,877,952. Guard with the larger figure; neither is a full pass.
        var estimatedDbBytes = Math.Max(73_518_727_168L, 127_053_824L * (FixedRows / 100_000));
        var existingBytes = resume ? FileBytes(databasePath) : 0;
        var requiredFree = (resume ? Math.Max(0, estimatedDbBytes - existingBytes) + estimatedDbBytes : estimatedDbBytes * 2) +
            (long)Math.Ceiling(volume.TotalSize * 0.25);
        if (requestedRows == FixedRows && space < requiredFree)
            throw new InvalidOperationException($"Full SQLite run refused: free bytes {space} below estimated DB+backup+25% reserve {requiredFree}.");
    }
    else
    {
        var raw = Environment.GetEnvironmentVariable("LF_CAPACITY_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Set LF_CAPACITY_SQLSERVER_CONNECTION to a dedicated loopback SQL connection; it is never written to the report.");
        var builder = new SqlConnectionStringBuilder(raw);
        if (builder.AttachDBFilename.Length > 0) throw new InvalidOperationException("AttachDbFilename is not permitted for isolated runs.");
        if (!IsLoopbackDataSource(builder.DataSource)) throw new InvalidOperationException("SQL Server must be a LocalDB instance or numeric loopback TCP endpoint with an explicit port.");
        builder.ConnectTimeout = 5;
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), CONVERT(int, SERVERPROPERTY('EngineEdition'))";
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Could not read local SQL edition.");
            var edition = reader.GetString(0);
            var engineEdition = reader.GetInt32(1);
            // EngineEdition 4 is Express; its database size ceiling is below this fixed dataset.
            if (engineEdition == 4) throw new InvalidOperationException($"SQL Server preflight denied full workload: {edition} (EngineEdition=4) has a relational database size ceiling below the fixed full dataset; estimated full dataset is 73,518,727,168 bytes before backup/temp/WAL and reserve.");
        }
        builder.InitialCatalog = $"lf_capacity_value_{Guid.NewGuid():N}";
        connectionString = builder.ConnectionString;
        databasePath = "sqlserver-loopback-dedicated-database";
    }

    if (resume) connectionString = $"Data Source={databasePath};Pooling=False;Cache=Shared";
    var settings = new StorageSettings { Type = provider == "sqlite" ? "Sqlite" : "SqlServer", ConnectionString = connectionString, SqliteWal = true };
    var backend = new StorageBackend(settings, root);
    var store = backend.PrtgStore();
    var now = DateTime.Now;
    var devices = Enumerable.Range(1, HostCount).Select(id => new PrtgDeviceRow
    {
        Objid = id, Name = $"capacity-host-{id:D4}", GroupPath = $"/capacity/region-{(id - 1) % 10:D2}",
        Ip = $"10.{(id - 1) / 65_536}.{((id - 1) / 256) % 256}.{((id - 1) % 254) + 1}",
        Tags = "acceptance.synthetic", Status = PrtgSensorStatuses.Up, SyncedAt = now, CreatedAt = now
    }).ToList();
    var sensors = Enumerable.Range(0, SensorCount).Select(i => new PrtgSensorRow
    {
        Objid = 1_000_001L + i, DeviceObjid = 1 + i / 5, Name = $"capacity-sensor-{i + 1:D5}",
        SensorType = "synthetic-hourly-aggregate", Unit = "percent", Status = PrtgSensorStatuses.Up,
        Category = "acceptance", CategorySource = "auto", SyncedAt = now, CreatedAt = now
    }).ToList();
    var mapDate = DateTime.ParseExact(manifest.RunDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    var hostMaps = devices.Select(d => new PrtgHostMapRow
    {
        MapDate = mapDate, DeviceObjid = d.Objid, HostId = d.Objid, HostName = $"capacity-host-{d.Objid:D4}",
        Ip = d.Ip, MapStatus = "mapped", CreatedAt = now
    }).ToList();
    var devicesWritten = resume ? HostCount : store.UpsertDevices(devices, now);
    var sensorsWritten = resume ? SensorCount : store.UpsertSensors(sensors, now);
    var mapsWritten = resume ? HostCount : store.ReplaceHostMapForDate(mapDate, hostMaps);
    if (devicesWritten != HostCount || sensorsWritten != SensorCount || mapsWritten != HostCount)
        throw new InvalidOperationException($"Shape setup mismatch: devices={devicesWritten}, sensors={sensorsWritten}, hostMaps={mapsWritten}.");
    if (provider == "sqlite" && !resume)
    {
        InitializeOwnedDatabase(databasePath, manifest);
        var initialCheckpoint = new RunCheckpoint
        {
            Schema = "LogForesight.N6StorageCheckpoint", SchemaVersion = 1,
            RunId = manifest.RunId, ManifestSha256 = manifest.ManifestSha256,
            CommittedRows = 0, CurrentProcessActiveSeconds = 0, InitialElapsedSeconds = 0,
            ResumeWallSeconds = 0, ResumeCount = 0, LastProcessStartedUtc = DateTimeOffset.UtcNow,
            CheckpointSha256 = string.Empty
        };
        initialCheckpoint.CheckpointSha256 = HashCheckpoint(initialCheckpoint);
        WriteJsonAtomic(checkpointPath, initialCheckpoint);
    }

    var firstPeriod = mapDate.AddDays(-180).Date;
    var batch = new List<PrtgValueRow>(BatchSize);
    var batchSamplesMs = new List<double>();
    long committed = priorCheckpoint?.CommittedRows ?? 0;
    long highWaterBytes = 0;
    long storageHighWaterBytes = 0;
    var outputVolume = new DriveInfo(Path.GetPathRoot(root)!);
    long minimumFreeBytes = outputVolume.AvailableFreeSpace;
    long proofBytesTotal = 0;
    var proofBytesMin = int.MaxValue;
    var proofBytesMax = 0;
    long peakWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64;
    var run = Stopwatch.StartNew();
    var resumeWallSeconds = priorCheckpoint?.ResumeWallSeconds ?? 0;
    var resumeCount = priorCheckpoint?.ResumeCount ?? 0;
    var initialElapsedSeconds = priorCheckpoint?.InitialElapsedSeconds ?? 0;
    var accumulatedActiveSeconds = priorCheckpoint?.AccumulatedActiveSeconds ?? 0;
    var currentProcessRows = 0L;
    if (resume) resumeCount++;
    var startSensorIndex = checked((int)(committed / HoursPerSensor));
    var startHourIndex = checked((int)(committed % HoursPerSensor));
    for (var sensorIndex = startSensorIndex; sensorIndex < SensorCount && committed < requestedRows; sensorIndex++)
    {
        var sensorId = sensors[sensorIndex].Objid;
        for (var hour = sensorIndex == startSensorIndex ? startHourIndex : 0; hour < HoursPerSensor && committed < requestedRows; hour++)
        {
            var typedValue = CreateTypedValue(sensorId, firstPeriod.AddHours(hour), sensorIndex, hour, now);
            var proofBytes = System.Text.Encoding.UTF8.GetByteCount(typedValue.TrustedProof!);
            proofBytesTotal += proofBytes;
            currentProcessRows++;
            proofBytesMin = Math.Min(proofBytesMin, proofBytes);
            proofBytesMax = Math.Max(proofBytesMax, proofBytes);
            batch.Add(typedValue);
            if (batch.Count != BatchSize && committed + batch.Count != requestedRows) continue;
            if (resumeCount == 0) initialElapsedSeconds = run.Elapsed.TotalSeconds;
            var pendingCheckpoint = new RunCheckpoint
            {
                Schema = "LogForesight.N6StorageCheckpoint", SchemaVersion = 1,
                RunId = manifest.RunId, ManifestSha256 = manifest.ManifestSha256,
                CommittedRows = committed, CurrentProcessActiveSeconds = run.Elapsed.TotalSeconds,
                InitialElapsedSeconds = initialElapsedSeconds,
                AccumulatedActiveSeconds = accumulatedActiveSeconds + run.Elapsed.TotalSeconds,
                ResumeWallSeconds = resumeWallSeconds + (resume ? run.Elapsed.TotalSeconds : 0),
                ResumeCount = resumeCount, LastProcessStartedUtc = DateTimeOffset.UtcNow,
                CheckpointSha256 = string.Empty
            };
            pendingCheckpoint.CheckpointSha256 = HashCheckpoint(pendingCheckpoint);
            WriteJsonAtomic(checkpointPath, pendingCheckpoint);
            var writeWatch = Stopwatch.StartNew();
            var written = store.MergeSampledValues(batch);
            writeWatch.Stop();
            if (written != batch.Count) throw new InvalidOperationException($"Batch write mismatch: expected {batch.Count}, got {written}.");
            batchSamplesMs.Add(writeWatch.Elapsed.TotalMilliseconds);
            committed += written;
            batch.Clear();
            if (simulateCrashAfterCommit)
            {
                Console.Error.WriteLine($"INTERRUPTED: simulated process loss after production SQLite commit at row {committed}, before ownership marker/checkpoint update.");
                return 76;
            }
            var process = Process.GetCurrentProcess();
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
            if (provider == "sqlite")
            {
                highWaterBytes = Math.Max(highWaterBytes, FileBytes(databasePath));
                storageHighWaterBytes = Math.Max(storageHighWaterBytes,
                    FileBytes(databasePath) + FileBytes(databasePath + "-wal") + FileBytes(databasePath + "-shm"));
                minimumFreeBytes = Math.Min(minimumFreeBytes, outputVolume.AvailableFreeSpace);
            }
            if (committed % 100_000 == 0 || committed == requestedRows)
            {
                Console.WriteLine($"rows={committed}/{requestedRows} elapsed_s={run.Elapsed.TotalSeconds:F1} db_bytes={highWaterBytes} process_ws_peak_bytes={peakWorkingSetBytes}");
            }
            if (provider == "sqlite")
                UpdateOwnedDatabaseCheckpoint(databasePath, manifest, committed);
            if (resumeCount == 0) initialElapsedSeconds = run.Elapsed.TotalSeconds;
            var checkpoint = new RunCheckpoint
            {
                Schema = "LogForesight.N6StorageCheckpoint", SchemaVersion = 1,
                RunId = manifest.RunId, ManifestSha256 = manifest.ManifestSha256,
                CommittedRows = committed, CurrentProcessActiveSeconds = run.Elapsed.TotalSeconds,
                InitialElapsedSeconds = initialElapsedSeconds,
                AccumulatedActiveSeconds = accumulatedActiveSeconds + run.Elapsed.TotalSeconds,
                ResumeWallSeconds = resumeWallSeconds + (resume ? run.Elapsed.TotalSeconds : 0), ResumeCount = resumeCount,
                LastProcessStartedUtc = DateTimeOffset.UtcNow, CheckpointSha256 = string.Empty
            };
            checkpoint.CheckpointSha256 = HashCheckpoint(checkpoint);
            WriteJsonAtomic(checkpointPath, checkpoint);
            if (committed % 100_000 == 0 || committed == requestedRows)
                await File.WriteAllTextAsync(Path.Combine(root, "n6-progress.json"), JsonSerializer.Serialize(new
                {
                    schema = "LogForesight.N6StorageProgress", schemaVersion = 2,
                    runId = manifest.RunId, manifestSha256 = manifest.ManifestSha256,
                    committedRows = committed, requestedRows, elapsedSeconds = accumulatedActiveSeconds + run.Elapsed.TotalSeconds,
                    currentProcessActiveSeconds = run.Elapsed.TotalSeconds, initialProcessCheckpointedActiveSeconds = initialElapsedSeconds,
                    resumeWallSeconds, resumeCount, interrupted = false,
                    dbHighWaterBytes = highWaterBytes, processPeakWorkingSetBytes = peakWorkingSetBytes,
                    sqliteDbWalShmHighWaterBytes = storageHighWaterBytes, outputVolumeMinimumFreeBytes = minimumFreeBytes,
                    provider, syntheticStorageOnly = true, fullCapacityAccepted = false
                }, JsonOptionsHolder.Options));
            if (controlledStopAfterBatches > 0 && batchSamplesMs.Count >= controlledStopAfterBatches && committed < requestedRows)
            {
                Console.Error.WriteLine($"INTERRUPTED: controlled stop after {batchSamplesMs.Count} committed batches; resume with --resume --root <same-root> --provider sqlite --rows {requestedRows}.");
                return 75;
            }
        }
    }
    run.Stop();
    using (var ctx = backend.CreateContext())
    {
        var actualRows = ctx.PrtgValues.LongCount();
        if (actualRows != requestedRows) throw new InvalidOperationException($"Persisted row count mismatch: {actualRows} != {requestedRows}.");
        var typedRows = ctx.PrtgValues.LongCount(v => v.TrustVersion == 1 && v.TrustedProof != null);
        if (typedRows != requestedRows) throw new InvalidOperationException($"Typed proof count mismatch: {typedRows} != {requestedRows}.");
    }
    if (provider == "sqlite")
    {
        highWaterBytes = Math.Max(highWaterBytes, FileBytes(databasePath));
        storageHighWaterBytes = Math.Max(storageHighWaterBytes,
            FileBytes(databasePath) + FileBytes(databasePath + "-wal") + FileBytes(databasePath + "-shm"));
        minimumFreeBytes = Math.Min(minimumFreeBytes, outputVolume.AvailableFreeSpace);
    }
    var requiredFullShape = requestedRows == FixedRows;
    var hasCompleteElapsed = manifest.OriginalProvenance != "legacy/audited-adopted" ||
        (manifest.OriginalProgressRows == manifest.RequestedRows && manifest.OriginalAuditedRows == manifest.RequestedRows);
    var totalActiveSeconds = accumulatedActiveSeconds + run.Elapsed.TotalSeconds;
    var totalElapsedSeconds = hasCompleteElapsed
        ? (manifest.OriginalProvenance == "legacy/audited-adopted" ? manifest.OriginalProgressElapsedSeconds : 0) + totalActiveSeconds
        : (double?)null;
    var summary = new
    {
        schema = "LogForesight.N6GeneratedHighWaterResult", schemaVersion = 2,
        input = new { hosts = HostCount, sensors = SensorCount, days = 180, rowsPerSensor = HoursPerSensor, requestedRows, rowOrder = "sensor-major, one row per sensor-hour", values = "deterministic synthetic 4-slot typed proof per hour via production MergeSampledValues; no native source authority" },
        provider, databasePath, devicesWritten, sensorsWritten, hostMapsWritten = mapsWritten,
        rowsCommitted = committed, persistedRows = requestedRows, elapsedSeconds = totalElapsedSeconds,
        initialProcessCheckpointedActiveSeconds = initialElapsedSeconds,
        currentProcessActiveSeconds = run.Elapsed.TotalSeconds,
        accumulatedOwnedActiveSeconds = totalActiveSeconds,
        totalElapsedSecondsComplete = hasCompleteElapsed,
        elapsedBasis = "sum of owned-run active stopwatch durations persisted at batch checkpoints plus final process; legacy progress time is included only when its final checkpoint was fully audited",
        resumeWallSeconds = resumeCount > 0 ? resumeWallSeconds + run.Elapsed.TotalSeconds : 0, resumeCount,
        interruptedBetweenProcesses = resumeCount > 0,
        runId = manifest.RunId, manifestSha256 = manifest.ManifestSha256,
        originalProvenance = manifest.OriginalProvenance,
        originalSourceFingerprint = manifest.OriginalSourceFingerprint,
        originalProgressSha256 = manifest.OriginalProgressSha256,
        originalLogSha256 = manifest.OriginalLogSha256,
        originalProgressRows = manifest.OriginalProgressRows,
        originalAuditedRows = manifest.OriginalAuditedRows,
        originalUnloggedTailRows = manifest.OriginalUnloggedTailRows,
        originalElapsedSecondsComplete = manifest.OriginalProgressRows == 0 ||
            manifest.OriginalProgressRows == manifest.RequestedRows && manifest.OriginalAuditedRows == manifest.RequestedRows,
        originalProgressElapsedSeconds = manifest.OriginalProgressElapsedSeconds,
        originalProgressSampledDbBytes = manifest.OriginalProgressSampledDbBytes,
        databaseBytesAtAdoption = manifest.DatabaseBytesAtAdoption,
        walBytesAtAdoption = manifest.WalBytesAtAdoption,
        shmBytesAtAdoption = manifest.ShmBytesAtAdoption,
        rowsPerSecond = totalElapsedSeconds.HasValue ? committed / Math.Max(totalElapsedSeconds.Value, 0.001) : (double?)null,
        currentProcessRowsPerSecond = currentProcessRows / Math.Max(run.Elapsed.TotalSeconds, 0.001),
        batchP95Milliseconds = Percentile(batchSamplesMs, 0.95), batchCount = batchSamplesMs.Count,
        sqliteDatabaseBytes = highWaterBytes, processPeakWorkingSetBytes = peakWorkingSetBytes,
        sqliteDbWalShmHighWaterBytes = storageHighWaterBytes,
        outputVolumeMinimumFreeBytes = minimumFreeBytes,
        storageSampling = "after each committed batch; SQLite DB/WAL/SHM only, not unsampled peaks or SQL Server server-side files",
        fullFixedShape = requiredFullShape,
        sqlServerLoopbackOnly = provider == "sqlite" || IsLoopbackDataSource(new SqlConnectionStringBuilder(connectionString).DataSource),
        trustVersionOneRows = requestedRows, typedSlotsPerRow = 4,
        typedProofUtf8Bytes = new { scope = "rows generated in current process only", rows = currentProcessRows,
            total = proofBytesTotal, min = currentProcessRows == 0 ? (int?)null : proofBytesMin,
            max = currentProcessRows == 0 ? (int?)null : proofBytesMax,
            average = currentProcessRows == 0 ? (double?)null : proofBytesTotal / (double)currentProcessRows },
        fullWorkloadAccepted = false, capacityAccepted = false, retentionAccepted = false,
        recoveryAccepted = false, wholeRoundAccepted = false,
        caveat = "Direct generated EF store/schema high-water with current four-slot typed proof only. No joint NetIQ/AI/PRTG/user mixed load, backup/restore, journal/DB pause recovery, upgrade/rollback, N6 p95/headroom decision, or native-source claim is made. This synthetic shape cannot replace the accepted M2 estimate or establish a pass until its per-row payload is reconciled against the frozen 100k measurement."
    };
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(summary, JsonOptionsHolder.Options));
    File.Delete(checkpointPath);
    Console.WriteLine($"result={resultPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}. Check input, provider availability and dedicated output path; connection details are intentionally omitted.");
    return 2;
}

static PrtgValueRow CreateTypedValue(long sensorId, DateTime wallHour, int sensorIndex, int hourIndex, DateTime createdAt)
{
    wallHour = DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified);
    var utcHour = DateTime.SpecifyKind(wallHour, DateTimeKind.Utc);
    var value = 10 + sensorIndex % 80 + hourIndex % 24 / 10d;
    var slots = Enumerable.Range(0, 4).Select(slot =>
    {
        var measuredAt = utcHour.AddMinutes(slot * 15);
        var physical = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sensorId}|{wallHour:O}|{slot}")))[..32];
        return new PrtgTrustedSampleSlot(slot, physical, measuredAt, measuredAt.AddSeconds(10), value);
    }).ToArray();
    var effective = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);
    var proof = PrtgTrustedSampleProof.From(new PrtgTrustedSample(sensorId, value,
        "capacity-synthetic-source-v1", $"resource-{sensorId}", $"channel-{sensorId}", "epoch-1",
        "typed-semantic-v1", "capacity-strategy-v1", 15, effective, slots[0].MeasuredAt,
        slots[0].ReceivedAt, TimeSpan.FromMinutes(15), PrtgTrustedSampleQuality.Good,
        slots[0].PhysicalIdHash, "UTC", "UTC"), slots);
    return new PrtgValueRow
    {
        SensorObjid = sensorId, PeriodStart = wallHour,
        AvgValue = slots.Average(s => s.Value), MinValue = slots.Min(s => s.Value), MaxValue = slots.Max(s => s.Value),
        Coverage = 100, Quality = PrtgDataQuality.Sampled, CreatedAt = createdAt,
        TrustVersion = 1, TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
    };
}
static double Percentile(IReadOnlyList<double> values, double p)
{
    if (values.Count == 0) return 0;
    var ordered = values.Order().ToArray();
    return ordered[Math.Clamp((int)Math.Ceiling(p * ordered.Length) - 1, 0, ordered.Length - 1)];
}
static bool IsLoopbackDataSource(string value)
{
    // Numeric TCP avoids DNS and named SQL aliases that could redirect a local
    // looking name to a production server. LocalDB uses a local named instance.
    var matches = System.Text.RegularExpressions.Regex.IsMatch(value,
        @"^(?:\(localdb\)\\[A-Za-z0-9_-]{1,128}|tcp:(?:127\.0\.0\.1|\[::1\]),[0-9]{1,5})$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    if (!matches || value.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return matches;
    return int.TryParse(value[(value.LastIndexOf(',') + 1)..], out var port) && port is >= 1 and <= 65535;
}
static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
static string GetSourceFingerprint()
{
    static string HashAssembly(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    return HashText($"{HashAssembly(Assembly.GetExecutingAssembly())}|{HashAssembly(typeof(StorageBackend).Assembly)}");
}
static RunManifest CreateManifest(string root, string databasePath, string provider, long rows, string sourceFingerprint)
{
    var value = new RunManifest
    {
        Schema = "LogForesight.N6StorageRunManifest", SchemaVersion = 1,
        RunId = Guid.NewGuid().ToString("N"), RootPath = Path.GetFullPath(root),
        DatabasePath = Path.GetFullPath(databasePath), Provider = provider,
        Hosts = HostCount, Sensors = SensorCount, Days = 180, RequestedRows = rows,
        RowOrder = "sensor-major, one row per sensor-hour", TypedSlotsPerRow = 4,
        SourceFingerprint = sourceFingerprint, OriginalProvenance = "generated-with-current-binary",
        CreatedUtc = DateTimeOffset.UtcNow, RunDate = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        ManifestSha256 = string.Empty
    };
    value.ManifestSha256 = HashManifest(value);
    return value;
}
static RunManifest ReadAndValidateManifest(string path, string root, string provider, long rows, string sourceFingerprint)
{
    var value = JsonSerializer.Deserialize<RunManifest>(File.ReadAllBytes(path), JsonOptionsHolder.Options)
        ?? throw new InvalidDataException("Owned run manifest is empty.");
    if (value.Schema != "LogForesight.N6StorageRunManifest" || value.SchemaVersion != 1 ||
        value.ManifestSha256 != HashManifest(value)) throw new InvalidDataException("Owned run manifest schema or checksum is invalid.");
    if (value.RootPath != Path.GetFullPath(root) || value.Provider != provider || value.RequestedRows != rows ||
        value.Hosts != HostCount || value.Sensors != SensorCount || value.Days != 180 || value.TypedSlotsPerRow != 4 ||
        value.RowOrder != "sensor-major, one row per sensor-hour")
        throw new InvalidDataException("Resume scope differs from the immutable owned run manifest.");
    if (value.SourceFingerprint != sourceFingerprint)
        throw new InvalidDataException("Resume executable/core fingerprint differs from the original generator build.");
    if (!IsSameOrWithin(Path.GetFullPath(value.DatabasePath), Path.GetFullPath(root)) || !File.Exists(value.DatabasePath))
        throw new InvalidDataException("Manifest database path is missing or outside the owned run root.");
    if ((File.GetAttributes(value.DatabasePath) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Manifest database must be a regular file, not a reparse point.");
    return value;
}
static RunCheckpoint ReadAndValidateCheckpoint(string path, RunManifest manifest)
{
    var value = JsonSerializer.Deserialize<RunCheckpoint>(File.ReadAllBytes(path), JsonOptionsHolder.Options)
        ?? throw new InvalidDataException("Owned run checkpoint is empty.");
    if (value.Schema != "LogForesight.N6StorageCheckpoint" || value.SchemaVersion != 1 ||
        value.RunId != manifest.RunId || value.ManifestSha256 != manifest.ManifestSha256 ||
        value.CheckpointSha256 != HashCheckpoint(value))
        throw new InvalidDataException("Checkpoint schema, run identity, manifest, or checksum is invalid.");
    if (value.CommittedRows < 0 || value.CommittedRows > manifest.RequestedRows || value.CommittedRows % BatchSize != 0 && value.CommittedRows != manifest.RequestedRows)
        throw new InvalidDataException("Checkpoint row boundary is outside the fixed committed-batch sequence.");
    return value;
}
static string HashManifest(RunManifest value) => HashText(JsonSerializer.Serialize(new
{
    value.Schema, value.SchemaVersion, value.RunId, value.RootPath, value.DatabasePath, value.Provider,
    value.Hosts, value.Sensors, value.Days, value.RequestedRows, value.RowOrder, value.TypedSlotsPerRow,
    value.SourceFingerprint, value.OriginalProvenance, value.CreatedUtc, value.RunDate,
    value.OriginalSourceFingerprint, value.OriginalProgressSha256, value.OriginalLogSha256,
    value.OriginalProgressRows, value.OriginalAuditedRows, value.OriginalUnloggedTailRows,
    value.OriginalProgressElapsedSeconds, value.OriginalProgressSampledDbBytes,
    value.DatabaseBytesAtAdoption, value.WalBytesAtAdoption, value.ShmBytesAtAdoption
}, JsonOptionsHolder.Options));
static string HashCheckpoint(RunCheckpoint value) => HashText(JsonSerializer.Serialize(new
{
    value.Schema, value.SchemaVersion, value.RunId, value.ManifestSha256, value.CommittedRows,
    value.CurrentProcessActiveSeconds, value.InitialElapsedSeconds, value.AccumulatedActiveSeconds, value.ResumeWallSeconds,
    value.ResumeCount, value.LastProcessStartedUtc
}, JsonOptionsHolder.Options));
static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}
static void WriteJsonAtomic<T>(string path, T value)
{
    var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        JsonSerializer.Serialize(stream, value, JsonOptionsHolder.Options);
        stream.Flush(flushToDisk: true);
    }
    File.Move(temporary, path, overwrite: true);
}
static void InitializeOwnedDatabase(string path, RunManifest manifest)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE lf_n6_generation_control (run_id TEXT NOT NULL PRIMARY KEY, manifest_sha256 TEXT NOT NULL, committed_rows INTEGER NOT NULL); INSERT INTO lf_n6_generation_control (run_id, manifest_sha256, committed_rows) VALUES ($run, $hash, 0);";
    command.Parameters.AddWithValue("$run", manifest.RunId);
    command.Parameters.AddWithValue("$hash", manifest.ManifestSha256);
    command.ExecuteNonQuery();
}
static void UpdateOwnedDatabaseCheckpoint(string path, RunManifest manifest, long committed)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE lf_n6_generation_control SET committed_rows = $rows WHERE run_id = $run AND manifest_sha256 = $hash AND committed_rows <= $rows;";
    command.Parameters.AddWithValue("$rows", committed);
    command.Parameters.AddWithValue("$run", manifest.RunId);
    command.Parameters.AddWithValue("$hash", manifest.ManifestSha256);
    if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Owned database checkpoint identity or monotonic row count is invalid.");
}
static long ValidateOwnedDatabase(string path, RunManifest manifest, long committed)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var connection = new SqliteConnection(builder.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    long markerRows;
    command.CommandText = "SELECT run_id, manifest_sha256, committed_rows FROM lf_n6_generation_control;";
    using (var reader = command.ExecuteReader())
    {
        if (!reader.Read() || reader.GetString(0) != manifest.RunId || reader.GetString(1) != manifest.ManifestSha256)
            throw new InvalidDataException("Database ownership marker differs from the manifest/checkpoint or contains multiple run identities.");
        markerRows = reader.GetInt64(2);
        if (reader.Read()) throw new InvalidDataException("Database contains multiple run ownership markers.");
    }
    command.CommandText = "SELECT (SELECT COUNT(*) FROM lf_prtg_devices), (SELECT COUNT(*) FROM lf_prtg_sensors), (SELECT COUNT(*) FROM lf_prtg_host_map), (SELECT COUNT(*) FROM lf_prtg_values), (SELECT COUNT(*) FROM lf_prtg_sensors WHERE objid < 1000001 OR objid >= 1015001), (SELECT COUNT(*) FROM lf_prtg_devices WHERE objid < 1 OR objid > 3000);";
    using var shape = command.ExecuteReader();
    if (!shape.Read() || shape.GetInt64(0) != HostCount || shape.GetInt64(1) != SensorCount || shape.GetInt64(2) != HostCount ||
        shape.GetInt64(3) < committed || shape.GetInt64(3) > committed + BatchSize || shape.GetInt64(4) != 0 || shape.GetInt64(5) != 0)
        throw new InvalidDataException("Database shape or persisted row count differs from the fixed owned checkpoint.");
    var actualRows = shape.GetInt64(3);
    if (actualRows > manifest.RequestedRows || markerRows < committed || markerRows > actualRows || actualRows - committed > BatchSize || markerRows - committed > BatchSize)
        throw new InvalidDataException("Database and checkpoint differ by more than one verifiable committed batch.");
    return actualRows;
}
static void AuditOwnedRowRange(string path, RunManifest manifest, long start, long end)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var connection = new SqliteConnection(builder.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT id, sensor_objid, period_start, avg_value, min_value, max_value, coverage, quality, trust_version, trusted_proof FROM lf_prtg_values WHERE id > $start ORDER BY id;";
    command.Parameters.AddWithValue("$start", start);
    using var reader = command.ExecuteReader();
    var firstPeriod = DateTime.ParseExact(manifest.RunDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(-180);
    for (var index = start; index < end; index++)
    {
        if (!reader.Read()) throw new InvalidDataException("Committed tail row is missing during interruption recovery.");
        var sensorIndex = checked((int)(index / HoursPerSensor));
        var hour = checked((int)(index % HoursPerSensor));
        var expected = CreateTypedValue(1_000_001L + sensorIndex, firstPeriod.AddHours(hour), sensorIndex, hour, DateTime.MinValue);
        if (reader.GetInt64(0) != index + 1 || reader.GetInt64(1) != expected.SensorObjid ||
            DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture) != expected.PeriodStart ||
            Math.Abs(reader.GetDouble(3) - expected.AvgValue!.Value) > 1e-9 ||
            Math.Abs(reader.GetDouble(4) - expected.MinValue!.Value) > 1e-9 ||
            Math.Abs(reader.GetDouble(5) - expected.MaxValue!.Value) > 1e-9 || reader.GetDouble(6) != 100 ||
            !string.Equals(reader.GetString(7), PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt32(8) != 1 || !string.Equals(reader.GetString(9), expected.TrustedProof, StringComparison.Ordinal))
            throw new InvalidDataException($"Database tail is not the deterministic next row at committed index {index}.");
    }
    if (reader.Read()) throw new InvalidDataException("Database contains rows beyond its reported interrupted batch.");
}
static void AdoptLegacyRun(string root, long requestedRows, string sourceRootArgument, string sourceLogArgument, string currentFingerprint)
{
    var sourceRoot = Path.GetFullPath(sourceRootArgument);
    var logPath = Path.GetFullPath(sourceLogArgument);
    var manifestPath = Path.Combine(root, "n6-run-manifest.json");
    var checkpointPath = Path.Combine(root, "n6-checkpoint.json");
    var resultPath = Path.Combine(root, "n6-high-water-result.json");
    var progressPath = Path.Combine(root, "n6-progress.json");
    if (File.Exists(manifestPath) || File.Exists(checkpointPath) || File.Exists(resultPath) || !File.Exists(progressPath))
        throw new InvalidDataException("Legacy adoption requires only the unfinished old progress receipt and one database; prior manifests/results are refused.");
    var frozenFingerprint = ValidateFrozenSourceTree(sourceRoot);
    var sourceLog = File.ReadAllText(logPath, Encoding.UTF8);
    var lastProgress = sourceLog.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).LastOrDefault(line => line.StartsWith("rows=", StringComparison.Ordinal))
        ?? throw new InvalidDataException("Frozen source log contains no row progress record.");
    using var progress = JsonDocument.Parse(File.ReadAllBytes(progressPath));
    var progressRoot = progress.RootElement;
    var progressRows = progressRoot.GetProperty("committedRows").GetInt64();
    var legacyRequestedRows = progressRoot.GetProperty("requestedRows").GetInt64();
    var elapsed = progressRoot.GetProperty("elapsedSeconds").GetDouble();
    if (progressRoot.GetProperty("provider").GetString() != "sqlite" || legacyRequestedRows != requestedRows ||
        progressRows <= 0 || progressRows > requestedRows || progressRows % 100_000 != 0 && progressRows != requestedRows || elapsed <= 0 ||
        !lastProgress.StartsWith($"rows={progressRows}/{requestedRows} ", StringComparison.Ordinal))
        throw new InvalidDataException("Legacy progress, full-shape scope, or frozen log high-water row identity differs from the owned run.");
    var parsedProgress = ParseLegacyProgressLine(lastProgress);
    if (Math.Abs(parsedProgress.ElapsedSeconds - elapsed) > 0.0500001 ||
        parsedProgress.DbBytes != progressRoot.GetProperty("dbHighWaterBytes").GetInt64())
        throw new InvalidDataException("Legacy JSON progress and rounded log sample disagree.");
    if (requestedRows == FixedRows && progressRows == 35_800_000 &&
        HashFile(logPath) != "7561FB02DFEA6A5C0B37F4D345F8AE11C558CBA24D13DCEC5CF39637B8E6400B")
        throw new InvalidDataException("Full interrupted-run log fingerprint differs from the approved 35.8M-row run.");
    if (requestedRows == FixedRows && progressRows == 35_800_000 &&
        (parsedProgress.ElapsedSeconds != 27_760.2 || progressRoot.GetProperty("dbHighWaterBytes").GetInt64() != 40_784_166_912L))
        throw new InvalidDataException("Legacy sampled elapsed or DB high-water values differ from the approved frozen run log.");
    var dbFiles = Directory.GetFiles(root, "n6-sqlite-*.db", SearchOption.TopDirectoryOnly);
    if (dbFiles.Length != 1) throw new InvalidDataException("Legacy adoption requires exactly one old generator SQLite database.");
    var databasePath = Path.GetFullPath(dbFiles[0]);
    if ((File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Legacy SQLite database must be a regular file, not a reparse point.");
    var databaseBytesAtAdoption = new FileInfo(databasePath).Length;
    var walBytesAtAdoption = FileBytes(databasePath + "-wal");
    var shmBytesAtAdoption = FileBytes(databasePath + "-shm");
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Path.GetFullPath(progressPath), databasePath, databasePath + "-wal", databasePath + "-shm"
    };
    if (Directory.GetFiles(root).Any(path => !allowed.Contains(Path.GetFullPath(path))) || Directory.GetDirectories(root).Length != 0)
        throw new InvalidDataException("Legacy output directory contains unexpected files or subdirectories.");
    var runDate = AuditLegacyDatabase(databasePath, progressRows, requestedRows, out var auditedRows);
    var originalProgressSha = HashFile(progressPath);
    var originalLogSha = HashFile(logPath);
    var manifest = CreateManifest(root, databasePath, "sqlite", requestedRows, currentFingerprint);
    manifest.OriginalProvenance = "legacy/audited-adopted";
    manifest.RunDate = runDate;
    manifest.OriginalSourceFingerprint = frozenFingerprint;
    manifest.OriginalProgressSha256 = originalProgressSha;
    manifest.OriginalLogSha256 = originalLogSha;
    manifest.OriginalProgressRows = progressRows;
    manifest.OriginalAuditedRows = auditedRows;
    manifest.OriginalUnloggedTailRows = auditedRows - progressRows;
    manifest.OriginalProgressElapsedSeconds = elapsed;
    manifest.OriginalProgressSampledDbBytes = progressRoot.GetProperty("dbHighWaterBytes").GetInt64();
    manifest.DatabaseBytesAtAdoption = databaseBytesAtAdoption;
    manifest.WalBytesAtAdoption = walBytesAtAdoption;
    manifest.ShmBytesAtAdoption = shmBytesAtAdoption;
    manifest.ManifestSha256 = HashManifest(manifest);
    // Mutations begin only after the read-only full-prefix audit succeeds.
    InitializeLegacyOwnedDatabase(databasePath, manifest, auditedRows);
    var checkpoint = new RunCheckpoint
    {
        Schema = "LogForesight.N6StorageCheckpoint", SchemaVersion = 1,
        RunId = manifest.RunId, ManifestSha256 = manifest.ManifestSha256,
        CommittedRows = auditedRows, CurrentProcessActiveSeconds = 0, InitialElapsedSeconds = 0, AccumulatedActiveSeconds = 0,
        ResumeWallSeconds = 0, ResumeCount = 0, LastProcessStartedUtc = DateTimeOffset.UtcNow,
        CheckpointSha256 = string.Empty
    };
    checkpoint.CheckpointSha256 = HashCheckpoint(checkpoint);
    WriteJsonAtomic(manifestPath, manifest);
    WriteJsonAtomic(checkpointPath, checkpoint);
    var evidenceCopy = Path.Combine(root, "n6-legacy-progress-audited.json");
    File.Copy(progressPath, evidenceCopy, overwrite: false);
    Console.WriteLine($"ADOPTED: sampledRows={progressRows} auditedRows={auditedRows}/{requestedRows} unloggedTailRows={auditedRows - progressRows} provenance=legacy/audited-adopted sourceFingerprint={frozenFingerprint}; database changed only after complete read-only deterministic-prefix audit.");
}
static (double ElapsedSeconds, long DbBytes) ParseLegacyProgressLine(string line)
{
    var match = System.Text.RegularExpressions.Regex.Match(line,
        @"^rows=(?<rows>[0-9]+)/[0-9]+ elapsed_s=(?<elapsed>[0-9]+\.[0-9]) db_bytes=(?<bytes>[0-9]+) process_ws_peak_bytes=[0-9]+$");
    if (!match.Success || !double.TryParse(match.Groups["elapsed"].Value, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var elapsed) ||
        !long.TryParse(match.Groups["bytes"].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var bytes))
        throw new InvalidDataException("Legacy log row sample is not in the frozen canonical format.");
    return (elapsed, bytes);
}
static string ValidateFrozenSourceTree(string root)
{
    var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["scripts/prtg-workload/storage-generator/Program.cs"] = "5D345FAD8037D42A8AB9880BF5A07C8226743FB31EC835145DF90525EF9BC1CD",
        ["scripts/prtg-workload/storage-generator/PrtgStorageGenerator.csproj"] = "F6C37C9FD62AEA3EB1D3876A6C093306F551EF9155283FDF7EE497F833E789C8",
        ["LogForesight.Core/Persistence/Sql/EfPrtgStore.cs"] = "CE1732F9B4BCE518AD5CD5BCE260377CA30B4BF80F758A8AB8F7999BA778D4C3",
        ["LogForesight.Core/Persistence/Sql/LfDbContext.cs"] = "0A5D9FC340E555BD621152DC9C14E025CEE649E91A90E72A98AB94D8EA6BFD5B",
        ["LogForesight.Core/Persistence/Sql/SchemaUpgrader.cs"] = "EB65CC244ABE1BD0DFE42F797C8F7475BB05AD1B4389C536C6E5FFA4980AC99A"
    };
    foreach (var pair in expected)
    {
        var file = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file) || HashFile(file) != pair.Value) throw new InvalidDataException("Frozen source identity does not match the approved interrupted-run source: " + pair.Key);
        if (pair.Key.StartsWith("LogForesight.Core/", StringComparison.Ordinal))
        {
            if (HashFile(Path.Combine(FindRepositoryRoot(), pair.Key.Replace('/', Path.DirectorySeparatorChar))) != pair.Value)
                throw new InvalidDataException("Current storage semantics differ from the frozen interrupted-run source: " + pair.Key);
        }
    }
    return HashText(string.Join("|", expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value)));
}
static string AuditLegacyDatabase(string path, long progressRows, long requestedRows, out long auditedRows)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var connection = new SqliteConnection(builder.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT (SELECT COUNT(*) FROM lf_prtg_devices), (SELECT MIN(objid) FROM lf_prtg_devices), (SELECT MAX(objid) FROM lf_prtg_devices), (SELECT COUNT(*) FROM lf_prtg_sensors), (SELECT MIN(objid) FROM lf_prtg_sensors), (SELECT MAX(objid) FROM lf_prtg_sensors), (SELECT COUNT(*) FROM lf_prtg_host_map), (SELECT COUNT(*) FROM lf_prtg_values), (SELECT MIN(id) FROM lf_prtg_values), (SELECT MAX(id) FROM lf_prtg_values), (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='lf_n6_generation_control');";
    using (var shape = command.ExecuteReader())
    {
        if (!shape.Read() || shape.GetInt64(0) != HostCount || shape.GetInt64(1) != 1 || shape.GetInt64(2) != HostCount ||
            shape.GetInt64(3) != SensorCount || shape.GetInt64(4) != 1_000_001 || shape.GetInt64(5) != 1_015_000 ||
            shape.GetInt64(6) != HostCount || shape.GetInt64(8) != 1 || shape.GetInt64(9) != shape.GetInt64(7) || shape.GetInt64(10) != 0)
            throw new InvalidDataException("Legacy SQLite fixed host/sensor/map/value shape failed the read-only audit.");
        auditedRows = shape.GetInt64(7);
    }
    if (auditedRows < progressRows || auditedRows > requestedRows || auditedRows - progressRows > 99_999 ||
        auditedRows % BatchSize != 0 && auditedRows != requestedRows)
        throw new InvalidDataException("Legacy database prefix is outside the frozen 100,000-row progress sampling interval or commit boundary.");
    AuditLegacyStructureRows(connection);
    command.CommandText = "SELECT MIN(map_date) FROM lf_prtg_host_map;";
    var mapDate = DateTime.Parse(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture).Date;
    command.CommandText = "SELECT id, sensor_objid, period_start, avg_value, min_value, max_value, coverage, quality, trust_version, trusted_proof FROM lf_prtg_values ORDER BY id;";
    using var reader = command.ExecuteReader();
    var firstPeriod = mapDate.AddDays(-180).Date;
    long index = 0;
    while (reader.Read())
    {
        if (index >= auditedRows) throw new InvalidDataException("Legacy database contains values beyond its audited prefix.");
        var sensorIndex = checked((int)(index / HoursPerSensor));
        var hour = checked((int)(index % HoursPerSensor));
        var sensorId = 1_000_001L + sensorIndex;
        var wallHour = firstPeriod.AddHours(hour);
        var actualPeriod = DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
        var expected = CreateTypedValue(sensorId, wallHour, sensorIndex, hour, DateTime.MinValue);
        if (reader.GetInt64(0) != index + 1 || reader.GetInt64(1) != sensorId || actualPeriod != wallHour ||
            Math.Abs(reader.GetDouble(3) - expected.AvgValue!.Value) > 1e-9 ||
            Math.Abs(reader.GetDouble(4) - expected.MinValue!.Value) > 1e-9 ||
            Math.Abs(reader.GetDouble(5) - expected.MaxValue!.Value) > 1e-9 ||
            reader.GetDouble(6) != 100 || !string.Equals(reader.GetString(7), PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt32(8) != 1 || !string.Equals(reader.GetString(9), expected.TrustedProof, StringComparison.Ordinal))
            throw new InvalidDataException($"Legacy database deterministic prefix differs at row index {index}.");
        index++;
    }
    if (index != auditedRows) throw new InvalidDataException($"Legacy prefix has {index} rows; expected audited row count {auditedRows}.");
    return mapDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
static void AuditLegacyStructureRows(SqliteConnection connection)
{
    using var command = connection.CreateCommand();
    DateTime? seedTimestamp = null;
    command.CommandText = "SELECT objid, name, group_path, ip, tags, status, dependency_objid, paused, synced_at, created_at FROM lf_prtg_devices ORDER BY objid;";
    using (var reader = command.ExecuteReader())
    {
        for (var id = 1; id <= HostCount; id++)
        {
            if (!reader.Read()) throw new InvalidDataException("Legacy device sequence is truncated.");
            var expectedIp = $"10.{(id - 1) / 65_536}.{((id - 1) / 256) % 256}.{((id - 1) % 254) + 1}";
            var syncedAt = DateTime.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
            var createdAt = DateTime.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);
            seedTimestamp ??= createdAt;
            if (reader.GetInt64(0) != id || reader.GetString(1) != $"capacity-host-{id:D4}" ||
                reader.GetString(2) != $"/capacity/region-{(id - 1) % 10:D2}" || reader.GetString(3) != expectedIp ||
                reader.GetString(4) != "acceptance.synthetic" || reader.GetString(5) != PrtgSensorStatuses.Up ||
                !reader.IsDBNull(6) || reader.GetInt64(7) != 0 || syncedAt != createdAt || createdAt != seedTimestamp)
                throw new InvalidDataException($"Legacy device mapping differs at device {id}.");
        }
        if (reader.Read()) throw new InvalidDataException("Legacy database contains extra devices.");
    }

    command.CommandText = "SELECT objid, device_objid, name, sensor_type, tags, unit, status, thresholds_json, dependency_objid, paused, category, category_source, synced_at, created_at FROM lf_prtg_sensors ORDER BY objid;";
    using (var reader = command.ExecuteReader())
    {
        for (var index = 0; index < SensorCount; index++)
        {
            if (!reader.Read()) throw new InvalidDataException("Legacy sensor sequence is truncated.");
            var sensorId = 1_000_001L + index;
            var syncedAt = DateTime.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture);
            var createdAt = DateTime.Parse(reader.GetString(13), System.Globalization.CultureInfo.InvariantCulture);
            if (reader.GetInt64(0) != sensorId || reader.GetInt64(1) != 1 + index / 5 ||
                reader.GetString(2) != $"capacity-sensor-{index + 1:D5}" ||
                reader.GetString(3) != "synthetic-hourly-aggregate" || !reader.IsDBNull(4) ||
                reader.GetString(5) != "percent" || reader.GetString(6) != PrtgSensorStatuses.Up ||
                !reader.IsDBNull(7) || !reader.IsDBNull(8) || reader.GetInt64(9) != 0 ||
                reader.GetString(10) != "acceptance" || reader.GetString(11) != "auto" ||
                syncedAt != createdAt || createdAt != seedTimestamp)
                throw new InvalidDataException($"Legacy sensor mapping differs at sensor index {index}.");
        }
        if (reader.Read()) throw new InvalidDataException("Legacy database contains extra sensors.");
    }

    command.CommandText = "SELECT map_date, device_objid, ip, host_id, host_name, map_status, note, created_at FROM lf_prtg_host_map ORDER BY device_objid;";
    using (var reader = command.ExecuteReader())
    {
        DateTime? expectedMapDate = null;
        for (var id = 1; id <= HostCount; id++)
        {
            if (!reader.Read()) throw new InvalidDataException("Legacy host-map sequence is truncated.");
            var mapDate = DateTime.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture).Date;
            expectedMapDate ??= mapDate;
            var expectedIp = $"10.{(id - 1) / 65_536}.{((id - 1) / 256) % 256}.{((id - 1) % 254) + 1}";
            var createdAt = DateTime.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture);
            if (mapDate != expectedMapDate.Value || reader.GetInt64(1) != id || reader.GetString(2) != expectedIp ||
                reader.GetInt64(3) != id || reader.GetString(4) != $"capacity-host-{id:D4}" ||
                reader.GetString(5) != "mapped" || !reader.IsDBNull(6) || createdAt != seedTimestamp)
                throw new InvalidDataException($"Legacy host-map identity differs at device {id}.");
        }
        if (reader.Read()) throw new InvalidDataException("Legacy database contains extra host maps.");
    }
}
static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, "LogForesight.Core")) && Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Could not locate source repository root for the storage semantics audit.");
}
static void InitializeLegacyOwnedDatabase(string path, RunManifest manifest, long committed)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE lf_n6_generation_control (run_id TEXT NOT NULL PRIMARY KEY, manifest_sha256 TEXT NOT NULL, committed_rows INTEGER NOT NULL); INSERT INTO lf_n6_generation_control (run_id, manifest_sha256, committed_rows) VALUES ($run, $hash, $rows);";
    command.Parameters.AddWithValue("$run", manifest.RunId);
    command.Parameters.AddWithValue("$hash", manifest.ManifestSha256);
    command.Parameters.AddWithValue("$rows", committed);
    command.ExecuteNonQuery();
}
static bool IsSameOrWithin(string path, string parent)
{
    var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var basePath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
}
static string Required(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
    ? value : throw new ArgumentException($"Missing --{key}.");
static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--resume" or "--adopt-legacy" or "--simulate-crash-after-db-commit")
        {
            var flag = args[i][2..];
            if (result.ContainsKey(flag)) throw new ArgumentException("Unknown or duplicate command argument.");
            result[flag] = "true";
            continue;
        }
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ArgumentException($"Expected --name value, got {args[i]}.");
        var key = args[i][2..];
        if (key is not ("mode" or "root" or "provider" or "rows" or "stop-after-batches" or "source-root" or "source-log") || result.ContainsKey(key))
            throw new ArgumentException("Unknown or duplicate command argument.");
        result[key] = args[++i];
    }
    return result;
}

static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

sealed class RunManifest
{
    public string Schema { get; set; } = ""; public int SchemaVersion { get; set; } public string RunId { get; set; } = "";
    public string RootPath { get; set; } = ""; public string DatabasePath { get; set; } = ""; public string Provider { get; set; } = "";
    public int Hosts { get; set; } public int Sensors { get; set; } public int Days { get; set; } public long RequestedRows { get; set; }
    public string RowOrder { get; set; } = ""; public int TypedSlotsPerRow { get; set; } public string SourceFingerprint { get; set; } = "";
    public string OriginalProvenance { get; set; } = ""; public DateTimeOffset CreatedUtc { get; set; } public string RunDate { get; set; } = ""; public string ManifestSha256 { get; set; } = "";
    public string OriginalSourceFingerprint { get; set; } = ""; public string OriginalProgressSha256 { get; set; } = ""; public string OriginalLogSha256 { get; set; } = "";
    public long OriginalProgressRows { get; set; } public long OriginalAuditedRows { get; set; } public long OriginalUnloggedTailRows { get; set; }
    public double OriginalProgressElapsedSeconds { get; set; } public long OriginalProgressSampledDbBytes { get; set; }
    public long DatabaseBytesAtAdoption { get; set; } public long WalBytesAtAdoption { get; set; } public long ShmBytesAtAdoption { get; set; }
}

sealed class RunCheckpoint
{
    public string Schema { get; set; } = ""; public int SchemaVersion { get; set; } public string RunId { get; set; } = "";
    public string ManifestSha256 { get; set; } = ""; public long CommittedRows { get; set; } public double CurrentProcessActiveSeconds { get; set; }
    public double InitialElapsedSeconds { get; set; } public double AccumulatedActiveSeconds { get; set; }
    public double ResumeWallSeconds { get; set; } public int ResumeCount { get; set; }
    public DateTimeOffset LastProcessStartedUtc { get; set; } public string CheckpointSha256 { get; set; } = "";
}

