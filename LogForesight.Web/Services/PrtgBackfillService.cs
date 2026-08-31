using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// PRTG 歷史回填的行程內單例執行狀態＋併發 1 的 gate。
/// 繼承自 <see cref="PrtgProbeRunState"/> 避免重複實作。
/// </summary>
public class PrtgBackfillRunState : PrtgProbeRunState
{
}

/// <summary>
/// 極薄的 IRunConsole adapter：將回填輸出逐行收集至 <see cref="PrtgBackfillRunState"/>。
/// </summary>
public class PrtgBackfillConsole : IRunConsole
{
    private readonly PrtgBackfillRunState _state;

    public PrtgBackfillConsole(PrtgBackfillRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// PRTG 歷史回填服務：Singleton，背景執行 PRTG 歷史回填任務並維護狀態。
/// </summary>
public class PrtgBackfillService
{
    private readonly ISystemSettingsStore _settings;
    private readonly StorageBackend _backend;
    private readonly PrtgBackfillRunState _state;
    private readonly PrtgProbeRunState _probeState;

    public PrtgBackfillService(
        ISystemSettingsStore settings,
        StorageBackend backend,
        PrtgBackfillRunState state,
        PrtgProbeRunState probeState)
    {
        _settings = settings;
        _backend = backend;
        _state = state;
        _probeState = probeState;
    }

    public PrtgBackfillStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        return new PrtgBackfillStatusDto
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

        if (!s.PrtgEnabled)
        {
            error = "PRTG 未啟用。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行回填。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法執行回填。";
            return false;
        }

        if (_probeState.Snapshot().IsRunning)
        {
            error = "環境探測執行中，請稍後再試。";
            return false;
        }

        // 回填不重跑結構同步、sensor 清單來自鏡像——鏡像空的就沒有東西可回填，
        // 放行只會空跑 N 天然後報成功（一筆資料都沒抓的成功最難察覺），在入口就擋下
        if (_backend.PrtgStore().GetSensorTargets().Count == 0)
        {
            error = "鏡像尚無任何感測器結構。請先執行一次每日擷取（或等夜間排程跑過）再回填。";
            return false;
        }

        if (!_state.TryBegin())
        {
            error = "回填已在執行中。";
            return false;
        }

        var console = new PrtgBackfillConsole(_state);
        PrtgClient? client = null;
        try
        {
            client = PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _state.EndRun(false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        var prtgStore = _backend.PrtgStore();
        var fetchService = new PrtgFetchService(client, prtgStore, console);
        var days = s.PrtgBackfillDays;
        var concurrency = s.PrtgFetchConcurrency;

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                using (client)
                {
                    success = await PrtgBackfillRunner.RunAsync(fetchService, days, concurrency, console, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                console.WriteLine($"回填過程發生未預期錯誤：{ex.Message}");
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
