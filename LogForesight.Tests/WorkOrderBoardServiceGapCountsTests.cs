using LogForesight.Core.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 待派只回計數的入口（<see cref="WorkOrderBoardService.GetGapCounts"/>，儀表板待派卡用）：
/// 與 <c>GetGaps</c> 共用同一個試跑，兩者的可派／派不出去問題數必須在同一份資料上相同。
/// 共用 <see cref="WorkOrderBoardServiceTests"/> 的組裝與資料輔助。
/// </summary>
public partial class WorkOrderBoardServiceTests
{
    [Fact]
    public void GapCounts_與GetGaps的可派與派不出去問題數相同_含不會派的問題不計入()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var roomA = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var ok = AddHost("HOST-OK");                                          // disk：可以派
        AddHost("HOST-X", new[] { roomA.GroupId });                           // disk：沒有人看得到（同問題也落在第二段）
        AddHost("HOST-Y", new[] { roomA.GroupId }, Ntfs());                   // ntfs：沒有人看得到
        var low = AddHost("HOST-LOW", null, Disk(IssueSeverity.Low));   // 嚴重度未達：第三段
        Candidate(h, inPool: true, ok.HostId, low.HostId);

        var gaps = Service().GetGaps(null, null, 1);
        var counts = Service().GetGapCounts(null, null);

        Assert.False(counts.TooLarge);
        Assert.Equal(gaps.Dispatchable.Issues, counts.DispatchableIssues);
        Assert.Equal(gaps.UnassignableSection.Issues, counts.UnassignableIssues);
        // 明確的期望值：可派＝disk（HOST-OK）；派不出去＝disk（HOST-X）與 ntfs（HOST-Y）；第三段的問題不進計數
        Assert.Equal((1, 2), (counts.DispatchableIssues, counts.UnassignableIssues));
        Assert.Equal((gaps.From, gaps.To), (counts.From, counts.To));
    }

    [Fact]
    public void GapCounts_出現點超過上限_TooLarge且計數為零()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");

        var counts = Service(maxOccurrences: 1).GetGapCounts(null, null);

        Assert.True(counts.TooLarge);
        Assert.Equal((0, 0), (counts.DispatchableIssues, counts.UnassignableIssues));
    }
}
