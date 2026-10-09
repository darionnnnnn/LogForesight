using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgNativePrimarySnapshotGuardTests
{
    // Synthetic application-contract fixtures. These do not authorize an installed PRTG source.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedChannelMustBePresentAndEqualInEverySnapshot(bool switched)
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var context = new PrtgTrustedSnapshotContext(77, "source-r4", "resource-r2", "channel-r3-77",
            "epoch-r7", "semantic-v2", "strategy-v5", 15, hour, "UTC", TimeSpan.FromSeconds(60),
            hour.AddSeconds(10), hour.AddSeconds(15), seconds => TimeSpan.FromSeconds(seconds),
            AnalysisTimeZoneId: "UTC", SelectedPrimaryChannelId: "3");
        var row = new Dictionary<string, object>
        {
            ["objid"] = 77, ["lastvalue_raw"] = 8,
            ["lastcheck_raw"] = hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            ["interval_raw"] = 60, ["status_raw"] = 3
        };
        if (switched) row["primarychannel_raw"] = 4;
        var parser = new PrtgTrustedSnapshotParser();
        var result = parser.Parse(JsonSerializer.Serialize(row), context);
        Assert.False(result.AcceptedForAccumulation);
        Assert.Equal(switched ? "native_primarychannel_id_mismatch" : "missing_native_primarychannel_id",
            result.RejectionReason);
        var accumulator = new PrtgSnapshotAccumulator();
        parser.AddToAccumulator(JsonSerializer.Serialize(row), context, accumulator, out var rejected);
        Assert.NotNull(rejected);
        Assert.Empty(accumulator.DrainAll(4, hour.AddHours(1)));
    }
}
