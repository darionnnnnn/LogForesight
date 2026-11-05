using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// 站台主機在主機清單被停用時，不再分析本機日誌。判定只有這一份：預覽、範圍檢查與健康提示都讀它，
/// 趟內則直接看 <c>HostStore.Touch</c> 回傳的同一筆主機。未登記的站台主機不算停用（首次執行會自動登記）。
/// </summary>
public static class LocalHostPolicy
{
    public static bool IsDisabled(IEnumerable<WebHost> hosts, string machineName) =>
        hosts.FirstOrDefault(h => string.Equals(h.HostName, machineName, StringComparison.OrdinalIgnoreCase)) is { Active: false };
}
