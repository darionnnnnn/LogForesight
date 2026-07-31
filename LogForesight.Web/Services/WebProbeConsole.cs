namespace LogForesight.Web.Services;

/// <summary><see cref="IRunConsole"/> 的 Web probe adapter：不落地 NLog（probe 是短期診斷輸出，
/// 不是分析紀錄），單純把每一行累積進 <see cref="NetiqProbeRunState"/> 供輪詢讀取</summary>
public class WebProbeConsole : IRunConsole
{
    private readonly NetiqProbeRunState _state;

    public WebProbeConsole(NetiqProbeRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);

    public void WithColor(ConsoleColor color, Action write) => write();
}
