using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

public enum PrtgAuthorityRepairDocumentKind
{
    ResourcePressureMode,
    DiskSemanticEvidence,
    DiskVerificationResults
}

public enum PrtgAuthorityRepairState
{
    Missing,
    WithinLimit,
    OverFormalLimit,
    RepairBudgetExceeded,
    InvalidJson,
    InvalidContent
}

public sealed record PrtgAuthorityRepairSnapshot(
    PrtgAuthorityRepairDocumentKind Kind,
    long? HostId,
    long Version,
    int FormalLimitCharacters,
    int RepairBudgetCharacters,
    PrtgAuthorityRepairState State,
    int? ContentCharacters,
    string? Content);

public sealed record PrtgAuthorityJsonRemoval(string Pointer);
public sealed record PrtgAuthorityTextPatch(int Start, int DeleteLength, string Replacement,
    string ExpectedDeletedTextSha256);
public sealed record PrtgAuthorityRepairResult(long PreviousVersion, long NewVersion,
    int PreviousCharacters, int NewCharacters, int EditCount, string ContentSha256);

/// <summary>
/// Explicit, bounded maintenance repair for the existing PRTG authority blobs. Normal readers
/// keep their smaller formal limits. This store never returns a partial document as trusted data.
/// </summary>
public sealed class PrtgAuthorityRepairStore
{
    public const int MaximumEdits = 16;
    public const int MaximumEditCharacters = 4096;
    private readonly StorageBackend _backend;
    private readonly int? _testFormalLimit;
    private readonly int? _testRepairBudget;

    public PrtgAuthorityRepairStore(StorageBackend backend) : this(backend, null) { }

    internal PrtgAuthorityRepairStore(StorageBackend backend, int? testFormalLimit, int? testRepairBudget = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (testFormalLimit is < 1) throw new ArgumentOutOfRangeException(nameof(testFormalLimit));
        if (testRepairBudget is < 1) throw new ArgumentOutOfRangeException(nameof(testRepairBudget));
        _testFormalLimit = testFormalLimit;
        _testRepairBudget = testRepairBudget;
    }

    public PrtgAuthorityRepairSnapshot Inspect(PrtgAuthorityRepairDocumentKind kind, long? hostId = null)
    {
        var target = Resolve(kind, hostId, _testFormalLimit);
        var budget = RepairBudget(target.FormalLimit);
        using var ctx = _backend.CreateContext();
        var read = EfJsonBlobStore.ReadBoundedInContext(ctx, target.Key, budget);
        if (read.Version == 0 && read.Prefix is null)
            return new(kind, hostId, 0, target.FormalLimit, budget, PrtgAuthorityRepairState.Missing, 0, null);
        if (read.ReportedLength > budget || read.Prefix is null)
            return new(kind, hostId, read.Version, target.FormalLimit, budget,
                PrtgAuthorityRepairState.RepairBudgetExceeded, null, null);

        var state = Classify(kind, hostId, read.Prefix, target.FormalLimit);
        return new(kind, hostId, read.Version, target.FormalLimit, budget, state, read.Prefix.Length, read.Prefix);
    }

    public PrtgAuthorityRepairResult RemoveJsonPaths(PrtgAuthorityRepairDocumentKind kind, long expectedVersion,
        IReadOnlyList<PrtgAuthorityJsonRemoval> removals, long? hostId = null)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (removals.Count is < 1 or > MaximumEdits) throw new ArgumentOutOfRangeException(nameof(removals));
        if (removals.Any(x => x is null || string.IsNullOrWhiteSpace(x.Pointer) || x.Pointer.Length > 512))
            throw new ArgumentException("Every repair pointer must be non-empty and at most 512 characters.", nameof(removals));
        return Repair(kind, hostId, expectedVersion, (raw, _) =>
        {
            JsonNode root;
            try { root = JsonNode.Parse(raw) ?? throw new JsonException("JSON root is null."); }
            catch (JsonException) { throw new InvalidOperationException("repair-json-pointer-requires-complete-json"); }
            foreach (var removal in removals) RemovePointer(root, removal.Pointer);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }, removals.Count);
    }

    public PrtgAuthorityRepairResult ApplyTextPatches(PrtgAuthorityRepairDocumentKind kind, long expectedVersion,
        IReadOnlyList<PrtgAuthorityTextPatch> patches, long? hostId = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Count is < 1 or > MaximumEdits) throw new ArgumentOutOfRangeException(nameof(patches));
        foreach (var patch in patches)
        {
            if (patch is null || patch.Start < 0 || patch.DeleteLength < 0 || patch.DeleteLength > MaximumEditCharacters ||
                patch.Replacement is null || patch.Replacement.Length > MaximumEditCharacters ||
                patch.ExpectedDeletedTextSha256 is not { Length: 64 } ||
                !patch.ExpectedDeletedTextSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("A text patch is outside the fixed repair limits.", nameof(patches));
        }
        return Repair(kind, hostId, expectedVersion, (raw, repairAtUtc) =>
        {
            var ordered = patches.OrderByDescending(p => p.Start).ToArray();
            var priorStart = raw.Length + 1;
            var builder = new StringBuilder(raw);
            foreach (var patch in ordered)
            {
                if (patch.Start > raw.Length || (long)patch.Start + patch.DeleteLength > raw.Length ||
                    patch.Start >= priorStart || (long)patch.Start + patch.DeleteLength > priorStart)
                    throw new InvalidOperationException("repair-patch-overlap-or-range");
                var deleted = raw.Substring(patch.Start, patch.DeleteLength);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deleted)));
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash),
                        Encoding.ASCII.GetBytes(patch.ExpectedDeletedTextSha256.ToUpperInvariant())))
                    throw new InvalidOperationException("repair-patch-deleted-text-mismatch");
                builder.Remove(patch.Start, patch.DeleteLength).Insert(patch.Start, patch.Replacement);
                priorStart = patch.Start;
            }
            var patched = builder.ToString();
            var syntaxValid = true;
            try { _ = JsonNode.Parse(raw); }
            catch (JsonException) { syntaxValid = false; }
            if (syntaxValid && !HasStrictShapeFailure(kind, raw))
                throw new InvalidOperationException("repair-text-patch-requires-invalid-json-or-ambiguous-authority-shape");
            return InvalidateRestoredAuthority(kind, hostId, patched, repairAtUtc);
        }, patches.Count);
    }

    private PrtgAuthorityRepairResult Repair(PrtgAuthorityRepairDocumentKind kind, long? hostId,
        long expectedVersion, Func<string, DateTime, string> edit, int editCount)
    {
        if (expectedVersion <= 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var target = Resolve(kind, hostId, _testFormalLimit);
        var budget = RepairBudget(target.FormalLimit);
        using var ctx = _backend.CreateContext();
        using var transaction = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
        var read = EfJsonBlobStore.ReadBoundedInContext(ctx, target.Key, budget);
        if (read.Version == 0 || read.Prefix is null || read.ReportedLength > budget)
            throw new InvalidOperationException("repair-document-exceeds-bounded-repair-budget");
        if (read.Version != expectedVersion)
            throw new DbUpdateConcurrencyException("repair-stale-version");

        var now = DateTime.UtcNow;
        var candidate = edit(read.Prefix, now);
        if (candidate.Length > target.FormalLimit)
            throw new InvalidOperationException("repair-result-exceeds-formal-document-limit");
        ValidateCandidate(kind, hostId, candidate);
        var newVersion = expectedVersion + 1;
        var affected = ctx.Blobs.Where(row => row.BlobKey == target.Key && row.Version == expectedVersion)
            .ExecuteUpdate(update => update.SetProperty(row => row.Content, candidate)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.Version, row => row.Version + 1));
        if (affected != 1) throw new DbUpdateConcurrencyException("repair-stale-version");
        transaction.Commit();
        return new(expectedVersion, newVersion, read.Prefix.Length, candidate.Length, editCount,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))));
    }

    private static PrtgAuthorityRepairState Classify(PrtgAuthorityRepairDocumentKind kind, long? hostId,
        string raw, int formalLimit)
    {
        if (raw.Length > formalLimit) return PrtgAuthorityRepairState.OverFormalLimit;
        try { ValidateCandidate(kind, hostId, raw); return PrtgAuthorityRepairState.WithinLimit; }
        catch (JsonException) { return PrtgAuthorityRepairState.InvalidJson; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        { return PrtgAuthorityRepairState.InvalidContent; }
    }

    private static void ValidateCandidate(PrtgAuthorityRepairDocumentKind kind, long? hostId, string raw)
    {
        switch (kind)
        {
            case PrtgAuthorityRepairDocumentKind.ResourcePressureMode:
                PrtgResourcePressureModeStore.ValidateRepairDocument(raw, hostId!.Value);
                break;
            case PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence:
                PrtgDiskSemanticEvidenceStore.ValidateRepairDocument(raw);
                break;
            case PrtgAuthorityRepairDocumentKind.DiskVerificationResults:
                PrtgDiskVerificationResultStore.ValidateRepairDocument(raw);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static bool HasStrictShapeFailure(PrtgAuthorityRepairDocumentKind kind, string raw)
    {
        try
        {
            switch (kind)
            {
                case PrtgAuthorityRepairDocumentKind.ResourcePressureMode:
                    StrictAuthorityJsonDocument.Validate<PrtgResourcePressureModeDocument>(raw);
                    break;
                case PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence:
                    StrictAuthorityJsonDocument.Validate<Dictionary<long, PrtgDiskSemanticEvidence>>(raw);
                    break;
                case PrtgAuthorityRepairDocumentKind.DiskVerificationResults:
                    StrictAuthorityJsonDocument.Validate<Dictionary<long, PrtgDiskVerificationResult>>(raw);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return false;
        }
        catch (JsonException) { return true; }
    }

    private static string InvalidateRestoredAuthority(PrtgAuthorityRepairDocumentKind kind, long? hostId,
        string raw, DateTime repairAtUtc)
    {
        var root = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Repair source must be one complete JSON object.");
        switch (kind)
        {
            case PrtgAuthorityRepairDocumentKind.ResourcePressureMode:
            {
                var grants = FindProperty(root, "Grants") as JsonArray
                    ?? throw new InvalidDataException("Mode repair document has no grant collection.");
                var now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var item in grants)
                {
                    if (item is not JsonObject grant) throw new InvalidDataException("Mode repair grant is not an object.");
                    SetProperty(grant, "FormalEnabled", JsonValue.Create(false));
                    SetProperty(grant, "ModeTransitionId", JsonValue.Create(Guid.NewGuid().ToString("N")));
                    SetProperty(grant, "UpdatedAtUtc", JsonValue.Create(now));
                }
                break;
            }
            case PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence:
            {
                var repairTime = repairAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                InvalidateMapEntries(root, (entry, _) =>
                {
                    SetProperty(entry, "ParserSemanticVersion", JsonValue.Create(PrtgDiskSemanticEvidenceStore.RepairInvalidationMarker));
                    SetProperty(entry, nameof(PrtgDiskSemanticEvidence.RepairInvalidatedAtUtc), JsonValue.Create(repairTime));
                });
                break;
            }
            case PrtgAuthorityRepairDocumentKind.DiskVerificationResults:
                InvalidateMapEntries(root, (entry, _) =>
                {
                    SetProperty(entry, "ParserSemanticVersion", JsonValue.Create("repair-invalidated"));
                    SetProperty(entry, "Status", JsonValue.Create("Stale"));
                    SetProperty(entry, "ValuesMatch", null);
                    SetProperty(entry, "ComparedPointCount", JsonValue.Create(0));
                });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void InvalidateMapEntries(JsonObject root, Action<JsonObject, string> invalidate)
    {
        foreach (var (key, node) in root.ToArray())
        {
            if (node is not JsonObject entry) throw new InvalidDataException("Disk repair entry is not an object.");
            invalidate(entry, key);
        }
    }

    private static JsonNode? FindProperty(JsonObject obj, string name)
    {
        var property = obj.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Key is null ? null : property.Value;
    }

    private static void SetProperty(JsonObject obj, string name, JsonNode? value)
    {
        var existingName = obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        obj[existingName ?? name] = value;
    }

    private static (string Key, int FormalLimit) Resolve(PrtgAuthorityRepairDocumentKind kind, long? hostId,
        int? testFormalLimit)
    {
        if (kind == PrtgAuthorityRepairDocumentKind.ResourcePressureMode)
        {
            if (hostId is not > 0) throw new ArgumentOutOfRangeException(nameof(hostId));
            return (PrtgResourcePressureModeStore.BlobKey(hostId.Value),
                testFormalLimit ?? PrtgResourcePressureModeStore.MaximumDocumentCharacters);
        }
        if (hostId is not null) throw new ArgumentException("Shared disk documents do not take a host id.", nameof(hostId));
        return kind switch
        {
            PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence =>
                (PrtgDiskSemanticEvidenceStore.BlobKey, testFormalLimit ?? PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters),
            PrtgAuthorityRepairDocumentKind.DiskVerificationResults =>
                (PrtgDiskVerificationResultStore.BlobKey, testFormalLimit ?? PrtgDiskVerificationResultStore.MaximumSnapshotCharacters),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private int RepairBudget(int formalLimit) => _testRepairBudget ?? checked(formalLimit * 2);

    private static void RemovePointer(JsonNode root, string pointer)
    {
        if (!pointer.StartsWith("/", StringComparison.Ordinal) || pointer == "/")
            throw new ArgumentException("A repair pointer must identify a child value.", nameof(pointer));
        var segments = pointer[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segments[i], out var child) && child is not null => child,
                JsonArray array when int.TryParse(segments[i], out var index) && index >= 0 && index < array.Count => array[index]!,
                _ => throw new InvalidOperationException("repair-json-pointer-not-found")
            };
        }
        var last = segments[^1];
        var removed = current switch
        {
            JsonObject obj => obj.Remove(last),
            JsonArray array when int.TryParse(last, out var index) && index >= 0 && index < array.Count => RemoveArrayAt(array, index),
            _ => false
        };
        if (!removed) throw new InvalidOperationException("repair-json-pointer-not-found");
    }

    private static bool RemoveArrayAt(JsonArray array, int index) { array.RemoveAt(index); return true; }
}
