using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 開機時的冪等 DDL 升級（docs/DB-SPEC.md 定案 13：自製冪等 DDL，不用 EF Core Migrations——
/// 雙 provider 各自維護一份 migration 歷史的長期成本，對這個專案的變更頻率不成比例）。
///
/// <see cref="Database.EnsureCreated"/> 只在資料庫不存在時建表，對既有 DB 不補欄不補索引；
/// 這裡補上那個缺口：逐步「檢查缺什麼 → 缺才補」，每一步都可重複執行不出錯——
/// 新建的 DB 因為 EnsureCreated 已經建好最新 schema，每一步在新 DB 上都是 no-op。
///
/// 於 <see cref="StorageBackend"/> 建連線、EnsureCreated 之後呼叫，批次與 Web 啟動時都會跑到。
/// </summary>
internal static class SchemaUpgrader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static void Upgrade(LfDbContext ctx)
    {
        var isSqlite = ctx.Database.IsSqlite();

        AddColumnIfMissing(ctx, isSqlite, "lf_log_lines", "created_at", isSqlite ? "TEXT NULL" : "datetime2 NULL");
        AddIndexIfMissing(ctx, isSqlite, "lf_log_lines", "IX_lf_log_lines_log_key_created_at", "log_key, created_at");

        // P1-2：問題查詢頁分頁排序需要的抽出欄（見 LfDbContext.DailyRecordRow.HasCorrelation 註解）。
        // 舊資料補上後預設為 0/false，下次批次重新分析同一天會自然更新為正確值。
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "has_correlation",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");

        // 風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）：這是 SQL 後端上線以來第一張
        // 「不是靠 EnsureCreated 建出來」的全新資料表——既有部署的 DB 已經存在，EnsureCreated
        // 對它不會做任何事（只在資料庫整個不存在時建表），所以這裡要用跟補欄位/補索引同一套
        // 「檢查缺什麼→缺才補」冪等 DDL 補上整張表，新 DB 則由 EnsureCreated 直接建好、這裡 no-op。
        CreateTableIfMissing(ctx, isSqlite, "lf_risky_events",
            isSqlite ? SqliteCreateRiskyEventsTable : SqlServerCreateRiskyEventsTable);
        AddIndexIfMissing(ctx, isSqlite, "lf_risky_events",
            "IX_lf_risky_events_host_id_date_source_event_id", "host_id, date, source, event_id");
        AddIndexIfMissing(ctx, isSqlite, "lf_risky_events", "IX_lf_risky_events_date", "date");

        // 問題事實表的聚合維度（docs/SCALE-ISSUE-FIRST-PLAN.md P4／根因 C）。
        // 舊列的預設值（host_id=0、event_count=0、log_name=''）**不是正確資料**——
        // 由 TopIssueBackfiller 在背景補齊，補完之前畫面要誠實標示「統計中」。
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "host_id",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bigint NOT NULL DEFAULT 0");
        // record_date 的預設是 DateTime.MinValue（0001-01-01）而不是 NULL：模型上它是
        // 非可空的 DateTime，用 NULL 會讓讀取直接炸；MinValue 同時是「這列還沒回填」的
        // 明確標記——真實的紀錄日期不可能是西元 1 年
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "record_date",
            isSqlite ? "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'" : "datetime2 NOT NULL DEFAULT '0001-01-01'");
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "event_count",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "int NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "elevates_day_risk",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "log_name",
            isSqlite ? "TEXT NOT NULL DEFAULT ''" : "nvarchar(255) NOT NULL DEFAULT ''");
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "entry_type",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "int NOT NULL DEFAULT 0");
        AddIndexIfMissing(ctx, isSqlite, "lf_top_issues",
            "IX_lf_top_issues_date_signature", "record_date, source_name, event_id");
        AddIndexIfMissing(ctx, isSqlite, "lf_top_issues", "IX_lf_top_issues_host_date", "host_id, record_date");

        // 處理狀態三表（docs/SCALE-ISSUE-FIRST-PLAN.md P3）：與 lf_risky_events 完全同一套
        // 「檢查缺什麼→缺才補」的冪等 DDL——既有部署的 DB 已經存在，EnsureCreated 對它
        // 什麼都不做，新 DB 則由 EnsureCreated 建好、這裡 no-op。
        CreateTableIfMissing(ctx, isSqlite, "lf_issue_handling",
            isSqlite ? SqliteCreateIssueHandling : SqlServerCreateIssueHandling);
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling",
            "IX_lf_issue_handling_unique", "host_name_key, record_date, issue_key", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling",
            "IX_lf_issue_handling_host_date", "host_name_key, record_date");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling", "IX_lf_issue_handling_case_id", "case_id");

        CreateTableIfMissing(ctx, isSqlite, "lf_issue_cases",
            isSqlite ? SqliteCreateIssueCases : SqlServerCreateIssueCases);
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_cases",
            "IX_lf_issue_cases_host_issue_closed", "host_name_key, issue_key, closed_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_cases",
            "IX_lf_issue_cases_handler_closed", "handler_id, closed_at");

        CreateTableIfMissing(ctx, isSqlite, "lf_record_handling",
            isSqlite ? SqliteCreateRecordHandling : SqlServerCreateRecordHandling);
        AddIndexIfMissing(ctx, isSqlite, "lf_record_handling",
            "IX_lf_record_handling_unique", "host_name_key, record_date", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_record_handling", "IX_lf_record_handling_handler", "handler_id");
        AddIndexIfMissing(ctx, isSqlite, "lf_record_handling", "IX_lf_record_handling_status", "status");
    }

    private const string SqliteCreateIssueHandling = """
        CREATE TABLE lf_issue_handling (
            id INTEGER NOT NULL CONSTRAINT PK_lf_issue_handling PRIMARY KEY AUTOINCREMENT,
            host_name TEXT NOT NULL,
            host_name_key TEXT NOT NULL,
            record_date TEXT NOT NULL,
            issue_key TEXT NOT NULL,
            status TEXT NOT NULL,
            actor_id INTEGER NULL,
            actor_account TEXT NOT NULL,
            note TEXT NULL,
            due_date TEXT NULL,
            case_id TEXT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateIssueHandling = """
        CREATE TABLE lf_issue_handling (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_issue_handling PRIMARY KEY,
            host_name nvarchar(255) NOT NULL,
            host_name_key nvarchar(255) NOT NULL,
            record_date datetime2 NOT NULL,
            issue_key nvarchar(512) NOT NULL,
            status nvarchar(30) NOT NULL,
            actor_id bigint NULL,
            actor_account nvarchar(255) NOT NULL,
            note nvarchar(max) NULL,
            due_date datetime2 NULL,
            case_id nvarchar(64) NULL,
            updated_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreateIssueCases = """
        CREATE TABLE lf_issue_cases (
            case_id TEXT NOT NULL CONSTRAINT PK_lf_issue_cases PRIMARY KEY,
            host_name TEXT NOT NULL,
            host_name_key TEXT NOT NULL,
            issue_key TEXT NOT NULL,
            issue_label TEXT NOT NULL,
            status TEXT NOT NULL,
            handler_id INTEGER NULL,
            note TEXT NULL,
            due_date TEXT NULL,
            first_linked_date TEXT NOT NULL,
            last_linked_date TEXT NOT NULL,
            closed_at TEXT NULL,
            created_at TEXT NOT NULL,
            created_by_account TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateIssueCases = """
        CREATE TABLE lf_issue_cases (
            case_id nvarchar(64) NOT NULL CONSTRAINT PK_lf_issue_cases PRIMARY KEY,
            host_name nvarchar(255) NOT NULL,
            host_name_key nvarchar(255) NOT NULL,
            issue_key nvarchar(512) NOT NULL,
            issue_label nvarchar(512) NOT NULL,
            status nvarchar(30) NOT NULL,
            handler_id bigint NULL,
            note nvarchar(max) NULL,
            due_date datetime2 NULL,
            first_linked_date datetime2 NOT NULL,
            last_linked_date datetime2 NOT NULL,
            closed_at datetime2 NULL,
            created_at datetime2 NOT NULL,
            created_by_account nvarchar(255) NOT NULL,
            updated_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreateRecordHandling = """
        CREATE TABLE lf_record_handling (
            id INTEGER NOT NULL CONSTRAINT PK_lf_record_handling PRIMARY KEY AUTOINCREMENT,
            host_name TEXT NOT NULL,
            host_name_key TEXT NOT NULL,
            record_date TEXT NOT NULL,
            status TEXT NOT NULL,
            handler_id INTEGER NULL,
            due_date TEXT NULL,
            note TEXT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateRecordHandling = """
        CREATE TABLE lf_record_handling (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_record_handling PRIMARY KEY,
            host_name nvarchar(255) NOT NULL,
            host_name_key nvarchar(255) NOT NULL,
            record_date datetime2 NOT NULL,
            status nvarchar(30) NOT NULL,
            handler_id bigint NULL,
            due_date datetime2 NULL,
            note nvarchar(max) NULL,
            updated_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreateRiskyEventsTable = """
        CREATE TABLE lf_risky_events (
            id INTEGER NOT NULL CONSTRAINT PK_lf_risky_events PRIMARY KEY AUTOINCREMENT,
            host_id INTEGER NOT NULL,
            date TEXT NOT NULL,
            log_name TEXT NOT NULL,
            source TEXT NOT NULL,
            event_id INTEGER NOT NULL,
            entry_type INTEGER NOT NULL,
            event_time TEXT NOT NULL,
            message TEXT NOT NULL,
            rule_id TEXT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateRiskyEventsTable = """
        CREATE TABLE lf_risky_events (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_risky_events PRIMARY KEY,
            host_id bigint NOT NULL,
            date datetime2 NOT NULL,
            log_name nvarchar(255) NOT NULL,
            source nvarchar(255) NOT NULL,
            event_id int NOT NULL,
            entry_type int NOT NULL,
            event_time datetime2 NOT NULL,
            message nvarchar(max) NOT NULL,
            rule_id nvarchar(64) NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private static void CreateTableIfMissing(LfDbContext ctx, bool isSqlite, string table, string createTableSql)
    {
        if (TableExists(ctx, isSqlite, table)) return;

        Log.Info("[SQL] schema 升級：建立資料表 {Table}", table);
        ctx.Database.ExecuteSqlRaw(createTableSql);
    }

    /// <summary>SQLite：<c>sqlite_master</c>；SqlServer：INFORMATION_SCHEMA.TABLES</summary>
    private static bool TableExists(LfDbContext ctx, bool isSqlite, string table)
    {
        var names = isSqlite
            ? ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", table).ToList()
            : ctx.Database.SqlQueryRaw<string>(
                "SELECT TABLE_NAME AS Value FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = {0}", table).ToList();
        return names.Contains(table, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddColumnIfMissing(LfDbContext ctx, bool isSqlite, string table, string column, string columnDefinition)
    {
        // 表不存在就跳過：ALTER TABLE 對不存在的表會直接拋，讓整個站台啟動失敗。
        // 正常情況下 EnsureCreated 已經建好所有表，走到這裡代表這個部署的 schema
        // 比預期更舊（或這是只建了部分表的測試情境）——缺表不是這支函式該修的問題。
        if (!TableExists(ctx, isSqlite, table)) return;

        if (ColumnExists(ctx, isSqlite, table, column)) return;

        Log.Info("[SQL] schema 升級：{Table} 補 {Column} 欄", table, column);
        // 值全為內部寫死的表/欄位名稱常數（非外部輸入），且 SQL 本就不支援參數化識別字——
        // 組成一般字串變數而非直接以內插字串呼叫，EF1002（內插字串注入警告）因此不適用
        var sql = "ALTER TABLE " + table + " ADD " + (isSqlite ? "COLUMN " : "") + column + " " + columnDefinition;
        ctx.Database.ExecuteSqlRaw(sql);
    }

    private static void AddIndexIfMissing(
        LfDbContext ctx, bool isSqlite, string table, string indexName, string columns, bool unique = false)
    {
        // 同 AddColumnIfMissing：表不存在就跳過，不讓 CREATE INDEX 炸掉啟動
        if (!TableExists(ctx, isSqlite, table)) return;

        if (IndexExists(ctx, isSqlite, table, indexName)) return;

        Log.Info("[SQL] schema 升級：{Table} 補索引 {Index}", table, indexName);
        var sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + indexName + " ON " + table + " (" + columns + ")";
        ctx.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>SQLite：<c>pragma_table_info</c> 可當資料表函數查；SqlServer：INFORMATION_SCHEMA</summary>
    private static bool ColumnExists(LfDbContext ctx, bool isSqlite, string table, string column)
    {
        var names = isSqlite
            ? ctx.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('" + table + "')").ToList()
            : ctx.Database.SqlQueryRaw<string>(
                "SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0}", table).ToList();
        return names.Contains(column, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>SQLite：<c>pragma_index_list</c>；SqlServer：sys.indexes 依表名 join</summary>
    private static bool IndexExists(LfDbContext ctx, bool isSqlite, string table, string indexName)
    {
        var names = isSqlite
            ? ctx.Database.SqlQueryRaw<string>("SELECT name FROM pragma_index_list('" + table + "')").ToList()
            : ctx.Database.SqlQueryRaw<string>(
                "SELECT i.name AS Value FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = {0}", table).ToList();
        return names.Contains(indexName, StringComparer.OrdinalIgnoreCase);
    }
}
