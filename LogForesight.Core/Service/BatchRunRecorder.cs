using NLog;
using NLog.Targets;

namespace LogForesight;

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

    private readonly BatchRunStore? _store;
    private readonly BatchRun _run;
    private readonly BatchRunNLogTarget? _target;
    private readonly CancellationToken _ct;
    private bool _finished;

    /// <param name="ct">執行用的取消權杖（docs/WEB-SCHEDULER-PLAN.md §1.4.4）：優雅停止時
    /// <see cref="OperationCanceledException"/> 會在 using 範圍結束時經 <see cref="Dispose"/> 回填——
    /// 這裡收下權杖，讓 Dispose 分得出「使用者停止」（記「已停止」）與「異常中斷」（exit 1）。
    /// console 傳 <see cref="CancellationToken.None"/>，行為與加入此參數前完全相同。</param>
    /// <param name="onRegistrationFailed">
    /// 登記失敗時的可見回報（docs/FEEDBACK-8-PLAN.md #3）：原本只寫 NLog，Web 排程執行時
    /// 這趟執行在執行監控頁會整筆「消失」（不是顯示失敗，是完全查不到這次執行發生過），
    /// 使用者只能翻 log 檔才查得到。呼叫端（Web）可傳入把這句話送進 <c>IRunConsole</c>，
    /// 讓狀態卡與執行明細看得到「這趟執行本次不會出現在執行監控」；null＝維持原本只寫 log。
    /// </param>
    public BatchRunRecorder(BatchRunStore? store, string hostName, string[] args, string? trigger = null,
        CancellationToken ct = default, Action<string>? onRegistrationFailed = null)
    {
        _store = store;
        _ct = ct;
        _run = new BatchRun
        {
            HostName = hostName,
            StartedAt = DateTime.Now,
            AppVersion = typeof(BatchRunRecorder).Assembly.GetName().Version?.ToString() ?? "unknown",
            Args = string.Join(" ", args),
            Trigger = trigger
        };

        if (_store == null) return;

        try
        {
            _store.StartRun(_run);

            // 掛上 NLog target：Warn 以上自動流入執行紀錄，不需要在 codebase 各處加呼叫。
            // 完整診斷仍在 logs\logforesight.log，這裡只收「一眼確認有沒有問題」需要的部分
            _target = new BatchRunNLogTarget(_store, _run.RunId, OnLogRecorded);
            _target.Attach();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "批次執行紀錄登記失敗（不影響本次分析）：{0}", ex.Message);
            onRegistrationFailed?.Invoke($"執行紀錄登記失敗（不影響本次分析，但這趟執行本次不會出現在執行監控）：{ex.Message}");
            _store = null;
        }
    }

    // NetIQ 多台 Sentinel 平行處理後（docs/FEEDBACK-3-PLAN.md #2），這幾個計數可能被多個
    // 平行執行的 Task 同時呼叫；_run 的計數是屬性（不是欄位），無法用 Interlocked，改用 lock。
    // OnLogRecorded 也在此列——NLog target 的 Write 可能被多執行緒同時觸發（平行任務各自
    // 呼叫 Log.Warn 時），一併納入同一把鎖。
    private readonly object _countLock = new();

    public long RunId => _run.RunId;

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

    public void Finish(int exitCode)
    {
        if (_finished || _store == null) return;
        _finished = true;

        try
        {
            _target?.Detach();
            _run.FinishedAt = DateTime.Now;
            _run.ExitCode = exitCode;
            _store.FinishRun(_run);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "批次執行紀錄回填失敗：{0}", ex.Message);
        }
    }

    private void OnLogRecorded(string level)
    {
        lock (_countLock)
        {
            if (level is "Error" or "Fatal") _run.ErrorCount++;
            else if (level == "Warn") _run.WarnCount++;
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
    }

    /// <summary>
    /// 把 NLog 的 Warn 以上事件轉寫進執行紀錄。
    /// 用 target 而不是在各處加呼叫：既有程式碼已經在該記的地方記了 log，
    /// 逐處改呼叫既繁瑣又一定會漏。
    /// </summary>
    private class BatchRunNLogTarget : TargetWithLayout
    {
        private readonly BatchRunStore _store;
        private readonly long _runId;
        private readonly Action<string> _onRecorded;

        public BatchRunNLogTarget(BatchRunStore store, long runId, Action<string> onRecorded)
        {
            _store = store;
            _runId = runId;
            _onRecorded = onRecorded;
            Name = "batchrun";
        }

        public void Attach()
        {
            var config = LogManager.Configuration;
            if (config == null) return;

            config.AddTarget(this);
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, this);
            LogManager.ReconfigExistingLoggers();
        }

        public void Detach()
        {
            var config = LogManager.Configuration;
            if (config == null) return;

            config.RemoveTarget(Name);
            LogManager.ReconfigExistingLoggers();
        }

        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                var level = logEvent.Level.Name;
                _onRecorded(level);

                _store.AppendLog(new BatchRunLog
                {
                    RunId = _runId,
                    LoggedAt = logEvent.TimeStamp,
                    Level = level,
                    Logger = ShortLoggerName(logEvent.LoggerName),
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
