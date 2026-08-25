using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 資料可見範圍（docs/WEB-SPEC.md §7.1 第 3 層）——**Phase 1 的核心驗收項目**。
///
/// 這是不可繞過的最後防線：即使 API 忘了掛 [Permission]，查詢仍只回授權範圍內的資料。
/// §12 要求「每個查詢型 Service 至少一條授權過濾測試」，這裡是那條規則的源頭。
/// </summary>
public class VisibilityServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly FakeSystemSettingsStore _settings = new();

    private VisibilityService Create(ICurrentUser currentUser) =>
        new(currentUser, _users, _userGroups, _access, _hosts, _cases, _settings, _issueOwners, _issueAggregates);

    /// <summary>建立「OO 部門使用者 → OO 部門主機」的完整授權鏈，並額外建立一台 XX 部門主機</summary>
    private (WebUser user, WebHost ooHost, WebHost xxHost) SetupTwoDepartments()
    {
        var ooGroup = _userGroups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });
        var xxGroup = _userGroups.Upsert(new UserGroup { GroupName = "XX部門", Role = UserRole.User });

        var user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\wang",
            GroupIds = new List<long> { ooGroup.GroupId }
        });

        var ooHost = _hosts.Upsert(new WebHost { HostName = "SRV-OO-01", GroupIds = new List<long> { 10 } });
        var xxHost = _hosts.Upsert(new WebHost { HostName = "SRV-XX-01", GroupIds = new List<long> { 20 } });

        _access.ReplaceAll(new[]
        {
            new GroupAccess { UserGroupId = ooGroup.GroupId, HostGroupId = 10 },
            new GroupAccess { UserGroupId = xxGroup.GroupId, HostGroupId = 20 }
        });

        return (user, ooHost, xxHost);
    }

    [Fact]
    public void 一般使用者_只看得到被授權群組的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.DoesNotContain(xxHost.HostId, visible);
    }

    [Fact]
    public void 持有ViewAll_看得到全部主機()
    {
        var (_, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 跨部門成員_看得到兩個部門的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var xxGroup = _userGroups.FindByName("XX部門")!;
        _users.SetGroups(user.UserId, new[] { user.GroupIds[0], xxGroup.GroupId });

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 未授權任何主機群組_可見範圍為空()
    {
        SetupTwoDepartments();
        var lonely = _users.Upsert(new WebUser { Account = "DOMAIN\\new" });

        Assert.Empty(Create(FakeCurrentUser.ForUser(lonely.UserId)).GetVisibleHostIds());
    }

    /// <summary>停用群組是「暫時收回這批人的權限」的手段，成員資格還在也不該給範圍</summary>
    [Fact]
    public void 群組已停用_不帶來可見範圍()
    {
        var (user, _, _) = SetupTwoDepartments();
        var ooGroup = _userGroups.FindByName("OO部門")!;
        ooGroup.Active = false;
        _userGroups.Upsert(ooGroup);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    [Fact]
    public void 使用者已停用_可見範圍為空()
    {
        var (user, _, _) = SetupTwoDepartments();
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>serverAdmin 是維護帳號，刻意不看業務資料（最小授權）</summary>
    [Fact]
    public void serverAdmin_可見範圍為空()
    {
        SetupTwoDepartments();

        var service = Create(FakeCurrentUser.ServerAdmin());

        Assert.Empty(service.GetVisibleHostIds());
    }

    [Fact]
    public void 未登入_可見範圍為空()
    {
        SetupTwoDepartments();

        Assert.Empty(Create(FakeCurrentUser.Anonymous()).GetVisibleHostIds());
    }

    /// <summary>
    /// 對沒有權限的人來說，「不存在」與「看不到」必須是同一件事——
    /// 回 403 等於確認「這台主機存在」，可以用來列舉機房裡有哪些主機。
    /// </summary>
    [Fact]
    public void EnsureVisible_未授權主機_拋出找不到而非權限不足()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var ex = Assert.Throws<DomainException>(() => service.EnsureVisible(xxHost.HostId));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void EnsureVisible_已授權主機_通過()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(ooHost.HostId);   // 不應拋例外
    }

    /// <summary>停用主機的資料只留在資料庫，可見範圍必須排除它——這是 N-1 的核心行為</summary>
    [Fact]
    public void 主機已停用_不在ViewAll的可見範圍()
    {
        var (_, ooHost, xxHost) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds();

        Assert.DoesNotContain(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    /// <summary>群組授權路徑同樣要排除停用主機，不能只有 ViewAll 分支生效</summary>
    [Fact]
    public void 主機已停用_不在群組授權的可見範圍()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(ooHost.HostId, visible);
    }

    /// <summary>重新啟用後應完全復原——停用是可逆的，不是刪除</summary>
    [Fact]
    public void 主機重新啟用後_恢復可見()
    {
        var (_, ooHost, _) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);
        ooHost.Active = true;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
    }

    // ── 負責人路徑（docs/archive/FEEDBACK-11-PLAN.md §2b）─────────────────────────────────

    /// <summary>
    /// 負責人匯入是主機歸屬的第一手資料，卻與部門群組授權完全脫鉤——改版前
    /// 「這台出事、負責人卻打不開」是常態。負責人與群組授權取聯集。
    /// </summary>
    [Fact]
    public void 負責人_看得到自己負責的主機即使不屬於授權群組()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(xxHost);

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);   // 群組授權來的
        Assert.Contains(xxHost.HostId, visible);   // 負責人來的
    }

    /// <summary>負責人給的是整台可見（與群組授權同級），不是案件授與那種問題層級的窄授與</summary>
    [Fact]
    public void 負責人_不視為案件授與而是完整可見()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(xxHost);

        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(xxHost.HostId);   // 不應拋例外
        Assert.False(service.IsCaseGrantOnly(xxHost.HostId));
        Assert.Null(service.GetIssueKeyRestriction(xxHost.HostId));
    }

    /// <summary>停用主機的整批排除優先於負責人路徑——資料只留在資料庫，重新啟用即復原</summary>
    [Fact]
    public void 負責人_主機已停用時仍不可見()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        xxHost.Active = false;
        _hosts.Upsert(xxHost);

        Assert.DoesNotContain(xxHost.HostId, Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>停用的使用者不因負責人身分取得任何範圍（停用是安全事件，優先於一切授權路徑）</summary>
    [Fact]
    public void 負責人_使用者已停用時可見範圍為空()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(xxHost);
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>指派前的檢查也要含負責人路徑，否則「他看得到這台嗎」的提示會說謊</summary>
    [Fact]
    public void 指定使用者的可見範圍_含負責人路徑()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(xxHost);

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIdsFor(user.UserId);

        Assert.Contains(xxHost.HostId, visible);
    }

    /// <summary>使用者詳細頁要能區分「群組授權來的」與「負責人來的」，這個投影是那個徽章的來源</summary>
    [Fact]
    public void GetOwnedHostIdsFor_只回負責的啟用主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        xxHost.OwnerUserIds = new List<long> { user.UserId };
        _hosts.Upsert(xxHost);
        var stopped = _hosts.Upsert(new WebHost
        {
            HostName = "SRV-STOPPED", Active = false, OwnerUserIds = new List<long> { user.UserId }
        });

        var owned = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetOwnedHostIdsFor(user.UserId);

        Assert.Contains(xxHost.HostId, owned);
        Assert.DoesNotContain(ooHost.HostId, owned);     // 群組授權來的不算負責人
        Assert.DoesNotContain(stopped.HostId, owned);    // 停用主機不算
    }

    // ── 問題負責人路徑（回饋十八輪批次F，第四條授權路徑）───────────────────────────────

    /// <summary>問題負責人自動可見（保留期內出現過其負責問題的）主機，即使不屬於授權群組——
    /// 與主機負責人同級：整台可見，聯集進 GetVisibleHostIds。</summary>
    [Fact]
    public void 問題負責人_看得到出現過其問題的主機即使不屬於授權群組()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.HostIdsForResult = new HashSet<long> { xxHost.HostId };

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);   // 群組授權來的
        Assert.Contains(xxHost.HostId, visible);   // 問題負責人來的
    }

    /// <summary>已停用（撤場）的主機不因問題負責人身分被看見——同主機負責人的既有語意
    /// （回饋十八輪體檢輪修正：GetIssueOwnedHostIds 原本漏過濾 Active，撤場主機仍會現身）。</summary>
    [Fact]
    public void 問題負責人_已停用主機不視為可見()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        xxHost.Active = false;
        _hosts.Upsert(xxHost);
        _issueAggregates.HostIdsForResult = new HashSet<long> { xxHost.HostId };

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(xxHost.HostId, visible);
    }

    /// <summary>不是任何問題的負責人時，不查聚合（沒有規則就不必問）——驗證早退路徑。</summary>
    [Fact]
    public void 問題負責人_沒有規則時不觸發聚合查詢()
    {
        var (user, _, _) = SetupTwoDepartments();
        _issueAggregates.HostIdsForResult = new HashSet<long> { 999 };   // 若誤觸查詢會混進不該有的主機

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(999, visible);
        Assert.Null(_issueAggregates.LastHostIdsForCall);
    }

    /// <summary>問題負責人給的是整台可見，不是案件授與那種窄授與——與主機負責人同級待遇</summary>
    [Fact]
    public void 問題負責人_不視為案件授與而是完整可見()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.HostIdsForResult = new HashSet<long> { xxHost.HostId };

        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(xxHost.HostId);   // 不應拋例外
        Assert.False(service.IsCaseGrantOnly(xxHost.HostId));
        Assert.Null(service.GetIssueKeyRestriction(xxHost.HostId));
    }

    /// <summary>停用的使用者不因問題負責人身分取得任何範圍——同主機負責人的既有語意</summary>
    [Fact]
    public void 問題負責人_使用者已停用時可見範圍為空()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.HostIdsForResult = new HashSet<long> { xxHost.HostId };
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>指派前的檢查也要含問題負責人路徑，否則「他看得到這台嗎」的提示會說謊
    /// （同主機負責人 GetVisibleHostIdsFor 的既有覆蓋範圍）。</summary>
    [Fact]
    public void 指定使用者的可見範圍_含問題負責人路徑()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _issueOwners.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
        _issueAggregates.HostIdsForResult = new HashSet<long> { xxHost.HostId };

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIdsFor(user.UserId);

        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void GetVisibleHosts_依主機名稱排序()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-Z", GroupIds = new List<long> { 10 } });
        _hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } });

        var hosts = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHosts();

        Assert.Equal(new[] { "SRV-A", "SRV-Z" }, hosts.Select(h => h.HostName));
    }
    // ── 案件授與（docs/archive/FEEDBACK-10-PLAN.md §7）────────────────────────────────

    /// <summary>
    /// 被指派為某個問題的案件處理人時，該主機從「完全看不到」變成「只看得到這個問題」——
    /// EnsureVisible 放行，但 IsCaseGrantOnly 為 true，查詢端據此把內容裁剪到被授與的問題。
    /// </summary>
    [Fact]
    public void 案件處理人_對未授權主機取得範圍限定可見()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        // 指派前：XX 部門主機完全看不到
        Assert.Throws<DomainException>(() => service.EnsureVisible(xxHost.HostId));

        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = xxHost.HostName, IssueKey = "App|disk|153|1",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.InProgress
        });

        var granted = Create(FakeCurrentUser.ForUser(user.UserId));
        granted.EnsureVisible(xxHost.HostId);   // 不再拋例外
        Assert.True(granted.IsCaseGrantOnly(xxHost.HostId));
        Assert.Equal(new[] { "App|disk|153|1" }, granted.GetCaseGrants()[xxHost.HostName]);

        // **範圍限定**：授與不會讓主機混進一般可見清單，統計與清單頁因此不受影響
        Assert.DoesNotContain(xxHost.HostId, granted.GetVisibleHostIds());
    }

    /// <summary>
    /// 結案後仍看得到自己處理過的問題（Q3b 定案）——授與以「現在或曾經是處理人」為準，
    /// 否則使用者剛按下結案就再也打不開自己寫的處理紀錄。
    /// </summary>
    [Fact]
    public void 案件結案後_處理人仍看得到該問題()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = xxHost.HostName, IssueKey = "App|disk|153|1",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.Resolved,
            ClosedAt = DateTime.Now
        });

        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(xxHost.HostId);
        Assert.True(service.IsCaseGrantOnly(xxHost.HostId));
    }

    /// <summary>別人名下的案件不會授與可見性——授與的對象是處理人本人</summary>
    [Fact]
    public void 他人案件_不授與可見性()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = xxHost.HostName, IssueKey = "App|disk|153|1",
            HandlerId = user.UserId + 999, Status = IssueHandlingStatuses.InProgress
        });

        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        Assert.Throws<DomainException>(() => service.EnsureVisible(xxHost.HostId));
        Assert.Empty(service.GetCaseGrants());
    }

    /// <summary>
    /// 已在授權範圍內的主機不算「案件授與」——IsCaseGrantOnly 為 false，
    /// 查詢端因此不會對本來就有完整權限的人裁剪畫面。
    /// </summary>
    [Fact]
    public void 已授權主機_不視為案件授與()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        _cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = ooHost.HostName, IssueKey = "App|disk|153|1",
            HandlerId = user.UserId, Status = IssueHandlingStatuses.InProgress
        });

        Assert.False(Create(FakeCurrentUser.ForUser(user.UserId)).IsCaseGrantOnly(ooHost.HostId));
    }

    /// <summary>指派前的檢查（§7）：問「別人」看不看得到某台主機，用來提示執行指派的人</summary>
    [Fact]
    public void 指定使用者的可見範圍_依其所屬群組解析()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll));

        var visible = service.GetVisibleHostIdsFor(user.UserId);

        Assert.Contains(ooHost.HostId, visible);
        Assert.DoesNotContain(xxHost.HostId, visible);
    }

    /// <summary>停用的使用者不看任何主機——指派給停用帳號的提示因此會標出「看不到」</summary>
    [Fact]
    public void 指定使用者的可見範圍_停用者為空()
    {
        var (user, _, _) = SetupTwoDepartments();
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIdsFor(user.UserId));
    }
}
