using NLog;
using NLog.Config;
using NLog.Targets;

namespace LogForesight.Core.Service;

/// <summary>
/// 批次執行紀錄的收集（docs/WEB-SPEC.md §11、§10.1）。
///
/// 啟動時登記一列（FinishedAt=null），結束時回填——於是「異常中斷的執行」變成可查詢的狀態，
/// 比等到「今天沒紀錄」才發現早一步。
///
/// **失敗不得中斷分析**（§11-4）：執行監控是附屬功能，它自己不能成為批次的故障點。
/// 所有寫入都包在 try/catch 裡，失敗只記本地 NLog。
/// </summary>
public class BatchRunRecorder : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 慢 SQL 效能監控器名稱：資料層效能告警 ≠ 這趟分析有問題。
    /// 慢查詢 Warn 仍照常寫入執行詳情供排查，但不計入 WarnCount，避免整趟成功執行被判成 warning。
    /// </summary>
    public const string ExemptWarnLogger = "SqlPerformanceMonitor";

    /// <summary>執行輸出（<c>IRunConsole</c> 的白話訊息）寫進執行紀錄時的 logger 名稱；寫入端與診斷匯出的判讀共用</summary>
    public const string OutputLogger = "Output";

    /// <summary>
    /// 不計入 WarnCount 的 logger 短名稱（依名稱判定，不比對訊息內容）：慢 SQL 監控，以及
    /// AI JSON 失敗診斷（<see cref="AiJsonDiagnostics.LoggerName"/>，只給匯出用的證據行，
    /// 不是這趟分析的新問題）。兩者仍照常寫入執行紀錄。執行監控頁（RunMonitorService）共用此判定。
    /// </summary>
    public static bool IsWarnCountExemptLogger(string? loggerName) =>
        string.Equals(loggerName, ExemptWarnLogger, StringComparison.Ordinal)
        || string.Equals(loggerName, AiJsonDiagnostics.LoggerName, StringComparison.Ordinal);

    private readonly BatchRunStore? _store;
    private readonly BatchRun _run;
    private readonly BatchRunNLogTarget? _target;
    private readonly CancellationToken _ct;
    private volatile bool _finished;
    private IDisposable? _scope;

    /// <summary>
    /// scope 屬性鍵。值是**這個 recorder 實例**的識別碼，不是 RunId——RunId 由各自的
    /// store 配號，兩個獨立 store（例如測試裡各自的資料庫、或日後多後端並存）會配出同一個號，
    /// 那時 target 就會把別人那一趟的事件當成自己的收進來。識別碼逐實例產生，不會碰撞。
    /// </summary>
    private const string ScopeKey = "lf_run_scope";

    /// <summary>這個 recorder 實例的識別碼（見 <see cref="ScopeKey"/>）</summary>
    private readonly string _scopeToken = Guid.NewGuid().ToString("N");

    /// <param name="ct">執行用的取消權杖（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：優雅停止時
    /// <see cref="OperationCanceledException"/> 會在 using 範圍結束時經 <see cref="Dispose"/> 回填——
    /// 這裡收下權杖，讓 Dispose 分得出「使用者停止」（記「已停止」）與「異常中斷」（exit 1）。
    /// console 傳 <see cref="CancellationToken.None"/>，行為與加入此參數前完全相同。</param>
    /// <param name="onRegistrationFailed">
    /// 登記失敗時的可見回報（docs/archive/FEEDBACK-8-PLAN.md #3）：原本只寫 NLog，Web 排程執行時
    /// 這趟執行在執行監控頁會整筆「消失」（不是顯示失敗，是完全查不到這次執行發生過），
    /// 使用者只能翻 log 檔才查得到。呼叫端（Web）可傳入把這句話送進 <c>IRunConsole</c>，
    /// 讓狀態卡與執行明細看得到「這趟執行本次不會出現在執行監控」；null＝維持原本只寫 log。
    /// </param>
    /// <param name="jobType">作業類型（選填）：<see cref="BatchRun.JobTypeAi"/>＝AI 分析排程；null＝一般批次/取數執行。</param>
    /// <param name="startedAt">整趟的起點（選填）：recorder 建構前已有前置作業時由呼叫端傳入，讓紀錄與「總執行時間」同一個起點。</param>
    /// <remarks>
    /// 【AsyncLocal 作用域限制】<see cref="ScopeContext"/> 底層是 <see cref="AsyncLocal{T}"/>：
    /// 它沿著非同步呼叫鏈**向下**傳遞，但不會向外（向上）傳播。因此 recorder 必須在
    /// 「涵蓋整趟執行的那個方法本體」內建構（現況是 <c>AnalysisOrchestrator.RunAsync</c> 的本體），
    /// 之後所有 <c>await</c>、<c>Task.WhenAll</c>、<c>Parallel.ForEachAsync</c> 子任務才繼承得到這個 scope。
    /// 若日後把建構搬進一個被 <c>await</c> 的輔助 async 方法，scope 會在該方法返回時就消失，
    /// 整趟執行的 Warn 全部靜默丟失且沒有任何訊號。
    /// </remarks>
    public BatchRunRecorder(BatchRunStore? store, string hostName, string[] args, string? trigger = null,
        CancellationToken ct = default, Action<string>? onRegistrationFailed = null, string? jobType = null,
        bool catchUp = false, DateTime? startedAt = null, string? scope = null, DateTime? windowInstanceStart = null)
    {
        _store = store;
        _ct = ct;
        _run = new BatchRun
        {
            HostName = hostName,
            StartedAt = startedAt ?? DateTime.Now,
            AppVersion = BuildVersion.Current.Label,
            Args = string.Join(" ", args),
            Trigger = trigger,
            JobType = jobType,
            Scope = scope,
            CatchUp = catchUp ? true : null,
            WindowInstanceStart = windowInstanceStart
        };

        if (_store == null) return;

        try
        {
            _store.StartRun(_run);

            // 掛上 NLog target：Warn 以上自動流入執行紀錄，不需要在 codebase 各處加呼叫。
            // 完整診斷仍在 logs\logforesight.log，這裡只收「一眼確認有沒有問題」需要的部分
            _target = new BatchRunNLogTarget(_store, _run.RunId, _scopeToken, OnLogRecorded);
            _target.Attach();
            _scope = ScopeContext.PushProperty(ScopeKey, _scopeToken);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "批次執行紀錄登記失敗（不影響本次分析）：{0}", ex.Message);
            onRegistrationFailed?.Invoke($"執行紀錄登記失敗（不影響本次分析，但這趟執行本次不會出現在執行監控）：{ex.Message}");
            _store = null;
        }
    }

    // NetIQ 多台 Sentinel 平行處理後（docs/archive/FEEDBACK-3-PLAN.md #2），這幾個計數可能被多個
    // 平行執行的 Task 同時呼叫；_run 的計數是屬性（不是欄位），無法用 Interlocked，改用 lock。
    // OnLogRecorded 也在此列——NLog target 的 Write 可能被多執行緒同時觸發（平行任務各自
    // 呼叫 Log.Warn 時），一併納入同一把鎖。
    private readonly object _countLock = new();

    // 快照與結束列互斥：結束後才落地的舊快照（FinishedAt 為空）會讓這趟永遠顯示執行中。
    // 鎖序固定 _rowLock → _countLock；_countLock 內不呼叫 store（store 內的 Warn 會經 NLog target 回來取 _countLock）。
    private readonly object _rowLock = new();
    private readonly List<PhaseScope> _openPhases = new();

    public long RunId => _run.RunId;

    /// <summary>Whether this recorder still owns the live parent run with the supplied durable id.</summary>
    public bool OwnsRun(long runId) => _store != null && runId > 0 && _run.RunId == runId && !_finished && !_ct.IsCancellationRequested;

    public int DaysAnalyzed
    {
        get { lock (_countLock) return _run.DaysAnalyzed; }
        set { lock (_countLock) _run.DaysAnalyzed = value; }
    }

    public int AiCalls
    {
        get { lock (_countLock) return _run.AiCalls; }
        set { lock (_countLock) _run.AiCalls = value; }
    }

    public int AiFailures
    {
        get { lock (_countLock) return _run.AiFailures; }
        set { lock (_countLock) _run.AiFailures = value; }
    }

    public bool Stopped
    {
        get { lock (_countLock) return _run.Stopped; }
        set { lock (_countLock) _run.Stopped = value; }
    }

    /// <summary>
    /// 開始一個階段。頂層階段的起訖各記一則里程碑，結束時追加一列快照（執行中與異常中斷的趟也看得到已完成的階段）；
    /// 子階段只隨結束列寫出，避免一趟的列數隨天數成長。同一個鍵可以有多個實例（多日的觸發式取數），以回傳的 scope 配對。
    /// </summary>
    public PhaseScope Phase(string key, string? note = null)
    {
        var record = new RunPhaseRecord(key, DateTime.Now) { Note = note };
        var scope = new PhaseScope(this, record);
        bool late;
        lock (_countLock)
        {
            late = _finished;
            if (!late)
            {
                (_run.Phases ??= new List<RunPhaseRecord>()).Add(record);
                _openPhases.Add(scope);
            }
        }
        // 不在 _countLock 內寫 log：NLog target 的 Write 先持 target 鎖再回頭取 _countLock，反過來拿就會死鎖
        if (late)
        {
            Log.Warn("執行紀錄已結束，略過階段 {0}", key);
            scope.Abandon();
            return scope;
        }

        if (RunTimingPhases.IsTopLevel(key))
        {
            Milestone($"{RunTimingPhases.Label(key)}開始{(note == null ? "" : $"（{note}）")}");
        }
        return scope;
    }

    private void CompletePhase(PhaseScope scope, RunPhaseRecord record, string outcome, string? note)
    {
        bool late;
        lock (_countLock)
        {
            late = _finished;
            if (!late)
            {
                record.FinishedAt = DateTime.Now;
                record.Outcome = outcome;
                if (note != null) record.Note = note;
                _openPhases.Remove(scope);
            }
        }
        if (late)
        {
            Log.Warn("執行紀錄已結束，略過階段 {0} 的結果", record.Key);
            return;
        }

        if (!RunTimingPhases.IsTopLevel(record.Key)) return;

        var elapsed = record.FinishedAt!.Value - record.StartedAt;
        Milestone($"{RunTimingPhases.Label(record.Key)}結束：{RunPhaseOutcomes.Label(outcome)}" +
                  $"{(record.Note == null ? "" : $"（{record.Note}）")}，耗時 {elapsed.ToString(@"hh\:mm\:ss")}");
        WriteSnapshot();
    }

    private void WriteSnapshot()
    {
        if (_store == null) return;
        try
        {
            lock (_rowLock)
            {
                BatchRun copy;
                lock (_countLock)
                {
                    if (_finished) return;
                    copy = _run.Snapshot();
                }
                _store.FinishRun(copy);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "階段快照寫入失敗（不影響本次分析）：{0}", ex.Message);
        }
    }

    /// <summary>沒明講結果就離開（例外或取消）時依取消權杖判定，例外路徑不留懸空階段</summary>
    private string ImplicitOutcome() => _ct.IsCancellationRequested ? RunPhaseOutcomes.Stopped : RunPhaseOutcomes.Failed;

    /// <summary>單一階段的範圍：<see cref="Complete"/> 寫結果（只有第一次有效），未呼叫就 Dispose 視為停止或失敗</summary>
    public sealed class PhaseScope : IDisposable
    {
        private readonly BatchRunRecorder _owner;
        private readonly RunPhaseRecord _record;
        private int _completed;

        internal PhaseScope(BatchRunRecorder owner, RunPhaseRecord record)
        {
            _owner = owner;
            _record = record;
        }

        internal void Abandon() => _completed = 1;

        public void Complete(string outcome, string? note = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1) return;
            _owner.CompletePhase(this, _record, outcome, note);
        }

        public void Dispose() => Complete(_owner.ImplicitOutcome());
    }

    /// <summary>里程碑：固定的 Info 級紀錄（開始/掃描完成/逐日分析完成/結束）</summary>
    public void Milestone(string message) => Append("Info", "Milestone", message, null);

    /// <summary>每趟直接寫入的執行輸出行數上限；超過後只保留最新 <see cref="OutputTailCapacity"/> 行</summary>
    private const int OutputHeadLimit = 1500;

    /// <summary>超過上限後環形緩衝保留的最新行數（結束時寫出）</summary>
    private const int OutputTailCapacity = 500;

    // 輸出可能來自並行的本機／NetIQ 兩路，計數與緩衝用專用鎖保護
    private readonly object _outputLock = new();
    private int _outputWritten;
    private int _outputSkipped;
    private readonly Queue<(DateTime LoggedAt, string Message)> _outputTail = new();
    private bool _outputFlushed;

    /// <summary>
    /// 執行輸出（IRunConsole 的白話訊息）寫進執行紀錄：Logger="Output"、Level="Info"。
    /// 不靠放寬 NLog 層級（會把 EF／HttpClient 的 Info 全寫進資料庫），而是由 console 包裝層親自呼叫。
    /// 前 1500 行直接寫入；之後只保留最新 500 行，於結束時連同略過行數一起寫出。空白行不記。
    /// </summary>
    public void Message(string message)
    {
        if (_store == null || string.IsNullOrWhiteSpace(message)) return;

        lock (_outputLock)
        {
            if (_outputFlushed) return;
            if (_outputWritten < OutputHeadLimit)
            {
                _outputWritten++;
                Append("Info", OutputLogger, message, null);
                return;
            }

            _outputTail.Enqueue((DateTime.Now, message));
            if (_outputTail.Count > OutputTailCapacity)
            {
                _outputTail.Dequeue();
                _outputSkipped++;
            }
        }
    }

    /// <summary>
    /// 記錄記憶體里程碑：start=true 寫 <see cref="BatchRun.MemoryStart"/>，否則寫 <see cref="BatchRun.MemoryEnd"/>，
    /// 並在診斷行留一句白話。結束那一次必須在 <see cref="Finish"/> 之前呼叫才會進最終列。失敗不中斷分析。
    /// </summary>
    public void RecordMemory(bool start, MemorySnapshot snapshot)
    {
        if (_store == null) return;
        try
        {
            var mark = new RunMemoryMark(
                snapshot.CapturedAt,
                snapshot.WorkingSetBytes,
                snapshot.PeakWorkingSetBytes,
                snapshot.ManagedHeapBytes,
                snapshot.GcCommittedBytes,
                snapshot.LohSizeBytes,
                snapshot.HardLimitBytes,
                snapshot.SqliteHeapHighwaterBytes,
                snapshot.NlogRuleCount);
            lock (_countLock)
            {
                if (start) _run.MemoryStart = mark;
                else _run.MemoryEnd = mark;
            }
            // 開始標記要立刻寫成一列：行程中途被殺（吃光記憶體最常見的結局）時沒有任何階段會結束，
            // 等第一個階段快照才落地的話，站台重啟補上的「異常中斷」列連開始標記都沒有。
            // 結束標記由呼叫端接著的 Finish 寫出，不必多一列。
            if (start) WriteSnapshot();

            var parts = new List<string>();
            if (mark.WorkingSetBytes is { } ws) parts.Add($"工作集 {ToMb(ws)} MB");
            if (mark.PeakWorkingSetBytes is { } peak) parts.Add($"峰值 {ToMb(peak)} MB");
            if (mark.ManagedHeapBytes is { } heap) parts.Add($".NET 堆 {ToMb(heap)} MB");
            if (mark.SqliteHeapHighwaterBytes is { } sqlite) parts.Add($"SQLite 峰值 {ToMb(sqlite)} MB");
            Milestone($"記憶體（{(start ? "開始" : "結束")}）：{string.Join("、", parts)}");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "記憶體里程碑記錄失敗（不影響本次分析）：{0}", ex.Message);
        }
    }

    private static long ToMb(long bytes) => (long)Math.Round(bytes / 1048576.0, MidpointRounding.AwayFromZero);

    /// <summary>致命原因親自寫進紀錄：最外層 catch 執行 Log.Fatal 時 NLog target 已卸除，靠它會遺失</summary>
    public void Fatal(Exception ex)
    {
        if (_store == null) return;
        lock (_countLock) _run.ErrorCount++;
        // 記憶體不足要在執行詳情的診斷行也看得到固定字樣，不只在排程狀態卡
        Append("Fatal", "Orchestrator", RunFailureReasons.Describe(ex), ex.ToString());
    }

    /// <summary>寫出超過上限後保留的最新輸出（任何結束路徑都經 Finish 呼叫；只寫一次）</summary>
    private void FlushOutputTail()
    {
        lock (_outputLock)
        {
            if (_outputFlushed) return;
            _outputFlushed = true;
            if (_outputSkipped > 0)
                Append("Info", OutputLogger, $"（中間略過 {_outputSkipped} 行輸出）", null);
            // 保留行的時間是當下記的：尾段才寫入不代表它們都發生在結束時刻
            while (_outputTail.Count > 0)
            {
                var (loggedAt, message) = _outputTail.Dequeue();
                Append("Info", OutputLogger, message, null, loggedAt);
            }
        }
    }

    /// <summary>這一趟目前還剩多少失敗與待補（趟末里程碑用；判定本身在 <see cref="RunResume"/>）。</summary>
    public RunResume CurrentResume()
    {
        lock (_countLock) return RunResume.Of(_run);
    }

    public void RecordDayAnalyzed()
    {
        lock (_countLock) _run.DaysAnalyzed++;
    }

    public void RecordAiCall(bool success)
    {
        lock (_countLock)
        {
            _run.AiCalls++;
            if (!success) _run.AiFailures++;
        }
    }

    public void RecordLocalOutcome(int daysAnalyzed, int daysFailed)
    {
        if (_store == null) return;
        lock (_countLock)
        {
            _run.LocalDaysAnalyzed = daysAnalyzed;
            _run.LocalDaysFailed = daysFailed;
        }
    }

    public void RecordNetiqOutcome(int daysAnalyzed, int daysFailed, int hostsSkipped)
    {
        if (_store == null) return;
        lock (_countLock)
        {
            _run.NetiqDaysAnalyzed = daysAnalyzed;
            _run.NetiqDaysFailed = daysFailed;
            _run.NetiqHostsSkipped = hostsSkipped;
        }
    }

    public void RecordPrtgOutcome(string outcome, int sensorsFetched, int sensorsFailed, int triggeredHosts, string? strategy = null,
        string? note = null)
    {
        if (_store == null) return;
        lock (_countLock)
        {
            _run.PrtgOutcome = outcome;
            _run.PrtgOutcomeNote = note;
            _run.PrtgStrategy = strategy;
            _run.PrtgSensorsFetched = sensorsFetched;
            _run.PrtgSensorsFailed = sensorsFailed;
            _run.PrtgTriggeredHosts = triggeredHosts;
        }
    }

    public void RecordPrtgDays(IReadOnlyList<PrtgDayStat> days)
    {
        if (_store == null) return;
        lock (_countLock)
        {
            _run.PrtgDays = days.ToList();
        }
    }

    public void Finish(int exitCode)
    {
        if (_finished || _store == null) return;

        // 還開著的階段先收掉（例外直接離開、沒走到各自的 Complete），結束列才看得到完整的階段
        List<PhaseScope> open;
        lock (_countLock) open = _openPhases.ToList();
        foreach (var phase in open) phase.Dispose();

        try
        {
            lock (_rowLock)
            {
                BatchRun final;
                lock (_countLock)
                {
                    if (_finished) return;
                    _finished = true;
                    _run.FinishedAt = DateTime.Now;
                    _run.ExitCode = exitCode;
                    // 上面收階段與進鎖之間才開、或 Complete 已過了只做一次的閘門但還沒取到鎖的階段：
                    // 直接掃紀錄本身，沒有訖的在同一把鎖內收掉，結束列不留永遠「進行中」的階段
                    var implicitOutcome = ImplicitOutcome();
                    foreach (var record in _run.Phases ?? Enumerable.Empty<RunPhaseRecord>())
                    {
                        if (record.FinishedAt != null) continue;
                        record.FinishedAt = _run.FinishedAt;
                        record.Outcome = implicitOutcome;
                    }
                    foreach (var straggler in _openPhases) straggler.Abandon();
                    _openPhases.Clear();
                    final = _run.Snapshot();
                }
                FlushOutputTail();
                _target?.Detach();
                _scope?.Dispose();
                _scope = null;
                _store.FinishRun(final);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "批次執行紀錄回填失敗：{0}", ex.Message);
        }
    }

    private void OnLogRecorded(string level, string loggerName)
    {
        lock (_countLock)
        {
            if (level is "Error" or "Fatal")
            {
                _run.ErrorCount++;
            }
            else if (level == "Warn")
            {
                if (!IsWarnCountExemptLogger(loggerName))
                    _run.WarnCount++;
            }
        }
    }

    private void Append(string level, string logger, string message, string? exceptionText, DateTime? loggedAt = null)
    {
        if (_store == null) return;

        try
        {
            _store.AppendLog(new BatchRunLog
            {
                RunId = _run.RunId,
                LoggedAt = loggedAt ?? DateTime.Now,
                Level = level,
                Logger = logger,
                Message = message,
                ExceptionText = exceptionText
            });
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "批次執行紀錄寫入失敗：{0}", ex.Message);
        }
    }

    /// <summary>
    /// 正常結束時 <see cref="Finish"/> 已先執行、這裡是 no-op；帶著未回填的紀錄走到這裡只有兩種情況：
    /// 優雅停止（取消權杖已觸發 → 記里程碑並標記「已停止」，不是失敗）或未預期例外（exit 1）。
    /// </summary>
    public void Dispose()
    {
        if (!_finished && _ct.IsCancellationRequested)
        {
            Milestone("執行已優雅停止（手動停止或執行窗口結束，已停在主機日邊界；剩餘缺漏日由下次執行自動回補）");
            _run.Stopped = true;
            Finish(exitCode: 0);
            return;
        }

        Finish(_run.ExitCode ?? 1);
        _scope?.Dispose();
        _scope = null;
    }

    /// <summary>
    /// 把 NLog 的 Warn 以上事件轉寫進執行紀錄。
    /// 用 target 而不是在各處加呼叫：既有程式碼已經在該記的地方記了 log，
    /// 逐處改呼叫既繁瑣又一定會漏。
    /// </summary>
    internal class BatchRunNLogTarget : TargetWithLayout
    {
        /// <summary>保護 <see cref="LogManager.Configuration"/> 這份全域設定的讀改寫（見 <see cref="Attach"/>）</summary>
        private static readonly object ConfigLock = new();

        private readonly BatchRunStore _store;
        private readonly long _runId;
        private readonly string _scopeToken;
        private readonly Action<string, string> _onRecorded;

        /// <summary>
        /// 附掛時自建的那一條規則；卸除時以同一個物件移除。
        /// 不用 <c>config.AddRule(...)</c>：它不回傳規則物件，卸除端只能留下 Targets.Count==0 的空殼
        /// （nlog.config 內 <c>Microsoft.*</c>／<c>System.Net.Http.*</c> 的 final 規則同樣沒有 target，不能憑此判斷刪除）。
        /// </summary>
        private LoggingRule? _rule;

        public BatchRunNLogTarget(BatchRunStore store, long runId, string scopeToken, Action<string, string> onRecorded)
        {
            _store = store;
            _runId = runId;
            _scopeToken = scopeToken;
            _onRecorded = onRecorded;
            // target 名稱要逐實例唯一：RunId 由各自的 store 配號，兩個獨立 store 會配出同一個號，
            // 同名 target 會在附掛時互相取代，先掛的那一趟從此收不到任何事件。
            Name = $"batchrun_{runId}_{scopeToken}";
        }

        /// <param name="configOverride">測試用：改掛在指定設定上而不動全域 <see cref="LogManager.Configuration"/></param>
        public void Attach(LoggingConfiguration? configOverride = null)
        {
            // 全域設定的讀改寫要互斥：取數排程與 AI 排程可以並行（AI 跟隨取數），
            // 兩趟執行各有自己的 recorder，附掛與卸除動的都是 LogManager.Configuration
            // 這一份全域物件。不互斥的話兩邊的 AddTarget/RemoveTarget 與 ReconfigExistingLoggers
            // 會交錯，先掛的那一趟可能被後來者的重設抹掉，從此收不到任何事件——
            // 而那個失敗是靜默的：執行紀錄只會顯示「這趟沒有任何警告」。
            lock (ConfigLock)
            {
                var config = configOverride ?? LogManager.Configuration;
                if (config == null) return;

                PurgeLeakedEmptyRules(config);

                config.AddTarget(this);
                _rule = new LoggingRule("*", LogLevel.Warn, LogLevel.Fatal, this);
                config.LoggingRules.Add(_rule);
                LogManager.ReconfigExistingLoggers();
            }
        }

        public void Detach(LoggingConfiguration? configOverride = null)
        {
            lock (ConfigLock)
            {
                var config = configOverride ?? LogManager.Configuration;
                if (config == null) return;

                if (_rule != null)
                {
                    config.LoggingRules.Remove(_rule);
                    _rule = null;
                }
                config.RemoveTarget(Name);
                LogManager.ReconfigExistingLoggers();
            }
        }

        /// <summary>
        /// 清掉舊版卸除時殘留的空殼規則：<c>*</c>、Warn～Fatal、沒有 target、非 final、無規則名稱。
        /// 判準必須全部成立才刪，nlog.config 內有意為之的 final 規則（沒有 writeTo）不會符合。
        /// </summary>
        private static void PurgeLeakedEmptyRules(LoggingConfiguration config)
        {
            var leaked = config.LoggingRules.Where(r =>
                r.LoggerNamePattern == "*"
                && r.Targets.Count == 0
                && !r.Final
                && string.IsNullOrEmpty(r.RuleName)
                && r.Levels.Count == 3
                && r.Levels.Contains(LogLevel.Warn)
                && r.Levels.Contains(LogLevel.Error)
                && r.Levels.Contains(LogLevel.Fatal)).ToList();
            foreach (var r in leaked) config.LoggingRules.Remove(r);
        }

        protected override void Write(LogEventInfo logEvent)
        {
            // 只收這一趟執行自己非同步流程內的事件：target 是全行程 Warn~Fatal 規則，
            // 不過濾的話夜間批次會把前景頁面的慢 SQL、互動 AI 逾時、AI 排程失敗全記成自己的問題。
            if (!ScopeContext.TryGetProperty(ScopeKey, out var v))
                return;

            if (v?.ToString() != _scopeToken)
                return;

            try
            {
                var level = logEvent.Level.Name;
                var shortLogger = ShortLoggerName(logEvent.LoggerName);
                _onRecorded(level, shortLogger);

                _store.AppendLog(new BatchRunLog
                {
                    RunId = _runId,
                    LoggedAt = logEvent.TimeStamp,
                    Level = level,
                    Logger = shortLogger,
                    Message = logEvent.FormattedMessage,
                    ExceptionText = logEvent.Exception?.ToString()
                });
            }
            catch
            {
                // 這裡在 NLog 的寫入路徑上——拋出例外會讓記 log 這件事本身變成故障點。
                // 執行紀錄寫不進去是可惜，讓分析因此中斷是更糟的結果
            }
        }

        private static string ShortLoggerName(string? loggerName)
        {
            if (string.IsNullOrEmpty(loggerName)) return string.Empty;

            var lastDot = loggerName.LastIndexOf('.');
            return lastDot >= 0 ? loggerName[(lastDot + 1)..] : loggerName;
        }
    }
}
