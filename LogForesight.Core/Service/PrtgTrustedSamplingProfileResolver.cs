using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

public sealed record PrtgTrustedSamplingProfileResolution(
    PrtgTrustedSnapshotContext? Context,
    IReadOnlyList<string> MissingFacts,
    string? RejectionReason)
{
    public bool Ready => Context != null && MissingFacts.Count == 0 && RejectionReason == null;
}

/// <summary>Joins a stored typed probe profile to the current DB identity and live settings.</summary>
public static class PrtgTrustedSamplingProfileResolver
{
    public static string StrategyFingerprint(string sourceGeneration,
        string rawTimestampTimeZoneId, string sourceApiTimeZoneId, string analysisTimeZoneId,
        string strategyName, int strategyMinutes)
    {
        var facts = string.Join("\u001f", sourceGeneration, rawTimestampTimeZoneId,
            sourceApiTimeZoneId, analysisTimeZoneId, strategyName,
            strategyMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)));
    }

    public static PrtgTrustedSamplingProfileResolution Resolve(
        PrtgTrustedSamplingProfile? profile, PrtgResourceIdentity? identity,
        PrtgMonitoringPolicy policy, long sensorObjid, string sensorType,
        string strategyName, int strategyMinutes, DateTime effectiveFromHourUtc,
        DateTime receivedAtUtc, DateTime asOfUtc, DateTime? authorityNowUtc = null)
    {
        var missing = new List<string>();
        if (profile == null) return Missing("profile");
        if (identity == null || !identity.Active || identity.PendingReconciliation || identity.Epoch <= 0)
            return Missing("current_resource_identity");
        if (string.IsNullOrWhiteSpace(policy.SourceGeneration) || string.IsNullOrWhiteSpace(policy.Revision))
            return Missing("current_source_policy");
        if (string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference)) missing.Add("raw_timestamp_timezone_probe");
        if (string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId)) missing.Add("analysis_timezone");
        if (string.IsNullOrWhiteSpace(sensorType)) missing.Add("sensor_type");
        if (string.IsNullOrWhiteSpace(profile.PrimaryChannelId)) missing.Add("primary_channel_id");
        if (string.IsNullOrWhiteSpace(profile.PrimaryChannelCaption)) missing.Add("primary_channel_caption");
        if (profile.Quantity == PrtgTrustedQuantitySemantic.Unknown || string.IsNullOrWhiteSpace(profile.Unit) ||
            string.IsNullOrWhiteSpace(profile.Direction)) missing.Add("quantity_unit_direction");
        if (string.IsNullOrWhiteSpace(profile.SourceMetadataReference)) missing.Add("source_metadata_reference");
        if (string.IsNullOrWhiteSpace(profile.PhysicalSampleReference)) missing.Add("physical_sample_comparison");
        if (missing.Count > 0) return new(null, missing, "source_authority_incomplete");

        var strategyFingerprint = StrategyFingerprint(policy.SourceGeneration,
            profile.RawTimestampTimeZoneId, profile.SourceApiTimeZoneId, profile.AnalysisTimeZoneId,
            strategyName, strategyMinutes);
        if (profile.SensorObjid != sensorObjid || profile.SourceGeneration != policy.SourceGeneration ||
            profile.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            profile.SourceApiTimeZoneId != policy.SourceTimeZoneId ||
            profile.AnalysisTimeZoneId != policy.AnalysisTimeZoneId ||
            !profile.MatchesCurrent(identity, sensorType, strategyFingerprint, strategyMinutes,
                effectiveFromHourUtc, new DateTimeOffset(authorityNowUtc ?? asOfUtc)))
            return new(null, Array.Empty<string>(), "profile_identity_or_policy_stale");

        try
        {
            profile.Validate();
            var context = new PrtgTrustedSnapshotContext(sensorObjid, profile.SourceGeneration,
                profile.ResourceGeneration, profile.ChannelGeneration, profile.IdentityEpoch.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), profile.SemanticVersion,
                profile.StrategyFingerprint, profile.StrategyMinutes, profile.StrategyEffectiveFromHourUtc,
                profile.RawTimestampTimeZoneId, profile.ConfirmedScanInterval, receivedAtUtc, asOfUtc,
                raw => double.IsFinite(raw) && raw > 0 &&
                    (profile.IntervalRawUnit == "seconds" ? TimeSpan.FromSeconds(raw) : TimeSpan.FromMinutes(raw)) == profile.ConfirmedScanInterval
                    ? profile.ConfirmedScanInterval : null,
                true, profile.AnalysisTimeZoneId);
            context = context with
            {
                NormalizeConfirmedQuantity = value => profile.Direction switch
                {
                    "direct" => value * profile.Scale,
                    "inverse" => -value * profile.Scale,
                    "absolute" => Math.Abs(value) * profile.Scale,
                    _ => throw new InvalidDataException("unconfirmed_quantity_direction")
                },
                SelectedPrimaryChannelId = profile.PrimaryChannelId
            };
            return new(context, Array.Empty<string>(), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or TimeZoneNotFoundException)
        { return new(null, Array.Empty<string>(), "profile_invalid"); }

        PrtgTrustedSamplingProfileResolution Missing(string fact) => new(null, new[] { fact }, "source_authority_incomplete");
    }

    public static PrtgTrustedSamplingProfileResolution Resolve(
        PrtgTrustedSamplingProfile? profile, PrtgResourceIdentity? identity,
        PrtgMonitoringPolicy policy, long sensorObjid, string sensorType,
        PrtgTrustedSamplingStrategyContext currentStrategy,
        DateTime receivedAtUtc, DateTime asOfUtc, DateTime? authorityNowUtc = null)
    {
        if (currentStrategy == null || !currentStrategy.Ready)
            return new(null, currentStrategy?.MissingFacts ?? ["current_strategy_contract"],
                "current_strategy_contract_missing");
        if (currentStrategy.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            currentStrategy.AnalysisTimeZoneId != policy.AnalysisTimeZoneId)
            return new(null, ["current_time_basis"], "current_strategy_contract_stale");
        var result = Resolve(profile, identity, policy, sensorObjid, sensorType,
            currentStrategy.StrategyName, currentStrategy.StrategyMinutes,
            currentStrategy.EffectiveFromHourUtc, receivedAtUtc, asOfUtc,
            authorityNowUtc ?? asOfUtc);
        if (!result.Ready || profile == null) return result;
        if (profile.StrategyFingerprint != currentStrategy.StrategyFingerprint)
            return new(null, Array.Empty<string>(), "profile_strategy_fingerprint_stale");
        return result;
    }
}
