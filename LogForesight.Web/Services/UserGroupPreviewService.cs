using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 使用者編輯視窗的群組即時摘要：依「目前勾選、尚未儲存」的群組算出能力、可見主機台數與是否在派工池，不寫入任何資料。
///
/// 不另寫規則：能力問 <see cref="UserCapabilityResolver"/>（群組角色聯集 ∪ 負責人隱含的 User 角色）、
/// 群組授權的可見範圍問 <see cref="HostVisibilityResolver.GroupVisibleHostIds"/>（與使用者詳情的「看得到的主機」同一條規則）、
/// 派工池看啟用中且 <see cref="UserGroup.DispatchPool"/> 的群組（與 <see cref="DispatchCandidateSource"/> 同定義）。
/// </summary>
public sealed class UserGroupPreviewService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;
    private readonly IHostStore _hosts;
    private readonly IGroupAccessStore _access;
    private readonly UserCapabilityResolver _capabilities;

    public UserGroupPreviewService(
        IUserStore users, IUserGroupStore groups, IHostStore hosts, IGroupAccessStore access, UserCapabilityResolver capabilities)
    {
        _users = users;
        _groups = groups;
        _hosts = hosts;
        _access = access;
        _capabilities = capabilities;
    }

    /// <summary>能力的中文名稱與顯示順序（畫面逐項列出；新增能力時在這裡補一行）</summary>
    internal static readonly (Capability Capability, string Text)[] CapabilityTexts =
    {
        (Capability.ViewAll, "檢視全部主機"),
        (Capability.Handle, "處理（標記狀態、回覆交辦）"),
        (Capability.Assign, "指派處理人"),
        (Capability.ConfirmPermission, "確認權限異動"),
        (Capability.Maintain, "系統維護"),
        (Capability.DevMonitor, "執行監控"),
        (Capability.ViewAudit, "查閱稽核紀錄")
    };

    /// <param name="userId">編輯中的既有使用者；新增中為 null（沒有負責主機、也沒有負責人隱含能力）</param>
    /// <param name="groupIds">目前勾選的群組（不存在的編號略過）</param>
    /// <param name="active">視窗上「啟用」的目前值；停用帳號登入不了，摘要一律是無能力、看不到主機、不在派工池</param>
    public UserGroupPreviewDto Preview(long? userId, IReadOnlyCollection<long> groupIds, bool active = true)
    {
        var allGroups = _groups.GetAll().ToDictionary(g => g.GroupId);
        var picked = groupIds.Distinct().Where(allGroups.ContainsKey).Select(id => allGroups[id]).ToList();
        var activeGroups = picked.Where(g => g.Active).ToList();
        var dto = new UserGroupPreviewDto { InactiveGroupCount = picked.Count - activeGroups.Count };
        if (!active) return dto;

        var existing = userId is > 0 ? _users.Get(userId.Value) : null;
        // 以「這位使用者＋勾選的群組」組一個暫時的帳號問能力：不碰儲存的資料
        var probe = new WebUser
        {
            UserId = existing?.UserId ?? 0,
            Active = true,
            GroupIds = picked.Select(g => g.GroupId).ToList()
        };
        var capabilities = _capabilities.Resolve(probe);
        dto.Capabilities = CapabilityTexts.Where(c => capabilities.Contains(c.Capability)).Select(c => c.Text).ToList();
        dto.CanHandle = capabilities.Contains(Capability.Handle);
        dto.InPool = activeGroups.Any(g => g.DispatchPool);

        var snapshot = _hosts.CapturePrtgSnapshot();
        var visible = HostVisibilityResolver.GroupVisibleHostIds(snapshot, activeGroups, _access).ToHashSet();
        if (existing is { Active: true })
            visible.UnionWith(HostVisibilityResolver.GetOwnedHostIds(snapshot, _users, existing.UserId));
        dto.VisibleHostCount = visible.Count;
        return dto;
    }
}
