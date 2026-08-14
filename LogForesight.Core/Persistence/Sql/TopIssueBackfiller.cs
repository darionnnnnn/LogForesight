using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把 <c>lf_top_issues</c> 的聚合維度回填到既有列（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4）。
///
/// **為什麼一定要回填而不是「舊資料就算了」**：問題聚合（主機數／期間跨度／出現密度／
/// 總次數）改由這張表 GROUP BY 直接回答之後，沒回填的舊列會讓數字**偏低但看起來正常**——
/// 這比報錯難查得多，也違反本專案「涵蓋範圍要誠實顯示」的原則。
///
/// **為什麼在背景跑**：這一步要逐筆解析 ContentJson（host_id 與 record_date 可以純 SQL join
/// 補，但 event_count／elevates_day_risk／log_name／entry_type 只存在於 JSON 裡）。
/// 掛在啟動路徑上，大 DB 會讓 Windows 服務啟動逾時（預設 30 秒，規劃 §8.2 E3）。
///
/// 進度以 <see cref="Progress"/> 對外，供 <c>/api/health/detail</c> 與畫面的「統計中」標示。
/// 冪等：以「record_date 還是 DateTime.MinValue」判定未回填，重跑只會處理剩下的。
/// </summary>
public sealed class TopIssueBackfiller
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>一批處理的父列數。太大則單筆交易過長、記憶體尖峰高；太小則往返次數多。</summary>
    private const int BatchSize = 500;

    private readonly Func<LfDbContext> _contextFactory;

    public TopIssueBackfiller(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public TopIssueBackfillProgress Progress { get; private set; } = new();

    /// <summary>還沒回填的問題列數（0＝已完成）</summary>
    public int CountPending()
    {
        using var ctx = _contextFactory();
        return ctx.TopIssues.Count(t => t.RecordDate == DateTime.MinValue);
    }

    /// <summary>
    /// 執行回填直到完成或被取消。**每批一個交易**——中途取消（站台關閉）時已完成的批次
    /// 不會回滾，下次啟動接著跑。
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        var pending = CountPending();
        if (pending == 0)
        {
            Progress = new TopIssueBackfillProgress { Completed = true };
            return;
        }

        Log.Info("[SQL] lf_top_issues 聚合欄回填開始：待處理 {Pending} 列", pending);
        Progress = new TopIssueBackfillProgress { Total = pending };

        var done = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var processed = RunBatch();
            if (processed == 0) break;

            done += processed;
            Progress = new TopIssueBackfillProgress { Total = pending, Done = Math.Min(done, pending) };
        }

        var remaining = CountPending();
        Progress = new TopIssueBackfillProgress { Total = pending, Done = pending - remaining, Completed = remaining == 0 };

        if (remaining == 0) Log.Info("[SQL] lf_top_issues 聚合欄回填完成：{Done} 列", done);
        else Log.Info("[SQL] lf_top_issues 聚合欄回填中止（站台關閉），剩餘 {Remaining} 列，下次啟動接續", remaining);
    }

    /// <summary>回填一批；回傳本批處理的問題列數（0＝沒有待處理的了）</summary>
    private int RunBatch()
    {
        using var ctx = _contextFactory();

        // 以父列為單位取一批：同一筆紀錄的問題列一起補，才只解析一次 ContentJson
        var recordIds = ctx.TopIssues
            .Where(t => t.RecordDate == DateTime.MinValue)
            .Select(t => t.RecordId)
            .Distinct()
            .Take(BatchSize)
            .ToList();
        if (recordIds.Count == 0) return 0;

        var records = ctx.DailyRecords
            .Where(r => recordIds.Contains(r.RecordId))
            .Select(r => new { r.RecordId, r.HostId, r.RecordDate, r.ContentJson })
            .ToList()
            .ToDictionary(r => r.RecordId);

        var issueRows = ctx.TopIssues.Where(t => recordIds.Contains(t.RecordId)).ToList();

        foreach (var group in issueRows.GroupBy(t => t.RecordId))
        {
            if (!records.TryGetValue(group.Key, out var parent))
            {
                // 父列不存在（理論上 FK cascade 不會發生）：填上可辨識的日期避免這批永遠卡住
                foreach (var row in group) row.RecordDate = DateTime.MinValue.AddDays(1);
                continue;
            }

            // 逐筆比對 ContentJson 裡的問題，取回只存在於 JSON 的四個欄位
            var issues = ParseIssues(parent.ContentJson);
            foreach (var row in group)
            {
                row.HostId = parent.HostId;
                row.RecordDate = parent.RecordDate.Date;

                var match = issues.FirstOrDefault(i =>
                    string.Equals(i.Source, row.SourceName, StringComparison.OrdinalIgnoreCase) &&
                    i.EventId == row.EventId);
                if (match == null) continue;

                row.EventCount = match.Count;
                row.ElevatesDayRisk = match.ElevatesDayRisk;
                row.LogName = match.LogName;
                row.EntryType = (int)match.EntryType;
                // 回饋十九輪批次B：與其餘聚合維度同一批回填，不必另開一個回填流程
                row.KnownIssue = match.KnownIssue;
                row.EventKey = match.EventKey;
            }
        }

        ctx.SaveChanges();
        return issueRows.Count;
    }

    /// <summary>解析失敗時回空清單：這一列的聚合欄補不齊（次數為 0），但不該讓整個回填停擺</summary>
    private static List<LogIssueSignature> ParseIssues(string contentJson)
    {
        try
        {
            return JsonSerializer.Deserialize<DailyAnalysisRecord>(contentJson)?.TopIssues ?? new List<LogIssueSignature>();
        }
        catch (JsonException ex)
        {
            Log.Warn("[SQL] 回填時無法解析紀錄內容，該列的聚合欄維持預設值：{Msg}", ex.Message);
            return new List<LogIssueSignature>();
        }
    }
}

/// <summary>回填進度（供 /api/health/detail 與畫面的「統計中」標示）</summary>
public sealed class TopIssueBackfillProgress
{
    public int Total { get; init; }
    public int Done { get; init; }
    public bool Completed { get; init; }

    /// <summary>未完成時聚合數字會偏低，畫面必須誠實標示</summary>
    public bool InProgress => !Completed && Total > 0;
}
