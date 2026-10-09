using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Service;

/// <summary>
/// 本行程可讀取的唯讀資料庫儲存事實。欄位刻意不包含伺服器、資料庫、檔案、掛載點或連線名稱。
/// </summary>
public sealed class PrtgStorageEnvironmentFacts
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "unknown";
    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; init; } = "unknown";
    [JsonPropertyName("edition")]
    public string Edition { get; init; } = "unknown";
    [JsonPropertyName("engine_edition")]
    public string EngineEdition { get; init; } = "unknown";
    [JsonPropertyName("file_status")]
    public string FileStatus { get; init; } = "unknown";
    [JsonPropertyName("volume_status")]
    public string VolumeStatus { get; init; } = "unknown";
    [JsonPropertyName("local_file_status")]
    public string LocalFileStatus { get; init; } = "unknown";
    [JsonPropertyName("log_status")]
    public string LogStatus { get; init; } = "unknown";
    [JsonPropertyName("database_log_aggregate")]
    public PrtgStorageLogAggregate? DatabaseLogAggregate { get; init; }
    [JsonPropertyName("queries_attempted")]
    public int QueriesAttempted { get; init; }
    [JsonPropertyName("queries_succeeded")]
    public int QueriesSucceeded { get; init; }
    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }
    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }
    [JsonPropertyName("file_rows_truncated")]
    public bool FileRowsTruncated { get; init; }
    [JsonPropertyName("volume_rows_truncated")]
    public bool VolumeRowsTruncated { get; init; }
    [JsonPropertyName("file_capacities")]
    public IReadOnlyList<PrtgStorageCapacityRow> FileCapacities { get; init; } = Array.Empty<PrtgStorageCapacityRow>();
    [JsonPropertyName("volume_capacities")]
    public IReadOnlyList<PrtgStorageVolumeRow> VolumeCapacities { get; init; } = Array.Empty<PrtgStorageVolumeRow>();
}

public sealed record PrtgStorageCapacityRow(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("allocated_bytes")] long? AllocatedBytes,
    [property: JsonPropertyName("used_bytes")] long? UsedBytes,
    [property: JsonPropertyName("maximum_bytes")] long? MaximumBytes,
    [property: JsonPropertyName("maximum_kind")] string MaximumKind,
    [property: JsonPropertyName("growth_bytes")] long? GrowthBytes,
    [property: JsonPropertyName("growth_percent")] int? GrowthPercent);

public sealed record PrtgStorageVolumeRow(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("total_bytes")] long? TotalBytes,
    [property: JsonPropertyName("available_bytes")] long? AvailableBytes);

public sealed record PrtgStorageLogAggregate(
    [property: JsonPropertyName("allocated_bytes")] long? AllocatedBytes,
    [property: JsonPropertyName("used_bytes")] long? UsedBytes);

/// <summary>
/// 僅收集資料庫引擎、檔案與磁碟區中繼資料；不掃描應用程式資料列、不變更結構，也不輸出設定路徑。
/// 整體限制為十秒、每個 provider 最多三個中繼資料命令，檔案與磁碟區各最多回傳十六列。
/// </summary>
public static class PrtgStorageEnvironmentProbe
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(10);
    public const int MaximumMetadataQueries = 3;
    public const int MaximumRowsPerKind = 16;

    public static async Task<PrtgStorageEnvironmentFacts> CollectAsync(
        DbContext context,
        string? ownedDataRoot,
        CancellationToken cancellationToken = default)
    {
        return await CollectConnectionAsync(context.Database.ProviderName,
            context.Database.GetDbConnection(), ownedDataRoot, cancellationToken, MaximumDuration).ConfigureAwait(false);
    }

    internal static async Task<PrtgStorageEnvironmentFacts> CollectConnectionAsync(
        string? providerName,
        DbConnection connection,
        string? ownedDataRoot,
        CancellationToken cancellationToken,
        TimeSpan maximumDuration)
    {
        var provider = providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => "SqlServer",
            "Microsoft.EntityFrameworkCore.Sqlite" => "Sqlite",
            _ => "unknown"
        };
        if (provider == "unknown") return new PrtgStorageEnvironmentFacts();
        if (cancellationToken.IsCancellationRequested)
            return new PrtgStorageEnvironmentFacts { Provider = provider, Status = "cancelled", Cancelled = true };

        using var timeout = new CancellationTokenSource(maximumDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        var queriesAttempted = 0;
        var queriesSucceeded = 0;
        var files = new List<PrtgStorageCapacityRow>(MaximumRowsPerKind);
        var volumes = new List<PrtgStorageVolumeRow>(MaximumRowsPerKind);
        var fileRowsTruncated = false;
        var volumeRowsTruncated = false;
        var engineVersion = "unknown";
        var edition = "unknown";
        var engineEdition = "unknown";
        var localFileMeasured = false;
        PrtgStorageLogAggregate? logAggregate = null;
        var logQuerySucceeded = false;
        var timedOut = false;
        var cancelled = false;

        try
        {
            if (wasClosed)
                await connection.OpenAsync(linked.Token).ConfigureAwait(false);
            if (provider == "Sqlite")
            {
                var sqlite = await ReadSqliteAsync(connection, ownedDataRoot, linked.Token,
                    () => { queriesAttempted++; }, () => { queriesSucceeded++; }).ConfigureAwait(false);
                engineVersion = sqlite.Version;
                files.AddRange(sqlite.Files);
                volumes.AddRange(sqlite.Volumes);
                localFileMeasured = sqlite.LocalFileMeasured;
            }
            else
            {
                var sql = await ReadSqlServerAsync(connection, linked.Token,
                    () => { queriesAttempted++; }, () => { queriesSucceeded++; }).ConfigureAwait(false);
                engineVersion = sql.Version;
                edition = sql.Edition;
                engineEdition = sql.EngineEdition;
                files.AddRange(sql.Files);
                volumes.AddRange(sql.Volumes);
                fileRowsTruncated = sql.FilesTruncated;
                volumeRowsTruncated = sql.VolumesTruncated;
                logAggregate = sql.LogAggregate;
                logQuerySucceeded = sql.LogQuerySucceeded;
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2)
        {
            timedOut = true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            timedOut = true;
        }
        catch (Exception)
        {
            // Provider 錯誤（包含中繼資料權限不足）代表證據缺漏，不應讓整個探測失敗；
            // 不序列化例外訊息，避免洩漏路徑或主機資訊。
        }
        finally
        {
            if (wasClosed)
            {
                try { await connection.CloseAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        var complete = !timedOut && !cancelled && queriesSucceeded == queriesAttempted && queriesAttempted > 0 &&
                       files.Count > 0 && volumes.Count > 0;
        var any = queriesSucceeded > 0 || files.Count > 0 || volumes.Count > 0;
        return new PrtgStorageEnvironmentFacts
        {
            Status = cancelled ? "cancelled" : timedOut ? "timeout" : complete ? "measured" : any ? "partial" : "unknown",
            Provider = provider,
            EngineVersion = SafeVersion(engineVersion),
            Edition = SafeEdition(edition),
            EngineEdition = SafeEngineEdition(engineEdition),
            FileStatus = DetailStatus(files.Count, files.Any(row => row.AllocatedBytes.HasValue || row.UsedBytes.HasValue || row.MaximumBytes.HasValue), cancelled || timedOut),
            VolumeStatus = DetailStatus(volumes.Count, volumes.Any(row => row.TotalBytes.HasValue && row.AvailableBytes.HasValue), cancelled || timedOut),
            LocalFileStatus = provider == "Sqlite" ? DetailStatus(localFileMeasured ? 1 : 0, localFileMeasured, cancelled || timedOut) : "unknown",
            LogStatus = logQuerySucceeded && logAggregate is not null ? DetailStatus(1,
                logAggregate.AllocatedBytes.HasValue || logAggregate.UsedBytes.HasValue, cancelled || timedOut) : "unknown",
            DatabaseLogAggregate = logAggregate,
            QueriesAttempted = Math.Min(queriesAttempted, MaximumMetadataQueries),
            QueriesSucceeded = Math.Min(queriesSucceeded, MaximumMetadataQueries),
            TimedOut = timedOut,
            Cancelled = cancelled,
            FileRowsTruncated = fileRowsTruncated,
            VolumeRowsTruncated = volumeRowsTruncated,
            FileCapacities = files.Take(MaximumRowsPerKind).ToArray(),
            VolumeCapacities = volumes.Take(MaximumRowsPerKind).ToArray()
        };
    }

    private static async Task<(string Version, IReadOnlyList<PrtgStorageCapacityRow> Files,
        IReadOnlyList<PrtgStorageVolumeRow> Volumes, bool LocalFileMeasured)> ReadSqliteAsync(
        DbConnection connection, string? ownedDataRoot, CancellationToken cancellationToken,
        Action attempted, Action succeeded)
    {
        var version = "unknown";
        long? pageSize = null;
        long? pageCount = null;
        long? maxPageCount = null;
        if (connection is SqliteConnection sqliteConnection)
            version = sqliteConnection.ServerVersion;
        (pageSize, _) = await ReadPragmaInt64Async(connection, "page_size", cancellationToken, attempted, succeeded).ConfigureAwait(false);
        (pageCount, _) = await ReadPragmaInt64Async(connection, "page_count", cancellationToken, attempted, succeeded).ConfigureAwait(false);
        (maxPageCount, _) = await ReadPragmaInt64Async(connection, "max_page_count", cancellationToken, attempted, succeeded).ConfigureAwait(false);

        var files = new List<PrtgStorageCapacityRow>(3);
        var dbFile = connection is SqliteConnection sqlite ? sqlite.DataSource : null;
        if (pageSize is > 0 && pageCount is >= 0)
        {
            var allocated = MultiplyPages(pageCount, pageSize);
            var maximum = MultiplyPages(maxPageCount, pageSize);
            files.Add(new PrtgStorageCapacityRow("database-pages", allocated, null,
                maximum, maximum.HasValue ? "bounded" : "unknown", null, null));
        }
        var localFileMeasured = AddLocalFileLength(files, dbFile, "database-file");
        if (localFileMeasured && !string.IsNullOrWhiteSpace(dbFile) && dbFile != ":memory:")
        {
            AddLocalFileLength(files, dbFile + "-wal", "write-ahead-log");
            AddLocalFileLength(files, dbFile + "-shm", "shared-memory");
        }

        var volumes = new List<PrtgStorageVolumeRow>(1);
        AddOwnedLocalVolume(volumes, ownedDataRoot);
        return (version, files, volumes, localFileMeasured);
    }

    private static async Task<(long? Value, bool Read)> ReadPragmaInt64Async(
        DbConnection connection, string pragma, CancellationToken cancellationToken, Action attempted, Action succeeded)
    {
        if (pragma is not ("page_size" or "page_count" or "max_page_count"))
            throw new ArgumentOutOfRangeException(nameof(pragma));
        attempted();
        using var command = CreateCommand(connection, $"PRAGMA {pragma};", cancellationToken);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        succeeded();
        try { return (Convert.ToInt64(value, CultureInfo.InvariantCulture), true); }
        catch (Exception) { return (null, false); }
    }

    private static async Task<(string Version, string Edition, string EngineEdition, IReadOnlyList<PrtgStorageCapacityRow> Files,
        IReadOnlyList<PrtgStorageVolumeRow> Volumes, PrtgStorageLogAggregate? LogAggregate, bool LogQuerySucceeded,
        bool FilesTruncated, bool VolumesTruncated)> ReadSqlServerAsync(
        DbConnection connection, CancellationToken cancellationToken, Action attempted, Action succeeded)
    {
        var version = "unknown";
        var edition = "unknown";
        var engineEdition = "unknown";
        var files = new List<PrtgStorageCapacityRow>(MaximumRowsPerKind);
        var fileTruncated = false;
        attempted();
        try
        {
            using var command = CreateCommand(connection,
                "SELECT TOP (17) CONVERT(nvarchar(32), SERVERPROPERTY('ProductVersion')), " +
                "CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), " +
                "CONVERT(int, SERVERPROPERTY('EngineEdition')), type_desc, size, max_size, growth, is_percent_growth, " +
                "CASE WHEN type = 0 THEN FILEPROPERTY(name, 'SpaceUsed') ELSE NULL END " +
                "FROM sys.database_files " +
                "ORDER BY file_id;", cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var seen = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen++ >= MaximumRowsPerKind) { fileTruncated = true; break; }
                if (seen == 1)
                {
                    version = BoundedString(reader, 0, 32);
                    edition = SafeEdition(BoundedString(reader, 1, 128));
                    engineEdition = BoundedString(reader, 2, 16);
                }
                var role = SafeFileRole(BoundedString(reader, 3, 16));
                var allocated = MultiplyPages(NullableInt64(reader, 4), 8192);
                var maxPages = NullableInt64(reader, 5);
                long? maximum = maxPages switch
                {
                    -1 => null,
                    0 => allocated,
                    > 0 => MultiplyPages(maxPages, 8192),
                    _ => null
                };
                var growth = NullableInt64(reader, 6);
                var percent = !reader.IsDBNull(7) && Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture);
                var usedPages = NullableInt64(reader, 8);
                files.Add(new PrtgStorageCapacityRow(role, allocated, MultiplyPages(usedPages, 8192),
                    maximum, maxPages switch
                    {
                        -1 => "unbounded",
                        0 => "fixed-at-current",
                        > 0 => maximum.HasValue ? "bounded" : "unknown",
                        _ => "unknown"
                    },
                    !percent ? MultiplyPages(growth, 8192) : null, percent && growth is >= 0 and <= 100 ? (int)growth.Value : null));
            }
            if (seen > 0) succeeded();
        }
        catch (OperationCanceledException) { throw; }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2) { throw new TimeoutException(); }
        catch (Exception) { }

        PrtgStorageLogAggregate? logAggregate = null;
        var logQuerySucceeded = false;
        attempted();
        try
        {
            using var command = CreateCommand(connection,
                "SELECT total_log_size_in_bytes, used_log_space_in_bytes FROM sys.dm_db_log_space_usage;", cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var allocatedBytes = NullableInt64(reader, 0);
                var usedBytes = NullableInt64(reader, 1);
                if (allocatedBytes is >= 0 || usedBytes is >= 0)
                    logAggregate = new PrtgStorageLogAggregate(allocatedBytes is >= 0 ? allocatedBytes : null,
                        usedBytes is >= 0 ? usedBytes : null);
                succeeded();
                logQuerySucceeded = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2) { throw new TimeoutException(); }
        catch (Exception) { }

        var volumes = new List<PrtgStorageVolumeRow>(MaximumRowsPerKind);
        var volumeTruncated = false;
        attempted();
        try
        {
            using var command = CreateCommand(connection,
                "SELECT TOP (17) mf.type_desc, vs.total_bytes, vs.available_bytes " +
                "FROM sys.database_files AS f " +
                "JOIN sys.master_files AS mf ON mf.database_id = DB_ID() AND mf.file_id = f.file_id " +
                "CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs " +
                "ORDER BY mf.file_id;", cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var seen = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen++ >= MaximumRowsPerKind) { volumeTruncated = true; break; }
                volumes.Add(new PrtgStorageVolumeRow(SafeFileRole(BoundedString(reader, 0, 16)),
                    NullableInt64(reader, 1), NullableInt64(reader, 2)));
            }
            succeeded();
        }
        catch (OperationCanceledException) { throw; }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2) { throw new TimeoutException(); }
        catch (Exception) { }
        return (version, edition, engineEdition, files, volumes, logAggregate, logQuerySucceeded,
            fileTruncated, volumeTruncated);
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 3;
        return command;
    }

    private static string BoundedString(DbDataReader reader, int ordinal, int maxLength)
    {
        if (reader.IsDBNull(ordinal)) return "unknown";
        var value = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static long? NullableInt64(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try { return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }
    }

    private static long? MultiplyPages(long? pages, long? pageSize)
    {
        if (!pages.HasValue || !pageSize.HasValue || pages.Value < 0 || pageSize.Value <= 0 ||
            pages.Value > long.MaxValue / pageSize.Value) return null;
        return pages.Value * pageSize.Value;
    }

    private static string SafeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        return System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{1,3}(?:\.\d{1,5}){1,3}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant) ? version : "unknown";
    }

    private static string SafeEdition(string? edition)
    {
        if (string.IsNullOrWhiteSpace(edition)) return "unknown";
        var value = edition.Trim();
        foreach (var known in new[] { "Enterprise", "Standard", "Developer", "Express", "Web", "Evaluation", "Azure" })
            if (value.Contains(known, StringComparison.OrdinalIgnoreCase)) return known;
        return "unknown";
    }

    private static string SafeEngineEdition(string? value) => value?.Trim() switch
    {
        "1" => "personal-or-desktop",
        "2" => "standard",
        "3" => "enterprise",
        "4" => "express",
        "5" => "azure-sql-database",
        "6" => "azure-synapse",
        "8" => "azure-sql-managed-instance",
        _ => "unknown"
    };

    private static string DetailStatus(int count, bool hasMeasuredBytes, bool interrupted) => count == 0 || !hasMeasuredBytes
        ? "unknown"
        : interrupted ? "partial" : "measured";

    private static string SafeFileRole(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        "ROWS" or "DATA" => "data",
        "LOG" => "log",
        _ => "unknown"
    };

    private static bool AddLocalFileLength(List<PrtgStorageCapacityRow> files, string? path, string role)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && path != ":memory:" && IsLocalFixedPath(path) && File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (length >= 0)
                {
                    files.Add(new PrtgStorageCapacityRow(role, length, null, null, "unknown", null, null));
                    return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    private static void AddOwnedLocalVolume(List<PrtgStorageVolumeRow> volumes, string? ownedDataRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ownedDataRoot)) return;
            var full = Path.GetFullPath(ownedDataRoot);
            if (!IsLocalFixedPath(full)) return;
            var root = Path.GetPathRoot(full)!;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;
            volumes.Add(new PrtgStorageVolumeRow("owned-data-root", drive.TotalSize, drive.AvailableFreeSpace));
        }
        catch (Exception) { }
    }

    internal static bool IsLocalFixedPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
                root.StartsWith("//", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception) { return false; }
    }
}
