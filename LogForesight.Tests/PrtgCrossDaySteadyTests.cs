using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>持續 Warning 且數值持平降一級：純函式判定（PrtgCrossDay）與數值證據組裝（管線 BuildSteadyEvidence）。</summary>
public sealed class PrtgCrossDaySteadyTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 15);

    private readonly EfSqliteFixture _fx = new();
    private readonly EfPrtgStore _store;

    public PrtgCrossDaySteadyTests() => _store = new EfPrtgStore(_fx.NewContext);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static LogIssueSignature Sig(string ruleCode, IssueSeverity severity = IssueSeverity.Medium, bool elevates = false) => new()
    {
        LogName = PrtgFindingMapper.PrtgLogName,
        Source = $"PRTG:{ruleCode}",
        EventId = 0,
        EntryType = LogEntryType.Warning,
        EventKey = $"prtg:{ruleCode}:2001",
        SampleMessages = new List<string> { "原始說明" },
        Severity = severity,
        ElevatesDayRisk = elevates
    };

    /// <summary>連續第 m 日＝前 m-1 日都有命中。</summary>
    private static Dictionary<string, HashSet<DateTime>> Consecutive(LogIssueSignature sig, int m) => new()
    {
        [sig.EventKey] = Enumerable.Range(1, m - 1).Select(n => Day.AddDays(-n)).ToHashSet()
    };

    private static Dictionary<string, PrtgSteadyEvidence> Evidence(LogIssueSignature sig, double firstUsed, double lastUsed,
        int usableDays = 7, PrtgValueSemantic semantic = PrtgValueSemantic.DiskPercentFree) =>
        new() { [sig.EventKey] = new PrtgSteadyEvidence(semantic, usableDays, firstUsed, lastUsed) };

    private static PrtgCrossDay.CrossDayResult Run(LogIssueSignature sig, int m, Dictionary<string, PrtgSteadyEvidence>? steady) =>
        PrtgCrossDay.ApplyDetailed(new[] { sig }, Consecutive(sig, m), Day, steady);

    [Fact]
    public void 連續第6日不降_第7日降一級並標註()
    {
        var sig6 = Sig(PrtgRuleEvaluator.RuleWarning);
        var r6 = Run(sig6, 6, Evidence(sig6, 82, 82));
        Assert.Equal(0, r6.SteadyDowngraded);
        Assert.Equal(IssueSeverity.High, sig6.Severity); // 原本跨日升級

        var sig7 = Sig(PrtgRuleEvaluator.RuleWarning);
        var r7 = Run(sig7, 7, Evidence(sig7, 82, 82.5));
        Assert.Equal(1, r7.SteadyDowngraded);
        Assert.Equal(0, r7.Escalated);
        Assert.Equal(IssueSeverity.Low, sig7.Severity);
        Assert.Equal("原始說明；近 14 日第 7 次，連續第 7 日；已持續 7 日，數值持平（近 7 日已用 82%→82.5%）", sig7.SampleMessages[0]);
    }

    [Fact]
    public void High降為Medium()
    {
        var high = Sig(PrtgRuleEvaluator.RuleWarning, IssueSeverity.High);
        var r = Run(high, 8, Evidence(high, 80, 80));
        Assert.Equal(IssueSeverity.Medium, high.Severity);
        Assert.Equal(1, r.SteadyDowngraded);
        Assert.Contains("數值持平", high.SampleMessages[0]);
    }

    [Fact]
    public void Low持平_維持Low不套跨日升級_不計入持平降級筆數()
    {
        // 規則嚴重度可由使用者在畫面設成 Low：沒有可降的一級，持平仍不套跨日升級（PRTG-SPEC），但不算一筆「持平降級」
        var low = Sig(PrtgRuleEvaluator.RuleWarning, IssueSeverity.Low);
        var r = Run(low, 8, Evidence(low, 80, 80));
        Assert.Equal(IssueSeverity.Low, low.Severity);
        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(0, r.Escalated);
        Assert.Contains("數值持平", low.SampleMessages[0]);
    }

    [Theory]
    [InlineData(1.9, true)]
    [InlineData(2.0, true)]
    [InlineData(2.1, false)]
    public void 已用上升幅度兩側(double rise, bool downgraded)
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var r = Run(sig, 7, Evidence(sig, 80, 80 + rise));
        Assert.Equal(downgraded, r.SteadyDowngraded == 1);
        Assert.Equal(downgraded ? IssueSeverity.Low : IssueSeverity.High, sig.Severity);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void 可用日數兩側(int usable, bool downgraded)
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var r = Run(sig, 7, Evidence(sig, 80, 80, usable));
        Assert.Equal(downgraded, r.SteadyDowngraded == 1);
    }

    [Theory]
    [InlineData(PrtgValueSemantic.DiskPercentFree, 89.9, true)]
    [InlineData(PrtgValueSemantic.DiskPercentFree, 90.1, false)] // 可用 9.9%
    [InlineData(PrtgValueSemantic.MemoryPercentFree, 94.9, true)]
    [InlineData(PrtgValueSemantic.MemoryPercentFree, 95.1, false)]
    public void 危險區兩側(PrtgValueSemantic semantic, double lastUsed, bool downgraded)
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var r = Run(sig, 7, Evidence(sig, lastUsed, lastUsed, semantic: semantic));
        Assert.Equal(downgraded, r.SteadyDowngraded == 1);
        Assert.Equal(downgraded ? IssueSeverity.Low : IssueSeverity.High, sig.Severity);
    }

    [Theory]
    [InlineData(PrtgValueSemantic.CpuPercentUsed)]
    [InlineData(PrtgValueSemantic.PingMs)]
    public void 其他語意不降(PrtgValueSemantic semantic)
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var r = Run(sig, 7, Evidence(sig, 30, 30, semantic: semantic));
        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(IssueSeverity.High, sig.Severity);
    }

    [Fact]
    public void 降級後惡化_恢復原嚴重度並照常升級_說明不含持平()
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var r = Run(sig, 8, Evidence(sig, 80, 83)); // 上升 3 點
        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(1, r.Escalated);
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.DoesNotContain("數值持平", sig.SampleMessages[0]);
    }

    [Fact]
    public void 沒有證據_行為與原本相同()
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        var baseline = Sig(PrtgRuleEvaluator.RuleWarning);
        var withEmpty = Run(sig, 9, new Dictionary<string, PrtgSteadyEvidence>());
        var legacy = PrtgCrossDay.Apply(new[] { baseline }, Consecutive(baseline, 9), Day);

        Assert.Equal(legacy, (withEmpty.Escalated, withEmpty.Chronic));
        Assert.Equal(baseline.Severity, sig.Severity);
        Assert.Equal(baseline.SampleMessages[0], sig.SampleMessages[0]);
    }

    [Theory]
    [InlineData("down")]
    [InlineData("flapping")]
    [InlineData("disk_free_trend")]
    public void 非warning規則不受影響(string rule)
    {
        var sig = Sig(rule, IssueSeverity.Medium);
        var baseline = Sig(rule, IssueSeverity.Medium);
        var r = Run(sig, 8, Evidence(sig, 80, 80));
        PrtgCrossDay.Apply(new[] { baseline }, Consecutive(baseline, 8), Day);

        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(baseline.Severity, sig.Severity);
        Assert.Equal(baseline.SampleMessages[0], sig.SampleMessages[0]);
    }

    [Fact]
    public void ElevatesDayRisk的warning不降()
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning, elevates: true);
        var r = Run(sig, 8, Evidence(sig, 80, 80));
        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(IssueSeverity.High, sig.Severity);
    }

    [Fact]
    public void 候選只含warning且連續7日以上()
    {
        var w7 = Sig(PrtgRuleEvaluator.RuleWarning);
        var w6 = Sig(PrtgRuleEvaluator.RuleWarning);
        w6.EventKey = "prtg:warning:3001";
        var down = Sig("down");
        var hits = Consecutive(w7, 7);
        hits[w6.EventKey] = Enumerable.Range(1, 5).Select(n => Day.AddDays(-n)).ToHashSet();
        hits[down.EventKey] = Enumerable.Range(1, 9).Select(n => Day.AddDays(-n)).ToHashSet();

        var candidates = PrtgCrossDay.SteadyCandidates(new[] { w7, w6, down }, hits, Day);

        Assert.Equal(new[] { w7.EventKey }, candidates.Keys);
        Assert.Equal(7, candidates[w7.EventKey]);
    }

    [Fact]
    public void EventKey解出objid()
    {
        Assert.True(PrtgFindingMapper.TryGetTargetObjid("prtg:warning:2001", out var id));
        Assert.Equal(2001, id);
        Assert.False(PrtgFindingMapper.TryGetTargetObjid("prtg:warning", out _));
        Assert.False(PrtgFindingMapper.TryGetTargetObjid("x:warning:1", out _));
        Assert.False(PrtgFindingMapper.TryGetTargetObjid("prtg:warning:abc", out _));
    }

    // ---- 管線組裝（BuildSteadyEvidence）：數值走 PrtgValueAssessment

    private static readonly DateTime Completed = DateTime.Today.AddDays(-1);

    private void SeedDisk(long sensor, long device, int days, Func<int, double> freeByDaysAgo, int skipRecentDays = 0)
    {
        _store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = sensor, DeviceObjid = device, Name = $"S{sensor}", SensorType = "SNMP Disk Free",
                Category = PrtgSensorCategories.Disk, Status = "Warning" }
        }, DateTime.Now);
        for (var d = 0; d < 10; d++)
        {
            _store.ReplaceHostMapForDate(Completed.AddDays(-d), new List<PrtgHostMapRow>
            {
                new() { DeviceObjid = device, HostId = 11, HostName = "h11", MapStatus = PrtgMapStatus.Ok }
            });
        }
        var rows = new List<PrtgValueRow>();
        for (var d = skipRecentDays; d < days; d++)
        for (var h = 0; h < 24; h++)
            rows.Add(new PrtgValueRow
            {
                SensorObjid = sensor, PeriodStart = Completed.AddDays(-d).AddHours(h), AvgValue = freeByDaysAgo(d),
                Coverage = 100, Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Now
            });
        _store.UpsertValues(rows);
    }

    private (LogIssueSignature Sig, Dictionary<string, HashSet<DateTime>> Hits) WarningFinding(long sensor)
    {
        var sig = Sig(PrtgRuleEvaluator.RuleWarning);
        sig.EventKey = $"prtg:warning:{sensor}";
        var hits = new Dictionary<string, HashSet<DateTime>>
        {
            [sig.EventKey] = Enumerable.Range(1, 9).Select(n => Completed.AddDays(-n)).ToHashSet()
        };
        return (sig, hits);
    }

    private IReadOnlyDictionary<string, PrtgSteadyEvidence> Build(LogIssueSignature sig, Dictionary<string, HashSet<DateTime>> hits,
        IReadOnlyDictionary<string, PrtgValueSemantic>? overrides = null) =>
        PrtgDailyPipeline.BuildSteadyEvidence(new[] { sig }, hits, Completed, new Dictionary<long, long> { [5] = 11 },
            new HashSet<long> { 11 }, _store, overrides ?? new Dictionary<string, PrtgValueSemantic>(), CancellationToken.None);

    [Fact]
    public void 管線組裝_持平磁碟取得證據並降級()
    {
        SeedDisk(2001, 5, 7, d => 18 + d * 0.1); // 可用 18%，每日微幅變動
        var (sig, hits) = WarningFinding(2001);

        var evidence = Build(sig, hits);
        var r = PrtgCrossDay.ApplyDetailed(new[] { sig }, hits, Completed, evidence);

        Assert.Equal(1, r.SteadyDowngraded);
        Assert.Equal(IssueSeverity.Low, sig.Severity);
        Assert.Contains("數值持平", sig.SampleMessages[0]);
    }

    [Fact]
    public void 管線組裝_可用日不足3天_不降()
    {
        SeedDisk(2001, 5, 3, _ => 18);
        var (sig, hits) = WarningFinding(2001);

        var r = PrtgCrossDay.ApplyDetailed(new[] { sig }, hits, Completed, Build(sig, hits));

        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(IssueSeverity.High, sig.Severity);
    }

    [Theory]
    [InlineData(1, true)]    // 最後可用日是評估日前一天：仍可代表現況
    [InlineData(2, false)]   // 數值已斷兩天（例如快照停擺）：幾天前的值不能代表現在，不降
    public void 管線組裝_最後可用日早於評估日前一天_不取證據(int skipRecentDays, bool expectEvidence)
    {
        SeedDisk(2001, 5, 7, _ => 18, skipRecentDays);   // 可用日 6／5 天，都超過下限 4 天
        var (sig, hits) = WarningFinding(2001);

        var r = PrtgCrossDay.ApplyDetailed(new[] { sig }, hits, Completed, Build(sig, hits));

        Assert.Equal(expectEvidence ? 1 : 0, r.SteadyDowngraded);
        Assert.Equal(expectEvidence ? IssueSeverity.Low : IssueSeverity.High, sig.Severity);
    }

    [Fact]
    public void 管線組裝_未知單位與非候選都不查出證據()
    {
        SeedDisk(2001, 5, 7, _ => 18);
        var (sig, hits) = WarningFinding(2001);
        hits[sig.EventKey] = Enumerable.Range(1, 3).Select(n => Completed.AddDays(-n)).ToHashSet(); // 連續僅 4 日，非候選

        Assert.Empty(Build(sig, hits));
    }

    [Fact]
    public void 管線組裝_數值查詢擲例外_不降也不擲出()
    {
        SeedDisk(2001, 5, 7, _ => 18);
        var (sig, hits) = WarningFinding(2001);

        var evidence = PrtgDailyPipeline.BuildSteadyEvidence(new[] { sig }, hits, Completed, new Dictionary<long, long> { [5] = 11 },
            new HashSet<long> { 11 }, _store, null!, CancellationToken.None); // 評估入口建構擲例外

        Assert.Empty(evidence);
        var r = PrtgCrossDay.ApplyDetailed(new[] { sig }, hits, Completed, evidence);
        Assert.Equal(0, r.SteadyDowngraded);
        Assert.Equal(IssueSeverity.High, sig.Severity);
    }
}
