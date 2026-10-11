using System.Text.Json;
using System.Data;
using System.Data.Common;
using System.Globalization;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LogForesight.Core.Service;

/// <summary>重試失敗保留原分析與人工處置，但不能把原成功冒稱最新嘗試成功。</summary>
public static class NetiqSourceAttempt
{
    private const string LogPrefix = "netiq_hostday_attempt_";
    private const int MaxHostsPerDayRead = 10_000;
    private const int MaxLineChars = 4096;
    private static readonly HashSet<string> KnownStatuses = new(StringComparer.Ordinal)
        { "running", "success", "partial", "retained-incomplete", "failed", "stopped" };
    private static readonly object WriteLock = new();

    public sealed record Receipt(long RunId, long HostId, DateTime Date, string Status, DateTime AttemptAtUtc);

    /// <summary>Atomically records attempts and disqualifies existing NetIQ parents for non-success attempts.
    /// A success receipt never qualifies an old parent; qualification requires a successfully saved replacement.</summary>
    public static void RecordBatch(StorageBackend backend, IReadOnlyCollection<long> hostIds, DateTime day,
        long runId, string status, DateTime? attemptAtUtc = null)
    {
        if (!KnownStatuses.Contains(status)) throw new ArgumentException("Unknown NetIQ attempt status.", nameof(status));
        if (hostIds.Count == 0) return;
        var date = day.Date;
        var at = attemptAtUtc ?? DateTime.UtcNow;
        if (at.Kind != DateTimeKind.Utc) throw new ArgumentException("Attempt timestamp must be UTC.", nameof(attemptAtUtc));
        if (at > DateTime.UtcNow.AddMinutes(5)) throw new ArgumentOutOfRangeException(nameof(attemptAtUtc), "Attempt timestamp cannot be in the future.");
        var ids = hostIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return; // e.g. recorder startup without an assigned run ID; never block source processing.
        lock (WriteLock)
        {
            using var probe = backend.CreateContext();
            var strategy = probe.Database.CreateExecutionStrategy();
            strategy.Execute(() =>
            {
                using var ctx = backend.CreateContext();
                using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
                foreach (var idChunk in ids.Chunk(500))
                {
                    var keyToReceipt = idChunk.ToDictionary(id => LogKey(date, id), id => new Receipt(runId, id, date, status, at));
                    var keys = keyToReceipt.Keys.ToArray();
                    var lastSeqs = ctx.LogLines.AsNoTracking().Where(row => keys.Contains(row.LogKey))
                        .GroupBy(row => row.LogKey).Select(group => new { LogKey = group.Key, Seq = group.Max(row => row.Seq) });
                    var existing = (from row in ctx.LogLines.AsNoTracking()
                                    join latestSeq in lastSeqs on new { row.LogKey, row.Seq } equals new { latestSeq.LogKey, latestSeq.Seq }
                                    select new { row.LogKey, row.Seq, Length = row.Line.Length, Line = row.Line.Substring(0, MaxLineChars + 1) })
                        .ToList().ToDictionary(row => row.LogKey);
                    var acceptedHostIds = new List<long>(idChunk.Length);
                    foreach (var (key, receipt) in keyToReceipt)
                    {
                        var line = JsonSerializer.Serialize(receipt);
                        if (existing.TryGetValue(key, out var row))
                        {
                            if (row.Length > MaxLineChars) throw new InvalidDataException("NetIQ attempt ledger receipt is oversized.");
                            Receipt? previous;
                            try { previous = JsonSerializer.Deserialize<Receipt>(row.Line); }
                            catch (JsonException ex) { throw new InvalidDataException("NetIQ attempt ledger contains malformed data.", ex); }
                            if (previous == null || !KnownStatuses.Contains(previous.Status) || previous.HostId != receipt.HostId || previous.Date.Date != date)
                                throw new InvalidDataException("NetIQ attempt ledger contains an invalid or unknown receipt.");
                            if (previous.RunId > receipt.RunId || (previous.RunId == receipt.RunId && previous.AttemptAtUtc > receipt.AttemptAtUtc)) continue;
                            var update = new LogLineRow { Seq = row.Seq, LogKey = key, Line = line, CreatedAt = DateTime.Now };
                            ctx.Attach(update);
                            ctx.Entry(update).Property(entity => entity.Line).IsModified = true;
                            ctx.Entry(update).Property(entity => entity.CreatedAt).IsModified = true;
                        }
                        else
                        {
                            ctx.LogLines.Add(new LogLineRow { LogKey = key, Line = line, CreatedAt = DateTime.Now });
                        }
                        acceptedHostIds.Add(receipt.HostId);
                    }
                    ctx.SaveChanges();
                    ctx.ChangeTracker.Clear();
                    if (status != "success")
                    {
                        // Keep the receipt and parent eligibility in the same transaction, including
                        // across 500-host ledger chunks. SQL isolation and WriteRevision also fence
                        // concurrent parent writers; no process-only host lock can cover other workers.
                        var parentIds = acceptedHostIds.ToArray();
                        long cursor = 0;
                        while (parentIds.Length > 0)
                        {
                            var parents = ReadParentMetadataPage(ctx, parentIds, date, cursor);
                            if (parents.Count == 0) break;
                            foreach (var parent in parents)
                            {
                                if (!parent.JsonValid || !parent.IsObject)
                                    throw new JsonException("NetIQ parent content must be a valid JSON object.");
                                if (parent.SourceCount == 0) continue; // omitted enum defaults to Unknown.
                                if (parent.SourceCount != 1 || parent.SourceValid != 1 || !parent.SourceEnum.HasValue)
                                    throw new JsonException("NetIQ parent LogSource must be one legal numeric enum value.");
                                if (parent.SourceEnum.Value != (int)AnalysisLogSource.Netiq) continue;
                                if (parent.AttemptStatusCount > 1 || parent.AttemptAtCount > 1)
                                    throw new JsonException("NetIQ parent contains duplicate attempt metadata properties.");
                                // Preserve the existing parent retry contract: cancellation is a
                                // failed fetch for qualification, while the durable receipt says stopped.
                                PatchParentAttempt(ctx, parent, status == "stopped" ? "failed" : status, at);
                            }
                            cursor = parents[^1].RecordId;
                            if (parents.Count < 16) break;
                        }
                    }
                }
                tx.Commit();
            });
        }
    }

    private sealed class ParentMetadataRow
    {
        public long RecordId { get; set; }
        public long WriteRevision { get; set; }
        public int JsonValid { get; set; }
        public int IsObject { get; set; }
        public int SourceCount { get; set; }
        public int SourceValid { get; set; }
        public int? SourceEnum { get; set; }
        public int AttemptStatusCount { get; set; }
        public int AttemptAtCount { get; set; }
    }

    private sealed record ParentMetadata(long RecordId, long WriteRevision, bool JsonValid, bool IsObject,
        int SourceCount, int SourceValid, int? SourceEnum, int AttemptStatusCount, int AttemptAtCount);

    /// <summary>Read only bounded identity/revision/source metadata; never materialize either JSON LOB.</summary>
    private static List<ParentMetadata> ReadParentMetadataPage(LfDbContext ctx, long[] hostIds, DateTime date, long cursor)
    {
        var sqlite = ctx.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        var sqlServer = ctx.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
        if (!sqlite && !sqlServer) throw new NotSupportedException("NetIQ parent patching supports SQLite and SQL Server.");

        var valid = sqlite ? "json_valid(p.content_json)" : "ISJSON(p.content_json)";
        var objectPrefix = "LEFT(LTRIM(REPLACE(REPLACE(REPLACE(p.content_json, CHAR(9), N''), " +
                           "CHAR(10), N''), CHAR(13), N'')), 1)";
        var objectCheck = sqlite
            ? $"CASE WHEN {valid} = 1 THEN json_type(p.content_json, '$') = 'object' ELSE 0 END"
            : $"({valid} = 1 AND {objectPrefix} = N'{{')";
        var safeJson = $"CASE WHEN {valid} = 1 AND {objectCheck} THEN p.content_json ELSE " + (sqlite ? "'{}'" : "N'{}'") + " END";
        var sourceCount = sqlite
            ? $"MIN(2, (SELECT COUNT(*) FROM json_each({safeJson}) WHERE key = 'LogSource'))"
            : "CASE WHEN source.source_count > 1 THEN 2 ELSE CONVERT(int, source.source_count) END";
        var sourceValid = sqlite
            ? $"CASE WHEN {sourceCount} = 1 AND json_type({safeJson}, '$.LogSource') = 'integer' " +
              $"AND json_extract({safeJson}, '$.LogSource') IN (0, 1, 2) THEN 1 ELSE 0 END"
            : "COALESCE(source.source_valid, 0)";
        var sourceEnum = sqlite
            ? $"CASE WHEN {sourceValid} = 1 THEN CAST(json_extract({safeJson}, '$.LogSource') AS INTEGER) END"
            : "source.source_enum";
        var attemptStatusCount = sqlite
            ? $"MIN(2, (SELECT COUNT(*) FROM json_each({safeJson}) WHERE key = 'LatestNetiqAttemptStatus'))"
            : "CASE WHEN source.attempt_status_count > 1 THEN 2 ELSE CONVERT(int, source.attempt_status_count) END";
        var attemptAtCount = sqlite
            ? $"MIN(2, (SELECT COUNT(*) FROM json_each({safeJson}) WHERE key = 'LatestNetiqAttemptAtUtc'))"
            : "CASE WHEN source.attempt_at_count > 1 THEN 2 ELSE CONVERT(int, source.attempt_at_count) END";
        var sourceApply = sqlite
            ? ""
            : " OUTER APPLY (SELECT COUNT_BIG(CASE WHEN [key] = N'LogSource' THEN 1 END) AS source_count, " +
              "MAX(CASE WHEN [key] = N'LogSource' AND [type] = 2 AND [value] IN (N'0', N'1', N'2') THEN TRY_CONVERT(int, [value]) END) AS source_enum, " +
              "MAX(CASE WHEN [key] = N'LogSource' AND [type] = 2 AND [value] IN (N'0', N'1', N'2') THEN 1 ELSE 0 END) AS source_valid, " +
              "COUNT_BIG(CASE WHEN [key] = N'LatestNetiqAttemptStatus' THEN 1 END) AS attempt_status_count, " +
              "COUNT_BIG(CASE WHEN [key] = N'LatestNetiqAttemptAtUtc' THEN 1 END) AS attempt_at_count " +
              $"FROM OPENJSON({safeJson})) source";
        var hostScope = sqlite
            ? "p.host_id IN (SELECT CAST(value AS INTEGER) FROM json_each(@hostIds))"
            : "p.host_id IN (SELECT TRY_CONVERT(bigint, [value]) FROM OPENJSON(@hostIds))";
        var pageLimit = sqlite ? "LIMIT @pageSize" : "OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY";
        var table = sqlite ? "\"lf_daily_records\"" : "[lf_daily_records]";
        var query = $"SELECT p.record_id AS RecordId, p.write_revision AS WriteRevision, " +
                    $"CASE WHEN {valid} = 1 THEN 1 ELSE 0 END AS JsonValid, " +
                    $"CASE WHEN {objectCheck} THEN 1 ELSE 0 END AS IsObject, {sourceCount} AS SourceCount, " +
                    $"{sourceValid} AS SourceValid, {sourceEnum} AS SourceEnum, " +
                    $"{attemptStatusCount} AS AttemptStatusCount, {attemptAtCount} AS AttemptAtCount " +
                    "FROM " + table + " p" + sourceApply + " WHERE " + hostScope + " " +
                    "AND p.record_date = @recordDate AND p.detail_pruned = 0 AND p.record_id > @cursor " +
                    $"ORDER BY p.record_id {pageLimit}";
        // EF formats Raw SQL even with named DbParameters; escape the JSON object
        // literals so they remain SQL text rather than composite-format placeholders.
        var rawSqlFormat = query.Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
        var rows = ctx.Database.SqlQueryRaw<ParentMetadataRow>(rawSqlFormat,
            CreateParameters(ctx, ("@hostIds", JsonSerializer.Serialize(hostIds)), ("@recordDate", date),
                ("@cursor", cursor), ("@pageSize", 16))).ToList();
        return rows.Select(row => new ParentMetadata(row.RecordId, row.WriteRevision, row.JsonValid != 0,
            row.IsObject != 0, row.SourceCount, row.SourceValid, row.SourceEnum,
            row.AttemptStatusCount, row.AttemptAtCount)).ToList();
    }

    private static void PatchParentAttempt(LfDbContext ctx, ParentMetadata parent, string status, DateTime at)
    {
        var sqlite = ctx.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        var table = sqlite ? "\"lf_daily_records\"" : "[lf_daily_records]";
        var sql = sqlite
            ? $"UPDATE {table} SET content_json = json_set(json_set(content_json, '$.LatestNetiqAttemptStatus', @status), " +
              "'$.LatestNetiqAttemptAtUtc', @attemptAt), write_revision = write_revision + 1 " +
              "WHERE record_id = @recordId AND write_revision = @revision AND detail_pruned = 0"
            : $"UPDATE {table} SET content_json = JSON_MODIFY(JSON_MODIFY(content_json, '$.LatestNetiqAttemptStatus', @status), " +
              "'$.LatestNetiqAttemptAtUtc', @attemptAt), write_revision = write_revision + 1 " +
              "WHERE record_id = @recordId AND write_revision = @revision AND detail_pruned = 0";
        var affected = ctx.Database.ExecuteSqlRaw(sql, CreateParameters(ctx,
            ("@status", status), ("@attemptAt", at.ToString("O", CultureInfo.InvariantCulture)),
            ("@recordId", parent.RecordId), ("@revision", parent.WriteRevision)));
        if (affected != 1)
            throw new DBConcurrencyException("NetIQ parent revision changed during attempt invalidation.");
    }

    private static DbParameter[] CreateParameters(LfDbContext ctx, params (string Name, object Value)[] values)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        return values.Select(value =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.Value = value.Value;
            return parameter;
        }).ToArray();
    }

    /// <summary>One bounded SQL query per requested date; query count depends on days, never hosts.</summary>
    public static IReadOnlyDictionary<(long HostId, DateTime Date), Receipt> ReadLatest(
        StorageBackend backend, DateTime from, DateTime to)
    {
        var first = from.Date;
        var last = to.Date;
        if (last < first) return new Dictionary<(long, DateTime), Receipt>();
        using var ctx = backend.CreateContext();
        var latest = new Dictionary<(long HostId, DateTime Date), Receipt>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var prefix = LogDatePrefix(date);
            var matches = ctx.LogLines.AsNoTracking().Where(row => row.LogKey.StartsWith(prefix));
            var lastSeqs = matches.GroupBy(row => row.LogKey)
                .Select(group => new { LogKey = group.Key, Seq = group.Max(row => row.Seq) });
            var rows = (from row in matches
                        join latestSeq in lastSeqs on new { row.LogKey, row.Seq } equals new { latestSeq.LogKey, latestSeq.Seq }
                        orderby row.Seq descending
                        select new { row.LogKey, Length = row.Line.Length, Line = row.Line.Substring(0, MaxLineChars + 1) })
                .Take(MaxHostsPerDayRead + 1).ToList();
            if (rows.Count > MaxHostsPerDayRead) throw new InvalidDataException("NetIQ attempt ledger exceeds the per-day host limit.");
            foreach (var row in rows)
            {
                if (row.Length > MaxLineChars) throw new InvalidDataException("NetIQ attempt ledger receipt is oversized.");
                Receipt receipt;
                try { receipt = JsonSerializer.Deserialize<Receipt>(row.Line) ?? throw new JsonException(); }
                catch (JsonException ex) { throw new InvalidDataException("NetIQ attempt ledger contains malformed data.", ex); }
                if (receipt.HostId <= 0 || !KnownStatuses.Contains(receipt.Status) || receipt.Date.Date != date ||
                    row.LogKey != LogKey(receipt.Date.Date, receipt.HostId) || receipt.AttemptAtUtc.Kind != DateTimeKind.Utc ||
                    receipt.AttemptAtUtc > DateTime.UtcNow.AddMinutes(5))
                    throw new InvalidDataException("NetIQ attempt ledger contains an invalid or unknown receipt.");
                if (receipt.RunId <= 0) receipt = receipt with { Status = "unknown" };
                latest[(receipt.HostId, date)] = receipt;
            }
        }
        return latest;
    }

    private static string LogDatePrefix(DateTime date) => LogPrefix + date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "_";
    public static string LogKey(DateTime date, long hostId) => LogDatePrefix(date) + hostId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static int Prune(StorageBackend backend, int retentionDays)
    {
        var cutoffDate = DateTime.Today.AddDays(-Math.Max(1, retentionDays)).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        const int maxRows = 200_000;
        const int batchSize = 5_000;
        var total = 0;
        using var ctx = backend.CreateContext();
        while (total < maxRows)
        {
            var seqs = ctx.LogLines.AsNoTracking().Where(row => row.LogKey.StartsWith(LogPrefix) &&
                    row.LogKey.Substring(LogPrefix.Length, 8).CompareTo(cutoffDate) < 0)
                .OrderBy(row => row.Seq).Select(row => row.Seq).Take(Math.Min(batchSize, maxRows - total)).ToArray();
            if (seqs.Length == 0) break;
            total += ctx.LogLines.Where(row => seqs.Contains(row.Seq)).ExecuteDelete();
        }
        return total;
    }

    public static void Mark(StorageBackend backend, long hostId, DateTime day, string status)
    {
        lock (EfAnalysisRecordStore.LockFor(hostId, day))
        {
            using var ctx = backend.CreateContext();
            var row = ctx.DailyRecords.SingleOrDefault(r => r.HostId == hostId && r.RecordDate == day.Date);
            if (row == null || row.DetailPruned) return;
            var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson);
            if (record == null || record.LogSource != AnalysisLogSource.Netiq) return;
            record.LatestNetiqAttemptStatus = status; record.LatestNetiqAttemptAtUtc = DateTime.UtcNow;
            row.ContentJson = JsonSerializer.Serialize(record);
            ctx.SaveChanges();
        }
    }
}
