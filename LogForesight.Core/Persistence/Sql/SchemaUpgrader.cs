using System.Collections.Concurrent;
using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NLog;
using LogForesight.Core.Persistence;

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

    /// <summary>
    /// 升級器建立的索引名稱（AddIndexIfMissing／AddFilteredUniqueIndexIfMissing 呼叫時登記）。
    /// 名稱都是常數，重複登記無副作用；移除重複索引時用它決定每組保留哪一個。
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> UpgraderIndexNames = new(StringComparer.OrdinalIgnoreCase);

    public static void Upgrade(LfDbContext ctx)
    {
        var isSqlite = ctx.Database.IsSqlite();

        AddColumnIfMissing(ctx, isSqlite, "lf_log_lines", "created_at", isSqlite ? "TEXT NULL" : "datetime2 NULL");
        AddIndexIfMissing(ctx, isSqlite, "lf_log_lines", "IX_lf_log_lines_log_key_created_at", "log_key, created_at");

        // 供上層快取失效判定的單調遞增版本號。不重用 updated_at 是因為 DateTime.Now 在 Windows 解析度約 15.6 ms，
        // 同 tick 寫入會漏更新，因此獨立加一欄。
        AddColumnIfMissing(ctx, isSqlite, "lf_blobs", "version",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bigint NOT NULL DEFAULT 0");

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
        // 舊列保留 NULL，交由 EfRiskyEventStore.BackfillSourceKeysBatch 在背景分批補齊；
        // 未完成前查詢仍走 source + UPPER()，避免啟動時掃描整張暫存表。
        AddColumnIfMissing(ctx, isSqlite, "lf_risky_events", "source_key",
            isSqlite ? "TEXT NULL" : "nvarchar(255) NULL");
        AddIndexIfMissing(ctx, isSqlite, "lf_risky_events",
            "IX_lf_risky_events_host_id_date_source_key_event_id", "host_id, date, source_key, event_id");
        AddIndexIfMissing(ctx, isSqlite, "lf_risky_events",
            "IX_lf_risky_events_host_id_date_source_event_id", "host_id, date, source, event_id");
        AddIndexIfMissing(ctx, isSqlite, "lf_risky_events", "IX_lf_risky_events_date", "date");

        // 問題事實表的聚合維度（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／根因 C）。
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
        // 慢查詢修正（回饋三十六輪批次B）：問題彙總的「event_id IN + 日期範圍」需要等值前導索引
        AddIndexIfMissing(ctx, isSqlite, "lf_top_issues", "IX_lf_top_issues_event_date", "event_id, record_date");

        // 處理狀態三表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3）：與 lf_risky_events 完全同一套
        // 「檢查缺什麼→缺才補」的冪等 DDL——既有部署的 DB 已經存在，EnsureCreated 對它
        // 什麼都不做，新 DB 則由 EnsureCreated 建好、這裡 no-op。
        CreateTableIfMissing(ctx, isSqlite, "lf_issue_handling",
            isSqlite ? SqliteCreateIssueHandling : SqlServerCreateIssueHandling);
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling",
            "IX_lf_issue_handling_unique", "host_name_key, record_date, issue_key", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling",
            "IX_lf_issue_handling_host_date", "host_name_key, record_date");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling", "IX_lf_issue_handling_case_id", "case_id");
        // 沿用此問題上次的說明（回饋第 50 輪 C-4）：依問題簽章取最新一筆說明
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_handling",
            "IX_lf_issue_handling_issue_key_updated_at", "issue_key, updated_at");

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

        // 首次寫入時間（回饋十九輪批次B，MTTA 保底）：舊列為 NULL，本輪不回填
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_handling", "created_at", isSqlite ? "TEXT NULL" : "datetime2 NULL");

        // 讀取面 SQL 化的抽出欄（回饋十九輪批次B）。舊列的預設值不是正確資料，
        // 由 DailyRecordBackfiller 依 extract_version 背景補齊（同 lf_top_issues 既有機制）。
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "headline",
            isSqlite ? "TEXT NOT NULL DEFAULT ''" : "nvarchar(max) NOT NULL DEFAULT ''");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "data_incomplete",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "security_log_available",
            isSqlite ? "INTEGER NULL" : "bit NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "error_count",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "int NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "warning_count",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "int NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "ai_analyzed",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "ai_pending",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "extract_version",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "int NOT NULL DEFAULT 0");
        AddIndexIfMissing(ctx, isSqlite, "lf_daily_records", "IX_lf_daily_records_extract_version", "extract_version");
        AddIndexIfMissing(ctx, isSqlite, "lf_daily_records",
            "IX_lf_daily_records_ai_pending_record_date", "ai_pending, record_date");
        // 慢查詢修正（回饋三十六輪批次B）：可行動快照的「risk_level IN + 日期範圍」篩選
        AddIndexIfMissing(ctx, isSqlite, "lf_daily_records", "IX_lf_daily_records_risk_date", "risk_level, record_date");

        // 詳情兩層保留期的標記
        AddColumnIfMissing(ctx, isSqlite, "lf_daily_records", "detail_pruned",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");

        // 依問題視角全面 SQL 化的最後兩欄（回饋十九輪批次B）：見 LfDbContext.TopIssueRow 類別註解。
        // event_key 補上後，SchemaUpgrader 這裡刻意**不**回填舊列（回填只在 TopIssueBackfiller
        // 尚未跑完的舊列上才有意義；已回填過的舊列即使沒有 event_key 也代表當初分析時
        // EventKey 恆為空——Windows 事件與規劃 §8.1 缺陷 1 修復前的 Linux 事件皆是如此，
        // 語意上就是空字串，不是「還沒回填」）。
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "known_issue", isSqlite ? "TEXT NULL" : "nvarchar(max) NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "event_key",
            isSqlite ? "TEXT NOT NULL DEFAULT ''" : "nvarchar(255) NOT NULL DEFAULT ''");
        // 來源名稱的寫入時計算鍵；舊列保留 NULL 供背景分批補齊，不在啟動閘掃全表。
        AddColumnIfMissing(ctx, isSqlite, "lf_top_issues", "source_key",
            isSqlite ? "TEXT NULL" : "nvarchar(255) NULL");
        AddIndexIfMissing(ctx, isSqlite, "lf_top_issues", "IX_lf_top_issues_source_key_event_date",
            "source_key, event_id, record_date");

        // 問題機房首見日（回饋十九輪批次B／G，↔ IssueFirstSeenRow）
        CreateTableIfMissing(ctx, isSqlite, "lf_issue_first_seen",
            isSqlite ? SqliteCreateIssueFirstSeen : SqlServerCreateIssueFirstSeen);
        // 種子（SeedIssueFirstSeenIfEmpty）已移往背景服務 IssueFirstSeenSeedHostedService。
        // 理由：原本放在啟動路徑上會因為全表掃描而導致 30 秒的 SCM 啟動逾時。

        // 權限異動檢核（↔ lf_permission_changes，含確認狀態）
        CreateTableIfMissing(ctx, isSqlite, "lf_permission_changes",
            isSqlite ? SqliteCreatePermissionChanges : SqlServerCreatePermissionChanges);
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_change_id", "change_id", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_status_detected_at", "status, detected_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_detected_at", "detected_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_host_detected", "host_name_key, detected_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_category_status", "category, status");
        AddIndexIfMissing(ctx, isSqlite, "lf_permission_changes",
            "IX_lf_permission_changes_created_at", "created_at");

        // 4670 的物件類型／處理程序名稱，與彙總列的涵蓋區間／對數（回饋二十六輪作業 B）。
        // 皆可為 null：既有列與逐則列本來就沒有這些值，不回填。
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "object_type",
            isSqlite ? "TEXT NULL" : "nvarchar(64) NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "process_name",
            isSqlite ? "TEXT NULL" : "nvarchar(max) NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "covered_from",
            isSqlite ? "TEXT NULL" : "datetime2 NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "covered_to",
            isSqlite ? "TEXT NULL" : "datetime2 NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "pair_count",
            isSqlite ? "INTEGER NULL" : "int NULL");

        // 未截斷的原始事件訊息（回饋二十八輪 P9）。只對升級後新寫入的列有值，既有列維持 null、不回填
        AddColumnIfMissing(ctx, isSqlite, "lf_permission_changes", "raw_text",
            isSqlite ? "TEXT NULL" : "nvarchar(max) NULL");

        // 報告全文（↔ lf_reports）：三種報告（風險／週檢／權限異動）的完整內容。
        // 唯一索引即 upsert 的判定鍵，**含 host_name**：host_id = 0 是「主機尚未登記」的哨兵值
        // 而不是一台主機，只用 host_id 會讓兩台都還沒登記成功的主機在同一天互相覆蓋。
        CreateTableIfMissing(ctx, isSqlite, "lf_reports",
            isSqlite ? SqliteCreateReports : SqlServerCreateReports);
        AddIndexIfMissing(ctx, isSqlite, "lf_reports",
            "IX_lf_reports_host_date_kind", "host_id, host_name, report_date, kind", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_reports", "IX_lf_reports_created_at", "created_at");

        // PRTG 鏡像層六張資料表：既有部署的 DB 已經存在，EnsureCreated 對它什麼都不做
        // （只在資料庫整個不存在時建表），因此需要透過自製冪等 DDL「檢查缺什麼→缺才補」來建立資料表與索引；
        // 新建的 DB 則由 EnsureCreated 直接建好最新 schema，這裡每一步都會是安靜的 no-op。
        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_devices",
            isSqlite ? SqliteCreatePrtgDevices : SqlServerCreatePrtgDevices);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_devices", "IX_lf_prtg_devices_ip", "ip");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_sensors",
            isSqlite ? SqliteCreatePrtgSensors : SqlServerCreatePrtgSensors);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_sensors", "IX_lf_prtg_sensors_device", "device_objid");
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_sensors", "IX_lf_prtg_sensors_type", "sensor_type");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_state_changes",
            isSqlite ? SqliteCreatePrtgStateChanges : SqlServerCreatePrtgStateChanges);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_state_changes",
            "IX_lf_prtg_state_ch_sensor", "sensor_objid, changed_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_state_changes",
            "IX_lf_prtg_state_ch_created", "created_at");
        // changed_at 單欄索引（回饋四十五輪 B5）：狀態變更的查詢主力是「某個時間區間內的全部變更」
        // （鏡像頁摘要的 Max(changed_at)、風險判定取區間），前導欄是 sensor_objid 的既有複合索引
        // 對這種查詢用不上，只能全表掃描。
        // 寫入成本誠實交代：這張表是夜間批次大量寫入，多一個索引就多一份維護成本；
        // 取捨是「夜間批次多付一點、白天使用者查詢快很多」。
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_state_changes",
            "IX_lf_prtg_state_ch_changed", "changed_at");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_values",
            isSqlite ? SqliteCreatePrtgValues : SqlServerCreatePrtgValues);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_values",
            "IX_lf_prtg_values_uniq", "sensor_objid, period_start", unique: true);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_values",
            "IX_lf_prtg_values_created", "created_at");
        // period_start 單欄索引（回饋四十五輪 B5）：數值查詢一律以時間區間為條件
        // （鏡像頁摘要的 Max(period_start)、校準與風險的區間取數），而既有 UNIQUE 索引的前導欄
        // 是 sensor_objid，不帶 sensor 條件的區間查詢完全吃不到它。
        // 寫入成本同上：這是全站資料量最大的一張表、夜間批次整批寫，索引維護成本換白天查詢速度。
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_values",
            "IX_lf_prtg_values_period", "period_start");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_host_map",
            isSqlite ? SqliteCreatePrtgHostMap : SqlServerCreatePrtgHostMap);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_host_map",
            "IX_lf_prtg_host_map_created", "created_at");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_manual_map",
            isSqlite ? SqliteCreatePrtgManualMap : SqlServerCreatePrtgManualMap);
        AddIndexIfMissing(ctx, isSqlite, "lf_prtg_manual_map",
            "IX_lf_prtg_manual_map_host", "host_id");

        CreateTableIfMissing(ctx, isSqlite, "lf_prtg_ip_excludes",
            isSqlite ? SqliteCreatePrtgIpExcludes : SqlServerCreatePrtgIpExcludes);

        // 交辦單（↔ WorkOrderRow／WorkOrderEventRow）與案件的成員欄。
        // 既有案件的 source_* 與 work_order_id 由 WorkOrderBackfiller 在背景補，不在啟動路徑上跑
        CreateTableIfMissing(ctx, isSqlite, "lf_work_orders",
            isSqlite ? SqliteCreateWorkOrders : SqlServerCreateWorkOrders);
        AddIndexIfMissing(ctx, isSqlite, "lf_work_orders", "IX_lf_work_orders_handler_closed", "handler_id, closed_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_work_orders",
            "IX_lf_work_orders_issue_closed", "source_key, event_id, closed_at");
        AddFilteredUniqueIndexIfMissing(ctx, isSqlite, "lf_work_orders",
            "IX_lf_work_orders_active_handler_issue", "handler_id, source_key, event_id", WorkOrderActiveIssueFilter);

        CreateTableIfMissing(ctx, isSqlite, "lf_work_order_events",
            isSqlite ? SqliteCreateWorkOrderEvents : SqlServerCreateWorkOrderEvents);
        AddIndexIfMissing(ctx, isSqlite, "lf_work_order_events",
            "IX_lf_work_order_events_order_created", "work_order_id, created_at");

        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "work_order_id", isSqlite ? "INTEGER NULL" : "bigint NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "source_key", isSqlite ? "TEXT NULL" : "nvarchar(255) NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "source_name", isSqlite ? "TEXT NULL" : "nvarchar(255) NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "event_id", isSqlite ? "INTEGER NULL" : "int NULL");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "day_sync_pending",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "cancelled",
            isSqlite ? "INTEGER NOT NULL DEFAULT 0" : "bit NOT NULL DEFAULT 0");
        AddColumnIfMissing(ctx, isSqlite, "lf_issue_cases", "day_sync_intent", isSqlite ? "TEXT NULL" : "nvarchar(max) NULL");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_cases",
            "IX_lf_issue_cases_work_order_closed", "work_order_id, closed_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_cases",
            "IX_lf_issue_cases_issue_closed", "source_key, event_id, closed_at");
        AddIndexIfMissing(ctx, isSqlite, "lf_issue_cases",
            "IX_lf_issue_cases_day_sync_pending", "day_sync_pending");

        // 最後一步：EF 預設命名與升級器命名並存時留下的重複索引（必須在所有 AddIndexIfMissing 之後，名稱才登記齊）。
        // 這是空間與寫入效能的整理、不是正確性前提——任何失敗（特別是尚未實機驗證的 SQL Server 系統目錄查詢）只記 Warn，不得擋住站台啟動
        try
        {
            RemoveDuplicateIndexes(ctx, isSqlite);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[SQL] 重複索引整理失敗（不影響站台運作，下次啟動再試）：{0}", ex.Message);
        }
    }

    /// <summary>索引的比對形狀：欄位（含順序）＋唯一性</summary>
    private sealed record IndexShape(string Name, bool Unique, string Columns);

    /// <summary>
    /// 移除重複索引（冪等）：同一張 lf_ 表中欄位（含順序）完全相同、唯一性相同、都不是部分索引、
    /// 都不是 sqlite_autoindex_ 的索引為一組，保留升級器使用的名稱、其餘 DROP。
    /// 組內沒有升級器名稱（無法判斷保留哪一個）→ 全部保留並 Warn。
    /// SQL Server 只報告不移除：DDL 尚未在 SQL Server 實機驗證。
    /// </summary>
    private static void RemoveDuplicateIndexes(LfDbContext ctx, bool isSqlite)
    {
        var tables = isSqlite
            ? ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'lf\\_%' ESCAPE '\\'").ToList()
            : ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables WHERE name LIKE 'lf[_]%'").ToList();

        foreach (var table in tables)
        {
            var groups = (isSqlite ? ReadSqliteIndexes(ctx, table) : ReadSqlServerIndexes(ctx, table))
                .GroupBy(i => (i.Unique, Columns: i.Columns.ToLowerInvariant()))
                .Where(g => g.Count() > 1);

            foreach (var group in groups)
            {
                var names = group.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (!isSqlite)
                {
                    Log.Warn("[SQL] schema 升級：{Table} 偵測到重複索引 {Indexes}，DDL 尚未在 SQL Server 實機驗證，不自動移除",
                        table, string.Join(" 與 ", names));
                    continue;
                }

                var keep = names.FirstOrDefault(n => UpgraderIndexNames.ContainsKey(n));
                if (keep == null)
                {
                    Log.Warn("[SQL] schema 升級：{Table} 偵測到重複索引 {Indexes}，組內沒有升級器使用的名稱、無法判斷保留哪一個，全部保留",
                        table, string.Join(" 與 ", names));
                    continue;
                }

                foreach (var name in names.Where(n => !string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info("[SQL] schema 升級：{Table} 移除與 {Keep} 重複的索引 {Index}", table, keep, name);
                    // 名稱來自系統目錄（非外部輸入），識別字不支援參數化
                    var sql = "DROP INDEX \"" + name.Replace("\"", "\"\"") + "\"";
                    ctx.Database.ExecuteSqlRaw(sql);
                }
            }
        }
    }

    /// <summary>SQLite：PRAGMA index_list／index_info；排除部分索引、sqlite_autoindex_ 與含運算式欄的索引</summary>
    private static List<IndexShape> ReadSqliteIndexes(LfDbContext ctx, string table)
    {
        var result = new List<IndexShape>();
        var rows = ctx.Database.SqlQueryRaw<string>(
            "SELECT name || '|' || \"unique\" || '|' || partial AS Value FROM pragma_index_list('" + table + "')").ToList();
        foreach (var row in rows)
        {
            var parts = row.Split('|');
            var name = parts[0];
            if (parts[2] != "0" || name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase)) continue;

            var columns = ctx.Database.SqlQueryRaw<string>(
                "SELECT ifnull(name, '') AS Value FROM pragma_index_info('" + name + "') ORDER BY seqno").ToList();
            if (columns.Count == 0 || columns.Any(c => c.Length == 0)) continue;
            result.Add(new IndexShape(name, parts[1] == "1", string.Join(",", columns)));
        }
        return result;
    }

    /// <summary>SQL Server：sys.indexes／sys.index_columns（只取鍵欄、依 key_ordinal）；排除篩選索引、主鍵、堆積</summary>
    private static List<IndexShape> ReadSqlServerIndexes(LfDbContext ctx, string table)
    {
        var rows = ctx.Database.SqlQueryRaw<string>(
            "SELECT i.name + '|' + CAST(i.is_unique AS varchar(1)) + '|' + " +
            "STUFF((SELECT ',' + c.name FROM sys.index_columns ic " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0 " +
            "ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '') AS Value " +
            "FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id " +
            "WHERE t.name = {0} AND i.type > 0 AND i.is_primary_key = 0 AND i.has_filter = 0", table).ToList();
        return rows.Select(r => r.Split('|'))
            .Where(p => p.Length == 3 && p[2].Length > 0)
            .Select(p => new IndexShape(p[0], p[1] == "1", p[2]))
            .ToList();
    }

    /// <summary>
    /// 「同一處理人同一問題至多一張進行中交辦單」部分唯一索引的過濾條件（與 LfDbContext 的 HasFilter 同字串）。
    /// 必須含 <c>source_key IS NOT NULL</c>：SQL Server 唯一索引把 NULL 視為相等、SQLite 視為相異，
    /// 不排除 NULL 的話同一人兩張多問題單在 SQL Server 會撞索引、在 SQLite 不會——兩後端行為分岔。
    /// </summary>
    internal const string WorkOrderActiveIssueFilter = "closed_at IS NULL AND source_key IS NOT NULL";

    /// <summary>部分唯一索引 DDL：SQL Server（filtered index）與 SQLite（partial index）同一份語法</summary>
    internal static string BuildFilteredUniqueIndexSql(string table, string indexName, string columns, string filter) =>
        "CREATE UNIQUE INDEX " + indexName + " ON " + table + " (" + columns + ") WHERE " + filter;

    private static void AddFilteredUniqueIndexIfMissing(
        LfDbContext ctx, bool isSqlite, string table, string indexName, string columns, string filter)
    {
        UpgraderIndexNames.TryAdd(indexName, 0);
        if (!TableExists(ctx, isSqlite, table)) return;

        if (IndexExists(ctx, isSqlite, table, indexName)) return;

        Log.Info("[SQL] schema 升級：{Table} 補部分唯一索引 {Index}", table, indexName);
        ctx.Database.ExecuteSqlRaw(BuildFilteredUniqueIndexSql(table, indexName, columns, filter));
    }

    private const string SqliteCreateWorkOrders = """
        CREATE TABLE lf_work_orders (
            work_order_id INTEGER NOT NULL CONSTRAINT PK_lf_work_orders PRIMARY KEY AUTOINCREMENT,
            source_name TEXT NULL,
            source_key TEXT NULL,
            event_id INTEGER NULL,
            issue_label TEXT NOT NULL,
            handler_id INTEGER NOT NULL,
            origin TEXT NOT NULL,
            scope_kind TEXT NOT NULL,
            scope_group_ids TEXT NOT NULL,
            auto_attach INTEGER NOT NULL,
            note TEXT NULL,
            due_date TEXT NULL,
            created_by_id INTEGER NULL,
            created_by_account TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_appended_at TEXT NULL,
            last_reply_at TEXT NULL,
            closed_at TEXT NULL,
            closed_reason TEXT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    internal const string SqlServerCreateWorkOrders = """
        CREATE TABLE lf_work_orders (
            work_order_id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_work_orders PRIMARY KEY,
            source_name nvarchar(255) NULL,
            source_key nvarchar(255) NULL,
            event_id int NULL,
            issue_label nvarchar(512) NOT NULL,
            handler_id bigint NOT NULL,
            origin nvarchar(30) NOT NULL,
            scope_kind nvarchar(30) NOT NULL,
            scope_group_ids nvarchar(max) NOT NULL,
            auto_attach bit NOT NULL,
            note nvarchar(1000) NULL,
            due_date datetime2 NULL,
            created_by_id bigint NULL,
            created_by_account nvarchar(255) NOT NULL,
            created_at datetime2 NOT NULL,
            last_appended_at datetime2 NULL,
            last_reply_at datetime2 NULL,
            closed_at datetime2 NULL,
            closed_reason nvarchar(30) NULL,
            updated_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreateWorkOrderEvents = """
        CREATE TABLE lf_work_order_events (
            event_id INTEGER NOT NULL CONSTRAINT PK_lf_work_order_events PRIMARY KEY AUTOINCREMENT,
            work_order_id INTEGER NOT NULL,
            action TEXT NOT NULL,
            actor_id INTEGER NULL,
            actor_account TEXT NOT NULL,
            member_delta INTEGER NOT NULL,
            note TEXT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateWorkOrderEvents = """
        CREATE TABLE lf_work_order_events (
            event_id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_work_order_events PRIMARY KEY,
            work_order_id bigint NOT NULL,
            action nvarchar(30) NOT NULL,
            actor_id bigint NULL,
            actor_account nvarchar(255) NOT NULL,
            member_delta int NOT NULL,
            note nvarchar(1000) NULL,
            created_at datetime2 NOT NULL
        )
        """;



    internal const string IssueFirstSeenWatermarkBlobKey = "issue_first_seen_watermark";
    internal const string IssueFirstSeenFullDoneBlobKey = "issue_first_seen_full_done";
    internal const string IssueFirstSeenSourceKeyRekeyDoneBlobKey = "issue_first_seen_source_key_rekey_done";
    private const string IssueFirstSeenSourceKeyRekeyVersion = "1";

    /// <summary>
    /// 將來源鍵回填前以 SQL <c>UPPER(source_name)</c> 寫入的首見日列，安全地
    /// 合併到 <c>lf_top_issues.source_key</c> 的正規鍵。只接受能由現有 top issue
    /// 列證明的對應；沒有 top issue 的舊列保留原樣。
    /// </summary>
    internal static int RekeyIssueFirstSeenToSourceKeys(Func<LfDbContext> contextFactory)
    {
        IExecutionStrategy strategy;
        using (var probe = contextFactory())
        {
            // 成功標記是 durable gate：正常 hosted service 重啟只做這個索引欄位查詢，
            // 不開交易、不載入 top_issues 或 issue_first_seen。
            if (probe.Blobs.AsNoTracking().Any(x =>
                    x.BlobKey == IssueFirstSeenSourceKeyRekeyDoneBlobKey &&
                    x.Content == IssueFirstSeenSourceKeyRekeyVersion))
            {
                return 0;
            }

            strategy = probe.Database.CreateExecutionStrategy();
        }

        return strategy.Execute(() =>
        {
            using var ctx = contextFactory();
            using var transaction = ctx.Database.BeginTransaction();

            var done = ctx.Blobs.FirstOrDefault(x =>
                x.BlobKey == IssueFirstSeenSourceKeyRekeyDoneBlobKey);
            if (done?.Content == IssueFirstSeenSourceKeyRekeyVersion)
            {
                transaction.Commit();
                return 0;
            }

            var topIssues = LoadDistinctSourceKeyTopIssues(ctx);
            var mappings = BuildIssueFirstSeenSourceMappings(topIssues);
            var existing = ctx.IssueFirstSeen.ToList();
            var byKey = existing.ToDictionary(x => (x.SourceKey, x.EventId));
            var pendingAdds = new List<IssueFirstSeenRow>();
            var changed = 0;

            foreach (var row in existing)
            {
                if (!mappings.TryGetValue((row.SourceKey, row.EventId), out var mapping) ||
                    string.Equals(row.SourceKey, mapping.SourceKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var targetKey = (mapping.SourceKey, row.EventId);
                if (byKey.TryGetValue(targetKey, out var target))
                {
                    if (row.FirstSeen < target.FirstSeen) target.FirstSeen = row.FirstSeen;
                    target.SourceName = mapping.SourceName;
                    ctx.IssueFirstSeen.Remove(row);
                }
                else
                {
                    ctx.IssueFirstSeen.Remove(row);
                    target = new IssueFirstSeenRow
                    {
                        SourceKey = mapping.SourceKey,
                        EventId = row.EventId,
                        SourceName = mapping.SourceName,
                        FirstSeen = row.FirstSeen
                    };
                    pendingAdds.Add(target);
                    byKey.Add(targetKey, target);
                }

                changed++;
            }

            // 先落實刪除，再新增 canonical key。SQL Server 常見 CI collation 下，
            // old/new 來源鍵可能被視為同一個 PK；同一個 SaveChanges 內若先 INSERT
            // 會撞唯一鍵。兩步仍在同一 execution-strategy transaction 內，故不會留下半套結果。
            ctx.SaveChanges();
            foreach (var row in pendingAdds) ctx.IssueFirstSeen.Add(row);

            if (done == null)
            {
                ctx.Blobs.Add(new BlobRow
                {
                    BlobKey = IssueFirstSeenSourceKeyRekeyDoneBlobKey,
                    Content = IssueFirstSeenSourceKeyRekeyVersion,
                    UpdatedAt = DateTime.Now,
                    Version = 1
                });
            }
            else
            {
                done.Content = IssueFirstSeenSourceKeyRekeyVersion;
                done.UpdatedAt = DateTime.Now;
                done.Version++;
            }

            ctx.SaveChanges();
            transaction.Commit();
            if (changed > 0)
                Log.Info("[SQL] lf_issue_first_seen 來源鍵重鍵完成：合併 {Changed} 列", changed);
            return changed;
        });
    }

    private static List<IssueFirstSeenTopIssue> LoadDistinctSourceKeyTopIssues(LfDbContext ctx)
    {
        var query = ctx.TopIssues.AsNoTracking().Where(x => x.SourceKey != null);
        if (ctx.Database.IsSqlite())
        {
            return query
                .Select(x => new
                {
                    SourceName = EF.Functions.Collate(x.SourceName, "BINARY"),
                    SourceKey = x.SourceKey!,
                    x.EventId
                })
                .Distinct()
                .ToList()
                .Select(x => new IssueFirstSeenTopIssue(x.SourceName, x.SourceKey, x.EventId))
                .ToList();
        }

        return query
            .Select(x => new
            {
                SourceName = EF.Functions.Collate(x.SourceName, "Latin1_General_100_BIN2"),
                SourceKey = x.SourceKey!,
                x.EventId
            })
            .Distinct()
            .ToList()
            .Select(x => new IssueFirstSeenTopIssue(x.SourceName, x.SourceKey, x.EventId))
            .ToList();
    }

    private static Dictionary<(string SourceKey, int EventId), IssueFirstSeenSourceMapping>
        BuildIssueFirstSeenSourceMappings(IEnumerable<IssueFirstSeenTopIssue> topIssues)
    {
        var mappings = new Dictionary<(string SourceKey, int EventId), IssueFirstSeenSourceMapping>();
        var ambiguous = new HashSet<(string SourceKey, int EventId)>();

        foreach (var topIssue in topIssues)
        {
            var mapping = new IssueFirstSeenSourceMapping(topIssue.SourceKey, topIssue.SourceName);
            foreach (var alias in LegacySourceKeyAliases(topIssue.SourceName, topIssue.SourceKey))
            {
                var key = (alias, (int)topIssue.EventId);
                if (ambiguous.Contains(key)) continue;

                if (!mappings.TryGetValue(key, out var previous))
                {
                    mappings.Add(key, mapping);
                    continue;
                }

                if (!string.Equals(previous.SourceKey, mapping.SourceKey, StringComparison.Ordinal))
                {
                    mappings.Remove(key);
                    ambiguous.Add(key);
                }
                else if (string.CompareOrdinal(mapping.SourceName, previous.SourceName) < 0)
                {
                    mappings[key] = mapping;
                }
            }
        }

        return mappings;
    }

    private static IEnumerable<string> LegacySourceKeyAliases(string sourceName, string sourceKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in new[]
        {
            sourceName,
            sourceName.ToUpperInvariant(),
            sourceName.ToUpper(),
            AsciiUpper(sourceName),
            sourceKey
        })
        {
            if (seen.Add(alias)) yield return alias;
        }
    }

    private static string AsciiUpper(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z') chars[i] = (char)(chars[i] - ('a' - 'A'));
        }
        return new string(chars);
    }

    private readonly record struct IssueFirstSeenSourceMapping(string SourceKey, string SourceName);
    private readonly record struct IssueFirstSeenTopIssue(string SourceName, string SourceKey, int EventId);

    internal static IssueFirstSeenSeedMergeOutcome MergeIssueFirstSeenSeed(LfDbContext ctx, bool force = false)
    {
        // 依分析等級設置 300 秒逾時，避免大表掃描因 60 秒前景逾時而失敗
        ctx.Database.SetCommandTimeout(StorageBackend.AnalysisCommandTimeoutSeconds);

        // 便宜閘門：讀取目前 lf_top_issues 最大 record_id 與既有浮水印比較（毫秒級單一查詢）
        var currentMaxRecordId = ctx.TopIssues.Max(t => (long?)t.RecordId) ?? 0;
        var watermarkRow = ctx.Blobs.FirstOrDefault(b => b.BlobKey == IssueFirstSeenWatermarkBlobKey);

        long watermark = 0;
        var hasValidWatermark = watermarkRow != null && long.TryParse(watermarkRow.Content, out watermark);

        if (!force && hasValidWatermark && watermark == currentMaxRecordId)
        {
            Log.Info("[SQL] lf_issue_first_seen 浮水印相同（{Watermark}），跳過機房首見日合併", watermark);
            return IssueFirstSeenSeedMergeOutcome.Skipped;
        }

        // force＝完整重算：浮水印視為 0（全部列都當新列掃）、全掃修正段也不看初次回補旗標。
        // 保留期清理刪列／重新分析舊日期這兩種情況需要它（見 BACKLOG「首見日的完整重算入口」）。
        if (force) watermark = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 檢查是否已完成初次回補（UPDATE 段）
        var fullDoneRow = ctx.Blobs.FirstOrDefault(b => b.BlobKey == IssueFirstSeenFullDoneBlobKey);

        // **兩段 SQL 刻意避開「在 HAVING 裡引用外層欄位或未分組的欄位」**：那種寫法在 Sqlite 上
        // 能跑，在 SqlServer 上卻可能因為分組規則較嚴而失敗——而測試只跑得到 Sqlite，
        // 這種差異會是「測試全綠、正式環境炸」。改用衍生資料表與純量子查詢，兩個 provider
        // 的語意都沒有模糊空間。

        // 補缺：lf_top_issues 有、lf_issue_first_seen 沒有的組合，補上歷史最小 record_date
        // （增量處理：只掃 record_id > watermark 的新列；浮水印不存在時 watermark 為 0，等同全掃）
        var inserted = ctx.Database.ExecuteSqlRaw("""
            INSERT INTO lf_issue_first_seen (source_key, event_id, source_name, first_seen)
            SELECT src.source_key, src.event_id, src.source_name, src.first_seen
            FROM (
                SELECT COALESCE(source_key, UPPER(source_name)) AS source_key,
                       event_id           AS event_id,
                       MIN(source_name)   AS source_name,
                       MIN(record_date)   AS first_seen
                FROM lf_top_issues
                WHERE record_date >= '2000-01-01'
                  AND record_id > {0}
                GROUP BY COALESCE(source_key, UPPER(source_name)), event_id
            ) src
            WHERE NOT EXISTS (
                SELECT 1 FROM lf_issue_first_seen fs
                WHERE fs.source_key = src.source_key AND fs.event_id = src.event_id
            )
            """, watermark);

        // 增量修正：回補會替既有組合寫入「日期更早」的新列（回望窗口最多 30 天），
        // 這些列必須讓 first_seen 往前移。只掃 record_id > watermark 的新列，成本與新資料量成正比。
        var incrementalUpdated = ctx.Database.ExecuteSqlRaw("""
            UPDATE lf_issue_first_seen
            SET first_seen = (
                SELECT MIN(t.record_date)
                FROM lf_top_issues t
                WHERE COALESCE(t.source_key, UPPER(t.source_name)) = lf_issue_first_seen.source_key
                  AND t.event_id = lf_issue_first_seen.event_id
                  AND t.record_date >= '2000-01-01'
                  AND t.record_id > {0}
            )
            WHERE (
                SELECT MIN(t2.record_date)
                FROM lf_top_issues t2
                WHERE COALESCE(t2.source_key, UPPER(t2.source_name)) = lf_issue_first_seen.source_key
                  AND t2.event_id = lf_issue_first_seen.event_id
                  AND t2.record_date >= '2000-01-01'
                  AND t2.record_id > {0}
            ) < lf_issue_first_seen.first_seen
            """, watermark);

        // 修正：兩邊都有、但歷史最小日期早於現存 first_seen 的，更新成較早的那個。
        // 純量子查詢在 WHERE 直接與 first_seen 比較——NULL（該問題已無歷史列）不成立，
        // 那一列自然不會被更新，不必另外處理。
        // （只在「初次回補」執行一次，成功後寫入旗標；旗標已存在則整段跳過）
        var updated = incrementalUpdated;
        if (force || fullDoneRow == null)
        {
            updated += ctx.Database.ExecuteSqlRaw("""
                UPDATE lf_issue_first_seen
                SET first_seen = (
                    SELECT MIN(t.record_date)
                    FROM lf_top_issues t
                    WHERE COALESCE(t.source_key, UPPER(t.source_name)) = lf_issue_first_seen.source_key
                      AND t.event_id = lf_issue_first_seen.event_id
                      AND t.record_date >= '2000-01-01'
                )
                WHERE (
                    SELECT MIN(t2.record_date)
                    FROM lf_top_issues t2
                    WHERE COALESCE(t2.source_key, UPPER(t2.source_name)) = lf_issue_first_seen.source_key
                      AND t2.event_id = lf_issue_first_seen.event_id
                      AND t2.record_date >= '2000-01-01'
                ) < lf_issue_first_seen.first_seen
                """);

            if (fullDoneRow == null)
            {
                ctx.Blobs.Add(new BlobRow
                {
                    BlobKey = IssueFirstSeenFullDoneBlobKey,
                    Content = "true",
                    UpdatedAt = DateTime.Now,
                    Version = 1
                });
            }
        }

        // 更新浮水印至 lf_blobs
        if (watermarkRow == null)
        {
            ctx.Blobs.Add(new BlobRow
            {
                BlobKey = IssueFirstSeenWatermarkBlobKey,
                Content = currentMaxRecordId.ToString(),
                UpdatedAt = DateTime.Now,
                Version = 1
            });
        }
        else
        {
            watermarkRow.Content = currentMaxRecordId.ToString();
            watermarkRow.UpdatedAt = DateTime.Now;
            watermarkRow.Version++;
        }

        // 若完成標記存在卻又有來源鍵尚未回填的 top issue，這次 seed 可能新增了
        // legacy UPPER(source_name) 首見日列；讓下一輪來源鍵回填重新檢查。正常 ready
        // 後的新列都有 source_key，因此不會因每次 seed 都重新掃描全表。
        var rekeyDone = ctx.Blobs.FirstOrDefault(b => b.BlobKey == IssueFirstSeenSourceKeyRekeyDoneBlobKey);
        if (rekeyDone?.Content == IssueFirstSeenSourceKeyRekeyVersion &&
            ctx.TopIssues.Any(t => t.SourceKey == null && t.RecordId > watermark))
        {
            ctx.Blobs.Remove(rekeyDone);
        }

        ctx.SaveChanges();

        Log.Info("[SQL] lf_issue_first_seen 機房首見日冪等合併完成（浮水印更新至 {Watermark}）：補缺 {Inserted} 列，修正 {Updated} 列，耗時 {Ms}ms",
            currentMaxRecordId, inserted, updated, sw.ElapsedMilliseconds);

        return IssueFirstSeenSeedMergeOutcome.Completed;
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
            source_key TEXT NULL,
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
            source_key nvarchar(255) NULL,
            event_id int NOT NULL,
            entry_type int NOT NULL,
            event_time datetime2 NOT NULL,
            message nvarchar(max) NOT NULL,
            rule_id nvarchar(64) NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreateIssueFirstSeen = """
        CREATE TABLE lf_issue_first_seen (
            source_key TEXT NOT NULL,
            event_id INTEGER NOT NULL,
            source_name TEXT NOT NULL,
            first_seen TEXT NOT NULL,
            CONSTRAINT PK_lf_issue_first_seen PRIMARY KEY (source_key, event_id)
        )
        """;

    private const string SqlServerCreateIssueFirstSeen = """
        CREATE TABLE lf_issue_first_seen (
            source_key nvarchar(255) NOT NULL,
            event_id int NOT NULL,
            source_name nvarchar(255) NOT NULL,
            first_seen datetime2 NOT NULL,
            CONSTRAINT PK_lf_issue_first_seen PRIMARY KEY (source_key, event_id)
        )
        """;

    private const string SqliteCreatePermissionChanges = """
        CREATE TABLE lf_permission_changes (
            id INTEGER NOT NULL CONSTRAINT PK_lf_permission_changes PRIMARY KEY AUTOINCREMENT,
            change_id TEXT NOT NULL,
            dedupe_key TEXT NOT NULL,
            host_name TEXT NOT NULL,
            host_name_key TEXT NOT NULL,
            detected_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            target TEXT NOT NULL,
            change_type TEXT NOT NULL,
            category TEXT NOT NULL,
            is_privileged_target INTEGER NOT NULL,
            initiator_account TEXT NULL,
            target_account TEXT NULL,
            before_value TEXT NOT NULL,
            after_value TEXT NOT NULL,
            alert_text TEXT NOT NULL,
            source TEXT NOT NULL,
            event_id INTEGER NULL,
            status TEXT NOT NULL,
            confirmed_by INTEGER NULL,
            confirmed_by_account TEXT NULL,
            confirmed_at TEXT NULL,
            confirm_note TEXT NULL
        )
        """;

    private const string SqlServerCreatePermissionChanges = """
        CREATE TABLE lf_permission_changes (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_permission_changes PRIMARY KEY,
            change_id nvarchar(64) NOT NULL,
            dedupe_key nvarchar(max) NOT NULL,
            host_name nvarchar(255) NOT NULL,
            host_name_key nvarchar(255) NOT NULL,
            detected_at datetime2 NOT NULL,
            created_at datetime2 NOT NULL,
            target nvarchar(max) NOT NULL,
            change_type nvarchar(64) NOT NULL,
            category nvarchar(64) NOT NULL,
            is_privileged_target bit NOT NULL,
            initiator_account nvarchar(255) NULL,
            target_account nvarchar(255) NULL,
            before_value nvarchar(max) NOT NULL,
            after_value nvarchar(max) NOT NULL,
            alert_text nvarchar(max) NOT NULL,
            source nvarchar(64) NOT NULL,
            event_id int NULL,
            status nvarchar(30) NOT NULL,
            confirmed_by bigint NULL,
            confirmed_by_account nvarchar(255) NULL,
            confirmed_at datetime2 NULL,
            confirm_note nvarchar(max) NULL
        )
        """;

    // 報告全文。content 不設長度上限：報告是人看的完整敘事，長度不可預期
    // （設上限在 SQLite（TEXT 無長度）測不出來，到 SQL Server 會變成寫入時的截斷例外）。
    // 主鍵在 SQLite 必須是 INTEGER（rowid），不能寫 bigint。
    private const string SqliteCreateReports = """
        CREATE TABLE lf_reports (
            report_id INTEGER NOT NULL CONSTRAINT PK_lf_reports PRIMARY KEY AUTOINCREMENT,
            host_id INTEGER NOT NULL,
            host_name TEXT NOT NULL,
            report_date TEXT NOT NULL,
            kind TEXT NOT NULL,
            risk_level TEXT NULL,
            categories TEXT NULL,
            file_name TEXT NOT NULL,
            content TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreateReports = """
        CREATE TABLE lf_reports (
            report_id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_reports PRIMARY KEY,
            host_id bigint NOT NULL,
            host_name nvarchar(255) NOT NULL,
            report_date datetime2 NOT NULL,
            kind nvarchar(20) NOT NULL,
            risk_level nvarchar(10) NULL,
            categories nvarchar(200) NULL,
            file_name nvarchar(255) NOT NULL,
            content nvarchar(max) NOT NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreatePrtgDevices = """
        CREATE TABLE lf_prtg_devices (
            objid INTEGER NOT NULL CONSTRAINT PK_lf_prtg_devices PRIMARY KEY,
            name TEXT NOT NULL,
            group_path TEXT NOT NULL,
            ip TEXT NULL,
            tags TEXT NULL,
            status TEXT NULL,
            dependency_objid INTEGER NULL,
            paused INTEGER NOT NULL,
            synced_at TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreatePrtgDevices = """
        CREATE TABLE lf_prtg_devices (
            objid bigint NOT NULL CONSTRAINT PK_lf_prtg_devices PRIMARY KEY,
            name nvarchar(255) NOT NULL,
            group_path nvarchar(512) NOT NULL,
            ip nvarchar(64) NULL,
            tags nvarchar(max) NULL,
            status nvarchar(64) NULL,
            dependency_objid bigint NULL,
            paused bit NOT NULL,
            synced_at datetime2 NOT NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreatePrtgSensors = """
        CREATE TABLE lf_prtg_sensors (
            objid INTEGER NOT NULL CONSTRAINT PK_lf_prtg_sensors PRIMARY KEY,
            device_objid INTEGER NOT NULL,
            name TEXT NOT NULL,
            sensor_type TEXT NOT NULL,
            tags TEXT NULL,
            unit TEXT NULL,
            status TEXT NULL,
            thresholds_json TEXT NULL,
            dependency_objid INTEGER NULL,
            paused INTEGER NOT NULL,
            category TEXT NULL,
            category_source TEXT NULL,
            synced_at TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreatePrtgSensors = """
        CREATE TABLE lf_prtg_sensors (
            objid bigint NOT NULL CONSTRAINT PK_lf_prtg_sensors PRIMARY KEY,
            device_objid bigint NOT NULL,
            name nvarchar(255) NOT NULL,
            sensor_type nvarchar(128) NOT NULL,
            tags nvarchar(max) NULL,
            unit nvarchar(64) NULL,
            status nvarchar(64) NULL,
            thresholds_json nvarchar(max) NULL,
            dependency_objid bigint NULL,
            paused bit NOT NULL,
            category nvarchar(64) NULL,
            category_source nvarchar(16) NULL,
            synced_at datetime2 NOT NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreatePrtgStateChanges = """
        CREATE TABLE lf_prtg_state_changes (
            id INTEGER NOT NULL CONSTRAINT PK_lf_prtg_state_changes PRIMARY KEY AUTOINCREMENT,
            sensor_objid INTEGER NOT NULL,
            changed_at TEXT NOT NULL,
            status TEXT NOT NULL,
            prev_status TEXT NULL,
            message TEXT NULL,
            quality TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreatePrtgStateChanges = """
        CREATE TABLE lf_prtg_state_changes (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_prtg_state_changes PRIMARY KEY,
            sensor_objid bigint NOT NULL,
            changed_at datetime2 NOT NULL,
            status nvarchar(64) NOT NULL,
            prev_status nvarchar(64) NULL,
            message nvarchar(max) NULL,
            quality nvarchar(16) NOT NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreatePrtgValues = """
        CREATE TABLE lf_prtg_values (
            id INTEGER NOT NULL CONSTRAINT PK_lf_prtg_values PRIMARY KEY AUTOINCREMENT,
            sensor_objid INTEGER NOT NULL,
            period_start TEXT NOT NULL,
            avg_value REAL NULL,
            min_value REAL NULL,
            max_value REAL NULL,
            coverage REAL NULL,
            quality TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """;

    private const string SqlServerCreatePrtgValues = """
        CREATE TABLE lf_prtg_values (
            id bigint NOT NULL IDENTITY(1,1) CONSTRAINT PK_lf_prtg_values PRIMARY KEY,
            sensor_objid bigint NOT NULL,
            period_start datetime2 NOT NULL,
            avg_value float NULL,
            min_value float NULL,
            max_value float NULL,
            coverage float NULL,
            quality nvarchar(16) NOT NULL,
            created_at datetime2 NOT NULL
        )
        """;

    private const string SqliteCreatePrtgHostMap = """
        CREATE TABLE lf_prtg_host_map (
            map_date TEXT NOT NULL,
            device_objid INTEGER NOT NULL,
            ip TEXT NULL,
            host_id INTEGER NULL,
            host_name TEXT NULL,
            map_status TEXT NOT NULL,
            note TEXT NULL,
            created_at TEXT NOT NULL,
            CONSTRAINT PK_lf_prtg_host_map PRIMARY KEY (map_date, device_objid)
        )
        """;

    private const string SqlServerCreatePrtgHostMap = """
        CREATE TABLE lf_prtg_host_map (
            map_date datetime2 NOT NULL,
            device_objid bigint NOT NULL,
            ip nvarchar(64) NULL,
            host_id bigint NULL,
            host_name nvarchar(255) NULL,
            map_status nvarchar(16) NOT NULL,
            note nvarchar(512) NULL,
            created_at datetime2 NOT NULL,
            CONSTRAINT PK_lf_prtg_host_map PRIMARY KEY (map_date, device_objid)
        )
        """;

    private const string SqliteCreatePrtgManualMap = """
        CREATE TABLE lf_prtg_manual_map (
            device_objid INTEGER NOT NULL,
            host_id INTEGER NOT NULL,
            created_by TEXT NULL,
            note TEXT NULL,
            created_at TEXT NOT NULL,
            CONSTRAINT PK_lf_prtg_manual_map PRIMARY KEY (device_objid)
        )
        """;

    private const string SqlServerCreatePrtgManualMap = """
        CREATE TABLE lf_prtg_manual_map (
            device_objid bigint NOT NULL,
            host_id bigint NOT NULL,
            created_by nvarchar(64) NULL,
            note nvarchar(512) NULL,
            created_at datetime2 NOT NULL,
            CONSTRAINT PK_lf_prtg_manual_map PRIMARY KEY (device_objid)
        )
        """;

    private const string SqliteCreatePrtgIpExcludes = """
        CREATE TABLE lf_prtg_ip_excludes (
            ip TEXT NOT NULL,
            note TEXT NULL,
            created_by TEXT NULL,
            created_at TEXT NOT NULL,
            CONSTRAINT PK_lf_prtg_ip_excludes PRIMARY KEY (ip)
        )
        """;

    private const string SqlServerCreatePrtgIpExcludes = """
        CREATE TABLE lf_prtg_ip_excludes (
            ip nvarchar(64) NOT NULL,
            note nvarchar(512) NULL,
            created_by nvarchar(64) NULL,
            created_at datetime2 NOT NULL,
            CONSTRAINT PK_lf_prtg_ip_excludes PRIMARY KEY (ip)
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
        UpgraderIndexNames.TryAdd(indexName, 0);
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

/// <summary>首見日合併的執行結果</summary>
public enum IssueFirstSeenSeedMergeOutcome
{
    Completed,
    Skipped
}
