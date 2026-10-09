using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>Server-built result of resolving a current trusted profile and bounded persisted slots.</summary>
public sealed class PrtgResourcePeriodAssessment
{
    internal PrtgResourcePeriodAssessment(long hostId, long deviceObjid, long sensorObjid,
        PrtgResourceFamily family, PrtgResourceReadinessInput input,
        PrtgResourcePressureDecision decision, DateTime authorityAsOfUtc, string sourceGeneration,
        string resourceGeneration, string channelGeneration, string resourceEpoch,
        string semanticVersion, string strategyVersion, KnownIssueRule? currentRule,
        string currentRuleFingerprint, string ruleAdmissionFingerprint)
    {
        HostId = hostId;
        DeviceObjid = deviceObjid;
        SensorObjid = sensorObjid;
        Family = family;
        Input = input;
        Decision = decision;
        AuthorityAsOfUtc = authorityAsOfUtc;
        SourceGeneration = sourceGeneration;
        ResourceGeneration = resourceGeneration;
        ChannelGeneration = channelGeneration;
        ResourceEpoch = resourceEpoch;
        SemanticVersion = semanticVersion;
        StrategyVersion = strategyVersion;
        CurrentRule = currentRule;
        CurrentRuleFingerprint = currentRuleFingerprint;
        RuleAdmissionFingerprint = ruleAdmissionFingerprint;
        EvidenceFingerprint = BuildEvidenceFingerprint(input, decision);
    }

    public long HostId { get; }
    public long DeviceObjid { get; }
    public long SensorObjid { get; }
    public PrtgResourceFamily Family { get; }
    public PrtgResourcePressureDecision Decision { get; }
    /// <summary>Current server time used to qualify metadata and identity.</summary>
    public DateTime AsOfUtc => AuthorityAsOfUtc;
    /// <summary>Cutoff for the actual physical-sample window.</summary>
    public DateTime EvidenceAsOfUtc => Input.AsOfUtc;
    public DateTime AuthorityAsOfUtc { get; }
    /// <summary>Host record date only when both evidence hours map to the same local host day.</summary>
    public DateTime? SingleWindowHostDay
    {
        get
        {
            if (Decision.Window.Count != 2 || Input.AuthorizedContext is not { } context) return null;
            try
            {
                var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(context.AnalysisTimeZoneId);
                var hostDates = Decision.Window.Select(hour =>
                {
                    var wall = DateTime.SpecifyKind(hour.WallPeriodStart, DateTimeKind.Unspecified);
                    var utc = TimeZoneInfo.ConvertTimeToUtc(wall, analysisZone);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).Date;
                }).Distinct().ToArray();
                return hostDates.Length == 1
                    ? DateTime.SpecifyKind(hostDates[0], DateTimeKind.Unspecified)
                    : null;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                return null;
            }
        }
    }
    public string ProfileFingerprint => Decision.ProfileFingerprint;
    public bool IsComplete => Decision.Kind != PrtgResourceDecisionKind.Insufficient;
    /// <summary>Stable digest over the accepted context, two wall-hour results, and slot proofs.</summary>
    public string EvidenceFingerprint { get; }
    internal PrtgResourceReadinessInput Input { get; }
    internal string SourceGeneration { get; }
    internal string ResourceGeneration { get; }
    internal string ChannelGeneration { get; }
    internal string ResourceEpoch { get; }
    internal string SemanticVersion { get; }
    internal string StrategyVersion { get; }
    public KnownIssueRule? CurrentRule { get; }
    public string CurrentRuleFingerprint { get; }
    public string RuleAdmissionFingerprint { get; }

    private static string BuildEvidenceFingerprint(PrtgResourceReadinessInput input,
        PrtgResourcePressureDecision decision)
    {
        var proofFacts = (input.Hours ?? Array.Empty<PrtgResourceHourlyEvidence>())
            .OrderBy(hour => hour.WallPeriodStart)
            .Select(hour => new
            {
                hour.SensorObjid,
                hour.WallPeriodStart,
                Proof = hour.ContextProof is null ? null : new
                {
                    hour.ContextProof.ContextHash,
                    Slots = hour.ContextProof.Slots.OrderBy(slot => slot.Slot)
                        .Select(slot => new { slot.Slot, slot.Value, slot.MeasuredAt, slot.ReceivedAt, slot.PhysicalIdHash })
                        .ToArray()
                }
            }).ToArray();
        var material = JsonSerializer.Serialize(new
        {
            decision.ProfileFingerprint,
            decision.Kind,
            decision.ReasonCode,
            Window = decision.Window.OrderBy(hour => hour.WallPeriodStart)
                .Select(hour => new { hour.WallPeriodStart, hour.AveragePercent, hour.CoveragePercent, hour.GoodSlots, hour.RequiredSlots })
                .ToArray(),
            Proofs = proofFacts
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
