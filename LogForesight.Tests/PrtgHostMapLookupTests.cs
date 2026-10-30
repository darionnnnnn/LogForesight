using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>某日用哪一份主機對應的純函式選法（docs/PRTG-SPEC.md §4，含往後沿用）。</summary>
public sealed class PrtgHostMapLookupTests
{
    private static readonly DateTime Today = new(2026, 10, 30);
    private static readonly DateTime Day = new(2026, 10, 10);

    private static PrtgHostMapRow Row(DateTime date, long host = 1) =>
        new() { MapDate = date, DeviceObjid = 1, HostId = host, MapStatus = PrtgMapStatus.Ok };

    [Fact]
    public void 窗內有多份_取窗內最晚()
    {
        var rows = new[] { Row(Day.AddDays(-20), 1), Row(Day.AddDays(-3), 2), Row(Day.AddDays(-10), 3) };
        Assert.Equal(2, PrtgHostMapLookup.Select(rows, Day, 31, Today)!.HostId);
    }

    [Fact]
    public void MapDate等於day_算窗內且優先於之後的對應()
    {
        var rows = new[] { Row(Day, 1), Row(Day.AddDays(2), 2) };
        Assert.Equal(1, PrtgHostMapLookup.Select(rows, Day, 31, Today)!.HostId);
    }

    [Fact]
    public void 回看窗下界不含_恰好day減lookback那天不選()
    {
        Assert.Null(PrtgHostMapLookup.Select([Row(Day.AddDays(-31))], Day, 31, Today));
        Assert.NotNull(PrtgHostMapLookup.Select([Row(Day.AddDays(-30))], Day, 31, Today));
    }

    [Fact]
    public void 窗內無_之後有兩份_取之後最早()
    {
        var rows = new[] { Row(Day.AddDays(9), 2), Row(Day.AddDays(3), 1), Row(Day.AddDays(-40), 3) };
        Assert.Equal(1, PrtgHostMapLookup.Select(rows, Day, 31, Today)!.HostId);
    }

    [Fact]
    public void 之後的對應等於today_可沿用()
    {
        Assert.Equal(5, PrtgHostMapLookup.Select([Row(Today, 5)], Day, 31, Today)!.HostId);
    }

    [Fact]
    public void 之後的對應晚於today_不選()
    {
        Assert.Null(PrtgHostMapLookup.Select([Row(Today.AddDays(1))], Day, 31, Today));
    }

    [Fact]
    public void 都沒有或窗長不合法_回null()
    {
        Assert.Null(PrtgHostMapLookup.Select([], Day, 31, Today));
        Assert.Null(PrtgHostMapLookup.Select([Row(Day)], Day, 0, Today));
    }

    [Fact]
    public void 兩個回看天數常數引用同一個值()
    {
        Assert.Equal(PrtgHostMapLookup.LookbackDays, PrtgValueAssessment.MapLookbackDays);
        Assert.Equal(PrtgHostMapLookup.LookbackDays, PrtgTriggeredValueFetcher.HostMapLookbackDays);
    }
}
