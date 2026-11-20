using System.Text.RegularExpressions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動去重鍵雜湊欄（dedupe_key_hash）：寫入點統一補欄、背景回填、就緒閘門與查詢路徑切換。
/// 用檔案型 SQLite 經真實的 <see cref="StorageBackend"/>（就緒旗標與正式站台同一條接線）。
/// SQL Server 只驗 EF 的 <c>ToQueryString</c> 翻譯，未連線實際 SQL Server。
/// </summary>
public sealed class PermissionChangeDedupeHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-perm-hash-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SqlCommandRecorder _recorder = new();
    private readonly StorageBackend _backend;
    private static readonly DateTime Day = new(2026, 11, 10, 9, 0, 0);

    public PermissionChangeDedupeHashTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "perm.db");
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={_dbPath}" }, _dir, _recorder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static PermissionChangeRecord Record(int index, string host = "SRV-A") => new()
    {
        HostName = host,
        DetectedAt = Day.AddSeconds(index),
        Target = "Domain Admins",
        ChangeType = "成員新增",
        AlertText = $"已將成員新增到群組 #{index}",
        EventId = 4728,
        Source = PermissionChangeSources.Netiq
    };

    private static string KeyOf(int index, string host = "SRV-A") => Record(index, host).DedupeKey();

    private int Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>把雜湊欄清成 NULL，模擬升級前寫入的既有列。</summary>
    private void MakeLegacy() => Exec("UPDATE lf_permission_changes SET dedupe_key_hash = NULL");

    // ── 寫入點統一補欄 ──

    [Fact]
    public void 批次寫入與彙總列寫入_都同時寫入雜湊欄()
    {
        var store = _backend.PermissionChanges();
        store.AppendChanges(Enumerable.Range(0, 3).Select(i => Record(i)).ToList());
        store.UpsertByDedupeKey(Record(10), "固定彙總鍵");

        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM lf_permission_changes"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM lf_permission_changes WHERE dedupe_key_hash IS NULL"));

        using var ctx = NewContext();
        var upserted = ctx.PermissionChanges.Single(r => r.DedupeKey == "固定彙總鍵");
        Assert.Equal(PermissionChangeDedupeHash.Of("固定彙總鍵"), upserted.DedupeKeyHash);
        Assert.Equal(64, upserted.DedupeKeyHash!.Length);
    }

    // ── 回填前後四個方法結果相同 ──

    private sealed record ScenarioResult(
        bool ExistsPresent, bool ExistsAbsent, int DeletedMany, bool DeleteOnePresent, bool DeleteOneAbsent,
        int CountAfterUpsertExisting, int CountAfterUpsertNew, int RemainingAfterAll);

    /// <summary>四個以去重鍵為條件的方法各跑一遍，回傳可比對的結果。</summary>
    private static ScenarioResult RunScenario(PermissionChangeStore store, Func<int> count)
    {
        var existsPresent = store.ExistsByDedupeKey(KeyOf(1));
        var existsAbsent = store.ExistsByDedupeKey(KeyOf(999));

        store.UpsertByDedupeKey(Record(1), KeyOf(1));      // 已存在：更新、不新增
        var afterUpsertExisting = count();
        store.UpsertByDedupeKey(Record(500), KeyOf(500));  // 不存在：新增
        var afterUpsertNew = count();

        var deletedMany = store.DeleteByDedupeKeys(new[] { KeyOf(2), KeyOf(3), KeyOf(998), "", KeyOf(2) });
        var deleteOnePresent = store.DeleteByDedupeKey(KeyOf(4));
        var deleteOneAbsent = store.DeleteByDedupeKey(KeyOf(997));
        return new ScenarioResult(existsPresent, existsAbsent, deletedMany, deleteOnePresent, deleteOneAbsent,
            afterUpsertExisting, afterUpsertNew, count());
    }

    [Fact]
    public void 回填前後_四個依去重鍵的方法結果相同()
    {
        var store = _backend.PermissionChanges();
        var backfiller = _backend.PermissionChangeDedupeBackfiller();
        int Count() => Scalar("SELECT COUNT(*) FROM lf_permission_changes");

        // 前：既有列全是 NULL 雜湊，旗標未就緒，走原鍵比對
        store.AppendChanges(Enumerable.Range(0, 8).Select(i => Record(i)).ToList());
        MakeLegacy();
        Assert.False(backfiller.Ready);
        var before = RunScenario(store, Count);

        // 後：清掉重種同一份資料，回填完成後旗標就緒，走雜湊索引
        Exec("DELETE FROM lf_permission_changes");
        store.AppendChanges(Enumerable.Range(0, 8).Select(i => Record(i)).ToList());
        MakeLegacy();
        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.Ready);
        var after = RunScenario(store, Count);

        Assert.Equal(before, after);
        Assert.True(before.ExistsPresent);
        Assert.False(before.ExistsAbsent);
        Assert.Equal(2, before.DeletedMany);      // 重複鍵與空字串不重複計、不誤刪
        Assert.True(before.DeleteOnePresent);
        Assert.False(before.DeleteOneAbsent);
        Assert.Equal(8, before.CountAfterUpsertExisting);
        Assert.Equal(9, before.CountAfterUpsertNew);
    }

    [Fact]
    public void 雜湊相同但原鍵不同_不會被當成同一筆()
    {
        // 就緒路徑會再比對原鍵：人為把另一列的雜湊欄改成目標鍵的雜湊，模擬碰撞
        var store = _backend.PermissionChanges();
        store.AppendChanges(new[] { Record(1) });
        _backend.PermissionChangeDedupeBackfiller().Run(CancellationToken.None);
        Assert.True(_backend.PermissionChangeDedupeBackfiller().Ready);

        Exec($"UPDATE lf_permission_changes SET dedupe_key_hash = '{PermissionChangeDedupeHash.Of(KeyOf(2))}'");

        Assert.False(store.ExistsByDedupeKey(KeyOf(2)));   // 雜湊命中、原鍵不符
        Assert.False(store.DeleteByDedupeKey(KeyOf(2)));
        Assert.Equal(0, store.DeleteByDedupeKeys(new[] { KeyOf(2) }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM lf_permission_changes"));
    }

    // ── 回填：分批、可中斷、可重跑 ──

    [Fact]
    public void 回填中斷後重跑_不重複不遺漏且每批至多五百列()
    {
        var store = _backend.PermissionChanges();
        store.AppendChanges(Enumerable.Range(0, 1203).Select(i => Record(i)).ToList());
        MakeLegacy();
        var backfiller = _backend.PermissionChangeDedupeBackfiller();

        // 先取消的權杖：一批都不做，旗標與標記都不動
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            backfiller.Run(cancelled.Token);
        }
        Assert.False(backfiller.Ready);
        Assert.Equal(1203, Scalar("SELECT COUNT(*) FROM lf_permission_changes WHERE dedupe_key_hash IS NULL"));

        // 手動只跑一批 ＝ 中途被中斷：恰補 500 列
        Assert.Equal(500, backfiller.RunBatch());
        Assert.Equal(703, Scalar("SELECT COUNT(*) FROM lf_permission_changes WHERE dedupe_key_hash IS NULL"));
        Assert.False(backfiller.Ready);

        // 重跑：補齊剩下的，總列數不變，每列的雜湊都對得上
        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.Ready);
        Assert.Equal(1203, Scalar("SELECT COUNT(*) FROM lf_permission_changes"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM lf_permission_changes WHERE dedupe_key_hash IS NULL"));
        using var ctx = NewContext();
        Assert.All(ctx.PermissionChanges.AsNoTracking().ToList(),
            r => Assert.Equal(PermissionChangeDedupeHash.Of(r.DedupeKey), r.DedupeKeyHash));

        // 再跑一次：沒有可補的，不改任何列
        Assert.Equal(0, backfiller.RunBatch());
    }

    // ── 啟動快判與旗標單調 ──

    [Fact]
    public void 快判_沒有待補列時成立_有待補列時不成立()
    {
        _backend.PermissionChanges().AppendChanges(new[] { Record(1), Record(2) });

        // 邊界一側：全部有雜湊 → 成立
        var ready = new PermissionChangeDedupeBackfiller(() => NewContext());
        Assert.True(ready.TryFastReady());
        Assert.True(ready.Ready);

        // 邊界另一側：有一列 NULL → 不成立、不寫標記
        Exec("UPDATE lf_permission_changes SET dedupe_key_hash = NULL WHERE id = (SELECT MIN(id) FROM lf_permission_changes)");
        var notReady = new PermissionChangeDedupeBackfiller(() => NewContext());
        Assert.False(notReady.TryFastReady());
        Assert.False(notReady.Ready);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM lf_blobs WHERE blob_key = '{PermissionChangeDedupeBackfiller.DoneBlobKey}'"));
    }

    [Fact]
    public void 完成標記存在時快判直接成立_不必再查資料列()
    {
        _backend.PermissionChanges().AppendChanges(new[] { Record(1) });
        _backend.PermissionChangeDedupeBackfiller().Run(CancellationToken.None);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM lf_blobs WHERE blob_key = '{PermissionChangeDedupeBackfiller.DoneBlobKey}'"));

        // 新行程（新的回填器實例）：標記存在 → 快判成立，連仍有 NULL 的列也不再掃
        MakeLegacy();
        var next = new PermissionChangeDedupeBackfiller(() => NewContext());
        Assert.True(next.TryFastReady());
        Assert.True(next.Ready);
    }

    [Fact]
    public void 就緒旗標只升不降_就緒後又出現NULL列也維持就緒()
    {
        var backfiller = _backend.PermissionChangeDedupeBackfiller();
        Assert.True(backfiller.TryFastReady());   // 空表：沒有待補列
        _backend.PermissionChanges().AppendChanges(new[] { Record(1) });
        MakeLegacy();

        Assert.True(backfiller.TryFastReady());   // 再快判仍成立
        Assert.True(backfiller.Ready);
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    // ── 查詢計畫 ──

    private List<string> PlanOf(Action query)
    {
        _recorder.Clear();
        query();
        var selects = _recorder.Commands.Where(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                                                    && c.Sql.Contains("lf_permission_changes")).ToList();
        Assert.NotEmpty(selects);
        var plan = new List<string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        foreach (var (sql, parameters) in selects)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
        }
        return plan;
    }

    [Fact]
    public void 查詢計畫_就緒前全表掃描_就緒後走雜湊索引()
    {
        var store = _backend.PermissionChanges();
        store.AppendChanges(Enumerable.Range(0, 50).Select(i => Record(i)).ToList());
        var backfiller = _backend.PermissionChangeDedupeBackfiller();
        Assert.False(backfiller.Ready);

        var legacyPlan = PlanOf(() => store.ExistsByDedupeKey(KeyOf(3)));
        Assert.DoesNotContain(legacyPlan, d => d.Contains("IX_lf_permission_changes_dedupe_key_hash"));

        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.Ready);

        var existsPlan = PlanOf(() => store.ExistsByDedupeKey(KeyOf(3)));
        Assert.Contains(existsPlan, d => d.Contains("SEARCH") && d.Contains("IX_lf_permission_changes_dedupe_key_hash"));
        var upsertPlan = PlanOf(() => store.UpsertByDedupeKey(Record(3), KeyOf(3)));
        Assert.Contains(upsertPlan, d => d.Contains("SEARCH") && d.Contains("IX_lf_permission_changes_dedupe_key_hash"));
    }

    /// <summary>執行刪除並取回該段期間對 lf_permission_changes 的 DELETE 指令之查詢計畫（<c>ExecuteDelete</c> 不是讀取指令，需開啟記錄）。</summary>
    private List<string> DeletePlanOf(Action delete)
    {
        _recorder.Clear();
        _recorder.IncludeNonQueries = true;
        try { delete(); }
        finally { _recorder.IncludeNonQueries = false; }
        var deletes = _recorder.Commands.Where(c => c.Sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                                                    && c.Sql.Contains("lf_permission_changes")).ToList();
        Assert.NotEmpty(deletes);
        var plan = new List<string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        foreach (var (sql, parameters) in deletes)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
        }
        return plan;
    }

    [Fact]
    public void 查詢計畫_兩個刪除方法就緒前不走雜湊索引_就緒後走雜湊索引()
    {
        var store = _backend.PermissionChanges();
        store.AppendChanges(Enumerable.Range(0, 50).Select(i => Record(i)).ToList());
        var backfiller = _backend.PermissionChangeDedupeBackfiller();
        Assert.False(backfiller.Ready);
        const string Index = "IX_lf_permission_changes_dedupe_key_hash";

        // 就緒前：兩者都只比原鍵，計畫不含雜湊索引
        Assert.DoesNotContain(PlanOf(() => store.DeleteByDedupeKey(KeyOf(3))), d => d.Contains(Index));
        Assert.DoesNotContain(DeletePlanOf(() => store.DeleteByDedupeKeys(new[] { KeyOf(4), KeyOf(5) })), d => d.Contains(Index));

        backfiller.Run(CancellationToken.None);
        Assert.True(backfiller.Ready);

        // 就緒後：依單鍵刪除（先查再移除）與依多鍵刪除（ExecuteDelete）都走雜湊索引
        Assert.Contains(PlanOf(() => store.DeleteByDedupeKey(KeyOf(6))), d => d.Contains("SEARCH") && d.Contains(Index));
        Assert.Contains(DeletePlanOf(() => store.DeleteByDedupeKeys(new[] { KeyOf(7), KeyOf(8) })),
            d => d.Contains("SEARCH") && d.Contains(Index));
    }

    // ── SQL Server 翻譯（不連線） ──

    /// <summary>只取 WHERE 之後（SELECT 清單本來就含所有欄位）</summary>
    private static string WhereOf(string sql) => sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

    [Fact]
    public void SqlServer翻譯_就緒走雜湊欄加原鍵_未就緒只比原鍵()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True").Options);

        var ready = PermissionChangeStore.ByDedupeKey(ctx.PermissionChanges, "k", useHash: true).ToQueryString();
        Assert.Matches(new Regex(@"\[dedupe_key_hash\] = @", RegexOptions.IgnoreCase), ready);
        Assert.Contains("[dedupe_key] = @", ready);

        var legacy = PermissionChangeStore.ByDedupeKey(ctx.PermissionChanges, "k", useHash: false).ToQueryString();
        Assert.DoesNotContain("dedupe_key_hash", WhereOf(legacy));
        Assert.Contains("[dedupe_key] = @", legacy);

        var readyMany = PermissionChangeStore.ByDedupeKeys(ctx.PermissionChanges, new List<string> { "a", "b" }, useHash: true).ToQueryString();
        Assert.Contains("[dedupe_key_hash] IN (", readyMany);
        Assert.Contains("[dedupe_key] IN (", readyMany);
        var legacyMany = PermissionChangeStore.ByDedupeKeys(ctx.PermissionChanges, new List<string> { "a", "b" }, useHash: false).ToQueryString();
        Assert.DoesNotContain("dedupe_key_hash", WhereOf(legacyMany));
    }

    [Fact]
    public void 雜湊函式_相同輸入相同輸出_固定六十四個小寫十六進位字元()
    {
        var a = PermissionChangeDedupeHash.Of("SRV-A|1|4728|x");
        Assert.Equal(a, PermissionChangeDedupeHash.Of("SRV-A|1|4728|x"));
        Assert.NotEqual(a, PermissionChangeDedupeHash.Of("SRV-A|1|4728|y"));
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Matches("^[0-9a-f]{64}$", PermissionChangeDedupeHash.Of(new string('長', 2000)));   // 長鍵一樣固定長度
    }
}
