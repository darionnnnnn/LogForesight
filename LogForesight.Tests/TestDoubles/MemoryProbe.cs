using System.Runtime.CompilerServices;

namespace LogForesight.Tests;

/// <summary>
/// 記憶體類測試的兩個小量測（第 77 輪批次 0）：都回答與機器快慢無關的問題。
/// <list type="bullet">
/// <item><see cref="AllocatedBy"/>（T4）：一次操作在目前執行緒配置了多少位元組——拿兩個主機數相比，看暫時配置是否隨主機數膨脹。</item>
/// <item><see cref="Track"/>／<see cref="IsCollected"/>（T6）：操作結束後某個物件是否已無人持有、可被回收——抓「趟結束後仍被長駐物件抓住」。</item>
/// </list>
/// 不以堆大小判斷洩漏（會抖動）；洩漏一律以長駐集合的筆數斷言（T5）。
/// </summary>
internal static class MemoryProbe
{
    /// <summary>
    /// <paramref name="action"/> 在目前執行緒配置的位元組數。只算同步部分：
    /// 被測操作若跳到其他執行緒（await 之後），配置不會被計入，呼叫端要以同步方式執行。
    /// </summary>
    public static long AllocatedBy(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// 在獨立的堆疊框內建立物件並只回傳弱參照：呼叫端的區域變數不會延長它的生命，
    /// 之後 <see cref="IsCollected"/> 回 false 就代表還有別人（被測程式）抓著它。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference Track(Func<object> create) => new(create());

    /// <summary>完整回收數次後，弱參照的目標是否已被回收。</summary>
    public static bool IsCollected(WeakReference reference)
    {
        for (var i = 0; i < 3 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return !reference.IsAlive;
    }
}
