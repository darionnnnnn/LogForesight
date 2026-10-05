namespace LogForesight.Core.Persistence.Sql;

/// <summary>Caller identity captured by the HTTP layer and rechecked on every transfer operation.</summary>
public sealed record PrtgTransferBinding(string OwnerId, string ScopeHash, string SourceIdentityHash);

/// <summary>
/// Capacity proven by a runtime probe. SafeAdditionalPayloadBytes is the remaining payload budget after
/// reserving at least 25% on every affected database volume and accounting for provider write amplification.
/// </summary>
public sealed record PrtgTransferCapacitySnapshot(
    bool IsVerified,
    long SafeAdditionalPayloadBytes,
    string? CapacitySource,
    string? FailureReason);

public interface IPrtgTransferCapacityProvider
{
    PrtgTransferCapacitySnapshot Capture();
}

public sealed record PrtgTransferCreateRequest(
    Guid TransferId,
    long DeclaredBytes,
    int ChunkCount,
    string PackageSha256,
    PrtgTransferBinding Binding);

public sealed record PrtgTransferStatus(
    Guid TransferId,
    string State,
    long DeclaredBytes,
    long ReceivedBytes,
    int ChunkCount,
    int ReceivedChunks,
    long Version,
    string? PackageSha256,
    string? ResultManifestJson,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? AbandonedAtUtc);

public sealed record PrtgTransferChunkWriteLease(
    Guid TransferId,
    Guid WriteId,
    int Ordinal,
    int ExpectedBytes,
    long Version,
    DateTimeOffset ExpiresAtUtc);

public sealed record PrtgTransferValidationLease(
    Guid TransferId,
    string LeaseOwner,
    long Version,
    DateTimeOffset ExpiresAtUtc);

public sealed record PrtgDiagnosticReadLease(Guid TransferId, string LeaseOwner, long Version,
    DateTimeOffset ExpiresAtUtc);

public sealed record PrtgTransferChunkReceipt(int Ordinal, int ByteLength, string Sha256, bool AlreadyAccepted);

public sealed class PrtgTransferStoreException(string code, string message, int httpStatusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatusCode { get; } = httpStatusCode;
}

public sealed class PrtgTransferSessionRow
{
    public Guid TransferId { get; set; }
    public string OwnerId { get; set; } = "";
    public string ScopeHash { get; set; } = "";
    public string SourceIdentityHash { get; set; } = "";
    public long DeclaredBytes { get; set; }
    public int ChunkCount { get; set; }
    public string PackageSha256 { get; set; } = "";
    public long ReceivedBytes { get; set; }
    public int ReceivedChunks { get; set; }
    public string State { get; set; } = PrtgTransferStates.Receiving;
    public long Version { get; set; } = 1;
    public Guid? ActiveWriteId { get; set; }
    /// <summary>Conservative process-memory reservation (4× declared body), not the request byte length.</summary>
    public long ActiveWriteBytes { get; set; }
    /// <summary>Persisted as UTC ticks so SQLite and SQL Server compare instants identically.</summary>
    public DateTimeOffset? ActiveWriteUntilUtc { get; set; }
    public string? LeaseOwner { get; set; }
    /// <summary>Persisted as UTC ticks; an active validation lease reserves 16 MiB in the global ledger.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? ResultManifestJson { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? AbandonedAtUtc { get; set; }
    public int CleanupAfterOrdinal { get; set; } = -1;
}

public sealed class PrtgTransferChunkRow
{
    public Guid TransferId { get; set; }
    public int Ordinal { get; set; }
    public byte[] Payload { get; set; } = [];
    public int ByteLength { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
    public PrtgTransferSessionRow Session { get; set; } = null!;
}

public static class PrtgTransferStates
{
    public const string Receiving = "receiving";
    public const string Validating = "validating";
    public const string ValidationFailed = "validation-failed";
    public const string Complete = "complete";
    public const string Abandoned = "abandoned";
}
