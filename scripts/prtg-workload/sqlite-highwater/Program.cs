using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

const long RequiredRows = 64_800_000;
const int RequiredDevices = 3_000;
const int RequiredSensors = 15_000;
const int BatchSize = 100;
const string SyntheticSource = "capacity-synthetic-source-v1";

try
{
    var argsMap = ParseArgs(args);
    if (argsMap.TryGetValue("verify-result", out var verifyResult))
    {
        if (!string.Equals(verifyResult, "true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--verify-result must be true.");
        return await VerifyReadOnlyResultAsync(Path.GetFullPath(Required(argsMap, "result")));
    }
    if (argsMap.TryGetValue("self-test", out var selfTest))
    {
        if (!string.Equals(selfTest, "true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--self-test must be true.");
        return await RunSafetySelfTestAsync(Path.GetFullPath(Required(argsMap, "output")));
    }
    var sourcePath = Path.GetFullPath(Required(argsMap, "source-db"));
    var sourceReportPath = Path.GetFullPath(Required(argsMap, "source-report"));
    var outputRoot = Path.GetFullPath(Required(argsMap, "output"));
    ValidateOwnedOutputRoot(outputRoot);
    ValidateSourceInputs(sourcePath, sourceReportPath);
    var sourceBefore = new FileInfo(sourcePath);
    var sourceLength = sourceBefore.Length;
    var sourceWriteUtc = sourceBefore.LastWriteTimeUtc;
    var sourceWal = ReadFileFence(sourcePath + "-wal");
    var sourceShm = ReadFileFence(sourcePath + "-shm");
    await using var sourceLease = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
        4096, FileOptions.RandomAccess);
    await using var sourceWalLease = sourceWal.Exists
        ? new FileStream(sourcePath + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess)
        : null;
    await using var sourceShmLease = sourceShm.Exists
        ? new FileStream(sourcePath + "-shm", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess)
        : null;
    await using var sourceFence = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    await sourceFence.OpenAsync();
    var sourceDataVersion = await ScalarLongAsync(sourceFence, "PRAGMA data_version");
    var sourceReportSha256 = await HashFileAsync(sourceReportPath);
    var sourceFacts = await ReadAndValidateSourceAsync(sourcePath, sourceReportPath);
    var outputDrive = new DriveInfo(Path.GetPathRoot(outputRoot)!);
    var estimatedOwnedBytes = checked((sourceLength + sourceWal.Length + sourceShm.Length) * 2 + 2L * 1024 * 1024 * 1024);
    var reserve = (long)Math.Ceiling(outputDrive.TotalSize * 0.25);
    if (outputDrive.AvailableFreeSpace < estimatedOwnedBytes + reserve)
        throw new InvalidOperationException("Insufficient free space for backup, restored working copy, 2 GiB round allowance, and 25% volume reserve.");

    Directory.CreateDirectory(outputRoot);
    await ClaimOwnedOutputAsync(outputRoot);
    var backupPath = Path.Combine(outputRoot, "source-backup.sqlite");
    var restoredPath = Path.Combine(outputRoot, "restored-working.sqlite");
    await BackupDatabaseAsync(sourcePath, backupPath);
    await BackupDatabaseAsync(backupPath, restoredPath);

    var settings = new StorageSettings
    {
        Type = "Sqlite",
        ConnectionString = $"Data Source={restoredPath};Pooling=False;Cache=Shared",
        SqliteWal = true
    };
    var backend = new StorageBackend(settings, outputRoot);
    var store = backend.PrtgStore();
    var baseline = await ReadDatabaseFactsAsync(restoredPath);
    ValidateFullShape(baseline, "restored baseline");
    if (baseline.Rows != RequiredRows || baseline.TypedRows != RequiredRows ||
        baseline.FourSlotRows != RequiredRows || baseline.SyntheticProofRows != RequiredRows ||
        baseline.Devices != RequiredDevices || baseline.Sensors != RequiredSensors || baseline.HostMaps != RequiredDevices ||
        baseline.ProofBytes != sourceFacts.ProofBytes || baseline.IndexBytes != sourceFacts.IndexBytes ||
        baseline.Devices != sourceFacts.Devices || baseline.Sensors != sourceFacts.Sensors || baseline.HostMaps != sourceFacts.HostMaps)
        throw new InvalidDataException("Restored database does not contain the full fixed synthetic four-slot proof shape.");

    var pruneCreated = DateTime.Today.AddHours(12);
    var expiredStart = DateTime.SpecifyKind(DateTime.Today.AddHours(1), DateTimeKind.Unspecified);
    var retainedStart = DateTime.SpecifyKind(DateTime.Today.AddHours(2), DateTimeKind.Unspecified);
    var sentinelValues = new[]
    {
        CreateTypedValue(1_000_001, expiredStart, pruneCreated),
        CreateTypedValue(1_000_002, retainedStart, pruneCreated)
    };
    if (store.MergeSampledValues(sentinelValues) != sentinelValues.Length)
        throw new InvalidOperationException("Could not seed the two isolated retention-boundary rows.");
    var cutoff = DateTime.Today.AddDays(-180);
    using (var context = backend.CreateContext())
    {
        context.Database.ExecuteSqlInterpolated($"UPDATE lf_prtg_values SET created_at = {cutoff.AddTicks(-1)} WHERE sensor_objid = {sentinelValues[0].SensorObjid} AND period_start = {sentinelValues[0].PeriodStart}");
        context.Database.ExecuteSqlInterpolated($"UPDATE lf_prtg_values SET created_at = {cutoff.AddTicks(1)} WHERE sensor_objid = {sentinelValues[1].SensorObjid} AND period_start = {sentinelValues[1].PeriodStart}");
    }
    var prunedRows = store.Prune(180);
    var rowsAfterPrune = await CountValuesAsync(restoredPath);
    var expiredRemaining = await SentinelExistsAsync(restoredPath, sentinelValues[0]);
    var retainedRemaining = await SentinelExistsAsync(restoredPath, sentinelValues[1]);
    var retentionBoundaryChecksPassed = prunedRows == 1 && !expiredRemaining && retainedRemaining && rowsAfterPrune == RequiredRows + 1;
    if (!retentionBoundaryChecksPassed)
        throw new InvalidDataException("Production 180-day prune did not delete only the pre-cutoff sentinel and retain the post-cutoff sentinel.");

    var rounds = new List<RoundObservation>(3);
    var previous = await MeasureStorageAsync(restoredPath);
    var previousProofBytes = sourceFacts.ProofBytes + EncodingBytes(sentinelValues[1].TrustedProof!);
    for (var round = 1; round <= 3; round++)
    {
        var period = DateTime.SpecifyKind(DateTime.Today.AddDays(181).AddHours(round), DateTimeKind.Unspecified);
        var batch = new List<PrtgValueRow>(BatchSize);
        long written = 0;
        long insertedProofBytes = 0;
        long dbHighWater = 0;
        long walHighWater = 0;
        long shmHighWater = 0;
        long totalHighWater = 0;
        for (var sensorOffset = 0; sensorOffset < RequiredSensors; sensorOffset++)
        {
            var row = CreateTypedValue(1_000_001L + sensorOffset, period, DateTime.UtcNow);
            insertedProofBytes += EncodingBytes(row.TrustedProof!);
            batch.Add(row);
            if (batch.Count < BatchSize && sensorOffset + 1 < RequiredSensors) continue;
            var count = store.MergeSampledValues(batch);
            if (count != batch.Count) throw new InvalidOperationException($"Round {round} wrote {count} of {batch.Count} rows.");
            written += count;
            batch.Clear();
            var files = MeasureFiles(restoredPath);
            dbHighWater = Math.Max(dbHighWater, files.DatabaseBytes);
            walHighWater = Math.Max(walHighWater, files.WalBytes);
            shmHighWater = Math.Max(shmHighWater, files.ShmBytes);
            totalHighWater = Math.Max(totalHighWater, files.TotalStorageBytes);
        }

        var current = await MeasureStorageAsync(restoredPath);
        var expectedRows = RequiredRows + 1 + (long)round * RequiredSensors;
        var shapeMatches = written == RequiredSensors;
        var currentProofBytes = checked(previousProofBytes + insertedProofBytes);
        var roundStorageHighWater = Math.Max(previous.TotalStorageBytes, totalHighWater);
        var growth = GrowthPercent(previous.TotalStorageBytes, roundStorageHighWater);
        var payloadGrowth = GrowthPercent(previousProofBytes, currentProofBytes);
        var indexGrowth = GrowthPercent(previous.IndexBytes, current.IndexBytes);
        if (!shapeMatches || growth > 5 || payloadGrowth > 5 || indexGrowth > 5)
            throw new InvalidDataException($"Round {round} did not satisfy exact retained row/proof/index/storage checks or exceeded 5% incremental growth.");
        rounds.Add(new RoundObservation(round, written, expectedRows, expectedRows, expectedRows,
            currentProofBytes, current.IndexBytes, dbHighWater, walHighWater, shmHighWater,
            roundStorageHighWater, growth, payloadGrowth, indexGrowth));
        previous = current with { TotalStorageBytes = roundStorageHighWater };
        previousProofBytes = currentProofBytes;
    }

    var finalFacts = await ReadDatabaseFactsAsync(restoredPath);
    ValidateFullShape(finalFacts, "final restored copy");
    if (finalFacts.Rows != RequiredRows + 1 + 3L * RequiredSensors ||
        finalFacts.TypedRows != finalFacts.Rows || finalFacts.FourSlotRows != finalFacts.Rows ||
        finalFacts.SyntheticProofRows != finalFacts.Rows || finalFacts.Devices != RequiredDevices ||
        finalFacts.Sensors != RequiredSensors || finalFacts.HostMaps != RequiredDevices ||
        !await SentinelExistsAsync(restoredPath, sentinelValues[1]))
        throw new InvalidDataException("Final read-only verification found a row, proof, slot, source-marker, or retained sentinel mismatch.");

    var sourceAfter = new FileInfo(sourcePath);
    var sourceWalAfter = ReadFileFence(sourcePath + "-wal");
    var sourceShmAfter = ReadFileFence(sourcePath + "-shm");
    var sourceDataVersionAfter = await ScalarLongAsync(sourceFence, "PRAGMA data_version");
    var sourceUnchanged = sourceAfter.Length == sourceLength && sourceAfter.LastWriteTimeUtc == sourceWriteUtc &&
        sourceDataVersionAfter == sourceDataVersion && sourceWalAfter == sourceWal && sourceShmAfter == sourceShm;
    if (!sourceUnchanged)
        throw new InvalidDataException("Source database changed while the helper was running; its snapshot fence failed.");
    var roundsPassed = rounds.Count == 3 && rounds.All(row => row.WrittenRows == RequiredSensors &&
        row.TotalStorageGrowthPercent <= 5 && row.ProofBytesGrowthPercent <= 5 && row.IndexGrowthPercent <= 5);
    var finalChecksPassed = finalFacts.Rows == RequiredRows + 1 + 3L * RequiredSensors &&
        finalFacts.TypedRows == finalFacts.Rows && finalFacts.FourSlotRows == finalFacts.Rows &&
        finalFacts.SyntheticProofRows == finalFacts.Rows && finalFacts.Devices == RequiredDevices &&
        finalFacts.Sensors == RequiredSensors && finalFacts.HostMaps == RequiredDevices &&
        retainedRemaining && !expiredRemaining;
    var measuredChecksPassed = retentionBoundaryChecksPassed && roundsPassed && finalChecksPassed && sourceUnchanged;
    var report = new
    {
        schema = "LogForesight.SqliteOwnedCopyHighWaterResult", schemaVersion = 1,
        runId = Guid.NewGuid().ToString("N"), owner = Environment.UserName,
        measuredAtUtc = DateTimeOffset.UtcNow,
        source = new { path = sourcePath, reportPath = sourceReportPath, reportSha256 = sourceReportSha256,
            sourceFacts.Provider, sourceFacts.Devices, sourceFacts.Sensors, sourceFacts.HostMaps,
            sourceFacts.Rows, sourceFacts.TypedRows, sourceFacts.FourSlotRows,
            sourceFacts.SyntheticProofRows, sourceFacts.ProofBytes, sourceFacts.IndexBytes, databaseBytes = sourceLength,
            walBytes = sourceWal.Length, shmBytes = sourceShm.Length,
            unchangedByReadOnlyModeAndLengthWriteTimeFence = sourceUnchanged },
        productionVersionFence = new { coreAssemblyVersion = typeof(StorageBackend).Assembly.GetName().Version?.ToString(),
            coreAssemblyMvid = typeof(StorageBackend).Assembly.ManifestModule.ModuleVersionId },
        ownedCopies = new { backupPath, restoredPath,
            backupIntegrityCheck = "ok", restoredIntegrityCheck = "ok", sourceWasOpenedReadOnly = true },
        retention180Days = new { days = 180, cutoff, basis = "production PrtgStore.Prune(180), created_at cut-off",
            productionRowsDeleted = prunedRows, strictPreCutoffSentinelDeleted = !expiredRemaining,
            strictPostCutoffSentinelRetained = retainedRemaining, baselineRowsRetained = rowsAfterPrune == RequiredRows + 1 },
        rounds,
        final = new { finalFacts.Devices, finalFacts.Sensors, finalFacts.HostMaps,
            finalFacts.Rows, finalFacts.TypedRows, finalFacts.FourSlotRows,
            finalFacts.SyntheticProofRows, finalFacts.ProofBytes, finalFacts.IndexBytes,
            finalFacts.DatabaseBytes, finalFacts.WalBytes, finalFacts.ShmBytes, finalFacts.TotalStorageBytes,
            exactExpectedRowsAndProofs = true, syntheticStorageOnly = true,
            nativeSourceEvidence = false, sqlServerCompatibilityAccepted = false },
        measuredChecksPassed,
        fullCapacityAccepted = false, retentionAccepted = false, recoveryAccepted = false,
        wholeRoundAccepted = false, productionPauseRecoveryJournalFaultTested = false,
        caveat = "SQLite owned-copy backup/restore, one-row strict created_at retention boundary, and three 15k-row synthetic storage rounds only. This does not qualify a full 64.8m run, a production workload, source-native evidence, pause/recovery/journal-fault behavior, N6 whole-round capacity, or SQL Server compatibility."
    };
    var reportPath = Path.Combine(outputRoot, "sqlite-owned-copy-highwater-result.json");
    await WriteNewTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"measured result written: {reportPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}. No result is recorded as passed; inspect the owned output directory before retrying.");
    return 2;
}

static async Task<int> RunSafetySelfTestAsync(string outputRoot)
{
    ValidateOwnedOutputRoot(outputRoot);
    Directory.CreateDirectory(outputRoot);
    await ClaimOwnedOutputAsync(outputRoot);
    var sourcePath = Path.Combine(outputRoot, "safety-source.sqlite");
    var backupPath = Path.Combine(outputRoot, "safety-backup.sqlite");
    var restoredPath = Path.Combine(outputRoot, "safety-restored.sqlite");
    var fakeReport = Path.Combine(outputRoot, "partial-input-report.json");
    var settings = new StorageSettings { Type = "Sqlite",
        ConnectionString = $"Data Source={sourcePath};Pooling=False;Cache=Shared", SqliteWal = true };
    var backend = new StorageBackend(settings, outputRoot);
    var store = backend.PrtgStore();
    var today = DateTime.Today;
    var rows = new[]
    {
        CreateTypedValue(9_001, DateTime.SpecifyKind(today.AddHours(1), DateTimeKind.Unspecified), DateTime.UtcNow),
        CreateTypedValue(9_002, DateTime.SpecifyKind(today.AddHours(2), DateTimeKind.Unspecified), DateTime.UtcNow),
        CreateTypedValue(9_003, DateTime.SpecifyKind(today.AddHours(3), DateTimeKind.Unspecified), DateTime.UtcNow)
    };
    if (store.MergeSampledValues(rows) != 3) throw new InvalidOperationException("Safety self-test could not seed its three typed rows.");
    var cutoff = DateTime.Today.AddDays(-180);
    using (var context = backend.CreateContext())
    {
        context.Database.ExecuteSqlInterpolated($"UPDATE lf_prtg_values SET created_at = {cutoff.AddTicks(-1)} WHERE sensor_objid = {rows[0].SensorObjid}");
        context.Database.ExecuteSqlInterpolated($"UPDATE lf_prtg_values SET created_at = {cutoff.AddTicks(1)} WHERE sensor_objid = {rows[1].SensorObjid}");
    }
    var sourceLength = new FileInfo(sourcePath).Length;
    var sourceWriteTime = new FileInfo(sourcePath).LastWriteTimeUtc;
    var sourceCount = await CountValuesAsync(sourcePath);
    await BackupDatabaseAsync(sourcePath, backupPath);
    await BackupDatabaseAsync(backupPath, restoredPath);
    var restoredSettings = new StorageSettings { Type = "Sqlite",
        ConnectionString = $"Data Source={restoredPath};Pooling=False;Cache=Shared", SqliteWal = true };
    var restoredBackend = new StorageBackend(restoredSettings, outputRoot);
    var pruned = restoredBackend.PrtgStore().Prune(180);
    var retainedOld = await SentinelExistsAsync(restoredPath, rows[1]);
    var deletedOld = await SentinelExistsAsync(restoredPath, rows[0]);
    var finalCount = await CountValuesAsync(restoredPath);
    await WriteNewTextAsync(fakeReport, JsonSerializer.Serialize(new
    {
        schema = "LogForesight.N6GeneratedHighWaterResult", schemaVersion = 2,
        provider = "sqlite", databasePath = sourcePath, fullFixedShape = false, input = new { }
    }));
    var partialRejected = false;
    try { _ = await ReadAndValidateSourceAsync(sourcePath, fakeReport); }
    catch (InvalidDataException) { partialRejected = true; }
    var sourceUnchanged = sourceCount == 3 && new FileInfo(sourcePath).Length == sourceLength &&
        new FileInfo(sourcePath).LastWriteTimeUtc == sourceWriteTime && await CountValuesAsync(sourcePath) == sourceCount;
    var checksPassed = pruned == 1 && deletedOld == false && retainedOld && finalCount == 2 &&
        partialRejected && sourceUnchanged;
    if (!checksPassed) throw new InvalidDataException("SQLite safety self-test did not preserve source or exact prune/input-fence behavior.");
    var report = new
    {
        schema = "LogForesight.SqliteHighWaterSafetySelfTest", schemaVersion = 1,
        owner = Environment.UserName, measuredAtUtc = DateTimeOffset.UtcNow,
        sourcePath, backupPath, restoredPath, sourceWasUntouched = sourceUnchanged,
        backupAndRestoreIntegrityChecksPassed = true, partialWorkloadInputRejected = partialRejected,
        productionPrune180DaysDeletedOnlyPreCutoffRow = pruned == 1 && !deletedOld && retainedOld && finalCount == 2,
        syntheticStorageOnly = true, nativeSourceEvidence = false,
        selfTestChecksPassed = checksPassed, fullCapacityAccepted = false, retentionAccepted = false,
        recoveryAccepted = false, sqlServerCompatibilityAccepted = false
    };
    var resultPath = Path.Combine(outputRoot, "sqlite-highwater-safety-selftest.json");
    await WriteNewTextAsync(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"safety self-test measurements written: {resultPath}");
    return 0;
}

static async Task<int> VerifyReadOnlyResultAsync(string reportPath)
{
    if (!File.Exists(reportPath) || (File.GetAttributes(reportPath) & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Result report must be an existing regular file.");
    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath), new JsonDocumentOptions { MaxDepth = 32 });
    var root = report.RootElement;
    if (root.GetProperty("schema").GetString() != "LogForesight.SqliteOwnedCopyHighWaterResult" ||
        root.GetProperty("schemaVersion").GetInt32() != 1 || !root.GetProperty("measuredChecksPassed").GetBoolean() ||
        root.GetProperty("fullCapacityAccepted").GetBoolean() || root.GetProperty("retentionAccepted").GetBoolean() ||
        root.GetProperty("recoveryAccepted").GetBoolean() || root.GetProperty("wholeRoundAccepted").GetBoolean())
        throw new InvalidDataException("Result report is invalid, incomplete, or improperly claims a formal capacity/retention/recovery pass.");
    var source = root.GetProperty("source");
    var sourcePath = Path.GetFullPath(source.GetProperty("path").GetString() ?? "");
    var sourceReportPath = Path.GetFullPath(source.GetProperty("reportPath").GetString() ?? "");
    if (!string.Equals(await HashFileAsync(sourceReportPath), source.GetProperty("reportSha256").GetString(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Source generator report SHA-256 changed after the result was produced.");
    var sourceFacts = await ReadDatabaseFactsAsync(sourcePath);
    ValidateFullShape(sourceFacts, "read-only source verification");
    await CheckIntegrityAsync(sourcePath);
    if (sourceFacts.Rows != source.GetProperty("Rows").GetInt64() || sourceFacts.TypedRows != source.GetProperty("TypedRows").GetInt64() ||
        sourceFacts.ProofBytes != source.GetProperty("ProofBytes").GetInt64())
        throw new InvalidDataException("Read-only source counts no longer match the recorded source fence.");
    var restoredPath = Path.GetFullPath(root.GetProperty("ownedCopies").GetProperty("restoredPath").GetString() ?? "");
    var finalFacts = await ReadDatabaseFactsAsync(restoredPath);
    ValidateFullShape(finalFacts, "read-only restored verification");
    await CheckIntegrityAsync(restoredPath);
    var final = root.GetProperty("final");
    if (finalFacts.Rows != final.GetProperty("Rows").GetInt64() ||
        finalFacts.TypedRows != final.GetProperty("TypedRows").GetInt64() ||
        finalFacts.FourSlotRows != final.GetProperty("FourSlotRows").GetInt64() ||
        finalFacts.SyntheticProofRows != final.GetProperty("SyntheticProofRows").GetInt64() ||
        finalFacts.ProofBytes != final.GetProperty("ProofBytes").GetInt64() ||
        finalFacts.IndexBytes != final.GetProperty("IndexBytes").GetInt64() ||
        finalFacts.Devices != RequiredDevices || finalFacts.Sensors != RequiredSensors ||
        finalFacts.HostMaps != RequiredDevices || finalFacts.Rows != RequiredRows + 1 + 3L * RequiredSensors)
        throw new InvalidDataException("Independent read-only database measurements disagree with the result report.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schema = "LogForesight.SqliteOwnedCopyHighWaterReadOnlyVerification", schemaVersion = 1,
        verifiedAtUtc = DateTimeOffset.UtcNow, reportPath, sourcePath, restoredPath,
        sourceRows = sourceFacts.Rows, restoredRows = finalFacts.Rows,
        typedFourSlotSyntheticRows = finalFacts.SyntheticProofRows,
        matchesReportAndExactFixedShape = true,
        fullCapacityAccepted = false, retentionAccepted = false, recoveryAccepted = false,
        sqlServerCompatibilityAccepted = false
    }));
    return 0;
}

static async Task<(string Provider, long Devices, long Sensors, long HostMaps, long Rows, long TypedRows, long FourSlotRows, long SyntheticProofRows, long ProofBytes, long IndexBytes)> ReadAndValidateSourceAsync(string sourcePath, string reportPath)
{
    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath), new JsonDocumentOptions { MaxDepth = 32 });
    var root = report.RootElement;
    var input = root.GetProperty("input");
    if (root.GetProperty("schema").GetString() != "LogForesight.N6GeneratedHighWaterResult" ||
        root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("provider").GetString() != "sqlite" ||
        !root.GetProperty("fullFixedShape").GetBoolean() ||
        input.GetProperty("hosts").GetInt32() != RequiredDevices ||
        input.GetProperty("sensors").GetInt32() != RequiredSensors ||
        input.GetProperty("days").GetInt32() != 180 || input.GetProperty("rowsPerSensor").GetInt32() != 4_320 ||
        input.GetProperty("rowOrder").GetString() != "sensor-major, one row per sensor-hour" ||
        input.GetProperty("requestedRows").GetInt64() != RequiredRows ||
        root.GetProperty("devicesWritten").GetInt32() != RequiredDevices ||
        root.GetProperty("sensorsWritten").GetInt32() != RequiredSensors ||
        root.GetProperty("hostMapsWritten").GetInt32() != RequiredDevices ||
        root.GetProperty("rowsCommitted").GetInt64() != RequiredRows ||
        root.GetProperty("persistedRows").GetInt64() != RequiredRows ||
        root.GetProperty("trustVersionOneRows").GetInt64() != RequiredRows ||
        root.GetProperty("typedSlotsPerRow").GetInt32() != 4 ||
        root.GetProperty("fullWorkloadAccepted").GetBoolean() || root.GetProperty("capacityAccepted").GetBoolean())
        throw new InvalidDataException("Source report is not the exact full fixed synthetic SQLite generator report; full-pass flags are rejected.");
    var values = input.GetProperty("values").GetString() ?? "";
    if (!values.Contains("synthetic", StringComparison.OrdinalIgnoreCase) ||
        !values.Contains("no native source authority", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Source report lacks the required synthetic/no-native-authority label.");
    var reportedPath = Path.GetFullPath(root.GetProperty("databasePath").GetString() ?? "");
    if (!string.Equals(reportedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Source database path does not match the generator report.");
    var facts = await ReadDatabaseFactsAsync(sourcePath);
    ValidateFullShape(facts, "source");
    var reportedProofBytes = root.GetProperty("typedProofUtf8Bytes").GetProperty("total").GetInt64();
    if (facts.Provider != "sqlite" || facts.Rows != RequiredRows || facts.TypedRows != RequiredRows ||
        facts.FourSlotRows != RequiredRows || facts.SyntheticProofRows != RequiredRows ||
        facts.ProofBytes != reportedProofBytes)
        throw new InvalidDataException("Read-only source facts do not match all 64.8m four-slot synthetic proof rows.");
    return (facts.Provider, facts.Devices, facts.Sensors, facts.HostMaps, facts.Rows, facts.TypedRows,
        facts.FourSlotRows, facts.SyntheticProofRows, facts.ProofBytes, facts.IndexBytes);
}

static async Task BackupDatabaseAsync(string sourcePath, string destinationPath)
{
    if (File.Exists(destinationPath)) throw new IOException("Refusing to overwrite a backup or restored copy.");
    var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
    await using var source = new SqliteConnection(sourceBuilder.ToString());
    await using var destination = new SqliteConnection(destinationBuilder.ToString());
    await source.OpenAsync();
    await destination.OpenAsync();
    source.BackupDatabase(destination);
    await destination.CloseAsync();
    await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    await verify.OpenAsync();
    var integrity = await ScalarTextAsync(verify, "PRAGMA integrity_check");
    if (integrity != "ok") throw new InvalidDataException($"SQLite backup integrity_check failed: {integrity}.");
}

static async Task<DbFacts> ReadDatabaseFactsAsync(string path)
{
    var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    await using var connection = new SqliteConnection(cs.ToString());
    await connection.OpenAsync();
    var provider = "sqlite";
    var devices = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_devices");
    var sensors = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_sensors");
    var hostMaps = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_host_map");
    var rows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_values");
    var typed = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_values WHERE trust_version = 1 AND trusted_proof IS NOT NULL");
    var slots = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_values WHERE trust_version = 1 AND json_array_length(json_extract(trusted_proof, '$.s')) = 4");
    var synthetic = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_values WHERE trust_version = 1 AND json_extract(trusted_proof, '$.sg') = 'capacity-synthetic-source-v1'");
    var proofBytes = await ScalarLongAsync(connection, "SELECT COALESCE(SUM(length(CAST(trusted_proof AS BLOB))), 0) FROM lf_prtg_values WHERE trust_version = 1 AND trusted_proof IS NOT NULL");
    var databaseBytes = FileSize(path);
    var walBytes = FileSize(path + "-wal");
    var shmBytes = FileSize(path + "-shm");
    var pageSize = await ScalarLongAsync(connection, "PRAGMA page_size");
    var pageCount = await ScalarLongAsync(connection, "PRAGMA page_count");
    var indexBytes = await ScalarLongAsync(connection,
        "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name IN ('IX_lf_prtg_values_uniq', 'IX_lf_prtg_values_created')");
    if (indexBytes <= 0) throw new InvalidDataException("SQLite dbstat did not report both PRTG values indexes.");
    var indexNames = await ScalarLongAsync(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('IX_lf_prtg_values_uniq', 'IX_lf_prtg_values_created')");
    if (indexNames != 2) throw new InvalidDataException("Expected unique-key and created_at production indexes are missing.");
    return new DbFacts(provider, devices, sensors, hostMaps, rows, typed, slots, synthetic, proofBytes, indexBytes,
        databaseBytes, walBytes, shmBytes, databaseBytes + walBytes + shmBytes, pageSize * pageCount);
}

static async Task CheckIntegrityAsync(string path)
{
    var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    await using var connection = new SqliteConnection(cs.ToString());
    await connection.OpenAsync();
    var integrity = await ScalarTextAsync(connection, "PRAGMA integrity_check");
    if (integrity != "ok") throw new InvalidDataException($"Read-only SQLite integrity_check failed: {integrity}.");
}

static async Task<StorageFacts> MeasureStorageAsync(string path)
{
    var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    await using var connection = new SqliteConnection(cs.ToString());
    await connection.OpenAsync();
    var indexBytes = await ScalarLongAsync(connection,
        "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name IN ('IX_lf_prtg_values_uniq', 'IX_lf_prtg_values_created')");
    if (indexBytes <= 0) throw new InvalidDataException("SQLite dbstat did not report both PRTG values indexes.");
    var files = MeasureFiles(path);
    return new StorageFacts(indexBytes, files.DatabaseBytes, files.WalBytes, files.ShmBytes, files.TotalStorageBytes);
}

static FileStorageFacts MeasureFiles(string path)
{
    var databaseBytes = FileSize(path);
    var walBytes = FileSize(path + "-wal");
    var shmBytes = FileSize(path + "-shm");
    return new FileStorageFacts(databaseBytes, walBytes, shmBytes, databaseBytes + walBytes + shmBytes);
}

static async Task<long> CountValuesAsync(string path)
{
    var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    await using var connection = new SqliteConnection(cs.ToString());
    await connection.OpenAsync();
    return await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lf_prtg_values");
}

static void ValidateFullShape(DbFacts facts, string label)
{
    if (facts.Devices != RequiredDevices || facts.Sensors != RequiredSensors || facts.HostMaps != RequiredDevices ||
        facts.Rows < RequiredRows || facts.TypedRows != facts.Rows || facts.FourSlotRows != facts.Rows ||
        facts.SyntheticProofRows != facts.Rows || facts.ProofBytes <= 0 || facts.IndexBytes <= 0 ||
        facts.DatabaseBytes <= 0 || facts.PageBytes <= 0)
        throw new InvalidDataException($"{label} failed read-only row/proof/slot/index/storage validation.");
}

static async Task<bool> SentinelExistsAsync(string path, PrtgValueRow row)
{
    var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    await using var connection = new SqliteConnection(cs.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM lf_prtg_values WHERE sensor_objid = $sensor AND period_start = $period";
    command.Parameters.AddWithValue("$sensor", row.SensorObjid);
    command.Parameters.AddWithValue("$period", row.PeriodStart);
    return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
}

static PrtgValueRow CreateTypedValue(long sensorId, DateTime periodStart, DateTime createdAt)
{
    periodStart = DateTime.SpecifyKind(periodStart, DateTimeKind.Unspecified);
    var utcHour = DateTime.SpecifyKind(periodStart, DateTimeKind.Utc);
    var slots = Enumerable.Range(0, 4).Select(slot =>
    {
        var measuredAt = utcHour.AddMinutes(slot * 15);
        var physical = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sensorId}|{periodStart:O}|{slot}")))[..32];
        return new PrtgTrustedSampleSlot(slot, physical, measuredAt, measuredAt.AddSeconds(10), 42 + slot);
    }).ToArray();
    var effective = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);
    var proof = PrtgTrustedSampleProof.From(new PrtgTrustedSample(sensorId, slots.Average(s => s.Value),
        SyntheticSource, $"resource-{sensorId}", $"channel-{sensorId}", "epoch-1", "typed-semantic-v1",
        "capacity-strategy-v1", 15, effective, slots[0].MeasuredAt, slots[0].ReceivedAt,
        TimeSpan.FromMinutes(15), PrtgTrustedSampleQuality.Good, slots[0].PhysicalIdHash, "UTC", "UTC"), slots);
    return new PrtgValueRow { SensorObjid = sensorId, PeriodStart = periodStart,
        AvgValue = slots.Average(s => s.Value), MinValue = slots.Min(s => s.Value), MaxValue = slots.Max(s => s.Value),
        Coverage = 100, Quality = PrtgDataQuality.Sampled, CreatedAt = createdAt,
        TrustVersion = 1, TrustedProof = PrtgTrustedSampleProof.Serialize(proof) };
}

static double GrowthPercent(long before, long after) => before <= 0 ? 100 : Math.Max(0, (after - before) * 100d / before);
static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
static SidecarFence ReadFileFence(string path)
{
    if (!File.Exists(path)) return new SidecarFence(false, 0, DateTime.MinValue);
    var info = new FileInfo(path);
    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("SQLite sidecar must not be a reparse point.");
    return new SidecarFence(true, info.Length, info.LastWriteTimeUtc);
}
static int EncodingBytes(string value) => System.Text.Encoding.UTF8.GetByteCount(value);
static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand(); command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}
static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand(); command.CommandText = sql;
    return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
}
static async Task<string> HashFileAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var hash = await SHA256.HashDataAsync(stream);
    return Convert.ToHexString(hash);
}
static async Task ClaimOwnedOutputAsync(string root)
{
    if (Directory.EnumerateFileSystemEntries(root).Any())
        throw new IOException("Output directory was populated after validation; refusing to use it.");
    var marker = Path.Combine(root, ".sqlite-highwater-owner");
    await using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
    var content = System.Text.Encoding.UTF8.GetBytes($"owner={Environment.UserName}\nrun={Guid.NewGuid():N}\n");
    await stream.WriteAsync(content);
    await stream.FlushAsync();
}
static async Task WriteNewTextAsync(string path, string content)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
}
static void ValidateSourceInputs(string sourcePath, string reportPath)
{
    if (!File.Exists(sourcePath) || !File.Exists(reportPath) ||
        (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0 ||
        (File.GetAttributes(reportPath) & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Source DB and source report must be existing regular files, not reparse points.");
    if (string.Equals(sourcePath, reportPath, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Source database and report must be separate files.");
}
static void ValidateOwnedOutputRoot(string root)
{
    if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Output must be a new, dedicated directory; existing paths are never reused.");
    if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
        throw new ArgumentException("Output must be on a local fixed volume.");
    for (var ancestor = new DirectoryInfo(root).Parent; ancestor is not null; ancestor = ancestor.Parent)
        if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Output ancestors must not be reparse points.");
}
static string Required(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
    ? value : throw new ArgumentException($"Missing --{key}.");
static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            throw new ArgumentException("Expected --source-db path --source-report path --output new-directory.");
        if (!result.TryAdd(args[i][2..], args[++i])) throw new ArgumentException("Duplicate command-line option.");
    }
    return result;
}

sealed record DbFacts(string Provider, long Devices, long Sensors, long HostMaps,
    long Rows, long TypedRows, long FourSlotRows, long SyntheticProofRows,
    long ProofBytes, long IndexBytes, long DatabaseBytes, long WalBytes, long ShmBytes, long TotalStorageBytes, long PageBytes);
sealed record RoundObservation(int Round, long WrittenRows, long ExpectedRetainedRows, long ExpectedTypedRows, long ExpectedFourSlotRows,
    long ProofBytes, long IndexBytes, long DatabaseBytes, long WalBytes, long ShmBytes, long TotalStorageBytes,
    double TotalStorageGrowthPercent, double ProofBytesGrowthPercent, double IndexGrowthPercent);
sealed record StorageFacts(long IndexBytes, long DatabaseBytes, long WalBytes, long ShmBytes, long TotalStorageBytes);
sealed record FileStorageFacts(long DatabaseBytes, long WalBytes, long ShmBytes, long TotalStorageBytes);
sealed record SidecarFence(bool Exists, long Length, DateTime LastWriteUtc);
