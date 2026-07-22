using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 多頻道 watchlist 推導（KnownIssueCatalog.ChannelWatchlists）：Operational 頻道的 Information 等級
/// 事件靠 watchlist 精挑，watchlist 由該頻道 provider 探測字串命中的規則 EventIds 推導。這裡驗證
/// Defender/RDP 規則載入後 watchlist 涵蓋正確、且各頻道不互相污染。
/// 讀寫 KnownIssueCatalog 共用靜態狀態，故納入序列執行的 collection。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class ChannelWatchlistTests : IDisposable
{
    private const string DefenderProbe = "Microsoft-Windows-Windows Defender";
    private const string RdpLsmProbe = "Microsoft-Windows-TerminalServices-LocalSessionManager";
    private const string RdpRcmProbe = "Microsoft-Windows-TerminalServices-RemoteConnectionManager";

    public ChannelWatchlistTests() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    [Theory]
    [InlineData(1116)]
    [InlineData(1118)]
    [InlineData(5001)]
    [InlineData(5010)]
    [InlineData(2001)]
    public void Defender事件在Defender_watchlist內(int eventId)
    {
        Assert.True(KnownIssueCatalog.IsWatched(DefenderProbe, eventId));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(24)]
    [InlineData(25)]
    public void RDP工作階段事件在LSM_watchlist內(int eventId)
    {
        Assert.True(KnownIssueCatalog.IsWatched(RdpLsmProbe, eventId));
    }

    [Fact]
    public void RDP驗證成功1149在RCM_watchlist內()
    {
        Assert.True(KnownIssueCatalog.IsWatched(RdpRcmProbe, 1149));
    }

    [Fact]
    public void 各頻道watchlist不互相污染()
    {
        // Defender 的 watchlist 不含 RDP/Security 事件，反之亦然——探測字串各自唯一
        Assert.False(KnownIssueCatalog.IsWatched(DefenderProbe, 21));      // RDP 事件不在 Defender
        Assert.False(KnownIssueCatalog.IsWatched(DefenderProbe, 4625));    // Security 事件不在 Defender
        Assert.False(KnownIssueCatalog.IsWatched(RdpLsmProbe, 1116));      // Defender 事件不在 RDP LSM
        Assert.False(KnownIssueCatalog.IsWatched(RdpRcmProbe, 21));        // LSM 事件不在 RCM
    }

    [Fact]
    public void HasWatchlist在規則存在時為真()
    {
        Assert.True(KnownIssueCatalog.HasWatchlist(DefenderProbe));
        Assert.True(KnownIssueCatalog.HasWatchlist(RdpLsmProbe));
        Assert.True(KnownIssueCatalog.HasWatchlist(RdpRcmProbe));
    }

    [Fact]
    public void 停用Defender規則後其事件不再被watchlist收錄()
    {
        // 模擬使用者在 rules.json 停用某條 Defender 規則：對應事件退出 watchlist（Information 事件收不進來）
        var rules = KnownIssueSeed.CreateRules();
        var rtpRule = rules.First(r => r.Id == "builtin-defender-rtp-disabled-5001");
        var replaced = rules.Select(r => r.Id == rtpRule.Id
            ? new KnownIssueRule
            {
                Id = r.Id, Origin = r.Origin, Enabled = false, Scope = r.Scope, MatchAllEventIds = r.MatchAllEventIds,
                MatchFilter = r.MatchFilter, SourcePattern = r.SourcePattern, EventIds = r.EventIds,
                Category = r.Category, Severity = r.Severity, Description = r.Description, CountThreshold = r.CountThreshold,
                PlainExplanation = r.PlainExplanation, Impact = r.Impact, LikelyCauses = r.LikelyCauses, NextSteps = r.NextSteps
            }
            : r).ToList();

        KnownIssueCatalog.Initialize(replaced);

        Assert.False(KnownIssueCatalog.IsWatched(DefenderProbe, 5001)); // 停用後 5001 退出 watchlist
        Assert.True(KnownIssueCatalog.IsWatched(DefenderProbe, 1116));  // 其他 Defender 規則不受影響
    }
}
