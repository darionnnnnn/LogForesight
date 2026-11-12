using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 體檢與本機分析同進退（第 70 輪 C-3）：本趟沒有執行本機分析、或基準日（昨天）沒有本機分析紀錄時，
/// 體檢整段略過（不呼叫 AI、不記 Warn）。重現案例：本機分析沒產出、體檢照跑、
/// <c>AttachWeeklyCheckup</c> 掛不上記 Warn、「上次體檢日」沒更新、下一趟又跑。
/// </summary>
public class CheckupSkipTests
{
    // ── 判定函式：唯一判定點 ──────────────────────────────────────────

    [Fact]
    public void 本趟沒有執行本機分析_略過體檢並說明原因()
    {
        var reason = AnalysisOrchestrator.LocalCheckupSkipReason(5, false, localAnalysisRan: false, baselineRecordExists: true);
        Assert.NotNull(reason);
        Assert.Contains("本機分析", reason);
    }

    [Fact]
    public void 基準日沒有本機分析紀錄_略過體檢並說明原因()
    {
        var reason = AnalysisOrchestrator.LocalCheckupSkipReason(5, false, localAnalysisRan: true, baselineRecordExists: false);
        Assert.NotNull(reason);
        Assert.Contains("基準日", reason);
    }

    [Fact]
    public void 本機分析有跑且基準日有紀錄_可以跑體檢()
    {
        Assert.Null(AnalysisOrchestrator.LocalCheckupSkipReason(5, false, localAnalysisRan: true, baselineRecordExists: true));
    }

    [Fact]
    public void 既有略過原因優先且訊息不變()
    {
        Assert.Equal("本機主機未登記，略過體檢",
            AnalysisOrchestrator.LocalCheckupSkipReason(0, false, localAnalysisRan: false, baselineRecordExists: false));
        Assert.Equal("本機主機已停用，略過體檢（體檢以本機主機為對象）",
            AnalysisOrchestrator.LocalCheckupSkipReason(5, true, localAnalysisRan: false, baselineRecordExists: false));
    }

    // ── 整趟：真實 Orchestrator ──────────────────────────────────────

    private sealed class EmptyCandidateSource : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
    }

    private sealed class Console : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    /// <summary>
    /// 本機主機已登記且啟用、近期有一筆分析紀錄（<paramref name="seedDaysAgo"/> 天前；null＝不寫），
    /// 再跑一趟真的 Orchestrator（AI 未設定）。回傳體檢階段、該趟 Warn 訊息與基準日紀錄。
    /// </summary>
    private static async Task<(RunPhaseRecord? Checkup, List<string> Warns, DailyAnalysisRecord? Baseline)> RunAsync(
        RunRequest request, int? seedDaysAgo)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-test-checkup-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AppSettings
            {
                Ai = new AiSettings { BaseUrl = "" },
                Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False" }
            };
            var backend = new StorageBackend(settings.Storage, dir, 1);
            var host = new HostStore(backend.Blob("hosts")).Touch(Environment.MachineName, DateTime.Now);
            var owner = new HostKey { HostId = host.HostId, HostName = Environment.MachineName };
            var yesterday = DateTime.Today.AddDays(-1);
            if (seedDaysAgo is { } ago)
            {
                backend.RecordStore(owner).Append(new DailyAnalysisRecord
                {
                    HostId = host.HostId, Host = Environment.MachineName, Date = DateTime.Today.AddDays(-ago),
                    RiskLevel = RiskLevels.Low, TopIssues = []
                });
            }

            var orchestrator = new AnalysisOrchestrator(new EmptyCandidateSource(), NullAiRequestGate.Instance);
            var result = await orchestrator.RunAsync(request, settings, dir, new RetentionOptions(), new Console(),
                CancellationToken.None);
            Assert.True(result.Success, result.FailureMessage);

            var store = new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
            var run = store.GetRun(result.BatchRunId!.Value)!;
            var warns = store.GetLogs(run.RunId).Where(l => l.Level == "Warn").Select(l => l.Message).ToList();
            var baseline = backend.RecordStore(owner).ReadRecent(yesterday, 1).FirstOrDefault(r => r.Date.Date == yesterday);
            return (run.Phases!.FirstOrDefault(p => p.Key == RunTimingPhases.Checkup), warns, baseline);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 重現_本機分析沒跑_體檢整段略過且不記Warn()
    {
        // 近期有紀錄（HasAnyRecord 為真、從未體檢過，舊行為 ShouldRun 為真），但本趟不分析本機、基準日沒有紀錄
        var (checkup, warns, baseline) = await RunAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false }, seedDaysAgo: 3);

        Assert.Null(baseline);
        Assert.DoesNotContain(warns, w => w.Contains("AttachWeeklyCheckup"));
        Assert.NotNull(checkup);
        Assert.Equal(RunPhaseOutcomes.Skipped, checkup!.Outcome);
    }

    [Fact]
    public async Task 指定NetIQ主機範圍_不動本機_體檢略過且不記Warn()
    {
        var (checkup, warns, _) = await RunAsync(
            new RunRequest { Scope = RunScope.NetiqHosts, IncludeLocal = true }, seedDaysAgo: 3);

        Assert.NotNull(checkup);
        Assert.Equal(RunPhaseOutcomes.Skipped, checkup!.Outcome);
        Assert.DoesNotContain(warns, w => w.Contains("AttachWeeklyCheckup"));
    }

    [Fact]
    public async Task 本機分析有跑且基準日有紀錄_體檢照跑並掛上()
    {
        // 昨天已有紀錄、回望 1 天 → 本機「無需回補」但這趟確實執行了本機分析
        var (checkup, warns, baseline) = await RunAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = true, BackfillOverride = 1 }, seedDaysAgo: 1);

        Assert.NotNull(checkup);
        Assert.NotEqual(RunPhaseOutcomes.Skipped, checkup!.Outcome);
        Assert.DoesNotContain(warns, w => w.Contains("AttachWeeklyCheckup"));
        Assert.NotNull(baseline?.WeeklyCheckup);
    }
}
