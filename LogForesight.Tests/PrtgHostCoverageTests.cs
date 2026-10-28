using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>主機清單內每台主機的 PRTG 對應狀態與未對應原因（docs/PRTG-SPEC.md §1、§4a）。</summary>
public class PrtgHostCoverageTests
{
    private static WebHost Host(long id, string name, string? ip, bool active = true, long? mergedInto = null) =>
        new() { HostId = id, HostName = name, IpAddress = ip, Active = active, MergedInto = mergedInto };

    private static PrtgHostMapRow Row(long device, long? host, string status) =>
        new() { DeviceObjid = device, HostId = host, MapStatus = status };

    [Fact]
    public void 未對應主機_逐台給出原因_已對應停用合併的主機不出現()
    {
        var hosts = new[]
        {
            Host(1, "a-mapped", "10.0.0.1"),
            Host(2, "b-no-ip", null),
            Host(3, "c-excluded", "10.0.0.3"),
            Host(4, "d-not-in-prtg", "10.0.0.4"),
            Host(5, "e-conflict", "10.0.0.5"),
            Host(6, "f-inactive", "10.0.0.6", active: false),
            Host(7, "g-merged", "10.0.0.7", mergedInto: 1)
        };
        var rows = new[]
        {
            Row(101, 1, PrtgMapStatus.Ok),
            Row(102, 1, PrtgMapStatus.Ok),
            Row(105, 5, PrtgMapStatus.Conflict),
            Row(999, null, PrtgMapStatus.Unmatched) // 主機清單外的 device：不影響任何主機
        };

        var unmapped = PrtgHostCoverage.Unmapped(hosts, rows, new[] { " 10.0.0.3 " });

        Assert.Equal(new[] { "b-no-ip", "c-excluded", "d-not-in-prtg", "e-conflict" }, unmapped.Select(h => h.HostName));
        Assert.Equal(PrtgHostCoverage.ReasonNoIp, unmapped.Single(h => h.HostId == 2).Reason);
        Assert.Equal(PrtgHostCoverage.ReasonIpExcluded, unmapped.Single(h => h.HostId == 3).Reason);
        Assert.Equal(PrtgHostCoverage.ReasonNotInPrtg, unmapped.Single(h => h.HostId == 4).Reason);
        Assert.Equal(PrtgHostCoverage.ReasonConflict, unmapped.Single(h => h.HostId == 5).Reason);
    }

    [Fact]
    public void 衝突優先於IP排除_使用者先看到要人工指派的原因()
    {
        var unmapped = PrtgHostCoverage.Unmapped(
            new[] { Host(5, "e", "10.0.0.5") },
            new[] { Row(105, 5, PrtgMapStatus.Conflict) },
            new[] { "10.0.0.5" });

        Assert.Equal(PrtgHostCoverage.ReasonConflict, Assert.Single(unmapped).Reason);
    }

    [Fact]
    public void 一IP由兩台主機共用_兩台都標衝突_不是只有HostId最小那台()
    {
        var hosts = new[] { Host(2, "a", "10.0.0.9"), Host(5, "b", "10.0.0.9") };
        // mapper 只把衝突列的 HostId 填給最小者（2）
        var rows = new[] { new PrtgHostMapRow { DeviceObjid = 9, HostId = 2, Ip = "10.0.0.9", MapStatus = PrtgMapStatus.Conflict } };

        var unmapped = PrtgHostCoverage.Unmapped(hosts, rows, Array.Empty<string>());

        Assert.All(unmapped, h => Assert.Equal(PrtgHostCoverage.ReasonConflict, h.Reason));
        Assert.Equal(2, unmapped.Count);
    }

    [Fact]
    public void 名稱不一致以主機計_同主機兩台裝置只算一台()
    {
        var rows = new[]
        {
            new PrtgHostMapRow { DeviceObjid = 1, HostId = 7, MapStatus = PrtgMapStatus.Ok, Note = PrtgHostMapper.NoteNameMismatchPrefix + "：a" },
            new PrtgHostMapRow { DeviceObjid = 2, HostId = 7, MapStatus = PrtgMapStatus.Ok, Note = PrtgHostMapper.NoteNameMismatchPrefix + "：b" },
            new PrtgHostMapRow { DeviceObjid = 3, HostId = 8, MapStatus = PrtgMapStatus.Ok }
        };

        Assert.Equal(new long[] { 7 }, PrtgHostCoverage.NameMismatchHostIds(rows));
    }

    [Fact]
    public void 全部主機都已對應時回空清單()
    {
        var unmapped = PrtgHostCoverage.Unmapped(
            new[] { Host(1, "a", "10.0.0.1") }, new[] { Row(1, 1, PrtgMapStatus.Ok) }, Array.Empty<string>());

        Assert.Empty(unmapped);
    }
}
