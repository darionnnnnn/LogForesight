using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 權限異動去重鍵的雜湊計算（唯一出口）。SHA-256 取完整 32 bytes 的小寫十六進位（64 字元）：
/// 固定長度、可放進 SQL Server 與 SQLite 的一般索引；碰撞機率可忽略，但查詢仍會再比對原鍵，不靠雜湊單獨判定相等。
/// </summary>
public static class PermissionChangeDedupeHash
{
    public static string Of(string dedupeKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dedupeKey ?? string.Empty)));
}

/// <summary>
/// 在背景補齊 <c>lf_permission_changes.dedupe_key_hash</c>（既有列升級時為 NULL）。
/// 做法比照 <see cref="TopIssueBackfiller"/> 的來源鍵回填：
/// 每批 500 列、每批一個交易，中途取消後下次接續；<see cref="Ready"/> 只升不降，
/// 就緒前 <see cref="PermissionChangeStore"/> 的四個依去重鍵查詢走原鍵比對，就緒後才走雜湊索引。
/// 啟動快判用索引可 seek 的 <c>dedupe_key_hash IS NULL</c> EXISTS，不對全表 Count；
/// 完成後寫版本化 blob，之後的啟動連 EXISTS 都不必做。
/// </summary>
public sealed class PermissionChangeDedupeBackfiller
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const string DoneBlobKey = "permission_changes_dedupe_hash_done";
    internal const string DoneVersion = "1";
    internal const int BatchSize = 500;

    private readonly Func<LfDbContext> _contextFactory;
    private int _ready;

    public PermissionChangeDedupeBackfiller(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>所有列都有雜湊欄，查詢可以走雜湊索引。只升不降。</summary>
    public bool Ready => Volatile.Read(ref _ready) == 1;

    /// <summary>
    /// 啟動快判（純讀）：完成標記存在，或沒有任何雜湊欄為 NULL 的列時直接發布就緒。
    /// 任一條件不成立什麼都不改，交由背景回填處理。
    /// </summary>
    public bool TryFastReady()
    {
        if (Ready) return true;
        if (SchemaUpgrader.HasBlobVersion(_contextFactory, DoneBlobKey, DoneVersion) || !HasPending())
        {
            Volatile.Write(ref _ready, 1);
            return true;
        }
        return false;
    }

    private bool HasPending()
    {
        using var ctx = _contextFactory();
        return ctx.PermissionChanges.AsNoTracking().Any(r => r.DedupeKeyHash == null);
    }

    /// <summary>回填一批（每批至多 <see cref="BatchSize"/> 列）；回傳本批處理列數，0＝沒有待補的了。</summary>
    internal int RunBatch()
    {
        using var ctx = _contextFactory();
        var batch = ctx.PermissionChanges
            .Where(r => r.DedupeKeyHash == null)
            .OrderBy(r => r.Id)
            .Take(BatchSize)
            .ToList();
        if (batch.Count == 0) return 0;

        foreach (var row in batch) row.DedupeKeyHash = PermissionChangeDedupeHash.Of(row.DedupeKey);
        ctx.SaveChanges();
        return batch.Count;
    }

    /// <summary>執行回填直到完成或被取消。完成才寫標記並發布就緒；被取消則下次啟動接續。</summary>
    public void Run(CancellationToken cancellationToken)
    {
        if (SchemaUpgrader.HasBlobVersion(_contextFactory, DoneBlobKey, DoneVersion))
        {
            Volatile.Write(ref _ready, 1);
            return;
        }

        var done = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var processed = RunBatch();
            if (processed == 0) break;
            done += processed;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Log.Info("[SQL] lf_permission_changes 去重鍵雜湊回填中止（站台關閉），本次已補 {Done} 列，下次啟動接續", done);
            return;
        }

        if (HasPending()) return;   // 理論上不會（每批都處理到空）；保守起見不發布就緒
        SchemaUpgrader.WriteBlobVersion(_contextFactory, DoneBlobKey, DoneVersion);
        Volatile.Write(ref _ready, 1);
        if (done > 0) Log.Info("[SQL] lf_permission_changes 去重鍵雜湊回填完成：{Done} 列", done);
    }
}
