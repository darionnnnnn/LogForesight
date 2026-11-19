using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 校準匯出的兩件事：某日採哪份主機對應（與規則歸戶同一個選法）、評估日是今天時持續 Down 只算到評估時刻。
/// 判定快取是行程層級靜態欄位，與 CalibrationServiceTests 同一個 Collection 序列化。
/// </summary>
[Collection("CalibrationCacheState")]
public sealed class CalibrationHostMapAndClockTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 9, 10);
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();

    public CalibrationHostMapAndClockTests()
    {
        _ruleStore.Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() };
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
            s.PrtgSensorTypeWhitelist = new List<string> { "Ping" };
            s.PrtgUrl = "https://prtg.example.invalid/";
        });
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(_fx.NewContext, PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.HostIds = [1];
            p.SensorIds = [1001];
        });
        new EfPrtgStore(_fx.NewContext).UpsertSensors(
            [new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, SensorType = "Ping", Category = "availability", Status = "Down", Paused = false }],
            DateTime.Now);
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FixedLocalClock(DateTime localNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(DateTime.SpecifyKind(localNow, DateTimeKind.Local)).ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    private CalibrationService Service(DateTime localNow)
    {
        CalibrationService.ClearAssessmentCache();
        return new CalibrationService(_fx.NewContext, new EfPrtgStore(_fx.NewContext),
            new EfIssueAggregateQuery(_fx.NewContext, _hostStore), _settingsStore, _ruleStore, clock: new FixedLocalClock(localNow));
    }

    private void Map(DateTime day, string status)
    {
        new EfPrtgStore(_fx.NewContext).ReplaceHostMapForDate(day, [new PrtgHostMapRow
        {
            MapDate = day, DeviceObjid = 10, HostId = status == PrtgMapStatus.Ok ? 1 : null, HostName = "HOST-1",
            MapStatus = status, CreatedAt = DateTime.Now
        }]);
    }

    private void DownSince(DateTime at) =>
        new EfPrtgStore(_fx.NewContext).AppendStateChanges([new PrtgStateChangeRow
        {
            SensorObjid = 1001, ChangedAt = at, Status = "Down", Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Now
        }]);

    private static int Coverage(CalibrationService service, DateTime anchor) =>
        Convert.ToInt32(service.AssessStatus(anchor, forceRefresh: true).PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]);

    [Fact]
    public void 最新一份對應全為unmatched_採較早的有效對應_涵蓋天數與歸戶一致()
    {
        var anchor = Today;
        Map(anchor.AddDays(-4), PrtgMapStatus.Ok);
        Map(anchor.AddDays(-1), PrtgMapStatus.Unmatched);
        DownSince(anchor.AddDays(-4));
        var store = new EfPrtgStore(_fx.NewContext);

        // 與規則歸戶（GetHostMapForDay）同一個選法：有效對應日起的每一天都選到 anchor-4 那份，而不是較新的 unmatched
        using (var ctx = _fx.NewContext())
        {
            for (var d = anchor.AddDays(-4); d <= anchor; d = d.AddDays(1))
            {
                Assert.Equal(anchor.AddDays(-4), EfPrtgStore.FindOkHostMapDateOnOrBefore(ctx, d, 30));
                Assert.Equal(anchor.AddDays(-4), store.GetHostMapForDay(d, 30).MapDate);
            }
        }

        Assert.Equal(5, Coverage(Service(anchor.AddHours(12)), anchor)); // anchor-4 .. anchor
    }

    [Fact]
    public void 只有unmatched對應時_校準不把那些日子算成涵蓋()
    {
        var anchor = Today;
        Map(anchor.AddDays(-2), PrtgMapStatus.Unmatched);
        DownSince(anchor.AddDays(-2));

        Assert.Equal(0, Coverage(Service(anchor.AddHours(12)), anchor));
        using var ctx = _fx.NewContext();
        Assert.Null(EfPrtgStore.FindOkHostMapDateOnOrBefore(ctx, anchor, 30));
    }

    [Fact]
    public void 有效對應在後_之前的日子仍不往後沿用_從有效對應當天才算涵蓋()
    {
        var anchor = Today;
        Map(anchor.AddDays(-1), PrtgMapStatus.Ok);
        DownSince(anchor.AddDays(-3));

        Assert.Equal(2, Coverage(Service(anchor.AddHours(12)), anchor)); // anchor-1、anchor；更早的日子沒有「當天已有對應」
    }

    private int DownMagnitudeOn(DateTime anchor, DateTime clockNow)
    {
        var package = Service(clockNow).BuildExportPackage(anchor);
        return package.RuleThresholds.MagnitudeSamples
            .Single(s => s.RuleCode == PrtgRuleEvaluator.RuleDown && s.Date == anchor).Magnitude;
    }

    [Fact]
    public void 當日評估_Down持續分鐘只算到評估時刻_不算到午夜()
    {
        Map(Today, PrtgMapStatus.Ok);
        DownSince(Today);

        var minutes = DownMagnitudeOn(Today, Today.AddHours(14));

        Assert.Equal(14 * 60, minutes);
    }

    [Fact]
    public void 當日評估_評估時刻在Down開始之前_不產生負值或超額()
    {
        Map(Today, PrtgMapStatus.Ok);
        DownSince(Today.AddHours(10));

        Assert.Equal(4 * 60, DownMagnitudeOn(Today, Today.AddHours(14)));
    }

    [Fact]
    public void 過去日評估_Down持續分鐘照算到當日結束()
    {
        var past = Today.AddDays(-1);
        Map(past, PrtgMapStatus.Ok);
        DownSince(past);

        // 時鐘在隔天 14:00，評估日是昨天：不受評估時刻限制
        Assert.Equal(24 * 60, DownMagnitudeOn(past, Today.AddHours(14)));
    }

    [Fact]
    public void 規則評估器_未指定評估時刻時算到當日結束()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 1001, ChangedAt = Today, Status = "Down", Quality = PrtgDataQuality.Ok }
        };
        var inputs = new List<PrtgSensorStatusInput> { new(1001, 10, "Down", "Ping", null) };
        var rules = new List<KnownIssueRule> { new() { Id = "d", Platform = "prtg", Enabled = true, PrtgRuleCode = "down", PrtgThreshold = 1 } };
        var names = new Dictionary<long, string>();
        var map = new Dictionary<long, long> { [1001] = 10 };

        var full = PrtgRuleEvaluator.Evaluate(Today, changes, map, inputs, rules, names, names, includeSilent: false);
        var partial = PrtgRuleEvaluator.Evaluate(Today, changes, map, inputs, rules, names, names, includeSilent: false,
            asOf: Today.AddHours(14));
        var otherDay = PrtgRuleEvaluator.Evaluate(Today, changes, map, inputs, rules, names, names, includeSilent: false,
            asOf: Today.AddDays(1).AddHours(14));

        Assert.Equal(1440, Assert.Single(full.Where(f => f.RuleCode == "down")).Magnitude);
        Assert.Equal(840, Assert.Single(partial.Where(f => f.RuleCode == "down")).Magnitude);
        Assert.Equal(1440, Assert.Single(otherDay.Where(f => f.RuleCode == "down")).Magnitude);
    }
}
