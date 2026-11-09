namespace LogForesight.Core.Service;

/// <summary>執行失敗原因的固定說明：讓「記憶體吃滿」與一般例外在執行紀錄上能一眼分辨。</summary>
public static class RunFailureReasons
{
    /// <summary>.NET 堆撞到硬上限擲出 <see cref="OutOfMemoryException"/> 時寫進失敗原因的固定句</summary>
    public const string MemoryLimit = "記憶體上限：.NET 堆已達硬上限（System.GC.HeapHardLimitPercent），本趟中止";

    /// <summary>失敗原因：OOM 回 <see cref="MemoryLimit"/>，其餘回例外訊息</summary>
    public static string Describe(Exception ex) => ex is OutOfMemoryException ? MemoryLimit : ex.Message;
}
