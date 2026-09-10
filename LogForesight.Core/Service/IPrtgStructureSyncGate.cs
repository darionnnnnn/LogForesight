namespace LogForesight.Core.Service;

/// <summary>
/// 手動觸發的「同步結構與對應」對夜間／手動取數執行的閘門（docs/PRTG-SPEC.md §5a）。
///
/// Core 不認識 Web 的服務，因此以介面表達這層依賴，由 Web 端實作並注入。
/// 反方向的互斥（取數執行中不准啟動同步）在 Web 服務的 TryStart 就擋掉了，不走這裡。
/// </summary>
public interface IPrtgStructureSyncGate
{
    /// <summary>手動同步是否正在執行。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 等到手動同步結束為止；已經結束時立即返回。
    ///
    /// 回 true 的條件是「等待的那趟同步跑完**而且成功**」——只有這時鏡像才是完整且新的。
    /// 被取消、有階段失敗、或等到上限仍未結束都回 false，呼叫端**不得**把鏡像當成剛更新過的，
    /// 必須自己再同步一次。半套的鏡像看起來與完整的鏡像一模一樣，分不出來就會整晚用錯資料。
    /// </summary>
    Task<bool> WaitUntilIdleAsync(CancellationToken ct);
}
