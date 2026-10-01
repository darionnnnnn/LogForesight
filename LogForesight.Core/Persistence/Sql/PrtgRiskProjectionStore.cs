using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>明確的歷史投影遷移入口。SQL 風險欄與有效 JSON 同交易更新，原始內容保留到詳情保留期。</summary>
public sealed class PrtgRiskProjectionStore(Func<LfDbContext> contextFactory)
{
    public PrtgRiskReviewCounts Counts(IReadOnlyCollection<long> visibleHostIds)
    {
        using var ctx = contextFactory();
        var rows = ctx.DailyRecords.AsNoTracking().Where(r => visibleHostIds.Contains(r.HostId));
        return new(rows.Count(r => r.RiskProjectionVersion < PrtgHistoricalRiskReview.Version),
            rows.Count(r => r.RiskReviewStatus == "pending" || r.RiskReviewStatus == "unavailable"),
            rows.Count(r => r.RiskReviewStatus == "revised"));
    }

    public int RunBatch(IReadOnlyCollection<long>? visibleHostIds = null, int batchSize = 100)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        using var probe = contextFactory();
        var query = probe.DailyRecords.AsNoTracking().Where(r => r.RiskProjectionVersion < PrtgHistoricalRiskReview.Version);
        if (visibleHostIds != null) query = query.Where(r => visibleHostIds.Contains(r.HostId));
        var candidates = query.OrderBy(r => r.RecordId).Select(r => new { r.RecordId, r.HostId, r.RecordDate }).Take(batchSize).ToList();
        var count = 0;
        foreach (var candidate in candidates)
        {
            lock (EfAnalysisRecordStore.LockFor(candidate.HostId, candidate.RecordDate))
            {
                using var strategyContext = contextFactory();
                strategyContext.Database.CreateExecutionStrategy().Execute(() =>
                {
                    using var ctx = contextFactory();
                    using var tx = ctx.Database.BeginTransaction();
                    var row = ctx.DailyRecords.SingleOrDefault(r => r.RecordId == candidate.RecordId);
                    if (row == null || row.RiskProjectionVersion >= PrtgHistoricalRiskReview.Version) return;
                    DailyAnalysisRecord? record = null;
                    if (!row.DetailPruned && !string.IsNullOrWhiteSpace(row.ContentJson))
                    {
                        try { record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson); }
                        catch (JsonException) { /* 無法重建時標未知，不用空紀錄降級。 */ }
                    }
                    row.RiskReviewStatus = record == null ? "unavailable" : "current";
                    if (record != null && PrtgHistoricalRiskReview.Apply(record))
                    {
                        row.OriginalRiskContentJson ??= row.ContentJson;
                        row.ContentJson = JsonSerializer.Serialize(record);
                        row.RiskLevel = record.RiskLevel;
                        row.Headline = record.Headline;
                        row.HasCorrelation = record.CorrelationAlerts.Count > 0;
                        row.AiAnalyzed = record.AiAnalyzed;
                        row.AiPending = record.AiPending;
                        row.RiskReviewStatus = record.RiskReview!.Status;
                    }
                    row.RiskProjectionVersion = PrtgHistoricalRiskReview.Version;
                    ctx.SaveChanges();
                    tx.Commit();
                });
                count++;
            }
        }
        return count;
    }
}

public sealed record PrtgRiskReviewCounts(int UncheckedRecords, int PendingReviewRecords, int RevisedRecords);
