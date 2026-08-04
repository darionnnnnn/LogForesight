namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IRunProgress"/> 的 Web 端實作（docs/FEEDBACK-8-PLAN.md #2）：單純把回報轉存進
/// <see cref="SchedulerRunState"/>，供狀態 API 顯示「目前進度到哪」。與 <see cref="WebRunConsole"/>
/// 同一個角色分工，只是這裡收的是量化進度而非文字訊息。
/// </summary>
public class WebRunProgress : IRunProgress
{
    private readonly SchedulerRunState _state;

    public WebRunProgress(SchedulerRunState state) => _state = state;

    public void Report(string phase, int done, int total) => _state.ReportProgress(phase, done, total);
}
