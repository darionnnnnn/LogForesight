using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LogForesight;

/// <summary>
/// Sqlite 連線層調校（回饋三十六輪批次B）。只掛在 Sqlite 後端，SQL Server 不涉入。
///
/// 為什麼需要它：本後端為了避開 Microsoft.Data.Sqlite 連線池歸還時
/// 「active statements 下移除 user function」的例外而停用連線池（見
/// <c>StorageBackend.DisableSqlitePoolingIfUnset</c>），代價是每個 DbContext 都是
/// 新的實體連線、page cache 每次歸零——大型 DB（數 GB）上等於每支查詢都從冷快取掃起。
/// PRAGMA 是逐連線狀態，關池後只能在每次連線開啟時重設，所以用 EF 攔截器收斂在這裡。
///
/// - cache_size：負值單位為 KB。預設僅 2MB，對 GB 級檔案的範圍掃描杯水車薪。
/// - mmap_size：記憶體對映讀取走 OS 檔案快取——**跨連線共享**，是關池後仍能
///   「暖快取」的主要機制（cache_size 只活在單一連線內）。
/// - temp_store=MEMORY：GROUP BY／DISTINCT 的 temp b-tree 不落地暫存檔。
///
/// 不動 journal_mode：WAL 會改變部署檔案佈局（-wal/-shm），另案評估。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>單一連線的 page cache 上限：64MB（負值＝KB）。按需成長，不是預先配置。</summary>
    private const string Pragmas =
        "PRAGMA cache_size=-65536; PRAGMA mmap_size=1073741824; PRAGMA temp_store=MEMORY;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }
}
