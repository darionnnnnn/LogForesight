using System.Text.Json;
using LogForesight.Core.Service;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

public enum PrtgModeReplayStatus { Pending, Running, Waiting, Completed, Overdue, Superseded }

/// <summary>Progress and paging cursors for durable mode replay; writes never change mode authority.</summary>
public sealed class PrtgResourcePressureModeReplayProgressStore(EfJsonBlobStore blob)
{
    public const int MaximumJobsPerHost = PrtgResourcePressureModeStore.MaximumReplayJobsPerHost;
    public const int MaximumCharacters = 128 * 1024;

    public static string BlobKey(long hostId) => hostId > 0
        ? $"prtg_resource_pressure_replay_progress_{hostId}"
        : throw new ArgumentOutOfRangeException(nameof(hostId));

    public PrtgResourcePressureModeReplayProgressDocument Get()
    {
        var read = blob.ReadBoundedWithVersion(MaximumCharacters);
        if (read.Version == 0 && read.Prefix is null) return new();
        if (read.ReportedLength > MaximumCharacters || read.Prefix is null ||
            read.Prefix.Length > MaximumCharacters)
            throw new InvalidDataException("PRTG mode replay progress exceeds its bounded read limit.");
        var document = JsonSerializer.Deserialize<PrtgResourcePressureModeReplayProgressDocument>(read.Prefix,
            LfJsonOptions.Pretty) ?? throw new JsonException("PRTG mode replay progress is null.");
        if (document.Items is null || document.Items.Count > MaximumJobsPerHost ||
            document.NextJobOffset is < 0 or > MaximumJobsPerHost ||
            document.Items.Any(item => item is null || item.HostId <= 0 || !Valid(item)))
            throw new InvalidDataException("PRTG mode replay progress is malformed or exceeds its bound.");
        return document;
    }

    public int GetNextJobOffset(int jobCount)
    {
        if (jobCount <= 0) return 0;
        return Get().NextJobOffset % jobCount;
    }

    public void SetNextJobOffset(int offset)
    {
        if (offset < 0 || offset > MaximumJobsPerHost) throw new ArgumentOutOfRangeException(nameof(offset));
        blob.Mutate(raw =>
        {
            var document = string.IsNullOrWhiteSpace(raw)
                ? new PrtgResourcePressureModeReplayProgressDocument()
                : JsonSerializer.Deserialize<PrtgResourcePressureModeReplayProgressDocument>(raw, LfJsonOptions.Pretty)
                    ?? throw new JsonException("PRTG mode replay progress is null.");
            if (document.Items is null || document.Items.Count > MaximumJobsPerHost)
                throw new InvalidDataException("PRTG mode replay progress is malformed or exceeds its bound.");
            document.NextJobOffset = offset;
            var content = JsonSerializer.Serialize(document, LfJsonOptions.Pretty);
            if (content.Length > MaximumCharacters)
                throw new InvalidDataException("PRTG mode replay progress exceeds its bounded write limit.");
            return (content, true);
        });
    }

    public PrtgResourcePressureModeReplayProgress? Find(PrtgResourcePressureModeReplayJob job) =>
        Get().Items.FirstOrDefault(item => SameIdentity(item, job));

    public void Update(PrtgResourcePressureModeReplayJob job,
        Func<PrtgResourcePressureModeReplayProgress, PrtgResourcePressureModeReplayProgress> change)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(change);
        blob.Mutate(raw =>
        {
            if (raw is { Length: > MaximumCharacters })
                throw new InvalidDataException("PRTG mode replay progress exceeds its bounded write limit.");
            var document = string.IsNullOrWhiteSpace(raw)
                ? new PrtgResourcePressureModeReplayProgressDocument()
                : JsonSerializer.Deserialize<PrtgResourcePressureModeReplayProgressDocument>(raw, LfJsonOptions.Pretty)
                    ?? throw new JsonException("PRTG mode replay progress is null.");
            if (document.Items is null || document.Items.Count > MaximumJobsPerHost ||
                document.Items.Any(item => item is null || item.HostId != job.HostId || !Valid(item)))
                throw new InvalidDataException("PRTG mode replay progress is malformed or exceeds its bound.");
            var index = document.Items.FindIndex(item => SameIdentity(item, job));
            var current = index >= 0 ? document.Items[index] : new PrtgResourcePressureModeReplayProgress(
                job.HostId, job.SensorObjid, job.Family, job.SourceGeneration, job.ResourceGeneration,
                job.Enabled, job.TransitionId, PrtgModeReplayStatus.Pending, job.RequestedAtUtc,
                job.DeadlineArmed ? job.DueAtUtc : null, 0, null, null, null);
            if (current.TransitionId != job.TransitionId)
                current = current with
                {
                    TransitionId = job.TransitionId,
                    Status = PrtgModeReplayStatus.Pending,
                    RequestedAtUtc = job.RequestedAtUtc,
                    DueAtUtc = job.DeadlineArmed ? job.DueAtUtc : null,
                    EvidenceReadyAtUtc = null,
                    AfterRecordId = 0,
                    UnresolvedHostDays = 0,
                    LastReason = null,
                    StartedAtUtc = null,
                    CompletedAtUtc = null
                };
            current = change(current) ?? throw new InvalidOperationException("Mode replay progress update returned null.");
            if (index >= 0) document.Items[index] = current;
            else
            {
                if (document.Items.Count >= MaximumJobsPerHost)
                    throw new InvalidDataException("PRTG mode replay progress capacity reached; refusing to truncate.");
                document.Items.Add(current);
            }
            var content = JsonSerializer.Serialize(document, LfJsonOptions.Pretty);
            if (content.Length > MaximumCharacters)
                throw new InvalidDataException("PRTG mode replay progress exceeds its bounded write limit.");
            return (content, true);
        });
    }

    private static bool SameIdentity(PrtgResourcePressureModeReplayProgress item,
        PrtgResourcePressureModeReplayJob job) => item.HostId == job.HostId &&
        item.SensorObjid == job.SensorObjid && item.Family == job.Family &&
        item.SourceGeneration == job.SourceGeneration && item.ResourceGeneration == job.ResourceGeneration &&
        item.Enabled == job.Enabled;

    private static bool Valid(PrtgResourcePressureModeReplayProgress item) => item.HostId > 0 &&
        item.SensorObjid > 0 && item.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        (item.SourceGeneration is { Length: 0 } && item.ResourceGeneration is { Length: 0 } ||
         item.SourceGeneration is { Length: > 0 and <= 128 } && item.ResourceGeneration is { Length: > 0 and <= 128 }) &&
        Enum.IsDefined(item.Status) && Guid.TryParseExact(item.TransitionId, "N", out _) &&
        item.RequestedAtUtc.Kind == DateTimeKind.Utc && item.RequestedAtUtc != default &&
        (item.DueAtUtc is null || item.DueAtUtc.Value.Kind == DateTimeKind.Utc &&
            item.DueAtUtc.Value >= (item.EvidenceReadyAtUtc ?? item.RequestedAtUtc) &&
            item.DueAtUtc.Value <= (item.EvidenceReadyAtUtc ?? item.RequestedAtUtc).AddMinutes(15)) &&
        (item.EvidenceReadyAtUtc is null || item.EvidenceReadyAtUtc.Value.Kind == DateTimeKind.Utc &&
            item.EvidenceReadyAtUtc.Value >= item.RequestedAtUtc) &&
        item.AfterRecordId >= 0 && item.UnresolvedHostDays is >= 0 and <= 100_000 &&
        (item.LastReason?.Length ?? 0) <= 256;
}

public sealed class PrtgResourcePressureModeReplayProgressDocument
{
    public List<PrtgResourcePressureModeReplayProgress> Items { get; set; } = [];
    public int NextJobOffset { get; set; }
}

/// <summary>Durable global host-keyset cursor, separate from all mode-authority blobs.</summary>
public sealed class PrtgResourcePressureModeReplayHostCursorStore(EfJsonBlobStore blob)
{
    private const int MaximumCharacters = 512;

    public string? Get()
    {
        var read = blob.ReadBoundedWithVersion(MaximumCharacters);
        if (read.Version == 0 && read.Prefix is null) return null;
        if (read.Prefix is null || read.ReportedLength > MaximumCharacters || read.Prefix.Length > MaximumCharacters)
            throw new InvalidDataException("PRTG replay host cursor exceeds its bounded read limit.");
        var document = JsonSerializer.Deserialize<PrtgResourcePressureModeReplayHostCursor>(read.Prefix,
            LfJsonOptions.Pretty) ?? throw new JsonException("PRTG replay host cursor is null.");
        if (document.AfterKey?.Length > 256) throw new InvalidDataException("PRTG replay host cursor is malformed.");
        return document.AfterKey;
    }

    public void Set(string? afterKey)
    {
        if (afterKey?.Length > 256) throw new ArgumentOutOfRangeException(nameof(afterKey));
        blob.Mutate(_ => (JsonSerializer.Serialize(new PrtgResourcePressureModeReplayHostCursor(afterKey),
            LfJsonOptions.Pretty), true));
    }
}

public sealed record PrtgResourcePressureModeReplayHostCursor(string? AfterKey);

public sealed record PrtgResourcePressureModeReplayProgress(long HostId, long SensorObjid,
    PrtgResourceFamily Family, string SourceGeneration, string ResourceGeneration, bool Enabled,
    string TransitionId, PrtgModeReplayStatus Status, DateTime RequestedAtUtc, DateTime? DueAtUtc,
    long AfterRecordId, string? LastReason, DateTime? StartedAtUtc, DateTime? CompletedAtUtc,
    int UnresolvedHostDays = 0, DateTime? EvidenceReadyAtUtc = null);
