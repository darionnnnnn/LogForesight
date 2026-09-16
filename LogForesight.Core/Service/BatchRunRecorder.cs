using NLog;
using NLog.Targets;

namespace LogForesight.Core.Service;

/// <summary>
/// 批次執行紀錄的收集（docs/WEB-SPEC.md §2.1 Phase 4、§11-5）。
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
    private const string ExemptWarnLogger = "SqlPerformanceMonitor";

    private readonly BatchRunStore? _store;
    private readonly BatchRun _run;
    private readonly BatchRunNLogTarget? _target;
    private readonly CancellationToken _ct;
    private bool _finished;
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
    /// <remarks>
    /// 【AsyncLocal 作用域限制】<see cref="ScopeContext"/> 底層是 <see cref="AsyncLocal{T}"/>：
    /// 它沿著非同步呼叫鏈**向下**傳遞，但不會向外（向上）傳播。因此 recorder 必須在
    /// 「涵蓋整趟執行的那個方法本體」內建構（現況是 <c>AnalysisOrchestrator.RunAsync</c> 的本體），
    /// 之後所有 <c>await</c>、<c>Task.WhenAll</c>、<c>Parallel.ForEachAsync</c> 子任務才繼承得到這個 scope。
    /// 若日後把建構搬進一個被 <c>await</c> 的輔助 async 方法，scope 會在該方法返回時就消失，
    /// 整趟執行的 Warn 全部靜默丟失且沒有任何訊號。
    /// </remarks>
    public BatchRunRecorder(BatchRunStore? store, string hostName, string[] args, string? trigger = null,
        CancellationToken ct = default, Action<string>? onRegistrationFailed = null, string? jobType = null)
    {
        _store = store;
        _ct = ct;
        _run = new BatchRun
        {
            HostName = hostName,
            StartedAt = DateTime.Now,
            AppVersion = typeof(BatchRunRecorder).Assembly.GetName().Version?.ToString() ?? "unknown",
            Args = string.Join(" ", args),
            Trigger = trigger,
            JobType = jobType
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

    public long RunId => _run.RunId;

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

    /// <summary>里程碑：固定的 Info 級紀錄（開始/掃描完成/逐日分析完成/結束）</summary>
    public void Milestone(string message) => Append("Info", "Milestone", message, null);

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

    public void RecordPrtgOutcome(string outcome, int sensorsFetched, int sensorsFailed, int triggeredHosts)
    {
        if (_store == null) return;
        lock (_countLock)
        {
            _run.PrtgOutcome = outcome;
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
        _finished = true;

        try
        {
            _target?.Detach();
            _scope?.Dispose();
            _scope = null;
            _run.FinishedAt = DateTime.Now;
            _run.ExitCode = exitCode;
            _store.FinishRun(_run);
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
                if (!string.Equals(loggerName, ExemptWarnLogger, StringComparison.Ordinal))
                    _run.WarnCount++;
            }
        }
    }

    private void Append(string level, string logger, string message, string? exceptionText)
    {
        if (_store == null) return;

        try
        {
            _store.AppendLog(new BatchRunLog
            {
                RunId = _run.RunId,
                LoggedAt = DateTime.Now,
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
    private class BatchRunNLogTarget : TargetWithLayout
    {
        /// <summary>保護 <see cref="LogManager.Configuration"/> 這份全域設定的讀改寫（見 <see cref="Attach"/>）</summary>
        private static readonly object ConfigLock = new();

        private readonly BatchRunStore _store;
        private readonly long _runId;
        private readonly string _scopeToken;
        private readonly Action<string, string> _onRecorded;

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

        public void Attach()
        {
            // 全域設定的讀改寫要互斥：取數排程與 AI 排程可以並行（AI 跟隨取數），
            // 兩趟執行各有自己的 recorder，附掛與卸除動的都是 LogManager.Configuration
            // 這一份全域物件。不互斥的話兩邊的 AddTarget/RemoveTarget 與 ReconfigExistingLoggers
            // 會交錯，先掛的那一趟可能被後來者的重設抹掉，從此收不到任何事件——
            // 而那個失敗是靜默的：執行紀錄只會顯示「這趟沒有任何警告」。
            lock (ConfigLock)
            {
                var config = LogManager.Configuration;
                if (config == null) return;

                config.AddTarget(this);
                config.AddRule(LogLevel.Warn, LogLevel.Fatal, this);
                LogManager.ReconfigExistingLoggers();
            }
        }

        public void Detach()
        {
            lock (ConfigLock)
            {
                var config = LogManager.Configuration;
                if (config == null) return;

                config.RemoveTarget(Name);
                LogManager.ReconfigExistingLoggers();
            }
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
