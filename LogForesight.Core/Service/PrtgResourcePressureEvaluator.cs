using System.Security.Cryptography;
using System.Text;

namespace LogForesight.Core.Service;

/// <summary>Pure two-hour policy evaluation. Results are advisory values; this class has no persistence or side effects.</summary>
public sealed record PrtgResourcePressureDecision(
    PrtgResourceDecisionKind Kind,
    PrtgResourceDecisionMode Mode,
    string RuleCode,
    string ProfileFingerprint,
    string ReasonCode,
    PrtgResourceFamily Family,
    DateTime? AsOfUtc,
    IReadOnlyList<PrtgResourceReadyHour> Window,
    double? WindowCoveragePercent,
    double? EarlierHourAveragePercent,
    double? LatestHourAveragePercent,
    string? FormalSeverity,
    bool ElevatesDayRisk);

public static class PrtgResourcePressureEvaluator
{
    public const string RulesVersion = "resource-period-rules-v1";
    private const string ProfileVersion = "resource-period-v2";

    public static string GetProfileFingerprint(PrtgResourceFamily family,
        PrtgResourceCurrentContext? context = null)
    {
        var policy = family switch
        {
            PrtgResourceFamily.Cpu => "cpu-used;unit=%;scale=1;window=2-completed-consecutive-wall-hours;coverage=75;hit=each>=90;recovery=each<85;default=hint",
            PrtgResourceFamily.Memory => "memory-used;remaining-converts=100-value;unit=%;scale=1;window=2-completed-consecutive-wall-hours;coverage=75;hit=each>=90;recovery=each<85;default=hint",
            PrtgResourceFamily.Disk => "disk-remaining;unit=%;scale=1;window=2-completed-consecutive-wall-hours;coverage=75;hit=each<=10;recovery=each>15;default=formal-high-nondayrisk",
            _ => "invalid-family"
        };
        var contextVersion = context is null ? "unbound" :
            $"{context.SensorObjid}|{context.SourceGeneration}|{context.ResourceGeneration}|{context.ChannelGeneration}|" +
            $"{context.ResourceEpoch}|{context.SemanticVersion}|{context.StrategyVersion}|{context.StrategyMinutes}|" +
            $"{context.StrategyEffectiveFromHour.Ticks}|{context.ConfirmedScanInterval.Ticks}|{context.ProfileFingerprint}|" +
            $"{context.RawTimestampTimeZoneId}|{context.AnalysisTimeZoneId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ProfileVersion}|{RulesVersion}|{policy}|{contextVersion}")));
    }

    public static PrtgResourcePressureDecision Evaluate(PrtgResourceReadinessInput input) =>
        EvaluateCore(input, null);

    internal static PrtgResourcePressureDecision EvaluateAuthorized(PrtgResourceReadinessInput input,
        PrtgResourcePolicyAuthorization authorization) => EvaluateCore(input, authorization);

    private static PrtgResourcePressureDecision EvaluateCore(PrtgResourceReadinessInput input,
        PrtgResourcePolicyAuthorization? authorization)
    {
        ArgumentNullException.ThrowIfNull(input);
        var readiness = PrtgResourcePeriodReadinessEvaluator.Evaluate(input);
        var mode = authorization?.RequestedMode ?? DefaultMode(input.Family);
        if (!Enum.IsDefined(mode))
            return Decision(PrtgResourceDecisionKind.Insufficient, mode, input, readiness,
                "invalid-decision-mode");
        if (!readiness.IsReady)
            return Decision(PrtgResourceDecisionKind.Insufficient, mode, input, readiness,
                readiness.ReasonCode);
        if (mode == PrtgResourceDecisionMode.FormalRisk &&
            (input.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory) &&
            (authorization is null || !authorization.MaintainAuthorized ||
             !authorization.ServerPersistedTrial ||
             authorization.TrialProfileFingerprint != readiness.ProfileFingerprint ||
             authorization.RulesVersion != RulesVersion))
            return Decision(PrtgResourceDecisionKind.Insufficient, mode, input, readiness,
                "formal-mode-requires-same-version-trial-and-maintain");

        var first = readiness.Window[0].AveragePercent;
        var second = readiness.Window[1].AveragePercent;
        var (kind, reason) = input.Family switch
        {
            PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory when first >= 90 && second >= 90 =>
                (PrtgResourceDecisionKind.Hit, "both-completed-hour-averages-at-or-above-90"),
            PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory when first < 85 && second < 85 =>
                (PrtgResourceDecisionKind.Recovery, "both-completed-hour-averages-below-85"),
            PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory =>
                (PrtgResourceDecisionKind.NoHit, "two-hour-pressure-threshold-not-met"),
            PrtgResourceFamily.Disk when first <= 10 && second <= 10 =>
                (PrtgResourceDecisionKind.Hit, "both-completed-hour-remaining-averages-at-or-below-10"),
            PrtgResourceFamily.Disk when first > 15 && second > 15 =>
                (PrtgResourceDecisionKind.Recovery, "both-completed-hour-remaining-averages-above-15"),
            PrtgResourceFamily.Disk =>
                (PrtgResourceDecisionKind.NoHit, "two-hour-low-water-threshold-not-met"),
            _ => (PrtgResourceDecisionKind.Insufficient, "invalid-resource-family")
        };
        return Decision(kind, mode, input, readiness, reason);
    }

    private static PrtgResourceDecisionMode DefaultMode(PrtgResourceFamily family) =>
        family == PrtgResourceFamily.Disk ? PrtgResourceDecisionMode.FormalRisk : PrtgResourceDecisionMode.Hint;

    private static PrtgResourcePressureDecision Decision(PrtgResourceDecisionKind kind,
        PrtgResourceDecisionMode mode, PrtgResourceReadinessInput input, PrtgResourcePeriodReadiness readiness,
        string reason) => new(kind, mode, RuleCode(input.Family), readiness.ProfileFingerprint, reason,
            input.Family, readiness.AsOfUtc, readiness.Window, readiness.WindowCoveragePercent,
            readiness.Window.Count > 0 ? readiness.Window[0].AveragePercent : null,
            readiness.Window.Count > 1 ? readiness.Window[1].AveragePercent : null,
            input.Family == PrtgResourceFamily.Disk && mode == PrtgResourceDecisionMode.FormalRisk ? "High" : null,
            ElevatesDayRisk: false);

    private static string RuleCode(PrtgResourceFamily family) => family switch
    {
        PrtgResourceFamily.Cpu => "prtg.resource.cpu-sustained-pressure",
        PrtgResourceFamily.Memory => "prtg.resource.memory-sustained-pressure",
        PrtgResourceFamily.Disk => "prtg.resource.disk-low-water",
        _ => "prtg.resource.invalid"
    };
}

/// <summary>
/// Internal capability minted only after the server validates a persisted opaque trial token and
/// the authenticated caller's Maintain permission. It cannot be constructed from a request DTO.
/// </summary>
internal sealed class PrtgResourcePolicyAuthorization
{
    internal PrtgResourcePolicyAuthorization(PrtgResourceDecisionMode requestedMode,
        bool maintainAuthorized, bool serverPersistedTrial, string trialProfileFingerprint,
        string rulesVersion)
    {
        RequestedMode = requestedMode;
        MaintainAuthorized = maintainAuthorized;
        ServerPersistedTrial = serverPersistedTrial;
        TrialProfileFingerprint = trialProfileFingerprint;
        RulesVersion = rulesVersion;
    }

    internal PrtgResourceDecisionMode RequestedMode { get; }
    internal bool MaintainAuthorized { get; }
    internal bool ServerPersistedTrial { get; }
    internal string TrialProfileFingerprint { get; }
    internal string RulesVersion { get; }
}
