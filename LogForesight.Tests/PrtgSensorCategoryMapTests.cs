using LogForesight.Core.Models;
using LogForesight.Core.Service;
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
        Assert.Null(PrtgSensorTypeCategoryMap.Resolve("SNMP Custom", overrides));
        Assert.Null(PrtgSensorTypeCategoryMap.Resolve(null, overrides));
    }

    [Theory]
    [InlineData("Port", "availability")]
    [InlineData("http", "availability")]
    [InlineData("SNTP", "availability")]
    [InlineData("DNS (DEPRECATED)", "availability")]
    [InlineData("FTP", "availability")]
    [InlineData("RDP (Remote Desktop)", "availability")]
    [InlineData("cisco ip sla", "availability")]
    [InlineData("SNMP Cisco System Health", "hardware")]
    public void 內建對照_實機type清單補齊的條目(string type, string expected)
    {
        var none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, PrtgSensorTypeCategoryMap.Resolve(type, none));
    }

    [Theory]
    [InlineData("SNMP Linux Load Average")]
    [InlineData("SNMP Custom Table")]
    [InlineData("SSH Script")]
    [InlineData("Core Health")]
    [InlineData("SNMP System Uptime")]
    [InlineData("Sensor Factory")]
    public void 內建對照_刻意不分類的type回null(string type)
    {
        var none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.Null(PrtgSensorTypeCategoryMap.Resolve(type, none));
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

    [Fact]
    public void ParseOverrides_帶語意代碼合法_Map只含分類()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[]
        {
            "SNMP Linux Meminfo=memory:memory-percent-free"
        });

        Assert.Empty(errors);
        Assert.Single(map);
        Assert.Equal(PrtgSensorCategories.Memory, map["SNMP Linux Meminfo"]);
    }

    [Fact]
    public void ParseOverrides_語意代碼不合法_回報錯誤含語意與可用值且略過該行()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[]
        {
            "foo=memory:bogus"
        });

        Assert.Empty(map);
        var err = Assert.Single(errors);
        Assert.StartsWith("第 1 行「foo=memory:bogus」：", err);
        Assert.Contains("語意", err);
        Assert.Contains("bogus", err);
        foreach (var code in PrtgValueSemantics.Codes)
        {
            Assert.Contains(code, err);
        }
    }

    [Fact]
    public void ParseOverrides_舊格式無語意代碼_照舊合法()
    {
        var (map, errors) = PrtgSensorTypeCategoryMap.ParseOverrides(new[]
        {
            "Custom Disk=disk"
        });

        Assert.Empty(errors);
        Assert.Single(map);
        Assert.Equal(PrtgSensorCategories.Disk, map["custom disk"]);
    }

    [Fact]
    public void ParseSemanticOverrides_只收含語意的行且大小寫不敏感()
    {
        var semantics = PrtgSensorTypeCategoryMap.ParseSemanticOverrides(new[]
        {
            "SNMP Linux Meminfo=memory:memory-percent-free",
            "Custom Ping = availability:ping-ms ",
            "plain-disk = disk",
            "broken = memory:invalid-code"
        });

        Assert.Equal(2, semantics.Count);
        Assert.Equal(PrtgValueSemantic.MemoryPercentFree, semantics["snmp linux meminfo"]);
        Assert.Equal(PrtgValueSemantic.PingMs, semantics["CUSTOM PING"]);
        Assert.False(semantics.ContainsKey("plain-disk"));
        Assert.False(semantics.ContainsKey("broken"));
    }

    [Theory]
    [InlineData("cpu-percent-used", PrtgValueSemantic.CpuPercentUsed)]
    [InlineData("CPU-PERCENT-USED", PrtgValueSemantic.CpuPercentUsed)]
    [InlineData(" memory-percent-free ", PrtgValueSemantic.MemoryPercentFree)]
    [InlineData("disk-percent-free", PrtgValueSemantic.DiskPercentFree)]
    [InlineData("ping-ms", PrtgValueSemantic.PingMs)]
    public void TryParseCode_合法代碼不分大小寫與空白(string code, PrtgValueSemantic expected)
    {
        Assert.True(PrtgValueSemantics.TryParseCode(code, out var semantic));
        Assert.Equal(expected, semantic);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParseCode_不合法代碼回傳false(string? code)
    {
        Assert.False(PrtgValueSemantics.TryParseCode(code, out _));
    }

    [Fact]
    public void TryResolve_覆寫優先且不分大小寫()
    {
        // 覆寫字典的不分大小寫由 ParseSemanticOverrides 提供（正式路徑唯一來源），測試比照建立
        var overrides = new Dictionary<string, PrtgValueSemantic>(StringComparer.OrdinalIgnoreCase)
        {
            ["snmp linux meminfo"] = PrtgValueSemantic.MemoryPercentFree,
            ["Ping"] = PrtgValueSemantic.CpuPercentUsed // 覆寫內建對照
        };

        Assert.True(PrtgValueSemantics.TryResolve("SNMP Linux Meminfo", overrides, out var sem1));
        Assert.Equal(PrtgValueSemantic.MemoryPercentFree, sem1);

        Assert.True(PrtgValueSemantics.TryResolve("ping", overrides, out var sem2));
        Assert.Equal(PrtgValueSemantic.CpuPercentUsed, sem2);

        // 內建對照
        Assert.True(PrtgValueSemantics.TryResolve("SNMP CPU Load", overrides, out var sem3));
        Assert.Equal(PrtgValueSemantic.CpuPercentUsed, sem3);

        // 未知
        Assert.False(PrtgValueSemantics.TryResolve("Unknown Type", overrides, out _));
    }
}
