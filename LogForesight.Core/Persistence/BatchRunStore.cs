using System.Text;
using System.Text.Json;
using NLog;

namespace LogForesight.Core.Persistence;

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

    /// <summary>
    /// 作業類型（回饋三十五輪批次D）：null＝取數／分析執行（含舊紀錄，缺欄反序列化為 null）、
    /// <see cref="JobTypeAi"/>＝AI 分析排程的執行。執行總表（主機×日視角）只統計取數執行，
    /// AI 執行走執行紀錄的逐筆視角——它不是「哪台主機哪天跑了沒」的語意。
    /// </summary>
    public string? JobType { get; set; }

    /// <summary>AI 分析排程執行的 <see cref="JobType"/> 值</summary>
    public const string JobTypeAi = "ai";

    /// <summary>本機分析成功天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysAnalyzed { get; set; }

    /// <summary>本機分析失敗天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析成功主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysAnalyzed { get; set; }

    /// <summary>NetIQ 機房分析失敗主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析已完成跳過的主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqHostsSkipped { get; set; }

    /// <summary>
    /// PRTG 擷取成果狀態：<see cref="PrtgOutcomeDisabled"/> | <see cref="PrtgOutcomeSuccess"/> |
    /// <see cref="PrtgOutcomePartial"/> | <see cref="PrtgOutcomeFailed"/>。null＝舊紀錄或本次未執行 PRTG。
    /// </summary>
    public string? PrtgOutcome { get; set; }

    /// <summary>PRTG 觸發式取數目標感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFetched { get; set; }

    /// <summary>PRTG 觸發式取數失敗感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFailed { get; set; }

    /// <summary>PRTG 觸發式取數問題主機數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgTriggeredHosts { get; set; }

    /// <summary>PRTG 未啟用（<see cref="PrtgOutcome"/>）</summary>
    public const string PrtgOutcomeDisabled = "disabled";

    /// <summary>PRTG 擷取成功（<see cref="PrtgOutcome"/>）</summary>
    public const string PrtgOutcomeSuccess = "success";

    /// <summary>PRTG 擷取部分成功（<see cref="PrtgOutcome"/>）</summary>
    public const string PrtgOutcomePartial = "partial";

    /// <summary>PRTG 擷取失敗（<see cref="PrtgOutcome"/>）</summary>
    public const string PrtgOutcomeFailed = "failed";

    /// <summary>逐日 PRTG 統計（null＝舊紀錄或未執行 PRTG）</summary>
    public List<PrtgDayStat>? PrtgDays { get; set; }
}

/// <summary>PRTG 單日擷取與分析統計摘要（逐日 PRTG 統計）。</summary>
public sealed record PrtgDayStat(
    DateTime Date,
    string Outcome,
    int Findings,
    int AttributedHosts,
    bool MapAvailable,
    int TriggerHosts,
    int TargetSensors,
    int FailedSensors);

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 續號探測的回看行數（docs/archive/SCALE-ISSUE-FIRST-PLAN.md N4）。
    /// 只看最後一行時，那一行剛好損毀就會讓續號從 0 重來、與既有紀錄重號；
    /// 損毀通常是連續的一小段，回看 10 行足以跨過去，而這仍是索引 (log_key, seq)
    /// 的同一次反向 seek，成本與讀一行幾乎相同——不值得為此退回全表掃描。
    ///
    /// **為什麼要取這 N 行裡的最大值、不能只取最後一筆可解析的**：執行紀錄是 append-only，
    /// 「結束」列帶的是該趟**開始時**配到的 RunId。取數與 AI 排程可以並行，附加順序可能是
    /// 「開始 100 → 開始 101 → 結束 101 → 結束 100」，尾端那一列的 RunId 是 100；只取它會把
    /// 下一趟配成 101，與既有的 101 撞號——撞號後兩趟執行被合併成一筆、診斷行整批錯掛。
    /// 32 行足以涵蓋「同時活著的執行數 × 2」再加上一段損毀容忍。
    /// </summary>
    private const int IdProbeLines = 32;

    /// <summary>
    /// SQL 端依**附加時間**窄化時往前多放的緩衝天數。
    /// 附加時間（寫入 DB 的那一刻）與業務時間（<see cref="BatchRun.StartedAt"/>／
    /// <see cref="BatchRunLog.LoggedAt"/>）不是同一個東西：跨午夜執行、時鐘誤差、
    /// 或先寫「開始」列再回填的情境，都會出現「業務時間在範圍內、附加時間略早於 cutoff」的列。
    /// 多放一天讓這些列留在候選集裡，精確判斷仍由記憶體端的業務時間過濾負責。
    /// </summary>
    private const int AppendTimeBufferDays = 1;

    /// <summary>
    /// 單筆執行查詢（<see cref="GetRun"/>）的預設回看窗口：沿用執行紀錄保留天數的出廠預設
    /// （<see cref="SystemSettings.DefaultRunLogRetentionDays"/>）——保留期以外的列本來就會被
    /// <see cref="Prune"/> 清掉。這個 store 建構時拿不到系統設定（建構式簽章為寫入端共用），
    /// 因此保留天數被調得比預設更長時，窗口內找不到就退回全撈（見 <see cref="GetRun"/>），
    /// 對外行為與改動前完全一致。
    /// </summary>
    private const int RunLookupWindowDays = SystemSettings.DefaultRunLogRetentionDays;

    private readonly EfJsonLogStore _runs;
    private readonly EfJsonLogStore _logs;
    private readonly object _lock = new();
    private long _lastRunId;
    private long _lastLogId;

    public BatchRunStore(EfJsonLogStore runs, EfJsonLogStore logs)
    {
        _runs = runs;
        _logs = logs;

        // 續號起點以反向 seek 取得，不再整份讀回——這裡是 Singleton store 的建構式，
        // 全撈等於站台啟動時同步讀十萬列並逐行解析。
        _lastRunId = ProbeLastId<BatchRun>(_runs, r => r.RunId, "執行紀錄");
        _lastLogId = ProbeLastId<BatchRunLog>(_logs, l => l.LogId, "執行診斷紀錄");
    }

    /// <summary>
    /// 續號起點＝尾端 N 行裡**解析得出來的最大** id（理由見 <see cref="IdProbeLines"/>）。
    /// 全部無法解析才回 0 並記 Warn——那代表尾端整段損毀，值得被看見而不是安靜地重號。
    /// </summary>
    private static long ProbeLastId<T>(EfJsonLogStore store, Func<T, long> idOf, string what) where T : class
    {
        var lines = store.ReadLastLines(IdProbeLines);
        if (lines.Count == 0) return 0;

        long? max = null;
        foreach (var line in lines)
        {
            var parsed = JsonLogParser.Parse<T>(new[] { line }, LfJsonOptions.Compact);
            if (parsed.Count == 0) continue;
            var id = idOf(parsed[0]);
            if (max == null || id > max) max = id;
        }
        if (max != null) return max.Value;

        Log.Warn("[BatchRunStore] {What}最後 {Count} 行都無法解析，續號自 0 起算——可能與既有紀錄重號。",
            what, lines.Count);
        return 0;
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

        // SQL 端先以附加時間窄化（含緩衝，不保證精確：CreatedAt 為 null 的既存列一律留下），
        // 業務時間的精確判斷仍在記憶體端做，不可因為「SQL 已經篩過」就拿掉。
        var runs = LatestPerRun(ReadRunLinesFrom(cutoff.AddDays(-AppendTimeBufferDays)))
            .Where(r => r.StartedAt.Date >= cutoff);

        // hostNames 為 null = 不限；空集合 = 查不到（與其他查詢介面同一語意）
        if (hostNames != null)
        {
            var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            runs = runs.Where(r => names.Contains(r.HostName));
        }

        return runs.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>
    /// 單筆執行。RunId 在 JSON 內容裡、不是資料表欄位，無法直接下推 SQL——
    /// 改以保留期窗口窄化候選集後在記憶體比對。
    ///
    /// 退回全撈只在**這個 runId 有可能存在於窗口之外**時發生：RunId 單調遞增，
    /// 若它小於窗口內最小的 RunId，代表是保留天數被調得比出廠預設更長時的舊列，值得全撈一次；
    /// 若它落在窗口內的 RunId 範圍之間卻沒找到（已被清除、或根本不存在——舊書籤、手改網址），
    /// 全撈也不會找到，只是把本輪要消滅的整份讀取變成每次查無此執行都付一次。
    /// 窗口內完全沒有列時無從判斷（可能是保留期拉長後長期未執行的站台），維持全撈一次。
    /// </summary>
    public BatchRun? GetRun(long runId)
    {
        var from = DateTime.Today.AddDays(-RunLookupWindowDays - AppendTimeBufferDays);
        var window = LatestPerRun(ReadRunLinesFrom(from));
        var hit = window.FirstOrDefault(r => r.RunId == runId);
        if (hit != null) return hit;

        var mayBeOlderThanWindow = window.Count == 0 || runId < window.Min(r => r.RunId);
        return mayBeOlderThanWindow
            ? LatestPerRun(ReadAllRunLines()).FirstOrDefault(r => r.RunId == runId)
            : null;
    }

    /// <summary>
    /// 單次執行的診斷紀錄。先取得該執行的時間範圍，再以該範圍（前後各留緩衝）窄化 log 分區。
    /// 找不到該執行時與改動前同樣是空清單；**執行中**的執行（FinishedAt 為 null）上界取現在時間，
    /// 不會因為沒有結束時間就回空。
    ///
    /// **窄化的結果就是最終結果，沒有「撈不到就全撈」的退路**，依據是：診斷行的業務時間與附加時間
    /// 同源——<see cref="AppendLog"/> 把 <see cref="BatchRunLog.LoggedAt"/> 設為寫入當下，
    /// 底層 <c>AppendLine</c> 寫的附加時間也是寫入當下，兩者相差毫秒級；而診斷行必然寫在該次執行
    /// 進行中，因此一定落在 [StartedAt-緩衝, (FinishedAt ?? 現在)+緩衝] 裡。
    /// 退路唯一會被觸發的情境反而是「這趟執行一行診斷都沒有」（NLog target 只收 Warn 以上，
    /// 乾淨執行本來就是零行）——那是最常見的情況，留著退路等於把全撈變成常態路徑。
    /// 升級前寫入、沒有附加時間的既存列也不會漏：底層對這種列一律視為在範圍內。
    /// 這個前提由測試釘住（有診斷行的執行必須只靠窄化就取得全部的行）。
    /// </summary>
    public List<BatchRunLog> GetLogs(long runId)
    {
        var run = GetRun(runId);
        if (run == null) return new List<BatchRunLog>();

        var from = run.StartedAt.AddDays(-AppendTimeBufferDays);
        var until = run.FinishedAt ?? DateTime.Now;
        if (until < run.StartedAt) until = run.StartedAt;

        return SelectLogs(ReadLogs(from, until.AddDays(AppendTimeBufferDays)), runId);
    }

    private static List<BatchRunLog> SelectLogs(IEnumerable<BatchRunLog> logs, long runId) =>
        logs.Where(l => l.RunId == runId).OrderBy(l => l.LogId).ToList();

    /// <summary>近 N 天的 Error/Fatal 紀錄（異常彙總）</summary>
    public List<BatchRunLog> GetRecentErrors(int days)
    {
        var cutoff = DateTime.Today.AddDays(-days + 1);

        // 與 GetRecentRuns 同一套：SQL 端粗篩（含緩衝），業務時間與 Level 的判斷仍在記憶體端
        return ReadLogs(cutoff.AddDays(-AppendTimeBufferDays), null)
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
    private static List<BatchRun> LatestPerRun(IEnumerable<BatchRun> lines) =>
        lines
            .GroupBy(r => r.RunId)
            .Select(g => g.Last())
            .ToList();

    private void AppendRunLine(BatchRun run) =>
        _runs.AppendLine(JsonSerializer.Serialize(run, LfJsonOptions.Compact));

    private List<BatchRun> ReadAllRunLines() => JsonLogParser.Parse<BatchRun>(_runs.ReadLines(), LfJsonOptions.Compact);

    /// <summary>附加時間不早於 from 的執行紀錄行（SQL 端窄化，不保證精確）</summary>
    private List<BatchRun> ReadRunLinesFrom(DateTime from) =>
        JsonLogParser.Parse<BatchRun>(_runs.ReadLines(from, null), LfJsonOptions.Compact);

    private List<BatchRunLog> ReadAllLogs() => JsonLogParser.Parse<BatchRunLog>(_logs.ReadLines(), LfJsonOptions.Compact);

    /// <summary>附加時間落在範圍內的診斷紀錄行（SQL 端窄化，不保證精確）</summary>
    private List<BatchRunLog> ReadLogs(DateTime? from, DateTime? to) =>
        JsonLogParser.Parse<BatchRunLog>(_logs.ReadLines(from, to), LfJsonOptions.Compact);
}
