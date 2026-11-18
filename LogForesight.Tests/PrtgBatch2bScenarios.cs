using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 2b 的情境產生器：只用「改動前就存在」的公開介面，輸出穩定字串。
/// 同一份情境在改動前的乾淨副本與現行程式各跑一次，字串必須逐字相同（見 <see cref="PrtgBatch2bTests"/> 的 golden）。
/// </summary>
internal static class PrtgBatch2bScenarios
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "scope-generation-2b";

    public sealed class PurgeEnv : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lf-2b-purge-" + Guid.NewGuid().ToString("N"));
        public StorageBackend Backend { get; }
        public SqlCommandRecorder Recorder { get; } = new();
        public PrtgScopePurgeStore Jobs { get; }
        public EfPrtgStore Prtg { get; }

        public PurgeEnv()
        {
            Directory.CreateDirectory(Dir);
            var conn = $"Data Source={Path.Combine(Dir, "test.db")}";
            Backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = conn }, Dir);
            LfDbContext Recording() => new(new DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite(conn).AddInterceptors(Recorder).Options);
            Jobs = new PrtgScopePurgeStore(Recording);
            Prtg = Backend.PrtgStore();
            Prtg.UpsertDevices([new PrtgDeviceRow { Objid = 1, Name = "d1", Ip = "10.0.0.1" },
                new PrtgDeviceRow { Objid = 2, Name = "d2", Ip = "10.0.0.2" }], Now.LocalDateTime);
            Prtg.UpsertSensors([new PrtgSensorRow { Objid = 11, DeviceObjid = 1, Name = "s11" },
                new PrtgSensorRow { Objid = 22, DeviceObjid = 2, Name = "s22" }], Now.LocalDateTime);
            using var ctx = Backend.CreateContext();
            for (var i = 0; i < 600; i++)
                ctx.PrtgValues.Add(new PrtgValueRow
                {
                    SensorObjid = i % 3 == 0 ? 11 : 22, PeriodStart = new DateTime(2026, 9, 1).AddHours(i),
                    AvgValue = i, Quality = "good", CreatedAt = Now.LocalDateTime
                });
            for (var i = 0; i < 300; i++)
                ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
                {
                    SensorObjid = i % 2 == 0 ? 11 : 22, ChangedAt = Now.LocalDateTime.AddMinutes(i), Status = "down",
                    Quality = "good", CreatedAt = Now.LocalDateTime
                });
            ctx.SaveChanges();
        }

        public PrtgScopePurgeJobRow Accepted(HashSet<long> keepDevices)
        {
            var preview = Jobs.CreatePreview(7, 3, null, Fingerprint, keepDevices, keepDevices.Count, Now);
            return Jobs.Accept(preview.JobId, 7, 3, null, Fingerprint, Now.AddMinutes(1));
        }

        public static string Fmt(PrtgScopePurgeJobRow r) =>
            $"status={r.Status};stop={r.StopReason};delV={r.DeletedValues};delS={r.DeletedStateChanges};" +
            $"remV={r.RemainingValues};remS={r.RemainingStateChanges};recon={r.CountsReconciled};" +
            $"otherV={r.OtherwiseRemovedValues};otherS={r.OtherwiseRemovedStateChanges};" +
            $"candV={r.CandidateValues};candS={r.CandidateStateChanges};curV={r.ValuesCursor}/{r.ValuesWatermark};" +
            $"curS={r.StateChangesCursor}/{r.StateChangesWatermark}";

        public int TableSelects() => Recorder.Selects().Count(c =>
            c.Sql.Contains("lf_prtg_values", StringComparison.Ordinal) ||
            c.Sql.Contains("lf_prtg_state_changes", StringComparison.Ordinal));

        /// <summary>走到 complete，回傳最終狀態字串與批次數。</summary>
        public (string Final, int Batches, int TableReads) RunToEnd(PrtgScopePurgeJobRow accepted)
        {
            Recorder.Clear();
            var current = accepted;
            var batches = 0;
            while (current.Status is not ("complete" or "stopped") && batches < 50)
            {
                current = Jobs.RunBatch(current.JobId, current.Revision, Fingerprint, Now.AddMinutes(2 + batches));
                batches++;
            }
            return (Fmt(current), batches, TableSelects());
        }

        public string RemainingRows()
        {
            using var ctx = Backend.CreateContext();
            return $"values={ctx.PrtgValues.Count()}/s11={ctx.PrtgValues.Count(x => x.SensorObjid == 11)};" +
                   $"states={ctx.PrtgStateChanges.Count()}/s11={ctx.PrtgStateChanges.Count(x => x.SensorObjid == 11)}";
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>保留全部裝置：候選集合為空。</summary>
    public static (string Final, string Rows, int Batches, int TableReads) PurgeEmptyCandidates()
    {
        using var env = new PurgeEnv();
        var accepted = env.Accepted([1, 2]);
        var (final, batches, reads) = env.RunToEnd(accepted);
        return (final, env.RemainingRows(), batches, reads);
    }

    /// <summary>保留裝置 1：sensor 22 為候選（候選非空，行為不得變）。</summary>
    public static (string Final, string Rows, int Batches, int TableReads) PurgeWithCandidates()
    {
        using var env = new PurgeEnv();
        var accepted = env.Accepted([1]);
        var (final, batches, reads) = env.RunToEnd(accepted);
        return (final, env.RemainingRows(), batches, reads);
    }

    /// <summary>候選非空時第一批之後的中間狀態（游標、刪除數、估算剩餘）。</summary>
    public static string PurgeWithCandidatesFirstBatch()
    {
        using var env = new PurgeEnv();
        var accepted = env.Accepted([1]);
        var first = env.Jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        return PurgeEnv.Fmt(first) + ";" + env.RemainingRows();
    }

    /// <summary>候選為空，但範圍指紋已變：照舊停止、不清除。</summary>
    public static (string Final, string Rows, int TableReads) PurgeEmptyCandidatesScopeChanged()
    {
        using var env = new PurgeEnv();
        var accepted = env.Accepted([1, 2]);
        env.Recorder.Clear();
        var stopped = env.Jobs.RunBatch(accepted.JobId, accepted.Revision, "another-generation", Now.AddMinutes(2));
        return (PurgeEnv.Fmt(stopped), env.RemainingRows(), env.TableSelects());
    }

    // ---------- A3：數值評估 ----------

    public sealed class AssessEnv : IDisposable
    {
        public static readonly DateOnly Completed = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lf-2b-assess-" + Guid.NewGuid().ToString("N"));
        public SqlCommandRecorder Recorder { get; } = new();
        public EfPrtgStore Store { get; }
        public IReadOnlyList<PrtgValueSensor> Sensors { get; }
        public Dictionary<long, long> DeviceToHost { get; } = new();
        public HashSet<long> HostIds { get; } = new();

        public AssessEnv(int sensorCount = 250)
        {
            Directory.CreateDirectory(Dir);
            var conn = $"Data Source={Path.Combine(Dir, "test.db")}";
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = conn }, Dir);
            var seed = backend.PrtgStore();
            var sensors = new List<PrtgSensorRow>();
            var values = new List<PrtgValueRow>();
            for (var i = 0; i < sensorCount; i++)
            {
                var objid = 1000 + i;
                sensors.Add(new PrtgSensorRow
                {
                    Objid = objid, DeviceObjid = objid, Name = $"S{objid}", Status = "Up",
                    SensorType = i % 4 == 3 ? "ping" : "SNMP CPU Load",
                    Category = i % 4 == 3 ? PrtgSensorCategories.Availability : PrtgSensorCategories.Cpu
                });
                var days = i % 5 == 0 ? 28 : i % 5 == 1 ? 5 : 0;
                for (var d = 0; d < days; d++)
                for (var h = 0; h < 24; h++)
                    values.Add(new PrtgValueRow
                    {
                        SensorObjid = objid, PeriodStart = Completed.AddDays(-d).ToDateTime(new TimeOnly(h, 0)),
                        AvgValue = 10 + d + h * 0.1 + i % 7, Coverage = 100, Quality = PrtgDataQuality.Ok,
                        CreatedAt = DateTime.Now
                    });
                // 每第 13 顆的裝置不對應，涵蓋 MappingInvalid
                if (i % 13 != 0) { DeviceToHost[objid] = 500 + i % 20; HostIds.Add(500 + i % 20); }
            }
            seed.UpsertSensors(sensors, DateTime.Now);
            seed.UpsertValues(values);
            // 對應：28 日內逐日一份，其中第 3 日整份失敗（全 unmatched）
            for (var d = 0; d < 40; d++)
            {
                var date = Completed.AddDays(-d).ToDateTime(TimeOnly.MinValue);
                var failed = d == 3;
                seed.ReplaceHostMapForDate(date, DeviceToHost.Select(m => new PrtgHostMapRow
                {
                    MapDate = date, DeviceObjid = m.Key, HostId = failed ? null : m.Value, HostName = $"h{m.Value}",
                    MapStatus = failed ? PrtgMapStatus.Unmatched : PrtgMapStatus.Ok, CreatedAt = DateTime.Now
                }).ToList());
            }
            LfDbContext Recording() => new(new DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite(conn).AddInterceptors(Recorder).Options);
            Store = new EfPrtgStore(Recording);
            Sensors = Store.GetAllSensors().Where(s => DeviceToHost.ContainsKey(s.Objid))
                .Select(s => new PrtgValueSensor(s.Objid, s.DeviceObjid, DeviceToHost[s.Objid], s.Name, s.SensorType, s.Category))
                .OrderBy(s => s.SensorObjid).ToList();
        }

        public PrtgValueAssessment Assessment() => new(Store, new Dictionary<string, PrtgValueSemantic>());

        /// <summary>「有效對應日期」查詢（SELECT DISTINCT map_date）的次數。</summary>
        public int OkDateQueries() => Recorder.Selects().Count(c =>
            c.Sql.Contains("lf_prtg_host_map", StringComparison.Ordinal) &&
            c.Sql.Contains("DISTINCT", StringComparison.OrdinalIgnoreCase));

        public static string Fmt(IEnumerable<PrtgValueAssessmentRow> rows) => string.Join("\n", rows.OrderBy(r => r.Sensor.SensorObjid)
            .Select(r => $"{r.Sensor.SensorObjid}|{r.Semantic}|{r.Reason}|{r.ValidDays}/{r.RequiredDays}|{r.LatestValidDay:yyyyMMdd}|" +
                         $"{string.Join(",", r.Daily.Select(x => $"{x.Day:MMdd}:{x.Value:F2}:{x.UsableHours}"))}|{r.ImplausibleHours}"));

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>就緒度：整體 Compute（內部分頁）。</summary>
    public static string Readiness(AssessEnv env)
    {
        var summary = PrtgValueReadiness.Compute(env.Assessment(), env.DeviceToHost, env.HostIds, new HashSet<long>(),
            AssessEnv.Completed, CancellationToken.None);
        return $"{summary.Population}|" + string.Join(",", summary.Counts.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) +
               "\n" + AssessEnv.Fmt(summary.Rows);
    }

    /// <summary>趨勢類／持平證據／規則試算共同的呼叫形狀：預設參數逐頁呼叫。</summary>
    public static string AssessAllPagesDefault(AssessEnv env, int windowDays, int requiredDays, DateOnly? newest = null)
    {
        var assessment = env.Assessment();
        var rows = new List<PrtgValueAssessmentRow>();
        foreach (var page in env.Sensors.Chunk(PrtgValueAssessment.PageSize))
            rows.AddRange(assessment.AssessPage(page, AssessEnv.Completed, windowDays, requiredDays, newest));
        return AssessEnv.Fmt(rows);
    }

    // ---------- A4：規則評估 ----------

    public static IReadOnlyList<KnownIssueRule> Rules() =>
        KnownIssueSeed.CreateRules().Where(r => r.Platform == "prtg").ToList();

    /// <summary>同裝置多顆感測器（availability／traffic／disk）、重複代碼多條規則、跨午夜 Down、震盪。</summary>
    public static (DateTime Day, List<PrtgStateChangeRow> Changes, Dictionary<long, long> SensorToDevice,
        List<PrtgSensorStatusInput> Statuses, List<KnownIssueRule> Rules) FoldScenario()
    {
        var day = new DateTime(2026, 8, 30);
        var statuses = new List<PrtgSensorStatusInput>();
        var sensorToDevice = new Dictionary<long, long>();
        var changes = new List<PrtgStateChangeRow>();
        void Add(long sensor, long device, string type, string category, string status = "Up")
        {
            statuses.Add(new PrtgSensorStatusInput(sensor, device, status, type, category));
            sensorToDevice[sensor] = device;
        }
        // 裝置 1：ping 兩顆都 Down（objid 小者為錨）＋兩顆 traffic Down ＋一顆 traffic 震盪 ＋ disk warning
        Add(12, 1, "Ping", PrtgSensorCategories.Availability, "Down");
        Add(11, 1, "Ping", PrtgSensorCategories.Availability, "Down");
        Add(13, 1, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic, "Down");
        Add(14, 1, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic, "Down");
        Add(15, 1, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic);
        Add(16, 1, "SNMP Disk Free", PrtgSensorCategories.Disk, "Warning");
        // 裝置 2：沒有 availability，兩顆 traffic Down 照常回傳
        Add(21, 2, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic, "Down");
        Add(22, 2, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic, "Down");
        // 裝置 3：availability 未 Down，同裝置 Down 不折疊
        Add(31, 3, "Ping", PrtgSensorCategories.Availability);
        Add(32, 3, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic, "Down");
        // 裝置 4：availability Down ＋ 震盪（合併）＋ 另一顆 availability Down
        Add(41, 4, "Ping", PrtgSensorCategories.Availability, "Down");
        Add(42, 4, "SNMP CPU Load", PrtgSensorCategories.Cpu, "Down");
        // 裝置 5：沉默（availability Up、其餘 Unknown）
        Add(51, 5, "Ping", PrtgSensorCategories.Availability);
        Add(52, 5, "SNMP CPU Load", PrtgSensorCategories.Cpu, "Unknown");
        foreach (var sensor in new long[] { 11, 12, 13, 14, 21, 22, 32, 41, 42 })
            changes.Add(new PrtgStateChangeRow { SensorObjid = sensor, ChangedAt = day.AddHours(-3), Status = "Down" });
        // 震盪：sensor 15（裝置 1）連續多次翻動
        foreach (var sensor in new long[] { 15 })
            for (var i = 0; i < 20; i++)
                changes.Add(new PrtgStateChangeRow
                {
                    SensorObjid = sensor, ChangedAt = day.AddHours(8).AddMinutes(i * 3), Status = i % 2 == 0 ? "Down" : "Up"
                });
        changes.Add(new PrtgStateChangeRow { SensorObjid = 16, ChangedAt = day.AddHours(9), Status = "Warning" });
        // 同代碼多條規則（Id 字典序最小者勝）
        var rules = Rules().ToList();
        var down = rules.First(r => r.PrtgRuleCode == PrtgRuleEvaluator.RuleDown);
        rules.Add(new KnownIssueRule
        {
            Id = "zzz-" + down.Id, Platform = "prtg", Enabled = true, PrtgRuleCode = PrtgRuleEvaluator.RuleDown,
            PrtgThreshold = 5, Origin = "custom"
        });
        return (day, changes, sensorToDevice, statuses, rules);
    }

    public static string FormatFindings(PrtgEvaluationResult result) =>
        $"merged={result.MergedCount};warn={string.Join("/", result.DuplicateRuleWarnings)}\n" +
        string.Join("\n", result.Select(f =>
            $"{f.DeviceObjid}|{f.SensorObjid}|{f.RuleCode}|{f.Magnitude}|{f.Rule.Id}|{f.SensorCategory}|{f.Detail}"));

    /// <summary>回望 1 天（含 includeSilent）與 5 天（逐日、不含 silent）。</summary>
    public static string EvaluateDays(int days, bool withLookups = false)
    {
        var (day, changes, sensorToDevice, statuses, rules) = FoldScenario();
        var sb = new StringBuilder();
        var lookups = withLookups ? PrtgSensorLookups.Build(statuses) : null;
        for (var d = 0; d < days; d++)
        {
            var current = day.AddDays(-d);
            var shifted = changes.Select(c => new PrtgStateChangeRow
            {
                SensorObjid = c.SensorObjid, ChangedAt = c.ChangedAt.AddDays(-d), Status = c.Status
            }).ToList();
            sb.AppendLine($"== {current:yyyyMMdd}");
            sb.AppendLine(FormatFindings(PrtgRuleEvaluator.Evaluate(current, shifted, sensorToDevice, statuses, rules,
                new Dictionary<long, string>(), new Dictionary<long, string>(), includeSilent: d == 0, lookups: lookups)));
        }
        return sb.ToString();
    }

    // ---------- A2：歷史弱佐證 ----------

    public static DailyAnalysisRecord Legacy() => new()
    {
        HostId = 101, Host = "TEST", Date = new DateTime(2026, 1, 20), LogSource = AnalysisLogSource.Netiq,
        RiskLevel = RiskLevels.High, RiskBasis = CorrelationPatternIds.PrtgStorageCorroborated,
        Headline = "舊雙重確認", Summary = "舊高風險敘事",
        TopIssues = [new() { Source = "disk", EventId = 153, LogName = "System", Severity = IssueSeverity.High }],
        CorrelationAlerts = ["【儲存故障雙重確認】舊推論"],
        CorrelationAlertRefs = [new() { Text = "【儲存故障雙重確認】舊推論", PatternId = CorrelationPatternIds.PrtgStorageCorroborated }]
    };

    public static DailyAnalysisRecord Plain(long hostId, int day) => new()
    {
        HostId = hostId, Host = "H" + hostId, Date = new DateTime(2026, 1, 1).AddDays(day), LogSource = AnalysisLogSource.Netiq,
        RiskLevel = RiskLevels.Low, Headline = "平靜", Summary = "無異常"
    };

    public static void SeedRisk(Func<LfDbContext> factory, int plain, bool legacyLast)
    {
        using var ctx = factory();
        for (var i = 0; i < plain; i++)
        {
            var record = Plain(1000 + i, i);
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = record.HostId, HostName = record.Host, RecordDate = record.Date, RiskLevel = "低",
                ContentJson = System.Text.Json.JsonSerializer.Serialize(record), CreatedAt = DateTime.Now
            });
        }
        if (legacyLast)
        {
            var legacy = Legacy();
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 101, HostName = "TEST", RecordDate = legacy.Date, RiskLevel = "高",
                ContentJson = System.Text.Json.JsonSerializer.Serialize(legacy), CreatedAt = DateTime.Now
            });
        }
        ctx.SaveChanges();
    }

    private static string Stable(string json)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(json, "\"ReviewedAtUtc\":\"[^\"]*\"", "\"ReviewedAtUtc\":\"-\"");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    public static string DumpRisk(Func<LfDbContext> factory)
    {
        using var ctx = factory();
        return string.Join("\n", ctx.DailyRecords.AsNoTracking().OrderBy(r => r.HostId).ThenBy(r => r.RecordDate).Select(r =>
            $"{r.HostId}|{r.RiskLevel}|{r.RiskProjectionVersion}|{r.RiskReviewStatus}|{r.Headline}|{r.HasCorrelation}|" +
            $"{(r.OriginalRiskContentJson == null ? "-" : "orig")}|{Stable(r.ContentJson)}"));
    }
}
