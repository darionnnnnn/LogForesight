using System.Text;
using LogForesight.Core;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>單次 PRTG probe 的快照，供狀態 API 一次性讀出</summary>
public record PrtgProbeSnapshot(
    bool IsRunning, DateTime? StartedAt, DateTime? CompletedAt,
    bool? Success, string? LatestMessage, IReadOnlyList<string> Output);

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
                _latestMessage, _output.ToList());
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

    public PrtgProbeService(ISystemSettingsStore settings, PrtgProbeRunState state)
    {
        _settings = settings;
        _state = state;
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
            Output = s.Output
        };
    }

    public bool TryStart(out string? error)
    {
        error = null;
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行探測。";
            return false;
        }

        // 先判斷才解密：CryptoHelper.Decrypt 對非本格式的值會擲例外，而這個欄位在
        // 匯入或手動編輯 blob 的路徑上有可能是明文（同 SentinelConnectionFactory 的相容寫法）
        var token = string.IsNullOrEmpty(s.PrtgApiTokenEnc)
            ? null
            : CryptoHelper.IsEncrypted(s.PrtgApiTokenEnc)
                ? CryptoHelper.Decrypt(s.PrtgApiTokenEnc)
                : s.PrtgApiTokenEnc;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "尚未設定 PRTG API Token，無法執行探測。";
            return false;
        }

        if (!_state.TryBegin())
        {
            error = "探測已在執行中。";
            return false;
        }

        var console = new PrtgProbeConsole(_state);
        PrtgClient? client = null;
        try
        {
            client = new PrtgClient(s.PrtgUrl, token, s.PrtgTimeoutSeconds, s.PrtgIgnoreSslErrors);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _state.EndRun(false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                using (client)
                {
                    success = await PrtgProbeRunner.RunAsync(client, console);
                }
            }
            catch (Exception ex)
            {
                console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}");
                success = false;
            }
            finally
            {
                _state.EndRun(success);
            }
        });

        return true;
    }
}
