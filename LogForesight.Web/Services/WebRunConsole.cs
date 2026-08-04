using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IRunConsole"/> 的 Web 端實作：落地 NLog（完整診斷仍在 log 檔）＋回報進度給
/// <see cref="SchedulerRunState"/>（供狀態 API 顯示「目前進度到哪」）。
/// </summary>
public class WebRunConsole : IRunConsole
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly SchedulerRunState _state;

    public WebRunConsole(SchedulerRunState state) => _state = state;

    public void WriteLine(string message = "")
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) return;

        Log.Info(trimmed);
        _state.ReportMessage(trimmed);
    }
}
