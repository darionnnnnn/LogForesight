using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

public enum PrtgTrustedQuantitySemantic
{
    Unknown = 0,
    CpuLoadPercent = 1,
    MemoryUsedPercent = 2,
    MemoryAvailablePercent = 3,
    DiskFreePercent = 4,
    DiskUsedPercent = 5
}

/// <summary>
/// Persisted, per-sensor facts needed before a batch snapshot row may enter trusted coverage.
/// Instances are produced from an actual bounded source metadata probe; this type deliberately
/// has no caller-controlled "verified" switch.
/// </summary>
public sealed record PrtgTrustedSamplingProfile(
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    long IdentityEpoch,
    string ChannelGeneration,
    string SensorType,
    string PrimaryChannelId,
    string PrimaryChannelCaption,
    PrtgTrustedQuantitySemantic Quantity,
    string Unit,
    double Scale,
    string Direction,
    string SemanticVersion,
    string StrategyFingerprint,
    int StrategyMinutes,
    DateTime StrategyEffectiveFromHourUtc,
    TimeSpan ConfirmedScanInterval,
    string IntervalRawUnit,
    string RawTimestampTimeZoneId,
    string SourceApiTimeZoneId,
    string AnalysisTimeZoneId,
    DateTimeOffset SourceMetadataObservedAtUtc,
    string SourceMetadataReference,
    string PhysicalSampleReference,
    bool SourceMarkedPrimary,
    double ComparedSnapshotValue,
    double ComparedPrimaryChannelValue,
    double ComparedSnapshotMeasurementOaDate,
    double ComparedPrimaryChannelMeasurementOaDate,
    string MetadataDigest,
    string AuthorityKind = "legacy_native_marker_v1",
    long BindingRevision = 0,
    string BindingFingerprint = "",
    string NativePrimaryChannelPropertyId = "",
    string QualificationProofReference = "",
    string BindingSettingsRevision = "",
    string BindingPolicyRevision = "",
    string AuthorityContextFingerprint = "")
{
    public const int MaximumSerializedBytes = 8 * 1024;
    public const string StorePrefix = "prtg_trusted_sampling_profile_";
    public const string ExplicitBindingAuthorityKind = "explicit_binding_primary_property_v1";
    public const string LegacyAuthorityKind = "legacy_native_marker_v1";
    private const int MaxText = 256;

    /// <summary>Creates a profile only from typed facts parsed from a source metadata response.</summary>
    internal static PrtgTrustedSamplingProfile FromProbe(
        long sensorObjid, PrtgResourceIdentity identity, string sensorType,
        string primaryChannelId, string primaryChannelCaption, PrtgTrustedQuantitySemantic quantity, string unit,
        double scale, string direction, string semanticVersion, string strategyFingerprint,
        int strategyMinutes, DateTime strategyEffectiveFromHourUtc, TimeSpan confirmedScanInterval,
        string intervalRawUnit, string rawTimestampTimeZoneId, string sourceApiTimeZoneId,
        string analysisTimeZoneId, DateTimeOffset observedAtUtc,
        string sourceMetadataReference, string physicalSampleReference, bool sourceMarkedPrimary,
        double comparedSnapshotValue, double comparedPrimaryChannelValue,
        double comparedSnapshotMeasurementOaDate, double comparedPrimaryChannelMeasurementOaDate,
        string authorityKind = LegacyAuthorityKind, long bindingRevision = 0,
        string bindingFingerprint = "", string nativePrimaryChannelPropertyId = "",
        string qualificationProofReference = "", string bindingSettingsRevision = "",
        string bindingPolicyRevision = "", string authorityContextFingerprint = "")
    {
        ArgumentNullException.ThrowIfNull(identity);
        var profile = new PrtgTrustedSamplingProfile(sensorObjid, identity.SourceGeneration,
            identity.Generation, identity.Epoch, identity.ChannelGeneration, sensorType,
            primaryChannelId, primaryChannelCaption, quantity, unit, scale, direction,
            semanticVersion, strategyFingerprint, strategyMinutes, strategyEffectiveFromHourUtc,
            confirmedScanInterval, intervalRawUnit, rawTimestampTimeZoneId, sourceApiTimeZoneId,
            analysisTimeZoneId, observedAtUtc,
            sourceMetadataReference, physicalSampleReference, sourceMarkedPrimary,
            comparedSnapshotValue, comparedPrimaryChannelValue, comparedSnapshotMeasurementOaDate,
            comparedPrimaryChannelMeasurementOaDate, "", authorityKind, bindingRevision,
            bindingFingerprint, nativePrimaryChannelPropertyId, qualificationProofReference,
            bindingSettingsRevision, bindingPolicyRevision, authorityContextFingerprint);
        profile = profile with { MetadataDigest = profile.ComputeDigest() };
        profile.Validate();
        return profile;
    }

    public bool MatchesCurrent(PrtgResourceIdentity identity, string sensorType,
        string strategyFingerprint, int strategyMinutes, DateTime effectiveFromHourUtc,
        DateTimeOffset nowUtc)
    {
        try { Validate(); }
        catch (InvalidDataException) { return false; }
        return identity != null && identity.Active && !identity.PendingReconciliation &&
            identity.Epoch > 0 && SensorObjid == identity.SensorId &&
            SourceGeneration == identity.SourceGeneration && ResourceGeneration == identity.Generation &&
            IdentityEpoch == identity.Epoch && ChannelGeneration == identity.ChannelGeneration &&
            SensorType == sensorType && StrategyFingerprint == strategyFingerprint &&
            StrategyMinutes == strategyMinutes && StrategyEffectiveFromHourUtc == effectiveFromHourUtc &&
            SourceMetadataObservedAtUtc <= nowUtc &&
            nowUtc - SourceMetadataObservedAtUtc <= TimeSpan.FromHours(24);
    }

    public void Validate()
    {
        if (SensorObjid <= 0 || IdentityEpoch <= 0 || !Valid(SourceGeneration, 128) ||
            !Valid(ResourceGeneration, 128) || !Valid(ChannelGeneration, 128) ||
            !Valid(SensorType, 128) || !Valid(PrimaryChannelId, 128) ||
            !Valid(PrimaryChannelCaption, 256) || !Enum.IsDefined(Quantity) ||
            Quantity == PrtgTrustedQuantitySemantic.Unknown || !Valid(Unit, 64) ||
            !Valid(Direction, 32) || !Valid(SemanticVersion, 128) ||
            !Valid(StrategyFingerprint, 128) || StrategyMinutes is not (5 or 15) ||
            StrategyEffectiveFromHourUtc.Kind != DateTimeKind.Utc ||
            StrategyEffectiveFromHourUtc == default || ConfirmedScanInterval <= TimeSpan.Zero ||
            ConfirmedScanInterval > TimeSpan.FromHours(1) ||
            IntervalRawUnit is not ("seconds" or "minutes") ||
            SourceMetadataObservedAtUtc == default || SourceMetadataObservedAtUtc.Offset != TimeSpan.Zero ||
            !Valid(SourceMetadataReference, 512) || SourceMetadataReference.Length < 8 ||
            !Valid(PhysicalSampleReference, 512) || PhysicalSampleReference.Length < 8 ||
            !double.IsFinite(Scale) || Scale <= 0 || !ZoneExists(RawTimestampTimeZoneId) ||
            !ZoneExists(SourceApiTimeZoneId) || !ZoneExists(AnalysisTimeZoneId) ||
            !WholeHourInZone(StrategyEffectiveFromHourUtc, AnalysisTimeZoneId) ||
            MetadataDigest != ComputeDigest())
            throw new InvalidDataException("可信採樣 profile 缺少來源欄位、身分不完整或超出界限。");
        var explicitBinding = AuthorityKind == ExplicitBindingAuthorityKind;
        if (explicitBinding)
        {
            if (SourceMarkedPrimary || BindingRevision <= 0 || !Hex64(BindingFingerprint) ||
                !Hex64(AuthorityContextFingerprint) ||
                NativePrimaryChannelPropertyId != PrimaryChannelId ||
                !Valid(QualificationProofReference, 128) || QualificationProofReference.Contains("://", StringComparison.Ordinal) ||
                !Valid(BindingSettingsRevision, 128) || !Valid(BindingPolicyRevision, 128))
                throw new InvalidDataException("管理員 binding、primarychannel property 或 qualification proof 不完整。");
        }
        else if (AuthorityKind != LegacyAuthorityKind)
            throw new InvalidDataException("可信採樣 profile authority kind 未支援。");
        if ((!explicitBinding && !SourceMarkedPrimary) || !double.IsFinite(ComparedSnapshotValue) ||
            !double.IsFinite(ComparedPrimaryChannelValue) || ComparedSnapshotValue != ComparedPrimaryChannelValue ||
            !ValidOaDate(ComparedSnapshotMeasurementOaDate) || !ValidOaDate(ComparedPrimaryChannelMeasurementOaDate) ||
            Math.Abs(ComparedSnapshotMeasurementOaDate - ComparedPrimaryChannelMeasurementOaDate) > 1.0 / 86400.0)
            throw new InvalidDataException("來源未證明 primary channel 或實體樣本值／時間對照不一致。");
        if (Direction is not ("direct" or "inverse" or "absolute"))
            throw new InvalidDataException("可信採樣 profile 的量值方向未確認。");
        var normalizedComparedValue = Direction switch
        { "inverse" => -ComparedSnapshotValue * Scale, "absolute" => Math.Abs(ComparedSnapshotValue) * Scale, _ => ComparedSnapshotValue * Scale };
        if (Unit != "%" || !double.IsFinite(normalizedComparedValue) || normalizedComparedValue is < 0 or > 100)
            throw new InvalidDataException("百分比量義正規化後必須介於 0 與 100。");
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this with { MetadataDigest = "" })) > MaximumSerializedBytes)
            throw new InvalidDataException("可信採樣 profile 超過 8 KiB 上限。");
    }

    private string ComputeDigest()
    {
        if (!double.IsFinite(Scale) || !double.IsFinite(ComparedSnapshotValue) || !double.IsFinite(ComparedPrimaryChannelValue) ||
            !double.IsFinite(ComparedSnapshotMeasurementOaDate) || !double.IsFinite(ComparedPrimaryChannelMeasurementOaDate))
            throw new InvalidDataException("可信採樣 profile 不接受非有限數值。");
        var payload = this with { MetadataDigest = "" };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
    }

    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool ValidOaDate(double value)
    {
        if (!double.IsFinite(value) || value < -657435.0 || value >= 2958466.0) return false;
        try { _ = DateTime.FromOADate(value); return true; }
        catch (ArgumentException) { return false; }
    }
    private static bool Hex64(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ZoneExists(string id)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
    private static bool WholeHourInZone(DateTime utc, string zoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            var wall = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            return !zone.IsAmbiguousTime(wall) && !zone.IsInvalidTime(wall) &&
                wall.Minute == 0 && wall.Second == 0 && wall.Millisecond == 0 &&
                wall.Ticks % TimeSpan.TicksPerHour == 0;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
