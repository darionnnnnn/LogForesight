using System.Text.Json;
using System.Data;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

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
                        // Bound full JSON residency independently of the small receipt batch.
                        var parentIds = acceptedHostIds.ToArray();
                        long cursor = 0;
                        while (parentIds.Length > 0)
                        {
                            var parents = ctx.DailyRecords.Where(row => parentIds.Contains(row.HostId) &&
                                row.RecordDate == date && !row.DetailPruned && row.RecordId > cursor)
                                .OrderBy(row => row.RecordId).Take(16).ToList();
                            if (parents.Count == 0) break;
                            foreach (var parent in parents)
                            {
                                var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(parent.ContentJson)
                                    ?? throw new InvalidDataException("NetIQ parent contains invalid data.");
                                if (record.LogSource != AnalysisLogSource.Netiq) continue;
                                // Preserve the existing parent retry contract: cancellation is a
                                // failed fetch for qualification, while the durable receipt says stopped.
                                record.LatestNetiqAttemptStatus = status == "stopped" ? "failed" : status;
                                record.LatestNetiqAttemptAtUtc = at;
                                parent.ContentJson = JsonSerializer.Serialize(record);
                            }
                            cursor = parents[^1].RecordId;
                            ctx.SaveChanges();
                            ctx.ChangeTracker.Clear();
                            if (parents.Count < 16) break;
                        }
                    }
                }
                tx.Commit();
            });
        }
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
