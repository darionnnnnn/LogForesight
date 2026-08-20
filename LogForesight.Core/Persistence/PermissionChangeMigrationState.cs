using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 權限異動自 JSONL log（key=perm_changes）與確認狀態 blob（key=perm_confirms）搬進真表的遷移狀態。
///
/// 與 <see cref="HandlingMigrationState"/> 相同原則：
/// 1. 完成與否是被寫下的事實，不再從表空不空反推；
/// 2. 完成後舊 log 與 blob 即為唯讀備份；
/// 3. 未完成時 Web 端擋下寫入（見 MigrationGateMiddleware）。
/// </summary>
public class PermissionChangeMigrationState
{
    /// <summary>尚未評估過（全新安裝或升級後第一次啟動）</summary>
    public const string Unknown = "unknown";

    /// <summary>需要搬移，但還沒開始</summary>
    public const string Pending = "pending";

    /// <summary>搬移進行中——此時權限異動的寫入應被擋下</summary>
    public const string Running = "running";

    /// <summary>已搬完（或本來就沒有舊資料）</summary>
    public const string Completed = "completed";

    public string State { get; set; } = Unknown;

    /// <summary>實際搬入的列數（供 log 與畫面顯示）</summary>
    public int MigratedRows { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>最後一次失敗的訊息（成功後清空）</summary>
    public string? LastError { get; set; }

    /// <summary>未完成前擋下寫入：只要還沒到 <see cref="Completed"/> 就擋</summary>
    public bool ShouldBlockWrites => State != Completed;
}

/// <summary>遷移狀態的持久化（blob key＝<c>permission_change_migration</c>）</summary>
public class PermissionChangeMigrationStateStore : JsonBlobSingleton<PermissionChangeMigrationState>
{
    public PermissionChangeMigrationStateStore(EfJsonBlobStore blob) : base(blob) { }
}
