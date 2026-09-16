using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

public class PrtgSensorCategoryMapTests
{
    [Fact]
    public void ParseOverrides_合法兩行_解析成Map且無錯誤()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[]
        {
            "  Custom Fan Sensor = hardware ",
            "HTTP Advanced=Availability"
        });

        Assert.Empty(errors);
        Assert.Equal(2, map.Count);
        Assert.Equal(PrtgSensorCategories.Hardware, map["custom fan sensor"]);
        Assert.Equal(PrtgSensorCategories.Availability, map["HTTP Advanced"]);
    }

    [Fact]
    public void ParseOverrides_缺等號_回報行號與原文且不進Map()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "SNMP CPU Load=cpu", "no equals here" });

        Assert.Single(map);
        var error = Assert.Single(errors);
        Assert.StartsWith("第 2 行「no equals here」：", error);
        Assert.Contains("=", error);
    }

    [Fact]
    public void ParseOverrides_分類不合法_訊息列出合法分類含availability()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "foo=bogus" });

        Assert.Empty(map);
        var error = Assert.Single(errors);
        Assert.StartsWith("第 1 行「foo=bogus」：", error);
        Assert.Contains("bogus", error);
        Assert.Contains("availability", error);
        Assert.Contains("hardware", error);
    }

    [Fact]
    public void ParseOverrides_type空白_回報錯誤()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "  =cpu" });

        Assert.Empty(map);
        Assert.Single(errors);
    }

    [Fact]
    public void ParseOverrides_空白行略過_不算錯誤但行號照算()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "", "   ", "a=disk", "\t", "broken" });

        Assert.Single(map);
        Assert.Equal(PrtgSensorCategories.Disk, map["a"]);
        var error = Assert.Single(errors);
        Assert.StartsWith("第 5 行「broken」：", error);
    }

    [Fact]
    public void ParseOverrides_重複type_後者覆蓋前者且不算錯()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "Foo=cpu", "foo=memory" });

        Assert.Empty(errors);
        Assert.Single(map);
        Assert.Equal(PrtgSensorCategories.Memory, map["FOO"]);
    }

    [Fact]
    public void ParseOverrides_null輸入_回空結果()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(null);

        Assert.Empty(map);
        Assert.Empty(errors);
    }

    [Fact]
    public void Resolve_補充表優先_再查內建表_都沒有回null()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SNMP CPU Load"] = PrtgSensorCategories.Hardware,
            ["Custom"] = PrtgSensorCategories.Disk
        };

        Assert.Equal(PrtgSensorCategories.Hardware, PrtgSensorTypeCategoryMap.Resolve("snmp cpu load", overrides));
        Assert.Equal(PrtgSensorCategories.Disk, PrtgSensorTypeCategoryMap.Resolve("Custom", overrides));
        Assert.Equal(PrtgSensorCategories.Availability, PrtgSensorTypeCategoryMap.Resolve("Ping", overrides));
        Assert.Null(PrtgSensorTypeCategoryMap.Resolve("HTTP", overrides));
        Assert.Null(PrtgSensorTypeCategoryMap.Resolve(null, overrides));
    }

    [Theory]
    [InlineData("traffic", true)]
    [InlineData("DISK", true)]
    [InlineData("cpu", true)]
    [InlineData("memory", true)]
    [InlineData("Availability", true)]
    [InlineData("hardware", true)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_六個合法值不分大小寫(string? value, bool expected)
    {
        Assert.Equal(expected, PrtgSensorCategories.IsValid(value));
    }
}
