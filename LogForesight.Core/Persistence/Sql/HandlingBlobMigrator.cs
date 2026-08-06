using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把三份處理狀態自整份 JSON blob 搬進真表（docs/SCALE-ISSUE-FIRST-PLAN.md P3）。
///
/// **設計約束**（這是全案唯一會動到既有資料的一步，三條都不可退讓）：
///   1. **冪等可重跑**：以「目標表是空的」為執行條件。搬過就不再搬，重啟站台不會重複匯入。
///   2. **失敗不破壞舊資料**：整段包在一個交易裡；**搬完不刪 blob**，只記 log。
///      舊 blob 留著當備份，真的出事時資料還在原地，不必從備份還原。
///   3. **不靜默丟資料**：解析失敗直接拋，讓啟動失敗而不是「安靜地少了一半處理狀態」——
///      後者要好幾天後才會有人發現，而那時新舊資料已經混在一起。
///
/// 於 <see cref="StorageBackend"/> 的 schema 確認之後執行，與 DDL 共用同一把跨行程互斥
/// （兩個行程同時啟動時只有一個會搬）。
/// </summary>
internal static class HandlingBlobMigrator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static void MigrateIfNeeded(LfDbContext ctx, Func<string, string?> readBlob)
    {
        MigrateIssueHandlings(ctx, readBlob);
        MigrateIssueCases(ctx, readBlob);
        MigrateRecordHandlings(ctx, readBlob);
    }

    private static void MigrateIssueHandlings(LfDbContext ctx, Func<string, string?> readBlob)
    {
        if (ctx.IssueHandlings.Any()) return;

        var items = Deserialize<IssueHandling>(readBlob("issue_handling"), "issue_handling");
        if (items.Count == 0) return;

        // 舊 blob 理論上不會有重複鍵（SaveMany 的合併保證），但真要有的話新表的唯一索引會擋下——
        // 這裡先去重並記 log，讓遷移不會因為一筆歷史髒資料而整個失敗
        var deduped = items
            .GroupBy(h => (HostNameKey.Of(h.HostName), h.Date.Date, h.IssueKey))
            .Select(g => g.Last())
            .ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] issue_handling 遷移：{Dup} 筆重複鍵已保留最後一筆", items.Count - deduped.Count);

        ctx.IssueHandlings.AddRange(deduped.Select(h => new IssueHandlingRow
        {
            HostName = h.HostName,
            HostNameKey = HostNameKey.Of(h.HostName),
            RecordDate = h.Date.Date,
            IssueKey = h.IssueKey,
            Status = h.Status,
            ActorId = h.ActorId,
            ActorAccount = h.ActorAccount,
            Note = h.Note,
            DueDate = h.DueDate,
            CaseId = h.CaseId,
            UpdatedAt = h.UpdatedAt
        }));

        ctx.SaveChanges();
        Log.Info("[SQL] issue_handling 已自 blob 遷入 lf_issue_handling：{Count} 列（原 blob 保留未刪）", deduped.Count);
    }

    private static void MigrateIssueCases(LfDbContext ctx, Func<string, string?> readBlob)
    {
        if (ctx.IssueCases.Any()) return;

        var items = Deserialize<IssueCase>(readBlob("issue_cases"), "issue_cases");
        if (items.Count == 0) return;

        var deduped = items.GroupBy(c => c.CaseId).Select(g => g.Last()).ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] issue_cases 遷移：{Dup} 筆重複 case_id 已保留最後一筆", items.Count - deduped.Count);

        ctx.IssueCases.AddRange(deduped.Select(c => new IssueCaseRow
        {
            CaseId = c.CaseId,
            HostName = c.HostName,
            HostNameKey = HostNameKey.Of(c.HostName),
            IssueKey = c.IssueKey,
            IssueLabel = c.IssueLabel,
            Status = c.Status,
            HandlerId = c.HandlerId,
            Note = c.Note,
            DueDate = c.DueDate,
            FirstLinkedDate = c.FirstLinkedDate,
            LastLinkedDate = c.LastLinkedDate,
            ClosedAt = c.ClosedAt,
            CreatedAt = c.CreatedAt,
            CreatedByAccount = c.CreatedByAccount,
            UpdatedAt = c.UpdatedAt
        }));

        ctx.SaveChanges();
        Log.Info("[SQL] issue_cases 已自 blob 遷入 lf_issue_cases：{Count} 列（原 blob 保留未刪）", deduped.Count);
    }

    private static void MigrateRecordHandlings(LfDbContext ctx, Func<string, string?> readBlob)
    {
        if (ctx.RecordHandlings.Any()) return;

        var items = Deserialize<RecordHandling>(readBlob("record_handling"), "record_handling");
        if (items.Count == 0) return;

        var deduped = items
            .GroupBy(h => (HostNameKey.Of(h.HostName), h.Date.Date))
            .Select(g => g.Last())
            .ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] record_handling 遷移：{Dup} 筆重複鍵已保留最後一筆", items.Count - deduped.Count);

        ctx.RecordHandlings.AddRange(deduped.Select(h => new RecordHandlingRow
        {
            HostName = h.HostName,
            HostNameKey = HostNameKey.Of(h.HostName),
            RecordDate = h.Date.Date,
            Status = h.Status,
            HandlerId = h.HandlerId,
            DueDate = h.DueDate,
            Note = h.Note,
            UpdatedAt = h.UpdatedAt
        }));

        ctx.SaveChanges();
        Log.Info("[SQL] record_handling 已自 blob 遷入 lf_record_handling：{Count} 列（原 blob 保留未刪）", deduped.Count);
    }

    /// <summary>
    /// 解析失敗**不吞**：處理狀態靜默當空會讓整站看起來「所有問題都沒人處理過」，
    /// 比啟動報錯難查得多——與 <see cref="JsonBlobCollection{T}"/> 的既有取捨一致。
    /// </summary>
    private static List<T> Deserialize<T>(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, LfJsonOptions.Pretty) ?? new List<T>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"處理狀態遷移失敗：blob「{key}」無法解析（{ex.Message}）。" +
                "資料未被修改，請確認 lf_blobs 的內容後再啟動。", ex);
        }
    }
}
