using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IRunConsole"/> 的 Web 端實作（docs/WEB-SCHEDULER-PLAN.md §1.4.2）：console 端adapter
/// 逐字重現彩色輸出，這裡則落地 NLog（完整診斷仍在 log 檔）＋回報進度給
/// <see cref="SchedulerRunState"/>（供狀態 API 顯示「目前進度到哪」）。色彩本身對 Web 沒有意義，
/// 直接忽略——訊息文字內容不變，只是不重現終端機顏色。
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

    public void WithColor(ConsoleColor color, Action write) => write();
}
