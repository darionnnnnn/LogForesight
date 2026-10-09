using LogForesight.Core.Service;

namespace LogForesight.Core.Models;

/// <summary>Host-local durable proof and recipient-start state for formal PRTG mail.</summary>
public sealed class PrtgFormalMailClaimShard
{
    public long HostId { get; set; }
    public Dictionary<string, PrtgFormalMailFenceEntry> Fences { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MailFormalStartClaim> Claims { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PrtgFormalMailFenceEntry
{
    public PrtgResourceFormalDeliveryFence Fence { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Small global urgent-outbox pointer to a fence retained in its host shard.</summary>
public sealed record PrtgFormalMailFenceReference(
    long HostId,
    string ShardKey,
    string FenceKey,
    string FenceHash);
