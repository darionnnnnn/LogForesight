using LogForesight.Web.Auth;

namespace LogForesight.Tests;

// ── 測試替身：ICurrentUser ───────────────────────────────────────────────────

internal class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; private init; } = true;
    public long UserId { get; private init; }
    public string Account { get; private init; } = "test-user";
    public string DisplayName { get; private init; } = "測試使用者";
    public IReadOnlySet<Capability> Capabilities { get; private init; } = new HashSet<Capability>();
    public bool IsServerAdmin { get; private init; }

    public bool Has(Capability capability) => Capabilities.Contains(capability);

    public static FakeCurrentUser ForUser(long userId, params Capability[] capabilities) =>
        new() { UserId = userId, Capabilities = capabilities.ToHashSet() };

    public static FakeCurrentUser WithCapabilities(params Capability[] capabilities) =>
        new() { UserId = 999, Capabilities = capabilities.ToHashSet() };

    public static FakeCurrentUser ServerAdmin() =>
        new() { UserId = 0, IsServerAdmin = true, Capabilities = RoleCapabilityMap.ForServerAdmin() };

    public static FakeCurrentUser Anonymous() => new() { IsAuthenticated = false, UserId = 0 };
}
