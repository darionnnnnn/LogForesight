using LogForesight.Core;
using LogForesight.Core.Service;
using System.Globalization;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskSemanticProbeTests
{
    private const string ValidChannels = "{\"channels\":[{\"objid\":8,\"name\":\"Free Space\",\"unit\":\"%\",\"scaling\":1,\"primary\":true}]}";
    private static readonly DateTime Start = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(1);
    private const string History = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46288.3333333333,\"Free Space_raw\":72.5}]}";

    private static PrtgDiskSemanticProbe Probe(string channels, Func<string>? history = null, Action<string>? called = null,
        PrtgDiskSemanticSourceContract? contract = null) =>
        new((path, ct) => { ct.ThrowIfCancellationRequested(); called?.Invoke(path); return Task.FromResult(path.Contains("channels") ? channels : history?.Invoke() ?? History); },
            contract ?? new("Taipei Standard Time", "Taipei Standard Time", "Taipei Standard Time", "source-v1", "8", "Free Space"));

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
        if (seconds < 0)
        {
            Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
            Assert.Null(result.ValuesMatch);
        }
        else Assert.False(result.ValuesMatch);
    }

    [Fact]
    public async Task DisplayTimestampCannotOverrideDifferentDayRawMeasurement()
    {
        const string history = "{\"histdata\":[{\"datetime\":\"2026-09-23 08:00:00 - 09:00:00\",\"datetime_raw\":46287.3333333333,\"Free Space_raw\":72.5}]}";
        var result = await Probe(ValidChannels, () => history).ProbeAsync(101, Start, End, [new(Start, 72.5)]);
        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
    }

    [Fact]
    public async Task TwentyFiveHourAnalysisDayIsAllowedWhenBoundariesAreUnambiguous()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var day = new DateOnly(2026, 11, 1);
        var startWall = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endWall = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startWall, zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endWall, zone);
        var history = "{\"histdata\":[{\"datetime_raw\":" + startUtc.ToOADate().ToString("R", CultureInfo.InvariantCulture) +
            ",\"Free Space_raw\":72.5}]}";
        var result = await Probe(ValidChannels, () => history,
            contract: new("UTC", "Eastern Standard Time", "Eastern Standard Time", "source-v1", "8", "Free Space"))
            .ProbeAsync(101, startUtc, endUtc, [new(startUtc, 72.5)]);
        Assert.Equal(TimeSpan.FromHours(25), endUtc - startUtc);
        Assert.Equal(PrtgDiskSemanticProbeStatus.Verified, result.Status);
        Assert.True(result.ValuesMatch);
    }

    [Fact]
    public async Task AmbiguousRawFoldPointInvalidatesWholeComparisonEvenWhenAnotherPointMatches()
    {
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var foldWall = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var history = "{\"histdata\":[{\"datetime_raw\":" + start.ToOADate().ToString("R", CultureInfo.InvariantCulture) +
            ",\"Free Space_raw\":72.5},{\"datetime_raw\":" + foldWall.ToOADate().ToString("R", CultureInfo.InvariantCulture) +
            ",\"Free Space_raw\":70.0}]}";
        var result = await Probe(ValidChannels, () => history,
            contract: new("Eastern Standard Time", "UTC", "Eastern Standard Time", "source-v1", "8", "Free Space"))
            .ProbeAsync(101, start, start.AddHours(25), [new(start, 72.5)]);

        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Null(result.ValuesMatch);
        Assert.Equal(0, result.ComparedPointCount);
    }

    [Fact]
    public async Task IntervalBeyondBoundedAnalysisDayIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Probe(ValidChannels).ProbeAsync(
            101, Start, Start.AddHours(26).AddTicks(1), [new(Start, 72.5)]));
    }

    [Fact]
    public async Task AmbiguousSourceApiFoldBoundaryCannotReachHistoricEndpoint()
    {
        var calls = new List<string>();
        var start = new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc);
        var result = await Probe(ValidChannels, called: calls.Add,
            contract: new("UTC", "Eastern Standard Time", "Eastern Standard Time", "source-v1", "8", "Free Space"))
            .ProbeAsync(101, start, start.AddHours(1), [new(start, 72.5)]);

        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Contains("source-api-time-ambiguous", result.Evidence);
        Assert.Single(calls);
    }

    [Theory]
    [InlineData("", "UTC", "UTC", "source-v1", "8", "Free Space")]
    [InlineData("UTC", "UTC", "", "source-v1", "8", "Free Space")]
    [InlineData("UTC", "UTC", "UTC", "", "8", "Free Space")]
    public async Task MissingAnalysisOrSourceGenerationAuthorityCannotCompare(string rawZone, string apiZone,
        string analysisZone, string sourceGeneration, string channelId, string caption)
    {
        var calls = new List<string>();
        var result = await Probe(ValidChannels, called: calls.Add,
            contract: new(rawZone, apiZone, analysisZone, sourceGeneration, channelId, caption))
            .ProbeAsync(101, Start, End, [new(Start, 72.5)]);

        Assert.Equal(PrtgDiskSemanticProbeStatus.NeedsManualReview, result.Status);
        Assert.Contains("source-contract-unconfirmed", result.Evidence);
        Assert.Null(result.ValuesMatch);
        Assert.Single(calls);
    }
}
