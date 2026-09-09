using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 「同步結構與對應」最近一次結果的儲存（blob key <see cref="BlobKey"/>，docs/PRTG-SPEC.md §5a）。
/// </summary>
public class PrtgStructureSyncStatusStore : JsonBlobSingleton<PrtgStructureSyncStatus>
{
    public const string BlobKey = "prtg_sync_status";

    public PrtgStructureSyncStatusStore(EfJsonBlobStore blob) : base(blob) { }

    /// <summary>
    /// 從未執行過時回 null。
    /// 基底的 <see cref="JsonBlobSingleton{T}.Get"/> 在內容不存在時回的是「欄位全為預設值」的物件，
    /// 那與「執行過但一筆都沒同步到」長得一模一樣——畫面要分辨「尚未同步」與「同步到 0 筆」，
    /// 靠的是 <see cref="PrtgStructureSyncStatus.CompletedAt"/> 有沒有被寫過。
    /// </summary>
    public PrtgStructureSyncStatus? GetOrNull()
    {
        var value = Get();
        return value.CompletedAt == default ? null : value;
    }
}
