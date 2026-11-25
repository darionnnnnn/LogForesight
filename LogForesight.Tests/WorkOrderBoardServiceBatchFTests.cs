using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 F（F-1）：待派三段（A3）、單題派工與預覽（A5／F12-3）、零成員狀態（CU-6）、授權入口（F12-2）。
/// 共用 <see cref="WorkOrderBoardServiceTests"/> 的組裝與資料輔助。
/// </summary>
public partial class WorkOrderBoardServiceTests
{
    // ── A3：三段 ─────────────────────────────────────────────────────────────

    [Fact]
    public void A3_待派分三段_可以派_派不出去_不會派_各段問題數與台數()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var roomA = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var ok = AddHost("HOST-OK");                                          // disk：可以派
        AddHost("HOST-X", new[] { roomA.GroupId });                           // disk：沒有人看得到（同一問題落在第二段）
        AddHost("HOST-LOW", null, Ntfs(IssueSeverity.Low));                   // ntfs：嚴重度未達（第三段）
        Candidate(h, inPool: true, ok.HostId, _hosts.GetAll().Single(x => x.HostName == "HOST-LOW").HostId);

        var gaps = Service().GetGaps(null, null, 1);

        Assert.Equal((1, 1), (gaps.Dispatchable.Issues, gaps.Dispatchable.Hosts));
        Assert.Equal((1, 1), (gaps.UnassignableSection.Issues, gaps.UnassignableSection.Hosts));
        Assert.Equal((1, 1), (gaps.NotDispatched.Issues, gaps.NotDispatched.Hosts));
        var reason = Assert.Single(gaps.UnassignableReasons);
        Assert.Equal((WorkOrderDispatcher.NoCandidateNoVisibility, 1, 1), (reason.Reason, reason.Issues, reason.Hosts));
        // 全部問題（disk、ntfs）仍是 Total；卡片只計前兩段
        Assert.Equal(2, gaps.Total);
    }

    [Fact]
    public void A3_嚴重度未達與不再打擾只在第三段_不進前兩段()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var low = AddHost("HOST-LOW", null, Disk(IssueSeverity.Low));
        var dismissed = AddHost("HOST-DIS", null, Ntfs());
        Candidate(h, inPool: true, low.HostId, dismissed.HostId);
        DismissedCase("HOST-DIS", Ntfs());

        var gaps = Service().GetGaps(null, null, 1);

        // 0 個可派、0 個派不出去：畫面第一段顯示空狀態文字；問題都在第三段
        Assert.Equal((0, 0), (gaps.Dispatchable.Issues, gaps.Dispatchable.Hosts));
        Assert.Equal((0, 0), (gaps.UnassignableSection.Issues, gaps.UnassignableSection.Hosts));
        Assert.Equal((2, 2), (gaps.NotDispatched.Issues, gaps.NotDispatched.Hosts));
        Assert.Empty(gaps.UnassignableReasons);
    }

    // ── F12-2：授權入口 ──────────────────────────────────────────────────────

    [Fact]
    public void F12_2_沒有人看得到的主機群組只出現一次_附主機數與問題數_並列出可授權的派工池群組()
    {
        var pool = AddPoolGroup("派工池甲");
        AddPoolGroup("派工池乙");
        _userGroups.Upsert(new UserGroup { GroupName = "停用池", Role = UserRole.User, Active = false, DispatchPool = true });
        _userGroups.Upsert(new UserGroup { GroupName = "非池", Role = UserRole.User, Active = true, DispatchPool = false });
        var h = AddUser("h", groupIds: pool.GroupId);
        var roomA = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var seen = AddHost("HOST-SEEN");
        // 同一台主機同時有兩個問題、另一台有一個問題，都沒有人看得到：群組只出現一次（原本每個問題各一列）
        AddHost("HOST-X1", new[] { roomA.GroupId }, Disk(), Ntfs());
        AddHost("HOST-X2", new[] { roomA.GroupId }, Disk());
        Candidate(h, inPool: true, seen.HostId);

        var gaps = Service().GetGaps(null, null, 1);

        var group = Assert.Single(gaps.UnseenHostGroups);
        Assert.Equal(("機房A", 2, 2), (group.GroupName, group.Hosts, group.Issues));
        Assert.Equal(roomA.GroupId, group.GroupId);
        Assert.Equal(new[] { "派工池乙", "派工池甲" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            gaps.PoolGroups.Select(p => p.GroupName));
        Assert.True(gaps.CanGrantAccess);
    }

    [Fact]
    public void F12_2_沒有Maintain時只給連結不給授權_CanGrantAccess為false()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var seen = AddHost("HOST-SEEN");
        Candidate(h, inPool: true, seen.HostId);

        Assert.False(Service(user: FakeCurrentUser.WithCapabilities(Capability.Assign)).GetGaps(null, null, 1).CanGrantAccess);
        Assert.True(Service(user: FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Maintain)).GetGaps(null, null, 1).CanGrantAccess);
    }

    [Fact]
    public void F12_2_沒有任何無人可見主機時群組清單為空()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var host = AddHost("HOST-OK");
        Candidate(h, inPool: true, host.HostId);

        Assert.Empty(Service().GetGaps(null, null, 1).UnseenHostGroups);
    }

    // ── A5／F12-3：單題派工與預覽 ───────────────────────────────────────────

    private (WebUser Handler, WebHost[] Hosts) TwoIssueSetup()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var a = AddHost("HOST-A", null, Disk(), Ntfs());
        var b = AddHost("HOST-B", null, Disk(), Ntfs());
        var c = AddHost("HOST-C", null, Disk());
        Candidate(h, inPool: true, a.HostId, b.HostId, c.HostId);
        return (h, new[] { a, b, c });
    }

    [Fact]
    public void A5_單題派工只派該問題_結果等於全頁試跑中該問題的那幾列_其他問題不被派()
    {
        TwoIssueSetup();
        var diskRow = RowOf(Service().GetGaps(null, null, 1), "disk");
        var expectedHosts = diskRow.Suggested.Sum(x => x.Hosts);   // 全頁試跑中 disk 這幾列：3 台

        var result = Service().RunAutoDispatch(null, null, "disk", 153);

        Assert.Equal(expectedHosts, result.AssignedMembers);
        Assert.Equal(1, result.CreatedOrders);
        var order = Assert.Single(_orderStore.All);
        Assert.Equal("disk", order.SourceName);
        // ntfs 完全沒有被派：沒有案件、待派仍是可派
        Assert.DoesNotContain(_caseStore.GetMany(new[] { "HOST-A", "HOST-B", "HOST-C" }), c => c.IssueKey == KeyOf(Ntfs()));
        Assert.Equal(2, RowOf(Service().GetGaps(null, null, 1), "ntfs").Suggested.Sum(x => x.Hosts));
        Assert.False(result.NothingToDispatch);
    }

    [Fact]
    public void A5_單題派工的來源不分大小寫_問題不存在或已派完時是零成員狀態()
    {
        TwoIssueSetup();

        Assert.Equal(3, Service().RunAutoDispatch(null, null, "DISK", 153).AssignedMembers);
        var again = Service().RunAutoDispatch(null, null, "disk", 153);
        Assert.True(again.NothingToDispatch);
        Assert.Equal(WorkOrderBoardService.NothingToDispatchMessage, again.Message);

        var missing = Service().RunAutoDispatch(null, null, "no-such-source", 1);
        Assert.True(missing.NothingToDispatch);
        Assert.Single(_orderStore.All);
    }

    [Fact]
    public void A5_單題派工的篩選只給一半_擲驗證錯誤()
    {
        TwoIssueSetup();

        Assert.Throws<DomainException>(() => Service().RunAutoDispatch(null, null, "disk", null));
        Assert.Throws<DomainException>(() => Service().RunAutoDispatch(null, null, null, 153));
        Assert.Throws<DomainException>(() => Service().PreviewAutoDispatch(null, null, " ", 153));
        Assert.Empty(_orderStore.All);
    }

    [Fact]
    public void F12_3_預覽等於結果_全頁與單題各一例_且預覽不落盤不寫稽核()
    {
        TwoIssueSetup();

        // 單題
        var onePreview = Service().PreviewAutoDispatch(null, null, "disk", 153);
        Assert.Empty(_orderStore.All);
        Assert.Empty(_caseStore.GetMany(new[] { "HOST-A", "HOST-B", "HOST-C" }));
        Assert.DoesNotContain(_audit.Entries, e => e.Action == AuditActions.WorkOrderAutoDispatchRun);
        var oneResult = Service().RunAutoDispatch(null, null, "disk", 153);
        Assert.Equal((onePreview.CreateOrders, onePreview.MergeOrders), (oneResult.CreatedOrders, oneResult.MergedOrders));
        Assert.Equal(onePreview.PerHandler.Select(p => (p.HandlerId, p.Hosts)), oneResult.PerHandler.Select(p => (p.HandlerId, p.Hosts)));
        Assert.Equal(onePreview.Hosts, oneResult.AssignedMembers);

        // 全頁：disk 已派，剩下 ntfs 兩台
        var fullPreview = Service().PreviewAutoDispatch(null, null);
        var fullResult = Service().RunAutoDispatch(null, null);
        Assert.Equal((fullPreview.CreateOrders, fullPreview.MergeOrders), (fullResult.CreatedOrders, fullResult.MergedOrders));
        Assert.Equal(fullPreview.PerHandler.Select(p => (p.HandlerId, p.Hosts)), fullResult.PerHandler.Select(p => (p.HandlerId, p.Hosts)));
        Assert.Equal(2, fullPreview.Hosts);
    }

    [Fact]
    public void F12_3_預覽區分將建立與將併入_已有同人同問題進行中單時算併入()
    {
        var (h, _) = TwoIssueSetup();
        // h 已有 disk 的進行中單（別的主機）：本次 disk 是併入，ntfs 是新建
        _orderStore.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = 153, IssueLabel = "disk 153", HandlerId = h.UserId,
            Origin = WorkOrderOrigins.Manual, CreatedAt = DateTime.Today.AddDays(-1), CreatedByAccount = "x"
        });

        var preview = Service().PreviewAutoDispatch(null, null);

        Assert.Equal((1, 1), (preview.CreateOrders, preview.MergeOrders));
        var perHandler = Assert.Single(preview.PerHandler);
        Assert.Equal(5, perHandler.Hosts);   // disk 3 台＋ntfs 2 台，每人台數是各組相異主機數加總
        var result = Service().RunAutoDispatch(null, null);
        Assert.Equal((1, 1), (result.CreatedOrders, result.MergedOrders));
    }

    [Fact]
    public void F12_3_沒有可派項目時預覽標示零成員與訊息()
    {
        AddPoolGroup();
        AddHost("HOST-LOW", null, Disk(IssueSeverity.Low));

        var preview = Service().PreviewAutoDispatch(null, null);

        Assert.True(preview.NothingToDispatch);
        Assert.Equal(WorkOrderBoardService.NothingToDispatchMessage, preview.Message);
        Assert.Equal((0, 0, 0), (preview.CreateOrders, preview.MergeOrders, preview.Hosts));
    }

    // ── CU-6：零成員 ─────────────────────────────────────────────────────────

    [Fact]
    public void CU6_兩人先後立即派工_後到的人得到零成員狀態而不是已建立0張()
    {
        TwoIssueSetup();

        var first = Service().RunAutoDispatch(null, null);
        var second = Service().RunAutoDispatch(null, null);

        Assert.False(first.NothingToDispatch);
        Assert.Null(first.Message);
        Assert.True(first.AssignedMembers > 0);
        Assert.True(second.NothingToDispatch);
        Assert.Equal("沒有需要派的項目（可能剛由其他人派出）", second.Message);
        Assert.Equal((0, 0, 0), (second.CreatedOrders, second.MergedOrders, second.AssignedMembers));
    }
    [Fact]
    public void CU6_兩人同時立即派工_後到者的併入不計沒有新成員的單()
    {
        TwoIssueSetup();
        AutoDispatchResultDto? first = null;
        var armed = true;
        // 後到者的試跑已取得「進行中案件鍵」（此時還沒有案件），在它取候選人時先到者整批派完：
        // 後到者仍帶著舊的待派清單去建單，每一組都撞上先到者剛建的案件，併入既有單但沒有任何新成員
        _candidates.BeforeBuild = () =>
        {
            if (!armed) return;
            armed = false;
            first = Service().RunAutoDispatch(null, null);
        };

        var second = Service().RunAutoDispatch(null, null);

        Assert.NotNull(first);
        Assert.True(first!.AssignedMembers > 0);
        Assert.Equal(0, second.AssignedMembers);
        Assert.Equal(0, second.CreatedOrders);
        Assert.Equal(0, second.MergedOrders);   // 併入了單，但沒有新成員：不算「併入 N 張」（舊算法會回併入 2 張、共 0 台）
    }
}
