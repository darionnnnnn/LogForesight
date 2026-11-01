namespace LogForesight.Core.Persistence;

/// <summary>
/// PRTG 範圍外清除作業佔用了「範圍寫入槽」：另一個清除作業正在執行，或監看範圍正在清除中，
/// 此刻不能接受第二個清除作業、也不能變更來源／對應／主機範圍。
///
/// 這**不是**故障，是多人或背景作業同時操作的正常結果，Web 層對應 HTTP 409（<c>ApiExceptionFilter</c>）。
/// 繼承 <see cref="InvalidOperationException"/> 是刻意的：既有 <c>catch (InvalidOperationException)</c>
/// 的背景服務與控制器行為不變；專屬型別只是讓狀態碼不必再靠訊息字串比對分流。
/// </summary>
public sealed class PrtgScopePurgeBusyException : InvalidOperationException
{
    public PrtgScopePurgeBusyException(string message) : base(message)
    {
    }
}
