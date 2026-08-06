using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence.Sql;
using NLog;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 依設定選擇儲存後端的執行期物件（Sqlite 預設／SqlServer 正式）。**每個行程（Web 站台、
/// 排程執行）建立一個實例並在整個生命週期內共用**——建構時依 provider 建連線、
/// EnsureCreated 建表（idempotent）並跑 <see cref="SchemaUpgrader"/>，之後 <see cref="Blob"/>／
/// <see cref="LogStore"/>／<see cref="RecordStore"/> 都重用同一個 DbContext 工廠，
/// 不需要每次重新確認 schema。
///
/// **全部資料走 SQL，無檔案**：分析紀錄以正規化列＋JSON 存（lf_daily_records/lf_top_issues），
/// webdata 各 store 透過 <see cref="EfJsonBlobStore"/>（整份型 → lf_blobs）與
/// <see cref="EfJsonLogStore"/>（append-only → lf_log_lines）走資料庫，store 業務邏輯不受
/// 後端影響。LINQ 保持 provider 中立，SQLite 上跑合約測試驗證語意。
/// </summary>
public class StorageBackend
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _dbFactory;
    private readonly string _dbDesc;

    /// <param name="settings">儲存後端設定（Type／ConnectionString）</param>
    /// <param name="fallbackDir">Sqlite 模式下用來決定預設 db 檔位置的退路（ConnectionString 未設時）</param>
    public StorageBackend(StorageSettings settings, string fallbackDir)
    {
        DbContextOptions<LfDbContext> options;
        if (settings.Type == "Sqlite")
        {
            var cs = string.IsNullOrWhiteSpace(settings.ConnectionString)
                ? $"Data Source={Path.Combine(fallbackDir, "logforesight.db")}"
                : settings.ConnectionString;
            cs = DisableSqlitePoolingIfUnset(cs);
            options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(cs).Options;
            _dbDesc = $"Sqlite（{cs}）";
        }
        else
        {
            // 暫時性錯誤（failover／節流等）自動重試；Sqlite 是本機檔案，沒有這類網路層
            // 暫時性錯誤，不需要
            options = new DbContextOptionsBuilder<LfDbContext>()
                .UseSqlServer(settings.ConnectionString, o => o.EnableRetryOnFailure(maxRetryCount: 5))
                .Options;
            _dbDesc = $"SqlServer（{MaskConnectionString(settings.ConnectionString)}）";
        }

        _dbFactory = () => new LfDbContext(options);

        Log.Info("[SQL] 啟用 {Desc} 後端（全資料走 SQL，無檔案）。正在確保 schema…", _dbDesc);
        try
        {
            // 跨行程互斥（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E3）：EnsureCreated 與 SchemaUpgrader
            // 都是「檢查缺什麼→缺才補」，這個序列**不是原子的**——Web 與批次（或多個 Web 實例）
            // 同時啟動時可能同時判定「缺」而重複執行 DDL，後者會撞「已存在」錯誤。
            //
            // 用**獨立的** mutex 名稱（不是排程那把 Global\LogForesight）：schema 確認不該排在
            // 一場正在跑的夜間分析後面等，那會讓站台啟動被分析時間綁架。
            var gate = new NamedMutexGate(SchemaMutexName);
            var exclusive = gate.RunExclusive(() =>
            {
                using var ctx = _dbFactory();
                ctx.Database.EnsureCreated();
                // EnsureCreated 只在資料庫不存在時建表，對既有 DB 什麼都不做——
                // 這裡補上既有 DB 缺的欄位/索引（自製冪等 DDL）
                SchemaUpgrader.Upgrade(ctx);
            }, SchemaMutexTimeout);

            if (!exclusive)
            {
                // 沒拿到鎖仍然執行了（schema 不能跳過，見 NamedMutexGate.RunExclusive 的說明）；
                // 記下來讓「兩個行程同時建表」的殘餘風險在事後查得到
                Log.Warn("[SQL] schema 確認未取得跨行程互斥（{Timeout} 秒內），已逕行執行——" +
                         "若同時有另一個行程正在啟動，請確認兩邊的 schema 升級 log 沒有異常",
                    SchemaMutexTimeout.TotalSeconds);
            }

            Log.Info("[SQL] schema 確認完成");
        }
        catch (Exception ex)
        {
            // 連線/建表失敗要顯性——這是 SQL 模式跑不起來的第一線索
            Log.Error(ex, "[SQL] 連線或建立 schema 失敗：{Msg}。請確認 Storage.ConnectionString 與資料庫可用性。", ex.Message);
            throw;
        }
    }

    /// <summary>schema 建立/升級的跨行程互斥名稱——刻意與排程的 <c>Global\LogForesight</c> 分開</summary>
    private const string SchemaMutexName = @"Global\LogForesight-Schema";

    /// <summary>
    /// schema 互斥的等待上限。取不到不會跳過（見 <see cref="NamedMutexGate.RunExclusive"/>），
    /// 所以這個值只決定「願意為另一個行程的 schema 升級等多久」，設太長會拖慢
    /// Windows 服務啟動（預設 30 秒逾時），設太短則失去互斥的意義。
    /// </summary>
    private static readonly TimeSpan SchemaMutexTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 資料層慢操作統計（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。每行程一份，
    /// 由本類別產生的所有 store 共用同一個實例——分散成多份就答不出「整站有幾次慢操作」。
    /// </summary>
    public SqlPerformanceMonitor Performance { get; } = new();

    /// <summary>store 的底層 blob（整份 JSON 存 lf_blobs 一列，key 為鍵）</summary>
    public EfJsonBlobStore Blob(string key) => new(_dbFactory, key, Performance);

    /// <summary>store 的底層 append-only 逐行資料（lf_log_lines，key 為鍵）</summary>
    public EfJsonLogStore LogStore(string key) => new(_dbFactory, key);

    /// <summary>EF 分析紀錄 store。ownerHost 由批次傳入（缺日判定與趨勢基準只看這台主機自己的
    /// 紀錄），Web 查詢端不傳（維持不分主機）</summary>
    public EfAnalysisRecordStore RecordStore(HostKey? ownerHost = null) =>
        new(_dbFactory, _dbDesc, ownerHost, Performance);

    /// <summary>風險 log 暫存 store</summary>
    public EfRiskyEventStore RiskyEventStore() => new(_dbFactory);

    /// <summary>
    /// 關閉 Sqlite 連線池：Microsoft.Data.Sqlite 預設開啟連線池，連線 Close() 回池前的
    /// Deactivate() 要把 EF Core 註冊在該實體連線上的 user function 移除；併發下若同一顆
    /// 實體連線還有其他 statement 未 finalize，會撞 SQLiteException「unable to delete/modify
    /// user-function due to active statements」（排程背景讀寫＋Web 前景查詢同時發生時最容易
    /// 觸發）。關閉池化後 Close() 直接關實體連線，沒有「清乾淨還池」這一步，此錯誤的根因隨之
    /// 消失。使用者若已在 ConnectionString 明寫 Pooling，尊重其設定、不覆寫。
    /// </summary>
    internal static string DisableSqlitePoolingIfUnset(string connectionString)
    {
        var alreadySet = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.TrimStart().StartsWith("Pooling", StringComparison.OrdinalIgnoreCase));
        if (alreadySet) return connectionString;

        var builder = new SqliteConnectionStringBuilder(connectionString) { Pooling = false };
        return builder.ConnectionString;
    }

    /// <summary>連線字串遮罩：log 與 Location 顯示用，不外流密碼</summary>
    private static string MaskConnectionString(string cs)
    {
        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase) &&
                        !p.TrimStart().StartsWith("Pwd", StringComparison.OrdinalIgnoreCase));
        return string.Join(";", parts);
    }
}
