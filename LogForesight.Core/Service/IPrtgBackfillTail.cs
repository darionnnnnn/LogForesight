namespace LogForesight.Core.Service;

/// <summary>
/// 立即執行勾選「一併補齊 PRTG 逐小時數值」時，同一趟結束前接續做的回填。
///
/// Core 不認識 Web 的回填服務，因此以介面表達，由 Web 端實作並注入（比照 <see cref="IPrtgStructureSyncGate"/>）。
/// 接續是同一趟執行的一部分：實作端**不得**以「取數執行中」擋下它（那個旗標就是這一趟自己）。
/// </summary>
public interface IPrtgBackfillTail
{
    /// <summary>
    /// 回填 <paramref name="days"/> 天並等到結束。被擋下或未完成回 false，原因寫在 <paramref name="console"/>。
    /// </summary>
    /// <param name="hostIds">非 null＝只回填這些主機的監看裝置（指定主機的立即執行）。</param>
    Task<bool> RunTailAsync(int days, IReadOnlyCollection<long>? hostIds, IRunConsole console, CancellationToken ct);
}
