using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機快照（<see cref="HostStore.CapturePrtgSnapshot()"/>）依資料版本共用（第 77 輪 D-2）：
/// 可見範圍每個請求都要它，派工候選池每位處理人各要一次；版本沒變就不該再讀一次整份主機清單。
/// 讀取次數以 blob 的效能監控計（門檻 0：每次操作都入統計）。
/// </summary>
public sealed class HostSnapshotCacheTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly SqlPerformanceMonitor _monitor = new(thresholdMs: 0);

    public void Dispose() => _fx.Dispose();

    private HostStore Store() => new(new EfJsonBlobStore(_fx.NewContext, "hosts", _monitor));

    private long BoundedReads() =>
        _monitor.Snapshot().TopSlowOperations.FirstOrDefault(o => o.Operation == "blob:hosts:ReadBounded")?.Count ?? 0;

    [Fact]
    public void 版本不變時連續擷取共用同一份快照且只讀一次()
    {
        var store = Store();
        store.Upsert(new WebHost { HostName = "a" });

        var first = store.CapturePrtgSnapshot();
        for (var i = 0; i < 9; i++) Assert.Same(first, store.CapturePrtgSnapshot());

        Assert.Equal(1, BoundedReads());
    }

    [Fact]
    public void 主機異動後下一次擷取反映新內容()
    {
        var store = Store();
        store.Upsert(new WebHost { HostName = "a" });
        var before = store.CapturePrtgSnapshot();

        var added = store.Upsert(new WebHost { HostName = "b" });
        var after = store.CapturePrtgSnapshot();

        Assert.NotSame(before, after);
        Assert.True(after.Version > before.Version);
        Assert.Equal("b", after.Find(added.HostId)!.HostName);
        Assert.Null(before.Find(added.HostId));
    }

    [Fact]
    public void 單台查找以編號命中_零編號退回依序比對()
    {
        var store = Store();
        var a = store.Upsert(new WebHost { HostName = "a" });
        var b = store.Upsert(new WebHost { HostName = "b" });
        var snapshot = store.CapturePrtgSnapshot();

        Assert.Equal("a", snapshot.Find(a.HostId)!.HostName);
        Assert.Equal("b", snapshot.Find(b.HostId)!.HostName);
        Assert.Null(snapshot.Find(9999));
        Assert.Null(snapshot.Find(0));
    }

    /// <summary>快照被多個請求共用，必須不可變：任何公開的可寫屬性或可變集合都會讓一個請求改到別人看到的資料。</summary>
    [Theory]
    [InlineData(typeof(PrtgHostSnapshot))]
    [InlineData(typeof(PrtgHostSnapshotEntry))]
    public void 快照型別對外不可變(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic, $"{type.Name}.{property.Name} 有公開 setter");
            var t = property.PropertyType;
            var mutableCollection = t.IsArray ||
                (t.IsGenericType && t.GetGenericTypeDefinition() is var g &&
                 (g == typeof(List<>) || g == typeof(IList<>) || g == typeof(ICollection<>) || g == typeof(Dictionary<,>) || g == typeof(HashSet<>)));
            Assert.False(mutableCollection, $"{type.Name}.{property.Name} 是可變集合 {t.Name}");
        }
    }

    /// <summary>
    /// 派工候選池對每位在池內的處理人各算一次可見主機；整份主機清單的讀取次數不得隨處理人數成長。
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    public void 派工候選池建立時主機清單只讀一次_與處理人數無關(int handlers)
    {
        var hosts = Store();
        for (var i = 0; i < 20; i++) hosts.Upsert(new WebHost { HostName = $"srv-{i:D2}" });
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var dept = groups.Upsert(new UserGroup { GroupName = "dept", Role = UserRole.User, DispatchPool = true, Active = true });
        for (var i = 0; i < handlers; i++)
            users.Upsert(new WebUser { Account = $"u{i}", GroupIds = new List<long> { dept.GroupId } });

        var source = new DispatchCandidateSource(users, groups, hosts, new FakeGroupAccessStore(), new FakeIssueOwnerStore(),
            new FakeIssueAggregateQuery(), new FakeSystemSettingsStore(), new FakeWorkOrderStore(new FakeIssueCaseStore()));
        var pool = source.Build();

        Assert.Equal(handlers, pool.PoolMemberCount);
        Assert.Equal(1, BoundedReads());
    }
}
