using System.Text;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>單次 PRTG probe 的快照，供狀態 API 一次性讀出</summary>
public record PrtgProbeSnapshot(
    bool IsRunning, DateTime? StartedAt, DateTime? CompletedAt,
    bool? Success, string? LatestMessage, IReadOnlyList<string> Output,
    bool Cancelled = false);

/// <summary>
/// PRTG probe 的行程內單例執行狀態＋併發 1 的 gate。
/// </summary>
public class PrtgProbeRunState
{
    private readonly object _lock = new();
    private readonly List<string> _output = new();

    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private bool? _success;
    private string? _latestMessage;

    private CancellationTokenSource? _cts;
    private bool _cancelled;

    /// <summary>最近一趟是否被使用者停止（新一趟開始時歸零）。</summary>
    public bool Cancelled
    {
        get { lock (_lock) return _cancelled; }
    }

    /// <summary>
    /// 搶執行權並建立本趟的取消來源。已在執行中回 false。
    /// </summary>
    public bool TryBeginRun(out CancellationToken token)
    {
        lock (_lock)
        {
            if (!TryBegin())
            {
                token = default;
                return false;
            }
            _cts = new CancellationTokenSource();
            _cancelled = false;
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>要求停止進行中的探測；沒有執行中時回 false。</summary>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (_cts == null || !_isRunning) return false;
            _cts.Cancel();
            return true;
        }
    }

    /// <summary>結束本趟：記住是否被停止、釋放並清空取消來源，再結束執行狀態。</summary>
    public void FinishRun(bool success, bool cancelled)
    {
        lock (_lock)
        {
            _cancelled = cancelled;
            _cts?.Dispose();
            _cts = null;
        }
        EndRun(success);
    }

    public bool TryBegin()
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
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
            _output.Add(message);
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

    public PrtgProbeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new PrtgProbeSnapshot(
                _isRunning, _startedAt, _completedAt, _success,
                _latestMessage, _output.ToList(), _cancelled);
        }
    }
}

/// <summary>
/// 極薄的 IRunConsole adapter：將探測輸出逐行收集至 <see cref="PrtgProbeRunState"/>。
/// </summary>
public class PrtgProbeConsole : IRunConsole
{
    private readonly PrtgProbeRunState _state;

    public PrtgProbeConsole(PrtgProbeRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// PRTG 環境探測服務：Singleton，背景執行 PRTG 探測任務並維護狀態。
/// </summary>
public class PrtgProbeService
{
    private readonly ISystemSettingsStore _settings;
    private readonly PrtgProbeRunState _state;
    // 必要相依，不設預設值：探測與回填會打同一台 PRTG，這道互斥是保護。
    // 做成可選參數的話，哪天有人漏注入，保護會靜默消失而不是編譯失敗。
    private readonly PrtgBackfillRunState _backfillState;
    private readonly SchedulerRunState _schedulerState;
    private readonly PrtgStructureSyncRunState _structureState;
    // 站台對照要對照的三個本機事實來源：鏡像、主機清單、Sentinel 清單。
    // 同樣不設預設值——漏注入的話這一段會靜默消失，而不是編譯失敗。
    private readonly StorageBackend _backend;
    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;

    public PrtgProbeService(ISystemSettingsStore settings, PrtgProbeRunState state, PrtgBackfillRunState backfillState,
        StorageBackend backend, IHostStore hosts, ISentinelStore sentinels,
        SchedulerRunState schedulerState, PrtgStructureSyncRunState structureState)
    {
        _settings = settings;
        _state = state;
        _backfillState = backfillState;
        _schedulerState = schedulerState;
        _structureState = structureState;
        _backend = backend;
        _hosts = hosts;
        _sentinels = sentinels;
    }

    public PrtgProbeStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        return new PrtgProbeStatusDto
        {
            IsRunning = s.IsRunning,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            Success = s.Success,
            LatestMessage = s.LatestMessage,
            Output = s.Output,
            Cancelled = s.Cancelled
        };
    }

    public bool TryCancel() => _state.TryCancel();

    public bool TryStartDataFlow(out string? error, out bool isConflict) =>
        TryStartCore(dataFlow: true, out error, out isConflict);

    /// <param name="error">拒絕原因；成功時為 null。</param>
    /// <param name="isConflict">true＝被互斥擋下（回填執行中／探測已在執行中），呼叫端該回 409；
    /// false＝設定不齊，該回 400。與回填、結構同步的 TryStart 同一套。</param>
    public bool TryStart(out string? error, out bool isConflict)
        => TryStartCore(dataFlow: false, out error, out isConflict);

    private bool TryStartCore(bool dataFlow, out string? error, out bool isConflict)
    {
        error = null;
        isConflict = false;
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行探測。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法執行探測。";
            return false;
        }

        if (_backfillState.Snapshot().IsRunning)
        {
            error = "回填執行中，請稍後再試。";
            isConflict = true;
            return false;
        }

        if (dataFlow && (_schedulerState.IsRunning || _structureState.Snapshot().IsRunning))
        {
            error = _schedulerState.IsRunning
                ? "取數排程正在執行，請完成後再驗證小範圍資料流。"
                : "結構同步正在執行，請完成後再驗證小範圍資料流。";
            isConflict = true;
            return false;
        }

        if (!_state.TryBeginRun(out var ct))
        {
            error = "探測已在執行中。";
            isConflict = true;
            return false;
        }

        var console = new PrtgProbeConsole(_state);
        PrtgClient? client = null;
        try
        {
            client = PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _state.FinishRun(false, false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        _ = Task.Run(async () =>
        {
            var success = false;
            var cancelled = false;
            try
            {
                using (client)
                {
                    if (dataFlow)
                    {
                        success = await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts, s, console, ct);
                    }
                    else
                    {
                        success = await PrtgProbeRunner.RunAsync(client, console, ct);
                    }

                    // 站台對照只在探測本身成功後才做（連線都不通時對照不出東西），
                    // 而且不影響 success：它是附加資訊，不是探測的成敗條件。
                    if (success && !dataFlow)
                    {
                        try
                        {
                            await PrtgProbeSiteCheck.RunAsync(client, console, _backend.PrtgStore(), _hosts, s, _sentinels.GetAll(),
                                new PrtgLiveGuardSource(client, ct, console), ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            console.WriteLine($"站台對照發生未預期錯誤：{ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                success = false;
                console.WriteLine();
                console.WriteLine("探測已由使用者停止。");
            }
            catch (Exception ex)
            {
                console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}");
                success = false;
            }
            finally
            {
                _state.FinishRun(success, cancelled);
            }
        });

        return true;
    }
}
