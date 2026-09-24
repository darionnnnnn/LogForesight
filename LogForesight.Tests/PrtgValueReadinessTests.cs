using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgValueReadinessTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);

    [Fact]
    public void EvaluatesEachSensorFromItsOwnHistory()
    {
        var a = PrtgValueReadiness.Evaluate(Input(101, FullWindowHours(AsOf)), AsOf);
        var b = PrtgValueReadiness.Evaluate(Input(102, FullWindowHours(AsOf, days: 6)), AsOf);

        Assert.Equal(PrtgValueReadinessStatus.Ready, a.Status);
        Assert.Equal(28, a.UsableDays);
        Assert.Equal(PrtgValueReadinessStatus.InsufficientData, b.Status);
        Assert.Equal(6, b.UsableDays);
    }

    [Fact]
    public void ReportsSixOfTwentyEightWhenOnlySixDaysHaveMappingAndData()
    {
        var hours = FullWindowHours(AsOf, days: 6).ToArray();
        var maps = Enumerable.Range(1, 6)
            .ToDictionary(i => AsOf.Date.AddDays(-i), _ => (long?)42);

        var result = PrtgValueReadiness.Evaluate(Input(107, hours) with { DailyMappedHostIds = maps }, AsOf);

        Assert.Equal(PrtgValueReadinessStatus.InsufficientData, result.Status);
        Assert.Equal(6, result.UsableDays);
        Assert.Equal(28, result.RequiredDays);
    }

    [Fact]
    public void RejectsSampledCoverageBelowThreshold()
    {
        var hours = Enumerable.Range(0, 28).SelectMany(day => Enumerable.Range(0, 12)
            .Select(hour => new PrtgReadinessHour(AsOf.Date.AddDays(-28 + day).AddHours(hour),
                PrtgDataQuality.Sampled, 25)));

        var result = PrtgValueReadiness.Evaluate(Input(103, hours), AsOf);

        Assert.Equal(0, result.UsableDays);
        Assert.Equal(PrtgValueReadinessStatus.InsufficientData, result.Status);
    }

    [Fact]
    public void UnknownWhenHistoricalHostMappingChanged()
    {
        var input = Input(104, FullWindowHours(AsOf));
        var maps = input.DailyMappedHostIds.ToDictionary(x => x.Key, x => x.Value);
        maps[AsOf.Date.AddDays(-3)] = 77;

        var result = PrtgValueReadiness.Evaluate(input with { DailyMappedHostIds = maps }, AsOf);

        Assert.Equal(PrtgValueReadinessStatus.Unknown, result.Status);
        Assert.Contains("變更", result.Reason);
    }

    [Fact]
    public void UnknownWhenMappingIsMissingOnPopulatedDay()
    {
        var input = Input(108, FullWindowHours(AsOf, days: 6));
        var maps = input.DailyMappedHostIds
            .Where(x => x.Key != AsOf.Date.AddDays(-3))
            .ToDictionary(x => x.Key, x => x.Value);

        var result = PrtgValueReadiness.Evaluate(input with { DailyMappedHostIds = maps }, AsOf);

        Assert.Equal(PrtgValueReadinessStatus.Unknown, result.Status);
        Assert.Equal(6, result.UsableDays);
    }

    [Fact]
    public void EmptyHistoryIsInsufficientAndZeroDenominatorIsNotReady()
    {
        var result = PrtgValueReadiness.Evaluate(Input(105, Array.Empty<PrtgReadinessHour>()), AsOf);

        Assert.Equal(PrtgValueReadinessStatus.InsufficientData, result.Status);
        Assert.Equal(0, result.UsableDays);
        Assert.False(result.SemanticReady);
    }

    [Fact]
    public void DataReadinessDoesNotClaimUnknownMeasurementSemantics()
    {
        var result = PrtgValueReadiness.Evaluate(Input(106, FullWindowHours(AsOf)), AsOf);

        Assert.Equal(PrtgValueReadinessStatus.Ready, result.Status);
        Assert.False(result.SemanticReady);
        Assert.Contains("語意待確認", result.Reason);
    }

    private static PrtgValueReadinessInput Input(long id, IEnumerable<PrtgReadinessHour> hours)
    {
        var maps = Enumerable.Range(1, PrtgValueReadiness.WindowDays)
            .ToDictionary(i => AsOf.Date.AddDays(-i), _ => (long?)42);
        return new PrtgValueReadinessInput(id, 500, PrtgSensorCategories.Disk, true, true,
            false, false, true, null, null, false, hours.ToArray(), maps);
    }

    private static IEnumerable<PrtgReadinessHour> FullWindowHours(DateTime asOf, int days = 28) =>
        Enumerable.Range(0, days).SelectMany(day => Enumerable.Range(0, 12)
            .Select(hour => new PrtgReadinessHour(asOf.Date.AddDays(-days + day).AddHours(hour), PrtgDataQuality.Ok, null)));
}
