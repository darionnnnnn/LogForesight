using Microsoft.EntityFrameworkCore;
using LogForesight.Sql;
using NLog;

namespace LogForesight;

/// <summary>
/// 依設定選擇儲存後端（Strategy + Factory）。兩種 provider：
///   - "Sqlite"（預設）：測試/開發用的單一 .db 檔
///   - "SqlServer"：正式環境（2000 台量級）
/// 新增後端時這裡是唯一需要改的地方，呼叫端（Program.cs／LogAnalysisService／Web DI）不需修改。
///
/// **全部資料走 SQL，無檔案**：分析紀錄以正規化列＋JSON 存（lf_daily_records/lf_top_issues），
/// webdata 各 store 透過 IJsonBlobStore（整份型 → lf_blobs）與 IJsonLogStore（append-only →
/// lf_log_lines）走資料庫，store 業務邏輯不受後端影響。LINQ 保持 provider 中立，
/// SQLite 上跑合約測試驗證語意。
///
/// （2026-07-24 起 Jsonl 檔案後端已退役，見 docs/NETIQ-WEB-CONFIG-PLAN.md 定案 10。
/// `Storage.Type` 設成非 Sqlite/SqlServer 的值一律於啟動時報錯，見 AppSettings 驗證。）
/// </summary>
public static class StorageFactory
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly object _schemaLock = new();

    private static Func<LfDbContext>? _dbFactory;
    private static string _dbDesc = "";

    /// <summary>
    /// 取（快取的）EF DbContext 工廠。首次建立時依 provider 建連線並 EnsureCreated 建表（idempotent）。
    /// Sqlite（測試/開發）：ConnectionString 或退回 {fallbackDir}\logforesight.db；SqlServer（正式）：ConnectionString。
    /// </summary>
    private static Func<LfDbContext> GetDbFactory(StorageSettings settings, string fallbackDir)
    {
        lock (_schemaLock)
        {
            if (_dbFactory != null) return _dbFactory;

            DbContextOptions<LfDbContext> options;
            if (settings.Type == "Sqlite")
            {
                var cs = string.IsNullOrWhiteSpace(settings.ConnectionString)
                    ? $"Data Source={Path.Combine(fallbackDir, "logforesight.db")}"
                    : settings.ConnectionString;
                options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(cs).Options;
                _dbDesc = $"Sqlite（{cs}）";
            }
            else
            {
                options = new DbContextOptionsBuilder<LfDbContext>().UseSqlServer(settings.ConnectionString).Options;
                _dbDesc = $"SqlServer（{MaskConnectionString(settings.ConnectionString)}）";
            }

            Func<LfDbContext> factory = () => new LfDbContext(options);

            Log.Info("[SQL] 啟用 {Desc} 後端（全資料走 SQL，無檔案）。正在確保 schema…", _dbDesc);
            try
            {
                using var ctx = factory();
                ctx.Database.EnsureCreated();
                Log.Info("[SQL] schema 確認完成");
            }
            catch (Exception ex)
            {
                // 連線/建表失敗要顯性——這是 SQL 模式跑不起來的第一線索
                Log.Error(ex, "[SQL] 連線或建立 schema 失敗：{Msg}。請確認 Storage.ConnectionString 與資料庫可用性。", ex.Message);
                throw;
            }

            _dbFactory = factory;
            return factory;
        }
    }

    /// <summary>store 的底層 blob（整份 JSON 存 lf_blobs 一列，key 為鍵）</summary>
    private static IJsonBlobStore Blob(StorageSettings settings, string dataRoot, string key) =>
        new EfJsonBlobStore(GetDbFactory(settings, dataRoot), key);

    /// <summary>store 的底層 append-only 逐行資料（lf_log_lines，key 為鍵）</summary>
    private static IJsonLogStore LogStore(StorageSettings settings, string dataRoot, string key) =>
        new EfJsonLogStore(GetDbFactory(settings, dataRoot), key);

    /// <summary>EF 分析紀錄 store。ownerHost 由批次傳入；fallbackDir 供 Sqlite 預設 db 路徑</summary>
    private static EfAnalysisRecordStore CreateEfRecordStore(StorageSettings settings, HostKey? ownerHost, string fallbackDir) =>
        new(GetDbFactory(settings, fallbackDir), _dbDesc, ownerHost);

    /// <summary>連線字串遮罩：log 與 Location 顯示用，不外流密碼</summary>
    private static string MaskConnectionString(string cs)
    {
        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase) &&
                        !p.TrimStart().StartsWith("Pwd", StringComparison.OrdinalIgnoreCase));
        return string.Join(";", parts);
    }

    /// <param name="dataRoot">
    /// 資料根目錄：Sqlite 模式下用來決定預設 db 檔位置的退路（ConnectionString 未設時）。
    /// </param>
    /// <param name="ownerHost">
    /// 批次寫入端的「本機」識別。傳入時，缺日判定與趨勢基準等批次面讀寫只看這台主機自己的
    /// 紀錄。Web 查詢端不傳，維持不分主機。
    /// </param>
    public static IAnalysisRecordStore CreateRecordStore(StorageSettings settings, string dataRoot, HostKey? ownerHost = null) =>
        CreateEfRecordStore(settings, ownerHost, dataRoot);

    /// <summary>規則儲存後端（DB blob，key=rules）</summary>
    public static IKnownIssueRuleStore CreateRuleStore(StorageSettings settings, string dataRoot) =>
        new JsonKnownIssueRuleStore(Blob(settings, dataRoot, "rules"));

    /// <summary>抑制設定的儲存後端（DB blob，key=suppressions）</summary>
    public static ISuppressionStore CreateSuppressionStore(StorageSettings settings, string dataRoot) =>
        new JsonSuppressionStore(Blob(settings, dataRoot, "suppressions"));

    // ── Web 自有資料的儲存後端（docs/WEB-SPEC.md §10.2）────────────────────────────

    /// <summary>Web 使用者</summary>
    public static IUserStore CreateUserStore(StorageSettings settings, string dataRoot) =>
        new JsonUserStore(Blob(settings, dataRoot, "users"));

    /// <summary>使用者群組</summary>
    public static IUserGroupStore CreateUserGroupStore(StorageSettings settings, string dataRoot) =>
        new JsonUserGroupStore(Blob(settings, dataRoot, "user_groups"));

    /// <summary>Web 的分析紀錄查詢（多條件篩選）</summary>
    public static IAnalysisRecordQuery CreateRecordQuery(StorageSettings settings, string dataRoot) =>
        // Web 查詢面不分主機（ownerHost=null）；Query/GetOne 自帶可見範圍過濾
        CreateEfRecordStore(settings, null, dataRoot);

    /// <summary>報告全文讀取（export\ 下的 txt——交付物，不屬「JSON 作為資料庫」，維持檔案）</summary>
    public static IReportReader CreateReportReader(StorageSettings settings, string dataRoot) =>
        new FileReportReader(dataRoot);

    /// <summary>主機——批次與 Web 共同寫入，職責見 IHostStore 註解</summary>
    public static IHostStore CreateHostStore(StorageSettings settings, string dataRoot) =>
        new JsonHostStore(Blob(settings, dataRoot, "hosts"));

    /// <summary>主機群組</summary>
    public static IHostGroupStore CreateHostGroupStore(StorageSettings settings, string dataRoot) =>
        new JsonHostGroupStore(Blob(settings, dataRoot, "host_groups"));

    /// <summary>群組授權對應</summary>
    public static IGroupAccessStore CreateGroupAccessStore(StorageSettings settings, string dataRoot) =>
        new JsonGroupAccessStore(Blob(settings, dataRoot, "group_access"));

    /// <summary>操作稽核</summary>
    public static IAuditLogStore CreateAuditLogStore(StorageSettings settings, string dataRoot) =>
        new JsonAuditLogStore(LogStore(settings, dataRoot, "audit"));

    /// <summary>內建規則的原廠種子鏡像（供「回復預設」比對與還原）</summary>
    public static IRuleSeedStore CreateRuleSeedStore(StorageSettings settings, string dataRoot) =>
        new JsonRuleSeedStore(Blob(settings, dataRoot, "rule_seeds"));

    /// <summary>批次執行紀錄——批次寫、Web 讀</summary>
    public static IBatchRunStore CreateBatchRunStore(StorageSettings settings, string dataRoot) =>
        new JsonBatchRunStore(
            LogStore(settings, dataRoot, "batch_runs"),
            LogStore(settings, dataRoot, "batch_run_logs"));

    /// <summary>風險日處理狀態</summary>
    public static IRecordHandlingStore CreateHandlingStore(StorageSettings settings, string dataRoot) =>
        new JsonRecordHandlingStore(
            Blob(settings, dataRoot, "record_handling"),
            LogStore(settings, dataRoot, "handling_log"));

    /// <summary>Web AI 加值輸出的快取</summary>
    public static IAiCacheStore CreateAiCacheStore(StorageSettings settings, string dataRoot) =>
        new JsonAiCacheStore(Blob(settings, dataRoot, "ai_cache"));

    /// <summary>問題層級處理狀態——Web 寫，日層級結案與否由此推導</summary>
    public static IIssueHandlingStore CreateIssueHandlingStore(StorageSettings settings, string dataRoot) =>
        new JsonIssueHandlingStore(Blob(settings, dataRoot, "issue_handling"));

    /// <summary>已知雜訊記憶（§5.1 D-1 #3）——同主機同簽章的自動雜訊判讀依據</summary>
    public static INoiseMarkStore CreateNoiseMarkStore(StorageSettings settings, string dataRoot) =>
        new JsonNoiseMarkStore(Blob(settings, dataRoot, "noise_marks"));

    /// <summary>
    /// 權限異動（perm_changes 由批次寫、perm_confirms 由 Web 寫）。
    /// 兩者各有單一寫入者，見 JsonPermissionChangeStore 的類別註解。
    /// </summary>
    public static IPermissionChangeStore CreatePermissionChangeStore(StorageSettings settings, string dataRoot) =>
        new JsonPermissionChangeStore(
            LogStore(settings, dataRoot, "perm_changes"),
            Blob(settings, dataRoot, "perm_confirms"));

    /// <summary>權限/角色異動監控的快照（批次寫、批次讀，Web 不碰）</summary>
    public static IPermissionSnapshotStore CreatePermissionSnapshotStore(StorageSettings settings, string dataRoot) =>
        new JsonPermissionSnapshotStore(Blob(settings, dataRoot, "permission_snapshot"));

    /// <summary>CSV／NetIQ 匯入紀錄</summary>
    public static IImportLogStore CreateImportLogStore(StorageSettings settings, string dataRoot) =>
        new JsonImportLogStore(LogStore(settings, dataRoot, "import_logs"));

    /// <summary>NetIQ Sentinel 連線設定（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1、2）</summary>
    public static ISentinelStore CreateSentinelStore(StorageSettings settings, string dataRoot) =>
        new JsonSentinelStore(Blob(settings, dataRoot, "sentinels"));
}
