using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Service;

namespace LogForesight.Core.Persistence;

public enum PrtgResourceFormalReasonState { Active, Recovered, Unknown }

/// <summary>A typed, current-identity observation from one independently qualified disk rule.</summary>
public sealed record PrtgResourceFormalReasonObservation(
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    string ReasonCode,
    PrtgResourceFormalReasonState State,
    string EvidenceFingerprint,
    string Summary,
    DateTime? ExistingEpisodeStartedAtUtc = null,
    DateTime? EvidenceDay = null,
    string? SourceRuleId = null,
    string? SourceRuleFingerprint = null,
    DateTime? EvidenceAsOfUtc = null);

public sealed record PrtgResourceFormalReason(
    string ReasonCode,
    string EvidenceFingerprint,
    string Summary,
    DateTime FirstObservedAtUtc,
    DateTime EvidenceDay,
    string? SourceRuleId,
    string? SourceRuleFingerprint);

public sealed record PrtgResourceFormalEpisode(
    long HostId,
    long DeviceObjid,
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    DateTime EpisodeObservedSinceUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<PrtgResourceFormalReason> Reasons,
    string ReasonSetFingerprint)
{
    public DateTime EvidenceAsOfUtc { get; init; }
}

/// <summary>Latest accepted evidence watermark, retained even when an episode recovers.</summary>
public sealed record PrtgResourceFormalEvidenceWatermark(
    long HostId,
    long DeviceObjid,
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    DateTime EvidenceAsOfUtc,
    DateTime AuthorityAsOfUtc,
    string AssessmentEvidenceFingerprint,
    string ReasonEvidenceFingerprint,
    string CurrentRuleFingerprint,
    string RuleAdmissionFingerprint);

internal sealed record PrtgResourceFormalReconcileResult(bool Accepted, PrtgResourceFormalEpisode? Episode);

public sealed class PrtgResourceFormalEpisodeDocument
{
    public List<PrtgResourceFormalEpisode> Items { get; set; } = [];
    public List<PrtgResourceFormalEvidenceWatermark> Watermarks { get; set; } = [];
}

/// <summary>
/// Stores one stable disk episode across its low-water and trend reasons. Unknown/incomplete
/// evaluations preserve active reasons; only an explicit qualified recovery removes one.
/// </summary>
public sealed class PrtgResourceFormalEpisodeStore(EfJsonBlobStore blob)
    : JsonBlobSingleton<PrtgResourceFormalEpisodeDocument>(blob)
{
    public const int MaximumEpisodesPerHost = 128;
    public const int MaximumEvidenceWatermarksPerHost = 128;
    private static readonly HashSet<string> ReasonCodes = new(StringComparer.Ordinal)
    {
        "disk-two-hour-low-water", "disk-seven-day-low-water-trend"
    };

    public static string BlobKey(long hostId) => hostId > 0
        ? $"prtg_resource_formal_episodes_{hostId}"
        : throw new ArgumentOutOfRangeException(nameof(hostId));

    public PrtgResourceFormalEpisode? GetCurrent(long hostId, long sensorObjid) =>
        hostId > 0 && sensorObjid > 0
            ? (Get().Items ?? []).FirstOrDefault(e => IsValid(e) && e.HostId == hostId && e.SensorObjid == sensorObjid)
            : null;

    public IReadOnlyList<PrtgResourceFormalEpisode> GetCurrentForHost(long hostId, DateTime asOfUtc)
    {
        if (hostId <= 0 || asOfUtc.Kind != DateTimeKind.Utc) return Array.Empty<PrtgResourceFormalEpisode>();
        var cutoff = asOfUtc.AddDays(-14);
        return (Get().Items ?? []).Where(IsValid)
            .Where(e => e.HostId == hostId && e.UpdatedAtUtc >= cutoff && e.UpdatedAtUtc <= asOfUtc)
            .OrderBy(e => e.SensorObjid).ToArray();
    }

    internal PrtgResourceFormalEpisode? Reconcile(PrtgResourcePeriodAssessment assessment,
        IEnumerable<PrtgResourceFormalReasonObservation> observations) =>
        ReconcileWithStatus(assessment, observations).Episode;

    internal PrtgResourceFormalReconcileResult ReconcileWithStatus(PrtgResourcePeriodAssessment assessment,
        IEnumerable<PrtgResourceFormalReasonObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(observations);
        if (assessment.Family != PrtgResourceFamily.Disk || assessment.HostId <= 0 ||
            assessment.SensorObjid <= 0 || assessment.AuthorityAsOfUtc.Kind != DateTimeKind.Utc ||
            assessment.EvidenceAsOfUtc.Kind != DateTimeKind.Utc ||
            assessment.EvidenceAsOfUtc > assessment.AuthorityAsOfUtc || !IsFingerprint(assessment.EvidenceFingerprint))
            throw new ArgumentException("A disk assessment with bounded UTC evidence and current authority is required.", nameof(assessment));

        var incoming = observations.Take(9).ToArray();
        if (incoming.Length > 8 || incoming.Any(o => !IsCurrentObservation(o, assessment)))
            throw new InvalidDataException("Disk formal reason observations exceed bounds or identity contract.");
        var reasonEvidenceFingerprint = ReasonEvidenceFingerprint(incoming);

        PrtgResourceFormalEpisode? result = null;
        var accepted = false;
        Update(document =>
        {
            document.Items ??= [];
            document.Watermarks ??= [];
            if (document.Items.Count > MaximumEpisodesPerHost)
                throw new InvalidDataException("Disk formal episode store exceeds its bound.");
            if (document.Watermarks.Count > MaximumEvidenceWatermarksPerHost)
                throw new InvalidDataException("Disk formal evidence watermark store exceeds its bound.");
            if (document.Watermarks.Any(w => !IsValid(w)) || document.Watermarks
                    .GroupBy(w => (w.HostId, w.SensorObjid)).Any(group => group.Count() > 1))
                throw new InvalidDataException("Disk formal evidence watermark store contains invalid or duplicate rows.");

            var watermarkIndex = document.Watermarks.FindIndex(w => w is not null &&
                w.HostId == assessment.HostId && w.SensorObjid == assessment.SensorObjid);
            var watermark = watermarkIndex >= 0 ? document.Watermarks[watermarkIndex] : null;
            var watermarkSameIdentity = watermark is not null && SameIdentity(watermark, assessment);
            if (watermark is not null && watermarkSameIdentity &&
                watermark.EvidenceAsOfUtc > assessment.EvidenceAsOfUtc)
                return;
            if (watermark is not null && watermarkSameIdentity &&
                watermark.EvidenceAsOfUtc == assessment.EvidenceAsOfUtc)
            {
                var exactSnapshot = watermark.AssessmentEvidenceFingerprint == assessment.EvidenceFingerprint &&
                    watermark.ReasonEvidenceFingerprint == reasonEvidenceFingerprint &&
                    watermark.CurrentRuleFingerprint == assessment.CurrentRuleFingerprint &&
                    watermark.RuleAdmissionFingerprint == assessment.RuleAdmissionFingerprint;
                if (watermark.AuthorityAsOfUtc > assessment.AuthorityAsOfUtc) return;
                if (watermark.AuthorityAsOfUtc == assessment.AuthorityAsOfUtc)
                {
                    if (!exactSnapshot)
                        throw new InvalidDataException("Conflicting disk assessment for the same evidence and authority time.");
                    result = document.Items.FirstOrDefault(e => e is not null && IsValid(e) &&
                        e.HostId == assessment.HostId && e.SensorObjid == assessment.SensorObjid && SameIdentity(e, assessment));
                    accepted = true;
                    return;
                }
            }
            else if (watermark is not null && watermarkSameIdentity == false &&
                watermark.EvidenceAsOfUtc > assessment.EvidenceAsOfUtc)
            {
                // A resource identity changed after this evidence was captured. Keep the current
                // identity's state and tombstone; an older identity replay cannot roll it back.
                return;
            }
            else if (watermark is not null && watermarkSameIdentity == false &&
                watermark.EvidenceAsOfUtc == assessment.EvidenceAsOfUtc &&
                watermark.AuthorityAsOfUtc >= assessment.AuthorityAsOfUtc)
            {
                // Identity changes at an identical evidence cutoff require a later current
                // authority snapshot; equal/older authority cannot switch the active generation.
                return;
            }

            var index = document.Items.FindIndex(e => e is not null && e.HostId == assessment.HostId &&
                e.SensorObjid == assessment.SensorObjid);
            var prior = index >= 0 ? document.Items[index] : null;
            var sameIdentity = prior is not null && IsValid(prior) && SameIdentity(prior, assessment);
            var reasons = sameIdentity
                ? prior!.Reasons.ToDictionary(r => r.ReasonCode, StringComparer.Ordinal)
                : new Dictionary<string, PrtgResourceFormalReason>(StringComparer.Ordinal);

            foreach (var observation in incoming)
            {
                switch (observation.State)
                {
                    case PrtgResourceFormalReasonState.Active:
                        var firstObserved = reasons.TryGetValue(observation.ReasonCode, out var oldReason)
                            ? oldReason.FirstObservedAtUtc : assessment.EvidenceAsOfUtc;
                        reasons[observation.ReasonCode] = new(observation.ReasonCode,
                            observation.EvidenceFingerprint, observation.Summary, firstObserved,
                            observation.EvidenceDay ?? throw new InvalidDataException("Active disk reason requires its host evidence day."),
                            observation.SourceRuleId, observation.SourceRuleFingerprint);
                        break;
                    case PrtgResourceFormalReasonState.Recovered:
                        reasons.Remove(observation.ReasonCode);
                        break;
                    case PrtgResourceFormalReasonState.Unknown:
                        break;
                    default:
                        throw new InvalidDataException("Disk formal reason state is invalid.");
                }
            }

            if (reasons.Count == 0)
            {
                if (index >= 0) document.Items.RemoveAt(index);
                result = null;
                accepted = true;
                SaveWatermark(document, watermarkIndex, assessment, reasonEvidenceFingerprint);
                return;
            }

            var ordered = reasons.Values.OrderBy(r => r.ReasonCode, StringComparer.Ordinal).ToArray();
            var adoptedStart = incoming.Select(o => o.ExistingEpisodeStartedAtUtc)
                .Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
            if (adoptedStart.Length > 1 || adoptedStart.Any(value => value.Kind != DateTimeKind.Utc || value > assessment.EvidenceAsOfUtc))
                throw new InvalidDataException("Disk episode adoption time is invalid or conflicting.");
            var episodeStart = sameIdentity && prior!.Reasons.Count > 0
                ? prior.EpisodeObservedSinceUtc : adoptedStart.SingleOrDefault(assessment.EvidenceAsOfUtc);
            var fingerprint = Fingerprint(ordered);
            var episode = new PrtgResourceFormalEpisode(assessment.HostId, assessment.DeviceObjid,
                assessment.SensorObjid, assessment.SourceGeneration, assessment.ResourceGeneration,
                assessment.ChannelGeneration, assessment.ResourceEpoch, episodeStart, assessment.AuthorityAsOfUtc,
                ordered, fingerprint) { EvidenceAsOfUtc = assessment.EvidenceAsOfUtc };
            if (index < 0)
            {
                if (document.Items.Count >= MaximumEpisodesPerHost)
                    throw new InvalidDataException("Disk formal episode capacity reached; refusing to truncate.");
                document.Items.Add(episode);
            }
            else document.Items[index] = episode;
            result = episode;
            accepted = true;
            SaveWatermark(document, watermarkIndex, assessment, reasonEvidenceFingerprint);
        });
        return new(accepted, result);
    }

    private static void SaveWatermark(PrtgResourceFormalEpisodeDocument document, int index,
        PrtgResourcePeriodAssessment assessment, string reasonEvidenceFingerprint)
    {
        var watermark = new PrtgResourceFormalEvidenceWatermark(assessment.HostId, assessment.DeviceObjid,
            assessment.SensorObjid, assessment.SourceGeneration, assessment.ResourceGeneration,
            assessment.ChannelGeneration, assessment.ResourceEpoch, assessment.EvidenceAsOfUtc,
            assessment.AuthorityAsOfUtc, assessment.EvidenceFingerprint, reasonEvidenceFingerprint,
            assessment.CurrentRuleFingerprint, assessment.RuleAdmissionFingerprint);
        if (index >= 0) document.Watermarks[index] = watermark;
        else
        {
            if (document.Watermarks.Count >= MaximumEvidenceWatermarksPerHost)
                throw new InvalidDataException("Disk formal evidence watermark capacity reached; refusing to truncate.");
            document.Watermarks.Add(watermark);
        }
    }

    private static bool SameIdentity(PrtgResourceFormalEvidenceWatermark watermark,
        PrtgResourcePeriodAssessment assessment) => watermark.DeviceObjid == assessment.DeviceObjid &&
        watermark.SourceGeneration == assessment.SourceGeneration &&
        watermark.ResourceGeneration == assessment.ResourceGeneration &&
        watermark.ChannelGeneration == assessment.ChannelGeneration &&
        watermark.ResourceEpoch == assessment.ResourceEpoch;

    private static bool SameIdentity(PrtgResourceFormalEpisode episode,
        PrtgResourcePeriodAssessment assessment) => episode.DeviceObjid == assessment.DeviceObjid &&
        episode.SourceGeneration == assessment.SourceGeneration &&
        episode.ResourceGeneration == assessment.ResourceGeneration &&
        episode.ChannelGeneration == assessment.ChannelGeneration &&
        episode.ResourceEpoch == assessment.ResourceEpoch;

    private static string ReasonEvidenceFingerprint(IEnumerable<PrtgResourceFormalReasonObservation> observations)
    {
        var canonical = JsonSerializer.Serialize(observations.OrderBy(o => o.ReasonCode, StringComparer.Ordinal)
            .Select(o => new { o.ReasonCode, o.State, o.EvidenceFingerprint, o.EvidenceAsOfUtc,
                o.EvidenceDay, o.SourceRuleId, o.SourceRuleFingerprint }).ToArray());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsCurrentObservation(PrtgResourceFormalReasonObservation? observation,
        PrtgResourcePeriodAssessment assessment) => observation is not null &&
        observation.SensorObjid == assessment.SensorObjid &&
        observation.SourceGeneration == assessment.SourceGeneration &&
        observation.ResourceGeneration == assessment.ResourceGeneration &&
        observation.ChannelGeneration == assessment.ChannelGeneration &&
        observation.ResourceEpoch == assessment.ResourceEpoch &&
        ReasonCodes.Contains(observation.ReasonCode) && Enum.IsDefined(observation.State) &&
        observation.EvidenceFingerprint is { Length: 64 } && observation.EvidenceFingerprint.All(Uri.IsHexDigit) &&
        observation.EvidenceAsOfUtc.HasValue && observation.EvidenceAsOfUtc.Value.Kind == DateTimeKind.Utc &&
        observation.EvidenceAsOfUtc.Value <= assessment.EvidenceAsOfUtc &&
        (!observation.ExistingEpisodeStartedAtUtc.HasValue || observation.ExistingEpisodeStartedAtUtc.Value.Kind == DateTimeKind.Utc) &&
        (!observation.EvidenceDay.HasValue || observation.EvidenceDay.Value.Kind == DateTimeKind.Unspecified &&
            observation.EvidenceDay.Value.TimeOfDay == TimeSpan.Zero) &&
        (observation.State != PrtgResourceFormalReasonState.Active || observation.EvidenceDay.HasValue) &&
        (observation.ReasonCode != "disk-seven-day-low-water-trend" || observation.State != PrtgResourceFormalReasonState.Active ||
            observation.SourceRuleId is { Length: > 0 and <= 128 } && observation.SourceRuleFingerprint is { Length: 64 } &&
            observation.SourceRuleFingerprint.All(Uri.IsHexDigit)) &&
        observation.Summary is { Length: <= 256 } && observation.Summary.All(c => !char.IsControl(c) && c != '<' && c != '>');

    private static bool IsValid(PrtgResourceFormalEpisode? episode) => episode is not null &&
        episode.HostId > 0 && episode.DeviceObjid > 0 && episode.SensorObjid > 0 &&
        ValidText(episode.SourceGeneration, 128) && ValidText(episode.ResourceGeneration, 128) &&
        ValidText(episode.ChannelGeneration, 128) && ValidText(episode.ResourceEpoch, 64) &&
        episode.EpisodeObservedSinceUtc.Kind == DateTimeKind.Utc && episode.UpdatedAtUtc.Kind == DateTimeKind.Utc &&
        episode.EpisodeObservedSinceUtc <= episode.UpdatedAtUtc &&
        episode.Reasons is { Count: > 0 and <= 2 } && episode.Reasons.All(r =>
            ReasonCodes.Contains(r.ReasonCode) && r.EvidenceFingerprint is { Length: 64 } &&
            r.EvidenceFingerprint.All(Uri.IsHexDigit) && r.Summary is { Length: <= 256 } &&
            r.Summary.All(c => !char.IsControl(c) && c != '<' && c != '>') &&
            r.FirstObservedAtUtc.Kind == DateTimeKind.Utc &&
            r.FirstObservedAtUtc >= episode.EpisodeObservedSinceUtc &&
            r.FirstObservedAtUtc <= episode.UpdatedAtUtc && r.EvidenceDay.Kind == DateTimeKind.Unspecified &&
            r.EvidenceDay.TimeOfDay == TimeSpan.Zero &&
            (r.ReasonCode != "disk-seven-day-low-water-trend" || r.SourceRuleId is { Length: > 0 and <= 128 } &&
                r.SourceRuleFingerprint is { Length: 64 } && r.SourceRuleFingerprint.All(Uri.IsHexDigit))) &&
        episode.Reasons.Select(r => r.ReasonCode).Distinct(StringComparer.Ordinal).Count() == episode.Reasons.Count &&
        episode.ReasonSetFingerprint == Fingerprint(episode.Reasons);

    private static bool IsValid(PrtgResourceFormalEvidenceWatermark? watermark) => watermark is not null &&
        watermark.HostId > 0 && watermark.DeviceObjid > 0 && watermark.SensorObjid > 0 &&
        ValidText(watermark.SourceGeneration, 128) && ValidText(watermark.ResourceGeneration, 128) &&
        ValidText(watermark.ChannelGeneration, 128) && ValidText(watermark.ResourceEpoch, 64) &&
        watermark.EvidenceAsOfUtc.Kind == DateTimeKind.Utc && watermark.AuthorityAsOfUtc.Kind == DateTimeKind.Utc &&
        watermark.EvidenceAsOfUtc <= watermark.AuthorityAsOfUtc &&
        IsFingerprint(watermark.AssessmentEvidenceFingerprint) && IsFingerprint(watermark.ReasonEvidenceFingerprint) &&
        ValidOptionalFingerprint(watermark.CurrentRuleFingerprint) && ValidOptionalFingerprint(watermark.RuleAdmissionFingerprint);

    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidOptionalFingerprint(string? value) => string.IsNullOrEmpty(value) || IsFingerprint(value);

    private static bool ValidText(string? text, int max) => text is { Length: > 0 } && text.Length <= max;

    private static string Fingerprint(IEnumerable<PrtgResourceFormalReason> reasons)
    {
        var canonical = JsonSerializer.Serialize(reasons.OrderBy(r => r.ReasonCode, StringComparer.Ordinal)
            .Select(r => new { r.ReasonCode, r.EvidenceFingerprint, r.Summary, r.EvidenceDay,
                r.SourceRuleId, r.SourceRuleFingerprint }).ToArray());
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
