namespace LogForesight.Core.Persistence;

/// <summary>
/// 樂觀鎖衝突（docs/SCALE-FIX-PLAN-2026-08-06.md D3）：讀取之後、寫入之前，
/// 同一列被別人改過。
///
/// 這**不是**故障，是多人同時操作的正常結果——呼叫端應該讓使用者重新整理後再試，
/// 而不是回一個沒有上下文的伺服器錯誤。三張處理狀態表以 <c>UpdatedAt</c> 當並發權杖
/// 偵測衝突（<c>DbUpdateConcurrencyException</c>），但那個型別對 Web 層只是
/// 「某種資料庫錯誤」；這裡轉成語意明確的例外並帶上**是哪一筆**，
/// <c>ApiExceptionFilter</c> 才能把它對應成 409 而不是 500。
///
/// 放 Core 而非 Web：拋出點在 store，Core 不能反向參照 Web 的 DomainException
/// （§4.2 分層責任邊界）。
/// </summary>
public sealed class ConcurrentUpdateException : Exception
{
    /// <param name="what">被搶先修改的對象描述（例：「SRV-01 2026-08-05 的問題處理狀態」），
    /// 會直接組進給使用者看的訊息——使用者要知道的是**哪一件**被改了，不是技術細節</param>
    public ConcurrentUpdateException(string what, Exception inner)
        : base($"{what}剛剛已被其他人修改，您看到的可能不是最新狀態。請重新整理後再操作一次。", inner)
    {
    }
}
