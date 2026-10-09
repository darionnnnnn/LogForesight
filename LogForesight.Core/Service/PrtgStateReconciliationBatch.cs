namespace LogForesight.Core.Service;

/// <summary>Bounded in-memory instructions for atomically reconciling the qualified PRTG families of one host-day.</summary>
public sealed class PrtgStateReconciliationBatch
{
    public const int MaximumItems = 15_000;

    public string SourceGeneration { get; }
    public IReadOnlyDictionary<long, string> StateResourceGenerationsBySensor { get; }
    public IReadOnlyDictionary<long, string> DiskResourceGenerationsBySensor { get; }
    public IReadOnlyList<PrtgResourceGenerationFence> ResourcePressureGenerations { get; }
    public IReadOnlySet<string> CurrentEventKeys { get; }
    public long ResourceModeBlobVersion { get; }
    public bool ResourceModeFenceRequired { get; }
    public long ExpectedParentRecordId { get; }
    public string ExpectedParentFingerprint { get; }
    public string ExpectedParentFindingFingerprint { get; }

    public PrtgStateReconciliationBatch(string sourceGeneration,
        IReadOnlyDictionary<long, string> stateResourceGenerationsBySensor,
        IReadOnlyDictionary<long, string> diskResourceGenerationsBySensor,
        IReadOnlySet<string> currentEventKeys,
        IReadOnlyCollection<PrtgResourceGenerationFence>? resourcePressureGenerations = null,
        long resourceModeBlobVersion = 0, bool resourceModeFenceRequired = false,
        long expectedParentRecordId = 0, string expectedParentFingerprint = "",
        string expectedParentFindingFingerprint = "")
    {
        SourceGeneration = sourceGeneration ?? string.Empty;
        StateResourceGenerationsBySensor = new Dictionary<long, string>(stateResourceGenerationsBySensor ??
            new Dictionary<long, string>());
        DiskResourceGenerationsBySensor = new Dictionary<long, string>(diskResourceGenerationsBySensor ??
            new Dictionary<long, string>());
        ResourcePressureGenerations = (resourcePressureGenerations ?? Array.Empty<PrtgResourceGenerationFence>()).ToArray();
        CurrentEventKeys = new HashSet<string>(currentEventKeys ?? new HashSet<string>(), StringComparer.Ordinal);
        ResourceModeBlobVersion = resourceModeBlobVersion;
        ResourceModeFenceRequired = resourceModeFenceRequired;
        ExpectedParentRecordId = expectedParentRecordId;
        ExpectedParentFingerprint = expectedParentFingerprint ?? string.Empty;
        ExpectedParentFindingFingerprint = expectedParentFindingFingerprint ?? string.Empty;
    }

    public bool IsValid()
    {
        if (!Guid.TryParseExact(SourceGeneration, "N", out _) ||
            StateResourceGenerationsBySensor.Keys.Union(DiskResourceGenerationsBySensor.Keys)
                .Union(ResourcePressureGenerations.Select(item => item.SensorObjid)).Count() > MaximumItems ||
            CurrentEventKeys.Count > MaximumItems ||
            ResourcePressureGenerations.Count > MaximumItems ||
            ResourceModeFenceRequired && ResourceModeBlobVersion < 0 ||
            ExpectedParentRecordId < 0 || ExpectedParentFingerprint.Length > 128 ||
            ExpectedParentFindingFingerprint.Length > 128 ||
            ExpectedParentRecordId > 0 && (ExpectedParentFingerprint.Length == 0 ||
                ExpectedParentFindingFingerprint.Length == 0))
            return false;

        static bool ValidResources(IReadOnlyDictionary<long, string> values) => values.All(pair =>
            pair.Key > 0 && pair.Value is { Length: 32 } && Guid.TryParseExact(pair.Value, "N", out _));
        var resources = StateResourceGenerationsBySensor.Concat(DiskResourceGenerationsBySensor).GroupBy(pair => pair.Key);
        var sharedSensorsHaveSameGeneration = resources.All(group => group.Select(pair => pair.Value)
            .Distinct(StringComparer.Ordinal).Count() == 1) && ResourcePressureGenerations.All(item =>
                item is { SensorObjid: > 0, SourceGeneration: { Length: > 0 and <= 128 },
                    ResourceGeneration: { Length: > 0 and <= 128 } });
        static bool ValidKeys(IReadOnlySet<string> values) => values.All(key =>
            key is { Length: > 0 and <= 512 } && !key.Any(char.IsControl));

        return ValidResources(StateResourceGenerationsBySensor) &&
            ValidResources(DiskResourceGenerationsBySensor) && sharedSensorsHaveSameGeneration &&
            ValidKeys(CurrentEventKeys);
    }
}

public sealed record PrtgResourceGenerationFence(long SensorObjid, string SourceGeneration, string ResourceGeneration);
