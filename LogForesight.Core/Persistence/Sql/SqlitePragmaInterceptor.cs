using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LogForesight.Core.Configuration;

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
/// - journal_mode：預設 WAL，讓讀者與寫者不互擋（多支背景服務與前景請求同時寫同一個檔）。
///   代價是部署目錄多出 -wal、-shm 兩檔，備份時須一併帶走；可由 Storage:SqliteWal=false 切回 DELETE。
/// - busy_timeout：寫鎖被占用時等 5 秒再放棄，而不是立刻擲 SQLITE_BUSY。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>單一連線的 page cache 上限預設 64MB（KB）。按需成長，不是預先配置（Storage:SqliteCacheSizeKb）。</summary>
    public const long DefaultCacheSizeKb = 65536;

    /// <summary>記憶體對映讀取上限預設 1GB（Storage:SqliteMmapSizeBytes）。</summary>
    public const long DefaultMmapSizeBytes = 1073741824;

    private readonly string Pragmas;

    /// <param name="wal">true＝journal_mode=WAL；false＝journal_mode=DELETE（Storage:SqliteWal）</param>
    /// <param name="cacheSizeKb">每條連線 page cache 上限（KB，&gt; 0）</param>
    /// <param name="mmapSizeBytes">記憶體對映上限（位元組，≥ 0）</param>
    public SqlitePragmaInterceptor(bool wal, long cacheSizeKb = DefaultCacheSizeKb, long mmapSizeBytes = DefaultMmapSizeBytes)
    {
        Pragmas = $"PRAGMA cache_size=-{cacheSizeKb}; PRAGMA mmap_size={mmapSizeBytes}; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;"
                  + (wal ? " PRAGMA journal_mode=WAL;" : " PRAGMA journal_mode=DELETE;");
    }

    /// <summary>
    /// 驗證三個 Sqlite 記憶體設定並回傳生效值；不合法時擲 <see cref="InvalidOperationException"/> 並點名設定鍵。
    /// </summary>
    public static (long CacheSizeKb, long MmapSizeBytes, long SoftHeapLimitBytes) ResolveMemorySettings(StorageSettings settings)
    {
        var cache = settings.SqliteCacheSizeKb ?? DefaultCacheSizeKb;
        if (cache <= 0)
            throw new InvalidOperationException($"Storage:SqliteCacheSizeKb 必須大於 0（目前 {cache}）；留空使用預設 {DefaultCacheSizeKb}。");
        var mmap = settings.SqliteMmapSizeBytes ?? DefaultMmapSizeBytes;
        if (mmap < 0)
            throw new InvalidOperationException($"Storage:SqliteMmapSizeBytes 不可為負（目前 {mmap}）；0＝不使用記憶體對映，留空使用預設 {DefaultMmapSizeBytes}。");
        var soft = settings.SqliteSoftHeapLimitBytes ?? 0;
        if (soft < 0)
            throw new InvalidOperationException($"Storage:SqliteSoftHeapLimitBytes 不可為負（目前 {soft}）；留空或 0＝不設上限。");
        return (cache, mmap, soft);
    }

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

    private void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }
}
