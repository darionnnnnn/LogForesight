using System.Collections.Concurrent;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LogForesight.Core.Service;

/// <summary>
/// 執行紀錄的 NLog 擷取：全行程只有一個常駐 target（<see cref="RunLogTarget"/>）與一條 Warn～Fatal 規則，
/// 啟動時安裝一次（Web 在 Program.cs，其餘路徑由 <see cref="BatchRunRecorder"/> 建構時冪等補裝）。
/// 每個 recorder 在自己的非同步範圍內以 <see cref="ScopeContext"/> 帶逐實例識別（<see cref="ScopeKey"/>），
/// target 依識別把事件交給對應的 recorder；沒有識別的事件不處理。
/// target 的 Write 只做記憶體內的計數與入佇列，不碰資料庫（寫入見 <see cref="RunLogQueue"/>）。
/// </summary>
public static class RunLogCapture
{
    /// <summary>
    /// scope 屬性鍵。值是**這個 recorder 實例**的識別碼，不是 RunId——RunId 由各自的 store 配號，
    /// 兩個獨立 store 會配出同一個號，那時 target 就會把別人那一趟的事件當成自己的。識別碼逐實例產生，不會碰撞。
    /// </summary>
    internal const string ScopeKey = "lf_run_scope";

    /// <summary>常駐 target 在 NLog 設定內的名稱</summary>
    public const string TargetName = "lf_run_log";

    private static readonly object InstallLock = new();
    private static readonly RunLogTarget Target = new() { Name = TargetName };
    private static readonly LoggingRule Rule = NewRule();

    // 長駐集合：每個進行中的 recorder 一筆，recorder 結束（Finish／Dispose）時移除；
    // 上限等於同時進行中的執行數（取數與 AI 各一趟），沒有結束的 recorder 才會殘留。
    private static readonly ConcurrentDictionary<string, Action<LogEventInfo>> Sinks = new(StringComparer.Ordinal);

    private static LoggingConfiguration? _installedOn;

    private static LoggingRule NewRule()
    {
        var rule = new LoggingRule("*", LogLevel.Warn, LogLevel.Fatal, Target);
        return rule;
    }

    /// <summary>目前已登記、尚未結束的 recorder 數（診斷與測試用）</summary>
    internal static int ActiveSinkCount => Sinks.Count;

    /// <summary>
    /// 確保目前的全域 NLog 設定掛著常駐 target 與規則。已安裝在同一份設定上時只是兩個檢查；
    /// 設定被整份換掉（測試或重新載入）時補裝一次。安裝完成後全行程的規則數與 target 數不再隨執行變動。
    /// </summary>
    public static void EnsureInstalled()
    {
        var config = LogManager.Configuration;
        if (config != null && ReferenceEquals(config, _installedOn) && config.LoggingRules.Contains(Rule)) return;

        lock (InstallLock)
        {
            config = LogManager.Configuration;
            if (config == null)
            {
                config = new LoggingConfiguration();
                LogManager.Configuration = config;
            }
            if (ReferenceEquals(config, _installedOn) && config.LoggingRules.Contains(Rule)) return;

            if (config.FindTargetByName(TargetName) == null) config.AddTarget(Target);
            if (!config.LoggingRules.Contains(Rule)) config.LoggingRules.Add(Rule);
            _installedOn = config;
            LogManager.ReconfigExistingLoggers();
        }
    }

    internal static void Register(string token, Action<LogEventInfo> sink) => Sinks[token] = sink;

    internal static void Unregister(string token) => Sinks.TryRemove(token, out _);

    internal static string ShortLoggerName(string? loggerName)
    {
        if (string.IsNullOrEmpty(loggerName)) return string.Empty;
        var lastDot = loggerName.LastIndexOf('.');
        return lastDot >= 0 ? loggerName[(lastDot + 1)..] : loggerName;
    }

    /// <summary>
    /// 依 scope 識別把事件分派給 recorder。沒有識別、識別為空字串或識別已登出都直接略過：
    /// 不過濾的話夜間批次會把前景頁面的慢 SQL、互動 AI 逾時、AI 排程失敗全記成自己的問題。
    /// </summary>
    internal static void Dispatch(LogEventInfo logEvent)
    {
        if (!ScopeContext.TryGetProperty(ScopeKey, out var value)) return;
        var token = value as string ?? value?.ToString();
        if (string.IsNullOrEmpty(token)) return;
        if (Sinks.TryGetValue(token, out var sink)) sink(logEvent);
    }

    /// <summary>常駐 target：Write 只做分派，不做任何 I/O</summary>
    internal sealed class RunLogTarget : Target
    {
        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                Dispatch(logEvent);
            }
            catch
            {
                // 在 NLog 的寫入路徑上：擲出例外會讓記 log 本身變成故障點
            }
        }
    }
}

/// <summary>
/// 單一 recorder 的有界寫入佇列：記錄路徑只把事件放進來，由專屬背景執行緒依序寫庫，
/// 所以記 log 的呼叫端不會等資料庫（交易內、持鎖中記警告都不會與寫入互鎖）。
/// 上限只約束「擷取來的警告以上事件」——里程碑、執行輸出這類 recorder 親自寫的行數有自己的上限，
/// 且必須與擷取事件同一條佇列才保有先後順序（診斷行以 LogId 排序）。
/// 佇列滿時丟棄新來的擷取事件並累計，寫入端在下一批之後補一筆說明；結束時 <see cref="Complete"/> 排空。
/// </summary>
internal sealed class RunLogQueue
{
    /// <summary>未寫出的擷取事件上限（含寫入中那一批）</summary>
    public const int DefaultCapacity = 2000;

    private const int BatchSize = 256;

    /// <summary>
    /// 測試專用：新建的佇列改在呼叫端執行緒直接寫入、不開背景執行緒。
    /// 多數測試共用 <c>EfSqliteFixture</c> 那一條記憶體連線，背景寫入與測試本身的資料庫操作同時用它會互相打斷
    /// （SQLite 擲「active statements」）；正式環境每次操作各開連線，不受影響。驗證非同步行為的測試改用檔案型資料庫並關掉這個旗標。
    /// </summary>
    internal static bool WriteInline { get; set; }

    private readonly bool _inline;

    private readonly object _gate = new();
    private readonly Queue<(BatchRunLog Log, bool Captured)> _queue = new();
    private readonly long _runId;
    private readonly int _capacity;
    private readonly Action<BatchRunLog> _write;
    private readonly Action<long> _onDropNote;
    private int _unwrittenCaptured;
    private int _inFlight;
    private long _dropped;
    private long _droppedTotal;
    private bool _completed;
    private Thread? _thread;

    /// <param name="write">實際寫庫的動作（只會在背景執行緒或 <see cref="Complete"/> 之後的呼叫端以外呼叫）；自行處理例外</param>
    /// <param name="onDropNote">寫出「丟棄說明」那一筆時回呼，帶本次說明涵蓋的筆數（讓 recorder 把它計入警告數）</param>
    public RunLogQueue(long runId, Action<BatchRunLog> write, Action<long> onDropNote, int capacity = DefaultCapacity,
        bool? inline = null)
    {
        _inline = inline ?? WriteInline;
        _runId = runId;
        _write = write;
        _onDropNote = onDropNote;
        _capacity = capacity;
    }

    /// <summary>累計丟棄的擷取事件數</summary>
    public long DroppedTotal
    {
        get { lock (_gate) return _droppedTotal; }
    }

    /// <summary>擷取來的事件入佇列；佇列已滿或已結束時丟棄並回傳 false。永不阻塞在 I/O 上。</summary>
    public bool TryEnqueueCaptured(BatchRunLog log)
    {
        if (_inline) return WriteNow(log);
        lock (_gate)
        {
            if (_completed) return false;
            if (_unwrittenCaptured >= _capacity)
            {
                _dropped++;
                _droppedTotal++;
                EnsureWriter();
                Monitor.PulseAll(_gate);
                return false;
            }
            _unwrittenCaptured++;
            _queue.Enqueue((log, true));
            EnsureWriter();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>recorder 親自寫的行（不受上限約束）；已結束時回傳 false，由呼叫端直接寫</summary>
    public bool TryEnqueueForced(BatchRunLog log)
    {
        if (_inline) return WriteNow(log);
        lock (_gate)
        {
            if (_completed) return false;
            _queue.Enqueue((log, false));
            EnsureWriter();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    private bool WriteNow(BatchRunLog log)
    {
        lock (_gate) if (_completed) return false;
        _write(log);
        return true;
    }

    /// <summary>等到目前已入佇列的內容都寫完（測試與需要即時讀回的呼叫端用）；逾時回傳 false</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (_inline) return true;
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_queue.Count > 0 || _inFlight > 0 || _dropped > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                if (_thread == null) return false;
                Monitor.Wait(_gate, remaining);
            }
            return true;
        }
    }

    /// <summary>停止收件並排空：回傳時所有已入佇列的事件都已寫完（逾時才放棄，回傳 false）</summary>
    public bool Complete(TimeSpan timeout)
    {
        Thread? thread;
        lock (_gate)
        {
            _completed = true;
            thread = _thread;
            Monitor.PulseAll(_gate);
        }
        return thread == null || thread.Join(timeout);
    }

    // 呼叫端須持有 _gate。執行緒不繼承呼叫端的 ExecutionContext：寫入端自己的 log
    // 不該帶著這一趟的 scope 識別又回流進同一條佇列。
    private void EnsureWriter()
    {
        if (_thread != null) return;
        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "lf-run-log-writer" };
        using (ExecutionContext.SuppressFlow())
        {
            _thread.Start();
        }
    }

    private void WriterLoop()
    {
        var batch = new List<(BatchRunLog Log, bool Captured)>(BatchSize);
        while (true)
        {
            long dropped;
            lock (_gate)
            {
                while (_queue.Count == 0 && _dropped == 0 && !_completed) Monitor.Wait(_gate);
                if (_queue.Count == 0 && _dropped == 0)
                {
                    Monitor.PulseAll(_gate);
                    return;
                }
                batch.Clear();
                while (batch.Count < BatchSize && _queue.Count > 0) batch.Add(_queue.Dequeue());
                _inFlight = batch.Count;
                dropped = _dropped;
                _dropped = 0;
            }

            foreach (var (log, captured) in batch)
            {
                _write(log);
                if (!captured) continue;
                lock (_gate) _unwrittenCaptured--;
            }

            if (dropped > 0)
            {
                _write(new BatchRunLog
                {
                    RunId = _runId,
                    LoggedAt = DateTime.Now,
                    Level = "Warn",
                    Logger = DropNoteLogger,
                    Message = $"執行紀錄的寫入佇列已滿（上限 {_capacity} 筆），有 {dropped} 筆警告以上紀錄沒有寫進這趟執行紀錄；完整內容仍在 logs 檔案。"
                });
                _onDropNote(dropped);
            }

            lock (_gate)
            {
                _inFlight = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>丟棄說明那一筆的 logger 名稱</summary>
    public const string DropNoteLogger = "RunLogCapture";
}
