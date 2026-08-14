using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LogForesight.Tests;

/// <summary>
/// 規模壓測的資料集（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P0）：在暫存目錄產生一個**檔案型** SQLite
/// 資料庫，內容為 N 台主機 × M 天的完整分析紀錄＋處理狀態＋案件＋授權設定。
///
/// **為什麼是檔案而不是 in-memory**：6000 台 × 90 天約 54 萬筆紀錄、GB 級的 ContentJson，
/// in-memory DB 會把整個資料集鎖在測試行程的堆積裡，量測到的就不是產品的成本而是產生器的成本。
///
/// **為什麼繞過 store 直接寫**：產生器要灌的是「已經存在的歷史」，不是在測寫入路徑。
/// 走 <c>IHostStore.Touch</c>／<c>SaveMany</c> 逐筆寫入 6000 台＝6000 次整份 blob 讀改寫，
/// 光產生資料就要跑幾十分鐘——那正是本規劃要修的問題（S2／N2），不該讓它先卡住量測本身。
/// 分析紀錄則走原生 ADO.NET 批次 INSERT，理由相同。
///
/// 產生的資料刻意包含三種「只有規模上來才會現形」的形狀：
///   1. **墓碑主機**（<see cref="ScaleProfile.TombstoneCount"/>）——沒有墓碑列時
///      <c>HostIdentityResolver.Expand</c> 幾乎不做事，O(N²) 的成本量不出來。
///   2. **重尾的問題分布**——前兩個簽章出現在九成以上的主機日（模擬 DCOM 10016 那種
///      「幾乎每台都有」的雜訊），其餘依冪次衰減。平均分布會讓「依問題」視角的
///      群組數與真實環境差一個量級。
///   3. **跨主機的進行中案件**——案件協調（<c>GetOpen</c>）是每次標記都會走的路徑。
/// </summary>
public sealed class ScaleDataSet : IDisposable
{
    private const int InsertBatchSize = 2000;

    private readonly string _rootDir;

    public ScaleProfile Profile { get; }
    public StorageBackend Backend { get; }
    public string DbPath { get; }

    /// <summary>全部主機（含墓碑列），依 HostId 遞增</summary>
    public List<WebHost> Hosts { get; }

    /// <summary>存活主機（Active 且非墓碑）——可見範圍與查詢的實際對象</summary>
    public List<WebHost> LivingHosts { get; }

    public IReadOnlyList<LogIssueSignature> Signatures { get; }

    /// <summary>資料集的實際產出量，供量測報告引用</summary>
    public ScaleDataSetStats Stats { get; private set; } = new();

    private ScaleDataSet(ScaleProfile profile, string rootDir, string dbPath, StorageBackend backend,
        List<WebHost> hosts, IReadOnlyList<LogIssueSignature> signatures)
    {
        Profile = profile;
        _rootDir = rootDir;
        DbPath = dbPath;
        Backend = backend;
        Hosts = hosts;
        LivingHosts = hosts.Where(h => h.Active && h.MergedInto == null).ToList();
        Signatures = signatures;
    }

    public static ScaleDataSet Generate(ScaleProfile profile, Action<string>? log = null)
    {
        log ??= _ => { };
        var sw = Stopwatch.StartNew();

        var rootDir = Path.Combine(Path.GetTempPath(), "lf-scale", $"{profile.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        var dbPath = Path.Combine(rootDir, "logforesight.db");

        // 走產品的 StorageBackend：schema 建立與 SchemaUpgrader 都與正式環境同一條路徑，
        // 量測到的才是產品真正的 schema，而不是測試自己捏的
        var backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" }, rootDir);

        var rnd = new Random(20260806);   // 固定種子：同一個 profile 每次產生的資料完全相同，量測才可比
        var signatures = BuildSignatures(profile, rnd);
        var hosts = BuildHosts(profile);

        var set = new ScaleDataSet(profile, rootDir, dbPath, backend, hosts, signatures);

        set.WriteBlob("hosts", hosts);
        set.WriteAuthorization(profile);
        log($"  主機與授權設定：{hosts.Count:N0} 台（含 {profile.TombstoneCount:N0} 個墓碑列）— {sw.ElapsedMilliseconds:N0} ms");

        sw.Restart();
        var recordStats = set.WriteRecords(profile, signatures, rnd);
        log($"  分析紀錄：{recordStats.RecordRows:N0} 列、問題子列 {recordStats.TopIssueRows:N0} 列、" +
            $"ContentJson 共 {recordStats.ContentJsonBytes / 1024 / 1024:N0} MB — {sw.ElapsedMilliseconds:N0} ms");

        sw.Restart();
        var handlingStats = set.WriteHandling(profile, signatures, rnd);
        log($"  處理狀態 blob：{handlingStats.HandlingRows:N0} 列 / {handlingStats.HandlingBlobBytes / 1024 / 1024:N0} MB；" +
            $"案件 blob：{handlingStats.CaseRows:N0} 列 / {handlingStats.CaseBlobBytes / 1024 / 1024:N0} MB — {sw.ElapsedMilliseconds:N0} ms");

        set.Stats = new ScaleDataSetStats
        {
            RecordRows = recordStats.RecordRows,
            TopIssueRows = recordStats.TopIssueRows,
            ContentJsonBytes = recordStats.ContentJsonBytes,
            HandlingRows = handlingStats.HandlingRows,
            HandlingBlobBytes = handlingStats.HandlingBlobBytes,
            CaseRows = handlingStats.CaseRows,
            CaseBlobBytes = handlingStats.CaseBlobBytes,
            DbFileBytes = new FileInfo(dbPath).Length
        };

        return set;
    }

    // ── 主機與授權 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 主機清單：前 <c>HostCount</c> 台為存活主機，最後 <c>TombstoneCount</c> 台是墓碑列
    /// （各自併入一台存活主機）。墓碑列不進可見範圍集合，但它們的歷史要隨目標主機一起看得到——
    /// 這正是 <c>HostIdentityResolver.Expand</c> 存在的理由，也是本規劃 N1 要量的成本。
    /// </summary>
    private static List<WebHost> BuildHosts(ScaleProfile profile)
    {
        var hosts = new List<WebHost>(profile.HostCount + profile.TombstoneCount);
        var createdAt = DateTime.Today.AddDays(-profile.Days - 30);

        for (var i = 0; i < profile.HostCount; i++)
        {
            hosts.Add(new WebHost
            {
                HostId = i + 1,
                HostName = $"SRV-{i + 1:D5}",
                Source = "netiq",
                Os = WebHost.OsWindows,
                Active = true,
                CreatedAt = createdAt,
                LastReportAt = DateTime.Today.AddHours(-6),
                // 每 100 台一個主機群組——2000~6000 台的組織通常有數十個群組
                GroupIds = new List<long> { i / 100 + 1 }
            });
        }

        for (var t = 0; t < profile.TombstoneCount; t++)
        {
            var targetId = t * 7 % profile.HostCount + 1;
            hosts.Add(new WebHost
            {
                HostId = profile.HostCount + t + 1,
                HostName = $"OLD-{t + 1:D5}",
                Source = "netiq",
                Os = WebHost.OsWindows,
                Active = false,
                MergedInto = targetId,
                CreatedAt = createdAt,
                GroupIds = new List<long>()
            });
        }

        return hosts;
    }

    /// <summary>使用者、部門群組、主機群組與授權矩陣：量少但每次查詢都會讀，不能省略</summary>
    private void WriteAuthorization(ScaleProfile profile)
    {
        var hostGroupCount = profile.HostCount / 100 + 1;

        WriteBlob("host_groups", Enumerable.Range(1, hostGroupCount)
            .Select(i => new HostGroup { GroupId = i, GroupName = $"機房-{i:D3}", Active = true }).ToList());

        // 部門群組：一個 admin（ViewAll）＋若干受限部門，讓「ViewAll」與「受限」兩條路徑都量得到
        WriteBlob("user_groups", new List<UserGroup>
        {
            new() { GroupId = 1, GroupName = "系統管理", Role = UserRole.Admin, Active = true },
            new() { GroupId = 2, GroupName = "維運一部", Role = UserRole.User, Active = true },
            new() { GroupId = 3, GroupName = "維運二部", Role = UserRole.User, Active = true }
        });

        WriteBlob("users", new List<WebUser>
        {
            new() { UserId = 1, Account = "scale-admin", DisplayName = "壓測管理者", Active = true, GroupIds = new List<long> { 1 } },
            new() { UserId = 2, Account = "scale-user", DisplayName = "壓測維運者", Active = true, GroupIds = new List<long> { 2 } }
        });

        // 維運一部看得到前三分之一的主機群組——受限使用者的可見範圍解析走的是另一條路徑
        WriteBlob("group_access", Enumerable.Range(1, Math.Max(1, hostGroupCount / 3))
            .Select(i => new GroupAccess { UserGroupId = 2, HostGroupId = i, GrantedAt = DateTime.Today }).ToList());
    }

    // ── 問題簽章 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 簽章池。前兩個固定為「幾乎每台都有」的低嚴重度雜訊（DCOM／SCM 的角色），
    /// 用來重現體檢 §10.1 的核心情境：**數量排序會讓最普遍的壓過最重要的**。
    /// 最後 5% 是高嚴重度＋ElevatesDayRisk 的稀有問題（disk 153 的角色）。
    /// </summary>
    private static IReadOnlyList<LogIssueSignature> BuildSignatures(ScaleProfile profile, Random rnd)
    {
        var list = new List<LogIssueSignature>(profile.DistinctIssues)
        {
            NewSignature("System", "DistributedCOM", 10016, IssueCategory.Config, IssueSeverity.Low, false),
            NewSignature("System", "Service Control Manager", 7000, IssueCategory.Service, IssueSeverity.Medium, false)
        };

        var rareFrom = (int)(profile.DistinctIssues * 0.95);
        for (var i = list.Count; i < profile.DistinctIssues; i++)
        {
            var rare = i >= rareFrom;
            var category = (IssueCategory)(i % Enum.GetValues<IssueCategory>().Length);
            var severity = rare ? IssueSeverity.High
                : i % 5 == 0 ? IssueSeverity.Medium
                : IssueSeverity.Low;

            list.Add(NewSignature(
                logName: i % 3 == 0 ? "Application" : "System",
                source: $"Source-{i:D4}",
                eventId: 1000 + i,
                category: category,
                severity: severity,
                elevates: rare && rnd.NextDouble() < 0.5));
        }

        return list;
    }

    private static LogIssueSignature NewSignature(
        string logName, string source, int eventId, IssueCategory category, IssueSeverity severity, bool elevates) =>
        new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = severity == IssueSeverity.Low ? EventLogEntryType.Warning : EventLogEntryType.Error,
            Category = category,
            Severity = severity,
            ElevatesDayRisk = elevates,
            KnownIssue = elevates ? "硬體前兆：需立即確認" : null
        };

    /// <summary>
    /// 重尾抽樣：<c>index = (r^3) * N</c> 讓低索引（普遍問題）被抽中的機率遠高於高索引。
    /// 平均分布會讓每個簽章的主機數都差不多，「影響最廣」與「最重要」的衝突就重現不出來。
    /// </summary>
    private static int SkewedIndex(Random rnd, int count)
    {
        var r = rnd.NextDouble();
        return Math.Min(count - 1, (int)(r * r * r * count));
    }

    // ── 分析紀錄 ────────────────────────────────────────────────────────────

    private (long RecordRows, long TopIssueRows, long ContentJsonBytes) WriteRecords(
        ScaleProfile profile, IReadOnlyList<LogIssueSignature> signatures, Random rnd)
    {
        using var conn = new SqliteConnection(StorageBackend.DisableSqlitePoolingIfUnset($"Data Source={DbPath}"));
        conn.Open();

        // 產生期間關閉同步寫入：這是拋棄式的壓測資料庫，掉電損毀無所謂，
        // 但少了每次 fsync 之後灌資料快一個量級
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;";
            pragma.ExecuteNonQuery();
        }

        long recordRows = 0, topIssueRows = 0, contentBytes = 0;
        var startDate = DateTime.Today.AddDays(-profile.Days + 1);

        var tx = conn.BeginTransaction();
        var recordCmd = NewRecordCommand(conn, tx);
        var issueCmd = NewIssueCommand(conn, tx);

        try
        {
            foreach (var host in LivingHosts)
            {
                for (var d = 0; d < profile.Days; d++)
                {
                    var date = startDate.AddDays(d);
                    var issues = PickIssues(signatures, profile.IssuesPerRecord, rnd);
                    var record = BuildRecord(host, date, issues);

                    var shaped = RecordStorageShaper.ForStorage(record);
                    var json = JsonSerializer.Serialize(shaped);
                    contentBytes += json.Length;

                    var recordId = ++recordRows;
                    BindRecord(recordCmd, recordId, shaped, json);
                    recordCmd.ExecuteNonQuery();

                    foreach (var issue in shaped.TopIssues)
                    {
                        BindIssue(issueCmd, recordId, shaped, issue);
                        issueCmd.ExecuteNonQuery();
                        topIssueRows++;
                    }

                    if (recordRows % InsertBatchSize == 0)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = conn.BeginTransaction();
                        recordCmd.Transaction = tx;
                        issueCmd.Transaction = tx;
                    }
                }
            }

            tx.Commit();
        }
        finally
        {
            recordCmd.Dispose();
            issueCmd.Dispose();
            tx.Dispose();
        }

        return (recordRows, topIssueRows, contentBytes);
    }

    private static SqliteCommand NewRecordCommand(SqliteConnection conn, SqliteTransaction tx)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO lf_daily_records
                (record_id, host_id, host_name, record_date, risk_level, has_correlation, weekly_checkup_date, content_json, created_at)
            VALUES ($id, $hostId, $hostName, $date, $risk, $corr, NULL, $json, $created)
            """;
        AddParameters(cmd, "$id", "$hostId", "$hostName", "$date", "$risk", "$corr", "$json", "$created");
        return cmd;
    }

    private static SqliteCommand NewIssueCommand(SqliteConnection conn, SqliteTransaction tx)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 含 P4 的聚合維度（host_id／record_date／event_count／elevates_day_risk／log_name／entry_type）：
        // 問題聚合改由這張表 GROUP BY 回答之後，產生器不填這些欄位就等於造出一份
        // 「回填未完成」的資料，量測到的會是空的聚合結果
        cmd.CommandText = """
            INSERT INTO lf_top_issues
                (record_id, source_name, event_id, category, severity_rank,
                 host_id, record_date, event_count, elevates_day_risk, log_name, entry_type)
            VALUES ($recordId, $source, $eventId, $category, $rank,
                    $hostId, $date, $count, $elevates, $logName, $entryType)
            """;
        AddParameters(cmd, "$recordId", "$source", "$eventId", "$category", "$rank",
            "$hostId", "$date", "$count", "$elevates", "$logName", "$entryType");
        return cmd;
    }

    /// <summary>預先建好具名參數，之後每列只換值不重建命令（批次灌資料的關鍵）</summary>
    private static void AddParameters(SqliteCommand cmd, params string[] names)
    {
        foreach (var name in names)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            cmd.Parameters.Add(parameter);
        }
    }

    private static void BindRecord(SqliteCommand cmd, long recordId, DailyAnalysisRecord record, string json)
    {
        cmd.Parameters["$id"].Value = recordId;
        cmd.Parameters["$hostId"].Value = record.HostId;
        cmd.Parameters["$hostName"].Value = record.Host;
        cmd.Parameters["$date"].Value = record.Date.Date;
        cmd.Parameters["$risk"].Value = record.RiskLevel;
        cmd.Parameters["$corr"].Value = record.CorrelationAlerts.Count > 0;
        cmd.Parameters["$json"].Value = json;
        cmd.Parameters["$created"].Value = DateTime.Now;
    }

    private static void BindIssue(SqliteCommand cmd, long recordId, DailyAnalysisRecord record, LogIssueSignature issue)
    {
        cmd.Parameters["$recordId"].Value = recordId;
        cmd.Parameters["$source"].Value = issue.Source;
        cmd.Parameters["$eventId"].Value = issue.EventId;
        cmd.Parameters["$category"].Value = issue.Category.ToString();
        cmd.Parameters["$rank"].Value = (int)issue.Severity;
        cmd.Parameters["$hostId"].Value = record.HostId;
        cmd.Parameters["$date"].Value = record.Date.Date;
        cmd.Parameters["$count"].Value = issue.Count;
        cmd.Parameters["$elevates"].Value = issue.ElevatesDayRisk;
        cmd.Parameters["$logName"].Value = issue.LogName;
        cmd.Parameters["$entryType"].Value = (int)issue.EntryType;
    }

    private static List<LogIssueSignature> PickIssues(
        IReadOnlyList<LogIssueSignature> signatures, int count, Random rnd)
    {
        var picked = new Dictionary<string, LogIssueSignature>(StringComparer.Ordinal);

        // 前兩個「幾乎每台都有」的雜訊：九成機率必中，重現「數量最大但資訊量為零」的問題
        for (var i = 0; i < 2 && i < signatures.Count; i++)
        {
            if (rnd.NextDouble() < 0.9) Add(signatures[i]);
        }

        var guard = 0;
        while (picked.Count < count && guard++ < count * 8)
            Add(signatures[SkewedIndex(rnd, signatures.Count)]);

        return picked.Values.ToList();

        void Add(LogIssueSignature template)
        {
            var key = IssueSignatureKey.For(template);
            if (picked.ContainsKey(key)) return;

            picked[key] = new LogIssueSignature
            {
                LogName = template.LogName,
                Source = template.Source,
                EventId = template.EventId,
                EntryType = template.EntryType,
                Category = template.Category,
                Severity = template.Severity,
                ElevatesDayRisk = template.ElevatesDayRisk,
                KnownIssue = template.KnownIssue,
                Count = rnd.Next(1, 40),
                FirstSeen = "08:15",
                LastSeen = "17:42",
                DistinctMessageCount = rnd.Next(1, 4),
                SampleMessages = new List<string>
                {
                    $"{template.Source} 於處理要求時發生錯誤（事件 {template.EventId}），詳見系統紀錄。"
                }
            };
        }
    }

    private static DailyAnalysisRecord BuildRecord(WebHost host, DateTime date, List<LogIssueSignature> issues)
    {
        var elevates = issues.Any(i => i.ElevatesDayRisk);
        var high = issues.Any(i => i.Severity == IssueSeverity.High);
        var risk = elevates || high ? RiskLevels.High
            : issues.Any(i => i.Severity == IssueSeverity.Medium) ? RiskLevels.Medium
            : RiskLevels.Low;

        return new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            TopIssues = issues,
            ErrorCount = issues.Sum(i => i.Count),
            WarningCount = issues.Count,
            Headline = $"{host.HostName} {date:MM/dd} 偵測到 {issues.Count} 類問題",
            Summary = "壓測資料集產生的摘要內容，長度接近實際報告的結構化摘要段落。",
            TrendAssessment = "與前七日相比無顯著變化。",
            Action = "確認相關服務狀態後再行判斷。",
            AiAnalyzed = false
        };
    }

    // ── 處理狀態與案件 ──────────────────────────────────────────────────────

    private (long HandlingRows, long HandlingBlobBytes, long CaseRows, long CaseBlobBytes) WriteHandling(
        ScaleProfile profile, IReadOnlyList<LogIssueSignature> signatures, Random rnd)
    {
        var startDate = DateTime.Today.AddDays(-profile.Days + 1);

        // 案件：跨主機分布在存活主機上，狀態固定 in_progress（進行中案件才會被協調路徑讀到）
        var cases = new List<IssueCase>(profile.OpenCaseCount);
        for (var i = 0; i < profile.OpenCaseCount; i++)
        {
            var host = LivingHosts[i % LivingHosts.Count];
            var signature = signatures[SkewedIndex(rnd, signatures.Count)];
            var firstLinked = startDate.AddDays(rnd.Next(profile.Days));

            cases.Add(new IssueCase
            {
                CaseId = $"case{i:D8}",
                HostName = host.HostName,
                IssueKey = IssueSignatureKey.For(signature),
                IssueLabel = $"{signature.Source} {signature.EventId}",
                Status = IssueHandlingStatuses.InProgress,
                HandlerId = 2,
                FirstLinkedDate = firstLinked,
                LastLinkedDate = firstLinked.AddDays(rnd.Next(0, 10)),
                CreatedAt = firstLinked,
                CreatedByAccount = "scale-admin",
                UpdatedAt = firstLinked
            });
        }

        // 處理狀態：每（主機, 日）依 HandlingRowsPerHostDay 的機率產生一列
        var handlings = new List<IssueHandling>();
        foreach (var host in LivingHosts)
        {
            for (var d = 0; d < profile.Days; d++)
            {
                if (rnd.NextDouble() >= profile.HandlingRowsPerHostDay) continue;

                var signature = signatures[SkewedIndex(rnd, signatures.Count)];
                handlings.Add(new IssueHandling
                {
                    HostName = host.HostName,
                    Date = startDate.AddDays(d),
                    IssueKey = IssueSignatureKey.For(signature),
                    Status = rnd.NextDouble() < 0.6 ? IssueHandlingStatuses.KnownNoise : IssueHandlingStatuses.InProgress,
                    ActorId = 1,
                    ActorAccount = "scale-admin",
                    Note = "壓測資料集產生的處理說明。",
                    UpdatedAt = DateTime.Now
                });
            }
        }

        // P3 起這兩份走真表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 B），不再是整份 blob。
        // 產生器改走 store 的批次入口——它現在是「本批次涉及的列建索引後合併」，
        // 不是舊實作的 O(既有列 × 本次列)，灌資料不再是瓶頸
        Backend.IssueHandlingStore().SaveMany(handlings);
        Backend.IssueCaseStore().SaveMany(cases);

        // 真表沒有「整份 JSON 大小」這個概念了；回傳 0 保留欄位形狀，
        // 量測報告改看列數與 DB 檔案大小
        return (handlings.Count, 0, cases.Count, 0);
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    /// <summary>整份 blob 直接寫入（繞過 store 的逐筆合併，見類別註解），回傳 JSON 位元組數</summary>
    private long WriteBlob<T>(string key, List<T> items)
    {
        var json = JsonSerializer.Serialize(items, LfJsonOptions.Pretty);
        Backend.Blob(key).Mutate<object?>(_ => (json, null));
        return json.Length;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException)
        {
            // 壓測資料庫留在暫存目錄不影響任何事，刪不掉就算了（不要讓清理失敗蓋掉量測結果）
        }
    }
}

/// <summary>資料集的實際產出量，量測報告會把它一起印出來——沒有這些數字，耗時無從解讀</summary>
public sealed class ScaleDataSetStats
{
    public long RecordRows { get; init; }
    public long TopIssueRows { get; init; }
    public long ContentJsonBytes { get; init; }
    public long HandlingRows { get; init; }
    public long HandlingBlobBytes { get; init; }
    public long CaseRows { get; init; }
    public long CaseBlobBytes { get; init; }
    public long DbFileBytes { get; init; }
}
