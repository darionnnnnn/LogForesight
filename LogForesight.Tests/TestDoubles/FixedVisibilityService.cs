using LogForesight.Web.Models;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>固定可見主機集合的可見範圍替身；可另外指定只有案件授與的主機。</summary>
internal sealed class FixedVisibilityService(IEnumerable<long> visibleHostIds,
    IEnumerable<long>? caseGrantOnlyHostIds = null) : IVisibilityService
{
    private readonly HashSet<long> _visible = visibleHostIds.ToHashSet();
    private readonly HashSet<long> _caseOnly = caseGrantOnlyHostIds?.ToHashSet() ?? [];

    public IReadOnlySet<long> GetVisibleHostIds() => _visible;
    public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
    public List<WebHost> GetVisibleHosts() => [];
    public void EnsureVisible(long hostId) { if (!_visible.Contains(hostId)) throw new InvalidOperationException(); }
    public IReadOnlyList<string> GetCaseGrantHostNames() => [];
    public bool IsCaseGrantOnly(long hostId) => _caseOnly.Contains(hostId);
    public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
}
