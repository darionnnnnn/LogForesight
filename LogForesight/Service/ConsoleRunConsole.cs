namespace LogForesight;

/// <summary>
/// <see cref="IRunConsole"/> 的 console 端實作（docs/WEB-SCHEDULER-PLAN.md §1.4.2）：
/// 逐字轉呼叫 <see cref="Console"/>，維持既有彩色輸出「一字未改」。
/// </summary>
public class ConsoleRunConsole : IRunConsole
{
    public void WriteLine(string message = "") => Console.WriteLine(message);

    public void WithColor(ConsoleColor color, Action write)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        try { write(); }
        finally { Console.ForegroundColor = original; }
    }
}
