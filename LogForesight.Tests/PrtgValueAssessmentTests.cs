using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 數值可信三條件（對應有效＋品質可用＋單位可判）與合理性檢查（docs/PRTG-SPEC.md §9b）。
/// 固定小集合：兩台主機、CPU／記憶體／磁碟各一顆，逐小時 ok 列。
/// </summary>
public sealed class PrtgValueAssessmentTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfPrtgStore _store;
    private static readonly DateOnly Completed = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

    public PrtgValueAssessmentTests()
    {
        _store = new EfPrtgStore(_fx.NewContext);
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedSensors(params (long Objid, long Device, string Type, string Category)[] sensors) =>
        _store.UpsertSensors(sensors.Select(s => new PrtgSensorRow
        {
            Objid = s.Objid, DeviceObjid = s.Device, Name = $"S{s.Objid}", SensorType = s.Type,
            Category = s.Category, Status = "Up"
        }).ToList(), DateTime.Now);

    private void MapDays(int days, params (long Device, long Host)[] maps)
    {
        for (var d = 0; d < days; d++)
        {
            var date = Completed.AddDays(-d).ToDateTime(TimeOnly.MinValue);
            _store.ReplaceHostMapForDate(date, maps.Select(m => new PrtgHostMapRow
            {
                DeviceObjid = m.Device, HostId = m.Host, HostName = $"h{m.Host}", MapStatus = PrtgMapStatus.Ok
            }).ToList());
        }
    }

    private void SeedHours(long sensor, int days, Func<int, int, double> value, string quality = PrtgDataQuality.Ok,
        double? coverage = 100)
    {
        var rows = new List<PrtgValueRow>();
        for (var d = 0; d < days; d++)
        for (var h = 0; h < 24; h++)
            rows.Add(new PrtgValueRow
            {
                SensorObjid = sensor,
                PeriodStart = Completed.AddDays(-d).ToDateTime(new TimeOnly(h, 0)),
                AvgValue = value(d, h), Coverage = coverage, Quality = quality, CreatedAt = DateTime.Now
            });
        _store.UpsertValues(rows);
    }

    private IReadOnlyList<PrtgValueAssessmentRow> Assess(int window = 3, int required = 3, string? category = null,
        IReadOnlyDictionary<string, PrtgValueSemantic>? overrides = null)
    {
        var assessment = new PrtgValueAssessment(_store, overrides ?? new Dictionary<string, PrtgValueSemantic>());
        var deviceToHost = new Dictionary<long, long> { [1] = 11, [2] = 12 };
        var candidates = assessment.Candidates(deviceToHost, new HashSet<long> { 11, 12 }, new HashSet<long>(), category);
        return assessment.AssessPage(candidates.ToList(), Completed, window, required);
    }

    [Fact]
    public void 三顆對應有效且三日ok列_全部就緒()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (102, 1, "SNMP Memory", PrtgSensorCategories.Memory),
            (201, 2, "SNMP Disk Free", PrtgSensorCategories.Disk));
        MapDays(3, (1, 11), (2, 12));
        SeedHours(101, 3, (d, h) => 10 + h);
        SeedHours(102, 3, (d, h) => 40 + d);
        SeedHours(201, 3, (d, h) => 80 - d);

        var rows = Assess();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(PrtgValueReason.Ready, row.Reason));
        var disk = Assert.Single(rows, row => row.Sensor.SensorObjid == 201);
        Assert.Equal(12, disk.Sensor.HostId);
        Assert.Equal(PrtgValueSemantic.DiskPercentFree, disk.Semantic);
        Assert.Equal(new[] { 78.0, 79.0, 80.0 }, disk.Daily.Select(day => day.Value));
    }

    [Fact]
    public void sampled列coverage不足_計品質不足而非就緒()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(3, (1, 11));
        SeedHours(101, 3, (d, h) => 20, PrtgDataQuality.Sampled, coverage: 60);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.LowCoverage, row.Reason);
        Assert.Equal(0, row.ValidDays);
    }

    [Fact]
    public void 對照外的type_計單位未知且不查數值()
    {
        SeedSensors((101, 1, "SNMP Linux Meminfo", PrtgSensorCategories.Memory));
        MapDays(3, (1, 11));
        SeedHours(101, 3, (d, h) => 20);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.UnknownUnit, row.Reason);
        Assert.Null(row.Semantic);
    }

    [Fact]
    public void 覆寫語意後_原先對照外的type可識別語意且可評估()
    {
        SeedSensors((101, 1, "SNMP Linux Meminfo", PrtgSensorCategories.Memory));
        MapDays(3, (1, 11));
        SeedHours(101, 3, (d, h) => 20);

        var overrides = PrtgSensorTypeCategoryMap.ParseSemanticOverrides(new[]
        {
            "SNMP Linux Meminfo=memory:memory-percent-free"
        });
        var row = Assert.Single(Assess(overrides: overrides));

        Assert.Equal(PrtgValueReason.Ready, row.Reason);
        Assert.Equal(PrtgValueSemantic.MemoryPercentFree, row.Semantic);
    }

    [Fact]
    public void 百分比超出範圍的小時不採用_不足時原因為值不合理()
    {
        // 第一組 value_raw 選錯頻道（磁碟總容量 bytes）時的樣子：整顆都不合理，不能靜默進趨勢。
        SeedSensors((201, 2, "SNMP Disk Free", PrtgSensorCategories.Disk));
        MapDays(3, (2, 12));
        SeedHours(201, 3, (d, h) => 307544645632);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.ImplausibleValue, row.Reason);
        Assert.Equal(72, row.ImplausibleHours);
    }

    [Fact]
    public void 近七個有效日每小時完全相同_計固定值不評估()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(7, (1, 11));
        SeedHours(101, 7, (d, h) => 3);

        var row = Assert.Single(Assess(window: 7, required: 7));

        Assert.Equal(PrtgValueReason.FixedValue, row.Reason);
    }

    /// <summary>快照 sampled 列加權合併後同一個值會差到 1e-17；固定值判定用容差，不用相等比</summary>
    [Fact]
    public void 近七個有效日每小時只差浮點誤差_仍計固定值()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(7, (1, 11));
        SeedHours(101, 7, (d, h) => 3 + (h % 2 == 0 ? 1e-12 : -1e-12));

        var row = Assert.Single(Assess(window: 7, required: 7));

        Assert.Equal(PrtgValueReason.FixedValue, row.Reason);
    }

    [Fact]
    public void 預估到達日_極大或非有限天數不給日期_合理天數進位()
    {
        var end = new DateOnly(2026, 10, 18);
        Assert.Equal(new DateOnly(2026, 10, 21), PrtgValueAssessment.ProjectedDate(end, 2.1));
        Assert.Equal(end, PrtgValueAssessment.ProjectedDate(end, 0));
        Assert.Null(PrtgValueAssessment.ProjectedDate(end, null));
        Assert.Null(PrtgValueAssessment.ProjectedDate(end, 1e9));
        Assert.Null(PrtgValueAssessment.ProjectedDate(end, double.PositiveInfinity));
        Assert.Null(PrtgValueAssessment.ProjectedDate(end, double.NaN));
        Assert.Null(PrtgValueAssessment.ProjectedDate(end, -1));
    }

    [Fact]
    public void 評估日對應不是同一台主機_計對應無效()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(3, (1, 99));
        SeedHours(101, 3, (d, h) => 20);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.MappingInvalid, row.Reason);
    }

    [Fact]
    public void 只有今天一份ok對應_回填的過去日往後沿用_就緒()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        _store.ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
            { DeviceObjid = 1, HostId = 11, HostName = "h11", MapStatus = PrtgMapStatus.Ok }]);
        SeedHours(101, 30, (d, h) => 20 + h);

        var row = Assert.Single(Assess(window: 30, required: 28));

        Assert.NotEqual(PrtgValueReason.MappingInvalid, row.Reason);
        Assert.True(row.ValidDays >= 28, $"有效日數 {row.ValidDays}");
        Assert.Equal(PrtgValueReason.Ready, row.Reason);
    }

    /// <summary>最新日有其他 device 的 ok 對應、但當天少了這台 device 的列：仍逐 device 沿用它較早的對應，不判對應無效</summary>
    [Fact]
    public void 最新日有ok但缺這台device_沿用該device較早的對應()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        for (var d = 1; d < 3; d++)
            _store.ReplaceHostMapForDate(Completed.AddDays(-d).ToDateTime(TimeOnly.MinValue), [new PrtgHostMapRow
                { DeviceObjid = 1, HostId = 11, HostName = "h11", MapStatus = PrtgMapStatus.Ok }]);
        _store.ReplaceHostMapForDate(Completed.ToDateTime(TimeOnly.MinValue), [new PrtgHostMapRow
            { DeviceObjid = 2, HostId = 12, HostName = "h12", MapStatus = PrtgMapStatus.Ok }]);
        SeedHours(101, 3, (d, h) => 20 + h);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.Ready, row.Reason);
    }

    [Fact]
    public void 有效日差一天_計有效日不足並帶實際天數()
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(3, (1, 11));
        SeedHours(101, 2, (d, h) => 20 + h);

        var row = Assert.Single(Assess());

        Assert.Equal(PrtgValueReason.InsufficientDays, row.Reason);
        Assert.Equal(2, row.ValidDays);
        Assert.Equal(3, row.RequiredDays);
    }

    [Fact]
    public void 範圍內沒有sensor_候選為空()
    {
        Assert.Empty(Assess());
    }

    [Fact]
    public void 磁碟趨勢命中才產生finding_且EventKey不帶世代()
    {
        SeedSensors((201, 2, "SNMP Disk Free", PrtgSensorCategories.Disk));
        MapDays(35, (2, 12));
        // 每天少 1 個百分點、目前 15%（低於低水位 20%）、預估 15 日耗盡（≤ 30）
        SeedHours(201, 35, (d, h) => 15 + d);
        var rule = new KnownIssueRule
        {
            Id = "builtin-prtg-disk-free-trend", Platform = "prtg", PrtgRuleCode = PrtgRuleEvaluator.RuleDiskFreeTrend,
            PrtgSensorCategory = PrtgSensorCategories.Disk, Enabled = true, Severity = IssueSeverity.High,
            PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional, Description = "disk"
        };

        var row = Assert.Single(Assess(window: 35, required: 28, category: PrtgSensorCategories.Disk));
        var finding = PrtgValueAssessment.DiskTrendFinding(row, rule, "SRV-01", Completed, out var explanation);

        Assert.NotNull(finding);
        Assert.Equal(201, finding!.SensorObjid);
        Assert.Contains("耗盡", explanation);
        var signature = PrtgFindingMapper.ToSignature(finding, Completed.ToDateTime(TimeOnly.MinValue), 12);
        Assert.Equal("prtg:disk_free_trend:201", signature.EventKey);
    }

    // ── 最新日對應外層算一次（P-7）──────────────────────────────────────

    private static long HostMapQueryCount(SqlPerformanceMonitor monitor) =>
        monitor.Snapshot().TopSlowOperations
            .Where(o => o.Operation == "prtg:GetHostMapForDate" || o.Operation == "prtg:GetLatestHostMapWithDate")
            .Sum(o => o.Count);

    [Theory]
    [InlineData(false)] // 最新日有 ok 對應
    [InlineData(true)]  // 最新日整天沒有 ok，借前一份
    public void 外層傳入最新日對應_三頁只查一次且結果與不傳相同(bool newestDayEmpty)
    {
        SeedSensors((101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (102, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (103, 2, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (104, 2, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (105, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu));
        MapDays(3, (1, 11), (2, 12));
        if (newestDayEmpty) _store.ReplaceHostMapForDate(Completed.ToDateTime(TimeOnly.MinValue), new List<PrtgHostMapRow>());
        foreach (var id in new long[] { 101, 102, 103, 104, 105 }) SeedHours(id, 3, (d, h) => 10 + h);

        var overrides = new Dictionary<string, PrtgValueSemantic>();
        var candidates = new PrtgValueAssessment(_store, overrides)
            .Candidates(new Dictionary<long, long> { [1] = 11, [2] = 12 }, new HashSet<long> { 11, 12 }, new HashSet<long>(), null)
            .ToList();
        Assert.Equal(5, candidates.Count);
        var pages = candidates.Chunk(2).ToList();
        Assert.Equal(3, pages.Count);

        // 不傳：每頁自己算
        var monitorA = new SqlPerformanceMonitor(thresholdMs: 0);
        var assessA = new PrtgValueAssessment(new EfPrtgStore(_fx.NewContext, monitorA), overrides);
        var rowsA = pages.SelectMany(p => assessA.AssessPage(p, Completed, 3, 3, Completed)).ToList();

        // 外層傳入：整趟只算一次
        var monitorB = new SqlPerformanceMonitor(thresholdMs: 0);
        var assessB = new PrtgValueAssessment(new EfPrtgStore(_fx.NewContext, monitorB), overrides);
        var map = assessB.ResolveNewestHostMap(Completed);
        var rowsB = pages.SelectMany(p => assessB.AssessPage(p, Completed, 3, 3, Completed, map)).ToList();
        // 外層算一次的查詢數＝單次解析的查詢數；不傳時三頁各解析一次
        var perResolve = HostMapQueryCount(monitorB);
        Assert.True(perResolve >= 1);
        Assert.Equal(perResolve * 3, HostMapQueryCount(monitorA));

        Assert.Equal(rowsA.Count, rowsB.Count);
        for (var i = 0; i < rowsA.Count; i++)
        {
            Assert.Equal(rowsA[i].Sensor.SensorObjid, rowsB[i].Sensor.SensorObjid);
            Assert.Equal(rowsA[i].Reason, rowsB[i].Reason);
            Assert.Equal(rowsA[i].ValidDays, rowsB[i].ValidDays);
        }
        Assert.All(rowsB, r => Assert.Equal(PrtgValueReason.Ready, r.Reason));
    }
}
