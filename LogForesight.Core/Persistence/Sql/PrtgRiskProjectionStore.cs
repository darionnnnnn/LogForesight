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

    /// <summary>處理過的候選數（迴圈終止條件用）。</summary>
    public int RunBatch(IReadOnlyCollection<long>? visibleHostIds = null, int batchSize = 100) =>
        RunBatchDetailed(visibleHostIds, batchSize).Processed;

    /// <summary>同時回報處理過的候選數與會影響查詢快取內容的列數（後者大於 0 才需推進資料版本戳）：
    /// 內容被改寫，或「是否待重評」（pending／unavailable，儀表板待重評天數與精簡列的重評提示讀它）改變。
    /// 只推進投影版本欄、或由未檢查變成已檢查的列不算。</summary>
    public PrtgRiskBatchResult RunBatchDetailed(IReadOnlyCollection<long>? visibleHostIds = null, int batchSize = 100)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        using var probe = contextFactory();
        var query = probe.DailyRecords.AsNoTracking().Where(r => r.RiskProjectionVersion < PrtgHistoricalRiskReview.Version);
        if (visibleHostIds != null) query = query.Where(r => visibleHostIds.Contains(r.HostId));
        var candidates = query.OrderBy(r => r.RecordId).Select(r => new { r.RecordId, r.HostId, r.RecordDate }).Take(batchSize).ToList();
        var count = 0;
        var rewritten = 0;
        foreach (var candidate in candidates)
        {
            lock (EfAnalysisRecordStore.LockFor(candidate.HostId, candidate.RecordDate))
            {
                using var strategyContext = contextFactory();
                var visibleChanged = false;
                strategyContext.Database.CreateExecutionStrategy().Execute(() =>
                {
                    visibleChanged = false; // 執行策略重試時重算
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
                    var statusBefore = row.RiskReviewStatus;
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
                        visibleChanged = true;
                    }
                    if (AwaitingReview(statusBefore) != AwaitingReview(row.RiskReviewStatus)) visibleChanged = true;
                    row.RiskProjectionVersion = PrtgHistoricalRiskReview.Version;
                    ctx.SaveChanges();
                    tx.Commit();
                });
                count++;
                if (visibleChanged) rewritten++;
            }
        }
        return new PrtgRiskBatchResult(count, rewritten);
    }

    /// <summary>與儀表板待重評天數、精簡列重評提示同一個判準。</summary>
    private static bool AwaitingReview(string? status) => status is "pending" or "unavailable";
}

public sealed record PrtgRiskBatchResult(int Processed, int VisibleChanged);

public sealed record PrtgRiskReviewCounts(int UncheckedRecords, int PendingReviewRecords, int RevisedRecords);
