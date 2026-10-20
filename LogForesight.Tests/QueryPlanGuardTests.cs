using System.Data.Common;
using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 查詢計畫守門：在有資料、跑過 ANALYZE 的 SQLite 檔案資料庫上，攔截主要查詢實際送出的 SQL，
/// 做 EXPLAIN QUERY PLAN，確認主表不是「純 SCAN」（SEARCH 或 SCAN ... USING INDEX 皆可）。
/// 索引被改壞或新查詢漏索引時，這裡會先亮紅燈。
/// </summary>
public sealed class QueryPlanGuardTests : IClassFixture<QueryPlanGuardTests.PlanFixture>
{
    /// <summary>記錄 SELECT 語句與參數（名稱＋值），EXPLAIN 時要以同名參數重放</summary>
    internal sealed class SqlRecorder : DbCommandInterceptor
    {
        private readonly object _gate = new();
        public List<(string Sql, List<(string Name, object? Value)> Parameters)> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (_gate)
                Commands.Add((command.CommandText,
                    command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToList()));
            return base.ReaderExecuting(command, eventData, result);
        }

        public void Clear() { lock (_gate) Commands.Clear(); }

        public List<(string Sql, List<(string Name, object? Value)> Parameters)> Selects()
        {
            lock (_gate)
                return Commands.Where(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private static readonly Regex AliasPattern = new(
        @"(?:FROM|JOIN)\s+""(\w+)""\s+AS\s+""(\w+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HeadPattern = new(
        @"^((?:SCAN|SEARCH) )(\w+)", RegexOptions.Compiled);

    private static readonly DateTime Today = DateTime.Today;
    private const int HostCount = 60;
    private const int Days = 10;

    /// <summary>
    /// 日層級處理狀態筆數：太少的表，規劃器合理地選全表掃描（掃 100 列比 100 次 seek 便宜），
    /// 守門就失去意義；放大到足以讓索引有選擇性（每台每日至多一列，故以日期往前攤開）。
    /// </summary>
    private const int HandlingRows = 1500;

    /// <summary>種一次資料、整組測試共用（測試只讀不寫，重種 600 筆紀錄太慢）</summary>
    public sealed class PlanFixture : IDisposable
    {
        internal readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-plan-guard-" + Guid.NewGuid().ToString("N"));
        internal readonly string _dbPath;
        internal readonly SqlRecorder _recorder = new();
        internal readonly StorageBackend _backend;
        internal readonly EfAnalysisRecordStore _records;

        public PlanFixture()
        {
            Directory.CreateDirectory(_directory);
            _dbPath = Path.Combine(_directory, "plan.db");
            _backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite",
                ConnectionString = $"Data Source={_dbPath}"
            }, _directory, _recorder);
            _records = _backend.RecordStore();
            Seed();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        // ── 種資料（一律走各 store 的正式寫入 API）──────────────────────────────

        private void Seed()
        {
            var levels = new[] { RiskLevels.High, RiskLevels.Medium, RiskLevels.Low };
            var sources = new[] { "DiskSrc", "ServiceSrc", "SecuritySrc" };
            for (var h = 1; h <= HostCount; h++)
            for (var d = 0; d < Days; d++)
            {
                var n = h * Days + d;
                var issues = new List<LogIssueSignature>();
                for (var i = 0; i < 2 + n % 2; i++)
                    issues.Add(new LogIssueSignature
                    {
                        LogName = "System",
                        Source = sources[(n + i) % sources.Length],
                        EventId = 100 + (n + i) % 17,
                        EntryType = System.Diagnostics.EventLogEntryType.Error,
                        Count = 3 + i
                    });
                _records.Append(new DailyAnalysisRecord
                {
                    HostId = h,
                    Host = $"host-{h:D3}",
                    Date = Today.AddDays(-d),
                    RiskLevel = levels[n % 3],
                    Headline = "h",
                    Summary = "s",
                    ErrorCount = 5,
                    WarningCount = 2,
                    AiPending = n % 5 == 0,
                    TopIssues = issues
                });
            }

            var workOrders = _backend.WorkOrderStore();
            for (var i = 0; i < 20; i++)
            {
                var closed = i % 2 == 0;
                workOrders.Insert(new WorkOrder
                {
                    SourceName = "DiskSrc",
                    EventId = 100 + i,
                    IssueLabel = $"DiskSrc EventId {100 + i}",
                    HandlerId = 1 + i % 5,
                    CreatedByAccount = "admin",
                    CreatedAt = DateTime.Now.AddDays(-i),
                    ClosedAt = closed ? DateTime.Now.AddDays(-1) : null,
                    ClosedReason = closed ? WorkOrderCloseReasons.AllClosed : null
                });
            }

            var handlings = _backend.RecordHandlingStore();
            for (var i = 0; i < HandlingRows; i++)
                handlings.Save(new RecordHandling
                {
                    HostName = $"host-{1 + i % HostCount:D3}",
                    Date = Today.AddDays(-(i / HostCount)),
                    Status = i % 4 == 0 ? HandlingStatuses.Resolved : HandlingStatuses.Open,
                    HandlerId = 1 + i % 5,
                    DueDate = i % 3 == 0 ? Today.AddDays(-5) : Today.AddDays(5),
                    UpdatedAt = DateTime.Now
                });

            // 正式站台的 lf_log_lines 同時存放多種 log_key（稽核、處理歷程…），批次執行紀錄只是其中一支；
        // 只有單一 key 時 log_key 毫無選擇性，規劃器掃全表是對的，無法反映實際環境
        foreach (var key in new[] { "audit", "handling_log", "notify_log", "permission_log", "schedule_log" })
            _backend.LogStore(key).AppendLines(Enumerable.Range(0, 300).Select(i => $"{{\"n\":{i}}}").ToList());

        var batchRuns = NewBatchRunStore();
            for (var i = 0; i < 40; i++)
            {
                var started = DateTime.Now.AddHours(-i * 4);
                var run = new BatchRun
                {
                    HostName = $"host-{1 + i % HostCount:D3}",
                    StartedAt = started,
                    AppVersion = "1.0",
                    Args = "-",
                    Trigger = "schedule"
                };
                batchRuns.StartRun(run);
                run.FinishedAt = started.AddMinutes(3);
                run.ExitCode = 0;
                batchRuns.FinishRun(run);
            }

            // 種完一次 ANALYZE，讓規劃器拿到統計資料（與正式站台維護後的狀態一致）
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var analyze = connection.CreateCommand();
            analyze.CommandText = "ANALYZE";
            analyze.ExecuteNonQuery();
        }

        internal BatchRunStore NewBatchRunStore() => new(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"));
    }

    private readonly ITestOutputHelper _output;
    private readonly PlanFixture _fx;

    public QueryPlanGuardTests(PlanFixture fixture, ITestOutputHelper output)
    {
        _fx = fixture;
        _output = output;
    }

    private SqlRecorder _recorder => _fx._recorder;
    private string _dbPath => _fx._dbPath;
    private StorageBackend _backend => _fx._backend;
    private EfAnalysisRecordStore _records => _fx._records;
    private BatchRunStore NewBatchRunStore() => _fx.NewBatchRunStore();

    // ── 計畫輔助 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 清空攔截器、執行 query，對攔截到的每一道 SELECT 做 EXPLAIN QUERY PLAN，
    /// 回傳所有計畫列的 detail 文字（參數以同名參數重放）。
    /// </summary>
    private List<string> PlanOf(Action query)
    {
        _recorder.Clear();
        query();
        var selects = _recorder.Selects();
        Assert.NotEmpty(selects);

        var plan = new List<string>();
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        foreach (var (sql, parameters) in selects)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            // SQLite 的計畫以 EF 產生的別名回報（SCAN l），先依 SQL 的 FROM／JOIN ... AS 還原成資料表名
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AliasPattern.Matches(sql)) aliases[m.Groups[2].Value] = m.Groups[1].Value;
            while (reader.Read())
            {
                var detail = reader.GetString(3);
                var head = HeadPattern.Match(detail);
                if (head.Success && aliases.TryGetValue(head.Groups[2].Value, out var table))
                    detail = head.Groups[1].Value + table + detail[head.Length..];
                plan.Add(detail);
            }
        }

        foreach (var (sql, _) in selects)
            _output.WriteLine("SQL> " + Regex.Replace(sql, @"\s+", " "));
        _output.WriteLine(string.Join(Environment.NewLine, plan));
        return plan;
    }

    /// <summary>
    /// 指定資料表在計畫中不得出現純 SCAN（後面沒接 USING INDEX／USING COVERING INDEX）；SEARCH 一律允許。
    /// </summary>
    private static void AssertNoFullScan(IEnumerable<string> plan, params string[] tables)
    {
        var lines = plan.ToList();
        foreach (var table in tables)
        {
            var scan = new Regex($@"^SCAN {Regex.Escape(table)}\b", RegexOptions.IgnoreCase);
            var offenders = lines.Where(l => scan.IsMatch(l.Trim()) &&
                !l.Contains(" USING INDEX", StringComparison.OrdinalIgnoreCase) &&
                !l.Contains(" USING COVERING INDEX", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(offenders.Count == 0,
                $"{table} 出現純 SCAN：{string.Join(" | ", offenders)}；完整計畫：{string.Join(" | ", lines)}");
        }
    }

    private static RecordQueryFilter RecentHighMedium() => new()
    {
        From = Today.AddDays(-(Days - 1)),
        To = Today,
        RiskLevels = new[] { RiskLevels.High, RiskLevels.Medium }
    };

    // ── 八支查詢 ────────────────────────────────────────────────────────────

    [Fact]
    public void AnalysisRecordStore_QueryPage_走索引()
    {
        // QueryPage 內含：舊列探測、COUNT、分頁本體多道 SELECT，全部都要檢查（皆讀 lf_daily_records）
        var plan = PlanOf(() => _records.QueryPage(RecentHighMedium(), 1, 20));
        AssertNoFullScan(plan, "lf_daily_records");
    }

    [Fact]
    public void AnalysisRecordStore_QueryLightweight_走索引()
    {
        // 另有一道讀 lf_top_issues 的 SELECT（依 record_id 取問題），本案只斷言主表 lf_daily_records
        var plan = PlanOf(() => _records.QueryLightweight(RecentHighMedium()));
        AssertNoFullScan(plan, "lf_daily_records");
    }

    [Fact]
    public void AnalysisRecordStore_CountPendingAi_走索引()
    {
        var plan = PlanOf(() => _records.CountPendingAi());
        AssertNoFullScan(plan, "lf_daily_records");
    }

    [Fact]
    public void AnalysisRecordStore_QueryPendingAi_走索引()
    {
        // 另有一道讀 lf_top_issues 的 SELECT，本案只斷言主表 lf_daily_records
        var plan = PlanOf(() => _records.QueryPendingAi(50));
        AssertNoFullScan(plan, "lf_daily_records");
    }

    [Fact]
    public void WorkOrderStore_GetActiveByHandler_走索引()
    {
        var plan = PlanOf(() => _backend.WorkOrderStore().GetActiveByHandler(3));
        AssertNoFullScan(plan, "lf_work_orders");
    }

    [Fact]
    public void IssueAggregateQuery_AggregateByCategory_走索引()
    {
        var aggregates = _backend.IssueAggregateQuery(new HostStore(_backend.Blob("plan_hosts")));
        var severities = new HashSet<IssueSeverity>(Enum.GetValues<IssueSeverity>());
        var plan = PlanOf(() => aggregates.AggregateByCategory(
            IssueExclusion.None, Today.AddDays(-(Days - 1)), Today, null, severities));
        AssertNoFullScan(plan, "lf_top_issues");
    }

    [Fact]
    public void RecordHandlingStore_GetMany_走索引()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"host-{i:D3}").ToList();
        var plan = PlanOf(() => _backend.RecordHandlingStore().GetMany(names, Today.AddDays(-(Days - 1)), Today));
        AssertNoFullScan(plan, "lf_record_handling");
    }

    [Fact]
    public void BatchRunStore_GetRecentRuns_走索引()
    {
        // 只有一道 SELECT（ReadRunLinesFrom 讀 lf_log_lines）；store 建構時的續號探測在 PlanOf 範圍之外
        var store = NewBatchRunStore();
        var plan = PlanOf(() => store.GetRecentRuns(7, null));
        AssertNoFullScan(plan, "lf_log_lines");
    }
}
