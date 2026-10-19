using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 任務 54-C4：PRTG 數值趨勢 finding 結構化證據、格式化文字、狀態型 finding 對比與主機詳情就緒度測試。
/// </summary>
public sealed class PrtgTrendEvidenceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly SqliteConnection _connection;

    public PrtgTrendEvidenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .Options);

    private sealed class StubVisibility : IVisibilityService
    {
        private readonly long _visibleHostId;
        public StubVisibility(long visibleHostId) => _visibleHostId = visibleHostId;
        public void EnsureVisible(long hostId) { }
        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { _visibleHostId };
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => throw new NotSupportedException();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => throw new NotSupportedException();
        public List<WebHost> GetVisibleHosts() => new() { new WebHost { HostId = _visibleHostId } };
        public IReadOnlyList<string> GetCaseGrantHostNames() => throw new NotSupportedException();
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
    }

    [Fact]
    public void Describe_四條規則與預估日期_完整字串格式斷言()
    {
        // 1. 磁碟
        var diskEvidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RuleDiskFreeTrend, 101, 201, "SRV-FILE-01", "C: Drive",
            new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 18), 35, 30,
            15.2, "%", -0.85, 20.0, new DateOnly(2026, 11, 15));
        Assert.Equal(
            "SRV-FILE-01／C: Drive：磁碟可用空間持續下降；近 35 日有效 30 日；目前 15.2%，每日 -0.85%；預估 2026-11-15 達 20.0%；資料截至 2026-10-18",
            diskEvidence.Describe());

        // 2. 記憶體
        var memEvidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RuleMemoryUsedTrend, 102, 202, "SRV-DB-01", "SNMP Memory",
            new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18), 28, 28,
            85.5, "%", 1.23, 90.0, new DateOnly(2026, 10, 25));
        Assert.Equal(
            "SRV-DB-01／SNMP Memory：記憶體使用率持續攀升；近 28 日有效 28 日；目前 85.5%，每日 +1.23%；預估 2026-10-25 達 90.0%；資料截至 2026-10-18",
            memEvidence.Describe());

        // 3. CPU
        var cpuEvidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RuleCpuLoadTrend, 103, 203, "SRV-APP-01", "CPU Load",
            new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18), 28, 25,
            72.0, "%", 0.50, 80.0, new DateOnly(2026, 11, 3));
        Assert.Equal(
            "SRV-APP-01／CPU Load：CPU 使用率長期上升且已偏高；近 28 日有效 25 日；目前 72.0%，每日 +0.50%；預估 2026-11-03 達 80.0%；資料截至 2026-10-18",
            cpuEvidence.Describe());

        // 4. Ping
        var pingEvidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RulePingLatencyTrend, 104, 204, "GW-CORE-01", "Ping",
            new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18), 28, 28,
            55.4, " ms", 2.10, 50.0, new DateOnly(2026, 10, 19));
        Assert.Equal(
            "GW-CORE-01／Ping：Ping 回應時間持續惡化；近 28 日有效 28 日；目前 55.4 ms，每日 +2.10 ms；預估 2026-10-19 達 50.0 ms；資料截至 2026-10-18",
            pingEvidence.Describe());
    }

    [Fact]
    public void Describe_ProjectedDate為Null_產生未預估到達日期替代句()
    {
        var evidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RuleDiskFreeTrend, 101, 201, "SRV-01", "D: Data",
            new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 18), 35, 30,
            12.0, "%", -0.05, 10.0, null);
        Assert.Equal(
            "SRV-01／D: Data：磁碟可用空間持續下降；近 35 日有效 30 日；目前 12.0%，每日 -0.05%；未預估到達 10.0% 的日期；資料截至 2026-10-18",
            evidence.Describe());
    }

    [Fact]
    public void DiskTrendFinding與TrendFinding_命中時Detail等於EvidenceDescribe且CurrentValue與引擎相同()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var completedDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

        // 1. 磁碟趨勢
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 2, Name = "SNMP Disk Free", SensorType = "SNMP Disk Free", Category = PrtgSensorCategories.Disk, Status = "Up" }
        }, DateTime.Now);
        for (var d = 0; d < 35; d++)
        {
            var date = completedDay.AddDays(-d).ToDateTime(TimeOnly.MinValue);
            store.ReplaceHostMapForDate(date, new List<PrtgHostMapRow>
            {
                new() { DeviceObjid = 2, HostId = 12, HostName = "SRV-DISK", MapStatus = PrtgMapStatus.Ok }
            });
        }
        var valueRows = new List<PrtgValueRow>();
        for (var d = 0; d < 35; d++)
        for (var h = 0; h < 24; h++)
            valueRows.Add(new PrtgValueRow
            {
                SensorObjid = 201,
                PeriodStart = completedDay.AddDays(-d).ToDateTime(new TimeOnly(h, 0)),
                AvgValue = 15.0 + d * 1.0,
                Coverage = 100,
                Quality = PrtgDataQuality.Ok,
                CreatedAt = DateTime.Now
            });
        store.UpsertValues(valueRows);

        var diskRule = new KnownIssueRule
        {
            Id = "builtin-prtg-disk-free-trend", Platform = "prtg", PrtgRuleCode = PrtgRuleEvaluator.RuleDiskFreeTrend,
            PrtgSensorCategory = PrtgSensorCategories.Disk, Enabled = true, Severity = IssueSeverity.High,
            PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional, Description = "disk"
        };

        var assessment = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>());
        var diskCandidates = assessment.Candidates(new Dictionary<long, long> { [2] = 12 }, new HashSet<long> { 12 }, new HashSet<long>(), PrtgSensorCategories.Disk);
        var diskRow = Assert.Single(assessment.AssessPage(diskCandidates.ToList(), completedDay, 35, 28));

        var diskFinding = PrtgValueAssessment.DiskTrendFinding(diskRow, diskRule, "SRV-DISK", completedDay, out _);
        Assert.NotNull(diskFinding);
        Assert.NotNull(diskFinding!.TrendEvidence);
        Assert.Equal(diskFinding.Detail, diskFinding.TrendEvidence!.Describe());
        Assert.Equal("SRV-DISK", diskFinding.TrendEvidence.DeviceName);

        var expectedDiskResult = PrtgDiskTrendEvaluator.Evaluate(
            diskRow.Daily.Select(d => new PrtgDiskTrendDay(d.Day, d.Value)).ToArray(), diskRule.PrtgDiskTrendThresholds!);
        Assert.Equal(expectedDiskResult!.CurrentAvailablePercent!.Value, diskFinding.TrendEvidence.CurrentValue, 4);

        // 2. 記憶體趨勢
        var memRule = new KnownIssueRule
        {
            Id = "builtin-prtg-memory-used-trend", Platform = "prtg",
            PrtgRuleCode = PrtgRuleEvaluator.RuleMemoryUsedTrend,
            PrtgSensorCategory = PrtgSensorCategories.Memory, Enabled = true, Severity = IssueSeverity.High,
            PrtgTrendThresholds = PrtgTrendThresholds.MemoryProvisional, Description = "mem"
        };
        var memDaily = new List<PrtgDailyValue>();
        for (var i = 27; i >= 0; i--)
        {
            var day = completedDay.AddDays(-i);
            var availPercent = 25.0 - (27 - i) * 0.5;
            memDaily.Add(new PrtgDailyValue(day, availPercent, 24));
        }
        var memSensor = new PrtgValueSensor(1001, 101, 11, "Memory Sensor", "SNMP Memory", PrtgSensorCategories.Memory);
        var memRow = new PrtgValueAssessmentRow(memSensor, PrtgValueSemantic.MemoryPercentFree, PrtgValueReason.Ready, 28, 28, completedDay, memDaily, 0);

        var memFinding = PrtgValueAssessment.TrendFinding(memRow, memRule, "SRV-MEM", completedDay, out _);
        Assert.NotNull(memFinding);
        Assert.NotNull(memFinding!.TrendEvidence);
        Assert.Equal(memFinding.Detail, memFinding.TrendEvidence!.Describe());
        Assert.Equal("SRV-MEM", memFinding.TrendEvidence.DeviceName);

        var expectedMemResult = PrtgValueAssessment.EvaluateTrend(memRow, memRule);
        Assert.NotNull(expectedMemResult);
        Assert.Equal(expectedMemResult!.CurrentValue!.Value, memFinding.TrendEvidence.CurrentValue, 4);
    }

    [Fact]
    public void ToSignature_保留TrendEvidence_且經EfAnalysisRecordStore存取欄位不變()
    {
        var date = DateTime.Today;
        var hostId = 101L;

        var evidence = new PrtgTrendEvidence(
            PrtgRuleEvaluator.RuleDiskFreeTrend, 1001, 2001, "SRV-TEST", "C: Drive",
            DateOnly.FromDateTime(date.AddDays(-34)), DateOnly.FromDateTime(date), 35, 30,
            14.5, "%", -0.75, 20.0, DateOnly.FromDateTime(date.AddDays(12)));

        var rule = new KnownIssueRule
        {
            Id = "builtin-prtg-disk-free-trend", Platform = "prtg", PrtgRuleCode = PrtgRuleEvaluator.RuleDiskFreeTrend,
            PrtgSensorCategory = PrtgSensorCategories.Disk, Enabled = true, Severity = IssueSeverity.High
        };

        var finding = new PrtgFinding(1001, 2001, PrtgRuleEvaluator.RuleDiskFreeTrend, evidence.Describe(), 30, rule, false)
        {
            SensorCategory = PrtgSensorCategories.Disk,
            TrendEvidence = evidence
        };

        var sig = PrtgFindingMapper.ToSignature(finding, date, hostId);
        Assert.NotNull(sig.PrtgTrendEvidence);
        Assert.Same(evidence, sig.PrtgTrendEvidence);

        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            HostId = hostId,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        });

        var attachResult = store.AttachPrtgFindings(hostId, date, new[] { sig }, new HashSet<string>(), out _);
        Assert.True(attachResult);

        var records = store.ReadRecent(date, 1);
        var readRecord = Assert.Single(records);
        var readIssue = Assert.Single(readRecord.TopIssues);
        Assert.NotNull(readIssue.PrtgTrendEvidence);

        var readEv = readIssue.PrtgTrendEvidence!;
        Assert.Equal(evidence.RuleCode, readEv.RuleCode);
        Assert.Equal(evidence.DeviceObjid, readEv.DeviceObjid);
        Assert.Equal(evidence.SensorObjid, readEv.SensorObjid);
        Assert.Equal(evidence.DeviceName, readEv.DeviceName);
        Assert.Equal(evidence.SensorName, readEv.SensorName);
        Assert.Equal(evidence.WindowStart, readEv.WindowStart);
        Assert.Equal(evidence.WindowEnd, readEv.WindowEnd);
        Assert.Equal(evidence.WindowDays, readEv.WindowDays);
        Assert.Equal(evidence.ValidDays, readEv.ValidDays);
        Assert.Equal(evidence.CurrentValue, readEv.CurrentValue);
        Assert.Equal(evidence.Unit, readEv.Unit);
        Assert.Equal(evidence.SlopePerDay, readEv.SlopePerDay);
        Assert.Equal(evidence.TargetValue, readEv.TargetValue);
        Assert.Equal(evidence.ProjectedDate, readEv.ProjectedDate);
    }

    [Fact]
    public void 狀態型Finding_TrendEvidence為Null_Detail維持原樣()
    {
        var downRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-down-availability");
        var day = new DateTime(2026, 10, 18);
        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 3001, Status = "Down", ChangedAt = day.AddHours(2) }
        };
        var sensorToDevice = new Dictionary<long, long> { [3001] = 4001 };
        var statuses = new List<PrtgSensorStatusInput>
        {
            new(3001, 4001, "Down", "ping", PrtgSensorCategories.Availability)
        };
        var sensorNames = new Dictionary<long, string> { [3001] = "Ping" };
        var deviceNames = new Dictionary<long, string> { [4001] = "GW-01" };

        var findings = PrtgRuleEvaluator.Evaluate(day, changes, sensorToDevice, statuses,
            new[] { downRule }, sensorNames, deviceNames, includeSilent: false);

        var finding = Assert.Single(findings);
        Assert.Null(finding.TrendEvidence);
        Assert.Contains("Down", finding.Detail);
    }

    [Fact]
    public void 主機詳情Sensor就緒度_Memory十日為InsufficientDays_非趨勢Sensor為Null_未知TypeCPU為UnknownUnit()
    {
        var hostId = 10L;
        var now = DateTime.Now;
        var yesterday = DateTime.Today.AddDays(-1);

        using (var ctx = NewContext())
        {
            ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1001, Name = "SRV-10", Ip = "10.0.0.1", SyncedAt = now, CreatedAt = now });

            // 1. memory sensor
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2001, DeviceObjid = 1001, Name = "Memory", SensorType = "SNMP Memory",
                Category = PrtgSensorCategories.Memory, Paused = false, SyncedAt = now, CreatedAt = now
            });
            // 2. 非趨勢分類 sensor（例如 custom / network）
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2002, DeviceObjid = 1001, Name = "Custom Net", SensorType = "Ping",
                Category = "network", Paused = false, SyncedAt = now, CreatedAt = now
            });
            // 3. CPU sensor 但 type 不在語意對照中
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2003, DeviceObjid = 1001, Name = "Unknown CPU", SensorType = "custom-cpu-unknown",
                Category = PrtgSensorCategories.Cpu, Paused = false, SyncedAt = now, CreatedAt = now
            });

            // 對應表：為昨天與過去 28 天每天建立對應
            for (var d = 0; d < 35; d++)
            {
                ctx.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    MapDate = yesterday.AddDays(-d), DeviceObjid = 1001, Ip = "10.0.0.1", HostId = hostId,
                    HostName = "SRV-10", MapStatus = PrtgMapStatus.Ok, Note = "自動對應", CreatedAt = now
                });
            }

            // memory sensor 只塞 10 個完整日
            for (var d = 0; d < 10; d++)
            for (var h = 0; h < 24; h++)
            {
                ctx.PrtgValues.Add(new PrtgValueRow
                {
                    SensorObjid = 2001,
                    PeriodStart = yesterday.AddDays(-d).AddHours(h),
                    AvgValue = 20.0 + d * 0.5 + h * 0.01,
                    Coverage = 100,
                    Quality = PrtgDataQuality.Ok,
                    CreatedAt = now
                });
            }

            ctx.SaveChanges();
        }

        var store = new EfPrtgStore(NewContext);
        var assessment = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>());
        var controller = new HostDetailController(null!, store, new StubVisibility(hostId), assessment);

        var result = controller.Prtg(hostId).Data;
        Assert.NotNull(result);
        var device = Assert.Single(result!.Devices);
        Assert.Equal(3, device.Sensors.Count);

        // Memory sensor
        var memSensor = device.Sensors.Single(s => s.Objid == 2001);
        Assert.Equal(nameof(PrtgValueReason.InsufficientDays), memSensor.ValueReasonCode);
        Assert.Equal("有效日不足", memSensor.ValueReason);
        Assert.Equal(10, memSensor.ValidDays);
        Assert.Equal(28, memSensor.RequiredDays);

        // 非趨勢分類 sensor
        var netSensor = device.Sensors.Single(s => s.Objid == 2002);
        Assert.Null(netSensor.ValueReasonCode);
        Assert.Null(netSensor.ValueReason);
        Assert.Null(netSensor.ValidDays);
        Assert.Null(netSensor.RequiredDays);

        // 未知 type 的 CPU sensor
        var cpuSensor = device.Sensors.Single(s => s.Objid == 2003);
        Assert.Equal(nameof(PrtgValueReason.UnknownUnit), cpuSensor.ValueReasonCode);
        Assert.Equal("單位未知", cpuSensor.ValueReason);
        Assert.Equal(0, cpuSensor.ValidDays);
        Assert.Equal(28, cpuSensor.RequiredDays);
    }

    [Fact]
    public void 資料不足不升風險_InsufficientDays傳入TrendFinding與DiskTrendFinding回Null()
    {
        var memRule = new KnownIssueRule
        {
            Id = "builtin-prtg-memory-used-trend", Platform = "prtg",
            PrtgRuleCode = PrtgRuleEvaluator.RuleMemoryUsedTrend,
            PrtgSensorCategory = PrtgSensorCategories.Memory, Enabled = true, Severity = IssueSeverity.High,
            PrtgTrendThresholds = PrtgTrendThresholds.MemoryProvisional
        };
        var diskRule = new KnownIssueRule
        {
            Id = "builtin-prtg-disk-free-trend", Platform = "prtg",
            PrtgRuleCode = PrtgRuleEvaluator.RuleDiskFreeTrend,
            PrtgSensorCategory = PrtgSensorCategories.Disk, Enabled = true, Severity = IssueSeverity.High,
            PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional
        };

        var day = new DateOnly(2026, 10, 18);
        var memRow = new PrtgValueAssessmentRow(
            new PrtgValueSensor(5001, 6001, 10, "Mem", "SNMP Memory", PrtgSensorCategories.Memory),
            PrtgValueSemantic.MemoryPercentFree,
            PrtgValueReason.InsufficientDays,
            10,
            28,
            null,
            [],
            0);

        var memFinding = PrtgValueAssessment.TrendFinding(memRow, memRule, "SRV-01", day, out var memExplanation);
        Assert.Null(memFinding);
        Assert.Equal("有效日不足", memExplanation);

        var diskRow = memRow with
        {
            Sensor = new PrtgValueSensor(5002, 6001, 10, "Disk", "SNMP Disk Free", PrtgSensorCategories.Disk),
            Semantic = PrtgValueSemantic.DiskPercentFree
        };
        var diskFinding = PrtgValueAssessment.DiskTrendFinding(diskRow, diskRule, "SRV-01", day, out var diskExplanation);
        Assert.Null(diskFinding);
        Assert.Equal("有效日不足", diskExplanation);
    }
}
