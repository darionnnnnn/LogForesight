using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>未回報主機的 PRTG 現況提示判定（<see cref="PrtgPresenceHint.Classify"/>）。</summary>
public class PrtgPresenceHintTests
{
    private static (string? Status, string? Category) S(string? status, string? category) => (status, category);

    [Fact]
    public void Ping為Down而traffic為Up_判down()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Down", PrtgSensorCategories.Availability),
            S("Up", PrtgSensorCategories.Traffic)
        });
        Assert.Equal(PrtgPresenceHint.Down, hint);
    }

    [Fact]
    public void Ping為Up而traffic為Down_有availability時只看它_判up()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Up", PrtgSensorCategories.Availability),
            S("Down", PrtgSensorCategories.Traffic)
        });
        Assert.Equal(PrtgPresenceHint.Up, hint);
    }

    [Fact]
    public void 無availability_一顆Down_判down()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Up", PrtgSensorCategories.Disk),
            S("Down (Acknowledged)", PrtgSensorCategories.Traffic),
            S("Up", null)
        });
        Assert.Equal(PrtgPresenceHint.Down, hint);
    }

    [Fact]
    public void 全部Unknown或空值_判unknown()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Unknown", PrtgSensorCategories.Traffic),
            S(null, PrtgSensorCategories.Cpu),
            S("", null)
        });
        Assert.Equal(PrtgPresenceHint.Unknown, hint);
    }

    [Fact]
    public void availability全部Unknown_其他分類Up_仍判unknown()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Unknown", PrtgSensorCategories.Availability),
            S("Up", PrtgSensorCategories.Traffic)
        });
        Assert.Equal(PrtgPresenceHint.Unknown, hint);
    }

    [Fact]
    public void 分類大小寫不同_仍視為availability()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Up", "Availability"),
            S("Down", PrtgSensorCategories.Traffic)
        });
        Assert.Equal(PrtgPresenceHint.Up, hint);
    }

    [Fact]
    public void 無sensor_判unknown()
    {
        Assert.Equal(PrtgPresenceHint.Unknown,
            PrtgPresenceHint.Classify(Array.Empty<(string? Status, string? Category)>()));
    }

    [Fact]
    public void 無availability_有Up也有Unknown_判up()
    {
        var hint = PrtgPresenceHint.Classify(new[]
        {
            S("Unknown", PrtgSensorCategories.Traffic),
            S("Warning", PrtgSensorCategories.Disk)
        });
        Assert.Equal(PrtgPresenceHint.Up, hint);
    }
}
