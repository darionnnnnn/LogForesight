using System.Text;

namespace LogForesight.Web.Services;

/// <summary>單次 probe 的快照（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.11），供狀態 API 一次性讀出，避免呼叫端分次讀取多個屬性時看到不一致的中間狀態</summary>
public record NetiqProbeSnapshot(
    bool IsRunning, long? SentinelId, string? SentinelName,
    DateTime? StartedAt, DateTime? CompletedAt, bool? Success,
    string? LatestMessage, string Output, string Mode = "legacy");

/// <summary>
/// probe 的行程內單例執行狀態＋**自成一個併發 1 的 gate**——與排程/手動分析的
/// <see cref="SchedulerRunState"/> 完全分開（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.11：
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
    private string _mode = "legacy";
    private int _metadataOutputBytes;
    private bool _metadataOutputOverflow;

    /// <summary>gate 本體：已在跑就回 false，呼叫端不得再開一個</summary>
    public bool TryBegin(long sentinelId, string sentinelName)
        => TryBeginCore(sentinelId, sentinelName, "legacy");

    public bool TryBeginMetadata(long sentinelId, string sentinelName)
        => TryBeginCore(sentinelId, sentinelName, "metadata-shape");

    private bool TryBeginCore(long sentinelId, string sentinelName, string mode)
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
            _sentinelId = sentinelId;
            _sentinelName = sentinelName;
            _mode = mode;
            _startedAt = DateTime.Now;
            _completedAt = null;
            _success = null;
            _latestMessage = null;
            _metadataOutputBytes = 0;
            _metadataOutputOverflow = false;
            _output.Clear();
            return true;
        }
    }

    public void AppendLine(string message)
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            if (_mode == "metadata-shape")
            {
                if (_metadataOutputOverflow) return;
                var lineBytes = Encoding.UTF8.GetByteCount(message) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                if (_metadataOutputBytes + lineBytes > LogForesight.Core.Service.NetiqEvidenceMetadataProbeRunner.MaxReportBytes)
                {
                    const string limitReport = "NetIQ response field-shape probe failed; reason=report-byte-limit-exceeded. Details withheld.";
                    _output.Clear();
                    _output.AppendLine(limitReport);
                    _metadataOutputBytes = Encoding.UTF8.GetByteCount(limitReport) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    _metadataOutputOverflow = true;
                    _latestMessage = limitReport;
                    return;
                }
                _metadataOutputBytes += lineBytes;
            }
            _output.AppendLine(message);
            if (!string.IsNullOrWhiteSpace(message)) _latestMessage = message;
        }
    }

    public void EndRun(bool success)
    {
        lock (_lock)
        {
            _isRunning = false;
            _success = success && !_metadataOutputOverflow;
            _completedAt = DateTime.Now;
        }
    }

    public NetiqProbeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new NetiqProbeSnapshot(
                _isRunning, _sentinelId, _sentinelName, _startedAt, _completedAt, _success,
                _latestMessage, _output.ToString(), _mode);
        }
    }
}
