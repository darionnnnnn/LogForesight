using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgCoveredStateTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 0, 0, 0, TimeSpan.FromHours(8));
    private static PrtgSensorCoverage Cover(DateTimeOffset from, DateTimeOffset through,
        string source = "core-1", string resource = "sensor-1") =>
        new(10, from, through, source, resource);
    private static PrtgTimedState Change(DateTimeOffset at, string status,
        string source = "core-1", string resource = "sensor-1") =>
        new(10, at, status, source, resource);

    [Fact]
    public void 已證明的前導Down跨三日無新訊息_每天仍有故障區間()
    {
        var eventAt = Start.AddHours(-2);
        var cover = Cover(eventAt.AddMinutes(-1), Start.AddDays(3));
        var changes = new[] { Change(eventAt, "Down") };
        for (var i = 0; i < 3; i++)
        {
            var day = Start.AddDays(i);
            var state = Assert.Single(PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
                [cover], changes, day, day.AddDays(1)));
            Assert.Equal(day, state.From);
            Assert.Equal(day.AddDays(1), state.Through);
            Assert.Equal("Down", state.Status);
            Assert.Equal(eventAt, state.EnteredAt);
        }
    }

    [Fact]
    public void 缺口切斷連續狀態_後段沒有新轉換就不可稱仍Down()
    {
        var changes = new[] { Change(Start.AddHours(-1), "Down") };
        var periods = PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [Cover(Start.AddHours(-2), Start.AddHours(6)), Cover(Start.AddHours(8), Start.AddDays(1))],
            changes, Start, Start.AddDays(1));
        var first = Assert.Single(periods);
        Assert.Equal(Start.AddHours(6), first.Through);
    }

    [Fact]
    public void 上午故障恢復下午再故障_分為兩段且時長不混算()
    {
        var changes = new[] { Change(Start.AddHours(1), "Down"), Change(Start.AddHours(4), "Up"),
            Change(Start.AddHours(15), "Down") };
        var periods = PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [Cover(Start, Start.AddDays(1))], changes, Start, Start.AddDays(1));
        Assert.Equal(3, periods.Count);
        Assert.Equal((Start.AddHours(1), Start.AddHours(4)), (periods[0].From, periods[0].Through));
        Assert.Equal("Up", periods[1].Status);
        Assert.Equal((Start.AddHours(15), Start.AddDays(1)), (periods[2].From, periods[2].Through));
    }

    [Fact]
    public void 來源或資源換世代_舊Down不得續算()
    {
        var leading = Change(Start.AddHours(-1), "Down");
        Assert.Empty(PrtgCoveredStateTimeline.Build(10, "core-2", "sensor-1",
            [Cover(Start.AddHours(-2), Start.AddDays(1), source: "core-2")], [leading], Start, Start.AddDays(1)));
        Assert.Empty(PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-2",
            [Cover(Start.AddHours(-2), Start.AddDays(1), resource: "sensor-2")], [leading], Start, Start.AddDays(1)));
        Assert.Empty(PrtgCoveredStateTimeline.Build(10, null, "sensor-1",
            [Cover(Start.AddHours(-2), Start.AddDays(1))], [leading], Start, Start.AddDays(1)));
    }

    [Fact]
    public void 相同時間互相矛盾的訊息_直到新狀態才有可判定區間()
    {
        var changes = new[] { Change(Start, "Down"), Change(Start, "Up"), Change(Start.AddHours(2), "Warning") };
        var state = Assert.Single(PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [Cover(Start, Start.AddDays(1))], changes, Start, Start.AddDays(1)));
        Assert.Equal(Start.AddHours(2), state.From);
        Assert.Equal("Warning", state.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void 相鄰覆蓋區塊於午夜銜接等同完整連續區間_保留跨日前導Down(int overlapMinutes)
    {
        var cover1 = Cover(Start.AddDays(-1), Start.AddMinutes(overlapMinutes));
        var cover2 = Cover(Start, Start.AddDays(1));
        var changes = new[]
        {
            Change(Start.AddMinutes(-15), "Down"),
            Change(Start.AddMinutes(20), "Up")
        };
        var periods = PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [cover1, cover2], changes, Start, Start.AddDays(1));
        Assert.Equal(2, periods.Count);
        Assert.Equal(Start, periods[0].From);
        Assert.Equal(Start.AddMinutes(20), periods[0].Through);
        Assert.Equal("Down", periods[0].Status);
        Assert.Equal(Start.AddMinutes(-15), periods[0].EnteredAt);
        Assert.Equal(Start.AddMinutes(20), periods[1].From);
        Assert.Equal(Start.AddDays(1), periods[1].Through);
        Assert.Equal("Up", periods[1].Status);
    }

    [Fact]
    public void 覆蓋缺口一tick切斷連續性_不可跨越累算()
    {
        var cover1 = Cover(Start.AddDays(-1), Start.AddTicks(-1));
        var cover2 = Cover(Start, Start.AddDays(1));
        var changes = new[]
        {
            Change(Start.AddMinutes(-15), "Down"),
            Change(Start.AddMinutes(20), "Up")
        };
        var periods = PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [cover1, cover2], changes, Start, Start.AddDays(1));
        var single = Assert.Single(periods);
        Assert.Equal("Up", single.Status);
        Assert.Equal(Start.AddMinutes(20), single.From);
    }

    [Fact]
    public void 沒有前導狀態不猜Down_涵蓋區塊起點至首筆狀態轉換間無狀態()
    {
        var cover = Cover(Start.AddHours(-1), Start.AddDays(1));
        var changes = new[] { Change(Start.AddMinutes(-15), "Down") };
        var periods = PrtgCoveredStateTimeline.Build(10, "core-1", "sensor-1",
            [cover], changes, Start.AddDays(-1), Start);
        var single = Assert.Single(periods);
        Assert.Equal(Start.AddMinutes(-15), single.From);
        Assert.Equal(Start, single.Through);
        Assert.Equal("Down", single.Status);
    }
}
