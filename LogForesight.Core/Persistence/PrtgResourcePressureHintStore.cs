using LogForesight.Core.Service;
using System.Security.Cryptography;
using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// Durable, per-host resource-period display state. These records are diagnostic hints; callers
/// must not map them to issue signatures, cases, mail, or day-risk changes.
/// </summary>
public sealed class PrtgResourcePressureHintStore(EfJsonBlobStore blob)
    : JsonBlobSingleton<PrtgResourcePressureHintDocument>(blob)
{
    public const int MaximumHintsPerHost = 128;
    public const int MaximumHintAgeDays = 14;
    private const int MaximumReasonLength = 96;

    public static string BlobKey(long hostId) => hostId > 0
        ? $"prtg_resource_pressure_hints_{hostId}"
        : throw new ArgumentOutOfRangeException(nameof(hostId));

    /// <summary>Returns only bounded, validated hints for the requested host.</summary>
    public IReadOnlyList<PrtgResourcePressureHint> GetCurrent(long hostId, DateTime asOfUtc)
    {
        if (hostId <= 0 || asOfUtc.Kind != DateTimeKind.Utc) return Array.Empty<PrtgResourcePressureHint>();
        var cutoff = asOfUtc.AddDays(-MaximumHintAgeDays);
        return (Get().Items ?? [])
            .Where(IsValid)
            .Where(h => h.HostId == hostId && EvidenceTime(h) >= cutoff && EvidenceTime(h) <= asOfUtc)
            .OrderBy(h => h.Family).ThenBy(h => h.SensorObjid)
            .ToArray();
    }

    /// <summary>
    /// Idempotently saves the latest evidence for a resource. Evidence time orders evaluations;
    /// authority time only arbitrates a refreshed evaluation of the same evidence cutoff.
    /// </summary>
    public bool Upsert(PrtgResourcePressureHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        if (!IsValid(hint)) throw new ArgumentException("PRTG resource hint is invalid or exceeds its bounds.", nameof(hint));
        var changed = false;
        Update(document =>
        {
            document.Items ??= [];
            if (document.Items.Count > MaximumHintsPerHost)
                throw new InvalidDataException("PRTG resource hint store exceeds its bound.");

            var index = document.Items.FindIndex(existing =>
                existing.SensorObjid == hint.SensorObjid && existing.Family == hint.Family);
            if (index >= 0)
            {
                var prior = document.Items[index];
                var priorEvidenceTime = EvidenceTime(prior);
                var incomingEvidenceTime = EvidenceTime(hint);
                if (priorEvidenceTime > incomingEvidenceTime) return;
                if (priorEvidenceTime == incomingEvidenceTime)
                {
                    if (prior.AsOfUtc > hint.AsOfUtc) return;
                    if (prior.AsOfUtc == hint.AsOfUtc && Fingerprint(prior) != Fingerprint(hint))
                        throw new InvalidDataException("Conflicting resource hint for the same source as-of.");
                    if (prior.AsOfUtc == hint.AsOfUtc) return;
                }
                document.Items[index] = hint;
                changed = true;
                return;
            }

            if (document.Items.Count >= MaximumHintsPerHost)
                throw new InvalidDataException("PRTG resource hint host capacity reached; refusing to truncate.");
            document.Items.Add(hint);
            changed = true;
        });
        return changed;
    }

    private static bool IsValid(PrtgResourcePressureHint? hint) => hint is not null &&
        hint.HostId > 0 && hint.DeviceObjid > 0 && hint.SensorObjid > 0 &&
        Enum.IsDefined(hint.Family) && Enum.IsDefined(hint.Kind) &&
        hint.AsOfUtc.Kind == DateTimeKind.Utc && hint.AsOfUtc != default &&
        hint.ReasonCode is { Length: > 0 and <= MaximumReasonLength } &&
        hint.ReasonCode.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
        (string.IsNullOrEmpty(hint.ProfileFingerprint) || IsSha256(hint.ProfileFingerprint)) &&
        ValidOptional(hint.SourceGeneration, 128) && ValidOptional(hint.ResourceGeneration, 128) &&
        ValidOptional(hint.ChannelGeneration, 128) && ValidOptional(hint.ResourceEpoch, 64) &&
        ValidOptional(hint.SemanticVersion, 128) && ValidOptional(hint.StrategyVersion, 128) &&
        (!hint.EarlierHourAveragePercent.HasValue || double.IsFinite(hint.EarlierHourAveragePercent.Value) && hint.EarlierHourAveragePercent is >= 0 and <= 100) &&
        (!hint.LatestHourAveragePercent.HasValue || double.IsFinite(hint.LatestHourAveragePercent.Value) && hint.LatestHourAveragePercent is >= 0 and <= 100) &&
        (!hint.WindowCoveragePercent.HasValue || double.IsFinite(hint.WindowCoveragePercent.Value) && hint.WindowCoveragePercent is >= 0 and <= 100) &&
        ValidOptionalUtc(hint.EvidenceAsOfUtc) && ValidOptionalUtc(hint.FirstHourStartUtc) && ValidOptionalUtc(hint.LatestHourStartUtc) &&
        ValidOptionalUtc(hint.EpisodeStartedAtUtc) &&
        EvidenceTime(hint) <= hint.AsOfUtc &&
        (hint.MissingFacts is null || hint.MissingFacts.Count <= 16 && hint.MissingFacts.All(IsSafeFactCode));

    private static bool ValidOptional(string? value, int max) => value is null || value.Length <= max;
    private static bool ValidOptionalUtc(DateTime? value) => !value.HasValue || value.Value.Kind == DateTimeKind.Utc;
    private static bool IsSafeFactCode(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static DateTime EvidenceTime(PrtgResourcePressureHint hint) => hint.EvidenceAsOfUtc ?? hint.AsOfUtc;
    private static string Fingerprint(PrtgResourcePressureHint hint) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hint)));
}

public sealed class PrtgResourcePressureHintDocument
{
    public List<PrtgResourcePressureHint> Items { get; set; } = [];
}

/// <summary>Safe display projection of a bounded two-hour evaluation, with no issue/case fields.</summary>
public sealed record PrtgResourcePressureHint(
    long HostId,
    long DeviceObjid,
    long SensorObjid,
    PrtgResourceFamily Family,
    PrtgResourceDecisionKind Kind,
    string ReasonCode,
    DateTime AsOfUtc,
    string ProfileFingerprint,
    string? SourceGeneration,
    string? ResourceGeneration,
    string? ChannelGeneration,
    string? ResourceEpoch,
    string? SemanticVersion,
    string? StrategyVersion,
    double? EarlierHourAveragePercent,
    double? LatestHourAveragePercent,
    double? WindowCoveragePercent,
    DateTime? EvidenceAsOfUtc = null,
    DateTime? FirstHourStartUtc = null,
    DateTime? LatestHourStartUtc = null,
    DateTime? EpisodeStartedAtUtc = null,
    IReadOnlyList<string>? MissingFacts = null);
