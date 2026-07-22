using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ChannelCoverage.WasRead 是趨勢層/慢速趨勢層排除「當天沒讀到該頻道」歷史日的判斷點，
/// 也是舊紀錄（ChannelsRead=null）相容的關鍵——舊紀錄必須 fallback 到既有的三頻道 + SecurityLogAvailable
/// 語意，新頻道則一律不算入基準，切換日才不會被假性零污染。
/// </summary>
public class ChannelCoverageTests
{
    [Fact]
    public void 新紀錄以ChannelsRead清單為準()
    {
        var record = new DailyAnalysisRecord
        {
            ChannelsRead = new List<string> { "System", "Application", ChannelCatalog.DefenderChannel }
        };

        Assert.True(ChannelCoverage.WasRead(record, "System"));
        Assert.True(ChannelCoverage.WasRead(record, ChannelCatalog.DefenderChannel));
        Assert.False(ChannelCoverage.WasRead(record, "Security"));           // 未列入 → 未讀
        Assert.False(ChannelCoverage.WasRead(record, ChannelCatalog.RdpLsmChannel));
    }

    [Fact]
    public void 舊紀錄fallback三傳統日誌一律視為已讀()
    {
        var oldRecord = new DailyAnalysisRecord { ChannelsRead = null, SecurityLogAvailable = true };

        Assert.True(ChannelCoverage.WasRead(oldRecord, "System"));
        Assert.True(ChannelCoverage.WasRead(oldRecord, "Application"));
        Assert.True(ChannelCoverage.WasRead(oldRecord, "Security"));
    }

    [Fact]
    public void 舊紀錄SecurityLogAvailable為false時Security視為未讀()
    {
        var oldRecord = new DailyAnalysisRecord { ChannelsRead = null, SecurityLogAvailable = false };

        Assert.False(ChannelCoverage.WasRead(oldRecord, "Security"));
        Assert.True(ChannelCoverage.WasRead(oldRecord, "System")); // 傳統日誌不受影響
    }

    [Fact]
    public void 舊紀錄的新頻道一律視為未讀避免切換日污染基準()
    {
        // 本欄位問世前根本沒掃 Defender/RDP，舊紀錄對這些頻道一律回 false，
        // 新頻道上線後才不會把「當年沒讀到」誤當成「當年是 0」拉低基準
        var oldRecord = new DailyAnalysisRecord { ChannelsRead = null, SecurityLogAvailable = true };

        Assert.False(ChannelCoverage.WasRead(oldRecord, ChannelCatalog.DefenderChannel));
        Assert.False(ChannelCoverage.WasRead(oldRecord, ChannelCatalog.RdpLsmChannel));
        Assert.False(ChannelCoverage.WasRead(oldRecord, ChannelCatalog.RdpRcmChannel));
    }
}
