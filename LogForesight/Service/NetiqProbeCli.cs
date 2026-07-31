namespace LogForesight;

/// <summary>
/// <c>--netiq-probe</c> 的 console 薄殼：篩出已啟用的 Sentinel，交給 Core 的
/// <see cref="NetiqProbeRunner"/>（Web「NetIQ 維護」頁「診斷」分頁共用同一份查詢邏輯，
/// docs/WEB-SCHEDULER-PLAN.md §1.4.11）。查詢邏輯、輸出格式一律不在這裡維護。
/// </summary>
public static class NetiqProbeCli
{
    /// <param name="sampleIp">一台已知的 Windows 主機 IP，用於核對跨主機事件的主機歸屬鍵、
    /// 頻道覆蓋、dt 邊界。省略時對應步驟標示略過。</param>
    /// <param name="sampleLinuxIp">一台已知的 Linux 主機 IP，用於核對 Linux 事件的欄位形狀。
    /// 省略時對應步驟標示略過。</param>
    public static async Task<int> RunAsync(
        ISentinelStore sentinelStore, NetiqOptions settings, string? sampleIp = null, string? sampleLinuxIp = null)
    {
        var sentinels = sentinelStore.GetAll().Where(s => s.Active).ToList();
        var ok = await NetiqProbeRunner.RunAsync(sentinels, settings, sampleIp, sampleLinuxIp, new ConsoleRunConsole());
        return ok ? 0 : 1;
    }
}
