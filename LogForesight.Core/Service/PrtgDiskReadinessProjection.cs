using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>Scalar-only value row retained after a bounded SQL proof projection is checked.</summary>
public sealed record PrtgDiskReadinessValue(long Id, long SensorObjid, DateTime PeriodStart,
    double? AvgValue, double? MinValue, double? MaxValue, double? Coverage, string Quality,
    bool Trusted)
{
    /// <summary>Exact bounded physical proof and scalar input digest; never a native source reference.</summary>
    public string EvidenceFingerprint { get; init; } = string.Empty;
}

/// <summary>SQL projection keeps at most one proof prefix; it is never passed to API output.</summary>
internal sealed record PrtgDiskReadinessProofProjection(long Id, long SensorObjid, DateTime PeriodStart,
    double? AvgValue, double? MinValue, double? MaxValue, double? Coverage, string Quality,
    int TrustVersion, string? ProofPrefix, int ProofLength);

internal sealed class PrtgReadinessCapacityException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}

internal static class PrtgDiskTrustedProofValidator
{
    public static bool IsTrusted(PrtgDiskReadinessProofProjection row,
        PrtgTrustedSamplingProfileResolution profileResolution, DateTime? evidenceAsOfUtc = null)
    {
        if (evidenceAsOfUtc is { Kind: not DateTimeKind.Utc }) return false;
        if (row.TrustVersion != 1 || row.ProofPrefix is null || row.ProofLength <= 0 ||
            row.ProofLength > PrtgTrustedSampleProof.MaximumSerializedBytes ||
            row.ProofPrefix.Length != row.ProofLength ||
            Encoding.UTF8.GetByteCount(row.ProofPrefix) > PrtgTrustedSampleProof.MaximumSerializedBytes ||
            !profileResolution.Ready || profileResolution.Context is not { } context || context.SensorObjid != row.SensorObjid ||
            row.AvgValue is not { } average || row.MinValue is not { } minimum || row.MaxValue is not { } maximum ||
            !double.IsFinite(average) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > average || average > maximum)
            return false;
        try
        {
            var proof = PrtgTrustedSampleProof.Deserialize(row.ProofPrefix);
            if (!proof.IsStructurallyValid() || !proof.MatchesHour(row.PeriodStart) ||
                proof.SourceGeneration != context.SourceGeneration || proof.ResourceGeneration != context.ResourceGeneration ||
                proof.ChannelGeneration != context.ChannelGeneration || proof.ResourceEpoch != context.ResourceEpoch ||
                proof.SemanticVersion != context.SemanticVersion || proof.StrategyVersion != context.StrategyVersion ||
                proof.StrategyMinutes != context.StrategyMinutes || proof.StrategyEffectiveFromHour != context.StrategyEffectiveFromHour ||
                proof.ConfirmedScanInterval != context.ConfirmedScanInterval ||
                proof.RawTimestampTimeZoneId != context.RawTimestampTimeZoneId ||
                proof.AnalysisTimeZoneId != context.AnalysisTimeZoneId ||
                proof.Slots.Any(s => s.MeasuredAt < proof.StrategyEffectiveFromHour ||
                    evidenceAsOfUtc.HasValue && (s.MeasuredAt > evidenceAsOfUtc.Value || s.ReceivedAt > evidenceAsOfUtc.Value)))
                return false;

            var expectedSlots = 60 / proof.StrategyMinutes;
            var actualValues = proof.Slots.Select(s => s.Value).Order().ToArray();
            var coverage = row.Coverage ?? 0;
            if (!double.IsFinite(coverage) || coverage is < 0 or > 100) return false;
            if (string.Equals(row.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase) &&
                (actualValues.Length != expectedSlots || Math.Abs(coverage - 100d) > 0.01))
                return false;
            if (string.Equals(row.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase) &&
                (actualValues.Length > expectedSlots || coverage < PrtgValueUsability.SampledMinCoverage ||
                 Math.Abs(coverage - actualValues.Length * 100d / expectedSlots) > 0.01)) return false;
            if (!string.Equals(row.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(row.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase)) return false;
            return Near(average, actualValues.Average()) && Near(minimum, actualValues.Min()) && Near(maximum, actualValues.Max());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException or TimeZoneNotFoundException)
        { return false; }
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) <= Math.Max(1e-9, Math.Abs(right) * 1e-9);
}
