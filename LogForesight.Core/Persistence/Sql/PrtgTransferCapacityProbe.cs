using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>依目前 SQL 檔案與記錄檔所在磁碟的實際餘裕，保守估算可新增的診斷 payload。</summary>
public sealed class PrtgTransferCapacityProbe(StorageBackend backend) : IPrtgTransferCapacityProvider
{
    private const long ReservePercent = 25;
    private const long WriteAmplification = 4;
    private readonly StorageBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public PrtgTransferCapacitySnapshot Capture()
    {
        try
        {
            using var db = _backend.CreateContext();
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose) connection.Open();
            try
            {
                var facts = db.Database.IsSqlite()
                    ? new CapacityFacts(ReadSqliteVolumes(connection), long.MaxValue, long.MaxValue)
                    : db.Database.IsSqlServer()
                        ? ReadSqlServerVolumes(connection)
                        : throw new InvalidOperationException("目前資料庫 provider 沒有可核實的容量探測方式。");
                var volumes = facts.Volumes.GroupBy(x => x.VolumeRoot, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new VolumeFact(group.Key,
                        group.Min(x => x.TotalBytes), group.Min(x => x.AvailableBytes)))
                    .ToArray();
                if (volumes.Length == 0 || volumes.Any(x => x.TotalBytes <= 0 || x.AvailableBytes < 0))
                    throw new InvalidOperationException("無法取得資料庫資料與交易記錄所在磁碟的容量資料。");

                var safe = Math.Min(volumes.Min(volume => SafePayloadBytes(volume.TotalBytes, volume.AvailableBytes)),
                    Math.Min(facts.DataFileRoomBytes, facts.LogFileRoomBytes) / WriteAmplification);
                return new PrtgTransferCapacitySnapshot(true, safe,
                    "runtime-sql-and-log-volumes", null);
            }
            finally
            {
                if (shouldClose) connection.Close();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new PrtgTransferCapacitySnapshot(false, 0, null,
                $"無法可靠確認資料庫資料與交易記錄磁碟餘裕，暫不接受傳輸（{ex.GetType().Name}）。");
        }
    }

    private static List<VolumeFact> ReadSqliteVolumes(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list";
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            var databaseName = reader.GetString(1);
            var path = reader.IsDBNull(2) ? "" : reader.GetString(2);
            if (databaseName.Equals("temp", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(path))
                continue;
            if (string.IsNullOrWhiteSpace(path) || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite 資料庫不是可量測的持久檔案。");
            paths.Add(path);
        }
        if (paths.Count == 0) throw new InvalidOperationException("SQLite 未回報持久資料檔。");
        return paths.Select(ReadLocalVolume).ToList();
    }

    private static CapacityFacts ReadSqlServerVolumes(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT files.type_desc, volume.volume_mount_point, volume.total_bytes, volume.available_bytes,
                   files.size, files.max_size, files.growth, files.is_percent_growth,
                   FILEPROPERTY(files.name, 'SpaceUsed') AS space_used_pages,
                   logspace.total_log_size_in_bytes, logspace.used_log_space_in_bytes
            FROM sys.database_files AS files
            CROSS APPLY sys.dm_os_volume_stats(DB_ID(), files.file_id) AS volume
            CROSS JOIN sys.dm_db_log_space_usage AS logspace
            WHERE files.type_desc IN ('ROWS', 'LOG')
            """;
        using var reader = command.ExecuteReader();
        var facts = new List<VolumeFact>();
        long dataRoom = 0;
        long logGrowthRoom = 0;
        long logFreeBytes = 0;
        while (reader.Read())
        {
            var fileType = reader.GetString(0);
            var mount = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(mount))
                throw new InvalidOperationException("SQL Server 未提供資料或交易記錄檔的磁碟掛載點。");
            var total = reader.GetInt64(2);
            var available = reader.GetInt64(3);
            facts.Add(new VolumeFact(mount, total, available));
            var sizePages = Convert.ToInt64(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture);
            var maxPages = Convert.ToInt64(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture);
            var growth = Convert.ToInt64(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture);
            var percentGrowth = reader.GetBoolean(7);
            var spaceUsedPages = reader.IsDBNull(8) ? sizePages : Convert.ToInt64(reader.GetValue(8), System.Globalization.CultureInfo.InvariantCulture);
            var currentPagesFree = Math.Max(0, sizePages - spaceUsedPages);
            var growthPages = growth <= 0 ? 0 : maxPages < 0 ? long.MaxValue : Math.Max(0, maxPages - sizePages);
            if (percentGrowth && growth > 0 && maxPages < 0) growthPages = long.MaxValue;
            var fileRoom = AddSaturated(PagesToBytes(currentPagesFree), PagesToBytes(growthPages));
            if (fileType == "ROWS") dataRoom = AddSaturated(dataRoom, fileRoom);
            else logGrowthRoom = AddSaturated(logGrowthRoom, PagesToBytes(growthPages));
            logFreeBytes = Math.Max(logFreeBytes,
                Math.Max(0, reader.GetInt64(9) - reader.GetInt64(10)));
        }
        if (facts.Count == 0)
            throw new InvalidOperationException("SQL Server 未回報資料與交易記錄檔的磁碟容量；請確認 DMV 權限。");
        return new CapacityFacts(facts, dataRoom, AddSaturated(logFreeBytes, logGrowthRoom));
    }

    private static VolumeFact ReadLocalVolume(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("無法解析資料庫檔案所在磁碟。");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.TotalSize <= 0 || drive.AvailableFreeSpace < 0)
            throw new InvalidOperationException("資料庫檔案所在磁碟尚未就緒或無法量測。");
        return new VolumeFact(root, drive.TotalSize, drive.AvailableFreeSpace);
    }

    private static long SafePayloadBytes(long totalBytes, long availableBytes)
    {
        var reserved = (long)Math.Ceiling(totalBytes * (ReservePercent / 100d));
        var writable = Math.Max(0, availableBytes - reserved);
        return writable / WriteAmplification;
    }

    private static long PagesToBytes(long pages) => pages == long.MaxValue ? long.MaxValue : checked(pages * 8192);

    private static long AddSaturated(long left, long right) =>
        left == long.MaxValue || right == long.MaxValue || left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed record VolumeFact(string VolumeRoot, long TotalBytes, long AvailableBytes);
    private sealed record CapacityFacts(List<VolumeFact> Volumes, long DataFileRoomBytes, long LogFileRoomBytes);
}
