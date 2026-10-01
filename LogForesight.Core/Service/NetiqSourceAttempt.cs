using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Service;

/// <summary>重試失敗保留原分析與人工處置，但不能把原成功冒稱最新嘗試成功。</summary>
public static class NetiqSourceAttempt
{
    public static void MarkExistingBatch(StorageBackend backend, IReadOnlyCollection<long> hostIds, DateTime day, string status)
    {
        using var ctx = backend.CreateContext();
        var existing = ctx.DailyRecords.AsNoTracking().Where(r => hostIds.Contains(r.HostId) && r.RecordDate == day.Date)
            .Select(r => r.HostId).Distinct().ToArray();
        foreach (var id in existing) Mark(backend, id, day, status);
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
