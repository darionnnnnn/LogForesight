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
    /// 回 true＝同步已結束、鏡像是新的；回 false＝等到上限仍未結束（對方卡住），
    /// 呼叫端**不得**把鏡像當成剛更新過的。
    /// </summary>
    Task<bool> WaitUntilIdleAsync(CancellationToken ct);
}
