using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>One canonical selected raw-work scope for Web preflight and transactional proof commits.</summary>
internal static class PrtgRawWorkScope
{
    internal static string Fingerprint(PrtgMonitoringPolicy policy, PrtgHostSnapshot? snapshot)
    {
        IEnumerable<string> selectedHostState = snapshot is null ? Array.Empty<string>() :
            snapshot.Hosts.Where(host => policy.HostIds.Contains(host.HostId))
                .OrderBy(host => host.HostId).Select(host => string.Join(":", host.HostId, host.Active, host.MergedInto));
        return PrtgProfileTransportCapacityStore.Fingerprint(string.Join("\n",
            policy.SourceGeneration, policy.EndpointHint, string.Join(",", policy.HostIds.Order()),
            string.Join(",", policy.SensorIds.Where(id => id > 0).Distinct().Order()),
            string.Join(";", selectedHostState)));
    }

    internal static string ReadInContext(LfDbContext context, PrtgMonitoringPolicy policy)
    {
        var read = EfJsonBlobStore.ReadBoundedInContext(context, "hosts", PrtgHostSnapshot.MaximumTextCharacters);
        if (read.Version == 0 && read.Prefix is null)
            return Fingerprint(policy, PrtgHostSnapshot.FromBoundedWebHosts(Array.Empty<WebHost>(), 0));
        if (read.ReportedLength > PrtgHostSnapshot.MaximumTextCharacters || read.Prefix is null)
            throw new InvalidDataException("qualification-host-scope-cap-exceeded");
        var hosts = JsonSerializer.Deserialize<List<WebHost>>(read.Prefix, LfJsonOptions.Pretty)
            ?? throw new InvalidDataException("qualification-host-scope-invalid");
        if (hosts.Count > PrtgHostSnapshot.MaximumRows)
            throw new InvalidDataException("qualification-host-scope-row-cap-exceeded");
        return Fingerprint(policy, PrtgHostSnapshot.FromBoundedWebHosts(hosts, read.Version));
    }
}
