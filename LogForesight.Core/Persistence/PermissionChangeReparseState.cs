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

    /// <summary>
    /// 已完成的重剖版本。解析器改版（新增擷取欄位、修掉舊的髒值形狀）之後，
    /// 舊部署的狀態是「Completed=true、Version 落後」——沒有這個欄位就永遠不會再跑一次，
    /// 既有列補不到新欄位。升級時把 <see cref="PermissionChangeReparser.CurrentVersion"/>
    /// 加一即可讓它重跑一輪；既有列的其他欄位不會因此被洗掉（只覆蓋剖得出值的欄位）。
    /// </summary>
    public int Version { get; set; }

    /// <summary>實際被更新的列數（供 log 與事後查核）</summary>
    public int UpdatedRows { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>重剖回填狀態的持久化</summary>
public class PermissionChangeReparseStateStore : JsonBlobSingleton<PermissionChangeReparseState>
{
    public PermissionChangeReparseStateStore(EfJsonBlobStore blob) : base(blob) { }
}
