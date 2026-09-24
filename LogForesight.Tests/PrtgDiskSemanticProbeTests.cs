using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskSemanticProbeTests
{
    private static readonly DateTime Start = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(1);
    private const string History = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46287.3333333333,\"value_raw\":[72.5]}]}";

    private static PrtgDiskSemanticProbe Probe(string channels, Func<string>? history = null, Action<string>? called = null) =>
        new((path, ct) => { ct.ThrowIfCancellationRequested(); called?.Invoke(path); return Task.FromResult(path.Contains("channels") ? channels : history?.Invoke() ?? History); });

    [Fact]
    public async Task ExplicitPercentPrimaryAndMatchingPersistedPointCanBeVerified()
    {
        var probe = Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"Percent\",\"scaling\":1}]}");
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
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":" + unit + "}]}").ProbeAsync(101, Start, End);
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
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\"}]}").ProbeAsync(101, Start, End, [new(Start, 50)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Mismatch, result.Status);
        Assert.False(result.ValuesMatch);
    }

    [Fact]
    public async Task PercentWithoutPersistedComparisonCannotAutoVerify()
    {
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\",\"scaling\":1}]}").ProbeAsync(101, Start, End);
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
        var json = "{\"channels\":[{\"objid\":8,\"channel\":\"" + channelName + "\",\"unit\":\"%\",\"scaling\":1}]}";
        var result = await Probe(json).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.Direction);
    }

    [Fact]
    public async Task MissingOrInvalidScaleCannotAutoVerify()
    {
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\"}]}").ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
    }

    [Fact]
    public async Task HistoricOaDateIsHandledWithFetchStyleLocalWallTime()
    {
        var oa = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Local).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var history = "{\"histdata\":[{\"datetime_raw\":" + oa + ",\"value_raw\":[72.5]}]}";
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\",\"scaling\":1}]}", () => history)
            .ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
    }

    [Fact]
    public async Task RequestsAreCappedAtTwoAndCancellationPropagates()
    {
        var count = 0;
        var result = await Probe("{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\"}]}", called: _ => count++).ProbeAsync(101, Start, End);
        Assert.Equal(2, count);
        Assert.Equal(PrtgDiskSemanticProbe.MaxApiCalls, count);

        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe("{}").ProbeAsync(101, Start, End, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task MultiChannelFirstPrimaryCanVerifyFirstHistoricValue()
    {
        const string channels = "{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true},{\"objid\":9,\"channel\":\"Used Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":false}]}";
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46287.3333333333,\"value_raw\":[72.5,27.5]}]}";
        var result = await Probe(channels, () => history).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.Equal("Free Space", result.ChannelName);
        Assert.Equal("%", result.Unit);
        Assert.Equal(1, result.Scale);
    }

    [Fact]
    public async Task MultiChannelSecondPrimaryCannotCompareFirstHistoricValue()
    {
        const string channels = "{\"channels\":[{\"objid\":9,\"channel\":\"Used Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":false},{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":\"1\"}]}";
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46287.3333333333,\"value_raw\":[27.5,72.5]}]}";
        var result = await Probe(channels, () => history).ProbeAsync(101, Start, End, [new(Start, 27.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
        Assert.Equal("Free Space", result.ChannelName);
    }

    [Fact]
    public async Task MultiplePrimaryMarkersAreAmbiguous()
    {
        const string channels = "{\"channels\":[{\"objid\":8,\"channel\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":\"true\"},{\"objid\":9,\"channel\":\"Other\",\"unit\":\"%\",\"scaling\":1,\"primary\":1}]}";
        var result = await Probe(channels).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ChannelIdentifier);
        Assert.Null(result.ValuesMatch);
    }
}
