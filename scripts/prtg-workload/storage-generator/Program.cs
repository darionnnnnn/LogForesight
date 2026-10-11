using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.SqlClient;

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
    if (Directory.Exists(root)) throw new ArgumentException("Use a new, dedicated output directory; existing directories are never reused.");
    var resultPath = Path.Combine(root, "n6-high-water-result.json");
    if (File.Exists(resultPath)) throw new InvalidOperationException($"Refusing to overwrite prior result: {resultPath}");

    if (mode != "n6") throw new ArgumentException("--mode must be n6.");
    var provider = argsMap.GetValueOrDefault("provider", "sqlite").ToLowerInvariant();
    if (provider is not ("sqlite" or "sqlserver")) throw new ArgumentException("--provider must be sqlite or sqlserver.");
    var requestedRows = long.Parse(Required(argsMap, "rows"), System.Globalization.CultureInfo.InvariantCulture);
    if (requestedRows is < 1 or > FixedRows) throw new ArgumentOutOfRangeException("rows", $"Must be 1..{FixedRows}; the fixed full shape is {FixedRows}.");
    Directory.CreateDirectory(root);
    var databasePath = Path.Combine(root, $"n6-{provider}-{Guid.NewGuid():N}.db");
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
        var requiredFree = estimatedDbBytes * 2 + (long)Math.Ceiling(volume.TotalSize * 0.25);
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
    var mapDate = DateTime.Today;
    var hostMaps = devices.Select(d => new PrtgHostMapRow
    {
        MapDate = mapDate, DeviceObjid = d.Objid, HostId = d.Objid, HostName = $"capacity-host-{d.Objid:D4}",
        Ip = d.Ip, MapStatus = "mapped", CreatedAt = now
    }).ToList();
    var devicesWritten = store.UpsertDevices(devices, now);
    var sensorsWritten = store.UpsertSensors(sensors, now);
    var mapsWritten = store.ReplaceHostMapForDate(mapDate, hostMaps);
    if (devicesWritten != HostCount || sensorsWritten != SensorCount || mapsWritten != HostCount)
        throw new InvalidOperationException($"Shape setup mismatch: devices={devicesWritten}, sensors={sensorsWritten}, hostMaps={mapsWritten}.");

    var firstPeriod = DateTime.Today.AddDays(-180).Date;
    var batch = new List<PrtgValueRow>(BatchSize);
    var batchSamplesMs = new List<double>();
    long committed = 0;
    long highWaterBytes = 0;
    long storageHighWaterBytes = 0;
    var outputVolume = new DriveInfo(Path.GetPathRoot(root)!);
    long minimumFreeBytes = outputVolume.AvailableFreeSpace;
    long proofBytesTotal = 0;
    var proofBytesMin = int.MaxValue;
    var proofBytesMax = 0;
    long peakWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64;
    var run = Stopwatch.StartNew();
    for (var sensorIndex = 0; sensorIndex < SensorCount && committed < requestedRows; sensorIndex++)
    {
        var sensorId = sensors[sensorIndex].Objid;
        for (var hour = 0; hour < HoursPerSensor && committed < requestedRows; hour++)
        {
            var typedValue = CreateTypedValue(sensorId, firstPeriod.AddHours(hour), sensorIndex, hour, now);
            var proofBytes = System.Text.Encoding.UTF8.GetByteCount(typedValue.TrustedProof!);
            proofBytesTotal += proofBytes;
            proofBytesMin = Math.Min(proofBytesMin, proofBytes);
            proofBytesMax = Math.Max(proofBytesMax, proofBytes);
            batch.Add(typedValue);
            if (batch.Count != BatchSize && committed + batch.Count != requestedRows) continue;
            var writeWatch = Stopwatch.StartNew();
            var written = store.MergeSampledValues(batch);
            writeWatch.Stop();
            if (written != batch.Count) throw new InvalidOperationException($"Batch write mismatch: expected {batch.Count}, got {written}.");
            batchSamplesMs.Add(writeWatch.Elapsed.TotalMilliseconds);
            committed += written;
            batch.Clear();
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
                await File.WriteAllTextAsync(Path.Combine(root, "n6-progress.json"), JsonSerializer.Serialize(new
                {
                    committedRows = committed, requestedRows, elapsedSeconds = run.Elapsed.TotalSeconds,
                    dbHighWaterBytes = highWaterBytes, processPeakWorkingSetBytes = peakWorkingSetBytes,
                    sqliteDbWalShmHighWaterBytes = storageHighWaterBytes, outputVolumeMinimumFreeBytes = minimumFreeBytes,
                    provider, syntheticStorageOnly = true, fullCapacityAccepted = false
                }, JsonOptionsHolder.Options));
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
    var summary = new
    {
        schema = "LogForesight.N6GeneratedHighWaterResult", schemaVersion = 2,
        input = new { hosts = HostCount, sensors = SensorCount, days = 180, rowsPerSensor = HoursPerSensor, requestedRows, rowOrder = "sensor-major, one row per sensor-hour", values = "deterministic synthetic 4-slot typed proof per hour via production MergeSampledValues; no native source authority" },
        provider, databasePath, devicesWritten, sensorsWritten, hostMapsWritten = mapsWritten,
        rowsCommitted = committed, persistedRows = requestedRows, elapsedSeconds = run.Elapsed.TotalSeconds,
        rowsPerSecond = requestedRows / Math.Max(run.Elapsed.TotalSeconds, 0.001),
        batchP95Milliseconds = Percentile(batchSamplesMs, 0.95), batchCount = batchSamplesMs.Count,
        sqliteDatabaseBytes = highWaterBytes, processPeakWorkingSetBytes = peakWorkingSetBytes,
        sqliteDbWalShmHighWaterBytes = storageHighWaterBytes,
        outputVolumeMinimumFreeBytes = minimumFreeBytes,
        storageSampling = "after each committed batch; SQLite DB/WAL/SHM only, not unsampled peaks or SQL Server server-side files",
        fullFixedShape = requiredFullShape,
        sqlServerLoopbackOnly = provider == "sqlite" || IsLoopbackDataSource(new SqlConnectionStringBuilder(connectionString).DataSource),
        trustVersionOneRows = requestedRows, typedSlotsPerRow = 4,
        typedProofUtf8Bytes = new { total = proofBytesTotal, min = proofBytesMin, max = proofBytesMax, average = proofBytesTotal / (double)requestedRows },
        fullWorkloadAccepted = false, capacityAccepted = false, retentionAccepted = false,
        recoveryAccepted = false, wholeRoundAccepted = false,
        caveat = "Direct generated EF store/schema high-water with current four-slot typed proof only. No joint NetIQ/AI/PRTG/user mixed load, backup/restore, journal/DB pause recovery, upgrade/rollback, N6 p95/headroom decision, or native-source claim is made. This synthetic shape cannot replace the accepted M2 estimate or establish a pass until its per-row payload is reconciled against the frozen 100k measurement."
    };
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(summary, JsonOptionsHolder.Options));
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
static string Required(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
    ? value : throw new ArgumentException($"Missing --{key}.");
static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ArgumentException($"Expected --name value, got {args[i]}.");
        var key = args[i][2..];
        if (key is not ("mode" or "root" or "provider" or "rows") || result.ContainsKey(key))
            throw new ArgumentException("Unknown or duplicate command argument.");
        result[key] = args[++i];
    }
    return result;
}

static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

