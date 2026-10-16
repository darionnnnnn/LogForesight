using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgValueReadinessTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hostStore;
    private static readonly DateOnly Completed = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
    private long _testHostId = 11;

    public PrtgValueReadinessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _store = _backend.PrtgStore();
        _hostStore = new HostStore(_backend.Blob("hosts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void SeedSensors(params (long Objid, long Device, string Type, string Category)[] sensors) =>
        _store.UpsertSensors(sensors.Select(s => new PrtgSensorRow
        {
            Objid = s.Objid,
            DeviceObjid = s.Device,
            Name = $"S{s.Objid}",
            SensorType = s.Type,
            Category = s.Category,
            Status = "Up"
        }).ToList(), DateTime.Now);

    private void MapDays(int days, params (long Device, long Host)[] maps)
    {
        for (var d = 0; d < days; d++)
        {
            var date = Completed.AddDays(-d).ToDateTime(TimeOnly.MinValue);
            _store.ReplaceHostMapForDate(date, maps.Select(m => new PrtgHostMapRow
            {
                MapDate = date,
                DeviceObjid = m.Device,
                HostId = m.Host,
                HostName = $"h{m.Host}",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
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
                AvgValue = value(d, h),
                Coverage = coverage,
                Quality = quality,
                CreatedAt = DateTime.Now
            });
        _store.UpsertValues(rows);
    }

    private void SeedStandardFixedSet()
    {
        var host = _hostStore.Upsert(new WebHost { HostName = "host11", Active = true });
        _testHostId = host.HostId;
        SeedSensors(
            (101, 1, "SNMP CPU Load", PrtgSensorCategories.Cpu),
            (102, 1, "SNMP Memory", PrtgSensorCategories.Memory),
            (103, 1, "SNMP Disk Free", PrtgSensorCategories.Disk),
            (104, 1, "Ping", PrtgSensorCategories.Availability),
            (105, 1, "WMI Free Disk Space", PrtgSensorCategories.Disk),
            (106, 1, "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            (107, 2, "SNMP CPU Load", PrtgSensorCategories.Cpu));

        MapDays(1, (1, _testHostId));
    }

    [Fact]
    public void 固定集合_母體5顆_理由合計等於母體_單位未知1顆_排除traffic與未對應()
    {
        SeedStandardFixedSet();
        var assessment = new PrtgValueAssessment(_store);
        var deviceToHost = new Dictionary<long, long> { [1] = _testHostId };
        var evaluationHostIds = new HashSet<long> { _testHostId };
        var policySensorIds = new HashSet<long>();

        var summary = PrtgValueReadiness.Compute(assessment, deviceToHost, evaluationHostIds, policySensorIds, Completed, CancellationToken.None);

        Assert.Equal(5, summary.Population);
        Assert.Equal(summary.Population, summary.Counts.Values.Sum());
        Assert.Equal(1, summary.Counts[PrtgValueReason.UnknownUnit]);
        Assert.DoesNotContain(summary.Rows, r => r.Sensor.SensorObjid == 106);
        Assert.DoesNotContain(summary.Rows, r => r.Sensor.SensorObjid == 107);
        Assert.Equal(5, summary.Rows.Count);
    }

    [Fact]
    public void 寫滿28個完整日_該顆評估為就緒()
    {
        SeedStandardFixedSet();
        MapDays(28, (1, _testHostId));
        SeedHours(101, 28, (d, h) => 10 + d + (h * 0.1));

        var assessment = new PrtgValueAssessment(_store);
        var deviceToHost = new Dictionary<long, long> { [1] = _testHostId };
        var evaluationHostIds = new HashSet<long> { _testHostId };
        var policySensorIds = new HashSet<long>();

        var summary = PrtgValueReadiness.Compute(assessment, deviceToHost, evaluationHostIds, policySensorIds, Completed, CancellationToken.None);

        var cpuRow = Assert.Single(summary.Rows, r => r.Sensor.SensorObjid == 101);
        Assert.Equal(PrtgValueReason.Ready, cpuRow.Reason);
        Assert.Equal(1, summary.Counts[PrtgValueReason.Ready]);
    }

    [Fact]
    public void 格式化文字_母體零顆與母體非零顆輸出格式正確()
    {
        var emptySummary = new PrtgValueReadinessSummary(
            Completed,
            0,
            Enum.GetValues<PrtgValueReason>().ToDictionary(r => r, _ => 0),
            []);

        var lineEmpty = PrtgValueReadiness.FormatLine(emptySummary);
        Assert.Contains("範圍內無可評估 sensor", lineEmpty);
        Assert.Equal($"數值資料就緒度（{Completed:yyyy-MM-dd}）：範圍內無可評估 sensor（請確認鏡像狀態頁籤的對應）。", lineEmpty);

        SeedStandardFixedSet();
        var assessment = new PrtgValueAssessment(_store);
        var deviceToHost = new Dictionary<long, long> { [1] = _testHostId };
        var evaluationHostIds = new HashSet<long> { _testHostId };
        var summary5 = PrtgValueReadiness.Compute(assessment, deviceToHost, evaluationHostIds, new HashSet<long>(), Completed, CancellationToken.None);

        var line5 = PrtgValueReadiness.FormatLine(summary5);
        Assert.Contains("母體 5 顆", line5);
        Assert.DoesNotContain(" 0", line5);
        Assert.EndsWith("。", line5);
    }

    [Fact]
    public void 政策縮圈限制sensor_母體僅剩指定一顆()
    {
        SeedStandardFixedSet();
        var assessment = new PrtgValueAssessment(_store);
        var deviceToHost = new Dictionary<long, long> { [1] = _testHostId };
        var evaluationHostIds = new HashSet<long> { _testHostId };
        var policySensorIds = new HashSet<long> { 101 };

        var summary = PrtgValueReadiness.Compute(assessment, deviceToHost, evaluationHostIds, policySensorIds, Completed, CancellationToken.None);

        Assert.Equal(1, summary.Population);
        var row = Assert.Single(summary.Rows);
        Assert.Equal(101, row.Sensor.SensorObjid);
    }

    [Fact]
    public void 控制器_無效原因回傳BadRequestObjectResult()
    {
        SeedStandardFixedSet();
        var visibility = new FixedVisibilityService([_testHostId]);
        var controller = new PrtgValueReadinessController(_backend, _hostStore, visibility);

        var actionResult = controller.Get(reason: "bogus");

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Fact]
    public void 控制器_原因篩選與分頁超限正確()
    {
        SeedStandardFixedSet();
        var visibility = new FixedVisibilityService([_testHostId]);
        var controller = new PrtgValueReadinessController(_backend, _hostStore, visibility);

        var filteredResult = controller.Get(reason: "UnknownUnit");
        var filteredData = filteredResult.Value?.Data;
        Assert.NotNull(filteredData);
        Assert.Equal(1, filteredData.TotalRows);
        var row = Assert.Single(filteredData.Rows);
        Assert.Equal(105, row.SensorObjid);
        Assert.Equal("host11", row.HostName);
        Assert.Equal("單位未知", row.ReasonLabel);
        Assert.Equal(5, filteredData.Population);

        var pagedResult = controller.Get(page: 99);
        var pagedData = pagedResult.Value?.Data;
        Assert.NotNull(pagedData);
        Assert.Empty(pagedData.Rows);
        Assert.Equal(5, pagedData.TotalRows);
    }

    [Fact]
    public void 控制器_可見範圍不含該主機則母體為零()
    {
        SeedStandardFixedSet();
        var emptyVisibility = new FixedVisibilityService([]);
        var controller = new PrtgValueReadinessController(_backend, _hostStore, emptyVisibility);

        var result = controller.Get();
        var data = result.Value?.Data;
        Assert.NotNull(data);
        Assert.Equal(0, data.Population);
        Assert.Empty(data.Rows);
    }
}
