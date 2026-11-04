using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Controllers.Api;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪批次D：未對應主機清單的 <c>pinHostId</c>（主機頁「無 PRTG 對應」徽章連結定位用）。
/// 釘住只改順序：總數、其餘順序與分頁語意不變。
/// </summary>
public class PrtgHostMappingAdminControllerTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly PrtgHostMappingAdminController _controller;

    public PrtgHostMappingAdminControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _controller = PrtgControllerFactory.HostMapping(_backend, new RecordingAuditService(), _hosts);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>四台未對應（沒有 IP）＋一台已對應（ok）；回傳 (未對應 id 依預設順序, 已對應 id)</summary>
    private (List<long> unmapped, long mapped) Seed()
    {
        var mapped = _hosts.Upsert(new WebHost { HostName = "srv-ok", IpAddress = "10.80.0.1", Active = true });
        var unmapped = new List<long>();
        for (var i = 1; i <= 4; i++)
        {
            unmapped.Add(_hosts.Upsert(new WebHost { HostName = $"srv-noip-{i}", Active = true }).HostId);
        }

        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            new() { MapDate = DateTime.Today, DeviceObjid = 8001, HostId = mapped.HostId, MapStatus = PrtgMapStatus.Ok }
        });

        var defaultOrder = _controller.GetPrtgUnmappedHosts(1, 20).Data!.Items.Select(i => i.HostId).ToList();
        return (defaultOrder, mapped.HostId);
    }

    [Fact]
    public void pinHostId_該台排第一頁第一筆_總數與其餘順序不變()
    {
        var (order, _) = Seed();
        Assert.Equal(4, order.Count);
        var target = order[^1];

        var page = _controller.GetPrtgUnmappedHosts(1, 20, target).Data!;

        Assert.Equal(4, page.Total);
        Assert.Equal(target, page.Items[0].HostId);
        Assert.Equal(order.Where(id => id != target).ToList(), page.Items.Skip(1).Select(i => i.HostId).ToList());
        Assert.Equal(4, page.Items.Count);
    }

    [Fact]
    public void pinHostId_沒給時順序與以前相同()
    {
        var (order, _) = Seed();
        var again = _controller.GetPrtgUnmappedHosts(1, 20, null).Data!;
        Assert.Equal(order, again.Items.Select(i => i.HostId).ToList());
    }

    [Fact]
    public void pinHostId_不在未對應集合內_當沒給()
    {
        var (order, mapped) = Seed();

        foreach (var pin in new long[] { mapped, 0, -5, 999_999, long.MaxValue })
        {
            var page = _controller.GetPrtgUnmappedHosts(1, 20, pin).Data!;
            Assert.Equal(4, page.Total);
            Assert.Equal(order, page.Items.Select(i => i.HostId).ToList());
        }
    }

    [Fact]
    public void pinHostId_第二頁語意_釘住的那台只在第一頁且不重複不遺漏()
    {
        var (order, _) = Seed();
        var target = order[^1];

        var first = _controller.GetPrtgUnmappedHosts(1, 2, target).Data!;
        var second = _controller.GetPrtgUnmappedHosts(2, 2, target).Data!;

        Assert.Equal(4, first.Total);
        Assert.Equal(4, second.Total);
        Assert.Equal(target, first.Items[0].HostId);
        Assert.DoesNotContain(second.Items, i => i.HostId == target);
        var all = first.Items.Concat(second.Items).Select(i => i.HostId).ToList();
        Assert.Equal(order.OrderBy(x => x), all.OrderBy(x => x));
        Assert.Equal(4, all.Distinct().Count());
        // 沒釘住的第二頁就是原順序的第 3、4 筆；釘住最後一台後，第二頁是剩下兩台的原順序
        Assert.Equal(order.Where(id => id != target).Skip(1).ToList(), second.Items.Select(i => i.HostId).ToList());
    }

    [Fact]
    public void pinHostId_是純查詢參數_不改變授權標記()
    {
        var method = typeof(PrtgHostMappingAdminController).GetMethod(nameof(PrtgHostMappingAdminController.GetPrtgUnmappedHosts))!;
        Assert.NotEmpty(method.GetCustomAttributes(typeof(LogForesight.Web.Filters.RequireViewAllAttribute), inherit: true));
        var pin = method.GetParameters().Single(p => p.Name == "pinHostId");
        Assert.True(pin.HasDefaultValue);
        Assert.Contains(pin.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.FromQueryAttribute), inherit: false), _ => true);
    }
}
