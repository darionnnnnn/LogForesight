using System.Globalization;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgMessageDateTimeParserTests
{
    [Theory]
    [InlineData(PrtgMessageDateTimeParser.Dmy24, "31.12.2026 23:59", 2026, 12, 31, 23)]
    [InlineData(PrtgMessageDateTimeParser.Dmy12, "31.12.2026 11:59 PM", 2026, 12, 31, 23)]
    [InlineData(PrtgMessageDateTimeParser.Mdy24, "12/31/2026 23:59", 2026, 12, 31, 23)]
    [InlineData(PrtgMessageDateTimeParser.Mdy12, "12/31/2026 11:59 PM", 2026, 12, 31, 23)]
    [InlineData(PrtgMessageDateTimeParser.Ymd24, "2026-12-31 23:59", 2026, 12, 31, 23)]
    [InlineData(PrtgMessageDateTimeParser.Ymd12, "2026-12-31 11:59 PM", 2026, 12, 31, 23)]
    public void ParsesOnlyConfiguredCalendarAndClockConvention(string format, string text,
        int year, int month, int day, int hour)
    {
        var ok = PrtgMessageDateTimeParser.TryParse(text, format, CultureInfo.GetCultureInfo("en-US"),
            TimeZoneInfo.Utc, out var result, out var reason);

        Assert.True(ok);
        Assert.Equal("verified", reason);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, 59, 0, TimeSpan.Zero), result);
    }

    [Theory]
    [InlineData(PrtgMessageDateTimeParser.Dmy24, "12/31/2026 23:59")]
    [InlineData(PrtgMessageDateTimeParser.Mdy24, "2026-12-31 23:59")]
    [InlineData(PrtgMessageDateTimeParser.Ymd24, "2026-12-31")]
    public void RejectsWrongDateOrderAndDateOnly(string format, string text)
    {
        Assert.False(PrtgMessageDateTimeParser.TryParse(text, format, CultureInfo.GetCultureInfo("en-US"),
            TimeZoneInfo.Utc, out _, out _));
    }

    [Fact]
    public void ExplicitOffsetMustAgreeWithConfiguredSourceZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture-plus-eight", TimeSpan.FromHours(8),
            "fixture plus eight", "fixture plus eight");

        Assert.True(PrtgMessageDateTimeParser.TryParse("2026-12-31 23:59 +08:00",
            PrtgMessageDateTimeParser.Ymd24, CultureInfo.GetCultureInfo("en-US"), zone, out _, out _));
        Assert.False(PrtgMessageDateTimeParser.TryParse("2026-12-31 23:59Z",
            PrtgMessageDateTimeParser.Ymd24, CultureInfo.GetCultureInfo("en-US"), zone, out _, out var reason));
        Assert.Equal("source-time-zone-offset-mismatch", reason);
    }

    [Fact]
    public void MessageFingerprintChangesIndependentlyOfRawTimestampAuthority()
    {
        var policy = new PrtgMonitoringPolicy
        {
            CoreSystemId = "core", SourceGeneration = "source-1",
            EndpointHint = EfPrtgObservationStore.SourceHintFor("https://fixture.example"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), HostIds = [1], SensorIds = [10],
            SourceTimeZoneId = TimeZoneInfo.Utc.Id, SourceCultureName = "en-US",
            RawTimestampTimeZoneId = TimeZoneInfo.Utc.Id, AnalysisTimeZoneId = TimeZoneInfo.Utc.Id,
            TimeBasisEvidenceReference = "raw-source-test", MessageDateFormat = PrtgMessageDateTimeParser.Ymd24,
            MessageTimeAuthorityRevision = 1
        };
        var rawAuthority = policy.SourceAuthorityFingerprint("https://fixture.example");
        var messageAuthority = policy.MessageTimeFingerprint("https://fixture.example");
        policy.MessageDateFormat = PrtgMessageDateTimeParser.Mdy24;
        policy.MessageTimeAuthorityRevision++;

        Assert.NotEqual(messageAuthority, policy.MessageTimeFingerprint("https://fixture.example"));
        Assert.Equal(rawAuthority, policy.SourceAuthorityFingerprint("https://fixture.example"));
    }
}
