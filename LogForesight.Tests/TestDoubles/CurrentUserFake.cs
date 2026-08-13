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

    /// <summary>指定帳號字串（回饋十八輪批次F）：驗證「誰做的」被正確記錄下來的測試
    /// （IssueOwnerRule.UpdatedByAccount 之類），比預設的 "test-user" 更能證明帳號真的有傳遞。</summary>
    public static FakeCurrentUser ForAccount(string account) =>
        new() { UserId = 999, Account = account, Capabilities = new HashSet<Capability> { Capability.Maintain } };

    public static FakeCurrentUser ServerAdmin() =>
        new() { UserId = 0, IsServerAdmin = true, Capabilities = RoleCapabilityMap.ForServerAdmin() };

    public static FakeCurrentUser Anonymous() => new() { IsAuthenticated = false, UserId = 0 };
}

// ── 測試替身：IAuthenticationProvider ────────────────────────────────────────
// 原本定義在 DynamicAuthenticationProviderTests.cs 檔尾，搬到這裡與同屬 Web.Auth
// 領域的 FakeCurrentUser 放在一起。

internal class FakeAuthenticationProvider : IAuthenticationProvider
{
    public string NameValue { get; set; } = "Fake";
    public bool RequiresPasswordValue { get; set; } = true;
    public CredentialCheckResult ResultToReturn { get; set; } = CredentialCheckResult.Ok;
    public int VerifyCalls { get; private set; }

    public string Name => NameValue;
    public bool RequiresPassword => RequiresPasswordValue;

    public CredentialCheckResult Verify(string account, string? password)
    {
        VerifyCalls++;
        return ResultToReturn;
    }
}
