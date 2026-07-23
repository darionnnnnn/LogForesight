using Microsoft.EntityFrameworkCore;
using LogForesight.Sql;
using NLog;

namespace LogForesight;

/// <summary>
/// 依設定選擇儲存後端（Strategy + Factory）。三種 provider：
///   - "Jsonl"（預設）：現行檔案格式，單機開箱即用
///   - "Sqlite"：測試/開發用的單一 .db 檔（真資料庫，取代 JSONL 檔案）
///   - "SqlServer"：正式環境（2000 台量級）
/// 新增後端時這裡是唯一需要改的地方，呼叫端（Program.cs／LogAnalysisService／Web DI）不需修改。
///
/// SQL 模式（Sqlite/SqlServer）下**全部資料**走 SQL：分析紀錄以正規化列＋JSON 存
/// （lf_daily_records/lf_top_issues），webdata 各 store 透過 IJsonBlobStore（整份型 → lf_blobs）
/// 與 IJsonLogStore（append-only → lf_log_lines）改走資料庫，store 業務邏輯完全沒改。
/// LINQ 保持 provider 中立，SQLite 上跑同一組合約測試驗證語意逐位一致。
/// </summary>
public static class StorageFactory
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly object _schemaLock = new();

    private static Func<LfDbContext>? _dbFactory;
    private static string _dbDesc = "";

    /// <summary>Type 是否為 SQL 後端（Sqlite 測試/開發、SqlServer 正式）</summary>
    private static bool IsSql(StorageSettings settings) =>
        settings.Type is "Sqlite" or "SqlServer";

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

    /// <summary>依 provider 建立 store 的底層 blob（SQL→DB 一列以 key 為鍵、Jsonl→jsonlPath 檔案）</summary>
    private static IJsonBlobStore Blob(StorageSettings settings, string dataRoot, string key, string jsonlPath) =>
        IsSql(settings)
            ? new EfJsonBlobStore(GetDbFactory(settings, dataRoot), key)
            : new FileJsonBlobStore(jsonlPath);

    /// <summary>依 provider 建立 append-only 逐行 store（SQL→lf_log_lines 以 key 為鍵、Jsonl→jsonlPath 檔案）</summary>
    private static IJsonLogStore LogStore(StorageSettings settings, string dataRoot, string key, string jsonlPath) =>
        IsSql(settings)
            ? new EfJsonLogStore(GetDbFactory(settings, dataRoot), key)
            : new FileJsonLogStore(jsonlPath);

    /// <summary>SQL 模式的 EF 分析紀錄 store。ownerHost 由批次傳入；fallbackDir 供 Sqlite 預設 db 路徑</summary>
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

    /// <param name="ownerHost">
    /// 批次寫入端的「本機」識別。傳入時，缺日判定與趨勢基準等批次面讀寫只看這台主機自己的
    /// 紀錄（見 <see cref="JsonlAnalysisRecordStore"/> 的 ownerHost 說明）。Web 查詢端不傳，維持不分主機。
    /// </param>
    public static IAnalysisRecordStore CreateRecordStore(StorageSettings settings, string? filePath = null, HostKey? ownerHost = null)
    {
        switch (settings.Type)
        {
            case "Sqlite":
            case "SqlServer":
                return CreateEfRecordStore(settings, ownerHost, Path.GetDirectoryName(filePath) ?? ".");
            case "Jsonl":
                return new JsonlAnalysisRecordStore(filePath, ownerHost);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，改用預設的 Jsonl。");
                return new JsonlAnalysisRecordStore(filePath, ownerHost);
        }
    }

    /// <summary>規則儲存後端（rules.json／DB blob）</summary>
    public static IKnownIssueRuleStore CreateRuleStore(StorageSettings settings, string? filePath = null)
    {
        var path = filePath ?? Path.Combine(AppContext.BaseDirectory, "rules.json");
        return new JsonKnownIssueRuleStore(Blob(settings, Path.GetDirectoryName(path) ?? ".", "rules", path));
    }

    /// <summary>抑制設定的儲存後端（suppressions.json／DB blob）</summary>
    public static ISuppressionStore CreateSuppressionStore(StorageSettings settings, string? filePath = null)
    {
        var path = filePath ?? Path.Combine(AppContext.BaseDirectory, "suppressions.json");
        return new JsonSuppressionStore(Blob(settings, Path.GetDirectoryName(path) ?? ".", "suppressions", path));
    }

    // ── Web 自有資料的儲存後端（docs/WEB-SPEC.md §10.2）────────────────────────────
    // 檔案位於 {DataRoot}\webdata\，與批次的 history.txt／rules.json 同一個資料根目錄：
    // JSONL 後端下 Web 與批次讀寫同一份資料，這是「JSONL 即資料庫」決策的直接結果。
    // 未來 SQL 後端在此加 case，Web 的 Service 層完全不需修改。

    /// <summary>Web 使用者（webdata\users.json）</summary>
    public static IUserStore CreateUserStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonUserStore(Blob(settings, dataRoot, "users", WebDataPath(dataRoot, "users.json")));
        }
    }

    /// <summary>使用者群組（webdata\groups.json）</summary>
    public static IUserGroupStore CreateUserGroupStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonUserGroupStore(Blob(settings, dataRoot, "user_groups", WebDataPath(dataRoot, "groups.json")));
        }
    }

    /// <summary>Web 的分析紀錄查詢（多條件篩選）。JSONL 後端與寫入端共用同一份 history.txt</summary>
    public static IAnalysisRecordQuery CreateRecordQuery(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Sqlite":
            case "SqlServer":
                // Web 查詢面不分主機（ownerHost=null）；Query/GetOne 自帶可見範圍過濾
                return CreateEfRecordStore(settings, null, dataRoot);
            case "Jsonl":
            default:
                return new JsonlAnalysisRecordStore(Path.Combine(dataRoot, "history.txt"));
        }
    }

    /// <summary>報告全文讀取（export\ 下的 txt）</summary>
    public static IReportReader CreateReportReader(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new FileReportReader(dataRoot);
        }
    }

    /// <summary>主機（webdata\hosts.json）——批次與 Web 共同寫入，職責見 IHostStore 註解</summary>
    public static IHostStore CreateHostStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonHostStore(Blob(settings, dataRoot, "hosts", WebDataPath(dataRoot, "hosts.json")));
        }
    }

    /// <summary>主機群組（webdata\host_groups.json）</summary>
    public static IHostGroupStore CreateHostGroupStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonHostGroupStore(Blob(settings, dataRoot, "host_groups", WebDataPath(dataRoot, "host_groups.json")));
        }
    }

    /// <summary>群組授權對應（webdata\group_access.json）</summary>
    public static IGroupAccessStore CreateGroupAccessStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonGroupAccessStore(Blob(settings, dataRoot, "group_access", WebDataPath(dataRoot, "group_access.json")));
        }
    }

    /// <summary>操作稽核（webdata\audit.jsonl）</summary>
    public static IAuditLogStore CreateAuditLogStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonAuditLogStore(LogStore(settings, dataRoot, "audit", WebDataPath(dataRoot, "audit.jsonl")));
        }
    }

    /// <summary>內建規則的原廠種子鏡像（rule_seeds.json，供「回復預設」比對與還原）</summary>
    public static IRuleSeedStore CreateRuleSeedStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonRuleSeedStore(Blob(settings, dataRoot, "rule_seeds", Path.Combine(dataRoot, "rule_seeds.json")));
        }
    }

    /// <summary>批次執行紀錄（rundata\runs.jsonl ＋ run_logs.jsonl）——批次寫、Web 讀</summary>
    public static IBatchRunStore CreateBatchRunStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonBatchRunStore(
                    LogStore(settings, dataRoot, "batch_runs", Path.Combine(dataRoot, "rundata", "runs.jsonl")),
                    LogStore(settings, dataRoot, "batch_run_logs", Path.Combine(dataRoot, "rundata", "run_logs.jsonl")));
        }
    }

    /// <summary>風險日處理狀態（webdata\handling.json ＋ handling_log.jsonl）</summary>
    public static IRecordHandlingStore CreateHandlingStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonRecordHandlingStore(
                    Blob(settings, dataRoot, "record_handling", WebDataPath(dataRoot, "handling.json")),
                    LogStore(settings, dataRoot, "handling_log", WebDataPath(dataRoot, "handling_log.jsonl")));
        }
    }

    /// <summary>Web AI 加值輸出的快取（webdata\ai_cache.json）</summary>
    public static IAiCacheStore CreateAiCacheStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonAiCacheStore(Blob(settings, dataRoot, "ai_cache", WebDataPath(dataRoot, "ai_cache.json")));
        }
    }

    /// <summary>問題層級處理狀態（webdata\issue_handling.json）——Web 寫，日層級結案與否由此推導</summary>
    public static IIssueHandlingStore CreateIssueHandlingStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonIssueHandlingStore(Blob(settings, dataRoot, "issue_handling", WebDataPath(dataRoot, "issue_handling.json")));
        }
    }

    /// <summary>
    /// 權限異動（rundata\perm_changes.jsonl 由批次寫、webdata\perm_confirms.json 由 Web 寫）。
    /// 兩個檔案各有單一寫入者，見 JsonPermissionChangeStore 的類別註解。
    /// </summary>
    public static IPermissionChangeStore CreatePermissionChangeStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonPermissionChangeStore(
                    LogStore(settings, dataRoot, "perm_changes", Path.Combine(dataRoot, "rundata", "perm_changes.jsonl")),
                    Blob(settings, dataRoot, "perm_confirms", WebDataPath(dataRoot, "perm_confirms.json")));
        }
    }

    /// <summary>CSV 匯入紀錄（webdata\import_logs.jsonl）</summary>
    public static IImportLogStore CreateImportLogStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonImportLogStore(LogStore(settings, dataRoot, "import_logs", WebDataPath(dataRoot, "import_logs.jsonl")));
        }
    }

    private static string WebDataPath(string dataRoot, string fileName) =>
        Path.Combine(dataRoot, "webdata", fileName);
}
