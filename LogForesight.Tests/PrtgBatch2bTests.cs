using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 2b：PRTG 路徑的單點等價最佳化（2-5 清除、2-6 重評版本戳、2-7 有效對應日期、2-8 規則評估）。
/// 「改動前」的數字由 HEAD eacb2448 的乾淨副本跑 <see cref="PrtgBatch2bScenarios"/> 取得（<see cref="PrtgBatch2bGoldens"/>）。
/// </summary>
public sealed class PrtgBatch2bTests
{
    // ---------- A1：範圍外清除 ----------

    [Fact]
    public void A1_候選為空_最終狀態與計數同改動前_不讀兩張資料表_一批走完()
    {
        var (final, rows, batches, reads) = PrtgBatch2bScenarios.PurgeEmptyCandidates();
        Assert.Equal(PrtgBatch2bGoldens.PurgeEmpty, final + "\n" + rows);
        Assert.Equal(0, reads);      // 改動前 6
        Assert.Equal(1, batches);    // 改動前 3
    }

    [Fact]
    public void A1_候選非空_刪除結果游標最終狀態與批次數同改動前()
    {
        var (final, rows, batches, reads) = PrtgBatch2bScenarios.PurgeWithCandidates();
        Assert.Equal(PrtgBatch2bGoldens.PurgeCand, final + "\n" + rows);
        Assert.Equal(3, batches);
        Assert.Equal(8, reads);
        Assert.Equal(PrtgBatch2bGoldens.PurgeCandFirst, PrtgBatch2bScenarios.PurgeWithCandidatesFirstBatch());
    }

    [Fact]
    public void A1_候選為空但範圍指紋已變_照舊停止且不清除()
    {
        var (final, rows, reads) = PrtgBatch2bScenarios.PurgeEmptyCandidatesScopeChanged();
        Assert.Equal(PrtgBatch2bGoldens.PurgeEmptyScope, final + "\n" + rows);
        Assert.Equal(0, reads);
    }

    // ---------- A2：歷史弱佐證重評 ----------

    [Fact]
    public void A2_一批全無內容變動_回報改寫0_處理數照舊_最終列與改動前相同()
    {
        using var fx = new EfSqliteFixture();
        PrtgBatch2bScenarios.SeedRisk(fx.NewContext, 3, legacyLast: false);
        var result = new PrtgRiskProjectionStore(fx.NewContext).RunBatchDetailed();
        Assert.Equal(new PrtgRiskBatchResult(3, 0), result);
        Assert.Equal(0, new PrtgRiskProjectionStore(fx.NewContext).RunBatch());
    }

    [Fact]
    public void A2_其中一筆有變動_改寫1_RunBatch回傳值語意不變_資料列同改動前()
    {
        using var fx = new EfSqliteFixture();
        PrtgBatch2bScenarios.SeedRisk(fx.NewContext, 3, legacyLast: true);
        var store = new PrtgRiskProjectionStore(fx.NewContext);
        Assert.Equal(new PrtgRiskBatchResult(4, 1), store.RunBatchDetailed());
        Assert.Equal(PrtgBatch2bGoldens.Risk, PrtgBatch2bScenarios.DumpRisk(fx.NewContext));

        using var fx2 = new EfSqliteFixture();
        PrtgBatch2bScenarios.SeedRisk(fx2.NewContext, 3, legacyLast: true);
        Assert.Equal(4, new PrtgRiskProjectionStore(fx2.NewContext).RunBatch());
    }

    private sealed class Env : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lf-2b-risk-" + Guid.NewGuid().ToString("N"));
        public StorageBackend Backend { get; }
        public Env()
        {
            Directory.CreateDirectory(Dir);
            Backend = new StorageBackend(new StorageSettings
            { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(Dir, "test.db")}" }, Dir);
        }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static IEnumerable<long> AllHostIds(int plain) => Enumerable.Range(0, plain).Select(i => 1000L + i).Append(101);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void A2_控制器_只有內容改寫才推進版本戳(bool legacy, int expectedBumps)
    {
        using var env = new Env();
        PrtgBatch2bScenarios.SeedRisk(env.Backend.CreateContext, 3, legacy);
        var stamp = new DataVersionStamp();
        var controller = new PrtgRiskReviewController(env.Backend, new FixedVisibilityService(AllHostIds(3)), stamp);
        controller.Retry();
        Assert.Equal(expectedBumps, stamp.Current);
        // 處理過的列最終都已標記完成
        using var read = env.Backend.CreateContext();
        Assert.DoesNotContain(read.DailyRecords, r => r.RiskProjectionVersion < PrtgHistoricalRiskReview.Version);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task A2_背景服務_候選未處理完不提早結束_只有內容改寫才推進版本戳(bool legacyLast, int expectedBumps)
    {
        using var env = new Env();
        // 101 筆無變動 + （可選）最後一筆需改寫：第一批 100 筆處理過但改寫 0，服務必須繼續第二批
        PrtgBatch2bScenarios.SeedRisk(env.Backend.CreateContext, 101, legacyLast);
        var stamp = new DataVersionStamp();
        var gate = new BackgroundWorkGate(new SchedulerRunState(), TimeSpan.FromMilliseconds(10));
        using var service = new PrtgRiskReviewHostedService(env.Backend, gate, stamp, TimeSpan.Zero);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(expectedBumps, stamp.Current);
        using var read = env.Backend.CreateContext();
        Assert.DoesNotContain(read.DailyRecords, r => r.RiskProjectionVersion < PrtgHistoricalRiskReview.Version);
        Assert.Equal(legacyLast ? 1 : 0, read.DailyRecords.Count(r => r.RiskReviewStatus != "current"));
    }

    // ---------- A3：數值評估 ----------

    private static readonly (int Window, int Required, bool NewestSame)[] Shapes = [(28, 28, false), (14, 5, true), (28, 1, false)];

    [Fact]
    public void A3_就緒度與每頁呼叫的結果同預設參數逐筆相同_有效對應日期查詢1次()
    {
        using var env = new PrtgBatch2bScenarios.AssessEnv();
        Assert.True(env.Sensors.Count > PrtgValueAssessment.PageSize * 2); // 至少三頁
        env.Recorder.Clear();
        var readiness = PrtgBatch2bScenarios.Readiness(env);
        Assert.Equal(1, env.OkDateQueries()); // 改動前 3

        env.Recorder.Clear();
        var rowsDefault = PrtgBatch2bScenarios.AssessAllPagesDefault(env, 28, 28);
        Assert.Equal(3, env.OkDateQueries()); // 不傳參數＝舊行為，每頁各查一次
        Assert.EndsWith(rowsDefault, readiness);
    }

    [Fact]
    public void A3_呼叫端迴圈外算一次傳入_四種觀察窗結果逐筆相同_查詢1次()
    {
        using var env = new PrtgBatch2bScenarios.AssessEnv();
        var assessment = env.Assessment();
        foreach (var (window, required, newestSame) in Shapes)
        {
            DateOnly? newest = newestSame ? PrtgBatch2bScenarios.AssessEnv.Completed : null;
            var expected = PrtgBatch2bScenarios.AssessAllPagesDefault(env, window, required, newest);

            env.Recorder.Clear();
            var newestMap = assessment.ResolveNewestHostMap(PrtgBatch2bScenarios.AssessEnv.Completed);
            var okDates = assessment.ResolveOkHostMapDates(PrtgBatch2bScenarios.AssessEnv.Completed, window);
            var rows = new List<PrtgValueAssessmentRow>();
            foreach (var page in env.Sensors.Chunk(PrtgValueAssessment.PageSize))
                rows.AddRange(assessment.AssessPage(page, PrtgBatch2bScenarios.AssessEnv.Completed, window, required, newest,
                    newestMap, okDates));
            Assert.Equal(1, env.OkDateQueries());
            Assert.Equal(expected, PrtgBatch2bScenarios.AssessEnv.Fmt(rows));
        }
    }

    [Fact]
    public void A3_沒有候選時就緒度不查有效對應日期()
    {
        using var env = new PrtgBatch2bScenarios.AssessEnv(sensorCount: 4);
        env.Recorder.Clear();
        var summary = PrtgValueReadiness.Compute(env.Assessment(), new Dictionary<long, long>(), new HashSet<long>(),
            new HashSet<long>(), PrtgBatch2bScenarios.AssessEnv.Completed, CancellationToken.None);
        Assert.Equal(0, summary.Population);
        Assert.Equal(0, env.OkDateQueries());
    }

    // ---------- A4：規則評估 ----------

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void A4_回望1天與5天_同裝置多顆與同代碼多規則的finding與順序同改動前(int days, bool withLookups)
    {
        var expected = days == 1 ? PrtgBatch2bGoldens.Eval1 : PrtgBatch2bGoldens.Eval5;
        Assert.Equal(expected.TrimEnd(), PrtgBatch2bScenarios.EvaluateDays(days, withLookups).TrimEnd());
    }

    [Fact]
    public void A4_同Objid重複時預建對照表與現場自建一致_取第一筆()
    {
        var (day, changes, sensorToDevice, statuses, rules) = PrtgBatch2bScenarios.FoldScenario();
        // 同一 sensor 以不同分類／類型重複出現：兩條路徑都取第一筆
        statuses.Add(new PrtgSensorStatusInput(11, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic));
        statuses.Add(new PrtgSensorStatusInput(13, 1, "Down", "Ping", PrtgSensorCategories.Availability));
        var names = new Dictionary<long, string>();
        var plain = PrtgBatch2bScenarios.FormatFindings(PrtgRuleEvaluator.Evaluate(
            day, changes, sensorToDevice, statuses, rules, names, names));
        var lookups = PrtgSensorLookups.Build(statuses);
        var prebuilt = PrtgBatch2bScenarios.FormatFindings(PrtgRuleEvaluator.Evaluate(
            day, changes, sensorToDevice, statuses, rules, names, names, lookups: lookups));
        Assert.Equal(plain, prebuilt);
        Assert.Equal(PrtgSensorCategories.Availability, lookups.Categories[11]);
        Assert.Equal("SNMP Traffic 64bit", lookups.Types[13]);
    }

    [Fact]
    public void A4_傳入預建對照表時評估採用它_不再自己重建()
    {
        var (day, changes, sensorToDevice, statuses, rules) = PrtgBatch2bScenarios.FoldScenario();
        var names = new Dictionary<long, string>();
        var normal = PrtgBatch2bScenarios.FormatFindings(PrtgRuleEvaluator.Evaluate(
            day, changes, sensorToDevice, statuses, rules, names, names));
        // 對照表來自「沒有任何 availability」的 sensor 現況：同裝置不折疊，與一般結果必然不同
        var altered = statuses.Select(s => s with { Category = PrtgSensorCategories.Traffic }).ToList();
        var withAltered = PrtgBatch2bScenarios.FormatFindings(PrtgRuleEvaluator.Evaluate(
            day, changes, sensorToDevice, statuses, rules, names, names, lookups: PrtgSensorLookups.Build(altered)));
        Assert.NotEqual(normal, withAltered);
        Assert.Contains("merged=0", withAltered);
    }
}
