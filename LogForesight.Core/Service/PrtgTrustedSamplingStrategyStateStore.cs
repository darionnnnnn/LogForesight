using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

public sealed record PrtgTrustedSamplingStrategyContext(
    string StrategyName, int StrategyMinutes, string StrategyFingerprint,
    DateTime EffectiveFromHourUtc, string RawTimestampTimeZoneId,
    string AnalysisTimeZoneId, IReadOnlyList<string> MissingFacts)
{
    public bool Ready => MissingFacts.Count == 0 && EffectiveFromHourUtc.Kind == DateTimeKind.Utc;
}

public sealed record PrtgTrustedSamplingPolicyContext(
    PrtgMonitoringPolicy Policy, PrtgTrustedSamplingStrategyContext Strategy);

/// <summary>
/// Persists the first whole analysis-wall-hour at which the current strategy/time-basis became active.
/// The single small blob is a fence, not sensor coverage or identity authority.
/// </summary>
public sealed class PrtgTrustedSamplingStrategyStateStore(EfJsonBlobStore blob)
{
    public const string BlobKey = "prtg_trusted_sampling_strategy_v1";
    private sealed record State(string Fingerprint, string StrategyName, int StrategyMinutes,
        string RawTimestampTimeZoneId, string AnalysisTimeZoneId, DateTime EffectiveFromHourUtc);

    /// <summary>Read-only strategy resolution for consumers such as historical readiness queries.</summary>
    public static PrtgTrustedSamplingStrategyContext ReadCurrent(string? serializedState,
        PrtgMonitoringPolicy policy, string strategyName, int strategyMinutes)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(policy.SourceGeneration)) missing.Add("source_generation");
        if (string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference)) missing.Add("raw_timestamp_timezone_probe");
        if (string.IsNullOrWhiteSpace(policy.SourceTimeZoneId)) missing.Add("source_api_timezone");
        if (string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId)) missing.Add("analysis_timezone");
        if (strategyMinutes is not (5 or 15)) missing.Add("snapshot_strategy");
        if (missing.Count > 0)
            return new(strategyName, strategyMinutes, "", default, policy.RawTimestampTimeZoneId,
                policy.AnalysisTimeZoneId, missing);

        var fingerprint = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(policy.SourceGeneration,
            policy.RawTimestampTimeZoneId, policy.SourceTimeZoneId, policy.AnalysisTimeZoneId,
            strategyName, strategyMinutes);
        if (string.IsNullOrWhiteSpace(serializedState))
            return new(strategyName, strategyMinutes, "", default, policy.RawTimestampTimeZoneId,
                policy.AnalysisTimeZoneId, ["strategy_effective_hour_missing"]);
        State? state;
        try { state = JsonSerializer.Deserialize<State>(serializedState, LfJsonOptions.Pretty); }
        catch (JsonException ex) { throw new InvalidDataException("PRTG snapshot strategy fence 格式損壞。", ex); }
        if (state == null || state.Fingerprint != fingerprint || state.StrategyName != strategyName ||
            state.StrategyMinutes != strategyMinutes || state.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            state.AnalysisTimeZoneId != policy.AnalysisTimeZoneId || state.EffectiveFromHourUtc.Kind != DateTimeKind.Utc)
            return new(strategyName, strategyMinutes, "", default, policy.RawTimestampTimeZoneId,
                policy.AnalysisTimeZoneId, ["strategy_fence_stale"]);
        return new(strategyName, strategyMinutes, fingerprint, state.EffectiveFromHourUtc,
            policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId, Array.Empty<string>());
    }

    public PrtgTrustedSamplingStrategyContext GetCurrent(PrtgMonitoringPolicy policy,
        string strategyName, int strategyMinutes, DateTime nowUtc)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(policy.SourceGeneration)) missing.Add("source_generation");
        if (string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference)) missing.Add("raw_timestamp_timezone_probe");
        if (string.IsNullOrWhiteSpace(policy.SourceTimeZoneId)) missing.Add("source_api_timezone");
        if (string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId)) missing.Add("analysis_timezone");
        if (strategyMinutes is not (5 or 15)) missing.Add("snapshot_strategy");
        if (missing.Count > 0)
            return new(strategyName, strategyMinutes, "", default, policy.RawTimestampTimeZoneId,
                policy.AnalysisTimeZoneId, missing);

        var fingerprint = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(policy.SourceGeneration,
            policy.RawTimestampTimeZoneId, policy.SourceTimeZoneId, policy.AnalysisTimeZoneId,
            strategyName, strategyMinutes);
        State? resolved = null;
        blob.Mutate(raw =>
        {
            State? prior = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { prior = JsonSerializer.Deserialize<State>(raw, LfJsonOptions.Pretty); }
                catch (JsonException ex) { throw new InvalidDataException("PRTG snapshot strategy fence 格式損壞。", ex); }
            }
            if (prior == null || prior.Fingerprint != fingerprint || prior.StrategyName != strategyName ||
                prior.StrategyMinutes != strategyMinutes || prior.EffectiveFromHourUtc.Kind != DateTimeKind.Utc)
                resolved = new State(fingerprint, strategyName, strategyMinutes,
                    policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId,
                    NextWallHourUtc(nowUtc, policy.AnalysisTimeZoneId));
            else resolved = prior;
            return (JsonSerializer.Serialize(resolved, LfJsonOptions.Pretty), resolved);
        });
        return new(strategyName, strategyMinutes, fingerprint, resolved!.EffectiveFromHourUtc,
            policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId, Array.Empty<string>());
    }

    private static DateTime NextWallHourUtc(DateTime nowUtc, string analysisZoneId)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("策略時間必須是 UTC。", nameof(nowUtc));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(analysisZoneId);
        var wall = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var candidate = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified)
            .AddHours(wall.Minute == 0 && wall.Second == 0 && wall.Millisecond == 0 && wall.Ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);
        // Skip a nonexistent or ambiguous DST wall hour. The next valid wall-hour boundary is stable.
        for (var attempt = 0; attempt < 4; attempt++, candidate = candidate.AddHours(1))
        {
            if (zone.IsInvalidTime(candidate) || zone.IsAmbiguousTime(candidate)) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            var roundTrip = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            if (!zone.IsInvalidTime(roundTrip) && !zone.IsAmbiguousTime(roundTrip) &&
                roundTrip.Minute == 0 && roundTrip.Second == 0 && roundTrip.Millisecond == 0)
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        throw new InvalidDataException("找不到明確的下一個分析整點。 ");
    }
}
