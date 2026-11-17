namespace LogForesight.Core.Time;

/// <summary>
/// 交辦、靜音、觀察中這條路徑的「現在」「今天」唯一出口。正式站台註冊 <see cref="TimeProvider.System"/>，行為與直接讀
/// <c>DateTime.Now</c>／<c>DateTime.Today</c>／<c>DateTime.UtcNow</c> 完全相同；測試與情境驅動器可換成可手動撥動的時鐘。
/// 範圍內的類別一律呼叫這裡，不各自換算（<c>HandoffClockGuardTests</c> 守住範圍內不得直接讀真實時鐘）。
/// </summary>
public static class ClockExtensions
{
    /// <summary>本地現在；Kind 為 Local，語意同 <see cref="DateTime.Now"/></summary>
    public static DateTime LocalNow(this TimeProvider clock) =>
        DateTime.SpecifyKind(clock.GetLocalNow().DateTime, DateTimeKind.Local);

    /// <summary>本地今天（00:00，Kind 為 Local），語意同 <see cref="DateTime.Today"/></summary>
    public static DateTime LocalToday(this TimeProvider clock) => clock.LocalNow().Date;

    /// <summary>UTC 現在；Kind 為 Utc，語意同 <see cref="DateTime.UtcNow"/></summary>
    public static DateTime UtcNow(this TimeProvider clock) => clock.GetUtcNow().UtcDateTime;
}
