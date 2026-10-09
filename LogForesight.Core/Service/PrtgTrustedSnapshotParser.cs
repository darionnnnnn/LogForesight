using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>提供由本機當前設定與已確認來源政策建立的可信樣本脈絡。</summary>
public sealed record PrtgTrustedSnapshotContext(
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    string SemanticVersion,
    string StrategyVersion,
    int StrategyMinutes,
    DateTime StrategyEffectiveFromHour,
    string RawTimestampTimeZoneId,
    TimeSpan ConfirmedScanInterval,
    DateTime ReceivedAt,
    DateTime AsOf,
    Func<double, TimeSpan?>? ParseConfirmedIntervalValue,
    bool ProbeAvailable = true,
    string AnalysisTimeZoneId = "",
    Func<double, double>? NormalizeConfirmedQuantity = null,
    string SelectedPrimaryChannelId = "");

public sealed record PrtgTrustedSnapshotParseResult(
    PrtgTrustedSample? Sample,
    string? RejectionReason)
{
    public bool AcceptedForAccumulation => Sample != null && RejectionReason == null;
}

/// <summary>
/// 僅把有本機可信脈絡、明確時間及已確認欄位解讀的 PRTG 單列轉成可信樣本。
/// 這個解析器不建立來源世代、語意、策略或掃描間隔。
/// </summary>
public sealed class PrtgTrustedSnapshotParser
{
    public const string MissingObjectId = "missing_objid";
    public const string ObjectIdMismatch = "objid_mismatch";
    public const string MissingMeasurementTime = "missing_measurement_time";
    public const string InvalidMeasurementTime = "invalid_measurement_time";
    public const string UnknownTimeZone = "unknown_source_timezone";
    public const string AmbiguousMeasurementTime = "ambiguous_source_time";
    public const string InvalidMeasurementTimeInZone = "invalid_source_time";
    public const string AmbiguousAnalysisTime = "ambiguous_analysis_time";
    public const string InvalidAnalysisTimeInZone = "invalid_analysis_time_in_zone";
    public const string MissingValue = "missing_lastvalue_raw";
    public const string InvalidValue = "invalid_lastvalue_raw";
    public const string MissingInterval = "missing_interval";
    public const string UnconfirmedIntervalUnit = "interval_unit_unconfirmed";
    public const string IntervalMismatch = "interval_mismatch";
    public const string InvalidStatus = "status_untrusted";
    public const string Paused = "paused";
    public const string UnknownStatus = "status_unknown";
    public const string ProbeUnavailable = "probe_unavailable";
    public const string InvalidContext = "source_context_invalid";
    public const string FutureTooFar = "measurement_future_too_far";
    public const string StaleMeasurement = "measurement_stale";
    public const string PhysicalMeasurementConflict = "physical_measurement_conflict";

    public PrtgTrustedSnapshotParseResult Parse(string rawJson, PrtgTrustedSnapshotContext context)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return Reject("invalid_json");
        if (!ContextIsValid(context)) return Reject(InvalidContext);
        if (!context.ProbeAvailable) return Reject(ProbeUnavailable);

        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(context.RawTimestampTimeZoneId); }
        catch (TimeZoneNotFoundException) { return Reject(UnknownTimeZone); }
        catch (InvalidTimeZoneException) { return Reject(UnknownTimeZone); }

        JsonDocument document;
        try { document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return Reject("invalid_json"); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Reject("row_not_object");
            var root = document.RootElement;
            if (HasDuplicateProperties(root)) return Reject("duplicate_json_property");

            if (!TryLong(root, "objid", out var objid)) return Reject(MissingObjectId);
            if (objid != context.SensorObjid) return Reject(ObjectIdMismatch);

            if (!TryProperty(root, "lastvalue_raw", out _)) return Reject(MissingValue);
            if (!TryRawNumber(root, "lastvalue_raw", out var value) || !double.IsFinite(value)) return Reject(InvalidValue);
            if (context.NormalizeConfirmedQuantity != null)
            {
                try { value = context.NormalizeConfirmedQuantity(value); }
                catch { return Reject(InvalidValue); }
                if (!double.IsFinite(value)) return Reject(InvalidValue);
            }

            if (!TryConfirmInterval(root, context, out var intervalError)) return Reject(intervalError!);

            if (!TryStatus(root, out var quality, out var statusError)) return Reject(statusError!);
            if (quality != PrtgTrustedSampleQuality.Good)
                return Reject(quality == PrtgTrustedSampleQuality.Paused ? Paused : UnknownStatus);

            if (!TryProperty(root, "lastcheck_raw", out _) && !TryProperty(root, "rawdatetime", out _))
                return Reject(MissingMeasurementTime);
            if (!TryTimestamp(root, out var wallTime)) return Reject(InvalidMeasurementTime);
            DateTime measuredUtc;
            try
            {
                var sourceWallTime = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(sourceWallTime)) return Reject(InvalidMeasurementTimeInZone);
                if (zone.IsAmbiguousTime(sourceWallTime)) return Reject(AmbiguousMeasurementTime);
                measuredUtc = TimeZoneInfo.ConvertTimeToUtc(sourceWallTime, zone);
            }
            catch (ArgumentException) { return Reject(InvalidMeasurementTimeInZone); }

            var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(context.AnalysisTimeZoneId);
            if (!TryAnalysisWallTime(measuredUtc, analysisZone, out _))
                return Reject(analysisZone.IsAmbiguousTime(TimeZoneInfo.ConvertTimeFromUtc(measuredUtc, analysisZone))
                    ? AmbiguousAnalysisTime : InvalidAnalysisTimeInZone);

            var receivedUtc = AsUtc(context.ReceivedAt);
            var asOfUtc = AsUtc(context.AsOf);
            var future = measuredUtc > asOfUtc;
            if (future && measuredUtc - asOfUtc > TimeSpan.FromMinutes(2)) return Reject(FutureTooFar);
            var freshnessLimit = TimeSpan.FromTicks(context.ConfirmedScanInterval.Ticks * 2) + TimeSpan.FromSeconds(30);
            if (!future && (receivedUtc < measuredUtc || asOfUtc - measuredUtc > freshnessLimit ||
                receivedUtc - measuredUtc > freshnessLimit))
                return Reject(StaleMeasurement);

            var effectiveHour = context.StrategyEffectiveFromHour;
            if (measuredUtc < effectiveHour) return Reject(InvalidContext);
            var physicalId = PhysicalMeasurementId(context, measuredUtc);
            return new(new PrtgTrustedSample(context.SensorObjid, value, context.SourceGeneration,
                context.ResourceGeneration, context.ChannelGeneration, context.ResourceEpoch,
                context.SemanticVersion, context.StrategyVersion, context.StrategyMinutes,
                effectiveHour, measuredUtc, receivedUtc, context.ConfirmedScanInterval,
                PrtgTrustedSampleQuality.Good, physicalId, context.RawTimestampTimeZoneId,
                context.AnalysisTimeZoneId), null);
        }
    }

    /// <summary>交由 accumulator 在其原子入帳鎖內處理物理樣本重播與衝突。</summary>
    public PrtgTrustedSampleDisposition AddToAccumulator(string rawJson, PrtgTrustedSnapshotContext context,
        PrtgSnapshotAccumulator accumulator, out string? rejectionReason, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            rejectionReason = "cancelled";
            return PrtgTrustedSampleDisposition.Rejected;
        }
        var parsed = Parse(rawJson, context);
        rejectionReason = parsed.RejectionReason;
        if (parsed.Sample == null) return PrtgTrustedSampleDisposition.Rejected;
        if (cancellationToken.IsCancellationRequested)
        {
            rejectionReason = "cancelled";
            return PrtgTrustedSampleDisposition.Rejected;
        }

        var disposition = accumulator.AddTrusted(parsed.Sample, AsUtc(context.AsOf), out var accumulatorReason);
        if (disposition == PrtgTrustedSampleDisposition.Rejected)
            rejectionReason = accumulatorReason ?? "accumulator_rejected";
        return disposition;
    }

    private static bool TryConfirmInterval(JsonElement root, PrtgTrustedSnapshotContext context, out string? reason)
    {
        reason = null;
        var hasRaw = TryProperty(root, "interval_raw", out _);
        var hasFormatted = TryProperty(root, "interval", out var formattedToken);
        if (!hasRaw && !hasFormatted) { reason = MissingInterval; return false; }

        TimeSpan? rawInterval = null;
        if (hasRaw)
        {
            if (!TryRawNumber(root, "interval_raw", out var raw) || !double.IsFinite(raw) || raw <= 0)
            { reason = IntervalMismatch; return false; }
            if (context.ParseConfirmedIntervalValue == null) { reason = UnconfirmedIntervalUnit; return false; }
            try { rawInterval = context.ParseConfirmedIntervalValue(raw); }
            catch { reason = IntervalMismatch; return false; }
            if (!rawInterval.HasValue || rawInterval.Value != context.ConfirmedScanInterval)
            { reason = IntervalMismatch; return false; }
        }

        if (!hasFormatted) return true;
        TimeSpan? formattedInterval;
        if (formattedToken.ValueKind == JsonValueKind.String)
        {
            var text = formattedToken.GetString();
            if (text == null || text.Length > 64) { reason = IntervalMismatch; return false; }
            if (TryParseExplicitInterval(text, out var explicitInterval))
                formattedInterval = explicitInterval;
            else if (!hasRaw && TryRawNumber(formattedToken, out var numeric) && double.IsFinite(numeric) && numeric > 0)
            {
                if (context.ParseConfirmedIntervalValue == null) { reason = UnconfirmedIntervalUnit; return false; }
                try { formattedInterval = context.ParseConfirmedIntervalValue(numeric); }
                catch { reason = IntervalMismatch; return false; }
            }
            else { reason = IntervalMismatch; return false; }
        }
        else if (formattedToken.ValueKind == JsonValueKind.Number && TryRawNumber(formattedToken, out var number) &&
                 double.IsFinite(number) && number > 0)
        {
            if (context.ParseConfirmedIntervalValue == null) { reason = UnconfirmedIntervalUnit; return false; }
            try { formattedInterval = context.ParseConfirmedIntervalValue(number); }
            catch { reason = IntervalMismatch; return false; }
        }
        else { reason = IntervalMismatch; return false; }

        if (!formattedInterval.HasValue || formattedInterval.Value != context.ConfirmedScanInterval)
        { reason = IntervalMismatch; return false; }
        return true;
    }

    private static bool TryParseExplicitInterval(string text, out TimeSpan interval)
    {
        interval = default;
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"^\s*(\d+(?:\.\d+)?)\s*(ms|millisecond|milliseconds|s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount) || amount <= 0)
            return false;
        var unit = match.Groups[2].Value.ToLowerInvariant();
        try
        {
            interval = unit switch
            {
                "ms" or "millisecond" or "milliseconds" => TimeSpan.FromMilliseconds(amount),
                "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
                "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromSeconds(amount)
            };
            return interval > TimeSpan.Zero;
        }
        catch (OverflowException) { return false; }
    }

    private static bool ContextIsValid(PrtgTrustedSnapshotContext context) => context != null &&
        context.SensorObjid > 0 && ValidText(context.SourceGeneration, 256) &&
        ValidText(context.ResourceGeneration, 256) && ValidText(context.ChannelGeneration, 256) &&
        ValidText(context.ResourceEpoch, 256) && ValidText(context.SemanticVersion, 128) &&
        ValidText(context.StrategyVersion, 128) && context.StrategyMinutes is 5 or 15 &&
        context.StrategyEffectiveFromHour != default && context.StrategyEffectiveFromHour.Kind == DateTimeKind.Utc &&
        context.ReceivedAt.Kind == DateTimeKind.Utc && context.AsOf.Kind == DateTimeKind.Utc &&
        IsHourInstantInZone(context.StrategyEffectiveFromHour, context.AnalysisTimeZoneId) &&
        ValidText(context.RawTimestampTimeZoneId, 128) && ValidText(context.AnalysisTimeZoneId, 128) &&
        IsKnownZone(context.AnalysisTimeZoneId) &&
        context.ConfirmedScanInterval > TimeSpan.Zero &&
        context.ConfirmedScanInterval <= TimeSpan.FromHours(1) && context.ReceivedAt != default && context.AsOf != default;

    private static bool TryLong(JsonElement root, string name, out long value)
    {
        value = 0;
        if (!TryProperty(root, name, out var token)) return false;
        return token.ValueKind == JsonValueKind.Number ? token.TryGetInt64(out value) :
            token.ValueKind == JsonValueKind.String && long.TryParse(token.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out value);
    }

    private static bool TryRawNumber(JsonElement root, string name, out double value)
    {
        if (!TryProperty(root, name, out var token)) { value = 0; return false; }
        return TryRawNumber(token, out value);
    }

    private static bool TryRawNumber(JsonElement token, out double value)
    {
        value = 0;
        if (token.ValueKind == JsonValueKind.Number) return token.TryGetDouble(out value);
        return token.ValueKind == JsonValueKind.String && double.TryParse(token.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool TryTimestamp(JsonElement root, out DateTime wallTime)
    {
        wallTime = default;
        var hasLastCheck = TryProperty(root, "lastcheck_raw", out _);
        var hasRawDateTime = TryProperty(root, "rawdatetime", out _);
        if (!hasLastCheck && !hasRawDateTime) return false;
        var lastCheck = 0d;
        var rawDateTime = 0d;
        if (hasLastCheck && !TryRawNumber(root, "lastcheck_raw", out lastCheck)) return false;
        if (hasRawDateTime && !TryRawNumber(root, "rawdatetime", out rawDateTime)) return false;
        if (hasLastCheck && hasRawDateTime && lastCheck != rawDateTime) return false;
        var oaDate = hasLastCheck ? lastCheck : rawDateTime;
        if (!double.IsFinite(oaDate) || oaDate < -657435.0 || oaDate >= 2958466.0) return false;
        try { wallTime = DateTime.FromOADate(oaDate); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool TryStatus(JsonElement root, out PrtgTrustedSampleQuality quality, out string? error)
    {
        quality = PrtgTrustedSampleQuality.Unknown;
        error = null;
        if (TryProperty(root, "status_raw", out var status))
        {
            if (!TryStatusCode(status, out var code))
            {
                error = InvalidStatus;
                return false;
            }
            if (code is 3 or 4 or 10) { quality = PrtgTrustedSampleQuality.Good; return true; }
            if (code is 7 or 8 or 9 or 11 or 12) { quality = PrtgTrustedSampleQuality.Paused; return true; }
            error = code == 6 ? ProbeUnavailable : UnknownStatus;
            return false;
        }

        if (TryProperty(root, "active", out var active) &&
            (active.ValueKind == JsonValueKind.False || active.ValueKind == JsonValueKind.Number && active.TryGetInt32(out var inactive) && inactive == 0))
        { quality = PrtgTrustedSampleQuality.Paused; return true; }

        if (TryProperty(root, "status", out var displayStatus) && displayStatus.ValueKind == JsonValueKind.String)
        {
            var text = displayStatus.GetString()?.Trim();
            if (string.Equals(text, "Up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Warning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Unusual", StringComparison.OrdinalIgnoreCase))
            { quality = PrtgTrustedSampleQuality.Good; return true; }
            if (text?.Contains("paus", StringComparison.OrdinalIgnoreCase) == true)
            { quality = PrtgTrustedSampleQuality.Paused; return true; }
        }
        error = InvalidStatus;
        return false;
    }

    private static bool TryStatusCode(JsonElement status, out int code)
    {
        code = 0;
        return status.ValueKind == JsonValueKind.Number ? status.TryGetInt32(out code) :
            status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out code);
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static bool HasDuplicateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) return true;
        return false;
    }

    private static string PhysicalMeasurementId(PrtgTrustedSnapshotContext context, DateTime measuredUtc) =>
        string.Join('|', context.SourceGeneration, context.ResourceGeneration, context.ChannelGeneration,
            measuredUtc.Ticks.ToString(CultureInfo.InvariantCulture));

    private static DateTime AsUtc(DateTime value) => value;

    private static bool IsHourInstantInZone(DateTime value, string zoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            var wall = TimeZoneInfo.ConvertTimeFromUtc(value, zone);
            return !zone.IsAmbiguousTime(wall) && !zone.IsInvalidTime(wall) &&
                wall.Minute == 0 && wall.Second == 0 && wall.Millisecond == 0 &&
                wall.Ticks % TimeSpan.TicksPerHour == 0;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool TryAnalysisWallTime(DateTime instant, TimeZoneInfo zone, out DateTime wall)
    {
        wall = default;
        if (instant.Kind != DateTimeKind.Utc) return false;
        wall = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        return !zone.IsAmbiguousTime(wall) && !zone.IsInvalidTime(wall);
    }

    private static bool IsKnownZone(string zoneId)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zoneId); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static PrtgTrustedSnapshotParseResult Reject(string reason) => new(null, reason);
}
