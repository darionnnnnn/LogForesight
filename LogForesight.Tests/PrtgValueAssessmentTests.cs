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
        var finding = PrtgValueAssessment.DiskTrendFinding(row, rule, Completed, out var explanation);

        Assert.NotNull(finding);
        Assert.Equal(201, finding!.SensorObjid);
        Assert.Contains("耗盡", explanation);
        var signature = PrtgFindingMapper.ToSignature(finding, Completed.ToDateTime(TimeOnly.MinValue), 12);
        Assert.Equal("prtg:disk_free_trend:201", signature.EventKey);
    }
}
