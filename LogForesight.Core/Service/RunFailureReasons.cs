namespace LogForesight.Core.Service;

/// <summary>執行失敗原因的固定說明：讓「記憶體吃滿」與一般例外在執行紀錄上能一眼分辨。</summary>
public static class RunFailureReasons
{
    /// <summary>擲出 <see cref="OutOfMemoryException"/> 時寫進失敗原因的固定句</summary>
    public const string MemoryLimit = "記憶體上限：.NET 記憶體不足（多半是 .NET 堆已達硬上限 System.GC.HeapHardLimitPercent），本趟中止";

    /// <summary>例外本身或其內層（含 <see cref="AggregateException"/> 的任一內層）是否為記憶體不足</summary>
    public static bool IsOutOfMemory(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < 16; depth++, ex = ex.InnerException)
        {
            if (ex is OutOfMemoryException) return true;
            if (ex is AggregateException agg && agg.InnerExceptions.Any(IsOutOfMemory)) return true;
        }
        return false;
    }

    /// <summary>失敗原因：記憶體不足回 <see cref="MemoryLimit"/>，其餘回例外訊息</summary>
    public static string Describe(Exception ex) => IsOutOfMemory(ex) ? MemoryLimit : ex.Message;
}
