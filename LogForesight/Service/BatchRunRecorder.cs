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

    private readonly IBatchRunStore? _store;
    private readonly BatchRun _run;
    private readonly BatchRunNLogTarget? _target;
    private bool _finished;

    public BatchRunRecorder(IBatchRunStore? store, string hostName, string[] args)
    {
        _store = store;
        _run = new BatchRun
        {
            HostName = hostName,
            StartedAt = DateTime.Now,
            AppVersion = typeof(BatchRunRecorder).Assembly.GetName().Version?.ToString() ?? "unknown",
            Args = string.Join(" ", args)
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
            _store = null;
        }
    }

    public long RunId => _run.RunId;

    /// <summary>里程碑：固定的 Info 級紀錄（開始/掃描完成/逐日分析完成/結束）</summary>
    public void Milestone(string message) => Append("Info", "Milestone", message, null);

    public void RecordDayAnalyzed() => _run.DaysAnalyzed++;

    public void RecordAiCall(bool success)
    {
        _run.AiCalls++;
        if (!success) _run.AiFailures++;
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
        if (level is "Error" or "Fatal") _run.ErrorCount++;
        else if (level == "Warn") _run.WarnCount++;
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

    public void Dispose() => Finish(_run.ExitCode ?? 1);

    /// <summary>
    /// 把 NLog 的 Warn 以上事件轉寫進執行紀錄。
    /// 用 target 而不是在各處加呼叫：既有程式碼已經在該記的地方記了 log，
    /// 逐處改呼叫既繁瑣又一定會漏。
    /// </summary>
    private class BatchRunNLogTarget : TargetWithLayout
    {
        private readonly IBatchRunStore _store;
        private readonly long _runId;
        private readonly Action<string> _onRecorded;

        public BatchRunNLogTarget(IBatchRunStore store, long runId, Action<string> onRecorded)
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
