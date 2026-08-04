using System.Text;
using System.Text.Json;
using LogForesight.Sql;

namespace LogForesight;

/// <summary>
/// 一次批次執行的紀錄（↔ lf_batch_runs）。
///
/// <see cref="FinishedAt"/> 為 null 代表「執行中或異常中斷」——這是刻意的設計：
/// 啟動時先寫一列、結束時回填，於是「掛掉的執行」變成可查詢的狀態
/// （FinishedAt IS NULL 且 StartedAt 超過合理時長），比等到「今天沒紀錄」才發現早一步。
/// </summary>
public class BatchRun
{
    public long RunId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>執行檔版本——查「這台還在跑舊版」</summary>
    public string AppVersion { get; set; } = string.Empty;

    public string Args { get; set; } = string.Empty;
    public int DaysAnalyzed { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>
    /// 觸發來源（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：<c>schedule</c>｜<c>manual:{帳號}</c>｜
    /// <c>console</c>。舊紀錄沒有這個欄位（JSON 反序列化容忍缺欄，null），Runs 頁顯示為
    /// 「工作排程器」——那正是升級前唯一的觸發來源，語意上等價。
    /// </summary>
    public string? Trigger { get; set; }

    /// <summary>
    /// 優雅停止（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4：使用者手動停止或執行窗口 End 到點）：
    /// 是「已停止」不是「失敗」——已完成的部分照常保留，剩餘日子由下次執行的缺漏回補自癒，
    /// 執行監控頁據此顯示獨立的「已停止」狀態。舊紀錄缺欄反序列化為 false，語意正確
    /// （過去沒有停止機制）。
    /// </summary>
    public bool Stopped { get; set; }
}

/// <summary>
/// 執行期間的診斷紀錄（↔ lf_batch_run_logs）。
/// **只收 Warn 以上與固定的 Info 里程碑**，不是把整個 NLog 檔灌進來——
/// 完整診斷仍在 logs\logforesight.log，這裡負責「一眼確認有沒有問題」。
/// </summary>
public class BatchRunLog
{
    public long LogId { get; set; }
    public long RunId { get; set; }
    public DateTime LoggedAt { get; set; }

    /// <summary>Info | Warn | Error | Fatal</summary>
    public string Level { get; set; } = string.Empty;

    public string Logger { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>完整堆疊（只有 Error/Fatal 有）</summary>
    public string? ExceptionText { get; set; }
}

/// <summary>
/// 一次批次執行的紀錄存取（log key=batch_runs ＋ batch_run_logs，append-only）。
///
/// 執行紀錄是 append-only 但需要「回填結束時間」——實作方式是再 append 一列同 RunId 的完整紀錄，
/// 讀取時同 RunId 取最後一列。這樣寫入端維持純附加，
/// 而批次執行中途被強制中斷時，先前寫的「開始」那一列仍然留著，正好就是我們要偵測的狀態。
/// </summary>
public class BatchRunStore
{
    private readonly EfJsonLogStore _runs;
    private readonly EfJsonLogStore _logs;
    private readonly object _lock = new();
    private long _lastRunId;
    private long _lastLogId;

    public BatchRunStore(EfJsonLogStore runs, EfJsonLogStore logs)
    {
        _runs = runs;
        _logs = logs;

        _lastRunId = ReadAllRunLines().Select(r => r.RunId).DefaultIfEmpty(0).Max();
        _lastLogId = ReadAllLogs().Select(l => l.LogId).DefaultIfEmpty(0).Max();
    }

    /// <summary>啟動時登記，回傳配發的 RunId</summary>
    public long StartRun(BatchRun run)
    {
        lock (_lock)
        {
            run.RunId = ++_lastRunId;
            AppendRunLine(run);
            return run.RunId;
        }
    }

    /// <summary>結束時回填統計與結束時間</summary>
    public void FinishRun(BatchRun run)
    {
        lock (_lock)
        {
            AppendRunLine(run);
        }
    }

    public void AppendLog(BatchRunLog log)
    {
        lock (_lock)
        {
            log.LogId = ++_lastLogId;
            if (log.LoggedAt == default) log.LoggedAt = DateTime.Now;

            _logs.AppendLine(JsonSerializer.Serialize(log, LfJsonOptions.Compact));
        }
    }

    /// <summary>近 N 天的執行紀錄（執行監控總表）</summary>
    public List<BatchRun> GetRecentRuns(int days, IReadOnlyCollection<string>? hostNames)
    {
        var cutoff = DateTime.Today.AddDays(-days + 1);
        var runs = LatestPerRun().Where(r => r.StartedAt.Date >= cutoff);

        // hostNames 為 null = 不限；空集合 = 查不到（與其他查詢介面同一語意）
        if (hostNames != null)
        {
            var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            runs = runs.Where(r => names.Contains(r.HostName));
        }

        return runs.OrderByDescending(r => r.StartedAt).ToList();
    }

    public BatchRun? GetRun(long runId) => LatestPerRun().FirstOrDefault(r => r.RunId == runId);

    public List<BatchRunLog> GetLogs(long runId) =>
        ReadAllLogs().Where(l => l.RunId == runId).OrderBy(l => l.LogId).ToList();

    /// <summary>近 N 天的 Error/Fatal 紀錄（異常彙總）</summary>
    public List<BatchRunLog> GetRecentErrors(int days)
    {
        var cutoff = DateTime.Today.AddDays(-days + 1);

        return ReadAllLogs()
            .Where(l => l.LoggedAt.Date >= cutoff && l.Level is "Error" or "Fatal")
            .OrderByDescending(l => l.LoggedAt)
            .ToList();
    }

    /// <summary>
    /// 清除超過保留天數的執行紀錄與診斷紀錄，回傳合計刪除筆數（docs/archive/HISTORY.md P0-3）。
    /// 依附加時間（非 StartedAt/LoggedAt 業務時間）判斷——批次每日執行，兩者實務上等價，
    /// 且直接用附加時間可讓底層 SQL 端整批刪，不必逐行反序列化 JSON 比對業務日期。
    /// </summary>
    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        return _runs.Prune(cutoff) + _logs.Prune(cutoff);
    }

    /// <summary>同一 RunId 取最後一列——「結束」那一列會覆蓋先前的「開始」</summary>
    private List<BatchRun> LatestPerRun() =>
        ReadAllRunLines()
            .GroupBy(r => r.RunId)
            .Select(g => g.Last())
            .ToList();

    private void AppendRunLine(BatchRun run) =>
        _runs.AppendLine(JsonSerializer.Serialize(run, LfJsonOptions.Compact));

    private List<BatchRun> ReadAllRunLines() => JsonLogParser.Parse<BatchRun>(_runs.ReadLines(), LfJsonOptions.Compact);

    private List<BatchRunLog> ReadAllLogs() => JsonLogParser.Parse<BatchRunLog>(_logs.ReadLines(), LfJsonOptions.Compact);
}
