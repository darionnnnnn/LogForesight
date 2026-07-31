using System.Text;

namespace LogForesight.Web.Services;

/// <summary>單次 probe 的快照（docs/WEB-SCHEDULER-PLAN.md §1.4.11），供狀態 API 一次性讀出，避免呼叫端分次讀取多個屬性時看到不一致的中間狀態</summary>
public record NetiqProbeSnapshot(
    bool IsRunning, long? SentinelId, string? SentinelName,
    DateTime? StartedAt, DateTime? CompletedAt, bool? Success,
    string? LatestMessage, string Output);

/// <summary>
/// probe 的行程內單例執行狀態＋**自成一個併發 1 的 gate**——與排程/手動分析的
/// <see cref="SchedulerRunState"/> 完全分開（docs/WEB-SCHEDULER-PLAN.md §1.4.11：
/// probe 是小規模診斷查詢，不該被夜間分析互斥擋住，但同時只允許一個 probe 在跑）。
/// </summary>
public class NetiqProbeRunState
{
    private readonly object _lock = new();
    private readonly StringBuilder _output = new();

    private bool _isRunning;
    private long? _sentinelId;
    private string? _sentinelName;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private bool? _success;
    private string? _latestMessage;

    /// <summary>gate 本體：已在跑就回 false，呼叫端不得再開一個</summary>
    public bool TryBegin(long sentinelId, string sentinelName)
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
            _sentinelId = sentinelId;
            _sentinelName = sentinelName;
            _startedAt = DateTime.Now;
            _completedAt = null;
            _success = null;
            _latestMessage = null;
            _output.Clear();
            return true;
        }
    }

    public void AppendLine(string message)
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _output.AppendLine(message);
            if (!string.IsNullOrWhiteSpace(message)) _latestMessage = message;
        }
    }

    public void EndRun(bool success)
    {
        lock (_lock)
        {
            _isRunning = false;
            _success = success;
            _completedAt = DateTime.Now;
        }
    }

    public NetiqProbeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new NetiqProbeSnapshot(
                _isRunning, _sentinelId, _sentinelName, _startedAt, _completedAt, _success,
                _latestMessage, _output.ToString());
        }
    }
}
