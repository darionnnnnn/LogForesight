using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 清單的領域規則。這些規則決定**今晚實際會去查哪些主機**，
/// 錯的方向不是「畫面難看」而是「某些主機靜默地沒有被檢查」——
/// 正是本系統最不能有的失敗方式（沒查 ≠ 沒事）。
/// </summary>
public class NetiqHostListTests
{
    private static WebHost Netiq(
        long id, string ip, string? sentinel = "SENTINEL-A",
        bool active = true, long? mergedInto = null) => new()
    {
        HostId = id,
        HostName = ip,
        IpAddress = ip,
        SentinelId = sentinel == null ? null : 1,
        NetiqServer = sentinel,
        Source = "netiq",
        Active = active,
        MergedInto = mergedInto
    };

    [Fact]
    public void Listed_排除停用與墓碑與本機主機()
    {
        var hosts = new[]
        {
            Netiq(1, "10.0.0.1"),
            Netiq(2, "10.0.0.2", active: false),
            Netiq(3, "10.0.0.3", mergedInto: 1),
            new WebHost { HostId = 4, HostName = "SRV-LOCAL", Source = "local", Active = true }
        };

        Assert.Equal(new long[] { 1 }, NetiqHostList.Listed(hosts).Select(h => h.HostId));
    }

    [Fact]
    public void PendingAssignment_未填Sentinel者()
    {
        var hosts = new[] { Netiq(1, "10.0.0.1"), Netiq(2, "10.0.0.2", sentinel: null) };

        Assert.Equal(new long[] { 2 }, NetiqHostList.PendingAssignment(hosts).Select(h => h.HostId));
    }

    [Fact]
    public void 待歸屬主機_不進輪巡清單()
    {
        var hosts = new[] { Netiq(1, "10.0.0.1"), Netiq(2, "10.0.0.2", sentinel: null) };

        Assert.Equal(new long[] { 1 }, NetiqHostList.Pollable(hosts).Select(h => h.HostId));
    }

    [Fact]
    public void IpConflicts_同IP兩台以上才算_且依建立順序()
    {
        var hosts = new[]
        {
            Netiq(5, "10.0.0.9"),
            Netiq(2, "10.0.0.9"),
            Netiq(3, "10.0.0.8")
        };

        var conflicts = NetiqHostList.IpConflicts(hosts);

        var group = Assert.Single(conflicts);
        Assert.Equal(new long[] { 2, 5 }, group.Select(h => h.HostId));
    }

    /// <summary>停用是衝突的處置手段之一，處置完衝突就該消失（導出狀態，不是要維護的旗標）</summary>
    [Fact]
    public void IpConflicts_停用其中一台後_衝突消失()
    {
        var hosts = new[] { Netiq(1, "10.0.0.9"), Netiq(2, "10.0.0.9", active: false) };

        Assert.Empty(NetiqHostList.IpConflicts(hosts));
    }

    /// <summary>衝突時只輪巡最早建立的那台，行為才可預測（不是隨機挑或兩台都查）</summary>
    [Fact]
    public void Pollable_IP衝突時只取最早建立的那台()
    {
        var hosts = new[] { Netiq(7, "10.0.0.9"), Netiq(3, "10.0.0.9") };

        Assert.Equal(new long[] { 3 }, NetiqHostList.Pollable(hosts).Select(h => h.HostId));
    }

    [Fact]
    public void Pollable_無IP者不列入()
    {
        var noIp = Netiq(1, "10.0.0.1");
        noIp.IpAddress = null;

        Assert.Empty(NetiqHostList.Pollable(new[] { noIp }));
    }

    [Fact]
    public void Ungrouped_不分來源_排除停用與墓碑()
    {
        var grouped = Netiq(1, "10.0.0.1");
        grouped.GroupIds.Add(5);

        var hosts = new[]
        {
            grouped,
            Netiq(2, "10.0.0.2"),
            Netiq(3, "10.0.0.3", active: false),
            new WebHost { HostId = 4, HostName = "SRV-LOCAL", Source = "local", Active = true }
        };

        Assert.Equal(new long[] { 2, 4 }, NetiqHostList.Ungrouped(hosts).Select(h => h.HostId));
    }

    // ── IP 驗證與批次貼上的解析 ───────────────────────────────────────────────

    [Theory]
    [InlineData("10.1.2.12", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("", false)]
    [InlineData("SRV-01", false)]
    [InlineData("10.1.2.999", false)]
    // IPAddress.TryParse 會把這種簡寫當成 10.0.0.1 收下——清單上的 IP 是實際要送去
    // Sentinel 篩選的條件，收下的後果是「這台主機永遠查無資料」
    [InlineData("10.1", false)]
    [InlineData("10.1.2", false)]
    public void IsValidIp(string value, bool expected)
    {
        Assert.Equal(expected, NetiqHostList.IsValidIp(value));
    }

    [Fact]
    public void ParseLine_取出IP與角色描述()
    {
        var line = NetiqHostList.ParseLine("10.1.2.12, OO部門資料庫", 1)!;

        Assert.Equal("10.1.2.12", line.IpAddress);
        Assert.Equal("OO部門資料庫", line.RoleDesc);
        Assert.Null(line.Error);
    }

    /// <summary>角色描述裡的逗號不該被切斷（只切第一個逗號）</summary>
    [Fact]
    public void ParseLine_角色描述含逗號_完整保留()
    {
        var line = NetiqHostList.ParseLine("10.1.2.12,DB 主機,備援", 1)!;

        Assert.Equal("DB 主機,備援", line.RoleDesc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 這是註解")]
    public void ParseLine_空行與註解_回null(string raw)
    {
        Assert.Null(NetiqHostList.ParseLine(raw, 1));
    }

    [Fact]
    public void ParseLine_IP不合法_帶出原因而不是丟棄()
    {
        var line = NetiqHostList.ParseLine("SRV-01,描述", 3)!;

        Assert.Equal(3, line.LineNumber);
        Assert.NotNull(line.Error);
        Assert.Contains("SRV-01", line.Error);
    }
}
