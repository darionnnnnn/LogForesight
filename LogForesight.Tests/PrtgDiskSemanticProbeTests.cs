using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskSemanticProbeTests
{
    private const string ValidChannels = "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true}]}";
    private static readonly DateTime Start = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(1);
    private const string History = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46288.3333333333,\"Free Space_raw\":72.5}]}";

    private static PrtgDiskSemanticProbe Probe(string channels, Func<string>? history = null, Action<string>? called = null) =>
        new((path, ct) => { ct.ThrowIfCancellationRequested(); called?.Invoke(path); return Task.FromResult(path.Contains("channels") ? channels : history?.Invoke() ?? History); },
            new("Taipei Standard Time", "Taipei Standard Time", "8", "Free Space"));

    [Fact]
    public async Task ExplicitPercentPrimaryAndMatchingPersistedPointCanBeVerified()
    {
        var probe = Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"Percent\",\"scaling\":1}]}");
        var result = await probe.ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.Equal("Percent", result.Unit);
        Assert.Equal("descending-danger", result.Direction);
        Assert.Equal(1, result.Scale);
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Contains("72.5", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task NullOrAmbiguousUnitNeedsManualReview(string unitJson)
    {
        var unit = unitJson == "null" ? "null" : "\"\"";
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":" + unit + "}]}").ProbeAsync(101, Start, End);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
    }

    [Fact]
    public async Task NoPrimaryChannelNeedsManualReview()
    {
        var result = await Probe("{\"channels\":[]}").ProbeAsync(101, Start, End);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
    }

    [Fact]
    public async Task PersistedMismatchIsReported()
    {
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\"}]}").ProbeAsync(101, Start, End, [new(Start, 50)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Mismatch, result.Status);
        Assert.False(result.ValuesMatch);
    }

    [Fact]
    public async Task PercentWithoutPersistedComparisonCannotAutoVerify()
    {
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1}]}").ProbeAsync(101, Start, End);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Equal("descending-danger", result.Direction);
    }

    [Theory]
    [InlineData("Used Space")]
    [InlineData("Total Space")]
    [InlineData("Space")]
    [InlineData("Free and Used")]
    public async Task NonFreeOrAmbiguousChannelCannotAutoVerify(string channelName)
    {
        var json = "{\"channels\":[{\"objid\":8,\"name\":\"" + channelName + "\",\"unit\":\"%\",\"scaling\":1}]}";
        var result = await Probe(json).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.Direction);
    }

    [Fact]
    public async Task MissingOrInvalidScaleCannotAutoVerify()
    {
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\"}]}").ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
    }

    [Fact]
    public async Task HistoricOaDateRequiresExplicitSourceWallTimeBasis()
    {
        var oa = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Local).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var history = "{\"histdata\":[{\"datetime_raw\":" + oa + ",\"Free Space_raw\":72.5}]}";
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1}]}", () => history)
            .ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
    }

    [Fact]
    public async Task RequestsAreCappedAtTwoAndCancellationPropagates()
    {
        var count = 0;
        var result = await Probe("{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\"}]}", called: _ => count++).ProbeAsync(101, Start, End);
        Assert.Equal(2, count);
        Assert.Equal(PrtgDiskSemanticProbe.MaxApiCalls, count);

        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe("{}").ProbeAsync(101, Start, End, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task MultiChannelPrimaryCanVerifyExactCaptionRegardlessOfPosition()
    {
        const string channels = "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true},{\"objid\":9,\"name\":\"Used Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":false}]}";
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46288.3333333333,\"Free Space_raw\":72.5,\"Used Space_raw\":27.5}]}";
        var result = await Probe(channels, () => history).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.Equal("Free Space", result.ChannelName);
        Assert.Equal("%", result.Unit);
        Assert.Equal(1, result.Scale);
    }

    [Fact]
    public async Task MultiChannelSecondPrimaryCannotCompareFirstHistoricValue()
    {
        const string channels = "{\"channels\":[{\"objid\":9,\"name\":\"Used Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":false},{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":\"1\"}]}";
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46288.3333333333,\"value_raw\":[27.5,72.5]}]}";
        var result = await Probe(channels, () => history).ProbeAsync(101, Start, End, [new(Start, 27.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
        Assert.Equal("Free Space", result.ChannelName);
    }

    [Fact]
    public async Task MultiplePrimaryMarkersAreAmbiguous()
    {
        const string channels = "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":\"true\"},{\"objid\":9,\"name\":\"Other\",\"unit\":\"%\",\"scaling\":1,\"primary\":1}]}";
        var result = await Probe(channels).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ChannelIdentifier);
        Assert.Null(result.ValuesMatch);
    }

    [Fact]
    public async Task UnknownTimeBasisNeverUsesServiceLocalZoneOrInfersOneChannelPrimary()
    {
        var calls = 0;
        var probe = new PrtgDiskSemanticProbe((_, _) => { calls++; return Task.FromResult(ValidChannels); });
        var result = await probe.ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
        Assert.Equal(0, result.ComparedPointCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NonFirstPrimaryUsesUniqueCaptionRawAndExplicitApiZone()
    {
        const string channels = "{\"channels\":[{\"objid\":9,\"name\":\"Used Space\",\"unit\":\"%\",\"scaling\":1},{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true}]}";
        var paths = new List<string>();
        var result = await Probe(channels, called: paths.Add).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.Contains("avg=0&usecaption=1", paths[1]);
        Assert.Contains("2026-09-23-08-00-00", paths[1]);
    }

    [Theory]
    [InlineData("{\"histdata\":[{\"datetime_raw\":46288.3333333333,\"value_raw\":[72.5]}]}")]
    [InlineData("{\"histdata\":[{\"datetime_raw\":46288.3333333333,\"Free Space_raw\":72.5,\"Free Space_raw\":1}]}")]
    [InlineData("{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00\",\"Free Space_raw\":72.5}]}")]
    [InlineData("{\"histdata\":[{\"datetime_raw\":46288.3333333333,\"Used Space_raw\":72.5}]}")]
    public async Task MissingOrAmbiguousPhysicalCaptionTimeCannotVerify(string history)
    {
        var result = await Probe(ValidChannels, () => history).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3599)]
    [InlineData(-60)]
    public async Task AValueInSameHourIsNotProofOfSamePhysicalMeasurement(int seconds)
    {
        var result = await Probe(ValidChannels).ProbeAsync(101, Start, End, [new(Start.AddSeconds(seconds), 72.5)]);
        Assert.NotEqual(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.False(result.ValuesMatch);
    }

    [Fact]
    public async Task DisplayTimestampCannotOverrideDifferentDayRawMeasurement()
    {
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46287.3333333333,\"Free Space_raw\":72.5}]}";
        var result = await Probe(ValidChannels, () => history).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Mismatch, result.Status);
        Assert.False(result.ValuesMatch);
    }
}
