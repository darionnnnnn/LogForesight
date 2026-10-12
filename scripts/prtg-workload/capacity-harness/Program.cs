using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

const int HostCount = 3_000;
const int SensorCount = 15_000;
const int Days = 180;
const int RowsPerSensor = Days * 24;
const long FixedRows = (long)SensorCount * RowsPerSensor;
const string PreparationSchema = "round53-capacity-harness-preparation-v1";
const string SyntheticSourceGeneration = "capacity-synthetic-source-v1";

var options = ParseArgs(args);
if (options.ContainsKey("self-test-root"))
{
    RunSelfTest(Required(options, "self-test-root"));
    return 0;
}

if (!options.ContainsKey("confirm-source-quiescent"))
    throw new ArgumentException("Full preparation requires --confirm-source-quiescent after the generator has exited and its result receipt is complete.");

var sourceRoot = Path.GetFullPath(Required(options, "generator-root"));
var outputRoot = Path.GetFullPath(Required(options, "output-root"));
if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("Generator root does not exist.");
if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
    throw new IOException("Output root must be a new, dedicated directory; existing data is never reused or deleted.");
AssertNoReparseAncestors(sourceRoot, "Generator root");
AssertNoReparseAncestors(Path.GetDirectoryName(outputRoot)!, "Output parent");
if (IsSameOrWithin(outputRoot, sourceRoot) || IsSameOrWithin(sourceRoot, outputRoot))
    throw new IOException("Source and output roots must be disjoint.");

var receiptPath = Path.Combine(sourceRoot, "n6-high-water-result.json");
if (!File.Exists(receiptPath)) throw new FileNotFoundException("Completed storage-generator receipt is missing.", receiptPath);
var receiptHash = HashFile(receiptPath);
using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath), new JsonDocumentOptions { MaxDepth = 16 });
ValidateGeneratorReceipt(receipt.RootElement);
var sourceDatabase = Path.GetFullPath(RequiredString(receipt.RootElement, "databasePath"));
if (!IsSameOrWithin(sourceDatabase, sourceRoot) || !File.Exists(sourceDatabase))
    throw new InvalidDataException("Generator database path must resolve to a regular file inside the completed generator root.");
AssertNoReparseAncestors(sourceDatabase, "Generator database");
if (!string.Equals(Path.GetExtension(sourceDatabase), ".db", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Only the owned SQLite generator database can be copied.");

var outputVolume = new DriveInfo(Path.GetPathRoot(outputRoot)!);
var sourceSidecars = SidecarPaths(sourceDatabase);
var sourceSnapshotBefore = HashFileSet(new[] { sourceDatabase }.Concat(sourceSidecars.Where(File.Exists)));
var sourceBytes = new FileInfo(sourceDatabase).Length + sourceSidecars.Where(File.Exists).Sum(path => new FileInfo(path).Length);
var minimumFree = checked(sourceBytes * 2L + (long)Math.Ceiling(outputVolume.TotalSize * 0.25));
if (outputVolume.AvailableFreeSpace < minimumFree)
    throw new IOException($"Copy refused: destination volume has {outputVolume.AvailableFreeSpace} free bytes; require at least {minimumFree} for an isolated working copy, backup copy and 25% total-volume reserve.");

var stageRoot = outputRoot + ".stage-" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(stageRoot);
var stagedDatabase = Path.Combine(stageRoot, "Db", "logforesight.db");
var finalDatabase = Path.Combine(outputRoot, "Db", "logforesight.db");
Directory.CreateDirectory(Path.GetDirectoryName(stagedDatabase)!);
CopyStableSqliteFiles(sourceDatabase, stagedDatabase);
var sourceAfterCopy = HashFileSet(new[] { sourceDatabase }.Concat(sourceSidecars.Where(File.Exists)));
if (!string.Equals(sourceSnapshotBefore, sourceAfterCopy, StringComparison.Ordinal))
    throw new IOException("Generator database or WAL sidecar changed during the copy; staged output is retained and is not promoted.");
ValidateDatabaseShapeReadOnly(stagedDatabase);

var backend = new StorageBackend(new StorageSettings
{
    Type = "Sqlite",
    SqliteWal = true,
    ConnectionString = $"Data Source={stagedDatabase};Pooling=False;Cache=Shared"
}, stageRoot);
var seededHostCount = SeedSyntheticWebHosts(backend, HostCount);
var databaseFacts = VerifyPreparedDatabase(backend, FixedRows);
var sourceSnapshotAfter = HashFileSet(new[] { sourceDatabase }.Concat(sourceSidecars.Where(File.Exists)));
if (!string.Equals(sourceSnapshotBefore, sourceSnapshotAfter, StringComparison.Ordinal))
    throw new IOException("Generator database or WAL sidecar changed during the read-only copy; staged output is retained for inspection and is not promoted.");
var receiptHashAfter = HashFile(receiptPath);
if (!string.Equals(receiptHash, receiptHashAfter, StringComparison.Ordinal))
    throw new IOException("Generator completion receipt changed during the copy; staged output is retained and is not promoted.");

var hostIds = Enumerable.Range(1, HostCount).Select(id => (long)id).ToArray();
var runId = Guid.NewGuid().ToString("N");
var prepared = new
{
    schema = PreparationSchema,
    status = "capacity-prerequisite-only",
    runId,
    createdUtc = DateTimeOffset.UtcNow,
    source = new
    {
        kind = "owned-synthetic-storage-generator",
        sourceGeneration = SyntheticSourceGeneration,
        generatorReceiptPath = receiptPath,
        generatorReceiptSha256 = receiptHash,
        sourceDatabasePath = sourceDatabase,
        sourceDatabaseAndSidecarsSha256 = sourceSnapshotBefore,
        sourceBytesIncludingSqliteSidecars = sourceBytes,
        sourceDatabaseWasOpenedBySqlite = false,
        sourceFilesWereCopiedWithoutDatabaseEngine = true,
        sourceUnchangedDuringCopy = true
    },
    fixtureShape = new
    {
        netiqHostRows = seededHostCount,
        prtgDevices = databaseFacts.Devices,
        prtgSensors = databaseFacts.Sensors,
        hostMapRows = databaseFacts.HostMaps,
        sourceHostMapDate = databaseFacts.HostMapDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        typedHistoricRows = databaseFacts.TypedRows,
        rowsPerSensor = RowsPerSensor,
        historyDays = Days,
        sourceGeneration = SyntheticSourceGeneration,
        hostIdsSha256 = HashText(string.Join("\n", hostIds)),
        capacityThresholdContract = new
        {
            hostCount = HostCount,
            sensorCount = SensorCount,
            historicRows = FixedRows,
            fixedCells = new[] { "netiq-only", "combined", "combined", "netiq-only", "netiq-only", "combined" },
            routeRequestsPerCell = 200,
            totalRouteRequests = 1_200,
            routeP95RelativeIncreaseMaximum = 0.10,
            routeP95AbsoluteMaximumMilliseconds = 2_000,
            netiqHostDayP95RelativeIncreaseMaximum = 0.10,
            scheduleWindowMaximumFraction = 0.75,
            resourceHeadroomFraction = 0.25
        }
    },
    output = new
    {
        dataRoot = outputRoot,
        sqlitePath = finalDatabase,
        sqliteSha256 = HashFile(stagedDatabase),
        hostsBlobKey = "hosts"
    },
    qualification = new
    {
        qualificationJobState = "absent",
        bindingRows = 0,
        profileRows = 0,
        nativeQualificationProofs = 0,
        syntheticTrustedSampleRowsAreNativeProof = false
    },
    sourceInputs = new
    {
        netiqServerEndpoint = "not-configured",
        sentinelFixtureEndpoint = "not-configured",
        scheduleOutcomes = "not-created",
        measuredRouteRequests = 0,
        realScheduleMatrixComplete = false
    },
    nativeSourceVerified = false,
    fullWorkloadAccepted = false,
    capacityAccepted = false,
    retentionAccepted = false,
    recoveryAccepted = false,
    wholeRoundAccepted = false,
    note = "Preparation only. It seeds synthetic Web host catalogue rows into an isolated copy of the completed storage generator database. It does not create qualification jobs, trusted bindings/profiles, native proofs, NetIQ outcomes, schedule evidence, route measurements, or public-matrix admission. The public live qualification fence is unchanged."
};

var reportPath = Path.Combine(stageRoot, "capacity-harness-preparation.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(prepared, JsonOptionsHolder.Options), new UTF8Encoding(false));
var launchPath = Path.Combine(stageRoot, "start-development-web.ps1");
await File.WriteAllTextAsync(launchPath, CreateLaunchScript(finalDatabase, outputRoot), new UTF8Encoding(false));
Directory.Move(stageRoot, outputRoot);
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "capacity-prerequisite-only",
    dataRoot = outputRoot,
    database = Path.Combine(outputRoot, "Db", "logforesight.db"),
    preparationReceipt = Path.Combine(outputRoot, "capacity-harness-preparation.json"),
    launchInstructions = Path.Combine(outputRoot, "start-development-web.ps1"),
    nativeSourceVerified = false,
    capacityAccepted = false,
    wholeRoundAccepted = false
}, JsonOptionsHolder.Options));
return 0;

static int SeedSyntheticWebHosts(StorageBackend backend, int count)
{
    using (var context = backend.CreateContext())
    {
        if (context.Blobs.AsNoTracking().Any(row => row.BlobKey == "hosts"))
            throw new InvalidDataException("Source database already contains a Web hosts blob; refusing to merge or overwrite prior data.");
    }
    var now = DateTime.Now;
    var hosts = Enumerable.Range(1, count).Select(id => new WebHost
    {
        HostId = id,
        HostName = $"capacity-synthetic-host-{id:D4}",
        Source = "netiq",
        Os = WebHost.OsWindows,
        Active = true,
        IpAddress = $"10.{(id - 1) / 65_536}.{((id - 1) / 256) % 256}.{((id - 1) % 254) + 1}",
        LastReportAt = now.AddMinutes(-((id - 1) % 59)),
        CreatedAt = now.AddDays(-30),
        GroupIds = [1 + ((id - 1) / 100)]
    }).ToList();
    var hostJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    backend.Blob("hosts").Mutate(_ => (JsonSerializer.Serialize(hosts, hostJsonOptions), hosts.Count));
    var loaded = new HostStore(backend.Blob("hosts")).GetAll();
    if (loaded.Count != count || loaded.Select(host => host.HostId).Distinct().Count() != count ||
        loaded.Any(host => !host.HostName.StartsWith("capacity-synthetic-host-", StringComparison.Ordinal)))
        throw new InvalidDataException("Seeded Web host catalogue failed the actual HostStore round-trip check.");
    return loaded.Count;
}

static (long Devices, long Sensors, long HostMaps, DateTime HostMapDate, long TypedRows) VerifyPreparedDatabase(StorageBackend backend, long expectedRows)
{
    using var context = backend.CreateContext();
    var devices = context.PrtgDevices.LongCount();
    var sensors = context.PrtgSensors.LongCount();
    var mapDateRows = context.PrtgHostMaps.AsNoTracking().Select(row => row.MapDate).ToArray();
    var hostMaps = context.PrtgHostMaps.LongCount();
    var values = context.PrtgValues.LongCount();
    var typed = context.PrtgValues.LongCount(row => row.TrustVersion == 1 && row.TrustedProof != null);
    if (devices != HostCount || sensors != SensorCount || hostMaps != HostCount || values != expectedRows || typed != expectedRows)
        throw new InvalidDataException($"Copied SQLite shape differs from the fixed workload: devices={devices}, sensors={sensors}, maps={hostMaps}, values={values}, typed={typed}.");
    var hostMapDate = ValidateSingleHostMapDate(mapDateRows, HostCount);

    var proofSamples = context.PrtgValues.AsNoTracking().Where(row => row.TrustVersion == 1 && row.TrustedProof != null)
        .OrderBy(row => row.SensorObjid).ThenBy(row => row.PeriodStart).Select(row => row.TrustedProof).Take(3).ToArray();
    if (proofSamples.Length != 3 || proofSamples.Any(raw =>
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var proof = PrtgTrustedSampleProof.Deserialize(raw);
        return proof.SourceGeneration != SyntheticSourceGeneration || proof.Slots.Count != 4;
    }))
        throw new InvalidDataException("Copied typed rows lack the expected explicitly synthetic four-slot source marker.");

    var qualificationKeys = context.Blobs.AsNoTracking().Select(row => row.BlobKey).ToArray();
    if (qualificationKeys.Any(key => key.StartsWith(PrtgTrustedSamplingBinding.StorePrefix, StringComparison.Ordinal) ||
            key.StartsWith(PrtgTrustedSamplingProfile.StorePrefix, StringComparison.Ordinal) ||
            key == PrtgHistoricAdmissionStore.BlobKey))
        throw new InvalidDataException("Copied source contains durable qualification, binding, profile, or historic admission state; refusing to relabel it as a clean capacity prerequisite.");
    if (new PrtgQualificationJobStateStore(backend).ReadCurrent() is not null)
        throw new InvalidDataException("Copied source contains a durable qualification job; refusing to prepare it as a test-source-only prerequisite.");
    return (devices, sensors, hostMaps, hostMapDate, typed);
}

static DateTime ValidateSingleHostMapDate(IReadOnlyCollection<DateTime> mapDateRows, int expectedCount)
{
    var dates = mapDateRows.Select(date => date.Date).Distinct().ToArray();
    if (mapDateRows.Count != expectedCount || dates.Length != 1)
        throw new InvalidDataException($"Host map date shape differs from the source generator: rows={mapDateRows.Count}, expected={expectedCount}, distinctDates={dates.Length}.");
    return dates[0];
}

static void ValidateGeneratorReceipt(JsonElement root)
{
    if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "schema") != "LogForesight.N6GeneratedHighWaterResult" ||
        RequiredString(root, "provider") != "sqlite")
        throw new InvalidDataException("Only a completed owned SQLite storage-generator receipt is accepted.");
    var input = root.GetProperty("input");
    RequireEqual(input.GetProperty("hosts").GetInt32(), HostCount, "generator hosts");
    RequireEqual(input.GetProperty("sensors").GetInt32(), SensorCount, "generator sensors");
    RequireEqual(input.GetProperty("days").GetInt32(), Days, "generator days");
    RequireEqual(input.GetProperty("rowsPerSensor").GetInt32(), RowsPerSensor, "generator rowsPerSensor");
    RequireEqual(input.GetProperty("requestedRows").GetInt64(), FixedRows, "generator requestedRows");
    RequireEqual(root.GetProperty("rowsCommitted").GetInt64(), FixedRows, "generator rowsCommitted");
    RequireEqual(root.GetProperty("persistedRows").GetInt64(), FixedRows, "generator persistedRows");
    if (!root.GetProperty("fullFixedShape").GetBoolean() || root.GetProperty("typedSlotsPerRow").GetInt32() != 4 ||
        root.GetProperty("trustVersionOneRows").GetInt64() != FixedRows)
        throw new InvalidDataException("Generator report does not attest the fixed four-slot 64,800,000-row shape.");
    foreach (var flag in new[] { "fullWorkloadAccepted", "capacityAccepted", "retentionAccepted", "recoveryAccepted", "wholeRoundAccepted" })
        if (root.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True)
            throw new InvalidDataException($"Generator receipt improperly claims {flag}; refusing source promotion.");
    var path = RequiredString(root, "databasePath");
    if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Generator receipt omitted its database path.");
}

static void ValidateDatabaseShapeReadOnly(string sourcePath)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var connection = new SqliteConnection(builder.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
    using var reader = command.ExecuteReader();
    var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (reader.Read()) tables.Add(reader.GetString(0));
    var required = new[] { "lf_blobs", "lf_prtg_devices", "lf_prtg_sensors", "lf_prtg_values", "lf_prtg_host_map" };
    var missing = required.Where(name => !tables.Contains(name)).ToArray();
    if (missing.Length > 0)
        throw new InvalidDataException("Copied SQLite shape differs from the expected generator schema; required tables are missing: " + string.Join(", ", missing));
}

static void CopyStableSqliteFiles(string sourcePath, string destinationPath)
{
    File.Copy(sourcePath, destinationPath, overwrite: false);
    var sourceWal = sourcePath + "-wal";
    if (File.Exists(sourceWal)) File.Copy(sourceWal, destinationPath + "-wal", overwrite: false);
    // The SQLite shared-memory index is transient and is rebuilt for the isolated copy.
}

static void RunSelfTest(string root)
{
    var fullRoot = Path.GetFullPath(root);
    if (Directory.Exists(fullRoot) || File.Exists(fullRoot))
        throw new IOException("Self-test root must be new and dedicated; existing data is never reused or deleted.");
    AssertNoReparseAncestors(Path.GetDirectoryName(fullRoot)!, "Self-test parent");
    var stageRoot = fullRoot + ".stage-selftest";
    Directory.CreateDirectory(stageRoot);
    var db = Path.Combine(stageRoot, "Db", "self-test.db");
    Directory.CreateDirectory(Path.GetDirectoryName(db)!);
    var backend = new StorageBackend(new StorageSettings
    {
        Type = "Sqlite", SqliteWal = true, ConnectionString = $"Data Source={db};Pooling=False"
    }, stageRoot);
    var hostCount = SeedSyntheticWebHosts(backend, 3);
    var jobAbsent = new PrtgQualificationJobStateStore(backend).ReadCurrent() is null;
    using var context = backend.CreateContext();
    var forbiddenBlobs = context.Blobs.AsNoTracking().Select(row => row.BlobKey).ToArray()
        .Where(key => key.StartsWith(PrtgTrustedSamplingBinding.StorePrefix, StringComparison.Ordinal) ||
            key.StartsWith(PrtgTrustedSamplingProfile.StorePrefix, StringComparison.Ordinal) ||
            key == PrtgHistoricAdmissionStore.BlobKey).ToArray();
    if (hostCount != 3 || !jobAbsent || forbiddenBlobs.Length != 0)
        throw new InvalidDataException("Small self-test must seed only synthetic hosts and must not create job, binding, or profile state.");
    var priorDate = DateTime.Today.AddDays(-1).Date;
    var priorHostMapDate = ValidateSingleHostMapDate(Enumerable.Repeat(priorDate, 3).ToArray(), 3);
    var report = new
    {
        schema = "round53-capacity-harness-self-test-v1",
        status = "self-test-only-not-e07-evidence",
        hosts = hostCount,
        fullShape = false,
        mapDateBoundaryCheck = "passed",
        sourceHostMapDateForBoundaryCheck = priorHostMapDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        qualificationJobState = "absent",
        bindingRows = 0,
        profileRows = 0,
        nativeQualificationProofs = 0,
        output = new
        {
            dataRoot = fullRoot,
            sqlitePath = Path.Combine(fullRoot, "Db", "self-test.db"),
            launchInstructions = Path.Combine(fullRoot, "start-development-web.ps1")
        },
        nativeSourceVerified = false,
        fullWorkloadAccepted = false,
        capacityAccepted = false,
        retentionAccepted = false,
        recoveryAccepted = false,
        wholeRoundAccepted = false
    };
    File.WriteAllText(Path.Combine(stageRoot, "self-test-receipt.json"), JsonSerializer.Serialize(report, JsonOptionsHolder.Options), new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(stageRoot, "start-development-web.ps1"),
        CreateLaunchScript(Path.Combine(fullRoot, "Db", "self-test.db"), fullRoot), new UTF8Encoding(false));
    Directory.Move(stageRoot, fullRoot);
    Console.WriteLine(JsonSerializer.Serialize(report, JsonOptionsHolder.Options));
}

static string CreateLaunchScript(string dbPath, string dataRoot)
{
    var escapedDb = dbPath.Replace("'", "''", StringComparison.Ordinal);
    var escapedRoot = dataRoot.Replace("'", "''", StringComparison.Ordinal);
    var script = @"
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$WebProjectPath,
    [ValidateRange(1, 65535)][int]$LoopbackPort = 5100,
    [switch]$PreviewOnly
)
$ErrorActionPreference = 'Stop'
$resolvedWebProjectPath = (Resolve-Path -LiteralPath $WebProjectPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedWebProjectPath -PathType Leaf) -or
    [IO.Path]::GetExtension($resolvedWebProjectPath) -cne '.csproj') {
    throw 'WebProjectPath must resolve to an existing .csproj file.'
}
$dotnetArguments = @(
    'run', '--project', $resolvedWebProjectPath, '--',
    '--Storage:Type', 'Sqlite',
    '--Storage:ConnectionString', 'Data Source=@DB@;Pooling=False',
    '--Storage:DataRoot', '@ROOT@',
    '--Auth:Provider', 'Stub',
    '--urls', ""http://127.0.0.1:$LoopbackPort""
)
$preview = [ordered]@{
    status = 'preview-only-no-web-started'
    purpose = 'test-source Web startup preview only; not a version-fence or qualification launcher'
    webProjectPath = $resolvedWebProjectPath
    loopbackUrl = ""http://127.0.0.1:$LoopbackPort""
    sqlitePath = '@DB@'
    dataRoot = '@ROOT@'
    dotnetExecutable = 'dotnet'
    dotnetArguments = $dotnetArguments
    sourceInputs = @{
        netiqServerEndpoint = 'not-configured'
        sentinelFixtureEndpoint = 'not-configured'
        scheduleOutcomes = 'not-created'
        nativeQualificationProofs = 0
    }
}
if ($PreviewOnly) {
    $preview | ConvertTo-Json -Depth 6 -Compress
    return
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
& dotnet @dotnetArguments
exit $LASTEXITCODE
";
    return script.Replace("@DB@", escapedDb, StringComparison.Ordinal)
        .Replace("@ROOT@", escapedRoot, StringComparison.Ordinal).TrimStart();
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (key == "--confirm-source-quiescent") { result[key[2..]] = "true"; continue; }
        if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Expected --name value, got {key}.");
        if (!result.TryAdd(key[2..], args[++i])) throw new ArgumentException($"Duplicate option {key}.");
    }
    return result;
}

static string Required(Dictionary<string, string> args, string key) =>
    args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{key}.");

static string RequiredString(JsonElement obj, string key) =>
    obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException($"Receipt omitted required string {key}.");

static void RequireEqual(long actual, long expected, string name)
{
    if (actual != expected) throw new InvalidDataException($"{name} mismatch: expected {expected}, received {actual}.");
}

static string[] SidecarPaths(string path) => [path + "-wal", path + "-shm"];

static string HashFile(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string HashFileSet(IEnumerable<string> paths)
{
    var items = paths.Order(StringComparer.Ordinal).Select(path => new
    {
        name = Path.GetFileName(path),
        length = new FileInfo(path).Length,
        sha256 = HashFile(path)
    });
    return HashText(JsonSerializer.Serialize(items, JsonOptionsHolder.Options));
}

static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static bool IsSameOrWithin(string candidate, string root)
{
    var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
        fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

static void AssertNoReparseAncestors(string path, string label)
{
    var cursor = new DirectoryInfo(Path.GetFullPath(path));
    while (cursor is not null)
    {
        if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{label} or an existing ancestor is a reparse point.");
        cursor = cursor.Parent;
    }
}

static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 24
    };
}
