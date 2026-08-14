using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把 <c>lf_daily_records</c> 讀取面 SQL 化的抽出欄回填到既有列（回饋十九輪批次B）。
///
/// 與 <see cref="TopIssueBackfiller"/> 同一個理由存在、同一套「每批一個交易、可中斷續跑」
/// 的骨架，差別只在判定「還沒回填」的方式：<c>lf_top_issues</c> 用 <c>record_date ==
/// DateTime.MinValue</c> 當哨兵（那幾個欄位本來就不可能合法地是那個值），但這裡要補的欄位
/// （<c>error_count</c>／<c>ai_analyzed</c> 等）本身的合法值就含 0／false／空字串，沒有
/// 任何一個值能安全地當「還沒回填」的訊號——所以改用顯式的 <see cref="DailyRecordRow.ExtractVersion"/>
/// 版本號：本輪寫入＝1，舊列預設 0，回填完成後蓋成 1。同一趟順便補 <c>has_correlation</c>
/// 舊列（P1-2 既有欄，舊資料預設 false 不精準，見 SchemaUpgrader 該欄註解）。
/// </summary>
public sealed class DailyRecordBackfiller
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int BatchSize = 500;
    private const int CurrentVersion = 1;

    private readonly Func<LfDbContext> _contextFactory;

    public DailyRecordBackfiller(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public TopIssueBackfillProgress Progress { get; private set; } = new();

    public int CountPending()
    {
        using var ctx = _contextFactory();
        return ctx.DailyRecords.Count(r => r.ExtractVersion < CurrentVersion);
    }

    /// <summary>執行回填直到完成或被取消。每批一個交易，中途取消不回滾已完成的批次。</summary>
    public void Run(CancellationToken cancellationToken)
    {
        var pending = CountPending();
        if (pending == 0)
        {
            Progress = new TopIssueBackfillProgress { Completed = true };
            return;
        }

        Log.Info("[SQL] lf_daily_records 抽出欄回填開始：待處理 {Pending} 列", pending);
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

        if (remaining == 0) Log.Info("[SQL] lf_daily_records 抽出欄回填完成：{Done} 列", done);
        else Log.Info("[SQL] lf_daily_records 抽出欄回填中止（站台關閉），剩餘 {Remaining} 列，下次啟動接續", remaining);
    }

    private int RunBatch()
    {
        using var ctx = _contextFactory();

        var rows = ctx.DailyRecords
            .Where(r => r.ExtractVersion < CurrentVersion)
            .Take(BatchSize)
            .ToList();
        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            var record = Parse(row.ContentJson);
            if (record == null)
            {
                // 解析失敗：標成已回填避免卡住整個回填流程，欄位維持預設值——同
                // TopIssueBackfiller.ParseIssues 的既有取捨（誠實接受這一列補不齊）
                row.ExtractVersion = CurrentVersion;
                continue;
            }

            row.Headline = record.Headline;
            row.DataIncomplete = record.DataIncomplete;
            row.SecurityLogAvailable = record.SecurityLogAvailable;
            row.ErrorCount = record.ErrorCount;
            row.WarningCount = record.WarningCount;
            row.AiAnalyzed = record.AiAnalyzed;
            row.AiPending = record.AiPending;
            row.HasCorrelation = record.CorrelationAlerts.Count > 0;
            row.ExtractVersion = CurrentVersion;
        }

        ctx.SaveChanges();
        return rows.Count;
    }

    private static DailyAnalysisRecord? Parse(string contentJson)
    {
        try
        {
            return JsonSerializer.Deserialize<DailyAnalysisRecord>(contentJson);
        }
        catch (JsonException ex)
        {
            Log.Warn("[SQL] 回填時無法解析紀錄內容，該列的抽出欄維持預設值：{Msg}", ex.Message);
            return null;
        }
    }
}
