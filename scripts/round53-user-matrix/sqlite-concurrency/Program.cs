using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LogForesight.Core.Persistence.Sql;

if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
    throw new ArgumentException("usage: SqliteConcurrencyHarness.exe OWNED_CLONE_DB SOURCE_DB_READ_ONLY");

var databasePath = Path.GetFullPath(args[0]);
var sourcePath = Path.GetFullPath(args[1]);
var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Pooling = false
}.ToString();

long ReadVersion()
{
    using var db = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "SELECT version FROM lf_blobs WHERE blob_key='hosts'";
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
}

string ReadUpdatedAt()
{
    using var db = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "SELECT updated_at FROM lf_blobs WHERE blob_key='hosts'";
    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
}

(long Version, string UpdatedAt) ReadSourceHosts()
{
    var readOnly = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly };
    using var db = new Microsoft.Data.Sqlite.SqliteConnection(readOnly.ToString());
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "SELECT version, updated_at FROM lf_blobs WHERE blob_key='hosts'";
    using var reader = command.ExecuteReader();
    if (!reader.Read()) throw new InvalidDataException("Source DB has no hosts blob.");
    return (reader.GetInt64(0), reader.GetString(1));
}

var safeSqlLog = new System.Text.StringBuilder();
var options = new DbContextOptionsBuilder<LfDbContext>()
    .UseSqlite(connectionString)
    .LogTo(message => safeSqlLog.AppendLine(message), new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information)
    .Options;
var store = new EfJsonBlobStore(() => new LfDbContext(options), "hosts");
var initialVersion = ReadVersion();
var initialTimestamp = ReadUpdatedAt();
var sourceHosts = ReadSourceHosts();
var canonical = DateTime.Parse(initialTimestamp, CultureInfo.InvariantCulture).ToString(
    "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
if (!StringComparer.Ordinal.Equals(canonical, initialTimestamp))
    throw new InvalidDataException($"Owned clone hosts timestamp is not EF/SQLite canonical: {initialTimestamp} vs {canonical}");

store.Mutate(current => ((current ?? "null") + " ", 1));
var afterFirstVersion = ReadVersion();
var afterFirstTimestamp = ReadUpdatedAt();
store.Mutate(current => ((current ?? "null") + " ", 2));
var afterSecondVersion = ReadVersion();
var afterSecondTimestamp = ReadUpdatedAt();
if (afterFirstVersion != initialVersion + 1 || afterSecondVersion != initialVersion + 2)
    throw new InvalidOperationException($"Consecutive mutations did not increment versions exactly: {initialVersion}->{afterFirstVersion}->{afterSecondVersion}");
foreach (var value in new[] { initialTimestamp, afterFirstTimestamp, afterSecondTimestamp })
{
    var expected = DateTime.Parse(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    if (!StringComparer.Ordinal.Equals(expected, value))
        throw new InvalidDataException($"Noncanonical EF/SQLite timestamp: {value} vs {expected}");
}
var updateSql = safeSqlLog.ToString();
var whereAt = updateSql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
if (whereAt < 0 || !updateSql.Contains("\"blob_key\"", StringComparison.Ordinal) ||
    !updateSql.Contains("\"updated_at\"", StringComparison.Ordinal) || !updateSql.Contains("\"version\"", StringComparison.Ordinal))
    throw new InvalidOperationException("EF SQLite UPDATE did not log the blob key, updated_at, and version concurrency predicate columns.");

Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
{
    result = "PASS",
    db = databasePath,
    sourceDb = sourcePath,
    sourceVersion = sourceHosts.Version,
    sourceTimestamp = sourceHosts.UpdatedAt,
    initialVersion,
    initialTimestamp,
    afterFirstVersion,
    afterFirstTimestamp,
    afterSecondVersion,
    afterSecondTimestamp,
    canonicalFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    concurrencyPredicateColumns = new[] { "blob_key", "updated_at", "version" },
    efSensitiveSqlLogging = false
}));
