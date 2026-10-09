using System.Text.Json;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>Persists per-resource formal-mode activation bound to a server-issued trial.</summary>
public sealed class PrtgResourcePressureModeStore(EfJsonBlobStore blob)
    : JsonBlobSingleton<PrtgResourcePressureModeDocument>(blob)
{
    public const int MaximumResourcesPerHost = 128;
    public const int MaximumRevocationsPerHost = MaximumResourcesPerHost * 2;
    public const int MaximumReplayJobsPerHost = MaximumResourcesPerHost * 2;
    public const int MaximumDocumentCharacters = 256 * 1024;
    public static string BlobKey(long hostId) => hostId > 0
        ? $"prtg_resource_pressure_modes_{hostId}"
        : throw new ArgumentOutOfRangeException(nameof(hostId));

    public PrtgResourcePressureModeGrant? Find(long hostId, long sensorObjid, PrtgResourceFamily family)
    {
        if (hostId <= 0 || sensorObjid <= 0 || !Enum.IsDefined(family)) return null;
        return Get().Grants?.FirstOrDefault(g => IsValid(g) && g.HostId == hostId &&
            g.SensorObjid == sensorObjid && g.Family == family);
    }

    /// <summary>Reads the document, exact grant and blob revision from one database read.</summary>
    public PrtgResourcePressureModeSnapshot ReadSnapshot(long hostId, long sensorObjid, PrtgResourceFamily family)
    {
        if (hostId <= 0 || sensorObjid <= 0 || !Enum.IsDefined(family))
            return new(null, null, 0);
        var snapshot = ReadHostSnapshot(hostId);
        var grant = snapshot.Grants.FirstOrDefault(g => IsValid(g) && g.SensorObjid == sensorObjid && g.Family == family);
        var revocation = snapshot.Revocations.Where(item => item.SensorObjid == sensorObjid && item.Family == family)
            .OrderByDescending(item => item.DisabledAtUtc).FirstOrDefault();
        return new(grant, revocation, snapshot.BlobVersion);
    }

    /// <summary>One bounded atomic read of all mode grants and off signals for an in-scope host.</summary>
    public PrtgResourcePressureModeHostSnapshot ReadHostSnapshot(long hostId)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        var read = blob.ReadBoundedWithVersion(MaximumDocumentCharacters);
        if (read.Version == 0 && read.Prefix is null)
            return new(hostId, [], [], 0);
        if (read.ReportedLength > MaximumDocumentCharacters || read.Prefix is null ||
            read.Prefix.Length > MaximumDocumentCharacters)
            throw new InvalidDataException("PRTG resource mode document exceeds its bounded read limit.");
        if (string.IsNullOrWhiteSpace(read.Prefix))
            throw new JsonException("Existing PRTG resource mode document is empty.");
        var document = JsonSerializer.Deserialize<PrtgResourcePressureModeDocument>(read.Prefix, LfJsonOptions.Pretty)
            ?? throw new JsonException("PRTG mode document is null.");
        if (document.Grants is null || document.Grants.Count > MaximumResourcesPerHost ||
            document.Grants.Any(grant => grant is null || grant.HostId != hostId || !IsValid(grant)) ||
            document.Revocations is null || document.Revocations.Count > MaximumRevocationsPerHost ||
            document.Revocations.Any(item => !IsValidRevocation(item) || item!.HostId != hostId))
            throw new InvalidDataException("PRTG resource mode document is malformed or exceeds its bound.");
        // Keep the off event in storage as audit history, but a later exact enabled grant
        // supersedes its withdrawal authority for that same source/resource generation.
        // A different generation's marker remains actionable and can withdraw only that generation.
        var currentRevocations = document.Revocations.Where(revocation => !document.Grants.Any(grant =>
            grant.FormalEnabled && grant.HostId == revocation.HostId &&
            grant.SensorObjid == revocation.SensorObjid && grant.Family == revocation.Family &&
            grant.SourceGeneration == revocation.SourceGeneration &&
            grant.ResourceGeneration == revocation.ResourceGeneration &&
            grant.UpdatedAtUtc > revocation.DisabledAtUtc)).ToArray();
        if (document.ReplayJobs is null || document.ReplayJobs.Count > MaximumReplayJobsPerHost ||
            document.ReplayJobs.Any(job => !IsValidReplayJob(job) || job!.HostId != hostId))
            throw new InvalidDataException("PRTG mode replay queue is malformed or exceeds its bound.");
        return new(hostId, document.Grants.ToArray(), currentRevocations, read.Version)
        {
            ReplayJobs = document.ReplayJobs.ToArray()
        };
    }

    /// <summary>Keyset-pages hosts with persisted mode documents; replay discovery does not depend on PRTG policy.</summary>
    public static PrtgResourcePressureModeHostPage ReadReplayHostsPage(StorageBackend backend,
        string? afterKey, int limit)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        const string prefix = "prtg_resource_pressure_modes_";
        using var ctx = backend.CreateContext();
        var keys = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(prefix) &&
                (afterKey == null || string.Compare(row.BlobKey, afterKey) > 0))
            .OrderBy(row => row.BlobKey).Select(row => row.BlobKey).Take(limit).ToArray();
        var hosts = new List<PrtgResourcePressureModeHostSnapshot>(keys.Length);
        foreach (var key in keys)
        {
            if (!long.TryParse(key.AsSpan(prefix.Length), out var hostId) || hostId <= 0) continue;
            hosts.Add(new PrtgResourcePressureModeStore(backend.Blob(key)).ReadHostSnapshot(hostId));
        }
        return new(hosts, keys.LastOrDefault(), keys.Length);
    }

    internal bool UpsertTrial(PrtgResourcePressureModeGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!IsValid(grant)) throw new ArgumentException("PRTG resource mode grant is invalid.", nameof(grant));
        var saved = false;
        Update(document =>
        {
            document.Grants ??= [];
            if (document.Grants.Count > MaximumResourcesPerHost)
                throw new InvalidDataException("PRTG resource mode store exceeds its bound.");
            var index = document.Grants.FindIndex(g => g.HostId == grant.HostId &&
                g.SensorObjid == grant.SensorObjid && g.Family == grant.Family);
            if (index < 0)
            {
                if (document.Grants.Count >= MaximumResourcesPerHost)
                    throw new InvalidDataException("PRTG resource mode capacity reached; refusing to truncate.");
                document.Grants.Add(grant);
                saved = true;
                return;
            }
            var prior = document.Grants[index];
            if (grant.UpdatedAtUtc < prior.UpdatedAtUtc) return;
            var sameContract = SameContract(prior, grant);
            document.Grants[index] = grant with
            {
                FormalEnabled = sameContract && prior.FormalEnabled,
                TrialConsumed = false
            };
            saved = true;
        });
        return saved;
    }

    /// <summary>Atomically consumes one unexpired trial token and activates only its matching contract.</summary>
    internal bool TryConsumeTrial(PrtgResourcePressureModeGrant expected, string trialResultHash, DateTime nowUtc)
    {
        return MutateModeDocument((ctx, document) =>
        {
            var grant = document.Grants?.FirstOrDefault(g => IsValid(g) && g.HostId == expected.HostId &&
                g.SensorObjid == expected.SensorObjid && g.Family == expected.Family);
            if (grant is null || !grant.MaintainAuthorized || grant.TrialConsumed ||
                grant.TrialExpiresAtUtc <= nowUtc || grant.TrialResultHash != trialResultHash ||
                !SameContract(grant, expected)) return false;
            var index = document.Grants!.IndexOf(grant);
            document.Grants[index] = grant with
            {
                FormalEnabled = true,
                TrialConsumed = true,
                UpdatedAtUtc = nowUtc,
                ModeTransitionId = Guid.NewGuid().ToString("N")
            };
            EnqueueReplay(ctx, document, document.Grants[index], enabled: true, nowUtc);
            return true;
        });
    }

    /// <summary>Rolls back only the exact activation write; a newer trial/grant is never overwritten.</summary>
    internal void DisableIfActivationMatches(long hostId, long sensorObjid, PrtgResourceFamily family,
        string trialResultHash, DateTime activationTimeUtc)
    {
        MutateModeDocument((ctx, document) =>
        {
            var grant = document.Grants?.FirstOrDefault(g => g.HostId == hostId && g.SensorObjid == sensorObjid &&
                g.Family == family && g.TrialResultHash == trialResultHash && g.UpdatedAtUtc == activationTimeUtc &&
                g.FormalEnabled);
            if (grant is null) return false;
            var index = document.Grants!.IndexOf(grant);
            var nowUtc = DateTime.UtcNow;
            var disabled = grant with
            {
                FormalEnabled = false,
                UpdatedAtUtc = nowUtc,
                ModeTransitionId = Guid.NewGuid().ToString("N")
            };
            document.Grants[index] = disabled;
            document.Revocations ??= [];
            var markerIndex = document.Revocations.FindLastIndex(item => item.HostId == hostId &&
                item.SensorObjid == sensorObjid && item.Family == family &&
                item.SourceGeneration == grant.SourceGeneration && item.ResourceGeneration == grant.ResourceGeneration);
            if (markerIndex >= 0)
                document.Revocations[markerIndex] = document.Revocations[markerIndex] with
                { DisabledAtUtc = nowUtc, ModeTransitionId = disabled.ModeTransitionId };
            else
            {
                if (document.Revocations.Count >= MaximumRevocationsPerHost)
                    throw new InvalidDataException("PRTG resource revocation capacity reached; refusing to truncate.");
                document.Revocations.Add(new(hostId, sensorObjid, family, grant.SourceGeneration,
                    grant.ResourceGeneration, nowUtc, disabled.ModeTransitionId));
            }
            EnqueueReplay(ctx, document, disabled, enabled: false, nowUtc);
            return true;
        });
    }

    internal bool DisableFormalMode(long hostId, long sensorObjid, DateTime nowUtc)
    {
        if (hostId <= 0 || sensorObjid <= 0 || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("PRTG mode identity or timestamp is invalid.");
        return blob.MutateWithContext((ctx, raw) =>
        {
            if (raw is { Length: > MaximumDocumentCharacters })
                throw new InvalidDataException("PRTG resource mode document exceeds its bounded write limit.");
            var document = string.IsNullOrWhiteSpace(raw)
                ? new PrtgResourcePressureModeDocument()
                : JsonSerializer.Deserialize<PrtgResourcePressureModeDocument>(raw, LfJsonOptions.Pretty)
                    ?? throw new JsonException("PRTG mode document is null.");
            document.ReplayJobs ??= [];
            if (document.Grants is null || document.Revocations is null)
                throw new InvalidDataException("PRTG resource mode document has null grant or revocation data.");
            if (document.Grants.Count > MaximumResourcesPerHost ||
                document.Revocations.Count > MaximumRevocationsPerHost ||
                document.ReplayJobs is null || document.ReplayJobs.Count > MaximumReplayJobsPerHost ||
                document.Grants.Any(grant => grant is null || grant.HostId != hostId || !IsValid(grant)) ||
                document.Revocations.Any(item => !IsValidRevocation(item) || item!.HostId != hostId) ||
                document.ReplayJobs.Any(item => !IsValidReplayJob(item) || item!.HostId != hostId))
                throw new InvalidDataException("PRTG resource mode store exceeds its bound.");
            var signalPresent = false;
            foreach (var candidate in document.Grants.Where(g => g.HostId == hostId && g.SensorObjid == sensorObjid &&
                         g.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory).ToArray())
            {
                var grant = candidate;
                var hasMarker = document.Revocations.Any(item => item.HostId == hostId &&
                    item.SensorObjid == sensorObjid && item.Family == grant.Family &&
                    item.SourceGeneration == grant.SourceGeneration && item.ResourceGeneration == grant.ResourceGeneration);
                if (!grant.FormalEnabled && hasMarker)
                {
                    if (string.IsNullOrWhiteSpace(grant.ModeTransitionId))
                    {
                        grant = grant with { ModeTransitionId = Guid.NewGuid().ToString("N"), UpdatedAtUtc = nowUtc };
                        var currentGrantIndex = document.Grants.FindIndex(g => g.HostId == hostId &&
                            g.SensorObjid == sensorObjid && g.Family == grant.Family);
                        if (currentGrantIndex >= 0) document.Grants[currentGrantIndex] = grant;
                        var priorMarker = document.Revocations.FindLastIndex(item => item.HostId == hostId &&
                            item.SensorObjid == sensorObjid && item.Family == grant.Family &&
                            item.SourceGeneration == grant.SourceGeneration && item.ResourceGeneration == grant.ResourceGeneration);
                        if (priorMarker >= 0)
                            document.Revocations[priorMarker] = document.Revocations[priorMarker] with
                            { ModeTransitionId = grant.ModeTransitionId };
                    }
                    EnqueueReplay(ctx, document, grant, enabled: false, nowUtc);
                    signalPresent = true;
                    continue;
                }
                if (grant.FormalEnabled)
                {
                    var index = document.Grants.IndexOf(grant);
                    grant = grant with
                    {
                        FormalEnabled = false,
                        UpdatedAtUtc = nowUtc,
                        ModeTransitionId = Guid.NewGuid().ToString("N")
                    };
                    document.Grants[index] = grant;
                    // Re-enabling the same generation makes its former off event historical.
                    // Refresh that event when it is turned off again so the new off transition
                    // regains exact withdrawal authority and repeated off calls stay idempotent.
                    for (var markerIndex = 0; markerIndex < document.Revocations.Count; markerIndex++)
                    {
                        var marker = document.Revocations[markerIndex];
                        if (marker.HostId == hostId && marker.SensorObjid == sensorObjid &&
                            marker.Family == grant.Family && marker.SourceGeneration == grant.SourceGeneration &&
                            marker.ResourceGeneration == grant.ResourceGeneration)
                            document.Revocations[markerIndex] = marker with { DisabledAtUtc = nowUtc };
                    }
                }
                if (!hasMarker)
                {
                    if (document.Revocations.Count >= MaximumRevocationsPerHost)
                        throw new InvalidDataException("PRTG resource revocation capacity reached; refusing to truncate.");
                    document.Revocations.Add(new(hostId, sensorObjid, grant.Family, grant.SourceGeneration,
                        grant.ResourceGeneration, nowUtc, grant.ModeTransitionId));
                }
                else if (!grant.FormalEnabled)
                {
                    var markerIndex = document.Revocations.FindLastIndex(item => item.HostId == hostId &&
                        item.SensorObjid == sensorObjid && item.Family == grant.Family &&
                        item.SourceGeneration == grant.SourceGeneration && item.ResourceGeneration == grant.ResourceGeneration);
                    if (markerIndex >= 0)
                        document.Revocations[markerIndex] = document.Revocations[markerIndex] with
                        { DisabledAtUtc = nowUtc, ModeTransitionId = grant.ModeTransitionId };
                }
                EnqueueReplay(ctx, document, grant, enabled: false, nowUtc);
                signalPresent = true;
            }
            if (!document.Grants.Any(g => g.HostId == hostId && g.SensorObjid == sensorObjid &&
                    g.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory))
            {
                foreach (var family in new[] { PrtgResourceFamily.Cpu, PrtgResourceFamily.Memory })
                {
                    var markerIndex = document.Revocations.FindLastIndex(item => item.HostId == hostId &&
                        item.SensorObjid == sensorObjid && item.Family == family);
                    if (markerIndex >= 0)
                    {
                        var marker = document.Revocations[markerIndex];
                        var transitionId = string.IsNullOrWhiteSpace(marker.ModeTransitionId)
                            ? Guid.NewGuid().ToString("N") : marker.ModeTransitionId;
                        if (transitionId != marker.ModeTransitionId)
                        {
                            marker = marker with { ModeTransitionId = transitionId };
                            document.Revocations[markerIndex] = marker;
                        }
                        EnqueueReplay(ctx, document, hostId, sensorObjid, family, marker.SourceGeneration,
                            marker.ResourceGeneration, enabled: false, transitionId, nowUtc);
                        signalPresent = true;
                        continue;
                    }
                    if (document.Revocations.Count >= MaximumRevocationsPerHost)
                        throw new InvalidDataException("PRTG resource revocation capacity reached; refusing to truncate.");
                    var newTransitionId = Guid.NewGuid().ToString("N");
                    document.Revocations.Add(new(hostId, sensorObjid, family, string.Empty, string.Empty,
                        nowUtc, newTransitionId));
                    EnqueueReplay(ctx, document, hostId, sensorObjid, family, string.Empty, string.Empty,
                        enabled: false, newTransitionId, nowUtc);
                    signalPresent = true;
                }
            }
            var serialized = JsonSerializer.Serialize(document, LfJsonOptions.Pretty);
            if (serialized.Length > MaximumDocumentCharacters)
                throw new InvalidDataException("PRTG resource mode document exceeds its bounded write limit.");
            return (serialized, signalPresent);
        }, skipUnchangedContent: true);
    }

    internal void RemoveTrial(long hostId, long sensorObjid, PrtgResourceFamily family, string trialResultHash)
    {
        Update(document =>
        {
            document.Grants ??= [];
            document.Grants.RemoveAll(grant => grant.HostId == hostId && grant.SensorObjid == sensorObjid &&
                grant.Family == family && grant.TrialResultHash == trialResultHash && !grant.FormalEnabled);
        });
    }

    private static bool IsValid(PrtgResourcePressureModeGrant? grant) => grant is not null &&
        grant.HostId > 0 && grant.SensorObjid > 0 &&
        grant.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        grant.UpdatedAtUtc.Kind == DateTimeKind.Utc && grant.UpdatedAtUtc != default &&
        grant.TrialExpiresAtUtc.Kind == DateTimeKind.Utc && grant.TrialExpiresAtUtc != default &&
        grant.ProfileFingerprint is { Length: 64 } && grant.ProfileFingerprint.All(Uri.IsHexDigit) &&
        grant.RulesVersion == PrtgResourcePressureEvaluator.RulesVersion &&
        grant.SourceGeneration is { Length: > 0 and <= 128 } &&
        grant.ResourceGeneration is { Length: > 0 and <= 128 } &&
        grant.ChannelGeneration is { Length: > 0 and <= 128 } &&
        grant.ResourceEpoch is { Length: > 0 and <= 64 } &&
        grant.SemanticVersion is { Length: > 0 and <= 128 } &&
        grant.StrategyVersion is { Length: > 0 and <= 128 } &&
        grant.RuleId is { Length: > 0 and <= 128 } &&
        grant.RuleFingerprint is { Length: 64 } && grant.RuleFingerprint.All(Uri.IsHexDigit) &&
        grant.TrialResultHash is { Length: 64 } && grant.TrialResultHash.All(Uri.IsHexDigit);

    private static bool SameContract(PrtgResourcePressureModeGrant left, PrtgResourcePressureModeGrant right) =>
        left.HostId == right.HostId && left.SensorObjid == right.SensorObjid && left.Family == right.Family &&
        left.ProfileFingerprint == right.ProfileFingerprint && left.RulesVersion == right.RulesVersion &&
        left.SourceGeneration == right.SourceGeneration && left.ResourceGeneration == right.ResourceGeneration &&
        left.ChannelGeneration == right.ChannelGeneration && left.ResourceEpoch == right.ResourceEpoch &&
        left.SemanticVersion == right.SemanticVersion && left.StrategyVersion == right.StrategyVersion &&
        left.RuleId == right.RuleId && left.RuleFingerprint == right.RuleFingerprint;

    private static bool IsValidRevocation(PrtgResourcePressureModeRevocation? item) =>
        item is not null && item.HostId > 0 && item.SensorObjid > 0 &&
        item.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        item.DisabledAtUtc.Kind == DateTimeKind.Utc && item.DisabledAtUtc != default &&
        (item.SourceGeneration is { Length: 0 } && item.ResourceGeneration is { Length: 0 } ||
         item.SourceGeneration is { Length: > 0 and <= 128 } &&
         item.ResourceGeneration is { Length: > 0 and <= 128 });

    private static void EnqueueReplay(LfDbContext ctx, PrtgResourcePressureModeDocument document,
        PrtgResourcePressureModeGrant grant, bool enabled, DateTime nowUtc) =>
        EnqueueReplay(ctx, document, grant.HostId, grant.SensorObjid, grant.Family, grant.SourceGeneration,
            grant.ResourceGeneration, enabled, grant.ModeTransitionId, nowUtc);

    private static void EnqueueReplay(LfDbContext ctx, PrtgResourcePressureModeDocument document, long hostId,
        long sensorObjid, PrtgResourceFamily family, string sourceGeneration, string resourceGeneration,
        bool enabled, string transitionId, DateTime nowUtc)
    {
        document.ReplayJobs ??= [];
        var index = document.ReplayJobs.FindIndex(job => job.HostId == hostId && job.SensorObjid == sensorObjid &&
            job.Family == family && job.SourceGeneration == sourceGeneration &&
            job.ResourceGeneration == resourceGeneration && job.Enabled == enabled);
        if (index >= 0)
        {
            var prior = document.ReplayJobs[index];
            var terminal = transitionId != prior.TransitionId && IsTerminalTransition(ctx, prior);
            var resetDeadline = terminal;
            // Pending work keeps its original absolute deadline. A completed/superseded transition
            // starts a fresh deadline for a later toggle of the same generation.
            document.ReplayJobs[index] = prior with
            {
                TransitionId = transitionId,
                RequestedAtUtc = resetDeadline ? nowUtc : prior.RequestedAtUtc,
                DueAtUtc = resetDeadline ? (enabled ? nowUtc : nowUtc.AddMinutes(15)) : prior.DueAtUtc,
                DeadlineArmed = resetDeadline ? !enabled : prior.DeadlineArmed
            };
            return;
        }
        if (document.ReplayJobs.Count >= MaximumReplayJobsPerHost)
            throw new InvalidDataException("PRTG mode replay queue capacity reached; refusing to truncate.");
        document.ReplayJobs.Add(new(hostId, sensorObjid, family, sourceGeneration, resourceGeneration,
            enabled, transitionId, nowUtc, enabled ? nowUtc : nowUtc.AddMinutes(15), DeadlineArmed: !enabled));
    }

    private static bool IsTerminalTransition(LfDbContext ctx, PrtgResourcePressureModeReplayJob job)
    {
        var key = PrtgResourcePressureModeReplayProgressStore.BlobKey(job.HostId);
        var row = ctx.Blobs.AsNoTracking().Where(blob => blob.BlobKey == key)
            .Select(blob => new { Prefix = blob.Content.Substring(0,
                PrtgResourcePressureModeReplayProgressStore.MaximumCharacters + 1),
                ContentLength = blob.Content.Length })
            .FirstOrDefault();
        if (row is null) return false;
        if (row.ContentLength > PrtgResourcePressureModeReplayProgressStore.MaximumCharacters)
            throw new InvalidDataException("PRTG mode replay progress exceeds its bounded transaction read limit.");
        var document = JsonSerializer.Deserialize<PrtgResourcePressureModeReplayProgressDocument>(row.Prefix,
            LfJsonOptions.Pretty) ?? throw new JsonException("PRTG mode replay progress is null.");
        if (document.Items is null || document.Items.Count > PrtgResourcePressureModeReplayProgressStore.MaximumJobsPerHost ||
            document.Items.Any(item => item is null || item.HostId != job.HostId))
            throw new InvalidDataException("PRTG mode replay progress is malformed or exceeds its bound.");
        var progress = document.Items.FirstOrDefault(item => item.HostId == job.HostId &&
            item.SensorObjid == job.SensorObjid && item.Family == job.Family &&
            item.SourceGeneration == job.SourceGeneration && item.ResourceGeneration == job.ResourceGeneration &&
            item.Enabled == job.Enabled);
        return progress?.TransitionId == job.TransitionId &&
            progress.Status is PrtgModeReplayStatus.Completed or PrtgModeReplayStatus.Superseded;
    }

    private bool MutateModeDocument(Func<LfDbContext, PrtgResourcePressureModeDocument, bool> mutate)
    {
        return blob.MutateWithContext((ctx, raw) =>
        {
            if (raw is { Length: > MaximumDocumentCharacters })
                throw new InvalidDataException("PRTG resource mode document exceeds its bounded write limit.");
            var document = string.IsNullOrWhiteSpace(raw)
                ? new PrtgResourcePressureModeDocument()
                : JsonSerializer.Deserialize<PrtgResourcePressureModeDocument>(raw, LfJsonOptions.Pretty)
                    ?? throw new JsonException("PRTG mode document is null.");
            document.Grants ??= [];
            document.Revocations ??= [];
            document.ReplayJobs ??= [];
            if (document.Grants.Count > MaximumResourcesPerHost || document.Revocations.Count > MaximumRevocationsPerHost ||
                document.ReplayJobs.Count > MaximumReplayJobsPerHost)
                throw new InvalidDataException("PRTG resource mode store exceeds its bound.");
            var changed = mutate(ctx, document);
            var content = JsonSerializer.Serialize(document, LfJsonOptions.Pretty);
            if (content.Length > MaximumDocumentCharacters)
                throw new InvalidDataException("PRTG resource mode document exceeds its bounded write limit.");
            return (content, changed);
        }, maxCurrentCharacters: MaximumDocumentCharacters, skipUnchangedContent: true);
    }

    private static bool IsValidReplayJob(PrtgResourcePressureModeReplayJob? job) =>
        job is not null && job.HostId > 0 && job.SensorObjid > 0 &&
        job.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        (job.SourceGeneration.Length == 0 && job.ResourceGeneration.Length == 0 ||
         job.SourceGeneration.Length is > 0 and <= 128 && job.ResourceGeneration.Length is > 0 and <= 128) &&
        Guid.TryParseExact(job.TransitionId, "N", out _) && job.RequestedAtUtc.Kind == DateTimeKind.Utc &&
        job.RequestedAtUtc != default && job.DueAtUtc.Kind == DateTimeKind.Utc &&
        job.DueAtUtc >= job.RequestedAtUtc && job.DueAtUtc <= job.RequestedAtUtc.AddMinutes(15) &&
        (job.DeadlineArmed || job.Enabled && job.DueAtUtc == job.RequestedAtUtc);
}

public sealed class PrtgResourcePressureModeDocument
{
    public List<PrtgResourcePressureModeGrant> Grants { get; set; } = [];
    public List<PrtgResourcePressureModeRevocation> Revocations { get; set; } = [];
    public List<PrtgResourcePressureModeReplayJob> ReplayJobs { get; set; } = [];
}

public sealed record PrtgResourcePressureModeSnapshot(PrtgResourcePressureModeGrant? Grant,
    PrtgResourcePressureModeRevocation? Revocation, long BlobVersion);

public sealed record PrtgResourcePressureModeHostSnapshot(long HostId,
    IReadOnlyList<PrtgResourcePressureModeGrant> Grants,
    IReadOnlyList<PrtgResourcePressureModeRevocation> Revocations, long BlobVersion)
{
    public IReadOnlyList<PrtgResourcePressureModeReplayJob> ReplayJobs { get; init; } = [];
}

public sealed record PrtgResourcePressureModeHostPage(
    IReadOnlyList<PrtgResourcePressureModeHostSnapshot> Hosts, string? NextCursor, int RawCount);

public sealed record PrtgResourcePressureModeRevocation(long HostId, long SensorObjid,
    PrtgResourceFamily Family, string SourceGeneration, string ResourceGeneration, DateTime DisabledAtUtc,
    string ModeTransitionId = "");

public sealed record PrtgResourcePressureModeReplayJob(long HostId, long SensorObjid,
    PrtgResourceFamily Family, string SourceGeneration, string ResourceGeneration, bool Enabled,
    string TransitionId, DateTime RequestedAtUtc, DateTime DueAtUtc, bool DeadlineArmed = true);

public sealed record PrtgResourcePressureModeGrant(
    long HostId,
    long SensorObjid,
    PrtgResourceFamily Family,
    string ProfileFingerprint,
    string RulesVersion,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    string SemanticVersion,
    string StrategyVersion,
    string RuleId,
    string RuleFingerprint,
    string TrialResultHash,
    bool MaintainAuthorized,
    bool FormalEnabled,
    DateTime UpdatedAtUtc,
    DateTime TrialExpiresAtUtc,
    bool TrialConsumed = false,
    string ModeTransitionId = "");

/// <summary>Opaque one-use-to-activate token response; the store keeps only the hash.</summary>
public sealed record PrtgResourceTrialGrant(
    string TrialResultId,
    long HostId,
    long SensorObjid,
    PrtgResourceFamily Family,
    string ExpiresAtUtc,
    string ProfileFingerprint,
    string RulesVersion);
