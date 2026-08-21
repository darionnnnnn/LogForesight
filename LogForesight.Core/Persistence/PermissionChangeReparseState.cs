using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 既有 NetIQ 權限異動列「重剖回填」的執行狀態（blob key＝<c>permission_change_reparse</c>）。
/// 一次性工作：跑完標記完成就不再掃描——之後寫入的列走的是修好的解析器。
/// 與遷移狀態不同，這裡**不擋寫入**：欄位沒補回來只是顯示退化，不影響新資料正確性。
/// </summary>
public class PermissionChangeReparseState
{
    public bool Completed { get; set; }

    /// <summary>實際被更新的列數（供 log 與事後查核）</summary>
    public int UpdatedRows { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>重剖回填狀態的持久化</summary>
public class PermissionChangeReparseStateStore : JsonBlobSingleton<PermissionChangeReparseState>
{
    public PermissionChangeReparseStateStore(EfJsonBlobStore blob) : base(blob) { }
}
