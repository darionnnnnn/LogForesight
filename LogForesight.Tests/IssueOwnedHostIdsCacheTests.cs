using Microsoft.Extensions.DependencyInjection;
using LogForesight.Web.Extensions;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題負責人可見範圍的跨請求快取（回饋四十五輪 B4）。
///
/// 這層快取坐在授權邊界上，所以測試的重心不是「有沒有變快」，而是**不能算錯**：
/// 不得跨使用者命中、指派變更（版本戳推進）後要立刻重算、保留天數改了窗口要跟著變、
/// 沒注入快取時行為與引入前完全相同。
/// </summary>
public class IssueOwnedHostIdsCacheRegistrationTests
{
    /// <summary>
    /// 快取是 VisibilityService 的可選相依：沒被註冊時不會編譯錯、測試也不會紅，
    /// 只是每個請求安靜地退回那支跨保留期的聚合查詢——正是這輪要修的問題。
    /// 與 PrtgDeviceIndexCache 同一道守門。
    /// </summary>
    [Fact]
    public void 註冊守門_快取以Singleton註冊於DI()
    {
        var services = new ServiceCollection();
        services.AddLogForesightServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IssueOwnedHostIdsCache));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}

public class IssueOwnedHostIdsCacheTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly DataVersionStamp _stamp = new();
    private readonly IssueOwnedHostIdsCache _cache;

    public IssueOwnedHostIdsCacheTests() => _cache = new IssueOwnedHostIdsCache(_stamp);

    /// <summary>每個「請求」都是一個新的 VisibilityService（Scoped），共用同一個 Singleton 快取。</summary>
    private VisibilityService NewRequest(ICurrentUser currentUser, bool withCache = true) =>
        new(currentUser, _users, _userGroups, _access, _hosts, _cases, _settings,
            _issueOwners, _issueAggregates, withCache ? _cache : null);

    private WebUser AddUser(string account) =>
        _users.Upsert(new WebUser { Account = account, GroupIds = new List<long>() });

    private WebHost AddHost(string name) =>
        _hosts.Upsert(new WebHost { HostName = name, GroupIds = new List<long>() });

    private void OwnIssue(long userId, string source, int eventId) =>
        _issueOwners.Upsert(new IssueProfile
        {
            SourceName = source,
            EventId = eventId,
            OwnerUserIds = new List<long> { userId }
        });

    // ── 驗收 1：命中就不再打那支聚合查詢 ──────────────────────────────────────────

    [Fact]
    public void 同一使用者版本戳不變時_第二次請求不再觸發聚合查詢()
    {
        var user = AddUser("DOMAIN\\a");
        var host = AddHost("SRV-A");
        OwnIssue(user.UserId, "disk", 153);
        _issueAggregates.HostIdsForResult = new HashSet<long> { host.HostId };

        var first = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();
        var second = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Equal(1, _issueAggregates.HostIdsForCallCount);
        Assert.Contains(host.HostId, first);
        Assert.Contains(host.HostId, second);
    }

    // ── 驗收 2：洩漏守門 ─────────────────────────────────────────────────────────

    /// <summary>兩個負責不同問題、可見範圍不同的使用者交錯呼叫：各自拿到自己的集合，
    /// 不得互相命中（鍵漏了使用者 id 就會在這裡炸）。</summary>
    [Fact]
    public void 兩個使用者交錯呼叫_不得互相命中()
    {
        var userA = AddUser("DOMAIN\\a");
        var userB = AddUser("DOMAIN\\b");
        var hostA = AddHost("SRV-A");
        var hostB = AddHost("SRV-B");
        OwnIssue(userA.UserId, "disk", 153);
        OwnIssue(userB.UserId, "nvme", 7);

        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId };
        var a1 = NewRequest(FakeCurrentUser.ForUser(userA.UserId)).GetVisibleHostIds();

        _issueAggregates.HostIdsForResult = new HashSet<long> { hostB.HostId };
        var b1 = NewRequest(FakeCurrentUser.ForUser(userB.UserId)).GetVisibleHostIds();

        // 之後的計算一律回「不該屬於任何人的」主機：再問一次時若拿到它，就代表沒命中自己的條目
        _issueAggregates.HostIdsForResult = new HashSet<long> { AddHost("SRV-X").HostId };
        var a2 = NewRequest(FakeCurrentUser.ForUser(userA.UserId)).GetVisibleHostIds();
        var b2 = NewRequest(FakeCurrentUser.ForUser(userB.UserId)).GetVisibleHostIds();

        Assert.Equal(new HashSet<long> { hostA.HostId }, a1.ToHashSet());
        Assert.Equal(new HashSet<long> { hostB.HostId }, b1.ToHashSet());
        Assert.Equal(new HashSet<long> { hostA.HostId }, a2.ToHashSet());
        Assert.Equal(new HashSet<long> { hostB.HostId }, b2.ToHashSet());
        Assert.Equal(2, _issueAggregates.HostIdsForCallCount);   // 兩人各算一次，沒有第三次
    }

    // ── 驗收 3：失效守門 ─────────────────────────────────────────────────────────

    /// <summary>模擬「指派了新的問題負責人」：寫入路徑推進版本戳後，下一次呼叫必須重算。</summary>
    [Fact]
    public void 版本戳推進後_下一次呼叫重新計算()
    {
        var user = AddUser("DOMAIN\\a");
        var hostA = AddHost("SRV-A");
        var hostB = AddHost("SRV-B");
        OwnIssue(user.UserId, "disk", 153);

        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId };
        var before = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        // 指派變更：新負責的問題讓另一台主機也出現在可見範圍
        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId, hostB.HostId };
        _stamp.Bump();
        var after = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(hostB.HostId, before);
        Assert.Contains(hostB.HostId, after);
        Assert.Equal(2, _issueAggregates.HostIdsForCallCount);
    }

    // ── 驗收 4：保留天數是鍵的一部分 ─────────────────────────────────────────────

    [Fact]
    public void 保留天數改變後_鍵不同而重新計算()
    {
        var user = AddUser("DOMAIN\\a");
        var hostA = AddHost("SRV-A");
        var hostB = AddHost("SRV-B");
        OwnIssue(user.UserId, "disk", 153);

        _settings.Update(s => s.RetentionDays = 30);
        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId };
        var before = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();
        var from30 = _issueAggregates.LastHostIdsForCall!.Value.From;

        _settings.Update(s => s.RetentionDays = 180);
        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId, hostB.HostId };
        var after = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();
        var from180 = _issueAggregates.LastHostIdsForCall!.Value.From;

        Assert.DoesNotContain(hostB.HostId, before);
        Assert.Contains(hostB.HostId, after);
        Assert.Equal(2, _issueAggregates.HostIdsForCallCount);
        Assert.True(from180 < from30);   // 窗口確實跟著設定變寬
    }

    [Fact]
    public void 組鍵_三個維度任一不同就是不同的鍵()
    {
        Assert.NotEqual(IssueOwnedHostIdsCache.KeyOf(1, 5, 90), IssueOwnedHostIdsCache.KeyOf(2, 5, 90));
        Assert.NotEqual(IssueOwnedHostIdsCache.KeyOf(1, 5, 90), IssueOwnedHostIdsCache.KeyOf(1, 6, 90));
        Assert.NotEqual(IssueOwnedHostIdsCache.KeyOf(1, 5, 90), IssueOwnedHostIdsCache.KeyOf(1, 5, 30));
        Assert.Equal(IssueOwnedHostIdsCache.KeyOf(1, 5, 90), IssueOwnedHostIdsCache.KeyOf(1, 5, 90));
    }

    // ── 驗收 5：沒注入快取時行為與引入前相同 ─────────────────────────────────────

    [Fact]
    public void 未注入快取時_每次請求都直接算()
    {
        var user = AddUser("DOMAIN\\a");
        var hostA = AddHost("SRV-A");
        var hostB = AddHost("SRV-B");
        OwnIssue(user.UserId, "disk", 153);

        _issueAggregates.HostIdsForResult = new HashSet<long> { hostA.HostId };
        var first = NewRequest(FakeCurrentUser.ForUser(user.UserId), withCache: false).GetVisibleHostIds();

        _issueAggregates.HostIdsForResult = new HashSet<long> { hostB.HostId };
        var second = NewRequest(FakeCurrentUser.ForUser(user.UserId), withCache: false).GetVisibleHostIds();

        Assert.Equal(2, _issueAggregates.HostIdsForCallCount);
        Assert.Contains(hostA.HostId, first);
        Assert.Contains(hostB.HostId, second);
        Assert.DoesNotContain(hostA.HostId, second);
    }

    // ── 驗收 6：短路徑不變 ───────────────────────────────────────────────────────

    [Fact]
    public void ViewAll與serverAdmin_不進問題負責人路徑()
    {
        var user = AddUser("DOMAIN\\a");
        AddHost("SRV-A");
        OwnIssue(user.UserId, "disk", 153);
        _issueAggregates.HostIdsForResult = new HashSet<long> { 999 };

        NewRequest(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds();
        var serverAdminVisible = NewRequest(FakeCurrentUser.ServerAdmin()).GetVisibleHostIds();
        var anonymousVisible = NewRequest(FakeCurrentUser.Anonymous()).GetVisibleHostIds();

        Assert.Equal(0, _issueAggregates.HostIdsForCallCount);
        Assert.Null(_issueAggregates.LastHostIdsForCall);
        Assert.Empty(serverAdminVisible);
        Assert.Empty(anonymousVisible);
    }

    // ── 驗收 7：回傳集合被改不影響下一次 ─────────────────────────────────────────

    [Fact]
    public void 呼叫端竄改回傳集合_不汙染快取()
    {
        var user = AddUser("DOMAIN\\a");
        var host = AddHost("SRV-A");
        OwnIssue(user.UserId, "disk", 153);
        _issueAggregates.HostIdsForResult = new HashSet<long> { host.HostId };

        var first = _cache.GetOrAdd(user.UserId, 90, () => new HashSet<long> { host.HostId });
        ((HashSet<long>)first).Add(999);          // 呼叫端亂改自己拿到的那份

        var second = _cache.GetOrAdd(user.UserId, 90, () => throw new InvalidOperationException("應命中快取"));

        Assert.DoesNotContain(999L, second);
        Assert.Equal(new HashSet<long> { host.HostId }, second.ToHashSet());
    }

    /// <summary>VisibilityService 這一側同樣不受影響：整個可見集合被改後，下一個請求照樣正確。</summary>
    [Fact]
    public void 可見集合被竄改後_下一個請求仍正確()
    {
        var user = AddUser("DOMAIN\\a");
        var host = AddHost("SRV-A");
        OwnIssue(user.UserId, "disk", 153);
        _issueAggregates.HostIdsForResult = new HashSet<long> { host.HostId };

        var first = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();
        ((HashSet<long>)first).Add(999);

        var second = NewRequest(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(999L, second);
        Assert.Equal(new HashSet<long> { host.HostId }, second.ToHashSet());
    }

    // ── TTL：逾時後重算（清理機制，不是新鮮度來源）─────────────────────────────

    [Fact]
    public void 逾時後_重新計算()
    {
        var now = new DateTime(2026, 9, 16, 10, 0, 0);
        var cache = new IssueOwnedHostIdsCache(_stamp, () => now);

        var a = cache.GetOrAdd(7, 90, () => new HashSet<long> { 1 });
        now = now.AddSeconds(IssueOwnedHostIdsCache.TtlSeconds + 1);
        var b = cache.GetOrAdd(7, 90, () => new HashSet<long> { 2 });

        Assert.Equal(new HashSet<long> { 1 }, a.ToHashSet());
        Assert.Equal(new HashSet<long> { 2 }, b.ToHashSet());
    }
}
