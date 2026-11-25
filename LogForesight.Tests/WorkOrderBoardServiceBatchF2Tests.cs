using LogForesight.Core.Models;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 F（F-2 前端用）：待派清單的 <c>section</c> 篩選。三段各自分頁，
/// 「可以派」第一頁不會被排在最前的「不會派」問題擠掉（A3）；各段統計與分頁總數分開。
/// </summary>
public partial class WorkOrderBoardServiceTests
{
    private void ThreeSectionSetup()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var roomA = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var ok = AddHost("HOST-OK");                                          // disk：可以派
        AddHost("HOST-X", new[] { roomA.GroupId });                           // disk：沒有人看得到
        var low = AddHost("HOST-LOW", null, Ntfs(IssueSeverity.Low));         // ntfs：嚴重度未達
        Candidate(h, inPool: true, ok.HostId, low.HostId);
    }

    [Fact]
    public void F2_section帶可以派_只列有建議處理人的問題_SectionTotal是該段問題數_各段統計不變()
    {
        ThreeSectionSetup();

        var gaps = Service().GetGaps(null, null, 1, "dispatchable");

        var row = Assert.Single(gaps.Rows);
        Assert.Equal("disk", row.Source);
        Assert.Equal(1, gaps.SectionTotal);
        Assert.Equal(2, gaps.Total);                       // 全部問題數不隨段落變
        Assert.Equal(1, gaps.UnassignableSection.Issues);  // 其他段的統計仍在
        Assert.Equal(1, gaps.NotDispatched.Issues);
    }

    [Fact]
    public void F2_section帶派不出去與不會派_各自只列該段的問題()
    {
        ThreeSectionSetup();

        var unassignable = Service().GetGaps(null, null, 1, "unassignable");
        var notDispatched = Service().GetGaps(null, null, 1, "notDispatched");

        Assert.Equal("disk", Assert.Single(unassignable.Rows).Source);
        Assert.Equal("ntfs", Assert.Single(notDispatched.Rows).Source);
        Assert.Equal((1, 1), (unassignable.SectionTotal, notDispatched.SectionTotal));
    }

    [Fact]
    public void F2_section某段沒有任何問題時列表為空_SectionTotal為0_另一側不帶section時是全部()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var low = AddHost("HOST-LOW", null, Ntfs(IssueSeverity.Low));
        Candidate(h, inPool: true, low.HostId);

        var dispatchable = Service().GetGaps(null, null, 1, "dispatchable");
        var all = Service().GetGaps(null, null, 1);

        Assert.Empty(dispatchable.Rows);
        Assert.Equal(0, dispatchable.SectionTotal);
        Assert.Equal(1, all.SectionTotal);
        Assert.Equal(all.Total, all.SectionTotal);
        Assert.Single(all.Rows);
    }

    [Fact]
    public void F2_section不認得的值回驗證錯誤()
    {
        ThreeSectionSetup();

        Assert.Throws<DomainException>(() => Service().GetGaps(null, null, 1, "bogus"));
    }
}
