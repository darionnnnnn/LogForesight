using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSnapshotParserTests
{
    private static readonly DateTime Hour = new(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
    private readonly PrtgTrustedSnapshotParser _parser = new();

    private static PrtgTrustedSnapshotContext Context(DateTime? received = null, DateTime? asOf = null,
        string zone = "UTC", int minutes = 15, DateTime? effective = null, bool probe = true,
        Func<double, TimeSpan?>? intervalReader = null, long sensorId = 77, string? channelGeneration = null) => new(
        sensorId, "source-r4", "resource-r2", channelGeneration ?? $"channel-r3-{sensorId}", "epoch-r7", "semantic-v2", "strategy-v5", minutes,
        effective ?? Hour, zone, TimeSpan.FromSeconds(60), received ?? Hour.AddSeconds(10), asOf ?? Hour.AddSeconds(15),
        intervalReader ?? (seconds => TimeSpan.FromSeconds(seconds)), probe, "UTC");

    private static string Row(DateTime? measured = null, string value = "17.25", string interval = "60",
        object? rawStatus = null, string? displayStatus = null, bool? active = null, string objid = "77",
        bool stringDate = true, string timestampProperty = "lastcheck_raw", string? formattedInterval = null, bool includeRawStatus = true)
    {
        var oa = (measured ?? Hour).ToOADate().ToString("R", CultureInfo.InvariantCulture);
        var properties = new Dictionary<string, object?>
        {
            ["objid"] = long.Parse(objid, CultureInfo.InvariantCulture),
            ["lastvalue_raw"] = value,
            [timestampProperty] = stringDate ? oa : double.Parse(oa, CultureInfo.InvariantCulture),
            ["interval_raw"] = interval,
        };
        if (includeRawStatus) properties["status_raw"] = rawStatus ?? 3;
        if (displayStatus != null) properties["status"] = displayStatus;
        if (active.HasValue) properties["active"] = active.Value;
        if (formattedInterval != null) properties["interval"] = formattedInterval;
        return JsonSerializer.Serialize(properties);
    }

    [Fact]
    public void 可信欄位字串與數字時間轉來源時區並經checkpoint累加()
    {
        var text = Row(stringDate: true);
        var parsedText = _parser.Parse(text, Context());
        Assert.True(parsedText.AcceptedForAccumulation, parsedText.RejectionReason);
        var numeric = _parser.Parse(Row(stringDate: false), Context());
        Assert.True(numeric.AcceptedForAccumulation, numeric.RejectionReason);
        var rawDateTime = _parser.Parse(Row(timestampProperty: "rawdatetime"), Context());
        Assert.True(rawDateTime.AcceptedForAccumulation, rawDateTime.RejectionReason);
        Assert.Equal(parsedText.Sample!.MeasuredAt, numeric.Sample!.MeasuredAt);
        Assert.Equal(parsedText.Sample.PhysicalMeasurementId, rawDateTime.Sample!.PhysicalMeasurementId);
        Assert.Equal(DateTimeKind.Utc, parsedText.Sample.MeasuredAt.Kind);
        Assert.Equal(DateTimeKind.Utc, parsedText.Sample.ReceivedAt.Kind);

        var accumulator = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            _parser.AddToAccumulator(text, Context(), accumulator, out var rejected));
        Assert.Null(rejected);
        var checkpoint = accumulator.Capture();
        var resumed = new PrtgSnapshotAccumulator();
        resumed.Restore(checkpoint);
        var row = Assert.Single(resumed.DrainAll(4, Hour.AddHours(1)));
        Assert.Equal(25.0, row.Coverage!.Value);
        Assert.Equal(17.25, row.AvgValue!.Value);
        Assert.Equal(1, PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots.Count);
    }

    [Fact]
    public void ExplicitBindingRejectsSnapshotWhoseNativePrimaryChannelSwitched()
    {
        var context = Context() with { SelectedPrimaryChannelId = "3" };
        var switched = JsonSerializer.Serialize(new
        {
            objid = 77, lastvalue_raw = "17.25", lastcheck_raw = Hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel_raw = 4
        });

        Assert.Equal(PrtgTrustedSnapshotParser.NativePrimaryChannelMismatch,
            _parser.Parse(switched, context).RejectionReason);
    }

    [Fact]
    public void ExplicitBindingRejectsSnapshotWithoutNativePrimaryChannelEvidence()
    {
        var context = Context() with { SelectedPrimaryChannelId = "3" };

        Assert.Equal(PrtgTrustedSnapshotParser.MissingNativePrimaryChannelId,
            _parser.Parse(Row(), context).RejectionReason);
    }

    [Fact]
    public void ExplicitBindingAcceptsOnlyNumericNativePrimaryChannelAliasesAndRejectsDisagreement()
    {
        var context = Context() with { SelectedPrimaryChannelId = "3" };
        var primaryChannel = JsonSerializer.Serialize(new
        {
            objid = 77, lastvalue_raw = "17.25", lastcheck_raw = Hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel = 3
        });
        var matchingAliases = JsonSerializer.Serialize(new
        {
            objid = 77, lastvalue_raw = "17.25", lastcheck_raw = Hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel = 3, primarychannel_raw = 3
        });
        var disagreeingAliases = JsonSerializer.Serialize(new
        {
            objid = 77, lastvalue_raw = "17.25", lastcheck_raw = Hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel = 3, primarychannel_raw = 4
        });
        var stringAlias = JsonSerializer.Serialize(new
        {
            objid = 77, lastvalue_raw = "17.25", lastcheck_raw = Hour.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel = "3"
        });

        Assert.True(_parser.Parse(primaryChannel, context).AcceptedForAccumulation);
        Assert.True(_parser.Parse(matchingAliases, context).AcceptedForAccumulation);
        Assert.Equal(PrtgTrustedSnapshotParser.MissingNativePrimaryChannelId,
            _parser.Parse(disagreeingAliases, context).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.MissingNativePrimaryChannelId,
            _parser.Parse(stringAlias, context).RejectionReason);
    }

    [Fact]
    public void PRTG_OADate依明確來源時區轉UTC且DST模糊或不存在時間拒收()
    {
        var zone = "Eastern Standard Time";
        var local = new DateTime(2026, 10, 5, 6, 0, 0);
        var context = Context(zone: zone, received: new DateTime(2026, 10, 5, 10, 0, 10, DateTimeKind.Utc),
            asOf: new DateTime(2026, 10, 5, 10, 0, 15, DateTimeKind.Utc));
        var parsed = _parser.Parse(Row(local), context);
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), parsed.Sample?.MeasuredAt);

        var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0);
        var dstContext = Context(zone: zone, received: new DateTime(2026, 11, 1, 7, 30, 10, DateTimeKind.Utc),
            asOf: new DateTime(2026, 11, 1, 7, 30, 15, DateTimeKind.Utc));
        Assert.Equal(PrtgTrustedSnapshotParser.AmbiguousMeasurementTime,
            _parser.Parse(Row(ambiguous), dstContext).RejectionReason);

        var invalid = new DateTime(2026, 3, 8, 2, 30, 0);
        var invalidContext = Context(zone: zone, received: new DateTime(2026, 3, 8, 8, 30, 10, DateTimeKind.Utc),
            asOf: new DateTime(2026, 3, 8, 8, 30, 15, DateTimeKind.Utc));
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidMeasurementTimeInZone,
            _parser.Parse(Row(invalid), invalidContext).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.UnknownTimeZone,
            _parser.Parse(Row(), Context(zone: "unverified-zone-name")).RejectionReason);
    }

    [Fact]
    public void 原始時間與分析桶分別採明確時區並保留跨日及半小時offset()
    {
        var taipeiMeasured = new DateTime(2026, 10, 5, 16, 30, 0, DateTimeKind.Utc);
        var taipeiContext = Context(received: taipeiMeasured.AddSeconds(5), asOf: taipeiMeasured.AddSeconds(10),
            effective: new DateTime(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc)) with { AnalysisTimeZoneId = "Taipei Standard Time" };
        var parsed = _parser.Parse(Row(taipeiMeasured), taipeiContext);
        Assert.True(parsed.AcceptedForAccumulation, parsed.RejectionReason);
        Assert.Equal("UTC", parsed.Sample!.RawTimestampTimeZoneId);
        Assert.Equal("Taipei Standard Time", parsed.Sample.AnalysisTimeZoneId);
        var acc = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            _parser.AddToAccumulator(Row(taipeiMeasured), taipeiContext, acc, out var reason));
        Assert.Null(reason);
        Assert.Equal(new DateTime(2026, 10, 6, 0, 0, 0), Assert.Single(acc.Capture()).Hour);
        var taipeiRow = Assert.Single(acc.PreviewDrainAll(4, taipeiMeasured.AddHours(1)));
        var proof = PrtgTrustedSampleProof.Deserialize(taipeiRow.TrustedProof!);
        Assert.True(proof.IsStructurallyValid());
        Assert.True(proof.MatchesHour(taipeiRow.PeriodStart));
        Assert.False(proof.MatchesHour(taipeiRow.PeriodStart.AddDays(-1)));
        Assert.False((proof with { AnalysisTimeZoneId = "UTC" }).IsStructurallyValid());
        Assert.False((proof with { RawTimestampTimeZoneId = "Eastern Standard Time" }).IsStructurallyValid());

        var indiaMeasured = new DateTime(2026, 10, 5, 10, 30, 0, DateTimeKind.Utc);
        var indiaContext = Context(received: indiaMeasured.AddSeconds(5), asOf: indiaMeasured.AddSeconds(10),
            effective: indiaMeasured) with { AnalysisTimeZoneId = "India Standard Time" };
        var indiaAccumulator = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            _parser.AddToAccumulator(Row(indiaMeasured), indiaContext, indiaAccumulator, out reason));
        Assert.Equal(new DateTime(2026, 10, 5, 16, 0, 0), Assert.Single(indiaAccumulator.Capture()).Hour);
    }

    [Fact]
    public void 未知或缺少明確時區及DST重複分析小時拒收()
    {
        Assert.Equal(PrtgTrustedSnapshotParser.UnknownTimeZone,
            _parser.Parse(Row(), Context(zone: "unverified-zone-name")).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidContext,
            _parser.Parse(Row(), Context() with { AnalysisTimeZoneId = "" }).RejectionReason);
        var repeatedUtc = new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc);
        var repeatedContext = Context(received: repeatedUtc.AddSeconds(5), asOf: repeatedUtc.AddSeconds(10)) with
        { AnalysisTimeZoneId = "Eastern Standard Time" };
        Assert.Equal(PrtgTrustedSnapshotParser.AmbiguousAnalysisTime,
            _parser.Parse(Row(repeatedUtc), repeatedContext).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidContext,
            _parser.Parse(Row(), Context() with { ReceivedAt = DateTime.SpecifyKind(Hour.AddSeconds(10), DateTimeKind.Unspecified) }).RejectionReason);
    }

    [Fact]
    public void 缺欄位錯誤objid及未確認interval單位都不產生可信樣本()
    {
        var missingTime = """{"objid":77,"lastvalue_raw":"1","interval_raw":"60","status_raw":3}""";
        Assert.Equal(PrtgTrustedSnapshotParser.MissingMeasurementTime, _parser.Parse(missingTime, Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.ObjectIdMismatch, _parser.Parse(Row(objid: "78"), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.MissingObjectId,
            _parser.Parse("""{"lastvalue_raw":"1","lastcheck_raw":"46300","interval_raw":"60","status_raw":3}""", Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.UnconfirmedIntervalUnit,
            _parser.Parse(Row(), Context() with { ParseConfirmedIntervalValue = null }).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.IntervalMismatch,
            _parser.Parse(Row(interval: "30"), Context()).RejectionReason);
        var intervalAlias = Row(rawStatus: null, includeRawStatus: false, displayStatus: "Warning")
            .Replace("\"interval_raw\"", "\"interval\"", StringComparison.Ordinal);
        Assert.True(_parser.Parse(intervalAlias, Context()).AcceptedForAccumulation);
        Assert.True(_parser.Parse(Row(formattedInterval: "60 s"), Context()).AcceptedForAccumulation);
        Assert.True(_parser.Parse(Row(formattedInterval: "1 min"), Context()).AcceptedForAccumulation);
        Assert.Equal(PrtgTrustedSnapshotParser.IntervalMismatch,
            _parser.Parse(Row(formattedInterval: "30 s"), Context()).RejectionReason);
        var formattedOnly = Row(formattedInterval: "1 min")
            .Replace("\"interval_raw\":\"60\",", "", StringComparison.Ordinal);
        Assert.True(_parser.Parse(formattedOnly, Context() with { ParseConfirmedIntervalValue = null }).AcceptedForAccumulation);
        var numericOnly = Row().Replace("\"interval_raw\":\"60\"", "\"interval\":60", StringComparison.Ordinal);
        Assert.True(_parser.Parse(numericOnly, Context()).AcceptedForAccumulation);
        var ambiguousInterval = """{"objid":77,"lastvalue_raw":"1","lastcheck_raw":"46300","interval_raw":"60","interval":"30","status_raw":3}""";
        Assert.Equal(PrtgTrustedSnapshotParser.IntervalMismatch, _parser.Parse(ambiguousInterval, Context()).RejectionReason);
        var duplicate = """{"objid":77,"OBJID":77,"lastvalue_raw":"1","lastcheck_raw":"46300","interval_raw":"60","status_raw":3}""";
        Assert.Equal("duplicate_json_property", _parser.Parse(duplicate, Context()).RejectionReason);
    }

    [Fact]
    public void 過期未來PausedUnknown及探測不可用明確拒收或延後()
    {
        var staleTime = Hour.AddMinutes(-5);
        Assert.Equal(PrtgTrustedSnapshotParser.StaleMeasurement,
            _parser.Parse(Row(staleTime), Context(received: Hour.AddSeconds(5), asOf: Hour.AddSeconds(15))).RejectionReason);
        var future = Hour.AddMinutes(1);
        var futureContext = Context(received: Hour.AddMinutes(1).AddSeconds(5), asOf: Hour.AddSeconds(15));
        var parsedFuture = _parser.Parse(Row(future), futureContext);
        Assert.True(parsedFuture.AcceptedForAccumulation, parsedFuture.RejectionReason);
        var futureAcc = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.DeferredFuture,
            _parser.AddToAccumulator(Row(future), futureContext, futureAcc, out _));
        Assert.Equal(0, futureAcc.SampleCount);
        Assert.Equal(PrtgTrustedSnapshotParser.FutureTooFar,
            _parser.Parse(Row(Hour.AddMinutes(3)), Context(received: Hour.AddMinutes(3).AddSeconds(5))).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.Paused, _parser.Parse(Row(rawStatus: 7), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.UnknownStatus, _parser.Parse(Row(rawStatus: 1), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.ProbeUnavailable,
            _parser.Parse(Row(), Context(probe: false)).RejectionReason);
    }

    [Fact]
    public void 官方statusRaw數字碼優先並區分可用暫停與未知狀態()
    {
        Assert.True(_parser.Parse(Row(rawStatus: 3), Context()).AcceptedForAccumulation);
        Assert.True(_parser.Parse(Row(rawStatus: "3"), Context()).AcceptedForAccumulation);
        Assert.True(_parser.Parse(Row(rawStatus: 4, displayStatus: "Warning"), Context()).AcceptedForAccumulation);
        Assert.True(_parser.Parse(Row(rawStatus: 10, displayStatus: "Unusual"), Context()).AcceptedForAccumulation);

        foreach (var code in new[] { 7, 8, 9, 11, 12 })
            Assert.Equal(PrtgTrustedSnapshotParser.Paused, _parser.Parse(Row(rawStatus: code), Context()).RejectionReason);
        foreach (var code in new[] { 1, 2, 5, 13, 14 })
            Assert.Equal(PrtgTrustedSnapshotParser.UnknownStatus, _parser.Parse(Row(rawStatus: code, displayStatus: "Up"), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.ProbeUnavailable,
            _parser.Parse(Row(rawStatus: 6, displayStatus: "Up"), Context()).RejectionReason);

        Assert.Equal(PrtgTrustedSnapshotParser.UnknownStatus,
            _parser.Parse(Row(rawStatus: "1", displayStatus: "Up"), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidStatus,
            _parser.Parse(Row(rawStatus: "not-a-code", displayStatus: "Up"), Context()).RejectionReason);
    }

    [Fact]
    public void 無rawStatus只接受明確display好狀態或paused且activeOnly不可信()
    {
        foreach (var display in new[] { "Up", "Warning", "Unusual" })
            Assert.True(_parser.Parse(Row(rawStatus: null, includeRawStatus: false, displayStatus: display), Context()).AcceptedForAccumulation);
        Assert.Equal(PrtgTrustedSnapshotParser.Paused,
            _parser.Parse(Row(rawStatus: null, includeRawStatus: false, displayStatus: "Paused"), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.Paused,
            _parser.Parse(Row(rawStatus: null, includeRawStatus: false, active: false), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.Paused,
            _parser.Parse(Row(rawStatus: null, includeRawStatus: false, displayStatus: "Up", active: false), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidStatus,
            _parser.Parse(Row(rawStatus: null, includeRawStatus: false, active: true), Context()).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidStatus,
            _parser.Parse(Row(rawStatus: null, includeRawStatus: false, displayStatus: "Down", active: true), Context()).RejectionReason);
    }

    [Fact]
    public void 相同物理樣本重播不重算且不同值明確衝突()
    {
        var accumulator = new PrtgSnapshotAccumulator();
        var raw = Row();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, _parser.AddToAccumulator(raw, Context(), accumulator, out _));
        Assert.Equal(PrtgTrustedSampleDisposition.Duplicate, _parser.AddToAccumulator(raw, Context(), accumulator, out _));
        var changedValue = Row(value: "18.25");
        Assert.Equal(_parser.Parse(raw, Context()).Sample!.PhysicalMeasurementId,
            _parser.Parse(changedValue, Context()).Sample!.PhysicalMeasurementId);
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected,
            _parser.AddToAccumulator(changedValue, Context(), accumulator, out var reason));
        Assert.Equal(PrtgTrustedSnapshotParser.PhysicalMeasurementConflict, reason);
        Assert.Equal(1, accumulator.SampleCount);
    }

    [Fact]
    public void 取消的樣本不增加coverage且一萬五千感測器逐筆入帳不需全量checkpoint查詢()
    {
        var accumulator = new PrtgSnapshotAccumulator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected,
            _parser.AddToAccumulator(Row(), Context(), accumulator, out var cancelledReason, cancellation.Token));
        Assert.Equal("cancelled", cancelledReason);
        Assert.Equal(0, accumulator.SampleCount);

        for (long sensorId = 1; sensorId <= 15_000; sensorId++)
        {
            var context = Context(sensorId: sensorId);
            Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
                _parser.AddToAccumulator(Row(objid: sensorId.ToString(CultureInfo.InvariantCulture)), context, accumulator, out var reason));
            Assert.Null(reason);
        }
        Assert.Equal(15_000, accumulator.SampleCount);
        Assert.Equal(15_000, accumulator.EntryCount);
    }

    [Fact]
    public void 同時槽較新物理量測替換且策略只在已生效完整小時接受()
    {
        var accumulator = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            _parser.AddToAccumulator(Row(Hour), Context(minutes: 5), accumulator, out _));
        Assert.Equal(PrtgTrustedSampleDisposition.Replaced,
            _parser.AddToAccumulator(Row(Hour.AddMinutes(3), value: "22"),
                Context(minutes: 5, received: Hour.AddMinutes(3).AddSeconds(5), asOf: Hour.AddMinutes(3).AddSeconds(10)), accumulator, out _));
        Assert.Equal(1, accumulator.SampleCount);
        var nextHour = Hour.AddHours(1);
        var newPolicy = Context(minutes: 15, effective: nextHour,
            received: nextHour.AddSeconds(10), asOf: nextHour.AddSeconds(15));
        Assert.Equal(PrtgTrustedSnapshotParser.InvalidContext,
            _parser.Parse(Row(nextHour.AddSeconds(-10)), newPolicy).RejectionReason);
        Assert.Equal(PrtgTrustedSnapshotParser.StaleMeasurement,
            _parser.Parse(Row(Hour.AddMinutes(45)), newPolicy).RejectionReason);
        Assert.True(_parser.Parse(Row(nextHour), newPolicy).AcceptedForAccumulation);
    }
}
