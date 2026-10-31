using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 改變 PRTG 監看範圍（設定、人工對應、IP 排除）前的共用檢查：範圍外清除作業進行中時一律拒絕。
/// 設定與主機對應分屬不同控制器，檢查只留這一份。
/// </summary>
internal static class PrtgScopeMutationGuard
{
    public static void EnsureNoActiveScopePurge(StorageBackend backend)
    {
        if (backend.PrtgStore().ScopePurgeJobs().HasActiveOwner())
            throw DomainException.Conflict("監看範圍外清除作業進行中，請等待它完成或停止後再改變 PRTG 範圍設定。");
    }
}
