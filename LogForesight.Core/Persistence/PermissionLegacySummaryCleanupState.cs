namespace LogForesight.Core.Persistence;

/// <summary>
/// 舊版「權限異動（彙總）」列一次性清理的執行狀態（blob key＝<c>permission_legacy_summary_cleanup</c>）。
///
/// 為什麼可以是單向閘門：產生這種列的邏輯已經不存在（現行彙總只產「例行同步（彙總）」），
/// 跑完之後不會再有新的同形列出現。唯一會需要繞過它的情境是「還原了一份更舊的資料庫」——
/// 那種情況下把這個 blob 刪掉即可讓它重跑一次。
/// </summary>
public class PermissionLegacySummaryCleanupState
{
    public bool Completed { get; set; }

    /// <summary>實際刪除的列數（供 log 與事後查核）</summary>
    public int DeletedRows { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>舊彙總列清理狀態的持久化</summary>
public class PermissionLegacySummaryCleanupStateStore : JsonBlobSingleton<PermissionLegacySummaryCleanupState>
{
    public PermissionLegacySummaryCleanupStateStore(Sql.EfJsonBlobStore blob) : base(blob) { }
}
