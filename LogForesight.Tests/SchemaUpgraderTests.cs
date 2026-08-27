using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P0-1／docs/DB-SPEC.md 定案 13：自製冪等 DDL 升級（不用 EF Core Migrations）。
///
/// 核心驗收：模擬「既有 DB 是升級前建的舊 schema（lf_log_lines 沒有 created_at 欄）」，
/// 驗證 <see cref="SchemaUpgrader.Upgrade"/> 補上欄位與索引後可正常讀寫；
/// 以及在已是新 schema 的 DB 上重複執行仍是 no-op、不拋例外（冪等性）。
/// </summary>
public class SchemaUpgraderTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SchemaUpgraderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// 建立升級前的最小舊 schema：兩張表都刻意不含本輪要補的新欄位
    /// （lf_log_lines 缺 created_at、lf_daily_records 缺 has_correlation）。
    /// </summary>
    private void CreateOldSchema()
    {
        using var raw = _connection.CreateCommand();
        raw.CommandText = "CREATE TABLE lf_log_lines (seq INTEGER PRIMARY KEY AUTOINCREMENT, log_key TEXT, line TEXT)";
        raw.ExecuteNonQuery();
        // created_at/weekly_checkup_date 是既有欄位（Phase C 就有），只有 has_correlation 是本輪新增——
        // 前兩者不能漏，否則 EF 對 DailyRecordRow 的查詢會因為模型與這張手刻的「假舊表」不符而報錯，
        // 那會是測試假設本身的錯誤，不是 SchemaUpgrader 的問題
        raw.CommandText = "CREATE TABLE lf_daily_records (record_id INTEGER PRIMARY KEY AUTOINCREMENT, host_id INTEGER, host_name TEXT, record_date TEXT, risk_level TEXT, weekly_checkup_date TEXT, content_json TEXT, created_at TEXT)";
        raw.ExecuteNonQuery();
        raw.CommandText = "CREATE TABLE lf_blobs (blob_key TEXT PRIMARY KEY, content TEXT, updated_at TEXT)";
        raw.ExecuteNonQuery();
    }

    [Fact]
    public void 舊schema缺created_at欄_升級後補上且可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.LogLines.Add(new LogLineRow { LogKey = "test", Line = "hello", CreatedAt = new DateTime(2026, 1, 1) });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.LogLines.Single();
            Assert.Equal("hello", row.Line);
            Assert.Equal(new DateTime(2026, 1, 1), row.CreatedAt);
        }
    }

    /// <summary>索引確實建立（不是只補了欄位卻漏了索引）</summary>
    [Fact]
    public void 舊schema升級後_索引存在()
    {
        CreateOldSchema();

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var indexNames = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='lf_log_lines'").ToList();

        Assert.Contains("IX_lf_log_lines_log_key_created_at", indexNames);
    }

    /// <summary>既有升級前寫入、沒有時間戳記的列不受影響——只是多了一欄 NULL</summary>
    [Fact]
    public void 舊schema既存列_升級後CreatedAt為null()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            raw.CommandText = "INSERT INTO lf_log_lines (log_key, line) VALUES ('test', 'existing-line')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.LogLines.Single();
        Assert.Equal("existing-line", row.Line);
        Assert.Null(row.CreatedAt);
    }

    /// <summary>舊 schema 缺 has_correlation 欄——升級後補上，既存列預設為 false（未知即保守值）</summary>
    [Fact]
    public void 舊schema缺has_correlation欄_升級後補上且既存列預設false()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            // created_at 是既有欄位、非 nullable，測試列必須帶值（不是本輪要驗證的重點，但省略會讓
            // EF 讀出 NULL 塞進非 nullable 的 DateTime 屬性而拋例外，與這則測試想驗證的事無關）
            raw.CommandText = "INSERT INTO lf_daily_records (host_id, host_name, record_date, risk_level, content_json, created_at) " +
                               "VALUES (1, 'HOST-A', '2026-01-01', '高', '{}', '2026-01-01')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.DailyRecords.Single();
        Assert.False(row.HasCorrelation);
    }

    /// <summary>升級後可正常讀寫 has_correlation（不是只補了欄位卻寫不進去）</summary>
    [Fact]
    public void 舊schema升級後_has_correlation可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = DateTime.Today,
                RiskLevel = "高", HasCorrelation = true, ContentJson = "{}"
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            Assert.True(ctx.DailyRecords.Single().HasCorrelation);
        }
    }

    /// <summary>新建 DB（EnsureCreated 已含最新 schema）上執行升級是 no-op，且可重複執行不拋例外</summary>
    [Fact]
    public void 新schema上執行升級_不拋例外且冪等()
    {
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();

        SchemaUpgrader.Upgrade(ctx);
        SchemaUpgrader.Upgrade(ctx);   // 再跑一次：冪等,不應重複建欄位/索引而出錯
    }

    // ── lf_blobs 快取版本欄位 ───────────────────────────────────────────────

    /// <summary>
    /// 舊 schema 缺 version 欄——升級後補上且可讀寫，既存列預設為 0
    /// 理由：SQLite 測試平常走 EnsureCreated，SchemaUpgrader 在測試裡從來沒被執行到。
    /// 沒有這組測試，寫錯了會是「新環境全綠、只有正式環境炸」。
    /// </summary>
    [Fact]
    public void 舊schema缺version欄_升級後補上且可讀寫()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            raw.CommandText = "INSERT INTO lf_blobs (blob_key, content, updated_at) VALUES ('hosts', '{}', '2026-01-01')";
            raw.ExecuteNonQuery();
        }

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            var row = ctx.Blobs.Single();
            Assert.Equal("hosts", row.BlobKey);
            Assert.Equal(0, row.Version); // 預設值為 0

            ctx.Blobs.Add(new BlobRow { BlobKey = "new_store", Content = "[]", Version = 42 });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            Assert.Equal(42, ctx.Blobs.Single(b => b.BlobKey == "new_store").Version); // 驗證升級後可正常讀寫
        }
    }

    // ── 回饋十九輪批次B：lf_daily_records 抽出欄／lf_issue_first_seen ──────────────

    /// <summary>舊 schema 缺批次B 的抽出欄——升級後補上，既存列的 extract_version 預設 0
    /// （尚未回填，DailyRecordBackfiller 依此判定候選）</summary>
    [Fact]
    public void 舊schema缺批次B抽出欄_升級後補上且既存列extract_version為0()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            raw.CommandText = "INSERT INTO lf_daily_records (host_id, host_name, record_date, risk_level, content_json, created_at) " +
                               "VALUES (1, 'HOST-A', '2026-01-01', '高', '{}', '2026-01-01')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.DailyRecords.Single();
        Assert.Equal(0, row.ExtractVersion);
        Assert.Equal(string.Empty, row.Headline);
        Assert.Equal(0, row.ErrorCount);
    }

    /// <summary>升級後可正常讀寫批次B 的抽出欄（不是只補了欄位卻寫不進去）</summary>
    [Fact]
    public void 舊schema升級後_批次B抽出欄可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = DateTime.Today, RiskLevel = "高",
                ContentJson = "{}", Headline = "測試標題", ErrorCount = 3, AiAnalyzed = true, ExtractVersion = 1
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.DailyRecords.Single();
            Assert.Equal("測試標題", row.Headline);
            Assert.Equal(3, row.ErrorCount);
            Assert.True(row.AiAnalyzed);
            Assert.Equal(1, row.ExtractVersion);
        }
    }

    /// <summary>lf_issue_first_seen 是全新的表（不掛在既有表升級之下），連線上完全沒有任何表時也要建得起來</summary>
    [Fact]
    public void 空連線升級後_lf_issue_first_seen表存在且可讀寫()
    {
        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                { SourceKey = "DISK", EventId = 153, SourceName = "disk", FirstSeen = new DateTime(2026, 1, 1) });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.IssueFirstSeen.Single();
            Assert.Equal("disk", row.SourceName);
            Assert.Equal(new DateTime(2026, 1, 1), row.FirstSeen);
        }
    }

    /// <summary>lf_permission_changes 是全新的表，連線上完全沒有任何表時也要由 SchemaUpgrader 建得起來且可讀寫</summary>
    [Fact]
    public void 空連線升級後_lf_permission_changes表存在且可讀寫()
    {
        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        var now = new DateTime(2026, 8, 20, 10, 0, 0);
        using (var ctx = NewContext())
        {
            ctx.PermissionChanges.Add(new PermissionChangeRow
            {
                ChangeId = "test-change-id-1",
                DedupeKey = "HOST-01|12345678|4728|alert",
                HostName = "HOST-01",
                HostNameKey = "HOST-01",
                DetectedAt = now,
                CreatedAt = now,
                Target = "本機 Administrators 群組",
                ChangeType = "成員新增",
                Category = "group_member",
                IsPrivilegedTarget = true,
                InitiatorAccount = "admin",
                TargetAccount = "user1",
                BeforeValue = "（不在群組中）",
                AfterValue = "user1",
                AlertText = "成員新增告警",
                Source = "本機監控",
                EventId = 4728,
                Status = "pending",
                ConfirmedBy = 1,
                ConfirmedByAccount = "auditor",
                ConfirmedAt = now,
                ConfirmNote = "確認備註"
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.PermissionChanges.Single();
            Assert.Equal("test-change-id-1", row.ChangeId);
            Assert.Equal("group_member", row.Category);
            Assert.True(row.IsPrivilegedTarget);
            Assert.Equal("pending", row.Status);
            Assert.Equal("admin", row.InitiatorAccount);
            Assert.Equal("user1", row.TargetAccount);
            Assert.Equal("auditor", row.ConfirmedByAccount);
            Assert.Equal("確認備註", row.ConfirmNote);
        }
    }

    // ── lf_issue_first_seen 歷史種子冪等合併 ──────────

    /// <summary>
    /// 補缺：lf_top_issues 有、lf_issue_first_seen 沒有的組合會被補上。
    /// </summary>
    [Fact]
    public void 首見日合併_補缺_無則補上且取最早日期()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            var recordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 3, 5)));
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 1, 20)));   // 更早
            ctx.TopIssues.Add(TopIssue(recordId, "DCOM", 10016, new DateTime(2026, 2, 1)));
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        using (var ctx = NewContext())
        {
            Assert.Equal(2, ctx.IssueFirstSeen.Count());
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 20), disk.FirstSeen);
            var dcom = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DCOM" && f.EventId == 10016);
            Assert.Equal(new DateTime(2026, 2, 1), dcom.FirstSeen);
        }
    }

    /// <summary>
    /// 順序無關：先讓 lf_issue_first_seen 有一列「較晚」的日期（模擬逐日 upsert 搶先寫入），
    /// 再跑合併——那一列必須被修正成歷史最小日期。
    /// </summary>
    [Fact]
    public void 首見日合併_順序無關_已存在較晚日期會被修正成較早()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            var recordId = AddParentRecord(ctx);
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                { SourceKey = "DISK", EventId = 153, SourceName = "disk", FirstSeen = new DateTime(2026, 5, 1) });
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 1, 20)));   // 更早的歷史
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        using (var ctx = NewContext())
        {
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 20), disk.FirstSeen);   // 被修正
        }
    }

    /// <summary>
    /// 哨兵列排除：record_date 為 MinValue 的列不會讓首見日變成西元 1 年。
    /// </summary>
    [Fact]
    public void 首見日合併_哨兵列排除_MinValue不會變成首見日()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            var recordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, DateTime.MinValue));           // 未回填哨兵
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 3, 5)));
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        using (var ctx = NewContext())
        {
            var disk = ctx.IssueFirstSeen.Single();
            Assert.Equal(new DateTime(2026, 3, 5), disk.FirstSeen);
        }
    }

    /// <summary>
    /// 合併是冪等的：連續執行兩次，第二次不改變任何資料。
    /// </summary>
    [Fact]
    public void 首見日合併_合併是冪等的()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            var recordId = AddParentRecord(ctx);
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                { SourceKey = "DISK", EventId = 153, SourceName = "disk", FirstSeen = new DateTime(2026, 5, 1) });
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 1, 20)));
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            SchemaUpgrader.MergeIssueFirstSeenSeed(ctx); // 連續執行兩次
        }

        using (var ctx = NewContext())
        {
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 20), disk.FirstSeen);
        }
    }

    /// <summary>
    /// 便宜閘門：浮水印與目前最大值相同時，合併整段被跳過，證明底層 SQL 沒有執行。
    /// </summary>
    [Fact]
    public void 首見日合併_浮水印相同時_跳過合併不執行SQL()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            var recordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 1, 20)));
            ctx.SaveChanges();
        }

        IssueFirstSeenSeedMergeOutcome firstOutcome;
        using (var ctx = NewContext())
        {
            firstOutcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, firstOutcome);

        // 第二次在資料未變更時執行，應被閘門跳過且 SQL 執行次數不增加
        IssueFirstSeenSeedMergeOutcome secondOutcome;
        using (var ctx = NewContext())
        {
            secondOutcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        Assert.Equal(IssueFirstSeenSeedMergeOutcome.Skipped, secondOutcome);
    }

    /// <summary>
    /// 浮水印更新：lf_top_issues 有新資料使浮水印落後時，合併會執行並更新浮水印。
    /// </summary>
    [Fact]
    public void 首見日合併_有新資料使浮水印落後時_執行合併並更新浮水印()
    {
        long firstRecordId;
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            firstRecordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(firstRecordId, "disk", 153, new DateTime(2026, 3, 1)));
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);
            var wmRow = ctx.Blobs.Single(b => b.BlobKey == SchemaUpgrader.IssueFirstSeenWatermarkBlobKey);
            Assert.Equal(firstRecordId.ToString(), wmRow.Content);
        }

        // 新增一筆 recordId 更大的父列與問題
        long secondRecordId;
        using (var ctx = NewContext())
        {
            secondRecordId = AddParentRecord(ctx);
            Assert.True(secondRecordId > firstRecordId);
            ctx.TopIssues.Add(TopIssue(secondRecordId, "disk", 153, new DateTime(2026, 1, 15))); // 更早的首見日
            ctx.TopIssues.Add(TopIssue(secondRecordId, "network", 404, new DateTime(2026, 2, 1)));
            ctx.SaveChanges();
        }

        IssueFirstSeenSeedMergeOutcome newOutcome;
        using (var ctx = NewContext())
        {
            newOutcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, newOutcome);

        using (var ctx = NewContext())
        {
            // 浮水印更新為第二筆 recordId
            var wmRow = ctx.Blobs.Single(b => b.BlobKey == SchemaUpgrader.IssueFirstSeenWatermarkBlobKey);
            Assert.Equal(secondRecordId.ToString(), wmRow.Content);

            // 且首見日正確更新與補缺——回補寫入的更早日期會被增量段修正
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 15), disk.FirstSeen);
            var net = ctx.IssueFirstSeen.Single(f => f.SourceKey == "NETWORK" && f.EventId == 404);
            Assert.Equal(new DateTime(2026, 2, 1), net.FirstSeen);
        }
    }

    /// <summary>
    /// 首次執行：INSERT 與 UPDATE 兩段都跑，首見日結果正確，旗標與浮水印被寫入。
    /// </summary>
    [Fact]
    public void 首見日合併_首次執行_INSERT與UPDATE皆執行且寫入旗標()
    {
        long recordId;
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);

            // 預先在 lf_issue_first_seen 塞入一筆較晚的首見日，驗證初次回補 UPDATE 段確實執行
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                { SourceKey = "DISK", EventId = 153, SourceName = "disk", FirstSeen = new DateTime(2026, 5, 1) });

            recordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(recordId, "disk", 153, new DateTime(2026, 1, 20)));    // 歷史更早，應被 UPDATE 修正
            ctx.TopIssues.Add(TopIssue(recordId, "network", 404, new DateTime(2026, 2, 10))); // 全新組合，應被 INSERT 補入
            ctx.SaveChanges();
        }

        IssueFirstSeenSeedMergeOutcome outcome;
        using (var ctx = NewContext())
        {
            outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
        }

        Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);

        using (var ctx = NewContext())
        {
            // 驗證旗標已寫入
            var fullDone = ctx.Blobs.SingleOrDefault(b => b.BlobKey == SchemaUpgrader.IssueFirstSeenFullDoneBlobKey);
            Assert.NotNull(fullDone);
            Assert.Equal("true", fullDone.Content);

            // 驗證浮水印已更新
            var wm = ctx.Blobs.SingleOrDefault(b => b.BlobKey == SchemaUpgrader.IssueFirstSeenWatermarkBlobKey);
            Assert.NotNull(wm);
            Assert.Equal(recordId.ToString(), wm.Content);

            // 驗證 UPDATE 與 INSERT 皆生效
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 20), disk.FirstSeen); // 被 UPDATE 修正為較早歷史日期

            var net = ctx.IssueFirstSeen.Single(f => f.SourceKey == "NETWORK" && f.EventId == 404);
            Assert.Equal(new DateTime(2026, 2, 10), net.FirstSeen); // 被 INSERT 補入
        }
    }

    /// <summary>
    /// 第二次執行（已有旗標、且有新增列）：只有新的 (source, event) 組合被補進去，既有組合的首見日不變。
    /// </summary>
    [Fact]
    public void 首見日合併_第二次執行已有旗標且有新增列_僅掃新列_補入新組合並修正回補的更早首見日()
    {
        long firstRecordId;
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            firstRecordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(firstRecordId, "disk", 153, new DateTime(2026, 3, 1)));
            ctx.SaveChanges();
        }

        // 首次執行，建立浮水印與旗標
        using (var ctx = NewContext())
        {
            var outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);
        }

        // 新增列：包含既有問題（帶不同日期）與全新問題組合
        long secondRecordId;
        using (var ctx = NewContext())
        {
            secondRecordId = AddParentRecord(ctx);
            Assert.True(secondRecordId > firstRecordId);
            ctx.TopIssues.Add(TopIssue(secondRecordId, "disk", 153, new DateTime(2026, 1, 10)));
            ctx.TopIssues.Add(TopIssue(secondRecordId, "memory", 200, new DateTime(2026, 2, 15)));
            ctx.SaveChanges();
        }

        // 第二次執行
        using (var ctx = NewContext())
        {
            var outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);
        }

        using (var ctx = NewContext())
        {
            // 浮水印推進至第二筆 recordId
            var wm = ctx.Blobs.Single(b => b.BlobKey == SchemaUpgrader.IssueFirstSeenWatermarkBlobKey);
            Assert.Equal(secondRecordId.ToString(), wm.Content);

            // 既有組合：新列日期更早（回補情境）時，增量段把首見日往前修正
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 1, 10), disk.FirstSeen);

            // 全新組合被補入
            var mem = ctx.IssueFirstSeen.Single(f => f.SourceKey == "MEMORY" && f.EventId == 200);
            Assert.Equal(new DateTime(2026, 2, 15), mem.FirstSeen);
        }
    }

    /// <summary>
    /// 第二次執行時 UPDATE 段不再執行：刻意將某筆既有首見日改成較晚日期，再跑一次合併，確認其未被修正。
    /// </summary>
    [Fact]
    public void 首見日合併_第二次執行時UPDATE段不再執行_既有首見日被篡改亦不被修正()
    {
        long firstRecordId;
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            firstRecordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(firstRecordId, "disk", 153, new DateTime(2026, 1, 20)));
            ctx.SaveChanges();
        }

        // 首次執行完成回補
        using (var ctx = NewContext())
        {
            var outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);
        }

        // 刻意將既有首見日改成較晚的錯誤日期，並新增一筆新紀錄讓浮水印落後以通過便宜閘門
        long secondRecordId;
        using (var ctx = NewContext())
        {
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            disk.FirstSeen = new DateTime(2026, 9, 30); // 刻意設為較晚日期

            secondRecordId = AddParentRecord(ctx);
            ctx.TopIssues.Add(TopIssue(secondRecordId, "network", 404, new DateTime(2026, 2, 1)));
            ctx.SaveChanges();
        }

        // 第二次執行
        using (var ctx = NewContext())
        {
            var outcome = SchemaUpgrader.MergeIssueFirstSeenSeed(ctx);
            Assert.Equal(IssueFirstSeenSeedMergeOutcome.Completed, outcome);
        }

        using (var ctx = NewContext())
        {
            // 驗證 disk 依然是 2026-09-30，證明 UPDATE 段未被執行（否則會被修正回 2026-01-20）
            var disk = ctx.IssueFirstSeen.Single(f => f.SourceKey == "DISK" && f.EventId == 153);
            Assert.Equal(new DateTime(2026, 9, 30), disk.FirstSeen);

            // 新組合 network 正確補入
            var net = ctx.IssueFirstSeen.Single(f => f.SourceKey == "NETWORK" && f.EventId == 404);
            Assert.Equal(new DateTime(2026, 2, 1), net.FirstSeen);
        }
    }

    /// <summary>lf_top_issues 有 FK 指向 lf_daily_records，測試子列前要先有父列</summary>
    private static long AddParentRecord(LfDbContext ctx)
    {
        var record = new DailyRecordRow
        {
            HostId = 1, HostName = "HOST-A", RecordDate = new DateTime(2026, 1, 1),
            RiskLevel = "低", ContentJson = "{}"
        };
        ctx.DailyRecords.Add(record);
        ctx.SaveChanges();
        return record.RecordId;
    }

    private static TopIssueRow TopIssue(long recordId, string source, int eventId, DateTime recordDate) => new()
    {
        RecordId = recordId, HostId = 1, RecordDate = recordDate, LogName = "System", SourceName = source,
        EventId = eventId, EntryType = 2, EventCount = 1, Category = "Storage",
        SeverityRank = (int)IssueSeverity.High, EventKey = ""
    };
}
