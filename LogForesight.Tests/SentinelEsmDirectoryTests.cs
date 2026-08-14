using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ESM 事件來源目錄的回應解析（docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §5.2）。
///
/// <para><b>這些樣本是合成的，不是實測</b>：本環境的探索帳號對 ESM 端點被 401/403 拒絕，
/// 拿不到任何真實回應（見 docs/archive/HISTORY.md 2026-07-29 第二輪 probe）。
/// 所以測的不是「欄位對應正確」——那還沒定案——而是**解析器的防禦行為**：
/// 認得的形狀要能解析、不認得的形狀要明確回報「不可用」而不是回報「沒有主機」。</para>
///
/// <para>兩者的差別是整件事的重點：<c>Usable=false</c> 會讓呼叫端退回事件掃描並警告；
/// 若誤報成「這台 Sentinel 沒有主機」，管理員會以為機房空了。</para>
/// </summary>
public class SentinelEsmDirectoryTests
{
    [Fact]
    public void 頂層陣列_解析出主機()
    {
        var result = SentinelEsmDirectory.Parse(
            """[{"name":"SRV-DC01","ipAddress":"10.1.8.1"},{"name":"SRV-DC02","ipAddress":"10.1.8.2"}]""");

        Assert.True(result.Usable);
        Assert.Equal(2, result.RawEntryCount);
        Assert.Equal(new[] { "10.1.8.1", "10.1.8.2" }, result.Sources.Select(s => s.IpAddress));
        Assert.Equal("SRV-DC01", result.Sources[0].Name);
    }

    /// <summary>Sentinel 的其他端點見過「陣列包在物件屬性底下」的形狀，兩種都要吃得下</summary>
    [Fact]
    public void 陣列包在物件屬性底下_一樣解析得出來()
    {
        var result = SentinelEsmDirectory.Parse(
            """{"total":1,"entries":[{"name":"SRV-A","ip":"10.1.2.5"}]}""");

        Assert.True(result.Usable);
        Assert.Equal("10.1.2.5", Assert.Single(result.Sources).IpAddress);
    }

    /// <summary>候選欄位名是猜的，大小寫不該成為第二個猜錯的機會</summary>
    [Theory]
    [InlineData("ipAddress", "name")]
    [InlineData("IPAddress", "Name")]
    [InlineData("ip", "hostname")]
    [InlineData("hostIp", "displayName")]
    public void 候選欄位名不分大小寫(string ipField, string nameField)
    {
        var result = SentinelEsmDirectory.Parse($$"""[{"{{nameField}}":"SRV-A","{{ipField}}":"10.1.2.5"}]""");

        Assert.True(result.Usable);
        Assert.Equal("10.1.2.5", result.Sources[0].IpAddress);
        Assert.Equal("SRV-A", result.Sources[0].Name);
    }

    [Fact]
    public void 名稱缺席_退回IP當顯示名稱()
    {
        var result = SentinelEsmDirectory.Parse("""[{"ipAddress":"10.1.2.5"}]""");

        Assert.True(result.Usable);
        Assert.Equal("10.1.2.5", result.Sources[0].Name);
    }

    /// <summary>
    /// **本測試是這個檔案存在的主要理由**：解析不出東西時，回的是「不可用」
    /// 而不是「沒有主機」。前者退回事件掃描，後者會讓管理員以為機房空了。
    /// </summary>
    [Fact]
    public void 格式不符_回報不可用而不是回報沒有主機()
    {
        // 條目數不是 0（確實收到 3 筆），但沒有一筆解析得出 IP
        var result = SentinelEsmDirectory.Parse(
            """[{"foo":"bar"},{"id":123},{"version":"8.5.0.1"}]""");

        Assert.False(result.Usable);
        Assert.Empty(result.Sources);
        Assert.Equal(3, result.RawEntryCount);   // 「收到 N 筆卻解析不出來」要說得出口
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>403 Forbidden</html>")]   // 錯誤頁而不是 JSON
    [InlineData("\"just a string\"")]
    [InlineData("{}")]
    public void 非預期回應_一律不可用(string body)
    {
        var result = SentinelEsmDirectory.Parse(body);

        Assert.False(result.Usable);
        Assert.Empty(result.Sources);
    }

    /// <summary>版本號之類的字串不能被當成 IP 收進來——IP 驗證要嚴格，
    /// 這是「這條目是不是一台主機」的唯一判準</summary>
    [Theory]
    [InlineData("8.5.0")]           // 段數不足
    [InlineData("10.1.2.300")]      // 超出 0~255
    [InlineData("not-an-ip")]
    public void 不合法的IP值不算主機(string value)
    {
        var result = SentinelEsmDirectory.Parse($$"""[{"name":"X","ipAddress":"{{value}}"}]""");

        Assert.False(result.Usable);
    }

    [Fact]
    public void 同IP重複條目_去重()
    {
        var result = SentinelEsmDirectory.Parse(
            """[{"name":"A","ip":"10.1.2.5"},{"name":"A-dup","ip":"10.1.2.5"}]""");

        Assert.Single(result.Sources);
        Assert.Equal("A", result.Sources[0].Name);   // 保留第一筆
    }

    [Fact]
    public void 混合條目_只收解析得出IP的()
    {
        var result = SentinelEsmDirectory.Parse(
            """[{"name":"A","ip":"10.1.2.5"},{"note":"這條不是主機"},{"name":"B","ip":"10.1.2.6"}]""");

        Assert.True(result.Usable);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(3, result.RawEntryCount);   // 條目數是「收到幾筆」，不是「解析出幾台」
    }
}
