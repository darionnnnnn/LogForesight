using LogForesight.Web.Models;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機識別解析（<see cref="HostIdentityResolver"/> / <see cref="HostLookup"/>）的純函數行為。
/// 紀錄以 HostId 關聯主機之後，「一台主機有哪些識別」就是查詢範圍的定義，
/// 錯了會直接表現為「看不到自己的歷史」或「看到不該看的主機」。
/// </summary>
public class HostIdentityResolverTests
{
    private static WebHost Host(long id, string name, long? mergedInto = null) =>
        new() { HostId = id, HostName = name, MergedInto = mergedInto };

    [Fact]
    public void Expand_主機本身排在最前()
    {
        var hosts = new[] { Host(1, "SRV-OLD", mergedInto: 2), Host(2, "SRV-NEW") };

        var keys = HostIdentityResolver.Expand(hosts, 2);

        Assert.Equal(2, keys[0].HostId);
    }

    [Fact]
    public void Expand_涵蓋已併入的墓碑列()
    {
        var hosts = new[]
        {
            Host(1, "SRV-OLD", mergedInto: 3),
            Host(2, "SRV-OTHER"),
            Host(3, "SRV-NEW")
        };

        var keys = HostIdentityResolver.Expand(hosts, 3);

        Assert.Equal(new long[] { 3, 1 }, keys.Select(k => k.HostId));
    }

    /// <summary>
    /// 合併鏈 A→B→C：A 也必須算進 C 的識別集合，否則鏈上最早那台的歷史會消失。
    /// 寫入端已擋掉新的鏈，但既有資料可能已經有——展開自己認得就不必賭資料乾淨。
    /// </summary>
    [Fact]
    public void Expand_涵蓋整條合併鏈()
    {
        var hosts = new[]
        {
            Host(1, "A", mergedInto: 2),
            Host(2, "B", mergedInto: 3),
            Host(3, "C")
        };

        var keys = HostIdentityResolver.Expand(hosts, 3);

        Assert.Equal(3, keys[0].HostId);
        Assert.Equal(new long[] { 1, 2, 3 }, keys.Select(k => k.HostId).OrderBy(id => id));
    }

    [Fact]
    public void Expand_查無主機_回空清單()
    {
        var keys = HostIdentityResolver.Expand(new[] { Host(1, "SRV-01") }, hostId: 99);

        Assert.Empty(keys);
    }

    [Fact]
    public void Surviving_跟隨合併鏈()
    {
        var a = Host(1, "A", mergedInto: 2);
        var hosts = new[] { a, Host(2, "B", mergedInto: 3), Host(3, "C") };

        Assert.Equal(3, HostIdentityResolver.Surviving(hosts, a).HostId);
    }

    /// <summary>資料異常（兩列互指）時必須停下來——無窮迴圈會讓整個查詢頁掛住</summary>
    [Fact]
    public void Surviving_互相指向_不無窮迴圈()
    {
        var a = Host(1, "A", mergedInto: 2);
        var hosts = new[] { a, Host(2, "B", mergedInto: 1) };

        var result = HostIdentityResolver.Surviving(hosts, a);

        Assert.Contains(result.HostId, new long[] { 1, 2 });
    }

    [Fact]
    public void Surviving_目標不存在_留在墓碑本身()
    {
        var a = Host(1, "A", mergedInto: 999);

        Assert.Equal(1, HostIdentityResolver.Surviving(new[] { a }, a).HostId);
    }
}

public class HostLookupTests
{
    private static readonly List<WebHost> Hosts = new()
    {
        new WebHost { HostId = 1, HostName = "SRV-OLD", MergedInto = 2 },
        new WebHost { HostId = 2, HostName = "SRV-NEW" }
    };

    [Fact]
    public void 以HostId解析()
    {
        var record = new DailyAnalysisRecord { HostId = 2, Host = "隨便寫的舊名稱" };

        Assert.Equal("SRV-NEW", new HostLookup(Hosts).For(record)!.HostName);
    }

    /// <summary>墓碑的紀錄要解析到存活主機——否則清單的連結會指向已停用的那一列</summary>
    [Fact]
    public void 墓碑的紀錄_解析到存活主機()
    {
        var record = new DailyAnalysisRecord { HostId = 1, Host = "SRV-OLD" };

        Assert.Equal(2, new HostLookup(Hosts).For(record)!.HostId);
    }

    [Fact]
    public void 舊紀錄無HostId_以名稱解析且不分大小寫()
    {
        var record = new DailyAnalysisRecord { HostId = 0, Host = "srv-new" };

        Assert.Equal(2, new HostLookup(Hosts).For(record)!.HostId);
    }

    [Fact]
    public void 查無對應主機_回null()
    {
        var record = new DailyAnalysisRecord { HostId = 0, Host = "從未登錄過的主機" };

        Assert.Null(new HostLookup(Hosts).For(record));
    }
}

/// <summary>
/// <see cref="RecordRepository"/> 的別名展開——**這是本次修復的核心迴歸測試**。
///
/// 修復前：紀錄以主機名稱查詢，A 併入 B 之後查 B 只帶 B 的名稱，
/// A 名下的歷史就從畫面上消失了（資料還在，但沒有任何頁面看得到）。
/// </summary>
public class RecordRepositoryAliasTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly LogForesight.Sql.EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly WebHost _oldHost;
    private readonly WebHost _newHost;

    private static readonly DateTime BeforeMerge = DateTime.Today.AddDays(-3);
    private static readonly DateTime AfterMerge = DateTime.Today;

    public RecordRepositoryAliasTests()
    {
        _store = new LogForesight.Sql.EfAnalysisRecordStore(_fx.NewContext, "test");

        _oldHost = _hosts.Upsert(new WebHost { HostName = "SRV-OLD" });
        _newHost = _hosts.Upsert(new WebHost { HostName = "SRV-NEW" });

        _store.Append(new DailyAnalysisRecord
        {
            HostId = _oldHost.HostId, Host = _oldHost.HostName, Date = BeforeMerge, RiskLevel = "高"
        });
        _store.Append(new DailyAnalysisRecord
        {
            HostId = _newHost.HostId, Host = _newHost.HostName, Date = AfterMerge, RiskLevel = "低"
        });

        _hosts.Merge(_oldHost.HostId, _newHost.HostId);
    }

    /// <summary>只有存活主機在授權範圍內——墓碑本身不在任何群組裡</summary>
    private RecordRepository CreateRepository() =>
        new(_store, _hosts, new FixedVisibility(_hosts, _newHost.HostId));

    [Fact]
    public void 併入後_可見範圍涵蓋墓碑的歷史紀錄()
    {
        var result = CreateRepository().Query(new RecordQueryFilter());

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Date.Date == BeforeMerge.Date);
    }

    [Fact]
    public void 併入後_指定查存活主機_一併帶出合併前的歷史()
    {
        var repository = CreateRepository();

        var result = repository.Query(new RecordQueryFilter
        {
            Hosts = repository.ResolveHostKeys(_newHost.HostId)
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 併入後_GetOne取得合併前那天的紀錄()
    {
        var result = CreateRepository().GetOne(_newHost.HostId, BeforeMerge);

        Assert.NotNull(result);
        Assert.Equal("高", result!.RiskLevel);
    }

    /// <summary>
    /// 舊紀錄（HostId 未寫入）在合併後同樣要跟著存活主機出現——
    /// 名稱 fallback 與別名展開必須一起生效，否則升級前的資料在合併後就看不到了。
    /// </summary>
    [Fact]
    public void 併入後_墓碑名下的舊紀錄也查得到()
    {
        _store.Append(new DailyAnalysisRecord
        {
            HostId = 0, Host = _oldHost.HostName, Date = BeforeMerge.AddDays(-1), RiskLevel = "中"
        });

        var result = CreateRepository().Query(new RecordQueryFilter());

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.RiskLevel == "中");
    }

    /// <summary>
    /// 反向防線：別名展開只涵蓋**已併入**的主機，不能順手把其他主機也放進範圍。
    /// 展開寫錯的話最容易的失敗方向就是「範圍變大」，那是授權漏洞。
    /// </summary>
    [Fact]
    public void 未併入的其他主機_不在可見範圍內()
    {
        var stranger = _hosts.Upsert(new WebHost { HostName = "SRV-STRANGER" });
        _store.Append(new DailyAnalysisRecord
        {
            HostId = stranger.HostId, Host = stranger.HostName, Date = AfterMerge, RiskLevel = "高"
        });

        var result = CreateRepository().Query(new RecordQueryFilter());

        Assert.DoesNotContain(result, r => r.Host == "SRV-STRANGER");
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>指定主機可見的測試替身（AlwaysVisibleService 無法驗證範圍邊界）</summary>
    private sealed class FixedVisibility : IVisibilityService
    {
        private readonly FakeHostStore _hosts;
        private readonly HashSet<long> _visible;

        public FixedVisibility(FakeHostStore hosts, params long[] visibleHostIds)
        {
            _hosts = hosts;
            _visible = visibleHostIds.ToHashSet();
        }

        public IReadOnlySet<long> GetVisibleHostIds() => _visible;

        public List<WebHost> GetVisibleHosts() =>
            _hosts.GetAll().Where(h => _visible.Contains(h.HostId)).ToList();

        public void EnsureVisible(long hostId)
        {
            if (!_visible.Contains(hostId)) throw DomainException.NotFound("找不到這台主機。");
        }
    }
}
