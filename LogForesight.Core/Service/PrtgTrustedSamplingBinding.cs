using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>Administrator-owned semantic binding for one sensor. A saved binding is configuration only.</summary>
public sealed record PrtgTrustedSamplingBinding(
    long SensorObjid,
    long BindingRevision,
    string ChannelObjectId,
    string ExpectedCaption,
    PrtgTrustedQuantitySemantic Quantity,
    string Unit,
    double Scale,
    string Direction,
    string IntervalRawUnit,
    string RawTimestampTimeZoneId,
    string AnalysisTimeZoneId,
    string TimeBasisEvidenceReference,
    string SettingsRevision,
    string PolicyRevision,
    string AuthorityContextFingerprint,
    string SourceGeneration,
    string ResourceGeneration,
    long IdentityEpoch,
    string ChannelGeneration,
    string SemanticFingerprint,
    string BindingFingerprint,
    string QualificationProofReference,
    DateTimeOffset UpdatedAtUtc,
    double? QualificationRawValue = null,
    double? QualificationMeasuredOaDate = null,
    string QualificationSourceVersion = "",
    DateTimeOffset? QualificationObservedAtUtc = null)
{
    public const int MaximumSerializedBytes = 8 * 1024;
    public const int MaximumReferenceLength = 128;
    public const string StorePrefix = "prtg_trusted_sampling_binding_";
    public string StoreKey => StorePrefix + SensorObjid.ToString(CultureInfo.InvariantCulture);

    public static PrtgTrustedSamplingBinding Create(long sensorId, string channelObjectId,
        string caption, PrtgTrustedQuantitySemantic quantity, string unit, double scale,
        string direction, string intervalRawUnit, string rawTimeZoneId, string analysisTimeZoneId,
        string timeBasisEvidenceReference, string settingsRevision, string policyRevision,
        string authorityContextFingerprint, string sourceGeneration, string resourceGeneration,
        long identityEpoch, string channelGeneration,
        long revision, string qualificationProofReference = "")
    {
        var value = new PrtgTrustedSamplingBinding(sensorId, revision, channelObjectId.Trim(), caption,
            quantity, unit.Trim(), scale, direction.Trim(), intervalRawUnit.Trim(), rawTimeZoneId.Trim(),
            analysisTimeZoneId.Trim(), timeBasisEvidenceReference.Trim(), settingsRevision,
            policyRevision, authorityContextFingerprint, sourceGeneration, resourceGeneration, identityEpoch, channelGeneration,
            "", "", qualificationProofReference, DateTimeOffset.UtcNow);
        value = value with { ChannelObjectId = CanonicalChannelObjectId(value.ChannelObjectId) };
        value = value with { SemanticFingerprint = value.ComputeSemanticFingerprint() };
        value = value with { BindingFingerprint = value.ComputeBindingFingerprint() };
        value.Validate();
        return value;
    }

    public bool Matches(long sensorId, string authorityContextFingerprint,
        string sourceGeneration, string resourceGeneration, long identityEpoch, string channelGeneration) =>
        SensorObjid == sensorId && AuthorityContextFingerprint == authorityContextFingerprint &&
        SourceGeneration == sourceGeneration && ResourceGeneration == resourceGeneration &&
        IdentityEpoch == identityEpoch && ChannelGeneration == channelGeneration && IsValid();

    /// <summary>Checks that a persisted profile's semantic fields still produce its binding authority fingerprint.</summary>
    public static bool MatchesProfileAuthority(PrtgTrustedSamplingProfile profile,
        string? timeBasisEvidenceReference)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(timeBasisEvidenceReference) ||
            profile.SensorObjid <= 0 || profile.BindingRevision <= 0 || profile.IdentityEpoch <= 0 ||
            string.IsNullOrWhiteSpace(profile.ResourceGeneration) ||
            string.IsNullOrWhiteSpace(profile.AuthorityContextFingerprint) ||
            string.IsNullOrWhiteSpace(profile.QualificationProofReference) ||
            profile.PrimaryChannelId is null || profile.PrimaryChannelCaption is null || profile.Unit is null ||
            profile.Direction is null || profile.IntervalRawUnit is null ||
            profile.RawTimestampTimeZoneId is null || profile.AnalysisTimeZoneId is null) return false;

        var semanticFingerprint = ComputeSemanticFingerprint(profile.SensorObjid, profile.PrimaryChannelId,
            profile.PrimaryChannelCaption, profile.Quantity, profile.Unit, profile.Scale, profile.Direction,
            profile.IntervalRawUnit, profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId,
            timeBasisEvidenceReference);
        var bindingFingerprint = ComputeBindingFingerprint(semanticFingerprint,
            profile.AuthorityContextFingerprint, profile.BindingRevision, profile.ResourceGeneration,
            profile.IdentityEpoch, profile.QualificationProofReference);
        return StringComparer.Ordinal.Equals(profile.BindingFingerprint, bindingFingerprint);
    }

    public void Validate()
    {
        if (SensorObjid <= 0 || BindingRevision <= 0 || !CanonicalChannelObjectIdValid(ChannelObjectId) ||
            !SafeText(ExpectedCaption, 256) || !Enum.IsDefined(Quantity) || Quantity == PrtgTrustedQuantitySemantic.Unknown ||
            Unit != "%" || !double.IsFinite(Scale) || Scale <= 0 ||
            Direction is not ("direct" or "inverse" or "absolute") ||
            IntervalRawUnit is not ("seconds" or "minutes") ||
            !ZoneExists(RawTimestampTimeZoneId) || !ZoneExists(AnalysisTimeZoneId) ||
            !Opaque(TimeBasisEvidenceReference) || !Opaque(SettingsRevision) || !Opaque(PolicyRevision) ||
            !Hex64(AuthorityContextFingerprint) || !Opaque(SourceGeneration) || !Opaque(ResourceGeneration) || IdentityEpoch <= 0 ||
            !Opaque(ChannelGeneration) || !Hex64(SemanticFingerprint) || !Hex64(BindingFingerprint) ||
            QualificationProofReference is null || QualificationProofReference.Length > MaximumReferenceLength ||
            QualificationProofReference.Length > 0 && !Opaque(QualificationProofReference) ||
            QualificationProofReference.Length == 0 && (QualificationRawValue.HasValue ||
                QualificationMeasuredOaDate.HasValue || QualificationSourceVersion.Length > 0 ||
                QualificationObservedAtUtc.HasValue) ||
            QualificationProofReference.Length > 0 && (!QualificationRawValue.HasValue ||
                !double.IsFinite(QualificationRawValue.Value) || !QualificationMeasuredOaDate.HasValue ||
                !ValidOaDate(QualificationMeasuredOaDate.Value) || !Opaque(QualificationSourceVersion) ||
                QualificationObservedAtUtc is null || QualificationObservedAtUtc.Value == default ||
                QualificationObservedAtUtc.Value.Offset != TimeSpan.Zero) ||
            UpdatedAtUtc == default || UpdatedAtUtc.Offset != TimeSpan.Zero ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this with { BindingFingerprint = "" })) > MaximumSerializedBytes)
            throw new InvalidDataException("PRTG 採樣 binding 欄位無效或超出界限。");
        if (SemanticFingerprint != ComputeSemanticFingerprint() || BindingFingerprint != ComputeBindingFingerprint())
            throw new InvalidDataException("PRTG 採樣 binding fingerprint 不相符。");
        var normalized = Direction switch
        {
            "direct" => Scale,
            "inverse" => -Scale,
            "absolute" => Scale,
            _ => double.NaN
        };
        if (!double.IsFinite(normalized)) throw new InvalidDataException("PRTG 採樣 binding scale 無效。");
    }

    public bool IsValid()
    {
        try { Validate(); return true; }
        catch (InvalidDataException) { return false; }
    }

    public PrtgTrustedSamplingBinding WithQualification(string proofReference, double rawValue,
        double measuredOaDate, string sourceVersion, DateTimeOffset observedAtUtc)
    {
        if (!Opaque(proofReference) || !double.IsFinite(rawValue) || !ValidOaDate(measuredOaDate) ||
            !Opaque(sourceVersion) || observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Qualification proof facts are invalid.", nameof(proofReference));
        var updated = this with { QualificationProofReference = proofReference, QualificationRawValue = rawValue,
            QualificationMeasuredOaDate = measuredOaDate, QualificationSourceVersion = sourceVersion,
            QualificationObservedAtUtc = observedAtUtc, UpdatedAtUtc = DateTimeOffset.UtcNow };
        updated = updated with { BindingFingerprint = updated.ComputeBindingFingerprint() };
        updated.Validate();
        return updated;
    }

    public PrtgTrustedSamplingBinding RefreshBindingFingerprint()
    {
        var updated = this with { BindingFingerprint = "" };
        updated = updated with { BindingFingerprint = updated.ComputeBindingFingerprint() };
        updated.Validate();
        return updated;
    }

    private string ComputeSemanticFingerprint() => ComputeSemanticFingerprint(SensorObjid, ChannelObjectId,
        ExpectedCaption, Quantity, Unit, Scale, Direction, IntervalRawUnit, RawTimestampTimeZoneId,
        AnalysisTimeZoneId, TimeBasisEvidenceReference);

    private static string ComputeSemanticFingerprint(long sensorObjid, string channelObjectId,
        string expectedCaption, PrtgTrustedQuantitySemantic quantity, string unit, double scale,
        string direction, string intervalRawUnit, string rawTimestampTimeZoneId,
        string analysisTimeZoneId, string timeBasisEvidenceReference)
    {
        var parts = new[] { sensorObjid.ToString(CultureInfo.InvariantCulture), channelObjectId,
            expectedCaption, quantity.ToString(), unit, scale.ToString("R", CultureInfo.InvariantCulture),
            direction, intervalRawUnit, rawTimestampTimeZoneId, analysisTimeZoneId, timeBasisEvidenceReference };
        var payload = string.Concat(parts.Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private string ComputeBindingFingerprint() => ComputeBindingFingerprint(SemanticFingerprint,
        AuthorityContextFingerprint, BindingRevision, ResourceGeneration, IdentityEpoch, QualificationProofReference);

    private static string ComputeBindingFingerprint(string semanticFingerprint,
        string authorityContextFingerprint, long bindingRevision, string resourceGeneration,
        long identityEpoch, string qualificationProofReference)
    {
        // Exclude only ChannelGeneration: Set rotates that field when this fingerprint is installed.
        // Resource generation and identity epoch are independent fences and remain fingerprinted.
        var payload = string.Join("\u001f", semanticFingerprint, authorityContextFingerprint,
            bindingRevision, resourceGeneration, identityEpoch, qualificationProofReference);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string CanonicalChannelObjectId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 20 || !value.All(char.IsAsciiDigit) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
            throw new ArgumentException("Channel object ID must be a nonnegative Int64 decimal string.", nameof(value));
        return id.ToString(CultureInfo.InvariantCulture);
    }
    private static bool CanonicalChannelObjectIdValid(string? value)
    {
        try { return value == CanonicalChannelObjectId(value); }
        catch (ArgumentException) { return false; }
    }
    private static bool SafeText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value.All(ch => !char.IsControl(ch));
    private static bool Opaque(string? value) => value is not null && SafeText(value, MaximumReferenceLength) &&
        !value.Contains("://", StringComparison.Ordinal) && !value.Contains('\\') && !value.Contains('/') &&
        !value.Contains('?') && !value.Contains('#') && !value.Contains('@');
    private static bool Hex64(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidOaDate(double value)
    {
        if (!double.IsFinite(value) || value < -657435.0 || value >= 2958466.0) return false;
        try { _ = DateTime.FromOADate(value); return true; }
        catch (ArgumentException) { return false; }
    }
    private static bool ZoneExists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
