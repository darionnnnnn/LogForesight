using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgFullBackfillPlanTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private EfPrtgStore Store() => new(_fixture.NewContext);

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 60)]
    [InlineData(10, 60)]
    [InlineData(11, 120)]
    public void HistoricQuotaLowerBound_UsesFiveRequestsPerRollingSixtySeconds(long requests, long seconds) =>
        Assert.Equal(seconds, PrtgFullBackfillPlan.QuotaLowerBoundSeconds(requests));

    [Fact]
    public void Build_UsesTriggeredHostsHistoricalMapAndWhitelistForEachDay()
    {
        var store = Store();
        var records = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var anchor = DateTime.Today;
        var yesterday = anchor.AddDays(-1);
        var twoDaysAgo = anchor.AddDays(-2);
        AddMap(store, yesterday, 100, 11);
        AddMap(store, twoDaysAgo, 200, 11);
        AddMap(store, anchor, 300, 11); // today's map must not be used for either historical day
        AddSensors(store, (1001, 100), (1002, 100), (2001, 200), (3001, 300));
        records.Append(new DailyAnalysisRecord { HostId = 11, Host = "host-11", Date = yesterday, RiskLevel = "高" });
        records.Append(new DailyAnalysisRecord { HostId = 11, Host = "host-11", Date = twoDaysAgo, RiskLevel = "中" });
        records.Append(new DailyAnalysisRecord { HostId = 22, Host = "host-22", Date = yesterday, RiskLevel = "低" });

        var plan = PrtgFullBackfillPlanBuilder.Build(store, records, anchor, 2, new long[] { 100, 200 },
            new[] { "diskfree" }, PrtgValueFetchScope.Triggered, Array.Empty<long>());

        Assert.Equal(twoDaysAgo, plan.FromDate);
        Assert.Equal(yesterday, plan.ToDate);
        Assert.Equal(2, plan.DayCount);
        Assert.Equal(3, plan.HistoricRequests);
        Assert.Equal(2, plan.DaysWithTargets);
        Assert.Equal(new[] { 1, 2 }, plan.Days.Select(day => day.TargetSensors));
    }

    [Fact]
    public void Build_FingerprintChangesWhenRiskRecordsOrHistoricalMapChanges()
    {
        var store = Store();
        var records = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var anchor = DateTime.Today;
        var yesterday = anchor.AddDays(-1);
        AddMap(store, yesterday, 100, 11);
        AddSensors(store, (1001, 100));
        records.Append(new DailyAnalysisRecord { HostId = 11, Host = "host-11", Date = yesterday, RiskLevel = "高" });

        var first = Build(store, records, anchor);
        records.Append(new DailyAnalysisRecord { HostId = 22, Host = "host-22", Date = yesterday, RiskLevel = "中" });
        var changedRecords = Build(store, records, anchor);
        AddMap(store, yesterday, (100, 11), (200, 22));
        AddSensors(store, (2001, 200));
        var changedMapAndTargets = Build(store, records, anchor);

        Assert.NotEqual(first.TargetFingerprint, changedRecords.TargetFingerprint);
        Assert.NotEqual(changedRecords.TargetFingerprint, changedMapAndTargets.TargetFingerprint);
    }

    private PrtgFullBackfillPlan Build(EfPrtgStore store, EfAnalysisRecordStore records, DateTime anchor) =>
        PrtgFullBackfillPlanBuilder.Build(store, records, anchor, 1, new long[] { 100 },
            new[] { "diskfree" }, PrtgValueFetchScope.Triggered, Array.Empty<long>());

    private static void AddMap(EfPrtgStore store, DateTime day, long deviceId, long hostId) =>
        AddMap(store, day, (deviceId, hostId));

    private static void AddMap(EfPrtgStore store, DateTime day, params (long DeviceId, long HostId)[] mappings) =>
        store.ReplaceHostMapForDate(day, mappings.Select(item => new PrtgHostMapRow
        {
            DeviceObjid = item.DeviceId, HostId = item.HostId, MapStatus = PrtgMapStatus.Ok
        }).ToArray());

    private static void AddSensors(EfPrtgStore store, params (long SensorId, long DeviceId)[] sensors) =>
        store.UpsertSensors(sensors.Select(item => new PrtgSensorRow
        {
            Objid = item.SensorId, DeviceObjid = item.DeviceId, Name = $"sensor-{item.SensorId}", SensorType = "diskfree"
        }).ToArray(), DateTime.Now);
}
