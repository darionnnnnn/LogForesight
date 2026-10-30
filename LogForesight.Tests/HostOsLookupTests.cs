using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>AI 補跑一輪內的主機作業系統對照：輪中新增的主機要補讀一次主機表，不是一律當 Windows。</summary>
public class HostOsLookupTests
{
    [Fact]
    public void 輪中新增的Linux主機查不到時補讀一次後取得Linux()
    {
        var loads = 0;
        var table = new Dictionary<long, string> { [1] = WebHost.OsWindows };
        var lookup = new HostOsLookup(() => { loads++; return new Dictionary<long, string>(table); });
        Assert.Equal(1, loads);

        table[2] = WebHost.OsLinux; // 輪中新增
        Assert.Equal(WebHost.OsLinux, lookup.Resolve(2));
        Assert.Equal(2, loads);
        Assert.Equal(WebHost.OsWindows, lookup.Resolve(1)); // 已有的不觸發重讀
        Assert.Equal(2, loads);
    }

    [Fact]
    public void 補讀後仍查不到預設Windows且每輪最多補讀一次()
    {
        var loads = 0;
        var lookup = new HostOsLookup(() => { loads++; return new Dictionary<long, string>(); });

        Assert.Equal(WebHost.OsWindows, lookup.Resolve(99));
        Assert.Equal(WebHost.OsWindows, lookup.Resolve(100));
        Assert.Equal(2, loads); // 建立時一次＋補讀一次，之後不再讀
    }
}
