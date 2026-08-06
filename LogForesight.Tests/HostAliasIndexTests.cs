using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機別名索引（docs/SCALE-ISSUE-FIRST-PLAN.md 根因 A／N1）。
///
/// 這個索引是**純效能改寫**：語意必須與改寫前的
/// <see cref="HostIdentityResolver.Expand"/> 逐位相同。因此這組測試的重點不是
/// 「索引會不會用」，而是「它與舊語意等價」——包含合併鏈、墓碑列、查無主機三種情況。
/// 等價性一旦破掉，症狀是「合併之後看不到舊識別下的歷史」，而那在畫面上只會表現成
/// 資料變少，不會報錯，極難察覺。
/// </summary>
public class HostAliasIndexTests
{
    private static WebHost Host(long id, string name, long? mergedInto = null) =>
        new() { HostId = id, HostName = name, MergedInto = mergedInto, Active = mergedInto == null };

    [Fact]
    public void 存活主機_展開含自己與所有併入它的墓碑列()
    {
        var hosts = new List<WebHost>
        {
            Host(1, "SRV-A"),
            Host(2, "OLD-A1", mergedInto: 1),
            Host(3, "OLD-A2", mergedInto: 1),
            Host(4, "SRV-B")
        };

        var keys = new HostAliasIndex(hosts).Aliases(1);

        Assert.Equal(3, keys.Count);
        Assert.Equal(1, keys[0].HostId);   // 本身永遠排最前（GetOne 依序擇一的既有契約）
        Assert.Equal(new[] { 1L, 2L, 3L }, keys.Select(k => k.HostId).OrderBy(x => x));
    }

    /// <summary>A→B→C 的合併鏈上，A 必須算進 C 的識別集合（既有語意，不可退化成只看直接的 MergedInto）</summary>
    [Fact]
    public void 合併鏈_最末端存活主機涵蓋整條鏈()
    {
        var hosts = new List<WebHost>
        {
            Host(1, "OLD-1", mergedInto: 2),
            Host(2, "OLD-2", mergedInto: 3),
            Host(3, "SRV-NOW")
        };

        var keys = new HostAliasIndex(hosts).Aliases(3);

        Assert.Equal(new[] { 1L, 2L, 3L }, keys.Select(k => k.HostId).OrderBy(x => x));
        Assert.Equal(3, keys[0].HostId);
    }

    [Fact]
    public void 查無主機_回空集合()
    {
        var index = new HostAliasIndex(new List<WebHost> { Host(1, "SRV-A") });
        Assert.Empty(index.Aliases(999));
    }

    /// <summary>直接查墓碑列時只回它自己——別名集合掛在存活主機下，不該從墓碑反向展開整組</summary>
    [Fact]
    public void 直接查墓碑列_只回它自己()
    {
        var hosts = new List<WebHost> { Host(1, "SRV-A"), Host(2, "OLD-A1", mergedInto: 1) };

        var keys = new HostAliasIndex(hosts).Aliases(2);

        Assert.Single(keys);
        Assert.Equal(2, keys[0].HostId);
    }

    [Fact]
    public void Surviving_跟隨合併鏈()
    {
        var hosts = new List<WebHost>
        {
            Host(1, "OLD-1", mergedInto: 2),
            Host(2, "OLD-2", mergedInto: 3),
            Host(3, "SRV-NOW")
        };
        var index = new HostAliasIndex(hosts);

        Assert.Equal(3, index.Surviving(1)!.HostId);
        Assert.Equal(3, index.Surviving(3)!.HostId);
        Assert.Null(index.Surviving(999));
    }

    /// <summary>資料異常（兩列互指）不得無窮迴圈——既有 Surviving 的守則，改寫後要維持</summary>
    [Fact]
    public void 互指的壞資料_不會無窮迴圈()
    {
        var hosts = new List<WebHost>
        {
            Host(1, "A", mergedInto: 2),
            Host(2, "B", mergedInto: 1)
        };

        var index = new HostAliasIndex(hosts);

        Assert.NotNull(index.Surviving(1));
        Assert.NotNull(index.Surviving(2));
    }

    /// <summary>
    /// 與改寫前的實作逐台比對：<see cref="HostIdentityResolver.Expand"/> 現在委派給索引，
    /// 這條測試釘住的是「兩者對同一份資料的結果集合一致」——含亂序、多組合併、孤兒墓碑。
    /// </summary>
    [Fact]
    public void 與Expand的結果逐台一致()
    {
        var hosts = new List<WebHost>
        {
            Host(5, "SRV-E"),
            Host(1, "SRV-A"),
            Host(2, "OLD-A1", mergedInto: 1),
            Host(9, "ORPHAN", mergedInto: 404),   // 併入目標不存在：留在墓碑本身
            Host(3, "SRV-C"),
            Host(4, "OLD-C1", mergedInto: 3)
        };
        var index = new HostAliasIndex(hosts);

        foreach (var host in hosts)
        {
            var viaIndex = index.Aliases(host.HostId).Select(k => k.HostId).OrderBy(x => x).ToList();
            var viaExpand = HostIdentityResolver.Expand(hosts, host.HostId).Select(k => k.HostId).OrderBy(x => x).ToList();
            Assert.Equal(viaExpand, viaIndex);
        }
    }

    [Fact]
    public void HostCount_含墓碑列()
    {
        var hosts = new List<WebHost> { Host(1, "SRV-A"), Host(2, "OLD-A1", mergedInto: 1) };
        Assert.Equal(2, new HostAliasIndex(hosts).HostCount);
    }
}
