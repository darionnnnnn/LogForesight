using System.Globalization;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 4：PRTG 每日評估的重複載入。原則是不改結果，只減少重複讀取——
/// 結果面的對照在 <see cref="PrtgBatch4BaselineTests"/>（改動前錄下的基準），這裡量讀取次數與範圍。
/// 以資料庫 SELECT 的次數與參數為證據；不用全域累計計數（其他測試類會並行執行）。
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgBatch4RepeatedLoadTests : IDisposable
{
    private readonly PrtgBatch4Rig _rig = new();
    private readonly ITestOutputHelper _output;

    public PrtgBatch4RepeatedLoadTests(ITestOutputHelper output)
    {
        _output = output;
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _rig.Dispose();
    }

    private static bool IsStateChangeRead(string sql) => sql.Contains("lf_prtg_state_changes", StringComparison.Ordinal);

    /// <summary>感測器鏡像整份讀取（<c>GetSensorStatuses</c>）：同時選出名稱、型別與分類，且沒有 objid 集合條件。</summary>
    private static bool IsSensorMirrorRead(string sql) =>
        sql.Contains("lf_prtg_sensors", StringComparison.Ordinal) && sql.Contains("\"Category\"", StringComparison.Ordinal) &&
        sql.Contains("\"SensorType\"", StringComparison.Ordinal) && sql.Contains("\"Name\"", StringComparison.Ordinal) &&
        sql.Contains("\"Status\"", StringComparison.Ordinal) && !sql.Contains(" IN (", StringComparison.Ordinal) &&
        !sql.Contains("JOIN", StringComparison.Ordinal);

    /// <summary>狀態變更讀取的 [from, to) 範圍（查詢參數依序是起、迄）。</summary>
    private static List<(DateTime From, DateTime To, string Sql, List<object?> Params)> StateChangeReads(PrtgBatch4Rig.SelectRecorder recorder) =>
        recorder.Where(IsStateChangeRead)
            .Select(read =>
            {
                var dates = read.Params.OfType<DateTime>().ToList();
                Assert.Equal(2, dates.Count);
                return (dates[0], dates[1], read.Text, read.Params);
            }).ToList();

    private static void AssertEachCalendarDayReadOnce(IReadOnlyList<(DateTime From, DateTime To, string Sql, List<object?> Params)> reads,
        DateTime firstDay, DateTime lastDay)
    {
        Assert.All(reads, read => Assert.Equal(TimeSpan.FromDays(1), read.To - read.From));
        Assert.Equal(
            Enumerable.Range(0, (lastDay - firstDay).Days + 1).Select(n => firstDay.AddDays(n)).ToList(),
            reads.Select(read => read.From).Order().ToList());
    }

    // ---- A2：狀態變更每個日曆日只讀一次 ----

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(5, false)]
    public async Task A2_回望N天_狀態變更讀取N加1次_每個日曆日只在一次查詢的範圍內(int lookbackDays, bool withPolicy)
    {
        _rig.EnablePrtg(seedPolicy: withPolicy);
        _rig.Seed();
        _rig.Recorder.Reset();

        await _rig.RunPipelineAsync(lookbackDays);

        var reads = StateChangeReads(_rig.Recorder);
        Assert.Equal(lookbackDays + 1, reads.Count);   // 改動前是 lookbackDays 次、每次兩天（共讀 2×lookbackDays 個日曆日）
        AssertEachCalendarDayReadOnce(reads, _rig.D(lookbackDays), _rig.Newest);
        if (withPolicy)
        {
            // 篩選下推：集合參數帶的是政策清單，政策外感測器不會被讀回
            Assert.All(reads, read =>
            {
                Assert.Contains("json_each", read.Sql);
                Assert.Contains(read.Params, p => p is string text && text == "[101,102,201,202,203,205,301,302,303]");
            });
        }
        else
        {
            Assert.All(reads, read => Assert.DoesNotContain("json_each", read.Sql));   // 空清單＝不限
        }
        // 只投影規則評估讀的三個欄位
        Assert.All(reads, read =>
        {
            Assert.DoesNotContain("\"message\"", read.Sql);
            Assert.DoesNotContain("\"prev_status\"", read.Sql);
            Assert.DoesNotContain("\"quality\"", read.Sql);
        });
    }

    [Fact]
    public void A2_查詢方法_政策外感測器的列不被讀回_只含規則評估欄位_排序與舊方法相同()
    {
        _rig.EnablePrtg(seedPolicy: false);
        _rig.Seed();
        var store = _rig.Backend.PrtgStore();
        var from = _rig.D(3);
        var to = _rig.D(0).AddDays(1);
        var policy = PrtgBatch4Rig.PolicySensors;

        var narrowed = store.GetStateChangesForRuleEvaluation(from, to, policy);
        var unrestricted = store.GetStateChangesForRuleEvaluation(from, to, null);
        var legacy = store.GetStateChanges(from, to);

        Assert.DoesNotContain(narrowed, row => row.SensorObjid == PrtgBatch4Rig.PolicyExcludedSensor);
        Assert.Contains(unrestricted, row => row.SensorObjid == PrtgBatch4Rig.PolicyExcludedSensor);
        // 與舊讀法（整列、讀回後篩）在規則評估欄位上逐筆相同，順序也相同
        string Key(PrtgStateChangeRow row) => $"{row.SensorObjid}|{row.ChangedAt:O}|{row.Status}";
        Assert.Equal(legacy.Where(row => policy.Contains(row.SensorObjid)).Select(Key), narrowed.Select(Key));
        Assert.Equal(legacy.Select(Key), unrestricted.Select(Key));
        // 其他欄位不填：回傳物件不可當完整列用
        Assert.All(unrestricted, row =>
        {
            Assert.Null(row.Message);
            Assert.Null(row.PrevStatus);
            Assert.Equal(string.Empty, row.Quality);
            Assert.Equal(0, row.Id);
        });
        Assert.Empty(store.GetStateChangesForRuleEvaluation(from, to, Array.Empty<long>()));
    }

    // ---- 滑動窗口本身 ----

    private static PrtgStateChangeRow Row(long sensor, DateTime at, string status) =>
        new() { SensorObjid = sensor, ChangedAt = at, Status = status };

    private sealed class WindowProbe
    {
        public readonly List<(DateTime From, DateTime To, IReadOnlyCollection<long>? Ids)> Loads = [];

        public List<PrtgStateChangeRow> Load(DateTime from, DateTime to, IReadOnlyCollection<long>? ids)
        {
            Loads.Add((from, to, ids));
            // 每個日曆日兩個 sensor、各一列；依 sensor、時間排序（與資料庫查詢相同）
            return new[] { 7L, 3L }.Order().Select(sensor => Row(sensor, from.AddHours(1), $"{sensor}@{from:MMdd}")).ToList();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 窗口_五十六天逐日走訪_每個日曆日只載入一次且常駐不超過兩天(bool descending)
    {
        var probe = new WindowProbe();
        var window = new PrtgStateChangeWindow(probe.Load, null);
        var first = new DateTime(2026, 9, 1);
        var days = Enumerable.Range(0, 56).Select(n => first.AddDays(n)).ToList();
        if (descending) days.Reverse();

        foreach (var day in days)
        {
            var rows = window.ForDay(day);
            Assert.True(window.ResidentDays <= 2, $"常駐 {window.ResidentDays} 天");
            // 前一天＋當天，依 sensor 再依時間
            Assert.Equal(
                new[] { $"3@{day.AddDays(-1):MMdd}", $"3@{day:MMdd}", $"7@{day.AddDays(-1):MMdd}", $"7@{day:MMdd}" },
                rows.Select(r => r.Status));
        }

        Assert.Equal(57, probe.Loads.Count);
        Assert.Equal(57, probe.Loads.Select(l => l.From).Distinct().Count());
        Assert.All(probe.Loads, l => Assert.Equal(TimeSpan.FromDays(1), l.To - l.From));
    }

    [Fact]
    public void 窗口_不連續的日期也取得完整的前一天加當天()
    {
        var probe = new WindowProbe();
        var window = new PrtgStateChangeWindow(probe.Load, null);
        var day = new DateTime(2026, 9, 10);

        foreach (var d in new[] { day, day.AddDays(-2), day.AddDays(-4), day.AddDays(-2) })
        {
            var rows = window.ForDay(d);
            Assert.Equal(new[] { d.AddDays(-1), d }.SelectMany(x => new[] { $"3@{x:MMdd}" }).Concat(
                new[] { d.AddDays(-1), d }.Select(x => $"7@{x:MMdd}")), rows.Select(r => r.Status));
            Assert.True(window.ResidentDays <= 2);
        }
    }

    [Fact]
    public void 窗口_感測器清單空等於不限_非空原樣下推()
    {
        var empty = new WindowProbe();
        new PrtgStateChangeWindow(empty.Load, new HashSet<long>()).ForDay(new DateTime(2026, 9, 10));
        Assert.All(empty.Loads, l => Assert.Null(l.Ids));

        var narrowed = new WindowProbe();
        new PrtgStateChangeWindow(narrowed.Load, new HashSet<long> { 3 }).ForDay(new DateTime(2026, 9, 10));
        Assert.All(narrowed.Loads, l => Assert.Equal(new long[] { 3 }, l.Ids));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(56, false)]
    public void A1_窗口讀到的資料餵給評估器_與舊讀法兩天整列的finding逐筆相同(int days, bool withPolicy)
    {
        _rig.EnablePrtg(seedPolicy: false);
        _rig.Seed();
        var store = _rig.Backend.PrtgStore();
        var statuses = store.GetSensorStatuses();
        var inputs = statuses.Select(s => new PrtgSensorStatusInput(s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Category)).ToList();
        var sensorToDevice = statuses.ToDictionary(s => s.Objid, s => s.DeviceObjid);
        var names = statuses.ToDictionary(s => s.Objid, s => s.SensorName);
        var deviceNames = store.GetDeviceIdNames();
        var rules = KnownIssueCatalog.Rules.Where(r => r.Platform == "prtg" && r.Enabled).ToList();
        var policy = withPolicy ? PrtgBatch4Rig.PolicySensors.ToHashSet() : new HashSet<long>();
        var lookups = PrtgSensorLookups.Build(inputs);
        var window = new PrtgStateChangeWindow(store, policy);

        string Signature(DateTime day, IReadOnlyList<PrtgStateChangeRow> changes) => string.Join("\n",
            PrtgRuleEvaluator.Evaluate(day, changes, sensorToDevice, inputs, rules, names, deviceNames,
                includeSilent: day == _rig.Newest, lookups: lookups)
            .Select(f => $"{f.DeviceObjid}|{f.SensorObjid}|{f.RuleCode}|{f.Magnitude}|{f.Acknowledged}|{f.SensorCategory}|{f.Detail}"));

        var total = 0;
        foreach (var offset in Enumerable.Range(0, days))   // 由近到遠，與每日管線相同
        {
            var day = _rig.D(offset);
            var legacy = store.GetStateChanges(day.AddDays(-1), day.AddDays(1))
                .Where(change => policy.Count == 0 || policy.Contains(change.SensorObjid)).ToList();
            var expected = Signature(day, legacy);
            Assert.Equal(expected, Signature(day, window.ForDay(day)));
            total += expected.Length;
        }
        Assert.True(total > 0, "測試資料沒有產生任何 finding，比對會空轉");
    }

    // ---- A4：感測器鏡像一趟讀一次 ----

    private static KnownIssueRule TrendRule(string ruleCode, string category, PrtgTrendThresholds? trend, PrtgDiskTrendThresholds? disk) => new()
    {
        Id = "t-trend-" + ruleCode, Platform = "prtg", PrtgRuleCode = ruleCode, PrtgThreshold = 0,
        PrtgTrendThresholds = trend, PrtgDiskTrendThresholds = disk, PrtgSensorCategory = category,
        Enabled = true, Severity = IssueSeverity.Medium, ElevatesDayRisk = false, Description = "trend fixture"
    };

    /// <summary>啟用磁碟、記憶體、CPU、Ping 四條趨勢規則（種子預設停用），讓每日路徑的四條趨勢都走候選與評估。</summary>
    private static void EnableTrendRules()
    {
        var trendCodes = new[]
        {
            PrtgRuleEvaluator.RuleDiskFreeTrend, PrtgRuleEvaluator.RuleMemoryUsedTrend,
            PrtgRuleEvaluator.RuleCpuLoadTrend, PrtgRuleEvaluator.RulePingLatencyTrend
        };
        var rules = KnownIssueSeed.CreateRules().Where(r => !trendCodes.Contains(r.PrtgRuleCode ?? "")).ToList();
        rules.Add(TrendRule(PrtgRuleEvaluator.RuleDiskFreeTrend, PrtgSensorCategories.Disk, null, PrtgDiskTrendThresholds.Provisional));
        rules.Add(TrendRule(PrtgRuleEvaluator.RuleMemoryUsedTrend, PrtgSensorCategories.Memory,
            PrtgTrendThresholds.ProvisionalFor(PrtgRuleEvaluator.RuleMemoryUsedTrend), null));
        rules.Add(TrendRule(PrtgRuleEvaluator.RuleCpuLoadTrend, PrtgSensorCategories.Cpu,
            PrtgTrendThresholds.ProvisionalFor(PrtgRuleEvaluator.RuleCpuLoadTrend), null));
        rules.Add(TrendRule(PrtgRuleEvaluator.RulePingLatencyTrend, PrtgSensorCategories.Availability,
            PrtgTrendThresholds.ProvisionalFor(PrtgRuleEvaluator.RulePingLatencyTrend), null));
        KnownIssueCatalog.Initialize(rules);
    }

    [Fact]
    public async Task A4_每日路徑啟用四條趨勢規則_感測器鏡像整份只讀一次_裝置只取編號與名稱()
    {
        EnableTrendRules();
        _rig.EnablePrtg(seedPolicy: true);
        _rig.Seed();
        _rig.Recorder.Reset();

        await _rig.RunPipelineAsync(2);

        // 開頭一次；就緒度與四條趨勢沿用，不再各自重讀（改動前共 6 次）
        Assert.Single(_rig.Recorder.Where(IsSensorMirrorRead));
        // 裝置鏡像：只選編號與名稱，沒有標籤、群組路徑
        var deviceReads = _rig.Recorder.Where(sql => sql.Contains("lf_prtg_devices", StringComparison.Ordinal));
        Assert.NotEmpty(deviceReads);
        Assert.All(deviceReads, read =>
        {
            Assert.DoesNotContain("\"tags\"", read.Text);
            Assert.DoesNotContain("\"group_path\"", read.Text);
        });
    }

    private void SeedDiskValues(params long[] sensors)
    {
        var store = _rig.Backend.PrtgStore();
        var rows = new List<PrtgValueRow>();
        foreach (var sensor in sensors)
            for (var d = 0; d < 10; d++)
                for (var h = 0; h < 24; h++)
                    rows.Add(new PrtgValueRow
                    {
                        SensorObjid = sensor, PeriodStart = _rig.D(d).AddHours(h), AvgValue = 18 + d * 0.1,
                        Coverage = 100, Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Now
                    });
        store.UpsertValues(rows);
    }

    private (IReadOnlyDictionary<long, long> DeviceToHost, HashSet<long> HostIds) MappedDevices() =>
        (_rig.HostRows.Select((host, i) => (Device: (long)i + 1, host.HostId)).ToDictionary(x => x.Device, x => x.HostId),
         _rig.HostRows.Select(host => host.HostId).ToHashSet());

    [Fact]
    public void A4_就緒度_傳入鏡像與自己讀結果相同_且傳入時不再讀鏡像()
    {
        _rig.EnablePrtg(seedPolicy: false);
        _rig.Seed();
        SeedDiskValues(102, 202, 203);
        var store = _rig.Backend.PrtgStore();
        var (deviceToHost, hostIds) = MappedDevices();
        var assessment = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>());
        var day = DateOnly.FromDateTime(_rig.Newest);
        var mirror = assessment.SensorMirror();

        _rig.Recorder.Reset();
        var selfRead = PrtgValueReadiness.Compute(assessment, deviceToHost, hostIds, new HashSet<long>(), day, CancellationToken.None);
        var selfReadMirrorReads = _rig.Recorder.Where(IsSensorMirrorRead).Count;
        _rig.Recorder.Reset();
        var shared = PrtgValueReadiness.Compute(assessment, deviceToHost, hostIds, new HashSet<long>(), day, CancellationToken.None, mirror);
        var sharedMirrorReads = _rig.Recorder.Where(IsSensorMirrorRead).Count;

        Assert.True(selfRead.Population > 0);
        Assert.Equal(1, selfReadMirrorReads);
        Assert.Equal(0, sharedMirrorReads);
        Assert.Equal(selfRead.Population, shared.Population);
        Assert.Equal(selfRead.Counts.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
            shared.Counts.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        Assert.Equal(selfRead.Rows.Select(r => (r.Sensor.SensorObjid, r.Sensor.HostId, r.Reason)),
            shared.Rows.Select(r => (r.Sensor.SensorObjid, r.Sensor.HostId, r.Reason)));
    }

    [Fact]
    public void A4_持平證據_傳入鏡像與自己讀結果相同_且傳入時不再讀鏡像()
    {
        _rig.EnablePrtg(seedPolicy: false);
        _rig.Seed();
        SeedDiskValues(203);
        var store = _rig.Backend.PrtgStore();
        var (deviceToHost, hostIds) = MappedDevices();
        var day = _rig.Newest;
        var signature = new LogIssueSignature
        {
            LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:warning", EventId = 0, EntryType = LogEntryType.Warning,
            EventKey = "prtg:warning:203", SampleMessages = ["原始說明"], Severity = IssueSeverity.Medium
        };
        var history = new Dictionary<string, HashSet<DateTime>>
        {
            [signature.EventKey] = Enumerable.Range(1, 9).Select(n => day.AddDays(-n)).ToHashSet()
        };
        var mirror = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>()).SensorMirror();

        _rig.Recorder.Reset();
        var selfRead = PrtgDailyPipeline.BuildSteadyEvidence([signature], history, day, deviceToHost, hostIds, store,
            new Dictionary<string, PrtgValueSemantic>(), CancellationToken.None);
        var selfReadMirrorReads = _rig.Recorder.Where(IsSensorMirrorRead).Count;
        _rig.Recorder.Reset();
        var shared = PrtgDailyPipeline.BuildSteadyEvidence([signature], history, day, deviceToHost, hostIds, store,
            new Dictionary<string, PrtgValueSemantic>(), CancellationToken.None, mirror);
        var sharedMirrorReads = _rig.Recorder.Where(IsSensorMirrorRead).Count;

        Assert.NotEmpty(selfRead);
        Assert.Equal(1, selfReadMirrorReads);
        Assert.Equal(0, sharedMirrorReads);
        Assert.Equal(selfRead.Keys.Order(), shared.Keys.Order());
        foreach (var key in selfRead.Keys) Assert.Equal(selfRead[key], shared[key]);
    }

    [Fact]
    public void A4_裝置編號與名稱查詢_與整列讀取後取每個編號第一筆相同()
    {
        _rig.EnablePrtg(seedPolicy: false);
        _rig.Seed();
        var store = _rig.Backend.PrtgStore();

        var expected = store.GetAllDevices().GroupBy(d => d.Objid).ToDictionary(g => g.Key, g => g.First().Name);

        Assert.Equal(expected.OrderBy(p => p.Key), store.GetDeviceIdNames().OrderBy(p => p.Key));
        Assert.Equal(3, expected.Count);
    }
}

/// <summary>校準逐日讀取：判定快取是行程層級靜態欄位，要與其他碰它的測試序列化。</summary>
[Collection("CalibrationCacheState")]
public sealed class PrtgBatch4CalibrationReadTests
{
    [Fact]
    public void A3_校準五十六天窗口_狀態變更讀五十七次_每個日曆日只讀一次且篩選下推()
    {
        var recorder = new PrtgBatch4Rig.SelectRecorder();
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        LfDbContext NewContext() =>
            new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var ctx = NewContext()) ctx.Database.EnsureCreated();

        var signature = PrtgBatch4Rig.BuildCalibrationSignature(NewContext);

        Assert.Equal(PrtgBatch4Golden.Calibration, signature);   // 結果與改動前相同
        var reads = recorder.Where(sql => sql.Contains("lf_prtg_state_changes", StringComparison.Ordinal)
                                          && sql.Contains("json_each", StringComparison.Ordinal))
            .Select(read =>
            {
                var dates = read.Params.OfType<DateTime>().ToList();
                return (From: dates[0], To: dates[1], read.Params);
            })
            // 校準另有一條涵蓋整個窗口的彙總查詢（逐日重放以外的既有讀取），不在逐日讀取之列
            .Where(read => read.To - read.From <= TimeSpan.FromDays(2)).ToList();
        var anchor = new DateTime(2026, 9, 1);
        var first = anchor.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1)).AddDays(-1);   // 窗口起點的前一天
        Assert.Equal(CalibrationConstants.RuleThresholdWindowDays + 1, reads.Count);   // 改動前是 56 次、每次兩天
        Assert.All(reads, read => Assert.Equal(TimeSpan.FromDays(1), read.To - read.From));
        Assert.Equal(Enumerable.Range(0, reads.Count).Select(n => first.AddDays(n)), reads.Select(read => read.From).Order());
        Assert.All(reads, read => Assert.Contains(read.Params, p => p is string text && text == "[1001,1002]"));
    }
}
