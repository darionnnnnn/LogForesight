using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

internal static class ExportJson
{
    public static JsonElement G(this JsonElement e, string name) => e.GetProperty(name);

    public static List<JsonElement> L(this JsonElement e) => e.EnumerateArray().ToList();

    public static string S(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
}

/// <summary>
/// 診斷匯出組裝的測試夾具：真實 SQLite 的 BatchRunStore／分析紀錄 store／主機等 store，
/// 搭配真實的遮罩器工廠。測試資料的時間都以「昨天」為基準，落在預設 14 天的期間內。
/// </summary>
internal sealed class RunExportHarness : IDisposable
{
    public readonly EfSqliteFixture Fx = new();
    public readonly IHostStore Hosts;
    public readonly IHostGroupStore Groups;
    public readonly IUserStore Users;
    public readonly ISentinelStore Sentinels;
    public readonly ISystemSettingsStore Settings;
    public readonly ScheduleOptionsStore Schedule;
    public readonly List<PrtgDeviceRow> Devices = new();
    public readonly BatchRunStore Runs;
    public readonly EfAnalysisRecordStore Records;
    public readonly RunMonitorService Monitor;
    public readonly RunExportDeidentifierFactory Factory;

    public static DateTime Today => DateTime.Today;
    public static DateTime Y => DateTime.Today.AddDays(-1);

    public RunExportHarness()
    {
        Hosts = new HostStore(Fx.Blob("hosts"));
        Groups = new HostGroupStore(Fx.Blob("host_groups"));
        Users = new UserStore(Fx.Blob("users"), Fx.Blob("user_last_login"));
        Sentinels = new SentinelStore(Fx.Blob("sentinels"));
        Settings = new SystemSettingsStore(Fx.Blob("system_settings"));
        Schedule = new ScheduleOptionsStore(Fx.Blob("schedule_options"));
        Runs = new BatchRunStore(Fx.LogStore("runs"), Fx.LogStore("run_logs"));
        Records = new EfAnalysisRecordStore(Fx.NewContext, "test");
        Monitor = new RunMonitorService(Runs, Hosts, Records, new FakeUserStore(), Schedule,
            new UserDisplayNameService(new FakeSystemSettingsStore()));
        Factory = new RunExportDeidentifierFactory(Hosts, Groups, Users, Sentinels, () => Devices, Settings);
    }

    public void Dispose() => Fx.Dispose();

    public RunExportService Service(RunExportLimits? limits = null, Func<IRunExportMasker>? masker = null) =>
        new(Runs, Monitor, Settings, Schedule, Hosts, Records, masker ?? (() => Factory.Create()), limits);

    public (JsonElement Root, byte[] Bytes, JsonDocument Doc) Export(int days = 14, RunExportLimits? limits = null,
        Func<IRunExportMasker>? masker = null, CancellationToken ct = default)
    {
        var result = Service(limits, masker).Build(days, ct);
        var doc = JsonDocument.Parse(result.Bytes);
        return (doc.RootElement, result.Bytes, doc);
    }

    public BatchRun AddRun(string host = "SRV-X", DateTime? start = null, Action<BatchRun>? edit = null)
    {
        var started = start ?? Y.AddHours(1);
        var run = new BatchRun
        {
            HostName = host,
            StartedAt = started,
            FinishedAt = started.AddHours(2),
            ExitCode = 0,
            AppVersion = "1.2.3.4"
        };
        edit?.Invoke(run);
        run.RunId = Runs.StartRun(run);
        Runs.FinishRun(run);
        return run;
    }

    public void AddLog(BatchRun run, string level, string logger, string message, DateTime? at = null, string? exception = null) =>
        Runs.AppendLog(new BatchRunLog
        {
            RunId = run.RunId,
            LoggedAt = at ?? run.StartedAt.AddMinutes(5),
            Level = level,
            Logger = logger,
            Message = message,
            ExceptionText = exception
        });

    public static string DiagnosticMessage(string label, string responseType = "DailyAiResult", string endedBy = "exhausted",
        params AiJsonAttemptDiagnostics[] attempts)
    {
        var entry = new AiJsonDiagnosticsEntry
        {
            Label = label,
            ResponseType = responseType,
            Model = "local-model",
            MaxTokens = 2048,
            Attempts = attempts.Length,
            EndedBy = endedBy,
            AttemptDetails = attempts.ToList()
        };
        return JsonSerializer.Serialize(entry, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public WebHost AddHost(string name, string? ip = null, string os = WebHost.OsWindows,
        string? display = null, string? role = null) =>
        Hosts.Upsert(new WebHost { HostName = name, IpAddress = ip, Os = os, Active = true, DisplayName = display, RoleDesc = role ?? string.Empty });

    /// <summary>寫入一筆並把抽出欄設成指定的降級狀態（<c>Append</c> 一律把次數歸零，所以事後直接改欄位）</summary>
    public DailyAnalysisRecord AddDegraded(WebHost host, DateTime date, string headline, string summary, int attempts, bool pending)
    {
        var rec = AddRecord(host, date, headline, summary);
        using var ctx = Fx.NewContext();
        ctx.DailyRecords.Where(r => r.RecordId == rec.RecordId)
            .ExecuteUpdate(s => s.SetProperty(p => p.AiDegradedAttempts, attempts).SetProperty(p => p.AiPending, pending));
        return rec;
    }

    public DailyAnalysisRecord AddRecord(WebHost host, DateTime date, string headline, string summary = "摘要",
        bool aiAnalyzed = true, List<LogIssueSignature>? issues = null, string risk = "中")
    {
        var rec = new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            Headline = headline,
            Summary = summary,
            AiAnalyzed = aiAnalyzed,
            TopIssues = issues ?? new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, Count = 3, SampleMessages = new List<string> { "磁碟 \"C:\\\" 發生錯誤" } }
            }
        };
        Records.Append(rec);
        return rec;
    }
}

/// <summary>排程作業診斷匯出組裝（批次 C）：C-1 訊號收集、C-2 上限、C-3 空資料、C-4 不變式、C-9／F-4 AI 格式異常、C-10 掃描範圍</summary>
public class RunExportServiceTests
{
    private static readonly string Anomaly = LogAnalysisService.AiFormatAnomalyHeadline;

    // exportedAt 與 meta.memory（匯出當下的即時行程記憶體）每次匯出都不同，比對穩定性時一併略過
    private static string Strip(string json) => Regex.Replace(
        Regex.Replace(json, "\"exportedAt\":\"[^\"]*\"", "\"exportedAt\":\"-\""),
        @"""memory"":\{[^}]*\}", "\"memory\":{}");

    // ===== C-1：訊號收集 =====

    [Fact]
    public void 第64輪_今天有趟時dailyHealth多一列isToday_趟次帶待補裝置數()
    {
        using var h = new RunExportHarness();
        h.AddRun("SRV-TODAY", DateTime.Today.AddHours(1), r =>
        {
            r.WarnCount = 1;
            r.PrtgDays = new List<PrtgDayStat>
            {
                new(DateTime.Today.AddDays(-1), BatchRun.PrtgOutcomePartial, 0, 0, true, 0, 0, 0, "1 台待補", NeedsReevaluation: true,
                    PendingSensorDevices: new long[] { 1, 2 }, PendingMessageDevices: new long[] { 2, 3 })
            };
        });

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var rows = root.G("dailyHealth").EnumerateArray().ToList();
            Assert.True(rows[^1].G("isToday").GetBoolean());
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), rows[^1].G("date").GetString());
            Assert.Single(rows, r => r.G("isToday").GetBoolean());
            var pending = root.G("abnormalRuns").EnumerateArray().SelectMany(r => r.G("unsuccessfulPrtgDays").EnumerateArray()).Single();
            Assert.Equal(3, pending.G("pendingDevices").GetInt32());
        }
    }

    [Fact]
    public void 第66輪_dailyHealth帶recheckCount與pendingSummary_guide說明不含空資料待複查()
    {
        using var h = new RunExportHarness();
        h.AddRun("SRV-TODAY", DateTime.Today.AddHours(1), r => r.NetiqDaysFailed = 2);

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var today = root.G("dailyHealth").EnumerateArray().Single(r => r.G("isToday").GetBoolean());
            Assert.Equal(0, today.G("recheckCount").GetInt32());
            Assert.Contains("NetIQ 2 主機日", today.G("pendingSummary").GetString());
            var guide = string.Join("\n", root.G("guide").L().Select(x => x.GetString()));
            Assert.Contains("recheckCount", guide);
        }
    }

    [Fact]
    public void 重複錯誤100則收成1組_次數100_樣本不超過3且互不相同()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        for (var i = 0; i < 100; i++)
            h.AddLog(run, "Error", "Orchestrator", $"查詢 SRV-A 第 {i} 次失敗 (id={Guid.NewGuid()})", run.StartedAt.AddMinutes(5 + i % 30));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var group = Assert.Single(root.G("signalGroups").L());
            Assert.Equal(100, group.G("count").GetInt32());
            Assert.Equal(1, group.G("affectedRuns").GetInt32());
            Assert.Equal("Error", group.S("level"));
            Assert.Equal("log", group.S("source"));
            Assert.Equal("Orchestrator", group.S("logger"));
            var samples = group.G("samples").L().Select(s => s.GetString()).ToList();
            Assert.Equal(3, samples.Count);
            Assert.Equal(3, samples.Distinct().Count());
            Assert.Equal(100, root.G("meta").G("signalTotals").G("errorFatal").GetInt32());
        }
    }

    [Fact]
    public void 組鍵先遮罩再正規化_只差數字與主機名的訊息併成一組()
    {
        using var h = new RunExportHarness();
        h.AddHost("SRV01");
        h.AddHost("SRV02");
        var run = h.AddRun();
        h.AddLog(run, "Error", "Orchestrator", "SRV01 第 3 次查詢失敗");
        h.AddLog(run, "Error", "Orchestrator", "SRV02 第 7 次查詢失敗");
        h.AddLog(run, "Error", "Other", "SRV01 第 3 次查詢失敗");   // 組鍵含 logger：不同 logger 不併

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var groups = root.G("signalGroups").L();
            Assert.Equal(2, groups.Count);
            var merged = groups.Single(g => g.S("logger") == "Orchestrator");
            Assert.Equal(2, merged.G("count").GetInt32());
            Assert.Equal(2, merged.G("samples").L().Count);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("SRV01", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SRV02", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Output的警告與失敗行進組_一般輸出_分析結果框_頻率異常行不進組()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        var lines = new[]
        {
            "  ⚠ [Sentinel-A] 2026-11-02 查詢結果被截斷（上限 5000 筆）",
            "\n  ✗ [Sentinel-A] 2026-11-02 批次查詢失敗（3 台）：逾時",
            "  ⚠ Security log 本次無法讀取（需系統管理員權限），入侵跡象相關偵測將標記為偵測盲區",
            "  ➜ 一般進度 12/50",
            "  ╔══════════════════════════════════════════════════╗",
            "  ║  ⚠ 警告：2026-11-02 偵測到需要立即關注的問題！",
            $"\n  ⚠ {AnalysisOrchestrator.TrendAlertConsoleHeading}",
            "分析完成 ✗ 不在行首"
        };
        foreach (var line in lines) h.AddLog(run, "Info", "Output", line);

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var groups = root.G("signalGroups").L();
            Assert.Equal(3, groups.Count);
            Assert.All(groups, g =>
            {
                Assert.Equal("output", g.S("source"));
                Assert.Equal("Output", g.S("logger"));
            });
            Assert.Equal(3, root.G("meta").G("signalTotals").G("outputSignals").GetInt32());
            // ✗ 推定為 Error、⚠ 為 Warn；排序 Error 在前
            Assert.Equal(new[] { "Error", "Warn", "Warn" }, groups.Select(g => g.S("level")).ToArray());
            // 輸出訊號不計入 /api/runs/errors 的對照基準
            Assert.Equal(0, root.G("meta").G("signalTotals").G("errorFatal").GetInt32());
        }
    }

    [Fact]
    public void 輸出樣本文案仍與正式碼一致_改文案會讓訊號篩選靜默少抓()
    {
        var root = FindRepoRoot();
        var netiq = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "NetiqPipelineService.cs"));
        var orchestrator = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Service", "AnalysisOrchestrator.cs"));
        Assert.Contains("✗ [{sentinelName}] {date:yyyy-MM-dd} 批次查詢失敗", netiq);
        Assert.Contains("⚠ [{sentinelName}] {date:yyyy-MM-dd} 查詢結果被截斷", netiq);
        Assert.Contains("⚠ Security log 本次無法讀取", orchestrator);
        Assert.Contains("⚠ {TrendAlertConsoleHeading}", orchestrator);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    [Theory]
    [InlineData("  ⚠ 某某", true, "Warn")]
    [InlineData("\n\t✗ 某某", true, "Error")]
    [InlineData("⚠️ 某某", true, "Warn")]
    [InlineData("  ║  ⚠ 某某", false, "")]
    [InlineData("一般 ⚠ 某某", false, "")]
    [InlineData("", false, "")]
    public void TryGetOutputSignal_行首符號判定(string message, bool expected, string level)
    {
        Assert.Equal(expected, RunExportService.TryGetOutputSignal(message, out var actual));
        Assert.Equal(level, actual);
    }

    // ===== 階段判定 =====

    [Fact]
    public void 階段判定_階段內_子階段內_階段外_舊紀錄缺Phases()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var withPhases = h.AddRun("SRV-P", y.AddHours(1), r =>
        {
            r.FinishedAt = y.AddHours(3);
            r.Phases = new List<RunPhaseRecord>
            {
                new(RunTimingPhases.Local, y.AddHours(1)) { FinishedAt = y.AddHours(1).AddMinutes(30), Outcome = "success" },
                new(RunTimingPhases.Prtg, y.AddHours(1).AddMinutes(30)) { FinishedAt = y.AddHours(2).AddMinutes(30), Outcome = "success" },
                new(RunTimingPhases.PrtgSync, y.AddHours(1).AddMinutes(40)) { FinishedAt = y.AddHours(1).AddMinutes(50), Outcome = "success" }
            };
        });
        var legacy = h.AddRun("SRV-L", y.AddHours(1));
        h.AddLog(withPhases, "Error", "A", "在本機階段內失敗", y.AddHours(1).AddMinutes(10));
        h.AddLog(withPhases, "Error", "A", "在子階段內失敗", y.AddHours(1).AddMinutes(45));
        h.AddLog(withPhases, "Error", "A", "在父階段內失敗", y.AddHours(2));
        h.AddLog(withPhases, "Error", "A", "在所有階段之外失敗", y.AddHours(5));
        h.AddLog(legacy, "Error", "A", "舊紀錄沒有階段資料", y.AddHours(1).AddMinutes(10));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            string StageOf(string sample) => root.G("signalGroups").L()
                .Single(g => g.G("samples").L().Any(s => s.GetString()!.Contains(sample)))
                .G("stages").L().Single().S("stage");

            Assert.Equal("local", StageOf("本機階段內"));
            Assert.Equal("prtg-sync", StageOf("子階段內"));
            Assert.Equal("prtg", StageOf("父階段內"));
            Assert.Equal("unknown", StageOf("所有階段之外"));
            Assert.Equal("unknown", StageOf("舊紀錄沒有階段資料"));
        }
    }

    [Fact]
    public void 階段判定_未結束的階段以趟次結束時間為界()
    {
        var y = RunExportHarness.Y;
        var run = new BatchRun
        {
            StartedAt = y.AddHours(1),
            FinishedAt = y.AddHours(2),
            Phases = new List<RunPhaseRecord> { new(RunTimingPhases.Netiq, y.AddHours(1)) }
        };
        Assert.Equal(RunTimingPhases.Netiq, RunExportService.StageOf(run, y.AddHours(1).AddMinutes(30)));
        Assert.Equal("unknown", RunExportService.StageOf(run, y.AddHours(3)));

        run.FinishedAt = null;
        Assert.Equal(RunTimingPhases.Netiq, RunExportService.StageOf(run, y.AddHours(30)));
        Assert.Equal("unknown", RunExportService.StageOf(null, y));
    }

    // ===== C-4 不變式 / C-2 上限 =====

    [Fact]
    public void C4不變式_Error與Fatal次數總和等於異常彙總_含省略的筆數_診斷行不影響()
    {
        using var h = new RunExportHarness();
        var run1 = h.AddRun("SRV-1");
        var run2 = h.AddRun("SRV-2", RunExportHarness.Y.AddHours(5));
        void Many(BatchRun r, string level, string message, int n)
        {
            for (var i = 0; i < n; i++) h.AddLog(r, level, "Orchestrator", message, r.StartedAt.AddMinutes(10 + i));
        }

        Many(run1, "Fatal", "致命的問題", 1);
        Many(run1, "Error", "第一種錯誤", 5);
        Many(run2, "Error", "第二種錯誤", 3);
        Many(run2, "Error", "第三種錯誤", 2);
        Many(run1, "Error", "第四種錯誤", 1);
        Many(run1, "Warn", "只是警告", 4);
        // AI 診斷行與慢 SQL：前者不進訊號組也不進彙總，後者是 Warn
        h.AddLog(run1, "Warn", AiJsonDiagnostics.LoggerName,
            RunExportHarness.DiagnosticMessage("daily-20260101-h9", attempts: new AiJsonAttemptDiagnostics { Attempt = 1, Category = "validation", Head = "{}" }));
        h.AddLog(run1, "Warn", BatchRunRecorder.ExemptWarnLogger, "[SQL][慢] Query 耗時 2500ms，超過門檻 2000ms");

        var expected = h.Monitor.GetErrorSummary(14).Groups.Sum(g => g.Count);
        Assert.Equal(12, expected);

        var (root, _, doc) = h.Export(limits: new RunExportLimits { SignalGroups = 2 });
        using (doc)
        {
            var totals = root.G("meta").G("signalTotals");
            Assert.Equal(expected, totals.G("errorFatal").GetInt32());
            Assert.Equal(1, totals.G("aiDiagnosticsLines").GetInt32());

            var kept = root.G("signalGroups").L().Where(g => g.S("source") == "log" && (g.S("level") is "Error" or "Fatal"))
                .Sum(g => g.G("count").GetInt32());
            var omitted = root.G("meta").G("omitted").L().Single(o => o.S("item") == "signalGroups");
            Assert.Equal(2, root.G("signalGroups").L().Count);
            Assert.Equal(6, kept);                                                       // 致命 1 ＋ 第一種錯誤 5
            Assert.Equal(6, omitted.G("errorFatalOccurrences").GetInt32());              // 3 ＋ 2 ＋ 1
            Assert.Equal(expected, kept + omitted.G("errorFatalOccurrences").GetInt32());
            Assert.Equal(4, omitted.G("count").GetInt32());                              // 三種錯誤組＋一個警告組
            Assert.Equal(10, omitted.G("occurrences").GetInt32());
        }
    }

    [Fact]
    public void 上限截斷時omitted數字正確_訊號組_異常趟_慢SQL()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        for (var i = 0; i < 5; i++)
        {
            var failed = h.AddRun($"SRV-{i}", y.AddHours(1 + i), r => r.ExitCode = 1);
            h.AddLog(failed, "Error", "Orchestrator", $"獨特的錯誤種類{(char)('甲' + i)}", failed.StartedAt.AddMinutes(1));
        }

        var anyRun = h.AddRun("SRV-S", y.AddHours(12));
        foreach (var (op, ms) in new[] { ("opA", 2500), ("opA", 3000), ("opB", 2100), ("opC", 2200) })
            h.AddLog(anyRun, "Warn", BatchRunRecorder.ExemptWarnLogger, $"[SQL][慢] {op} 耗時 {ms}ms，超過門檻 2000ms");

        var (root, _, doc) = h.Export(limits: new RunExportLimits { SignalGroups = 3, AbnormalRuns = 2, SlowSqlOperations = 1 });
        using (doc)
        {
            Assert.Equal(3, root.G("signalGroups").L().Count);
            Assert.Equal(2, root.G("abnormalRuns").L().Count);
            Assert.Single(root.G("slowSql").L());
            Assert.Equal("opA", root.G("slowSql")[0].S("operation"));

            var omitted = root.G("meta").G("omitted").L().ToDictionary(o => o.S("item"));
            Assert.Equal(2, omitted["signalGroups"].G("count").GetInt32());
            Assert.Equal(2, omitted["signalGroups"].G("occurrences").GetInt32());
            Assert.Equal(2, omitted["signalGroups"].G("errorFatalOccurrences").GetInt32());
            Assert.Equal(3, omitted["signalGroups"].G("limit").GetInt32());
            Assert.Equal(3, omitted["abnormalRuns"].G("count").GetInt32());
            Assert.Equal(2, omitted["slowSql"].G("count").GetInt32());
            Assert.Equal(2, omitted["slowSql"].G("occurrences").GetInt32());
        }
    }

    [Fact]
    public void 沒有被截斷時meta_omitted是空的()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        h.AddLog(run, "Error", "A", "一則錯誤");
        var (root, _, doc) = h.Export();
        using (doc) Assert.Empty(root.G("meta").G("omitted").L());
    }

    // ===== C-3 空資料 / 異常趟 / runStats =====

    [Fact]
    public void 期間內沒有任何趟次_仍可匯出_hasData為false_清單為空_機率與百分位為null()
    {
        using var h = new RunExportHarness();
        var (root, _, doc) = h.Export(7);
        using (doc)
        {
            Assert.Equal(1, root.G("meta").G("schemaVersion").GetInt32());
            Assert.False(root.G("meta").G("hasData").GetBoolean());
            Assert.Equal(7, root.G("meta").G("days").GetInt32());
            Assert.Empty(root.G("signalGroups").L());
            Assert.Empty(root.G("abnormalRuns").L());
            Assert.Empty(root.G("slowSql").L());
            Assert.Empty(root.G("runStats").G("byJob").L());
            Assert.Empty(root.G("runStats").G("phases").L());
            Assert.Equal(0, root.G("runStats").G("totalRuns").GetInt32());
            Assert.Equal(7, root.G("dailyHealth").L().Count);
            Assert.Equal(0, root.G("aiFormatAnomalies").G("caseCount").GetInt32());
            Assert.Empty(root.G("aiFormatAnomalies").G("cases").L());
            Assert.Equal(JsonValueKind.Null, root.G("aiFormatAnomalies").G("comparison").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.G("aiFormatAnomalies").G("detailedDiagnosticsFirstSeen").ValueKind);
        }
    }

    [Fact]
    public void 階段統計_沒有完成樣本時中位數P95最大值為null_AI呼叫為0時失敗率為null()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        h.AddRun("SRV-A", y.AddHours(1), r =>
        {
            r.Phases = new List<RunPhaseRecord>
            {
                new(RunTimingPhases.Local, y.AddHours(1)) { FinishedAt = y.AddHours(1).AddMinutes(10), Outcome = "success" },
                new(RunTimingPhases.Netiq, y.AddHours(1).AddMinutes(10))   // 趟次結束了階段卻沒有結束時間
            };
        });
        h.AddRun("SRV-B", y.AddHours(5), r =>
        {
            r.AiCalls = 4;
            r.AiFailures = 1;
            r.Phases = new List<RunPhaseRecord>
            {
                new(RunTimingPhases.Local, y.AddHours(5)) { FinishedAt = y.AddHours(5).AddMinutes(20), Outcome = "failed" }
            };
        });

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var phases = root.G("runStats").G("phases").L().ToDictionary(p => p.S("phase"));
            var local = phases["local"];
            Assert.Equal(2, local.G("runs").GetInt32());
            Assert.Equal(2, local.G("completed").GetInt32());
            Assert.Equal(1, local.G("notSucceeded").GetInt32());
            Assert.Equal(900.0, local.G("medianSeconds").GetDouble());   // (600 + 1200) / 2
            Assert.Equal(1200.0, local.G("p95Seconds").GetDouble());
            Assert.Equal(1200.0, local.G("maxSeconds").GetDouble());

            var netiq = phases["netiq"];
            Assert.Equal(0, netiq.G("completed").GetInt32());
            Assert.Equal(1, netiq.G("notSucceeded").GetInt32());   // 趟次已結束、階段卻沒結束
            Assert.Equal(JsonValueKind.Null, netiq.G("medianSeconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, netiq.G("p95Seconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, netiq.G("maxSeconds").ValueKind);

            var job = root.G("runStats").G("byJob").L().Single();
            Assert.Equal(4, job.G("aiCalls").GetInt32());
            Assert.Equal(0.25, job.G("aiFailureRate").GetDouble());
        }

        using var h2 = new RunExportHarness();
        h2.AddRun();
        var (root2, _, doc2) = h2.Export();
        using (doc2) Assert.Equal(JsonValueKind.Null, root2.G("runStats").G("byJob")[0].G("aiFailureRate").ValueKind);
    }

    [Fact]
    public void 異常趟_正常與進行中不進_warning_stuck_stopped_failed會進_stuck沒有Error行也會進()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var ok = h.AddRun("SRV-OK", y.AddHours(1));
        var warn = h.AddRun("SRV-W", y.AddHours(2), r => r.WarnCount = 2);
        var failedNoError = h.AddRun("SRV-F1", y.AddHours(3), r => r.ExitCode = 1);
        var failedWithError = h.AddRun("SRV-F2", y.AddHours(4), r => { r.ErrorCount = 3; r.Trigger = "manual:alice"; });
        var stuck = h.AddRun("SRV-ST", y.AddHours(5), r => { r.FinishedAt = null; r.ExitCode = null; });
        var stopped = h.AddRun("SRV-SP", y.AddHours(6), r => { r.Stopped = true; r.Trigger = BatchRunTriggers.Schedule; r.CatchUp = true; });
        var running = h.AddRun("SRV-RUN", DateTime.Now.AddMinutes(-5), r => { r.FinishedAt = null; r.ExitCode = null; });
        h.AddLog(failedWithError, "Error", "Orchestrator", "有 Error 行的失敗趟");

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var abnormal = root.G("abnormalRuns").L();
            var ids = abnormal.Select(a => a.G("runId").GetInt64()).ToHashSet();
            Assert.Equal(new[] { warn.RunId, failedNoError.RunId, failedWithError.RunId, stuck.RunId, stopped.RunId }.OrderBy(x => x),
                ids.OrderBy(x => x));
            Assert.DoesNotContain(ok.RunId, ids);
            Assert.DoesNotContain(running.RunId, ids);

            // failed／stuck 排在前面，再新到舊
            Assert.All(abnormal.Take(3), a => Assert.True(a.S("status") is "failed" or "stuck"));
            Assert.All(abnormal.Skip(3), a => Assert.True(a.S("status") is "warning" or "stopped"));

            var byId = abnormal.ToDictionary(a => a.G("runId").GetInt64());
            Assert.Equal("stuck", byId[stuck.RunId].S("status"));
            Assert.Equal(JsonValueKind.Null, byId[stuck.RunId].G("finishedAt").ValueKind);
            Assert.Equal("manual", byId[failedWithError.RunId].S("trigger"));
            Assert.Equal("schedule", byId[stopped.RunId].S("trigger"));
            Assert.True(byId[stopped.RunId].G("catchUp").GetBoolean());
            Assert.Equal(JsonValueKind.Null, byId[warn.RunId].G("catchUp").ValueKind);
            // 有 Error 行的趟，涉及的訊號代碼指到那一組
            Assert.Equal("S001", Assert.Single(byId[failedWithError.RunId].G("signalCodes").L()).GetString());
            Assert.Empty(byId[failedNoError.RunId].G("signalCodes").L());

            // 進行中的趟次只計入 runStats
            var jobs = root.G("runStats").G("byJob").L();
            Assert.Equal(1, jobs.Sum(j => j.G("running").GetInt32()));
            Assert.Equal(7, root.G("runStats").G("totalRuns").GetInt32());
            // 失敗或中斷、卻沒有任何 Error 行的趟數（F1 與 stuck）
            Assert.Equal(2, root.G("runStats").G("failedOrStuckWithoutErrorRuns").GetInt32());

            // 觸發來源只輸出類別，不輸出帳號
            Assert.DoesNotContain("alice", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ===== 慢 SQL / AI 歷史統計 / 回覆片段 =====

    [Fact]
    public void 慢SQL依操作彙總_不重複計入訊號組_解析不到的退回訊號組()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        foreach (var ms in new[] { 2500, 3000, 2100 })
            h.AddLog(run, "Warn", BatchRunRecorder.ExemptWarnLogger, $"[SQL][慢] blob:issue_handling:Mutate 耗時 {ms}ms，超過門檻 2000ms");
        h.AddLog(run, "Warn", BatchRunRecorder.ExemptWarnLogger, "[SQL][慢] 格式被改過了");

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var slow = Assert.Single(root.G("slowSql").L());
            Assert.Equal("blob:issue_handling:Mutate", slow.S("operation"));
            Assert.Equal(3, slow.G("count").GetInt32());
            Assert.Equal(3000, slow.G("maxMs").GetInt64());
            Assert.Equal(2533.3333, slow.G("avgMs").GetDouble());

            var group = Assert.Single(root.G("signalGroups").L());
            Assert.True(group.G("excludedFromWarnCount").GetBoolean());
            Assert.Equal(1, group.G("count").GetInt32());
            Assert.Equal(3, root.G("meta").G("signalTotals").G("slowSql").GetInt32());
            Assert.Equal(4, root.G("meta").G("signalTotals").G("warn").GetInt32());
        }
    }

    [Theory]
    [InlineData("[SQL][慢] Query 耗時 2500ms，超過門檻 2000ms", true, "Query", 2500)]
    [InlineData("[SQL][慢] blob:a b:Mutate 耗時 1ms，超過門檻 2000ms", true, "blob:a b:Mutate", 1)]
    [InlineData("[SQL][慢]  耗時 1ms，超過門檻 2000ms", false, "", 0)]
    [InlineData("[SQL][慢] Query 耗時 abcms，超過門檻 2000ms", false, "", 0)]
    [InlineData("其他訊息", false, "", 0)]
    public void SqlPerformanceMonitor_TryParseSlowLog(string message, bool ok, string op, long ms)
    {
        Assert.Equal(ok, SqlPerformanceMonitor.TryParseSlowLog(message, out var operation, out var elapsed));
        Assert.Equal(op, operation);
        Assert.Equal(ms, elapsed);
    }

    [Fact]
    public void AI逐次嘗試Warn依回覆型別統計_訊號組樣本不含回覆片段_次數照算()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        var parse = $"{AIService.JsonParseFailedPrefix}1/3 次）{AIService.JsonFailureTypeMarker}DailyAiResult，回覆長度=88，{AIService.JsonParseFailedPreviewMarker}這是模型亂回的內容SECRET-FRAGMENT-A";
        var validate = $"{AIService.JsonValidationFailedPrefix}2/3 次）{AIService.JsonFailureTypeMarker}DailyAiResult，{AIService.JsonValidationParsedMarker}{{\"headline\":\"SECRET-FRAGMENT-B\"}}";
        var other = $"{AIService.JsonParseFailedPrefix}1/3 次）{AIService.JsonFailureTypeMarker}OtherResult，回覆長度=5，{AIService.JsonParseFailedPreviewMarker}abc";
        h.AddLog(run, "Warn", "AIService", parse);
        h.AddLog(run, "Warn", "AIService", parse);
        h.AddLog(run, "Warn", "AIService", validate);
        h.AddLog(run, "Warn", "AIService", other);
        h.AddLog(run, "Error", "AIService", $"ChatJsonAsync 最終失敗，原因：{AIService.JsonContractError}：SECRET-FRAGMENT-C 後面還有很多字");
        h.AddLog(run, "Error", "AIService", $"ChatJsonAsync 最終失敗，原因：{AIService.JsonContractError}：（空回覆）");

        var expected = h.Monitor.GetErrorSummary(14).Groups.Sum(g => g.Count);
        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("SECRET-FRAGMENT", text);
            Assert.Contains("回覆片段已省略：有片段", text);
            Assert.Contains("（空回覆）", text);
            Assert.Equal(expected, root.G("meta").G("signalTotals").G("errorFatal").GetInt32());

            var history = root.G("aiFormatAnomalies").G("summary").G("failureHistory").L().ToDictionary(x => x.S("responseType"));
            Assert.Equal(2, history["DailyAiResult"].G("parseFailures").GetInt32());
            Assert.Equal(1, history["DailyAiResult"].G("validationFailures").GetInt32());
            Assert.Equal(1, history["OtherResult"].G("parseFailures").GetInt32());
            Assert.Equal(0, history["OtherResult"].G("validationFailures").GetInt32());
        }
    }

    [Fact]
    public void StripReplyFragment_契約失敗後的片段與兩個逐次嘗試範本的預覽欄被換成長度_沒有片段的訊息原樣保留()
    {
        var contract = $"原因：{AIService.JsonContractError}：這是回覆片段";
        Assert.Equal($"原因：{AIService.JsonContractError}：（回覆片段已省略：有片段，6 字）", RunExportService.StripReplyFragment(contract));
        Assert.Equal($"原因：{AIService.JsonContractError}：（空回覆）", RunExportService.StripReplyFragment($"原因：{AIService.JsonContractError}：（空回覆）"));
        Assert.Equal($"原因：{AIService.JsonContractError}：（空回覆）", RunExportService.StripReplyFragment($"原因：{AIService.JsonContractError}：  "));

        var preview = $"{AIService.JsonParseFailedPrefix}1/3 次）{AIService.JsonFailureTypeMarker}T，回覆長度=3，{AIService.JsonParseFailedPreviewMarker}abc";
        Assert.Equal($"{AIService.JsonParseFailedPrefix}1/3 次）{AIService.JsonFailureTypeMarker}T，回覆長度=3，{AIService.JsonParseFailedPreviewMarker}（回覆片段已省略：有片段，3 字）",
            RunExportService.StripReplyFragment(preview));
        Assert.Equal("一般訊息", RunExportService.StripReplyFragment("一般訊息"));
        Assert.Equal($"{AIService.JsonParseFailedPrefix}沒有預覽欄", RunExportService.StripReplyFragment($"{AIService.JsonParseFailedPrefix}沒有預覽欄"));
    }

    // ===== 去識別化：端對端 =====

    [Fact]
    public void 端對端_真實SQLite加真實遮罩器_整份輸出不含任何fixture原值_結構可解析()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host1 = h.AddHost("SRV-REAL-1", "10.8.8.8", display: "Real One", role: "資料庫伺服器");
        h.AddHost("SRV-REAL-2", "10.8.8.9");
        h.Groups.Upsert(new HostGroup { GroupName = "財務部" });
        h.Users.Upsert(new WebUser { Account = "alice", DisplayName = "Alice Real", Email = "alice@real.example" });
        h.Sentinels.Upsert(new Sentinel
        {
            Name = "SENT-REAL", BaseUrl = "https://sent-real.example.org/", Username = "svc-real",
            PasswordEnc = CryptoHelper.Encrypt("sentinel-pass-123")
        });
        h.Settings.Update(x =>
        {
            x.AiApiKeyEnc = CryptoHelper.Encrypt("ai-key-ABCDEF");
            x.SmtpPasswordEnc = CryptoHelper.Encrypt("smtp-pass-XYZ789");
            x.PrtgApiTokenEnc = CryptoHelper.Encrypt("prtg-token-QWERTY");
            x.PrtgPasswordEnc = CryptoHelper.Encrypt("prtg-pass-0000");
            x.PrtgPasshashEnc = CryptoHelper.Encrypt("1234567890");
            x.PrtgEnabled = true;
            x.PrtgUrl = "https://prtg-real.example.org";
            x.ServerDescription = "財務機房專用站台";
        });
        h.Devices.Add(new PrtgDeviceRow { Objid = 7, Name = "PRTG-DEV-REAL", Ip = "10.7.7.7", GroupPath = "Root/DC-REAL" });

        // 執行紀錄：站台、參數、訊息、堆疊、輸出行、慢 SQL、AI 診斷
        var run = h.AddRun("SRV-REAL-1", y.AddHours(1), r =>
        {
            r.ExitCode = 1;
            r.Args = "--host SRV-REAL-1 --token ai-key-ABCDEF --user alice";
            r.Trigger = "manual:alice";
            r.PrtgOutcome = BatchRun.PrtgOutcomePartial;
            r.Phases = new List<RunPhaseRecord>
            {
                new(RunTimingPhases.Prtg, y.AddHours(1)) { FinishedAt = y.AddHours(2), Outcome = "failed", Note = "連線 PRTG-DEV-REAL 失敗" }
            };
            r.PrtgDays = new List<PrtgDayStat> { new(y, BatchRun.PrtgOutcomeFailed, 0, 0, false, 0, 0, 0, "裝置 10.7.7.7 無回應") };
        });
        h.AddLog(run, "Error", "NetiqPipelineService",
            "連線 SRV-REAL-1 (10.8.8.8) 失敗，帳號 alice，密碼 sentinel-pass-123，位址 https://sent-real.example.org/api?token=abc123",
            y.AddHours(1).AddMinutes(30),
            "System.InvalidOperationException: 無法登入 SENT-REAL\r\n   at LogForesight.Core.Service.NetiqPipelineService.Run() in C:\\Users\\alice\\src\\NetiqPipelineService.cs:line 321\r\n   at System.Threading.Tasks.Task.Run()");
        h.AddLog(run, "Info", "Output", "  ✗ [SENT-REAL] 2026-11-02 SRV-REAL-2 分析失敗：Account Name: alice from 10.8.8.9");
        h.AddLog(run, "Warn", BatchRunRecorder.ExemptWarnLogger, "[SQL][慢] blob:SRV-REAL-1:Mutate 耗時 2600ms，超過門檻 2000ms");

        // AI 格式異常案例：回覆與問題訊息裡有身分資訊
        var reply = "{\"headline\": \"SRV-REAL-1 的 alice 帳號從 10.8.8.8 登入\", \"story\": ";
        h.AddRecord(host1, y.AddDays(-2), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, reply),
            issues: new List<LogIssueSignature>
            {
                new() { LogName = "Security", Source = "SRV-REAL-2", EventId = 4625, Count = 9,
                    SampleMessages = new List<string> { "Account Name: alice\r\nWorkstation Name: SRV-REAL-2\r\nSource Network Address: 10.8.8.9" } }
            });
        var diagLabel = $"daily-{y.AddDays(-2):yyyyMMdd}-h{host1.HostId}";
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName, RunExportHarness.DiagnosticMessage(diagLabel, attempts: new[]
        {
            new AiJsonAttemptDiagnostics
            {
                Attempt = 1, Category = "validation", FinishReason = "stop", PromptChars = 4000, ResponseChars = 700, ParseErrorPath = "$.action",
                FieldLengths = new Dictionary<string, object> { ["headline"] = 12, ["story"] = 640, ["action"] = "Array" },
                Head = "{\"headline\":\"SRV-REAL-1 出現問題\",\"story\":\"帳號 alice 於 10.8.8.8\"", Tail = "結尾 SENT-REAL"
            }
        }));

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var original in new[]
                     {
                         "SRV-REAL-1", "SRV-REAL-2", "Real One", "10.8.8.8", "10.8.8.9", "alice", "Alice Real", "real.example",
                         "SENT-REAL", "sent-real", "svc-real", "sentinel-pass-123", "ai-key-ABCDEF", "smtp-pass-XYZ789",
                         "prtg-token-QWERTY", "prtg-pass-0000", "1234567890", "財務部", "PRTG-DEV-REAL", "10.7.7.7", "DC-REAL",
                         "prtg-real", "財務機房專用站台", "資料庫伺服器", "C:\\\\Users", "abc123"
                     })
                Assert.DoesNotContain(original, text, StringComparison.OrdinalIgnoreCase);

            // 結構：每個區塊都在，且能解析回來
            foreach (var section in new[] { "meta", "guide", "context", "dailyHealth", "runStats", "signalGroups", "stacks", "slowSql", "abnormalRuns", "aiFormatAnomalies" })
                Assert.True(root.TryGetProperty(section, out _), section);
            Assert.True(root.G("meta").G("hasData").GetBoolean());
            Assert.True(root.G("meta").G("includesEventText").GetBoolean());
            Assert.True(root.G("meta").G("deidentification").G("tokens").L().Count > 0);
            Assert.Contains("host-", text);
            Assert.Contains("secret", text);

            var groups = root.G("signalGroups").L();
            Assert.Equal(2, groups.Count);
            Assert.Single(root.G("slowSql").L());
            Assert.Single(root.G("stacks").L());
            Assert.Contains("略過 1 層框架呼叫", root.G("stacks")[0].S("text"));
            Assert.DoesNotContain(@"C:\Users", root.G("stacks")[0].S("text"));
            Assert.Equal("host-001", root.G("abnormalRuns")[0].S("host"));

            var anomalies = root.G("aiFormatAnomalies");
            Assert.Equal(1, anomalies.G("caseCount").GetInt32());
            var theCase = anomalies.G("cases")[0];
            Assert.Equal("host-001", theCase.S("host"));
            Assert.Equal(3, theCase.G("attempts").GetInt32());
            Assert.Equal("object", theCase.S("replyShape"));
            Assert.Equal("validation", theCase.G("diagnostics")[0].G("attemptDetails")[0].S("category"));
            Assert.Equal(3, theCase.G("diagnostics")[0].G("attemptDetails")[0].G("fieldLengths").L().Count);
            Assert.Equal(9, theCase.G("input").G("issues")[0].G("count").GetInt32());
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), root.G("meta").G("period").S("to"));
        }
    }

    [Fact]
    public void 輸出順序與數字格式穩定_同一份資料匯出兩次內容相同_小數四位()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun("SRV-A", RunExportHarness.Y.AddHours(1), r => { r.AiCalls = 3; r.AiFailures = 1; });
        for (var i = 0; i < 5; i++) h.AddLog(run, i % 2 == 0 ? "Error" : "Warn", "L" + i, $"訊息類型 {(char)('甲' + i)} 第 {i} 次", run.StartedAt.AddMinutes(i));
        var service = h.Service();

        var first = Encoding.UTF8.GetString(service.Build(14, CancellationToken.None).Bytes);
        var second = Encoding.UTF8.GetString(service.Build(14, CancellationToken.None).Bytes);

        Assert.Equal(Strip(first), Strip(second));
        Assert.Contains("\"aiFailureRate\":0.3333", first);
        Assert.Contains("訊息類型", first);          // 中文不跳脫
        Assert.DoesNotContain("\\u", first);
    }

    // ===== C-10：結果掃描範圍 =====

    [Fact]
    public void C10_帳號admin_狀態status_群組count_level的站台能成功匯出_且放行清單不放過主機名()
    {
        using var h = new RunExportHarness();
        h.Users.Upsert(new WebUser { Account = "admin", DisplayName = "admin", Email = "admin@site.example" });
        foreach (var g in new[] { "status", "count", "level", "success", "Output", "log", "Error" })
            h.Groups.Upsert(new HostGroup { GroupName = g });
        h.AddHost("SRV-C10", "10.4.4.4");
        var run = h.AddRun("SRV-C10");
        h.AddLog(run, "Error", "Orchestrator", "admin 在 status 群組 count level 失敗，主機 SRV-C10 success Error");
        h.AddLog(run, "Info", "Output", "  ⚠ Output 群組 log 異常");

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var groups = root.G("signalGroups").L();
            Assert.Equal(2, groups.Count);
            Assert.Contains("user-001 在 group-001 群組 group-002 group-003 失敗，主機 host-001 group-004 group-005", groups.SelectMany(g => g.G("samples").L()).Select(s => s.GetString()!));
            // 程式產生的封閉集合值（等級、來源、logger）即使撞上字典詞也整串放行，不被誤遮
            var outputGroup = groups.Single(g => g.S("source") == "output");
            Assert.Equal("Output", outputGroup.S("logger"));
            Assert.Equal("Warn", outputGroup.S("level"));
            Assert.Equal("log", groups.Single(g => g.S("source") == "log").S("source"));
            Assert.DoesNotContain("SRV-C10", Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public void C10_放行清單只放程式產生的值_漏遮的自由文字會讓匯出失敗且不給檔案()
    {
        using var h = new RunExportHarness();
        h.AddHost("SRV-LEAK");
        var run = h.AddRun();
        h.AddLog(run, "Error", "Orchestrator", "LEAKME 連線 SRV-LEAK 失敗");

        var ex = Assert.Throws<RunExportDeidentificationException>(() =>
            h.Export(masker: () => new LeakyMasker(h.Factory.Create())));
        Assert.Equal(DeidentificationFailureKind.ResidualFound, ex.Kind);
        Assert.True(ex.Residual.ContainsKey(RunExportDeidentifier.ResidualKinds.Dictionary));
        // 例外訊息與屬性不含原值
        Assert.DoesNotContain("SRV-LEAK", ex.Message);
        Assert.DoesNotContain("SRV-LEAK", string.Join(",", ex.Residual.Keys));
    }

    [Fact]
    public void F4_notice說明短名稱不遮()
    {
        using var h = new RunExportHarness();
        h.AddRun();

        var (root, _, doc) = h.Export();
        using (doc)
        {
            Assert.Contains("群組與角色名稱若在兩字以內不會被遮",
                root.GetProperty("meta").GetProperty("deidentification").GetProperty("notice").GetString());
        }
    }

    [Fact]
    public void 解不開的憑證仍可匯出_名稱寫進meta且不含值()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        h.AddLog(run, "Error", "Orchestrator", "連線失敗 password=plain-value-1");

        var (root, bytes, doc) = h.Export(masker: () => RunExportDeidentifier.Create(
            new RunExportDeidentifierSource { UndecryptableSecrets = new[] { "PRTG 密碼" } }));

        using (doc)
        {
            var names = root.GetProperty("meta").GetProperty("deidentification")
                .GetProperty("undecryptableSecrets").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "PRTG 密碼" }, names);
            Assert.DoesNotContain("plain-value-1", System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public void 訊號組_同訊息同時有Warn與Error_count含兩者且errorFatalCount只算Error()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        h.AddLog(run, "Warn", "Orchestrator", "磁碟剩餘 12 GB");
        h.AddLog(run, "Error", "Orchestrator", "磁碟剩餘 3 GB");

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var group = Assert.Single(root.GetProperty("signalGroups").EnumerateArray());
            Assert.Equal("Error", group.GetProperty("level").GetString());
            Assert.Equal(2, group.GetProperty("count").GetInt32());
            Assert.Equal(1, group.GetProperty("errorFatalCount").GetInt32());
            Assert.Equal(1, root.GetProperty("meta").GetProperty("signalTotals").GetProperty("errorFatal").GetInt32());
        }
    }

    /// <summary>故意漏遮：訊息含 LEAKME 時原樣回傳。其餘委派給真實遮罩器（含結果掃描）</summary>
    private sealed class LeakyMasker : IRunExportMasker
    {
        private readonly RunExportDeidentifier _inner;

        public LeakyMasker(RunExportDeidentifier inner) => _inner = inner;

        public string Mask(string? text) => text != null && text.Contains("LEAKME") ? text : _inner.Mask(text);

        public string? MaskHost(string? hostText) => _inner.MaskHost(hostText);

        public string MaskStack(string? stack) => _inner.MaskStack(stack);

        public IReadOnlyDictionary<string, int> TokenCounts => _inner.TokenCounts;

        public void EnsureNoResidual(string json, IEnumerable<string>? allowedLiterals = null, bool scanPropertyNames = true) =>
            _inner.EnsureNoResidual(json, allowedLiterals, scanPropertyNames);
    }

    [Fact]
    public void ScanResidual_只掃值不掃屬性名稱的選項_動態值仍被掃()
    {
        var m = RunExportDeidentifier.Create(new RunExportDeidentifierSource
        {
            Users = new[] { new DeidentifierUser(1, "status", null, null) },
            Hosts = new[] { new DeidentifierHost(1, "SRV-Z", null, null, null, null, null) }
        });
        const string json = "{\"status\":\"ok\"}";

        Assert.True(m.ScanResidual(json).ContainsKey(RunExportDeidentifier.ResidualKinds.Dictionary));
        Assert.Empty(m.ScanResidual(json, scanPropertyNames: false));
        Assert.True(m.ScanResidual("{\"state\":\"SRV-Z\"}", scanPropertyNames: false).ContainsKey(RunExportDeidentifier.ResidualKinds.Dictionary));
        Assert.Throws<RunExportDeidentificationException>(() => m.EnsureNoResidual(json));
        m.EnsureNoResidual(json, scanPropertyNames: false);
    }

    [Fact]
    public void 版本字串是程式放行的值_非版本形狀的版本當資料遮罩()
    {
        using var h = new RunExportHarness();
        h.AddHost("SRV-V");
        var run = h.AddRun("SRV-X", null, r => r.AppVersion = "9.9.9.9");
        h.AddLog(run, "Error", "A", "版本 9.9.9.9 的問題");
        h.AddRun("SRV-Y", RunExportHarness.Y.AddHours(8), r => r.AppVersion = "ver SRV-V");
        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var text = Encoding.UTF8.GetString(bytes);
            // 版本欄位整串放行；訊息內的同一串在文字內被當 IP 遮掉
            Assert.Contains("\"version\":\"9.9.9.9\"", text);
            Assert.Equal("版本 ip-001 的問題", root.G("signalGroups")[0].G("samples")[0].GetString());
            // 非版本形狀的值當資料處理：字典內的主機名被遮
            Assert.DoesNotContain("SRV-V", text);
            Assert.Contains("\"version\":\"ver host-001\"", text);
        }
    }

    // ===== 取消 / 失敗 / 容量 =====

    [Fact]
    public void 已取消的請求不組裝_遮罩器來源失敗在讀取任何診斷行之前就中止()
    {
        using var h = new RunExportHarness();
        h.AddRun();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => h.Service().Build(14, cts.Token));

        var factoryCalls = 0;
        var ex = Assert.Throws<RunExportDeidentificationException>(() =>
            h.Service(masker: () =>
            {
                factoryCalls++;
                throw new RunExportDeidentificationException("主機清單", new InvalidOperationException("壞了"));
            }).Build(14, CancellationToken.None));
        Assert.Equal(DeidentificationFailureKind.SourceUnavailable, ex.Kind);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void 逐行讀取途中取消就停止_不產生任何內容()
    {
        using var h = new RunExportHarness();
        var run = h.AddRun();
        for (var i = 0; i < 20; i++) h.AddLog(run, "Error", "A", $"錯誤 {i}");
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            h.Service(masker: () => new CancelOnMask(h.Factory.Create(), cts)).Build(14, cts.Token));
    }

    private sealed class CancelOnMask : IRunExportMasker
    {
        private readonly RunExportDeidentifier _inner;
        private readonly CancellationTokenSource _cts;

        public CancelOnMask(RunExportDeidentifier inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        public string Mask(string? text)
        {
            _cts.Cancel();
            return _inner.Mask(text);
        }

        public string? MaskHost(string? hostText) => _inner.MaskHost(hostText);
        public string MaskStack(string? stack) => _inner.MaskStack(stack);
        public IReadOnlyDictionary<string, int> TokenCounts => _inner.TokenCounts;

        public void EnsureNoResidual(string json, IEnumerable<string>? allowedLiterals = null, bool scanPropertyNames = true) =>
            _inner.EnsureNoResidual(json, allowedLiterals, scanPropertyNames);
    }

    [Fact]
    public void 超過檔案上限回413_capacity_exceeded_不截斷()
    {
        using var h = new RunExportHarness();
        h.AddRun();
        var ex = Assert.Throws<DomainException>(() => h.Export(limits: new RunExportLimits { OutputBytes = 500 }));
        Assert.Equal(ApiErrorCodes.CapacityExceeded, ex.Code);
        Assert.Equal(413, ApiErrorCodes.StatusOf(ex.Code));
    }

    [Fact]
    public void context只有白名單_SystemSettings的密鑰與識別欄位值不得出現()
    {
        using var h = new RunExportHarness();
        h.AddRun();
        var used = new HashSet<string> { nameof(SystemSettings.AiModel), nameof(SystemSettings.AiProvider),
            nameof(SystemSettings.PrtgFetchStrategy), nameof(SystemSettings.PrtgValueFetchScope) };
        var markers = new List<string>();
        h.Settings.Update(s =>
        {
            foreach (var p in typeof(SystemSettings).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite && !used.Contains(p.Name)))
            {
                var marker = $"ZZMARK_{p.Name}_ZZ";
                // 密文欄位以真密文存放，其餘當一般字串
                p.SetValue(s, p.Name.EndsWith("Enc", StringComparison.Ordinal) ? CryptoHelper.Encrypt(marker) : marker);
                markers.Add(marker);
            }

            s.MailRecipients = new List<string> { "ZZMARK_recipient_ZZ" };
            s.AdServers = new List<string> { "ZZMARK_adserver_ZZ" };
            s.WatchedFolders = new List<string> { "ZZMARK_folder_ZZ" };
            markers.AddRange(new[] { "ZZMARK_recipient_ZZ", "ZZMARK_adserver_ZZ", "ZZMARK_folder_ZZ" });
            s.AiModel = "gpt-test";
        });

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.NotEmpty(markers);
            foreach (var marker in markers) Assert.DoesNotContain(marker, text);
            Assert.Equal("gpt-test", root.G("context").G("ai").S("model"));
            Assert.Equal(2048, root.G("context").G("ai").G("aiMaxTokens").GetInt32());
            Assert.Equal(60, root.G("context").G("ai").G("headlineMaxChars").GetInt32());
            Assert.Equal(600, root.G("context").G("ai").G("otherFieldMaxChars").GetInt32());
            Assert.Equal(2, root.G("context").G("ai").G("aiJsonRetryCount").GetInt32());
        }
    }

    // ===== F-4：AI 格式異常區塊 =====

    private static string Repeat(char c, int n) => new(c, n);

    [Fact]
    public void AI案例_解析失敗與內容檢查失敗都算_正常紀錄低風險跳過與標題變體不算_重新分析成功後不再是案例()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-1");
        h.AddRecord(host, y.AddDays(-1), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "不是 JSON 的一般文字"));
        h.AddRecord(host, y.AddDays(-2), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(2, "{\"headline\":\"" + Repeat('長', 80) + "\"}"));
        h.AddRecord(host, y.AddDays(-3), "今日狀況正常，無需處理", aiAnalyzed: false);
        h.AddRecord(host, y.AddDays(-4), "一個正常的標題");
        h.AddRecord(host, y.AddDays(-5), Anomaly + "（補）");
        var healed = h.AddRecord(host, y.AddDays(-6), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "之後重新分析成功"));
        using (var ctx = h.Fx.NewContext())
            ctx.DailyRecords.Where(r => r.RecordId == healed.RecordId).ExecuteUpdate(s => s.SetProperty(p => p.Headline, "重新分析成功的標題"));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var section = root.G("aiFormatAnomalies");
            Assert.Equal(2, section.G("caseCount").GetInt32());
            Assert.False(section.G("caseCountIsLowerBound").GetBoolean());
            var cases = section.G("cases").L();
            // 新到舊
            Assert.Equal(new[] { y.AddDays(-1).ToString("yyyy-MM-dd"), y.AddDays(-2).ToString("yyyy-MM-dd") }, cases.Select(c => c.S("date")).ToArray());
            Assert.Equal(new[] { 3, 2 }, cases.Select(c => c.G("attempts").GetInt32()).ToArray());
            Assert.Equal(new[] { "text", "object" }, cases.Select(c => c.S("replyShape")).ToArray());
        }
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(601, true)]
    public void AI案例_回覆字數達600字上限標cut_off_不推測被截掉的內容(int length, bool cutOff)
    {
        var shape = AiReplyShape.Classify(Repeat('x', length), LogAnalysisService.MaxSummaryChars);
        Assert.Equal(cutOff, shape.CutOff);
    }

    [Fact]
    public void AI案例_回覆類別與cut_off_從摘要前綴剝除後判定_前綴缺失時次數為null()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-S");
        h.AddRecord(host, y.AddDays(-1), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, Repeat('x', 600)));                       // 剛好 600
        h.AddRecord(host, y.AddDays(-2), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "```json\n" + Repeat('y', 700)));          // 截成 600 加「...」＝603
        h.AddRecord(host, y.AddDays(-3), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(1, "[1,2,3]"));
        h.AddRecord(host, y.AddDays(-4), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(1, "   "));
        h.AddRecord(host, y.AddDays(-5), Anomaly, "沒有前綴的舊格式摘要");

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var cases = root.G("aiFormatAnomalies").G("cases").L();
            Assert.Equal(new[] { "text", "fence", "array", "blank" }, cases.Take(4).Select(c => c.S("replyShape")).ToArray());
            Assert.Equal(new[] { true, true, false, false, false }, cases.Select(c => c.G("cutOff").GetBoolean()).ToArray());
            Assert.Equal(JsonValueKind.Null, cases[4].G("attempts").ValueKind);
            // 解不出說明前綴的摘要不是保留的 AI 回覆：回覆與形狀都是 null，不把整段摘要當回覆分類
            Assert.Equal(JsonValueKind.Null, cases[4].G("reply").ValueKind);
            Assert.Equal(JsonValueKind.Null, cases[4].G("replyShape").ValueKind);
            Assert.Equal(603, cases[1].S("reply").Length);
            Assert.Equal(2, root.G("aiFormatAnomalies").G("summary").G("cutOffCount").GetInt32());
        }
    }

    [Fact]
    public void AI案例_兩種來源都進_已恢復與從未降級不進_並帶出降級次數與是否放棄()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-D");
        var summary = LogAnalysisService.BuildAiFormatAnomalySummary(3, "[\n]");
        h.AddDegraded(host, y.AddDays(-1), AiDegradation.ComposeHeadline("磁碟錯誤偏多", 2), summary, attempts: 2, pending: true);       // 降級中
        h.AddDegraded(host, y.AddDays(-2), AiDegradation.ComposeHeadline("磁碟錯誤偏多", 3), summary, attempts: 3, pending: false);      // 已放棄
        h.AddRecord(host, y.AddDays(-3), Anomaly, summary);                                                                             // 歷史舊標題（次數 0）
        h.AddDegraded(host, y.AddDays(-4), "磁碟錯誤偏多，建議檢查陣列", "AI 補跑成功的白話摘要", attempts: 1, pending: false);               // 已恢復
        h.AddRecord(host, y.AddDays(-5), "從未降級的正常標題");

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var section = root.G("aiFormatAnomalies");
            Assert.Equal(3, section.G("caseCount").GetInt32());
            var cases = section.G("cases").L();
            Assert.Equal(new[] { y.AddDays(-1), y.AddDays(-2), y.AddDays(-3) }.Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
                cases.Select(c => c.S("date")).ToArray());
            Assert.Equal(new[] { 2, 3, 0 }, cases.Select(c => c.G("degradedAttempts").GetInt32()).ToArray());
            Assert.Equal(new[] { false, true, false }, cases.Select(c => c.G("abandoned").GetBoolean()).ToArray());
            // 新降級紀錄的摘要仍有說明前綴，能解出單次呼叫內的嘗試次數與回覆
            Assert.Equal(new[] { 3, 3, 3 }, cases.Select(c => c.G("attempts").GetInt32()).ToArray());
            Assert.Equal(new[] { "array", "array", "array" }, cases.Select(c => c.S("replyShape")).ToArray());
        }
    }

    [Fact]
    public void AI案例_對照組不選降級過的紀錄_即使已恢復成功且日期更近()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-C");
        h.AddDegraded(host, y.AddDays(-10), AiDegradation.ComposeHeadline("磁碟錯誤偏多", 1),
            LogAnalysisService.BuildAiFormatAnomalySummary(3, "{"), attempts: 1, pending: true);
        h.AddDegraded(host, y.AddDays(-9), "已恢復成功的正常標題", "補跑成功", attempts: 2, pending: false);    // 距離 1，但降級過
        h.AddRecord(host, y.AddDays(-7), "從未降級的正常標題");                                               // 距離 3

        var (root, _, doc) = h.Export(45);
        using (doc)
        {
            var only = Assert.Single(root.G("aiFormatAnomalies").G("cases").L());
            var control = only.G("control");
            Assert.Equal(y.AddDays(-7).ToString("yyyy-MM-dd"), control.S("date"));
            Assert.Equal(3, control.G("daysApart").GetInt32());
        }
    }

    [Fact]
    public void AI案例_降級紀錄的摘要沒有說明前綴時_次數與回覆與形狀都是null()
    {
        using var h = new RunExportHarness();
        var host = h.AddHost("SRV-AI-N");
        h.AddDegraded(host, RunExportHarness.Y.AddDays(-1), AiDegradation.ComposeHeadline("磁碟錯誤偏多", 1),
            "偵測與處置建議仍完整的規則層摘要", attempts: 1, pending: true);

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var only = Assert.Single(root.G("aiFormatAnomalies").G("cases").L());
            Assert.Equal(1, only.G("degradedAttempts").GetInt32());
            Assert.Equal(JsonValueKind.Null, only.G("attempts").ValueKind);
            Assert.Equal(JsonValueKind.Null, only.G("reply").ValueKind);
            Assert.Equal(JsonValueKind.Null, only.G("replyShape").ValueKind);
            Assert.False(only.G("cutOff").GetBoolean());
            Assert.Equal("unknown", Assert.Single(root.G("aiFormatAnomalies").G("summary").G("byReplyShape").L()).S("name"));
        }
    }

    [Fact]
    public void AI案例_guide寫明兩種來源_已恢復不算_以及兩種失敗率()
    {
        using var h = new RunExportHarness();
        h.AddRun();

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var guide = string.Join("\n", root.G("guide").L().Select(x => x.GetString()));
            var section = guide.Split('\n').Single(l => l.StartsWith("【aiFormatAnomalies】", StringComparison.Ordinal));
            Assert.Contains("degradedAttempts 大於 0 且仍未成功", section);
            Assert.Contains("歷史資料", section);
            Assert.Contains("已恢復的不算案例", section);
            Assert.Contains("abandoned", section);
            var rates = guide.Split('\n').Single(l => l.StartsWith("【兩種 AI 失敗率】", StringComparison.Ordinal));
            Assert.Contains("主機日層", rates);
            Assert.Contains("呼叫層", rates);
            Assert.Contains("aiFailures", rates);
        }
    }

    [Theory]
    [InlineData(3, "原文")]
    [InlineData(12, "")]
    public void 降級摘要前綴_產生與解析共用_來回一致(int attempts, string reply)
    {
        var summary = LogAnalysisService.BuildAiFormatAnomalySummary(attempts, reply);
        Assert.True(LogAnalysisService.TryParseAiFormatAnomalySummary(summary, out var parsed, out var rest));
        Assert.Equal(attempts, parsed);
        Assert.Equal(reply, rest);

        Assert.False(LogAnalysisService.TryParseAiFormatAnomalySummary("沒有前綴", out var none, out var all));
        Assert.Null(none);
        Assert.Equal("沒有前綴", all);

        Assert.True(LogAnalysisService.TryParseAiFormatAnomalySummary(
            LogAnalysisService.AiFormatAnomalySummaryPrefixHead + "X" + LogAnalysisService.AiFormatAnomalySummaryPrefixTail + "原文", out var bad, out var badRest));
        Assert.Null(bad);
        Assert.Equal("原文", badRest);
    }

    [Fact]
    public void AI案例_對照組_同主機最接近日期_同距離取較新_不選自己不跨主機_超過7天為null()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host1 = h.AddHost("SRV-AI-1");
        var host2 = h.AddHost("SRV-AI-2");
        h.AddRecord(host1, y.AddDays(-10), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{\"a\":1"));
        h.AddRecord(host1, y.AddDays(-20), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{\"b\":1"));
        h.AddRecord(host1, y.AddDays(-9), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{"));      // 另一個異常：不能當對照
        h.AddRecord(host2, y.AddDays(-30), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{\"c\":1"));

        h.AddRecord(host1, y.AddDays(-9), "今日狀況正常，無需處理", aiAnalyzed: false);          // 低風險跳過：不選
        h.AddRecord(host1, y.AddDays(-11), "AI 尚未完成的統計紀錄", aiAnalyzed: false);           // 未經 AI 分析：不選
        h.AddRecord(host2, y.AddDays(-10), "別台主機的正常紀錄，與 host1 案例距離 0");             // 跨主機：不選
        h.AddRecord(host1, y.AddDays(-12), "較舊的正常紀錄", issues: new List<LogIssueSignature>());
        h.AddRecord(host1, y.AddDays(-8), "較新的正常紀錄");                                      // 與 -12 同距離 2 天，取較新
        h.AddRecord(host2, y.AddDays(-22), "距離 host2 案例 8 天的正常紀錄");                      // 超過 7 天

        var (root, _, doc) = h.Export(45);
        using (doc)
        {
            var cases = root.G("aiFormatAnomalies").G("cases").L();
            Assert.Equal(4, cases.Count);
            JsonElement CaseOn(int daysAgo, string host) => cases.Single(c => c.S("date") == y.AddDays(-daysAgo).ToString("yyyy-MM-dd")
                                                                             && c.S("host") == host);

            var host1Token = cases.Single(c => c.S("date") == y.AddDays(-10).ToString("yyyy-MM-dd")).S("host");
            var host2Token = cases.Single(c => c.S("date") == y.AddDays(-30).ToString("yyyy-MM-dd")).S("host");
            Assert.NotEqual(host1Token, host2Token);

            var control = CaseOn(10, host1Token).G("control");
            Assert.Equal(y.AddDays(-8).ToString("yyyy-MM-dd"), control.S("date"));
            Assert.Equal(2, control.G("daysApart").GetInt32());
            Assert.Equal(1, control.G("features").G("issueCount").GetInt32());

            // 另一個異常的對照也是 -8（距離 1），不是 -9 的低風險跳過、也不是異常自己
            Assert.Equal(y.AddDays(-8).ToString("yyyy-MM-dd"), CaseOn(9, host1Token).G("control").S("date"));
            Assert.Equal(JsonValueKind.Null, CaseOn(20, host1Token).G("control").ValueKind);   // 前後 7 天內沒有
            Assert.Equal(JsonValueKind.Null, CaseOn(30, host2Token).G("control").ValueKind);   // 同主機距離 8 天；別台主機不換

            var comparison = root.G("aiFormatAnomalies").G("comparison");
            Assert.Equal(4, comparison.G("caseSamples").GetInt32());
            Assert.Equal(2, comparison.G("controlSamples").GetInt32());
            Assert.Equal(JsonValueKind.Array, comparison.G("features").ValueKind);
            var issueCount = comparison.G("features").L().Single(f => f.S("feature") == "issueCount");
            Assert.Equal(1.0, issueCount.G("caseMedian").GetDouble());
            Assert.Equal(1.0, issueCount.G("controlMedian").GetDouble());
        }
    }

    [Fact]
    public void AI案例_任一組樣本為0時comparison的features為null_並附樣本數_沒有案例時整個comparison為null()
    {
        using var h = new RunExportHarness();
        var host = h.AddHost("SRV-AI-1");
        h.AddRecord(host, RunExportHarness.Y.AddDays(-2), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{"));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var comparison = root.G("aiFormatAnomalies").G("comparison");
            Assert.Equal(1, comparison.G("caseSamples").GetInt32());
            Assert.Equal(0, comparison.G("controlSamples").GetInt32());
            Assert.Equal(JsonValueKind.Null, comparison.G("features").ValueKind);
        }
    }

    [Fact]
    public void AI案例_超過上限時omitted正確_input只給最新N個_詳情已清除的案例input與features為null()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-1");
        for (var i = 1; i <= 4; i++)
            h.AddRecord(host, y.AddDays(-i), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, $"{{\"n\":{i}"));
        h.AddRecord(host, y.AddDays(-40), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "會被清掉的詳情"));
        h.Records.PruneDetails(30);

        var (root, _, doc) = h.Export(60, new RunExportLimits { AiCases = 5, AiCaseInputs = 2 });
        using (doc)
        {
            var section = root.G("aiFormatAnomalies");
            Assert.Equal(5, section.G("caseCount").GetInt32());
            var cases = section.G("cases").L();
            Assert.Equal(5, cases.Count);
            Assert.Equal(2, cases.Count(c => c.G("input").ValueKind == JsonValueKind.Object));
            var pruned = Assert.Single(cases, c => c.G("detailPruned").GetBoolean());
            Assert.Equal(JsonValueKind.Null, pruned.G("input").ValueKind);
            Assert.Equal(JsonValueKind.Null, pruned.G("features").ValueKind);
            Assert.Equal(JsonValueKind.Null, pruned.G("reply").ValueKind);
            Assert.Equal(JsonValueKind.Null, pruned.G("replyShape").ValueKind);
            var omitted = root.G("meta").G("omitted").L().Single(o => o.S("item") == "aiFormatInputs");
            Assert.Equal(2, omitted.G("count").GetInt32());   // 5 個案例中 1 個詳情已清除，剩 4 個有輸入，只給 2 個
        }

        var (root2, _, doc2) = h.Export(60, new RunExportLimits { AiCases = 3 });
        using (doc2)
        {
            Assert.Equal(5, root2.G("aiFormatAnomalies").G("caseCount").GetInt32());
            Assert.Equal(3, root2.G("aiFormatAnomalies").G("cases").L().Count);
            var omitted = root2.G("meta").G("omitted").L().Single(o => o.S("item") == "aiFormatCases");
            Assert.Equal(2, omitted.G("count").GetInt32());
            Assert.Equal(3, omitted.G("limit").GetInt32());
        }
    }

    [Fact]
    public void AI案例_摘要分布與診斷統計_詳細診斷起始日()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var winHost = h.AddHost("WIN-1", os: WebHost.OsWindows);
        var linHost = h.AddHost("LIN-1", os: WebHost.OsLinux);
        h.AddRecord(winHost, y.AddDays(-1), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{"));
        h.AddRecord(linHost, y.AddDays(-1), Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "文字"));
        var run = h.AddRun();
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName, RunExportHarness.DiagnosticMessage("daily-20200101-h1", "DailyAiResult", "exhausted",
            new AiJsonAttemptDiagnostics { Attempt = 1, Category = "truncated", Head = "{" },
            new AiJsonAttemptDiagnostics { Attempt = 2, Category = "validation", Head = "{}" }),
            y.AddHours(3));
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName, RunExportHarness.DiagnosticMessage("deep-x", "DeepResult", "empty",
            new AiJsonAttemptDiagnostics { Attempt = 1, Category = "empty", Head = "" }),
            y.AddHours(4));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var section = root.G("aiFormatAnomalies");
            var summary = section.G("summary");
            Assert.Equal(new[] { "linux", "windows" }, summary.G("byOs").L().Select(x => x.S("name")).ToArray());
            Assert.Equal(1, summary.G("byDate").L().Count);
            Assert.Equal(2, summary.G("byDate")[0].G("count").GetInt32());
            Assert.Equal(new[] { "object", "text" }, summary.G("byReplyShape").L().Select(x => x.S("name")).ToArray());
            var diag = summary.G("diagnostics");
            Assert.Equal(2, diag.G("entries").GetInt32());
            Assert.Equal(new[] { "empty", "truncated", "validation" }, diag.G("byCategory").L().Select(x => x.S("name")).ToArray());
            Assert.Equal(new[] { "empty", "exhausted" }, diag.G("byEndedBy").L().Select(x => x.S("name")).ToArray());
            Assert.Equal(new[] { "DailyAiResult", "DeepResult" }, diag.G("byResponseType").L().Select(x => x.S("name")).ToArray());
            Assert.Equal(y.ToString("yyyy-MM-dd"), section.S("detailedDiagnosticsFirstSeen"));
            // 診斷行不進訊號組
            Assert.Empty(root.G("signalGroups").L());
        }
    }

    [Fact]
    public void AI診斷_recovered一筆_計入entries與byEndedBy並有起始日()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var run = h.AddRun();
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName, RunExportHarness.DiagnosticMessage("daily-20200101-h1", "DailyAiResult", "recovered",
            new AiJsonAttemptDiagnostics { Attempt = 1, Category = "empty", Head = "[" }),
            y.AddHours(3));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            var section = root.G("aiFormatAnomalies");
            var diag = section.G("summary").G("diagnostics");
            Assert.Equal(1, diag.G("entries").GetInt32());
            Assert.Contains("recovered", diag.G("byEndedBy").L().Select(x => x.S("name")));
            Assert.Equal(y.ToString("yyyy-MM-dd"), section.S("detailedDiagnosticsFirstSeen"));
        }
    }

    [Fact]
    public void 匯出meta_retiredUndecryptableSecrets存在_空陣列也輸出_guide有說明()
    {
        using var h = new RunExportHarness();
        h.AddRun();

        var (root, _, doc) = h.Export(masker: () => RunExportDeidentifier.Create(
            new RunExportDeidentifierSource { RetiredUndecryptableSecrets = new[] { "PRTG 密碼" } }));
        using (doc)
        {
            var d = root.GetProperty("meta").GetProperty("deidentification");
            Assert.Equal(new[] { "PRTG 密碼" }, d.GetProperty("retiredUndecryptableSecrets").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Empty(d.GetProperty("undecryptableSecrets").EnumerateArray());
            var g = root.G("guide").L().Select(x => x.GetString() ?? "").ToList();
            Assert.Contains(g, s => s.Contains("retiredUndecryptableSecrets") && s.Contains("不代表要重新設定"));
            Assert.Contains(g, s => s.Contains("prtgTargetSensors") && s.Contains("快照"));
        }

        var (root2, _, doc2) = h.Export(masker: () => RunExportDeidentifier.Create(new RunExportDeidentifierSource()));
        using (doc2)
        {
            Assert.Empty(root2.GetProperty("meta").GetProperty("deidentification").GetProperty("retiredUndecryptableSecrets").EnumerateArray());
        }
    }

    [Fact]
    public void AI案例_每個案例只保留對得上的診斷_最近的幾筆()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-AI-1");
        var date = y.AddDays(-2);
        h.AddRecord(host, date, Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "{"));
        var run = h.AddRun();
        var label = $"daily-{date:yyyyMMdd}-h{host.HostId}";
        for (var i = 0; i < 5; i++)
            h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName,
                RunExportHarness.DiagnosticMessage(label, attempts: new AiJsonAttemptDiagnostics { Attempt = 1, Category = "validation", Head = $"第{i}次" }),
                y.AddHours(2 + i));
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName,
            RunExportHarness.DiagnosticMessage($"daily-{date:yyyyMMdd}-h999999", attempts: new AiJsonAttemptDiagnostics { Attempt = 1, Category = "validation", Head = "OTHERHOST-HEAD" }));

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var diagnostics = root.G("aiFormatAnomalies").G("cases")[0].G("diagnostics").L();
            Assert.Equal(3, diagnostics.Count);
            Assert.Equal(new[] { "第4次", "第3次", "第2次" }, diagnostics.Select(d => d.G("attemptDetails")[0].S("head")).ToArray());
            Assert.DoesNotContain("OTHERHOST-HEAD", Encoding.UTF8.GetString(bytes));
            Assert.Equal(6, root.G("aiFormatAnomalies").G("summary").G("diagnostics").G("entries").GetInt32());
        }
    }

    [Fact]
    public void AI案例_診斷的空形狀_清洗前原文_思考長度_原文走遮罩_長度放行_舊格式缺欄為null()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var host = h.AddHost("SRV-RAW-1");
        var date = y.AddDays(-2);
        h.AddRecord(host, date, Anomaly, LogAnalysisService.BuildAiFormatAnomalySummary(3, "[]"));
        var run = h.AddRun();
        var label = $"daily-{date:yyyyMMdd}-h{host.HostId}";
        h.AddLog(run, "Warn", AiJsonDiagnostics.LoggerName, RunExportHarness.DiagnosticMessage(label, attempts: new[]
        {
            new AiJsonAttemptDiagnostics
            {
                Attempt = 1, Category = "empty", Head = "[]", EmptyShape = "array",
                RawChars = 1234, RawHead = "<|channel>thought 主機 SRV-RAW-1 從 10.8.8.8 連線<|channel>final<|message>[]",
                RawTail = "結尾 10.8.8.9", ReasoningChars = 77
            },
            // 不在封閉集合內的 emptyShape 當資料處理（遮罩）；沒有新欄位的舊格式嘗試，匯出缺欄為 null
            new AiJsonAttemptDiagnostics { Attempt = 2, Category = "empty", Head = "{}", EmptyShape = "形狀 10.8.8.7" },
            new AiJsonAttemptDiagnostics { Attempt = 3, Category = "no_json", Head = "文字" }
        }), y.AddHours(2));

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var original in new[] { "SRV-RAW-1", "10.8.8.8", "10.8.8.9", "10.8.8.7" })
                Assert.DoesNotContain(original, text, StringComparison.OrdinalIgnoreCase);

            var attempts = root.G("aiFormatAnomalies").G("cases")[0].G("diagnostics")[0].G("attemptDetails").L();
            Assert.Equal(3, attempts.Count);

            var first = attempts[0];
            Assert.Equal("array", first.S("emptyShape"));
            Assert.Equal(1234, first.G("rawChars").GetInt32());
            Assert.Equal(77, first.G("reasoningChars").GetInt32());
            Assert.Contains("<|channel>thought", first.S("rawHead")); // 內容保留，只遮識別資料
            Assert.Contains("host-", first.S("rawHead"));
            Assert.Contains("ip-", first.S("rawHead"));
            Assert.Contains("ip-", first.S("rawTail"));

            Assert.DoesNotContain("形狀 10.8.8.7", text);

            foreach (var key in new[] { "emptyShape", "rawChars", "rawHead", "rawTail", "reasoningChars" })
            {
                var present = attempts[2].TryGetProperty(key, out var value);
                Assert.True(!present || value.ValueKind == JsonValueKind.Null, key);
            }
        }
    }

    // ===== 純函式 =====

    [Fact]
    public void 中位數與P95_空樣本為null_偶數取平均_小數四位()
    {
        Assert.Null(RunExportService.Median(Array.Empty<double>()));
        Assert.Null(RunExportService.Percentile95(Array.Empty<double>()));
        Assert.Equal(2.5, RunExportService.Median(new[] { 4.0, 1.0, 2.0, 3.0 }));
        Assert.Equal(19.0, RunExportService.Percentile95(Enumerable.Range(1, 20).Select(x => (double)x).ToList()));
        Assert.Equal(0.3333, RunExportService.Round4(1.0 / 3));
        Assert.Equal(0.0, RunExportService.Round4(double.NaN));
    }

    [Fact]
    public void 截斷與清理_不從代理對中間切開_孤立代理字元被替換()
    {
        var text = new string('a', 4) + "😀😀";
        var cut = RunExportService.Cut(text, 5);   // 第 5 字是高代理，往內退一個
        Assert.Equal("aaaa…", cut);
        Assert.Equal("ab\uFFFDc", RunExportService.Clean("ab\uD83Dc"));
        Assert.Equal("😀", RunExportService.Clean("😀"));
        Assert.Equal("短", RunExportService.Cut("短", 10));
    }

    [Theory]
    [InlineData(null, "console")]
    [InlineData("", "console")]
    [InlineData("console", "console")]
    [InlineData("schedule", "schedule")]
    [InlineData("fetch-followup", "fetch-followup")]
    [InlineData("manual:alice", "manual")]
    [InlineData("api:xyz", "other")]
    public void 觸發類別只輸出類別不輸出帳號(string? trigger, string expected) =>
        Assert.Equal(expected, RunExportService.TriggerCategory(trigger));

    [Fact]
    public void 異常趟帶PRTG早退原因_走Text遮罩()
    {
        using var h = new RunExportHarness();
        var y = RunExportHarness.Y;
        var plain = h.AddRun("SRV-N1", y.AddHours(1), r =>
        {
            r.WarnCount = 1;
            r.PrtgOutcome = BatchRun.PrtgOutcomeDisabled;
            r.PrtgOutcomeNote = BatchRun.PrtgNoteUndecryptable;
        });
        // 原因字串萬一帶了位址之類的身分資訊，也必須和其他自由文字一樣被遮罩
        var leaky = h.AddRun("SRV-N2", y.AddHours(2), r =>
        {
            r.WarnCount = 1;
            r.PrtgOutcome = BatchRun.PrtgOutcomeDisabled;
            r.PrtgOutcomeNote = "憑證無法解密，來源 10.7.7.7";
        });
        var legacy = h.AddRun("SRV-N3", y.AddHours(3), r => { r.WarnCount = 1; r.PrtgOutcome = BatchRun.PrtgOutcomeDisabled; });

        var (root, bytes, doc) = h.Export();
        using (doc)
        {
            var byId = root.G("abnormalRuns").L().ToDictionary(a => a.G("runId").GetInt64());
            Assert.Equal(BatchRun.PrtgNoteUndecryptable, byId[plain.RunId].S("prtgOutcomeNote"));
            Assert.Equal("disabled", byId[plain.RunId].S("prtgOutcome"));

            var masked = byId[leaky.RunId].S("prtgOutcomeNote");
            Assert.StartsWith("憑證無法解密", masked);
            Assert.DoesNotContain("10.7.7.7", masked);
            Assert.DoesNotContain("10.7.7.7", Encoding.UTF8.GetString(bytes));

            Assert.Equal(JsonValueKind.Null, byId[legacy.RunId].G("prtgOutcomeNote").ValueKind);
        }
    }
}
