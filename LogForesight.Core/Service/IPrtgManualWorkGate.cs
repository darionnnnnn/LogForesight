namespace LogForesight.Core.Service;

/// <summary>正在進行的手動 PRTG 作業（同一時間至多一種：同步與回填在啟動時互斥）。</summary>
public enum PrtgManualWork
{
    None,
    StructureSync,
    Backfill
}

/// <summary>等待手動 PRTG 作業的結果，決定這一趟要不要自己同步結構。</summary>
public enum PrtgManualWaitOutcome
{
    /// <summary>等到的同步**成功**結束：鏡像是新的，本趟跳過結構同步。</summary>
    MirrorFresh,

    /// <summary>同步結束但不成功（被中止、有階段失敗）或等到上限仍未結束：鏡像可能是半套，本趟自己同步。</summary>
    SyncNotFresh,

    /// <summary>回填已結束：本趟照常取數。</summary>
    BackfillEnded,

    /// <summary>回填等到上限仍在執行：本趟照常取數，與它同時查詢 PRTG（寫入皆冪等）。</summary>
    BackfillStillRunning
}

/// <summary>
/// 手動 PRTG 作業（「同步結構與對應」與歷史回填）對夜間／手動取數的閘門（docs/PRTG-SPEC.md §5a）。
///
/// Core 不認識 Web 的服務，因此以介面表達這層依賴，由 Web 端實作並注入。
/// 反方向的互斥（取數執行中不准啟動同步或回填）在 Web 服務啟動時就擋掉了，不走這裡。
/// 立即執行的接續回填是同一趟的一部分，不算在內。
/// </summary>
public interface IPrtgManualWorkGate
{
    /// <summary>
    /// 回填最多等這麼久：再等下去，`prtg-findings-ready` 跟著延後，AI 判讀昨天的待補也一起被擋住。
    /// 逾時就與回填同時進行，由正在看畫面的管理者決定要不要停掉回填。
    /// </summary>
    static readonly TimeSpan BackfillMaxWait = TimeSpan.FromMinutes(30);

    /// <summary>目前正在進行的手動作業。</summary>
    PrtgManualWork Running { get; }

    /// <summary>
    /// 等目前的手動作業結束（各自有等待上限，逾時不擲例外）。取消訊號穿透。
    /// 只有等到的是**成功的同步**才回 <see cref="PrtgManualWaitOutcome.MirrorFresh"/>——
    /// 半套的鏡像看起來與完整的一模一樣，分不出來就會整晚用錯資料。
    /// </summary>
    Task<PrtgManualWaitOutcome> WaitUntilIdleAsync(CancellationToken ct);
}
