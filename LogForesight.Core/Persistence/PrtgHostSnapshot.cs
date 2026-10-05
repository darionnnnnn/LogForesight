using System.Collections.ObjectModel;
using LogForesight.Core.Models;

namespace LogForesight.Core.Persistence;

/// <summary>A bounded, immutable view of the host catalogue used by PRTG eligibility and authorization.</summary>
public sealed class PrtgHostSnapshot
{
    public const int MaximumTextCharacters = 16 * 1024 * 1024; // 32 MiB UTF-16
    public const int MaximumRows = 250_000;

    private readonly ReadOnlyCollection<PrtgHostSnapshotEntry> _hosts;

    public long Version { get; }
    public IReadOnlyList<PrtgHostSnapshotEntry> Hosts => _hosts;

    private PrtgHostSnapshot(long version, List<PrtgHostSnapshotEntry> hosts)
    {
        Version = version;
        _hosts = hosts.AsReadOnly();
    }

    public PrtgHostSnapshotEntry? Find(long hostId) => _hosts.FirstOrDefault(host => host.HostId == hostId);

    /// <summary>Compatibility adapter for test doubles: inspect objects directly without serializing an unbounded blob.</summary>
    internal static PrtgHostSnapshot FromWebHosts(IEnumerable<WebHost> hosts, long version)
        => Create(hosts, version, enforceEstimatedTextBound: true);

    internal static PrtgHostSnapshot FromBoundedWebHosts(IEnumerable<WebHost> hosts, long version)
        => Create(hosts, version, enforceEstimatedTextBound: false);

    private static PrtgHostSnapshot Create(IEnumerable<WebHost> hosts, long version, bool enforceEstimatedTextBound)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var entries = new List<PrtgHostSnapshotEntry>();
        var positiveIds = new HashSet<long>();
        long estimatedCharacters = 2; // array brackets
        foreach (var host in hosts)
        {
            if (host is null) throw new InvalidDataException("Host catalogue contains a null row.");
            if (entries.Count >= MaximumRows) throw new InvalidDataException($"Host catalogue exceeds {MaximumRows} rows.");
            if (host.HostId > 0 && !positiveIds.Add(host.HostId))
                throw new InvalidDataException($"Host catalogue contains duplicate positive HostId {host.HostId}.");
            if (enforceEstimatedTextBound) estimatedCharacters += EstimateSerializedCharacters(host);
            if (enforceEstimatedTextBound && estimatedCharacters > MaximumTextCharacters)
                throw new InvalidDataException($"Host catalogue exceeds the {MaximumTextCharacters}-character bound.");
            entries.Add(new PrtgHostSnapshotEntry(host));
        }
        return new PrtgHostSnapshot(version, entries);
    }

    private static long EstimateSerializedCharacters(WebHost host)
    {
        // Six output chars per UTF-16 code unit safely bounds JSON escaping. The fixed allowance
        // covers property names, separators, scalar fields, and numbers without materializing JSON.
        long total = 256 + 6L * (host.HostName.Length + (host.DisplayName?.Length ?? 0) +
            (host.IpAddress?.Length ?? 0) + (host.NetiqServer?.Length ?? 0) + host.RoleDesc.Length +
            host.Source.Length + host.Os.Length + (host.OrphanedFromSentinel?.Length ?? 0) + host.Tier.Length);
        total += 22L * (host.GroupIds.Count + host.OwnerUserIds.Count);
        return total;
    }
}

/// <summary>Read-only host fields needed by PRTG eligibility and visibility calculations.</summary>
public sealed class PrtgHostSnapshotEntry
{
    private readonly ReadOnlyCollection<long> _groupIds;
    private readonly ReadOnlyCollection<long> _ownerUserIds;

    public long HostId { get; }
    public string HostName { get; }
    public string? DisplayName { get; }
    public string? IpAddress { get; }
    public DateTime? IpUpdatedAt { get; }
    public long? SentinelId { get; }
    public string? NetiqServer { get; }
    public string RoleDesc { get; }
    public string Source { get; }
    public string Os { get; }
    public bool Active { get; }
    public long? MergedInto { get; }
    public DateTime? LastReportAt { get; }
    public DateTime CreatedAt { get; }
    public IReadOnlyList<long> GroupIds => _groupIds;
    public IReadOnlyList<long> OwnerUserIds => _ownerUserIds;
    public string? OrphanedFromSentinel { get; }
    public bool IsHighVolume { get; }
    public string Tier { get; }

    internal PrtgHostSnapshotEntry(WebHost host)
    {
        HostId = host.HostId;
        HostName = host.HostName;
        DisplayName = host.DisplayName;
        IpAddress = host.IpAddress;
        IpUpdatedAt = host.IpUpdatedAt;
        SentinelId = host.SentinelId;
        NetiqServer = host.NetiqServer;
        RoleDesc = host.RoleDesc;
        Source = host.Source;
        Os = host.Os;
        Active = host.Active;
        MergedInto = host.MergedInto;
        LastReportAt = host.LastReportAt;
        CreatedAt = host.CreatedAt;
        _groupIds = Array.AsReadOnly(host.GroupIds.ToArray());
        _ownerUserIds = Array.AsReadOnly(host.OwnerUserIds.ToArray());
        OrphanedFromSentinel = host.OrphanedFromSentinel;
        IsHighVolume = host.IsHighVolume;
        Tier = host.Tier;
    }

    public WebHost ToWebHost() => new()
    {
        HostId = HostId,
        HostName = HostName,
        DisplayName = DisplayName,
        IpAddress = IpAddress,
        IpUpdatedAt = IpUpdatedAt,
        SentinelId = SentinelId,
        NetiqServer = NetiqServer,
        RoleDesc = RoleDesc,
        Source = Source,
        Os = Os,
        Active = Active,
        MergedInto = MergedInto,
        LastReportAt = LastReportAt,
        CreatedAt = CreatedAt,
        GroupIds = _groupIds.ToList(),
        OwnerUserIds = _ownerUserIds.ToList(),
        OrphanedFromSentinel = OrphanedFromSentinel,
        IsHighVolume = IsHighVolume,
        Tier = Tier
    };
}
